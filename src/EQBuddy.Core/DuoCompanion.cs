namespace EQBuddy.Core;

/// <summary>
/// The duo half of <see cref="SessionStats"/>: the teammates derived from this (the
/// watched character's) own log, and the combined, display-only snapshot a widget or
/// phone shows while grouped. It lives in its own file, outside the
/// <c>SessionStats*.cs</c> ratchet glob, and costs SessionStats.cs no line at all — the
/// class is <c>sealed partial</c>, so this file reaches the private fields it reads
/// directly.
///
/// The archiver, the 5-minute checkpoint and the wiki pack never come through here:
/// they call the plain <see cref="Snapshot()"/>, so history stays the watched
/// character's own.
/// </summary>
public sealed partial class SessionStats
{
    private DerivedTeammates? _teammates;

    /// <summary>The teammates derived from this session's own log — fed by
    /// <see cref="LogWatcher"/> line by line, reset with this session's own rollover.
    /// Created on first use, so a teammate's own isolated instance (which nothing ever
    /// asks) never grows one.</summary>
    public DerivedTeammates Teammates
    {
        get
        {
            if (Volatile.Read(ref _teammates) is { } existing) return existing;
            lock (_lock) return _teammates ??= new DerivedTeammates(this);
        }
    }

    /// <summary>The version the desktop AND the phone read for the combined snapshot —
    /// the primary's own plus the teammates'. The phone pump's gate and the snapshot it
    /// pushes must read the SAME number, or a teammate's activity moves one and never
    /// the other and the pump pushes forever; <see cref="DuoSnapshot"/>'s
    /// <c>Version</c> is built from exactly these two parts. Equal to
    /// <see cref="CurrentVersion"/> while no teammate is known.</summary>
    public long DuoVersion => CurrentVersion + (Volatile.Read(ref _teammates)?.Version ?? 0);

    /// <summary>The snapshot every display surface reads — <c>MainWindow.BuildSnapshot</c>'s
    /// one call site, for the widget and the phone alike. The watched character's own
    /// snapshot (same reference) while no teammate is known; otherwise
    /// <see cref="TeammateCombine.Combine"/> over the SAME window and rules.</summary>
    public StatsSnapshot DuoSnapshot(TimeSpan? recentWindow, IReadOnlyList<TrackedRule>? rules) =>
        Volatile.Read(ref _teammates) is { } teammates
            ? TeammateCombine.Combine(this, teammates, recentWindow, rules)
            : Snapshot(recentWindow, rules);

    /// <summary>One person's OWN dps numbers for the per-person duo readout — never a
    /// combined rate. <see cref="EQBuddy.UI.Shared.PerPersonDpsPresentation"/> turns a list
    /// of these into the rows the widget draws.</summary>
    public readonly record struct PersonDps(string Name, double SessionDps, double CurrentDps, long DamageDealt);

    /// <summary>This instance's own row (its plain <see cref="Snapshot(TimeSpan?, IReadOnlyList{TrackedRule}?)"/>
    /// — the SOLO numbers, not <see cref="DuoSnapshot"/>'s combined one) first, then one
    /// row per CURRENT teammate off THEIR OWN isolated instance, name-sorted. Same window
    /// and rules as the caller's own display snapshot, so a row never disagrees with what
    /// fed it. Solo (no teammates known, or every known teammate has since left) answers a
    /// single-element list.
    ///
    /// <b>"Current" is <see cref="DerivedTeammates.CountedNow"/>'s roster, not
    /// <see cref="DerivedTeammates.SnapshotsFor"/>'s full set</b> — the latter is keyed on
    /// <c>KnownTeammates</c> (every name this session has ever accrued anything for, on
    /// purpose: it is what lets a departed teammate's contribution keep counting toward the
    /// COMBINED <see cref="DuoSnapshot"/> for the rest of the session). This readout is a
    /// different question — "who is presented as a partner playing with me right now" — so
    /// a name off the current roster (left, disbanded, logged out) is filtered out here even
    /// though its accrued numbers are still sitting in <c>KnownTeammates</c> for the combine
    /// to keep reading.</summary>
    public IReadOnlyList<PersonDps> PerPersonDps(TimeSpan? recentWindow, IReadOnlyList<TrackedRule>? rules)
    {
        var mine = Snapshot(recentWindow, rules);
        var rows = new List<PersonDps>
        {
            new(CharacterName is { Length: > 0 } n ? n : "You", mine.SessionDps, mine.CurrentDps, mine.DamageDealt),
        };
        if (Volatile.Read(ref _teammates) is { } teammates)
        {
            // Wrapped explicitly rather than relying on CountedNow()'s own comparer: its
            // declared return type is IReadOnlyCollection<string>, whose only Contains is
            // the LINQ extension (ordinal, case-SENSITIVE) unless the concrete instance is
            // asked for as a set — a silent case-sensitivity mismatch here would bring the
            // exact bug back for a name whose casing drifted between roster and stats.
            var current = new HashSet<string>(teammates.CountedNow(), StringComparer.OrdinalIgnoreCase);
            foreach (var (name, snap) in teammates.SnapshotsFor(recentWindow, rules))
                if (current.Contains(name))
                    rows.Add(new PersonDps(name, snap.SessionDps, snap.CurrentDps, snap.DamageDealt));
        }
        return rows;
    }

