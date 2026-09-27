namespace EQBuddy.Core;

/// <summary>
/// Builds the combined "you + your teammates" snapshot from the primary's own
/// <see cref="SessionStats"/> and every teammate <see cref="DerivedTeammates"/> derived
/// from the same log, by folding <see cref="DuoStats.Combine"/> once per teammate. The
/// field-by-field rules (what sums, what stays yours) live in <see cref="DuoStats"/>;
/// this class supplies the three things a fold of snapshots alone cannot know.
///
/// <b>1. The primary's party-kill rows, corrected exactly.</b> A teammate's kill and a
/// teammate's death are both party kills in the primary's own rows. Each teammate's
/// capture carries the exact target and killer rows those lines bumped
/// (<see cref="Mate.PartyKillsByTarget"/>/<see cref="Mate.PartyKillsByKiller"/>, recorded
/// off the primary's own parsed events), and only those counts are subtracted — a kill
/// the teammate made before they were on the roster stays a party kill, a death moves
/// out of both the target AND the killer rows, and the killer rows keep summing to
/// <see cref="StatsSnapshot.PartyKillCount"/>.
///
/// <b>2. Time, as a union.</b> Every side's combat spans are unioned (two sequential
/// fights add, two overlapping ones do not double), and so are the spans inside the
/// shared recent window, so combat seconds, session DPS, recent DPS/HPS and the live
/// "current" DPS are all recomputed ONCE from summed amounts over one union — never a
/// sum of rates with different denominators.
///
/// <b>3. One version.</b> The combined snapshot's <see cref="StatsSnapshot.Version"/> is
/// the primary's own plus <see cref="DerivedTeammates.Version"/> — exactly what
/// <see cref="SessionStats.DuoVersion"/> reports — so the phone pump's gate and the
/// snapshot it pushes always agree.
///
/// The primary is captured with the caller's recent window AND tracked rules (so
/// <c>Recent</c> and <c>Tracked</c> survive the combine); teammates with the window and
/// no rules, since no watch rule ever subscribes to a teammate's session.
/// </summary>
public static class TeammateCombine
{
    /// <summary>One side of the combine, captured under that side's own lock: the
    /// snapshot, its whole-session combat spans and untracked remainder, what it dealt
    /// inside the shared recent window and that window's spans, and its still-live
    /// fight, if any.</summary>
    internal sealed record Side(
        StatsSnapshot Snapshot,
        List<(DateTime Start, DateTime End)> Spans,
        double UntrackedSeconds,
        double WindowDamage,
        double WindowHealing,
        List<(DateTime Start, DateTime End)> WindowSpans,
        (DateTime Start, DateTime End, double Damage)? LiveFight);

    /// <summary>One teammate's capture plus the primary party-kill rows their own events
    /// account for.</summary>
    internal sealed record Mate(
        string Name,
        Side Side,
        IReadOnlyDictionary<string, int> PartyKillsByTarget,
        IReadOnlyDictionary<string, int> PartyKillsByKiller);

    /// <summary>The combined snapshot. No known teammate returns the primary's own
    /// snapshot BY REFERENCE — solo costs nothing and is byte-for-byte unchanged.</summary>
    public static StatsSnapshot Combine(SessionStats primary, DerivedTeammates teammates,
        TimeSpan? recentWindow, IReadOnlyList<TrackedRule>? rules)
    {
        var now = DateTime.Now;
        // One shared window end for every side, so "the last N minutes" means the same
        // minutes for everybody.
        var windowEnd = Later(primary.LastEventTimeSnapshot, teammates.LastEventTime);
        var mine = primary.CaptureForCombine(recentWindow, rules, windowEnd, now);
        var (mates, derivedVersion) = teammates.CaptureForCombine(recentWindow, windowEnd, now);
        return mates.Count == 0 ? mine.Snapshot : Fold(mine, mates, mine.Snapshot.Version + derivedVersion);
    }

    internal static StatsSnapshot Fold(Side mine, IReadOnlyList<Mate> mates, long version)
    {
        var result = mine.Snapshot;
        var spans = new List<(DateTime Start, DateTime End)>(mine.Spans);
        var untracked = mine.UntrackedSeconds;
        var windowSpans = new List<(DateTime Start, DateTime End)>(mine.WindowSpans);
        double windowDamage = mine.WindowDamage, windowHealing = mine.WindowHealing;
        var liveSpans = new List<(DateTime Start, DateTime End)>();
        double liveDamage = 0;
        AddLive(mine.LiveFight);

        foreach (var mate in mates)
        {
            // Running totals: each fold hands Combine the union SO FAR, so the last
            // fold's figures are the union across everybody.
            spans.AddRange(mate.Side.Spans);
            untracked += mate.Side.UntrackedSeconds;
            var combatSeconds = SessionStats.UnionCombatSeconds(spans, untracked, [], 0, 0);

            windowSpans.AddRange(mate.Side.WindowSpans);
            var windowUnion = SessionStats.UnionCombatSeconds(windowSpans, 0, [], 0, 0);
            // Snapshot's own "at least one second when there was damage" floor, applied
            // to the union so two single hits in one shared second cannot double it.
            if (windowUnion < 1 && windowDamage + mate.Side.WindowDamage > 0) windowUnion = 1;
            var recentTotals = new DuoStats.RecentWindowTotals(
                windowDamage, windowHealing, mate.Side.WindowDamage, mate.Side.WindowHealing, windowUnion);
            windowDamage += mate.Side.WindowDamage;
            windowHealing += mate.Side.WindowHealing;

            AddLive(mate.Side.LiveFight);
            var liveSeconds = SessionStats.UnionCombatSeconds(liveSpans, 0, [], 0, 0);
            var currentDps = liveDamage > 0 ? liveDamage / Math.Max(1, liveSeconds) : 0;

            result = DuoStats.Combine(result, mate.Side.Snapshot, mate.Name, combatSeconds,
                mate.PartyKillsByTarget, recentTotals, mate.PartyKillsByKiller, currentDps, version);
        }
        return result;

        void AddLive((DateTime Start, DateTime End, double Damage)? live)
        {
            if (live is not { } f) return;
            liveSpans.Add((f.Start, f.End));
            liveDamage += f.Damage;
        }
    }

    private static DateTime? Later(DateTime? a, DateTime? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;
}
