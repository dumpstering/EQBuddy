namespace EQBuddy.Core;

/// <summary>Which of a TEAMMATE's log events are admitted into the shared pipeline.
/// Your own log already carries everything bystander-visible when you play together
/// (third-party swings, "X has been slain by Teammate", mez/charm landings, zone lines),
/// so admitting those again would double-count. Character state (level, AA, skills,
/// stance, pet, buffs on YOU, /loc) belongs to the watched character only and would be
/// overwritten. What is left is what only their log knows: their first-person combat,
/// loot, money, XP, casts and deaths.
///
/// <b>Only SessionStats and the mez tracker ever see a teammate event.</b>
/// <see cref="BuffTracker"/> and <see cref="BuffLossLog"/> model the WATCHED
/// character's own body (buff durations learned from YOUR cast→worn-off pairs; buffs
/// lost on YOUR death or damage), <see cref="SlowTracker"/> reads a debuff wearing off
/// of YOU — a teammate's identically-worded first-person line would end your own
/// countdown or teach a wrong duration for a spell you never cast. Spawn timers, the
/// raid ledger and the spawn-point archive are persisted, group-visible ledgers your
/// own log already feeds (a kill, a raid boss falling, a spawn point are visible to
/// everyone nearby), so a teammate's copy of the same fact would only ever duplicate
/// what your own log already wrote in. Only the mez tracker gains real information
/// from a teammate's log that yours lacks: their mez casts, "Your Mesmerize spell has
/// worn off of X", their resists, and a mob hitting them (proof it's awake).</summary>
public static class TeammateFeed
{
    /// <summary>True when the event may be applied to SessionStats.</summary>
    public static bool AdmitForStats(GameEvent e) => e switch
    {
        KillEvent k => k.Killer == "You",
        DamageDealtEvent or DamageTakenEvent or MissEvent or HealEvent or RuneBlockEvent
            or RegenTickEvent or DeathEvent or LootEvent or MoneyEvent or AutoSellEvent
            or ItemDestroyedEvent or XpEvent or FactionEvent or CraftEvent or FashionEvent
            or ItemProcEvent or FizzleEvent or SpellCastEvent or SpellInterruptedEvent
            or SpellBlockedEvent or SpellWornOffEvent or ResistEvent or ConsiderEvent
            or RaidChatterEvent => true,
        _ => false,
    };

    /// <summary>True when the event may ALSO go to the mez tracker — the ONE other
    /// consumer that gains real information from a teammate's log (see the class
    /// doc). Kills are excluded: your own log already recorded this one as "slain by
    /// &lt;teammate&gt;". Deaths are excluded too: a death is a body-state fact about
    /// the teammate, not evidence about a mez/charm target.</summary>
    public static bool AdmitForMez(GameEvent e) => e is not KillEvent and not DeathEvent && AdmitForStats(e);
}