    /// <summary>Whether <paramref name="name"/> is this instance's own pet right now —
    /// the same test <c>Apply</c> uses to decide a kill is yours rather than a party
    /// kill.</summary>
    internal bool IsMyPet(string name) { lock (_lock) return _charm.IsPet(name); }

    /// <summary>This instance's CURRENT pet name, read live (a charm or a resummon can
    /// change it mid-session) — the derived-teammate roster refuses a name equal to it.</summary>
    internal string? LivePetName { get { lock (_lock) return _charm.PetName; } }

    /// <summary>When this session last saw an event.</summary>
    internal DateTime? LastEventTimeSnapshot { get { lock (_lock) return _lastEventTime; } }

    /// <summary>Union timestamped spans. Untracked seconds are only the remainder of
    /// upstream's bounded span history; their overlap cannot be recovered, so they are
    /// conservatively added.</summary>
    internal static double UnionCombatSeconds(
        List<(DateTime Start, DateTime End)> mineSpans, double mineUntracked,
        List<(DateTime Start, DateTime End)> mateSpans, double mateUntracked,
        double carryCombatSeconds)
    {
        double union = 0;
        DateTime curStart = default, curEnd = default;
        var open = false;
        foreach (var (start, end) in mineSpans.Concat(mateSpans).OrderBy(s => s.Start))
        {
            if (!open) { curStart = start; curEnd = end; open = true; continue; }
            if (start <= curEnd) { if (end > curEnd) curEnd = end; }
            else { union += (curEnd - curStart).TotalSeconds; curStart = start; curEnd = end; }
        }
        if (open) union += (curEnd - curStart).TotalSeconds;

        return union + mineUntracked + mateUntracked + carryCombatSeconds;
    }

    /// <summary>Everything <see cref="TeammateCombine"/> needs from one side, under ONE
    /// hold of this instance's lock, so a hit landing mid-capture cannot pair a span
    /// with damage the snapshot does not hold: the snapshot itself, the combat spans,
    /// what was dealt in the recent window ending at <paramref name="windowEnd"/> (the
    /// later of every side's last event) and that window's spans, and the live fight
    /// if it is still live at <paramref name="now"/> by the same rule
    /// <see cref="StatsSnapshot.CurrentDps"/> uses.</summary>
    internal TeammateCombine.Side CaptureForCombine(TimeSpan? recentWindow, IReadOnlyList<TrackedRule>? rules,
        DateTime? windowEnd, DateTime now)
    {
        lock (_lock)
        {
            var snap = Snapshot(recentWindow, rules);
            var (spans, untracked) = SnapshotCombatSpans();

            double windowDamage = 0, windowHealing = 0;
            var windowSpans = new List<(DateTime Start, DateTime End)>();
            if (recentWindow is { } w && (windowEnd ?? _lastEventTime) is { } winEnd)
            {
                var winStart = winEnd - w;
                for (var i = _journal.Count - 1; i >= 0; i--)
                {
                    var evt = _journal[i];
                    if (evt.Time < winStart) break;
                    if (evt.Time > winEnd) continue;
                    switch (evt)
                    {
                        case DamageDealtEvent dd: windowDamage += dd.Amount; break;
                        case HealEvent { Outgoing: true } h: windowHealing += h.Amount; break;
                    }
                }
                // Raw spans clipped to the window, exactly as Snapshot's own recent rate
                // walks them; the one-second floor is applied to the UNION by the caller.
                foreach (var (s, e) in _combatSpans) Clip(s, e);
                if (_combatStart is { } ocs && _combatLast is { } ocl) Clip(ocs, ocl);

                void Clip(DateTime s, DateTime e)
                {
                    var os = s > winStart ? s : winStart;
                    var oe = e < winEnd ? e : winEnd;
                    if (oe > os) windowSpans.Add((os, oe));
                }
            }

            (DateTime, DateTime, double)? live = null;
            if (_combatStart is { } cs && _combatLast is { } cl && now - cl <= CombatGap + TimeSpan.FromSeconds(2))
                live = (cs, cl, _combatDamage);

            return new TeammateCombine.Side(snap, spans, untracked, windowDamage, windowHealing, windowSpans, live);
        }
    }

    /// <summary>Every span this instance can still account for exactly — the closed
    /// spans, each stretched to the same 1-second floor a single-hit span gets, plus the
    /// still-open span — alongside the untracked remainder the 2048-entry trim can no
    /// longer name individually. The caller holds <c>_lock</c>.</summary>
    private (List<(DateTime Start, DateTime End)> Spans, double UntrackedSeconds) SnapshotCombatSpans()
    {
        static (DateTime, DateTime) Floor((DateTime Start, DateTime End) s) =>
            (s.Start, s.Start.AddSeconds(Math.Max(1, (s.End - s.Start).TotalSeconds)));

        var spans = _combatSpans.Select(Floor).ToList();
        if (_combatStart is { } cs && _combatLast is { } cl) spans.Add(Floor((cs, cl)));
        var trackedClosedSeconds = _combatSpans.Sum(s => Math.Max(1, (s.End - s.Start).TotalSeconds));
        var untracked = Math.Max(0, _closedCombatSeconds - trackedClosedSeconds);
        return (spans, untracked);
    }
}
