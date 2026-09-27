namespace EQBuddy.Core;

/// <summary>
/// The duo half of <see cref="SessionStats"/>: the combined, display-only snapshot a
/// widget or phone shows while grouped. It lives in its own file, outside the
/// <c>SessionStats*.cs</c> ratchet glob, and costs SessionStats.cs no line at all —
/// the class is <c>sealed partial</c>, so this file reaches the private fields it reads
/// directly.
/// </summary>
public sealed partial class SessionStats
{
    /// <summary>The CURRENT session's start — <see cref="DerivedTeammates"/> reads it on a
    /// teammate's own isolated instance.</summary>
    internal DateTime? SessionStartSnapshot { get { lock (_lock) return _sessionStart; } }

    /// <summary>This instance's CURRENT pet name, read live (a charm or a resummon can
    /// change it mid-session) — the derived-teammate roster refuses a name equal to it.</summary>
    internal string? LivePetName { get { lock (_lock) return _charm.PetName; } }

    /// <summary>The version the desktop AND the phone read for the combined snapshot.
    /// Equal to <see cref="CurrentVersion"/> while nothing is combined.</summary>
    public long DuoVersion => CurrentVersion;

    /// <summary>The snapshot every display surface reads — <c>MainWindow.BuildSnapshot</c>'s
    /// one call site. Solo while nothing is combined.</summary>
    public StatsSnapshot DuoSnapshot(TimeSpan? recentWindow, IReadOnlyList<TrackedRule>? rules) =>
        Snapshot(recentWindow, rules);

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

    /// <summary>The raw damage/healing this instance dealt in the last
    /// <paramref name="window"/> before its own last event, and that window's combat
    /// spans clipped to it, so a caller can union them across a roster and recompute
    /// the rate once rather than summing already-divided rates.</summary>
    internal (double Damage, double Healing, List<(DateTime Start, DateTime End)> Spans) SnapshotRecentWindowForCombine(TimeSpan window)
    {
        lock (_lock)
        {
            if (_lastEventTime is not { } winEnd) return (0, 0, []);
            var winStart = winEnd - window;
            double dmg = 0, healed = 0;
            for (var i = _journal.Count - 1; i >= 0; i--)
            {
                var evt = _journal[i];
                if (evt.Time < winStart) break;
                switch (evt)
                {
                    case DamageDealtEvent dd: dmg += dd.Amount; break;
                    case HealEvent { Outgoing: true } h: healed += h.Amount; break;
                }
            }
            var spans = new List<(DateTime Start, DateTime End)>();
            foreach (var (s, e) in _combatSpans)
            {
                var os = s > winStart ? s : winStart;
                var oe = e < winEnd ? e : winEnd;
                if (oe > os) spans.Add((os, oe));
            }
            if (_combatStart is { } cs && _combatLast is { } cl)
            {
                var os = cs > winStart ? cs : winStart;
                var oe = cl < winEnd ? cl : winEnd;
                if (oe > os) spans.Add((os, oe));
            }
            return (dmg, healed, spans);
        }
    }

    /// <summary>A snapshot and its own combat-span accounting, taken under ONE hold of
    /// the lock, so a concurrent hit cannot put a span newer than the snapshot's damage
    /// into a caller's union denominator.</summary>
    public (StatsSnapshot Snapshot, List<(DateTime Start, DateTime End)> Spans, double Untracked)
        SnapshotWithSpansForCombine(TimeSpan? recentWindow, IReadOnlyList<TrackedRule>? rules)
    {
        lock (_lock)
        {
            var snap = Snapshot(recentWindow, rules);
            var (spans, untracked) = SnapshotCombatSpans();
            return (snap, spans, untracked);
        }
    }

    /// <summary>Every span this instance can still account for exactly — the closed
    /// spans, each stretched to the same 1-second floor a single-hit span gets, plus the
    /// still-open span — alongside the untracked remainder the 2048-entry trim can no
    /// longer name individually.</summary>
    private (List<(DateTime Start, DateTime End)> Spans, double UntrackedSeconds) SnapshotCombatSpans()
    {
        static (DateTime, DateTime) Floor((DateTime Start, DateTime End) s) =>
            (s.Start, s.Start.AddSeconds(Math.Max(1, (s.End - s.Start).TotalSeconds)));

        lock (_lock)
        {
            var spans = _combatSpans.Select(Floor).ToList();
            if (_combatStart is { } cs && _combatLast is { } cl) spans.Add(Floor((cs, cl)));
            var trackedClosedSeconds = _combatSpans.Sum(s => Math.Max(1, (s.End - s.Start).TotalSeconds));
            var untracked = Math.Max(0, _closedCombatSeconds - trackedClosedSeconds);
            return (spans, untracked);
        }
    }
}
