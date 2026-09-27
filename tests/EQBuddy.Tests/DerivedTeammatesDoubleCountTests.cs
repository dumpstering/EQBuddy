using EQBuddy.Core;

namespace EQBuddy.Tests;

/// <summary>
/// The explicit double-count audit the own-log-teammates STEP 2 task asked for: every
/// scenario here drives a REAL primary <see cref="SessionStats"/> and a REAL
/// <see cref="DerivedTeammates"/> off the SAME raw log line (never a hand-built
/// event), through the UNCHANGED <see cref="LogParser"/> both times, then checks the
/// field the double-count could actually land in — not a re-derivation of the
/// arithmetic, a check of the real pipeline's output.
/// </summary>
public class DerivedTeammatesDoubleCountTests
{
    private const string Primary = "Smargush";
    private static readonly string[] Roster = ["Garg"];

    private static SessionStats NewPrimary() => new() { CharacterName = Primary };

    // ---------------------------------------------------------------------
    // (1) A kill "X has been slain by Garg!" counts once in combined kills.
    // ---------------------------------------------------------------------
    [Fact]
    public void TeammateKillCountsOnceInCombinedKills()
    {
        var primary = NewPrimary();
        var derived = new DerivedTeammates();
        var t = new DateTime(2026, 1, 1, 12, 0, 0);
        const string line = "A frenzied ghoul has been slain by Garg!";

        // Same line, same timestamp, fed to both — exactly how LogWatcher's primary
        // poll and its DerivedTeammates hook would each see it.
        primary.Apply(LogParser.Parse(t, line)!);
        derived.Observe(t, line, Primary, null, Roster);

        var mates = derived.Snapshots();
        Assert.True(mates.ContainsKey("Garg"));
        Assert.Equal(1, mates["Garg"].YourKillCount);
        // From the PRIMARY's own log, this is (before correction) a party kill of
        // target "A frenzied ghoul" — the exact row TeammateCombine must zero out
        // via the same-line-exact subtraction, not just leave doubled.
        Assert.Equal(1, primary.Snapshot().PartyKillCount);

        var combined = TeammateCombine.Combine(primary, derived, null, null);

        Assert.Equal(1, combined.YourKillCount);   // 0 (mine) + 1 (Garg) — not 2
        Assert.Equal(0, combined.PartyKillCount);  // the same kill, subtracted exactly
        Assert.DoesNotContain(combined.PartyKillsByTarget, nc => nc.Name.Equals("A frenzied ghoul", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------------
    // (2) "You healed Garg for N" counts once as the user's healing done AND once as
    // Garg's healing received — never doubled in any SUM field.
    // ---------------------------------------------------------------------
    [Fact]
    public void HealCountsOnceEachSideNeverDoubled()
    {
        var primary = NewPrimary();
        var derived = new DerivedTeammates();
        var t = new DateTime(2026, 1, 1, 12, 0, 0);
        const string line = "You healed Garg for 104 hit points.";

        primary.Apply(LogParser.Parse(t, line)!);
        derived.Observe(t, line, Primary, null, Roster);

        Assert.Equal(104, primary.Snapshot().HealingDone);
        Assert.Equal(0, primary.Snapshot().HealingReceived);

        var mates = derived.Snapshots();
        Assert.Equal(0, mates["Garg"].HealingDone);
        Assert.Equal(104, mates["Garg"].HealingReceived);

        var combined = TeammateCombine.Combine(primary, derived, null, null);

        // The heal is 104 exactly ONCE in each of the two summed fields — 208 in
        // either would mean it landed twice somewhere.
        Assert.Equal(104, combined.HealingDone);
        Assert.Equal(104, combined.HealingReceived);
    }

    // ---------------------------------------------------------------------
    // (3) The user's own stats are unchanged by the feature when the roster is empty
    // (golden: combined == primary).
    // ---------------------------------------------------------------------
    [Fact]
    public void EmptyRosterIsAGoldenNoOp()
    {
        var primary = NewPrimary();
        var t = new DateTime(2026, 1, 1, 12, 0, 0);
        primary.Apply(LogParser.Parse(t, "Garg slashes a froglok for 91 points of damage.")!);
        primary.Apply(LogParser.Parse(t.AddSeconds(1), "You slash a froglok for 50 points of damage.")!);

        var snap = primary.Snapshot();
        var combined = TeammateCombine.Combine(primary, new DerivedTeammates(), null, null);

        Assert.Same(snap, combined);   // no roster, no fold — the identical reference
    }

    // ---------------------------------------------------------------------
    // (4) A teammate's own death is not a party kill.
    // ---------------------------------------------------------------------
    [Fact]
    public void TeammateDeathIsNotAPartyKill()
    {
        var primary = NewPrimary();
        var derived = new DerivedTeammates();
        var t = new DateTime(2026, 1, 1, 12, 0, 0);
        const string line = "Garg has been slain by a rock golem!";

        // From the PRIMARY's own (unchanged) LogParser, this is — before correction —
        // an ordinary third-party KillEvent(Target: "Garg"), which upstream's own
        // Apply files as a party kill. That upstream quirk is exactly what
        // TeammateCombine must undo for a roster member.
        primary.Apply(LogParser.Parse(t, line)!);
        Assert.Equal(1, primary.Snapshot().PartyKillCount);
        Assert.Contains(primary.Snapshot().PartyKillsByTarget, nc => nc.Name.Equals("Garg", StringComparison.OrdinalIgnoreCase));

        derived.Observe(t, line, Primary, null, Roster);
        var mates = derived.Snapshots();
        // Garg's OWN perspective of this line is a death, not a kill of anything —
        // it never reaches Garg.YourKills at all.
        Assert.Empty(mates["Garg"].YourKills);
        Assert.Single(mates["Garg"].Deaths);

        var combined = TeammateCombine.Combine(primary, derived, null, null);

        Assert.Equal(0, combined.PartyKillCount);
        Assert.DoesNotContain(combined.PartyKillsByTarget, nc => nc.Name.Equals("Garg", StringComparison.OrdinalIgnoreCase));
        // The KILLER row that death put there goes too — "a rock golem" made no party
        // kill — so the killer breakdown still sums to PartyKillCount.
        Assert.DoesNotContain(combined.PartyKillsByKiller, nc => nc.Name.Equals("a rock golem", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(combined.PartyKillCount, combined.PartyKillsByKiller.Sum(nc => nc.Count));
    }

    // ---------------------------------------------------------------------
    // Roster detection: auto-detect from a join line, plus the names the caller adds by
    // hand, excluding the primary, the primary's pet, and any name ever seen as an NPC.
    // ---------------------------------------------------------------------
    [Fact]
    public void RosterAutoDetectsGroupJoinsAndExcludesKnownNames()
    {
        var derived = new DerivedTeammates();
        var t = new DateTime(2026, 1, 1, 12, 0, 0);

        derived.Observe(t, "Garg has joined the group.", Primary, null, ["Radiant", "Smargush", "Jarartik"]);
        derived.Observe(t, "Targeted (NPC): Radiant", Primary, null, null);

        var roster = derived.Roster(Primary, "Jarartik");

        Assert.Contains("Garg", roster);
        Assert.DoesNotContain("Radiant", roster);      // learned NPC, even though also typed manually
        Assert.DoesNotContain("Smargush", roster);     // never the primary's own name
        Assert.DoesNotContain("Jarartik", roster);     // never the primary's own current pet
    }

    [Fact]
    public void ExactWordMatchingRefusesGargoyle()
    {
        var derived = new DerivedTeammates();
        var t = new DateTime(2026, 1, 1, 12, 0, 0);
        // Capitalised, matching how the log actually prints a roster name ("Garg") —
        // a lowercase "gargoyle" against roster entry "Garg" would never match the
        // word-boundary check for an unrelated reason (case), so it proved nothing
        // about the boundary itself. See TeammatePerspectiveFixesTests for the
        // dedicated case-insensitivity coverage.
        const string line = "A basalt Gargoyle hits you for 40 points of damage.";

        derived.Observe(t, line, Primary, null, Roster);

        Assert.Empty(derived.Snapshots());
    }
}
