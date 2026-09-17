namespace EQBuddy.Core;

/// <summary>
/// Display-only combination of two isolated sessions. The watcher serializes its
/// mutations with DuoSync; snapshot readers acquire it before either stats lock.
/// SessionStats.cs spends one of its three added lines capturing a companion's
/// combat spans before an inactivity reset. No durable resource is attached.
/// </summary>
public sealed partial class SessionStats
{
    // Watcher mutations take its own lock first, then this display transaction lock.
    // Snapshot readers take only this lock, then each stats lock separately.
    internal object DuoSync { get; } = new();
    /// <summary>The teammate's own isolated <see cref="SessionStats"/> instance, or
    /// null solo — set/cleared by <see cref="LogWatcher"/> alongside its own teammate
    /// lifecycle (SelectTeammate/Select). Never anything but a plain, unattached
    /// instance: see <see cref="TeammateLogTail"/>'s class doc for the invariant this
    /// rests on.</summary>
    public SessionStats? Companion
    {
        get { lock (_lock) return _companion; }
        // Repair round A8: watcher-owned rather than public — LogWatcher is the only
        // intended caller (see the class doc above), and `internal` says so in the
        // type system instead of only in a comment. Tests reach it via
        // InternalsVisibleTo, same as everywhere else in this assembly.
        internal set
        {
            lock (DuoSync)
            {
            // Lock-ordering invariant (plan Part 2e): DuoSnapshot/DuoVersion read the
            // companion's own lock-protected members while this instance's own lock
            // (if any) is not held across that call — never the reverse. A companion
            // that itself had a companion would let that ordering invert two levels
            // deep, so chaining is refused outright rather than trusted to never happen.
            // Read OUTSIDE this instance's own lock (repair round A8): value.Companion
            // takes value's lock, and holding ours across that call would invert the
            // same ordering this comment already protects one level up.
            if (value?.Companion is not null)
                throw new InvalidOperationException(
                    "A teammate's own Companion must stay null — chaining is not supported.");
            // Repair round A8, the OTHER direction: the check above cannot see that
            // THIS instance is ALREADY serving as SOME OTHER instance's companion
            // right now (mine.Companion = mate succeeds, then mate.Companion = third
            // used to succeed too — mate ends up simultaneously mine's companion and
            // third's primary, the same two-deep chain from the other end). Checking
            // `IsSomeonesCompanion` (implicit `this`) is exactly that back-reference.
            if (value is not null && IsSomeonesCompanion)
                throw new InvalidOperationException(
                    "This instance is already serving as another primary's Companion — "
                    + "it cannot also be given one of its own.");
            SessionStats? old;
            lock (_lock)
            {
                old = _companion;
                // Repair round A2 (part ii): a companion swap must not go on carrying
                // a PREVIOUS teammate's rolled-over segments into a stranger's totals,
                // and the old companion's future rollovers must stop reaching this
                // instance at all once it is no longer the companion.
                if (old is not null) old.SessionEnding -= OnCompanionSessionEnding;
                _companion = value;
                _companionCarry = null;
                _companionCarrySpans.Clear();
                _companionCarryUntracked = 0;
                _duoMemo = null;
                _promotedPartyKillsByTarget.Clear();
                // Repair round C8: a new companion may be a different log entirely —
                // possibly a different machine with a different clock — so any offset
                // estimate built against the OLD companion must not go on describing
                // the new one. Deliberately NOT cleared on a primary session rollover
                // (see ClearPromotedPartyKillsByTarget's call sites): the offset
                // characterises the PAIRING of two machines' clocks, not the watched
                // character's session, so a rollover throwing it away would only mean
                // re-earning an estimate that was still true.
                _clockDrift.Reset();
                if (value is not null) value.SessionEnding += OnCompanionSessionEnding;
            }
            old?.MarkAsSomeonesCompanion(false);
            value?.MarkAsSomeonesCompanion(true);
            }
        }
    }
    private SessionStats? _companion;

    /// <summary>Repair round A8: whether THIS instance is currently assigned as some
    /// OTHER instance's <see cref="Companion"/> — the back-reference the one-hop
    /// "does my new companion already have one" check on the setter cannot see for
    /// itself. Guarded by this instance's OWN lock only, taken and released by the
    /// OWNER's setter rather than while the owner still holds its own lock.</summary>
    internal bool IsSomeonesCompanion { get { lock (_lock) return _isSomeonesCompanion; } }
    private bool _isSomeonesCompanion;
    private void MarkAsSomeonesCompanion(bool value) { lock (_lock) _isSomeonesCompanion = value; }

