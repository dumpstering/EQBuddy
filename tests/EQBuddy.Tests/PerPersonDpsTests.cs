using EQBuddy.Core;

namespace EQBuddy.Tests;

/// <summary>
/// <see cref="SessionStats.PerPersonDps"/> — the per-person duo readout's data half, fed
/// exactly the way <see cref="LogWatcher"/>'s poll feeds a session (<see cref="OwnLogDuo"/>):
/// one primary log, teammates derived from it, no file. Proves each row is that PERSON'S
/// OWN isolated number, never a combined or summed one.
/// </summary>
public class PerPersonDpsTests
{
    private static readonly DateTime T = new(2026, 1, 1, 12, 0, 0);

    [Fact]
    public void SoloReturnsOnlyTheUsersOwnRowMatchingTheSoloSnapshot()
    {
        var duo = new OwnLogDuo()
            .Feed(T, "You slash a zol ghoul knight for 200 points of damage.")
            .Feed(T.AddSeconds(1), "You slash a zol ghoul knight for 300 points of damage.");

        var rows = duo.Primary.PerPersonDps(null, null);
        var solo = duo.Primary.Snapshot(null, null);

        var mine = Assert.Single(rows);
        Assert.Equal(OwnLogDuo.PrimaryName, mine.Name);
        Assert.Equal(solo.DamageDealt, mine.DamageDealt);
        Assert.Equal(solo.SessionDps, mine.SessionDps);
        Assert.Equal(solo.CurrentDps, mine.CurrentDps);
        Assert.Equal(500, mine.DamageDealt);
    }

    /// <summary>The fixture the objective names verbatim: a join, then interleaved own
    /// and teammate melee lines — the shape <c>LogWatcher</c>'s poll actually sees, since
    /// the primary's OWN log prints a grouped teammate's swings in third person.</summary>
    [Fact]
    public void OneTeammateGivesEachPersonTheirOwnNumberNeverSummed()
    {
        var duo = new OwnLogDuo()
            .Feed(T, "You notify Garg that you agree to join the group.")
            .Feed(T.AddSeconds(1), "You slash a zol ghoul knight for 200 points of damage.")
            .Feed(T.AddSeconds(2), "Garg slashes a zol ghoul knight for 173 points of damage.")
            .Feed(T.AddSeconds(3), "You slash a zol ghoul knight for 300 points of damage.")
            .Feed(T.AddSeconds(4), "Garg slashes a zol ghoul knight for 173 points of damage.");

        var rows = duo.Primary.PerPersonDps(null, null);
        Assert.Equal(2, rows.Count);

        var mine = duo.Primary.Snapshot(null, null);
        var garg = duo.Teammates.LiveStats()["Garg"].Snapshot(null, null);

        Assert.Equal(OwnLogDuo.PrimaryName, rows[0].Name);
        Assert.Equal(mine.DamageDealt, rows[0].DamageDealt);
        Assert.Equal(500, rows[0].DamageDealt);

        Assert.Equal("Garg", rows[1].Name);
        Assert.Equal(garg.DamageDealt, rows[1].DamageDealt);
        Assert.Equal(346, rows[1].DamageDealt);

        // Neither row is the combined total — the bug shape a summed implementation
        // would produce.
        Assert.NotEqual(mine.DamageDealt + garg.DamageDealt, rows[0].DamageDealt);
        Assert.NotEqual(mine.DamageDealt + garg.DamageDealt, rows[1].DamageDealt);

        // And the combined display snapshot (what the widget's OWN header still reads)
        // is unaffected by this API — it stays the sum, proving the two paths don't
        // share mutated state.
        Assert.Equal(846, duo.Combined().DamageDealt);
    }

    [Fact]
    public void TwoTeammatesOrderPrimaryFirstThenNameSorted()
    {
        var duo = new OwnLogDuo()
            .Feed(T, "Garg has joined the group.")
            .Feed(T.AddSeconds(1), "Ally has joined the group.")
            .Feed(T.AddSeconds(2), "You slash a gnoll for 10 points of damage.")
            .Feed(T.AddSeconds(3), "Garg slashes a gnoll for 20 points of damage.")
            .Feed(T.AddSeconds(4), "Ally slashes a gnoll for 30 points of damage.");

        var rows = duo.Primary.PerPersonDps(null, null);

        Assert.Equal(3, rows.Count);
        Assert.Equal([OwnLogDuo.PrimaryName, "Ally", "Garg"], rows.Select(r => r.Name).ToArray());
        Assert.Equal(10, rows[0].DamageDealt);
        Assert.Equal(30, rows[1].DamageDealt);
        Assert.Equal(20, rows[2].DamageDealt);
    }

    [Fact]
    public void SameWindowAndRulesAsTheCallerAreHonoured()
    {
        var duo = new OwnLogDuo()
            .Feed(T, "Garg has joined the group.")
            .Feed(T.AddSeconds(1), "You slash a gnoll for 10 points of damage.")
            .Feed(T.AddSeconds(2), "Garg slashes a gnoll for 20 points of damage.");

        var window = TimeSpan.FromMinutes(5);
        var rows = duo.Primary.PerPersonDps(window, null);
        var mineWindowed = duo.Primary.Snapshot(window, null);
        var gargWindowed = duo.Teammates.LiveStats()["Garg"].Snapshot(window, null);

        Assert.Equal(mineWindowed.SessionDps, rows[0].SessionDps);
        Assert.Equal(gargWindowed.SessionDps, rows[1].SessionDps);
    }
}
