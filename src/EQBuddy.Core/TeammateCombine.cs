namespace EQBuddy.Core;

/// <summary>
/// Generalises <see cref="DuoStats.Combine"/> (built for exactly ONE teammate) to a
/// roster of any size, by FOLDING it once per teammate rather than rewriting it. This
/// file adds no new merge arithmetic of its own for the fields <c>DuoStats.Combine</c>
/// already gets right (sums, tags, the ability-row actor tagging) — it exists for two
/// corrections single-companion code never needed.
///
/// <b>1. A teammate's own DEATH line</b> ("Garg has been slain by a rock golem!") is a
/// <see cref="KillEvent"/> whose Killer is a mob, so upstream's own
/// <c>SessionStats.Apply</c> — unchanged, unaware any of this exists — files it as an
/// ordinary party kill of target "Garg". With one teammate that quirk was tolerated
/// (see <c>DuoCompanion.cs</c>'s history); with a roster it would multiply once per
/// teammate, so it is corrected here explicitly rather than left to accumulate
/// silently.
///
/// <b>Why folding is exact, not an approximation:</b> <see cref="DuoStats.Combine"/>'s
/// own kill-subtraction already accepts an exact per-target count of "the mate's kills
/// as seen in mine's own log" (<c>mateVisibleKillsByTarget</c>) instead of trusting the
/// mate's self-reported <see cref="StatsSnapshot.YourKills"/> — and for a teammate
/// DERIVED from the primary's own log (never a second file, never a second machine),
/// those two are the SAME thing by construction: every kill in a teammate's
/// <c>YourKills</c> came from a line <see cref="TeammatePerspective"/> rewrote out of
/// one specific primary-log line, so there is no daylight between "self-reported" and
/// "seen in mine's own log" left to worry about. This file borrows exactly that
/// parameter and adds one more entry to the same dictionary — a large sentinel keyed
/// on the teammate's OWN name — so the SAME subtraction mechanism that already zeroes
/// a mate's real kills out of the residual also zeroes out the misfiled death row,
/// with no second code path to keep in sync.
///
/// <b>2. The killer-row residual.</b> <c>DuoStats.Combine</c>'s own
/// <c>PartyKillsByKiller</c> strip only removes rows named exactly
/// <c>mateCharacterName</c> or <c>mate.PetName</c> — and <c>PetName</c> is always
/// empty for a derived teammate (it is never set on an isolated, file-less
/// <see cref="SessionStats"/>). So a kill promoted out of "&lt;name&gt;`s warder!" /
/// "&lt;name&gt;'s warder!" into the teammate's own <c>YourKills</c> left its killer
/// row exactly where it was, and the killer breakdown stopped summing to
/// <c>PartyKillCount</c> (which IS correct, being recomputed from the target
/// breakdown). <see cref="StripWarderKillerRows"/> is the one place that knows the
/// derived naming convention and removes it after every fold.
///
/// <b>Sequencing:</b> each fold's result becomes the next fold's <c>mine</c>, so a
/// third teammate's subtraction runs against a residual that already has the first
/// two teammates' kills AND deaths removed — folding composes correctly because
/// <c>Combine</c> always recomputes <c>PartyKillCount</c> from whatever
/// <c>PartyKillsByTarget</c> it was just handed, never from a value carried alongside
/// it.
///
/// <b>Combat-seconds union.</b> The snapshot-only overload below still cannot compute
/// an exact union (a <see cref="StatsSnapshot"/> carries no span history, only the
/// already-reduced <see cref="StatsSnapshot.CombatSeconds"/>), so it leaves
/// <c>combatSecondsOverride</c> null and inherits <c>DuoStats.Combine</c>'s own
/// <c>Math.Max</c> fallback. The <see cref="SessionStats"/>-based overload has the
/// LIVE instances and closes that gap for real: it builds one running N-way union
/// (primary's spans, then each teammate's in turn) via
/// <see cref="DuoCompanion.UnionCombatSeconds"/> — the same union the single-companion
/// <c>DuoSnapshot</c> path already computes for its one companion, generalised here to
/// however many teammates the roster holds, so even a roster of exactly one derived
/// teammate gets the real union rather than <c>Math.Max</c>.
///
/// <b>Corrected here (was: "not corrected", same gap as the single-companion killer
/// breakdown):</b> the mob that killed a teammate used to keep one extra "kill"
/// credited to it in <see cref="StatsSnapshot.PartyKillsByKiller"/> — <c>NameCount</c>
/// rows have no per-event pairing of their own to know how much of that mob's count
/// came from a real party kill versus a misfiled death. The pairing is available
/// anyway: a derived teammate's own death-by-killer counts (<see cref="DerivedTeammates.DeathKillersFor"/>)
/// are captured off the EXACT parsed event the death line produced, so subtracting
/// them from the killer breakdown can never remove more than that teammate's own
/// deaths actually put there, and never touches a row belonging to anyone else's real
/// party kill. See <see cref="SubtractDeathKillerRows"/>.
/// </summary>
public static class TeammateCombine
{
    /// <summary>Empty <paramref name="teammates"/> returns <paramref name="primary"/> BY
    /// REFERENCE (the no-roster path costs nothing and is byte-for-byte the solo
    /// snapshot — the golden "combined == primary" case a roster of zero must hold
    /// exactly). Snapshot-only: see the class doc for why this overload cannot compute
    /// an exact combat-seconds union and falls back to <c>Math.Max</c> per fold — use
    /// the <see cref="SessionStats"/> overload below when the live instances are at
    /// hand. <paramref name="deathKillers"/> is optional (defaults to none) — pass
    /// <see cref="DerivedTeammates.DeathKillersFor"/> per teammate to also correct the
    /// killer breakdown; omitting it leaves that residual exactly as before.</summary>
    public static StatsSnapshot Combine(StatsSnapshot primary, IReadOnlyDictionary<string, StatsSnapshot> teammates,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>>? deathKillers = null)
    {
        var result = primary;
        foreach (var (name, mate) in teammates)
        {
            result = DuoStats.Combine(result, mate, name, combatSecondsOverride: null, BuildSubtract(mate, name));
            StripWarderKillerRows(result, name);
            if (deathKillers is not null && deathKillers.TryGetValue(name, out var killers))
                SubtractDeathKillerRows(result, killers);
        }
        return result;
    }

