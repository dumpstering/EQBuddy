using EQBuddy.Core;

namespace EQBuddy.Tests;

/// <summary>
/// TeammateFeed's one remaining admission rule in isolation — see LogWatcherTests for
/// the end-to-end version (a teammate's log actually joining a session through
/// LogWatcher) and TeammateIsolationTests for the structural guarantee that makes
/// every OTHER event kind safe to apply to the teammate's own SessionStats
/// unconditionally (Step 3 retired the whole idea of a second admission list for
/// stats — see TeammateFeed's own class doc).
/// </summary>
public class TeammateFeedTests
{
    private static readonly DateTime T = new(2026, 1, 1, 12, 0, 0);

    public static TheoryData<GameEvent, bool> Rows()
    {
        var data = new TheoryData<GameEvent, bool>();
        // Bystander-visible world lines: your own log already carries these when you
        // play together, so re-admitting them to the mez tracker would double-count
        // or misattribute character state that belongs to the WATCHED character.
        data.Add(new ThirdMeleeEvent(T, "Someone", "orc pawn", 10), false);
        data.Add(new LevelEvent(T, 20), false);
        data.Add(new ZoneEvent(T, "West Freeport"), false);
        data.Add(new BuffLandedEvent(T, "You feel much better."), false);
        // First-person combat: mez-relevant (a mob hitting them, or landing damage on
        // them, is evidence the mob is awake).
        data.Add(new DamageDealtEvent(T, "orc pawn", 10, DamageKind.Melee, "You", false), true);
        // Loot is a stats fact about the teammate (now applied to their own isolated
        // SessionStats unconditionally — see TeammateIsolationTests), but
        // MezTracker.Apply has no case for it at all — admitting it here bought
        // nothing (Step 2, plan Part 4b).
        data.Add(new LootEvent(T, "Rusty Dagger", "orc pawn", null), false);
        // A kill credited to someone other than "You" is not mez-relevant from their
        // log either way.
        data.Add(new KillEvent(T, "orc pawn", "Bob"), false);
        // A death is not evidence about a mez/charm target.
        data.Add(new DeathEvent(T, "orc pawn"), false);
        // Casts and worn-offs are real evidence for the shared mez tracker — "Your
        // Mesmerize spell has worn off of X" and a teammate's own cast are exactly
        // what lets it track a mez cast from either side of a duo.
        data.Add(new SpellWornOffEvent(T, "Mesmerize", "orc pawn"), true);
        data.Add(new SpellCastEvent(T, "Mesmerize"), true);
        // Self-damage ("You hurt yourself for N points.") is still first-person combat
        // evidence from their log, admitted the same as any other damage taken.
        data.Add(new DamageTakenEvent(T, "orc pawn", 10, Melee: false, Self: true), true);
        // SpellInterruptedEvent and SpellBlockedEvent cancel a failed pending cast
        // (Step 2, plan Part 4a) so it cannot later claim a landing it never
        // produced. Fizzle and resist stay admitted too — Fizzle for the same
        // cancellation, ResistEvent to keep an awake creature's ledger current.
        data.Add(new SpellInterruptedEvent(T, "Charm"), true);
        data.Add(new SpellBlockedEvent(T, "Charm", "Rune"), true);
        data.Add(new FizzleEvent(T, "Charm"), true);
        data.Add(new ResistEvent(T, "Charm", "orc pawn"), true);
        // Item procs are the watched character's own last-item-proc attribution, and
        // MezTracker.Apply has no case for them either — Step 2 tightened AdmitForMez
        // to stop admitting it for no purpose.
        data.Add(new ItemProcEvent(T, "Kerdude's Bolt of Flame"), false);
        return data;
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void AdmissionMatchesTheEventKind(GameEvent evt, bool admitMez) =>
        Assert.Equal(admitMez, TeammateFeed.AdmitForMez(evt));

    /// <summary>Plan Part 4b: tighten AdmitForMez to exactly what MezTracker.Apply's
    /// switch consumes — SpellCastEvent, SpellWornOffEvent, SpellInterruptedEvent,
    /// SpellBlockedEvent, FizzleEvent, ResistEvent, DamageDealtEvent, DamageTakenEvent.
    /// Everything else that used to be admitted (MissEvent, HealEvent, RuneBlockEvent,
    /// RegenTickEvent, LootEvent, MoneyEvent, AutoSellEvent, ItemDestroyedEvent,
    /// XpEvent, FactionEvent, CraftEvent, FashionEvent, ItemProcEvent, ConsiderEvent,
    /// RaidChatterEvent) hits no case in the switch and only ever advanced Prune.
    ///
    /// Reflects over EVERY concrete GameEvent subtype in the assembly — not just the
    /// ones this file happens to list — via <see cref="System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject"/>,
    /// which builds a same-TYPE instance without running any constructor (AdmitForMez
    /// is a pure type-pattern switch, so field values don't matter). A future GameEvent
    /// type is picked up automatically and must be added to
    /// <see cref="ConsumedByMezFromATeammate"/> — with a reason — or this fails.</summary>
    private static readonly HashSet<Type> ConsumedByMezFromATeammate =
    [
        typeof(SpellCastEvent), typeof(SpellWornOffEvent), typeof(SpellInterruptedEvent),
        typeof(SpellBlockedEvent), typeof(FizzleEvent), typeof(ResistEvent),
        typeof(DamageDealtEvent), typeof(DamageTakenEvent),
    ];

    [Fact]
    public void AdmitForMezCarriesOnlyWhatTheTrackerConsumes()
    {
        var eventTypes = typeof(GameEvent).Assembly.GetTypes()
            .Where(t => typeof(GameEvent).IsAssignableFrom(t) && !t.IsAbstract);
        var failures = new List<string>();
        foreach (var t in eventTypes)
        {
            var instance = (GameEvent)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t);
            var expected = ConsumedByMezFromATeammate.Contains(t);
            var actual = TeammateFeed.AdmitForMez(instance);
            if (expected != actual)
                failures.Add($"{t.Name}: expected AdmitForMez={expected}, got {actual}");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
