using EQBuddy.Core;
using EQBuddy.UI.Shared;

namespace EQBuddy.Tests;

/// <summary>
/// <see cref="PerPersonDpsPresentation"/> — the pure formatting behind the widget's
/// per-person DPS readout. Fed <see cref="SessionStats.PersonDps"/> rows directly, so it
/// needs no log, no window and no window.
/// </summary>
public class PerPersonDpsPresentationTests
{
    private static SessionStats.PersonDps P(string name, double session, double current, long dealt) =>
        new(name, session, current, dealt);

    [Fact]
    public void SoloWithNoTeammatesIsHiddenEntirely()
    {
        var rows = PerPersonDpsPresentation.Rows([P("Smargush", 176, 0, 1_234_567)]);
        Assert.Empty(rows);
    }

    [Fact]
    public void OneTeammateShowsTwoRowsUserFirst()
    {
        var rows = PerPersonDpsPresentation.Rows(
        [
            P("Smargush", 176, 240, 1_234_567),
            P("Garg", 1020, 1310, 1_100_000),
        ]);

        Assert.Equal(2, rows.Count);
        Assert.Equal("Smargush", rows[0].Name);
        Assert.Equal("Garg", rows[1].Name);
    }

    [Fact]
    public void TwoTeammatesShowThreeRowsInGivenOrder()
    {
        var rows = PerPersonDpsPresentation.Rows(
        [
            P("Smargush", 176, 0, 1_234_567),
            P("Garg", 1020, 0, 1_100_000),
            P("Kellisanth", 400, 0, 250_000),
        ]);

        Assert.Equal(3, rows.Count);
        Assert.Equal(["Smargush", "Garg", "Kellisanth"], rows.Select(r => r.Name).ToArray());
    }

    [Fact]
    public void ALiveFightShowsTheNowFigure()
    {
        var rows = PerPersonDpsPresentation.Rows([P("Smargush", 176, 0, 1_234_567), P("Garg", 1020, 1310, 1_100_000)]);

        Assert.DoesNotContain("now", rows[0].Value);
        Assert.Contains("176 dps", rows[0].Value);
        Assert.Contains("1,234,567 dmg", rows[0].Value);

        Assert.Contains("1020 dps (now 1310)", rows[1].Value);
        Assert.Contains("1,100,000 dmg", rows[1].Value);
    }

    [Fact]
    public void NoLiveFightOmitsTheNowFigureEvenWithATeammate()
    {
        var rows = PerPersonDpsPresentation.Rows([P("Smargush", 176, 0, 1_234_567), P("Garg", 1020, 0, 1_100_000)]);

        Assert.DoesNotContain("now", rows[0].Value);
        Assert.DoesNotContain("now", rows[1].Value);
    }

    /// <summary>Each row is its OWN number — nothing here ever adds one person's dps or
    /// damage to another's, which is the whole point of the isolated-per-teammate design.
    /// A summed implementation would fail this the moment two rows differ.</summary>
    [Fact]
    public void RowsAreNeverSummedAcrossPeople()
    {
        var rows = PerPersonDpsPresentation.Rows([P("Smargush", 176, 0, 1_234_567), P("Garg", 1020, 0, 1_100_000)]);

        Assert.Contains("176 dps", rows[0].Value);
        Assert.DoesNotContain("1196", rows[0].Value);   // 176 + 1020, the summed-rate bug shape
        Assert.Contains("1020 dps", rows[1].Value);
        Assert.DoesNotContain("2,334,567", rows[1].Value);   // the summed-damage bug shape
    }
}
