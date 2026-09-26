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
/// <b>Not corrected here (same gap as the single-companion killer breakdown):</b> the
/// mob that killed a teammate keeps one extra "kill" credited to it in
/// <see cref="StatsSnapshot.PartyKillsByKiller"/> — <c>NameCount</c> rows have no
/// per-event pairing to know how much of that mob's count came from a real party kill
/// versus this one misfiled death, so it is left alone rather than guessed at. This
/// exists in the file-based feature today too, so this pass is no worse, not a new
/// regression.
/// </summary>
public static class TeammateCombine
{
    /// <summary>Empty <paramref name="teammates"/> returns <paramref name="primary"/> BY
    /// REFERENCE (the no-roster path costs nothing and is byte-for-byte the solo
    /// snapshot — the golden "combined == primary" case a roster of zero must hold
    /// exactly). Snapshot-only: see the class doc for why this overload cannot compute
    /// an exact combat-seconds union and falls back to <c>Math.Max</c> per fold — use
    /// the <see cref="SessionStats"/> overload below when the live instances are at
    /// hand.</summary>
    public static StatsSnapshot Combine(StatsSnapshot primary, IReadOnlyDictionary<string, StatsSnapshot> teammates)
    {
        var result = primary;
        foreach (var (name, mate) in teammates)
        {
            result = DuoStats.Combine(result, mate, name, combatSecondsOverride: null, BuildSubtract(mate, name));
            StripWarderKillerRows(result, name);
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
    /// <see cref="SessionStats.Snapshot()"/> be read here.</summary>
    public static StatsSnapshot Combine(SessionStats primary, IReadOnlyDictionary<string, SessionStats> teammates)
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
}