    /// <summary>Repair round A2 (part i): the CURRENT session's start, for
    /// <see cref="TeammateLogTail.PrimarySessionStart"/> — free (no sync-budget cost)
    /// because this file reaches the private <c>_sessionStart</c> field for the same
    /// reason it reaches <c>_lock</c>: the class is <c>sealed partial</c>. LogWatcher
    /// is the only intended reader, wiring it into the teammate tail it owns; it is
    /// `internal` rather than `private` only because it crosses the file boundary
    /// within this assembly.</summary>
    internal DateTime? SessionStartSnapshot { get { lock (_lock) return _sessionStart; } }

    /// <summary>Repair round C3: whether <paramref name="name"/> is recognized as
    /// THIS instance's own pet right now — free access to the private
    /// <c>_charm</c> field for the same reason as <c>_lock</c>. LogWatcher uses
    /// this on the PRIMARY instance to classify a third-party <see cref="KillEvent"/>
    /// the same way <c>Apply</c>'s own promotion rule does
    /// (<c>k.Killer == "You" || IsPet(k.Killer)</c>), without needing that private
    /// method exposed on SessionStats.cs itself.</summary>
    internal bool IsMyPet(string name) { lock (_lock) return _charm.IsPet(name); }

    /// <summary>Repair round C3: this instance's CURRENT pet name, read live rather
    /// than via a full <see cref="Snapshot()"/> — LogWatcher calls this once per
    /// third-party kill line to classify it, and a charm can change mid-session, so
    /// reading it fresh at each kill (rather than a single frozen snapshot value)
    /// is what lets a promoted-kill join correctly follow a pet swap instead of
    /// only ever recognizing whichever pet the teammate had at some one moment.</summary>
    internal string? LivePetName { get { lock (_lock) return _charm.PetName; } }

    /// <summary>
    /// Repair round C3: the TRUE per-target count of the teammate's own promoted
    /// kills, AS SEEN IN THIS (the primary's) OWN LOG — built by LogWatcher as it
    /// dispatches each of the primary's own third-party <see cref="KillEvent"/>s
    /// (<see cref="RecordTeammatePartyKill"/>), classifying the killer against the
    /// companion's identity at the time using <see cref="IsMyPet"/>'s sibling
    /// classification and <see cref="LivePetName"/>. This is the piece
    /// <see cref="DuoStats.Combine"/>'s old subtraction was missing: it used to
    /// subtract the teammate's SELF-REPORTED per-target kills
    /// (<c>StatsSnapshot.YourKills</c>) from the PRIMARY's own party-kill breakdown,
    /// which is wrong whenever the teammate's kill was not actually visible in the
    /// primary's own log — subtracting a self-reported count from a row that never
    /// counted it in the first place can delete a THIRD groupmate's real, visible
    /// kill of the same target name. This dictionary counts only what the primary's
    /// OWN log actually attributed to the teammate, so subtracting it can never
    /// remove more than the primary's own row already contains, and never removes
    /// a kill that belongs to anyone else.
    ///
    /// Cleared on every primary session boundary — <see cref="LogWatcher.OnPrimarySessionRolledOver"/>
    /// (an autonomous internal roll, live or during replay) and <see cref="LogWatcher.Select(string, long, long)"/>'s
    /// own explicit reset (a character switch or re-Select) — via
    /// <see cref="ClearPromotedPartyKillsByTarget"/>, exactly mirroring the
    /// lifecycle of the upstream <c>_partyKillsByTarget</c> dictionary it exists to
    /// correct against.
    /// </summary>
    private readonly Dictionary<string, int> _promotedPartyKillsByTarget = new(StringComparer.OrdinalIgnoreCase);

    internal void RecordTeammatePartyKill(string target)
    {
        lock (_lock) _promotedPartyKillsByTarget[target] = _promotedPartyKillsByTarget.GetValueOrDefault(target) + 1;
    }

    internal void ClearPromotedPartyKillsByTarget() { lock (_lock) _promotedPartyKillsByTarget.Clear(); }

