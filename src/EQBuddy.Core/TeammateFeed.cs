namespace EQBuddy.Core;

/// <summary>Which of a TEAMMATE's log events reach the ONE consumer left that still
/// filters them: the shared <see cref="MezTracker"/>.
///
/// Step 3 of the teammate-isolation redesign retired the whole reason this class used to
/// need two different admission lists. A teammate's log now drives its OWN, fully
/// isolated <see cref="SessionStats"/> instance (built and owned by
/// <see cref="TeammateLogTail"/>) — every parsed event reaches it unconditionally, with
/// no gate and no <c>fromTeammate</c> flag, because that instance never acquires a
/// durable store, a subscriber, or the watched character's identity (see
/// <see cref="TeammateLogTail"/>'s class doc for the invariant that makes this safe by
/// construction). What used to be <c>AdmitForStats</c> — a hand-maintained list of which
/// event kinds were "safe" to let touch the shared instance — is gone along with the
/// shared instance it protected.
///
/// The mez tracker is different: it is ONE tracker shared by both logs (a landing is
/// bystander-visible; only the CASTER's own log ever prints "Your X spell has worn off of
/// Y"), so it still needs telling which of a teammate's events are real evidence for it —
/// see <see cref="AdmitForMez"/>.</summary>
public static class TeammateFeed
{
    /// <summary>True when the event may go to the mez tracker — the one consumer that
    /// still needs telling apart from "everything a teammate's log parses". Tightened
    /// (Step 2, plan Part 4b) to EXACTLY what <see cref="MezTracker.Apply(GameEvent, string)"/>'s
    /// switch consumes: everything else used to be admitted here too but hit no case in
    /// that switch and only ever advanced its own Prune. Kills are excluded: your own
    /// log already recorded this one as "slain by &lt;teammate&gt;". Deaths are excluded
    /// too: a death is a body-state fact about the teammate, not evidence about a
    /// mez/charm target.</summary>
    public static bool AdmitForMez(GameEvent e) => e switch
    {
        SpellCastEvent or SpellWornOffEvent or SpellInterruptedEvent or SpellBlockedEvent
            or FizzleEvent or ResistEvent or DamageDealtEvent or DamageTakenEvent => true,
        _ => false,
    };
}
