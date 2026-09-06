using EQBuddy.Core;

namespace EQBuddy.Tests;

/// <summary>
/// TeammateFeed's admission rules in isolation — see LogWatcherTests for the end-to-end
/// version (a teammate's log actually joining a session through LogWatcher).
/// </summary>
public class TeammateFeedTests
{
    private static readonly DateTime T = new(2026, 1, 1, 12, 0, 0);

    [Fact]
    public void TeammateKillsDoNotReachTheSpawnLedgers()
    {
        // Your own log already recorded this kill as "slain by <teammate>" — the spawn
        // ledgers count kills by anyone, so admitting it again from their log would
        // double-count. SessionStats.YourKillCount still wants it, since only a "You"
        // kill from THEIR log tells you something your own log cannot. Mez is the ONE
        // other consumer a teammate line may ever reach, and a kill is not mez-relevant
        // either way.
        var yourKill = new KillEvent(T, "orc pawn", "You");
        Assert.True(TeammateFeed.AdmitForStats(yourKill));
        Assert.False(TeammateFeed.AdmitForMez(yourKill));
    }

    public static TheoryData<GameEvent, bool, bool> Rows()
    {
        var data = new TheoryData<GameEvent, bool, bool>();
        // Bystander-visible world lines: your own log already carries these when you
        // play together, so admitting them again would double-count or overwrite your
        // own character state.
        data.Add(new ThirdMeleeEvent(T, "Someone", "orc pawn", 10), false, false);
        data.Add(new LevelEvent(T, 20), false, false);
        data.Add(new ZoneEvent(T, "West Freeport"), false, false);
        data.Add(new BuffLandedEvent(T, "You feel much better."), false, false);
        // First-person combat and loot: only their log knows this, and it is
        // mez-relevant too (a mob hitting them, or landing damage on them, is
        // evidence the mob is awake).
        data.Add(new DamageDealtEvent(T, "orc pawn", 10, DamageKind.Melee, "You", false), true, true);
        data.Add(new LootEvent(T, "Rusty Dagger", "orc pawn", null), true, true);
        // A kill credited to someone other than "You" is neither a stats fact about the
        // teammate's own play nor mez-relevant from their log.
        data.Add(new KillEvent(T, "orc pawn", "Bob"), false, false);
        // A death is a body-state fact about the teammate (SessionStats wants it — it
        // is their own first-person death), but it is not evidence about a mez/charm
        // target, so BuffLossLog and friends must never see it either way — the mez
        // tracker excludes it explicitly, the same as a kill.
        data.Add(new DeathEvent(T, "orc pawn"), true, false);
        // A spell wearing off (not a kill, not a death) is exactly the kind of
        // first-person fact the mez tracker needs — "Your Mesmerize spell has worn
        // off of X" — so both admit it.
        data.Add(new SpellWornOffEvent(T, "Mesmerize", "orc pawn"), true, true);
        // Self-damage ("You hurt yourself for N points.") is still first-person combat
        // evidence from their log, admitted the same as any other damage taken.
        data.Add(new DamageTakenEvent(T, "orc pawn", 10, Melee: false, Self: true), true, true);
        return data;
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void AdmissionMatchesTheEventKind(GameEvent evt, bool admitStats, bool admitMez)
    {
        Assert.Equal(admitStats, TeammateFeed.AdmitForStats(evt));
        Assert.Equal(admitMez, TeammateFeed.AdmitForMez(evt));
    }
}