    /// <summary>Live-instance overload: an exact N-way combat-seconds UNION across the
    /// primary and every teammate, generalising <see cref="DuoCompanion.UnionCombatSeconds"/>'s
    /// two-way case instead of falling back to <c>Math.Max</c> — see the class doc's
    /// "combat-seconds union" paragraph. <paramref name="teammates"/>' values are the
    /// derived teammates' own isolated instances (e.g. from a snapshot of
    /// <see cref="DerivedTeammates"/>'s internal store) — reading their combat spans is
    /// safe under the same isolation invariant that already lets their
    /// <see cref="SessionStats.Snapshot()"/> be read here. <paramref name="deathKillers"/>
    /// is optional — see the snapshot-only overload's own doc.</summary>
    public static StatsSnapshot Combine(SessionStats primary, IReadOnlyDictionary<string, SessionStats> teammates,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>>? deathKillers = null)
    {
        var primarySnapshot = primary.Snapshot();
        if (teammates.Count == 0) return primarySnapshot;

        var (spans, untracked) = primary.SnapshotCombatSpansForCombine();
        var result = primarySnapshot;
        foreach (var (name, mateStats) in teammates)
        {
            var mate = mateStats.Snapshot();
            var (mateSpans, mateUntracked) = mateStats.SnapshotCombatSpansForCombine();
            // Running union so far (primary + every teammate folded up to and
            // including this one) — recomputed on every fold, not only at the end,
            // matching the two-way path's own semantics.
            spans.AddRange(mateSpans);
            untracked += mateUntracked;
            var combatSecondsOverride = SessionStats.UnionCombatSeconds(spans, 0, [], 0, untracked);

            result = DuoStats.Combine(result, mate, name, combatSecondsOverride, BuildSubtract(mate, name));
            StripWarderKillerRows(result, name);
            if (deathKillers is not null && deathKillers.TryGetValue(name, out var killers))
                SubtractDeathKillerRows(result, killers);
        }
        return result;
    }

    private static Dictionary<string, int> BuildSubtract(StatsSnapshot mate, string name)
    {
        var subtract = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var nc in mate.YourKills) subtract[nc.Name] = subtract.GetValueOrDefault(nc.Name) + nc.Count;
        // The death-row sentinel: whatever count mine.PartyKillsByTarget currently
        // holds for a row named exactly this teammate is entirely a misfiled death
        // (nobody else can share that exact name), so a value far larger than any
        // real session could reach zeroes the row out — DuoStats.Combine's own
        // ".Where(nc => nc.Count > 0)" then drops it.
        subtract[name] = subtract.GetValueOrDefault(name) + 1_000_000;
        return subtract;
    }

    /// <summary>Mutates <paramref name="snapshot"/>'s own <c>PartyKillsByKiller</c> list
    /// in place — safe here because <c>DuoStats.Combine</c> always builds that list
    /// fresh (a non-empty <c>mateCharacterName</c>, which every caller in this file
    /// supplies, takes the "residualPartyKillsByKiller = [.. mine.PartyKillsByKiller
    /// .Where(...)]" branch, never the bare "= mine.PartyKillsByKiller" one that would
    /// alias the input snapshot's own list). <see cref="StatsSnapshot"/> is not a
    /// record, so there is no <c>with</c> expression to rebuild it from instead.</summary>
    private static void StripWarderKillerRows(StatsSnapshot snapshot, string teammateName)
    {
        var apos = $"{teammateName}'s warder";
        var tick = $"{teammateName}`s warder";
        snapshot.PartyKillsByKiller.RemoveAll(nc =>
            nc.Name.Equals(apos, StringComparison.OrdinalIgnoreCase) ||
            nc.Name.Equals(tick, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Removes exactly <paramref name="killers"/>' counts from
    /// <paramref name="snapshot"/>'s own <c>PartyKillsByKiller</c> — one misfiled row
    /// per teammate death the primary's own log parsed as "&lt;teammate&gt; has been
    /// slain by &lt;killer&gt;!" (a real <see cref="KillEvent"/> upstream's own
    /// <c>SessionStats.Apply</c> files as an ordinary party kill of the teammate's
    /// name, target-side corrected by <c>DuoStats.Combine</c>'s own sentinel, but whose
    /// KILLER row it never touches). A killer row's count is reduced by exactly the
    /// teammate's own death count for that killer, floored at zero, and the row is
    /// dropped once its count reaches zero — never removed outright, so a killer that
    /// ALSO earned genuine party kills against other targets keeps its residual
    /// count.</summary>
    private static void SubtractDeathKillerRows(StatsSnapshot snapshot, IReadOnlyDictionary<string, int> killers)
    {
        if (killers.Count == 0) return;
        for (var i = snapshot.PartyKillsByKiller.Count - 1; i >= 0; i--)
        {
            var row = snapshot.PartyKillsByKiller[i];
            if (!killers.TryGetValue(row.Name, out var deaths)) continue;
            var remaining = row.Count - deaths;
            if (remaining <= 0) snapshot.PartyKillsByKiller.RemoveAt(i);
            else snapshot.PartyKillsByKiller[i] = row with { Count = remaining };
        }
    }
}
