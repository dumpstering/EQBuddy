namespace EQBuddy.Core;

/// <summary>
/// Generalises <see cref="DuoStats.Combine"/> (built for exactly ONE teammate) to a
/// roster of any size, by FOLDING it once per teammate rather than rewriting it. This
/// file adds no new merge arithmetic of its own for the fields <c>DuoStats.Combine</c>
/// already gets right (sums, tags, the ability-row actor tagging) — it exists only for
/// the one correction that single-companion code never needed: a teammate's own DEATH
/// line ("Garg has been slain by a rock golem!") is a <see cref="KillEvent"/> whose
/// Killer is a mob, so upstream's own <c>SessionStats.Apply</c> — unchanged, unaware
/// any of this exists — files it as an ordinary party kill of target "Garg". With one
/// teammate that quirk was tolerated (see <c>DuoCompanion.cs</c>'s history); with a
/// roster it would multiply once per teammate, so it is corrected here explicitly
/// rather than left to accumulate silently.
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
/// <b>Sequencing:</b> each fold's result becomes the next fold's <c>mine</c>, so a
/// third teammate's subtraction runs against a residual that already has the first
/// two teammates' kills AND deaths removed — folding composes correctly because
/// <c>Combine</c> always recomputes <c>PartyKillCount</c> from whatever
/// <c>PartyKillsByTarget</c> it was just handed, never from a value carried alongside
/// it.
///
/// <b>Known simplification, stated rather than hidden:</b> <c>combatSecondsOverride</c>
/// is left null on every fold, so combat-span UNION (exact, via
/// <c>DuoCompanion.UnionCombatSeconds</c>) is not available for more than one teammate
/// here — each fold falls back to <c>Math.Max</c> against the RUNNING total, which can
/// undercount when three or more actors' fights genuinely do not overlap. The
/// single-companion path (<see cref="SessionStats.DuoSnapshot"/>) still computes the
/// exact union for its own one companion; extending that to an arbitrary roster is a
/// separate, larger piece of work, deliberately out of scope for this pass.
///
/// <b>Not corrected here (same gap as the single-companion killer breakdown):</b> the
/// mob that killed a teammate keeps one extra "kill" credited to it in
/// <see cref="StatsSnapshot.PartyKillsByKiller"/> — <c>NameCount</c> rows have no
/// per-event pairing to know how much of that mob's count came from a real party kill
/// versus this one misfiled death, so it is left alone rather than guessed at. This
/// exists in the file-based feature today too (it only ever removed KILLER rows named
/// exactly as the teammate/their pet, never a mob's), so this pass is no worse, not a
/// new regression.
/// </summary>
public static class TeammateCombine
{
    /// <summary>Empty <paramref name="teammates"/> returns <paramref name="primary"/> BY
    /// REFERENCE (the no-roster path costs nothing and is byte-for-byte the solo
    /// snapshot — the golden "combined == primary" case a roster of zero must hold
    /// exactly).</summary>
    public static StatsSnapshot Combine(StatsSnapshot primary, IReadOnlyDictionary<string, StatsSnapshot> teammates)
    {
        var result = primary;
        foreach (var (name, mate) in teammates)
        {
            var subtract = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var nc in mate.YourKills) subtract[nc.Name] = subtract.GetValueOrDefault(nc.Name) + nc.Count;
            // The death-row sentinel: whatever count mine.PartyKillsByTarget currently
            // holds for a row named exactly this teammate is entirely a misfiled death
            // (nobody else can share that exact name), so a value far larger than any
            // real session could reach zeroes the row out — DuoStats.Combine's own
            // ".Where(nc => nc.Count > 0)" then drops it.
            subtract[name] = subtract.GetValueOrDefault(name) + 1_000_000;
            result = DuoStats.Combine(result, mate, name, combatSecondsOverride: null, subtract);
        }
        return result;
    }
}