    internal Dictionary<string, int> SnapshotPromotedPartyKillsByTarget()
    {
        lock (_lock) return new Dictionary<string, int>(_promotedPartyKillsByTarget, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Repair round C8: estimates how far the teammate's log's clock reads
    /// from THIS (the primary's) own — see <see cref="ClockDriftEstimator"/>'s own
    /// doc for why and how. Lives on the PRIMARY's instance, exactly like
    /// <see cref="_promotedPartyKillsByTarget"/> above, populated by LogWatcher as it
    /// observes a bystander-visible kill matching the teammate's own self-reported
    /// one (<see cref="TeammateLogTail.LastOwnKillTimestamp"/>).</summary>
    private readonly ClockDriftEstimator _clockDrift = new();

    internal void RecordClockDriftSample(DateTime primaryTimestamp, DateTime teammateTimestamp) =>
        _clockDrift.RecordSample(primaryTimestamp, teammateTimestamp);

    internal void ObservePrimaryTeammateKill(string target, DateTime time) => _clockDrift.ObservePrimaryKill(target, time);
    internal void ObserveTeammateOwnKill(string target, DateTime time) => _clockDrift.ObserveTeammateKill(target, time);
    internal void ClearPendingClockKills() => _clockDrift.ClearPendingKills();

    /// <summary>Repair round C8: the current best estimate of how far the teammate's
    /// clock reads from this (the primary's) clock — positive means the primary's
    /// clock reads AHEAD of the teammate's. Null when no bystander-visible kill has
    /// yet let it be estimated; report this honestly rather than assuming
    /// synchronised clocks nothing has confirmed. See
    /// <see cref="ClockDriftEstimator"/>'s own doc for the estimation method.</summary>
    public TimeSpan? EstimatedTeammateClockOffset => _clockDrift.Estimate;

    /// <summary>How many samples <see cref="EstimatedTeammateClockOffset"/> is built
    /// from — 0 exactly when that estimate is null.</summary>
    public int TeammateClockOffsetSampleCount => _clockDrift.SampleCount;

    /// <summary>True once enough is known to say the two logs' clocks disagree by
    /// more than <see cref="ClockDriftEstimator.Threshold"/>. <see cref="DuoSnapshot"/>
    /// refuses to combine while this is true, falling back to the solo snapshot
    /// exactly as it already does with no teammate selected at all — this is a
    /// judgement about whether the combine can be trusted, not a correction of
    /// either side's timestamps (deliberately out of scope; see
    /// <see cref="ClockDriftEstimator"/>'s own doc).</summary>
    public bool TeammateClockOffsetExceedsThreshold => _clockDrift.ExceedsThreshold;

    /// <summary>Repair round A2 (part ii): SessionStats.SessionGap's autonomous
    /// 60-minute roll is upstream and cannot be suppressed within this file's own
    /// sync budget, and it fires on the companion's OWN instance independently of
    /// the primary's — so a gap in only the teammate's log used to roll THEIR session
    /// while the primary's kept running, and their pre-roll contribution vanished
    /// from every duo total from that moment on. This accumulates what
    /// <see cref="OnCompanionSessionEnding"/> hands it — the FULL pre-roll snapshot,
    /// built before reset and delivered after Apply releases its lock — so <see cref="DuoSnapshot"/> can add
    /// it back in. Cleared when the companion changes (the setter above) or when the
    /// PRIMARY's own session rolls (<see cref="ClearCompanionCarry"/>, called from
    /// LogWatcher's existing Step 6b hook) — a carry only ever spans ONE primary
    /// session, matching "reset only at primary session boundaries".</summary>
    private StatsSnapshot? _companionCarry;
    private readonly List<(DateTime Start, DateTime End)> _companionCarrySpans = [];
    private double _companionCarryUntracked;
    private sealed record CombatCapture(List<(DateTime Start, DateTime End)> Spans, double UntrackedSeconds);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<StatsSnapshot, CombatCapture> EndingCombat = new();

    // Called while upstream still holds _lock, before ResetLocked destroys the spans.
    // The snapshot is the key, so concurrent rollovers cannot overwrite each other's
    // accounting, and snapshots that nobody retains release their capture automatically.
    private void CaptureDuoCombatBeforeReset(StatsSnapshot snapshot)
    {
        if (!_isSomeonesCompanion) return;
        var (spans, untracked) = SnapshotCombatSpans();
        EndingCombat.Add(snapshot, new CombatCapture(spans, untracked));
    }

    private void OnCompanionSessionEnding(StatsSnapshot ended)
    {
        // Repair round C1: DuoStats.CombineSameActorCarry, NOT the public two-actor
        // Combine — the carry and `ended` are the SAME actor across two time
        // segments, and folding them through Combine (which tags whatever it treats
        // as "mate" with an actor label) tagged an already-tagged historical
        // segment a second time on every subsequent rollover. See that method's own
        // doc for the full reasoning.
        lock (DuoSync)
        lock (_lock)
        {
            _companionCarry = DuoStats.CombineSameActorCarry(_companionCarry, ended);
            if (EndingCombat.TryGetValue(ended, out var combat))
            {
                _companionCarrySpans.AddRange(combat.Spans);
                _companionCarryUntracked += combat.UntrackedSeconds;
            }
            else _companionCarryUntracked += ended.CombatSeconds;
            _duoMemo = null;
        }
    }

    /// <summary>Drops whatever the companion's own gap rollovers have carried
    /// forward — called from <see cref="LogWatcher.OnPrimarySessionRolledOver"/>
    /// alongside its existing <c>_teammate?.Stats.Reset()</c>, so a carry never
    /// survives past the PRIMARY session it was accumulated during.</summary>
    public void ClearCompanionCarry()
    {
        lock (DuoSync)
        lock (_lock)
        {
            _companionCarry = null;
            _companionCarrySpans.Clear();
            _companionCarryUntracked = 0;
            _duoMemo = null;
        }
    }

    /// <summary>Union timestamped spans, including carried companion segments.
    /// Untracked seconds are only the remainder of upstream's bounded span history;
    /// their overlap cannot be recovered, so they are conservatively added.</summary>
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

    /// <summary>Every span this instance can still account for exactly — the closed
    /// spans still in <c>_combatSpans</c>, each stretched to the same 1-second floor
    /// <c>CloseCombatLocked</c>/<c>BuildSnapshotLocked</c> apply to a single-hit span
    /// (so a union built from these agrees with <see cref="StatsSnapshot.CombatSeconds"/>
    /// on a solo instance), plus the still-open span if combat is live right now —
    /// alongside the untracked remainder (see <see cref="UnionCombatSeconds"/>'s own
    /// doc) that <c>_combatSpans</c>' 2048-entry trim can no longer name individually.
    /// Locks its OWN <c>_lock</c> — reentrant-safe when the caller already holds it
    /// (repair round C7's atomic capture in <see cref="DuoSnapshot"/> does exactly
    /// that), and self-contained when it doesn't.</summary>
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

    /// <summary>Memoises the last <see cref="Combine"/> result by REFERENCE IDENTITY of
    /// both inputs — <see cref="Snapshot"/> already returns the SAME cached instance
    /// when nothing changed on that side, so this makes an unchanged duo pair free to
    /// re-request from desktop displays without re-walking either
    /// journal or re-merging any list.</summary>
    private (StatsSnapshot Mine, StatsSnapshot? Mate, StatsSnapshot Combined)? _duoMemo;

    /// <summary>Version of the desktop AND Mobile duo pair. Carry uses the live segment's
    /// version because upstream versions already continue across resets. Mobile's pump
    /// gate reads this too (2026-09-17, reversing the earlier primary-only call): the
    /// gate and the snapshot it pushes must agree on which version moved, or a teammate's
    /// own activity — invisible to <see cref="CurrentVersion"/> alone — moves the pushed
    /// snapshot's <c>Version</c> without ever satisfying the gate, and the 50 ms pump
    /// pushes to the phone forever. With no teammate this equals <see cref="CurrentVersion"/>
    /// exactly, so the no-teammate path is unchanged.</summary>
    public long DuoVersion => CurrentVersion + (Companion?.CurrentVersion ?? 0);

    /// <summary>The combined snapshot for display — <see cref="MainWindow.BuildSnapshot"/>'s
    /// one call site. <paramref name="rules"/> is applied ONLY to the watched
    /// character's own <see cref="Snapshot"/>: per Part 1c, the teammate's own instance
    /// must always be snapshotted with <c>rules: null</c> (a duo Text-watch match would
    /// otherwise be evaluated against a session nothing subscribes to). Returns
    /// <paramref name="mine"/>'s own snapshot BY REFERENCE when no teammate is
    /// selected, so the no-teammate path costs nothing beyond the existing memo.</summary>
    public StatsSnapshot DuoSnapshot(TimeSpan? recentWindow, IReadOnlyList<TrackedRule>? rules)
    {
        lock (DuoSync) return BuildDuoSnapshot(recentWindow, rules);
    }

    private StatsSnapshot BuildDuoSnapshot(TimeSpan? recentWindow, IReadOnlyList<TrackedRule>? rules)
    {
        // Solo/refused snapshots keep the original cheap path: no span copies.
        if (Companion is null || _clockDrift.ExceedsThreshold) return Snapshot(recentWindow, rules);
        // Repair round C7: mine's own snapshot and its combat-span accounting are
        // captured TOGETHER, under one hold of _lock — Monitor is reentrant, so
        // Snapshot's and SnapshotCombatSpans' own internal `lock (_lock)` blocks
        // nest safely on this same thread. Before this, the two were separate
        // statements with no lock spanning them, so a hit landing between them
        // (from another thread — the Mobile pump and the poll timer are not
        // guaranteed to be the same one) could put a span NEWER than what `mine`'s
        // already-captured DamageDealt accounts for into the union's denominator.
        StatsSnapshot mine;
        List<(DateTime Start, DateTime End)> mineSpans;
        double mineUntracked;
        lock (_lock)
        {
            mine = Snapshot(recentWindow, rules);
            (mineSpans, mineUntracked) = SnapshotCombatSpans();
        }

        // Repair round A8: captured once, under lock, rather than re-reading the
        // Companion property (itself now lock-protected) a second time further down —
        // a concurrent reassignment between the two reads used to be able to combine
        // mine's snapshot with a companion that was no longer this instance's.
        SessionStats? companion;
        lock (_lock) companion = _companion;
        if (companion is null) return mine;

        // Repair round C8: refuse to combine when the two logs' clocks disagree by
        // more than can be trusted — see TeammateClockOffsetExceedsThreshold's own
        // doc. Falls back to the solo snapshot exactly like "no companion" above;
        // the estimate itself stays readable through EstimatedTeammateClockOffset /
        // TeammateClockOffsetSampleCount regardless of whether combining proceeds.
        if (_clockDrift.ExceedsThreshold) return mine;

        // Repair round C7: the SAME atomic pairing for the companion's side, under
        // ITS OWN lock — reaching another instance's private `_lock` field is safe
        // from within this partial class's own body, same as `_combatSpans` et al.
        // Repair round A1: the SAME window, so the teammate's own recent kills/DPS/
        // HPS are actually there to combine — a null window here left mate.Recent
        // always null and CombineRecent silently returned mine's rates untouched.
        StatsSnapshot? mate;
        List<(DateTime Start, DateTime End)> mateSpans;
        double mateUntracked;
        lock (companion._lock)
        {
            mate = companion.Snapshot(recentWindow, null);
            (mateSpans, mateUntracked) = companion.SnapshotCombatSpans();
        }
        if (mate is null) return mine;

        // DuoSync spans both snapshots and the carry update on the production path.
        // ClearCompanionCarry and the rollover callback explicitly invalidate the memo.
        StatsSnapshot? carry;
        lock (_lock) carry = _companionCarry;
        // Repair round C1: same-actor fold here too — `mate` (the companion's
        // CURRENT live segment) and `carry` (everything it rolled over BEFORE now)
        // are the same teammate across time, not two different people. The public
        // Combine's actor-tagging must run exactly once, in the REAL primary+mate
        // combine below, on whatever this produces.
        var effectiveMate = DuoStats.CombineSameActorCarry(carry, mate, live: true);

        lock (_lock)
        {
            if (_duoMemo is { } memo && ReferenceEquals(memo.Mine, mine) && ReferenceEquals(memo.Mate, mate))
                return memo.Combined;
        }

        // Repair round A3: the teammate's OWN character name, so Combine can tell
        // their promoted kills apart from a genuine third groupmate's in the residual
        // party breakdown. Read from the SessionStats instance, not the snapshot —
        // StatsSnapshot carries no CharacterName field of its own.
        //
        // Retained companion spans participate in the same union as primary history.
        mateSpans.AddRange(_companionCarrySpans);
        var combatSecondsOverride = UnionCombatSeconds(mineSpans, mineUntracked, mateSpans,
            mateUntracked + _companionCarryUntracked, 0);
        // Repair round C3: the TRUE per-target count of the teammate's promoted
        // kills as seen in MY OWN log — see _promotedPartyKillsByTarget's own doc.
        var mateVisibleKillsByTarget = SnapshotPromotedPartyKillsByTarget();
        var combined = DuoStats.Combine(mine, effectiveMate, companion.CharacterName, combatSecondsOverride,
            mateVisibleKillsByTarget);
        lock (_lock) _duoMemo = (mine, mate, combined);
        return combined;
    }
}
