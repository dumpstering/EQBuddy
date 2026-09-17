namespace EQBuddy.Core;

/// <summary>
/// Step 2 of the teammate-isolation redesign (plan Part 4): the members here have NO
/// upstream counterpart at all, which is what earns them a spot outside MezTracker.cs
/// (the coordinator's clarified rule: relocation is only right for code upstream does
/// not have — everything upstream DOES have, including <c>RememberCast</c>'s new dedupe
/// and <c>OnWornOff</c>'s new caster guard, was edited in place in MezTracker.cs instead,
/// specifically to avoid conflicting with upstream's own ongoing edits to those methods
/// — 18 commits touched MezTracker.cs in the last 90 days, at least two of them inside
/// OnWornOff itself).
///
/// <b>Defect 2 (plan Part 4a):</b> <c>FizzleEvent</c>, <c>SpellInterruptedEvent</c> and
/// <c>SpellBlockedEvent</c> were admitted to the mez tracker (<see cref="TeammateFeed.AdmitForMez"/>)
/// but MezTracker.Apply had NO case for any of them, so a failed cast stayed in
/// <c>_recentCasts</c> for the full 8s <see cref="MezTracker.CastToLand"/> window and could
/// claim a landing a DIFFERENT caster actually produced. <see cref="CancelCast"/> removes
/// the newest matching pending cast and rolls <c>_lastCastOf</c> back to whatever it held
/// before — a fizzled cast landed nothing, so it must not count as a contaminating re-cast
/// in <c>OnWornOff</c>'s <c>recastAfterLanding</c> guard.
/// </summary>
public sealed partial class MezTracker
{
    /// <summary>Solo callers (every existing call site, every test) keep working
    /// unchanged — the watched character is always "You" from its own log's point of
    /// view. Only <see cref="TeammateLogTail"/> passes an explicit source.</summary>
    public void Apply(GameEvent evt) => Apply(evt, "You");

    /// <summary>Removes the newest <c>_recentCasts</c> entry matching (caster, base
    /// spell name) and rolls <c>_lastCastOf</c> back to whatever it held before — a
    /// cancelled cast landed nothing, so it must vanish from both ledgers as if it had
    /// never been cast. <paramref name="spell"/> empty (FizzleEvent's default, since
    /// the fizzle line names no spell) cancels that caster's newest pending cast of
    /// ANY spell.</summary>
    private void CancelCast(string caster, string spell, DateTime t)
    {
        var baseName = spell.Length > 0 ? SpellCatalog.BaseName(spell) : null;
        for (var i = _recentCasts.Count - 1; i >= 0; i--)
        {
            var entry = _recentCasts[i];
            if (!entry.Caster.Equals(caster, StringComparison.OrdinalIgnoreCase)) continue;
            if (baseName is not null &&
                !SpellCatalog.BaseName(entry.Spell).Equals(baseName, StringComparison.OrdinalIgnoreCase))
                continue;
            _recentCasts.RemoveAt(i);
            // The exact spell name may still be pending from an EARLIER cast (a queued
            // re-cast, or a different caster) — roll back to that, not to nothing,
            // unless there truly isn't one.
            var stillPending = false;
            for (var j = _recentCasts.Count - 1; j >= 0; j--)
            {
                if (!_recentCasts[j].Spell.Equals(entry.Spell, StringComparison.OrdinalIgnoreCase)) continue;
                _lastCastOf[entry.Spell] = _recentCasts[j].Time;
                stillPending = true;
                break;
            }
            if (!stillPending) _lastCastOf.Remove(entry.Spell);
            return;
        }
    }
}
