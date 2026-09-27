using EQBuddy.Core;
using EQBuddy.UI.Shared;

namespace EQBuddy.Tests;

/// <summary>Options → Behavior → Teammates: the rule a hand-typed name has to pass, and the
/// live "detected" line. The WPF row (<c>SettingsTeammatesView</c>) only prints these.</summary>
public class TeammatesPresentationTests
{
    [Theory]
    [InlineData("garg", "Garg")]
    [InlineData("  GARG ", "Garg")]
    [InlineData("kellisanth", "Kellisanth")]
    public void ATypedNameIsPutInTheShapeTheLogPrintsIt(string typed, string expected)
    {
        Assert.True(TeammatesPresentation.TryNormalizeName(typed, "Smargush", [], out var name, out var refusal));
        Assert.Equal(expected, name);
        Assert.Null(refusal);
    }

    [Theory]
    [InlineData("", "Type a character name first.")]
    [InlineData("Garg the Bold", "letters only")]
    [InlineData("G4rg", "letters only")]
    [InlineData("smargush", "is you")]
    [InlineData("GARG", "already counted")]
    public void ARefusedNameSaysWhyRatherThanDoingNothing(string typed, string reason)
    {
        Assert.False(TeammatesPresentation.TryNormalizeName(typed, "Smargush", ["Garg"], out _, out var refusal));
        Assert.Contains(reason, refusal);
    }

    [Fact]
    public void AnAcceptedNameIsOneTheRosterMatchesInTheLog()
    {
        // The whole point of normalising: the rewrite matches names exactly, so "garg" must
        // come out as the "Garg" the log prints.
        Assert.True(TeammatesPresentation.TryNormalizeName("garg", "Smargush", [], out var name, out _));
        var derived = new DerivedTeammates();
        derived.Observe(new DateTime(2026, 9, 26, 13, 56, 0), "Garg slashes a gnoll for 42 points of damage.",
            "Smargush", null, [name]);
        Assert.Equal(42, derived.Snapshots()["Garg"].DamageDealt);
    }

    [Fact]
    public void TheDetectedLineNamesWhoTheLogPutInTheGroupOrSaysHowSomeoneGetsThere()
    {
        Assert.Equal("In your group now: Garg, Kellisanth.",
            TeammatesPresentation.DetectedLine(["Kellisanth", "Garg"]));
        Assert.Contains("nobody yet", TeammatesPresentation.DetectedLine([]));
        Assert.Contains("group", TeammatesPresentation.DetectedLine([]));
        Assert.Contains("logging out ends it", TeammatesPresentation.DetectedLine([]));
    }

    /// <summary>A hand-added name is a join the log can end, so its row says when the log
    /// has ended it, and how to count them again — rather than listing it as if it counted.</summary>
    [Fact]
    public void AHandAddedRowSaysWhetherItIsCountedNow()
    {
        Assert.Equal("Garg — counted now", TeammatesPresentation.ManualRow("Garg", countedNow: true));
        var ended = TeammatesPresentation.ManualRow("Garg", countedNow: false);
        Assert.StartsWith("Garg — not counted now", ended);
        Assert.Contains("Add them again", ended);
    }

    /// <summary>A name added before and since ended by the log can be added again — only
    /// someone counted RIGHT NOW is refused as a duplicate.</summary>
    [Fact]
    public void ANameTheLogEndedCanBeAddedAgain()
    {
        Assert.True(TeammatesPresentation.TryNormalizeName("garg", "Smargush", ["Kellisanth"], out var name, out var refusal));
        Assert.Equal("Garg", name);
        Assert.Null(refusal);
    }
}
