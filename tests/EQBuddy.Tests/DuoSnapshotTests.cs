using EQBuddy.Core;
using EQBuddy.UI.Shared;

namespace EQBuddy.Tests;

/// <summary>
/// The live seam around <see cref="DuoStats.Combine"/>: <see cref="SessionStats.DuoSnapshot"/>
/// and <see cref="SessionStats.DuoVersion"/>, fed exactly the way the poll feeds them
/// (<see cref="OwnLogDuo"/>) — one primary log, teammates derived from it, no file.
/// Each finding the audit named has a test here that reproduces its bug shape.
/// </summary>
public class DuoSnapshotTests
{
    private static readonly DateTime T = new(2026, 1, 1, 12, 0, 0);

    // ---- Party-kill rows: exact corrections, scoped to the PRIMARY's session ----

    /// <summary>Finding 1. A teammate death recorded in one primary session must not be
    /// subtracted from a genuine party kill by the same killer in a LATER session — the
    /// rows it corrected rolled over with the session.</summary>
    [Fact]
    public void AStaleTeammateDeathFromAnEarlierSessionNeverEatsALaterPartyKill()
    {
        var duo = new OwnLogDuo("Garg")
            .Feed(T, "Garg slashes a lizard defender for 20 points of damage.")
            .Feed(T.AddSeconds(5), "Garg has been slain by a lizard defender!");
        Assert.Equal(1, duo.Teammates.DeathKillersFor("Garg").GetValueOrDefault("a lizard defender"));

        // Three hours later, the primary's session rolls over; a lizard defender
        // genuinely kills somebody else, and Garg is back fighting.
        var next = T.AddHours(3);
        duo.Feed(next, "Ripto has been slain by a lizard defender!")
           .Feed(next.AddSeconds(2), "Garg slashes a gnoll for 5 points of damage.");

        Assert.Empty(duo.Teammates.DeathKillersFor("Garg"));
        var combined = duo.Combined();
        Assert.Contains(combined.PartyKillsByKiller, nc =>
            nc.Name.Equals("a lizard defender", StringComparison.OrdinalIgnoreCase) && nc.Count == 1);
        Assert.Equal(1, combined.PartyKillCount);
        Assert.Equal(combined.PartyKillCount, combined.PartyKillsByKiller.Sum(nc => nc.Count));
        Assert.Equal(5, combined.DamageDealt);   // yesterday's 20 went with yesterday's session
    }

    /// <summary>Finding 3. A teammate's kills from BEFORE they joined the roster are
    /// real party kills of theirs that were never promoted — they stay. Only the
    /// promoted count moves out of the killer row, so the killer rows still sum to the
    /// header.</summary>
    [Fact]
    public void ATeammatesPreJoinKillsStayPartyKillsAndKillerRowsSumToTheHeader()
    {
        var duo = new OwnLogDuo()
            .Feed(T, "A gnoll has been slain by Garg!")
            .Feed(T.AddSeconds(10), "A gnoll pup has been slain by Garg!")
            .Feed(T.AddSeconds(20), "Garg has joined the group.")
            .Feed(T.AddSeconds(30), "A gnoll has been slain by Garg!")
            .Feed(T.AddSeconds(40), "A froglok has been slain by Garg!")
            .Feed(T.AddSeconds(50), "A froglok has been slain by Garg!");

        var combined = duo.Combined();

        Assert.Equal(3, combined.YourKillCount);
        Assert.Equal(2, combined.PartyKillCount);
        var garg = Assert.Single(combined.PartyKillsByKiller);
        Assert.Equal("Garg", garg.Name);
        Assert.Equal(2, garg.Count);
        Assert.Equal(combined.PartyKillCount, combined.PartyKillsByKiller.Sum(nc => nc.Count));
        Assert.Equal(combined.PartyKillCount, combined.PartyKillsByTarget.Sum(nc => nc.Count));
    }

    [Fact]
    public void ADeathBeforeTheGroupFormedIsCorrectedOnceTheyAreATeammate()
    {
        var duo = new OwnLogDuo()
            .Feed(T, "Garg has been slain by a rock golem!")
            .Feed(T.AddSeconds(30), "Garg has joined the group.")
            .Feed(T.AddSeconds(40), "Garg slashes a rock golem for 50 points of damage.");

        var combined = duo.Combined();

        Assert.Equal(0, combined.PartyKillCount);
        Assert.Empty(combined.PartyKillsByTarget);
        Assert.Empty(combined.PartyKillsByKiller);
    }

    [Fact]
    public void AWardersPromotedKillLeavesTheKillerBreakdown()
    {
        var combined = new OwnLogDuo("Kellisanth")
            .Feed(T, "A gnoll has been slain by Kellisanth`s warder!")
            .Combined();

        Assert.Equal(1, combined.YourKillCount);
        Assert.Equal(0, combined.PartyKillCount);
        Assert.Empty(combined.PartyKillsByKiller);
    }

    // ---- Time: unions, never sums of rates ----

    /// <summary>Finding 2, the live rate. The primary's open span and Garg's overlap; the
    /// combined current DPS is the SUMMED live damage over the UNION of the live spans,
    /// not the primary's rate plus Garg's rate (each over its own, different span).</summary>
    [Fact]
    public void CurrentDpsIsSummedLiveDamageOverTheUnionOfLiveSpans()
    {
        var now = DateTime.Now;
        var t0 = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second).AddSeconds(-8);
        var duo = new OwnLogDuo("Garg")
            .Feed(t0, "You slash a gnoll for 100 points of damage.")
            .Feed(t0.AddSeconds(2), "You slash a gnoll for 100 points of damage.")
            .Feed(t0.AddSeconds(3), "Garg slashes a gnoll for 100 points of damage.")
            .Feed(t0.AddSeconds(4), "You slash a gnoll for 100 points of damage.")
            .Feed(t0.AddSeconds(5), "Garg slashes a gnoll for 100 points of damage.")
            .Feed(t0.AddSeconds(6), "Garg slashes a gnoll for 100 points of damage.");

        var mine = duo.Primary.Snapshot();
        var garg = duo.Teammates.Snapshots()["Garg"];
        Assert.True(mine.CurrentDps > 0 && garg.CurrentDps > 0, "both fights must still be live for this test");

        var combined = duo.Combined();

        // The primary's span is t0..t0+6 (Garg's swings keep a fight the primary is
        // in alive), Garg's t0+3..t0+6: union 6 s, 600 damage.
        Assert.Equal(600.0 / 6.0, combined.CurrentDps, 3);
        Assert.NotEqual(mine.CurrentDps + garg.CurrentDps, combined.CurrentDps, 3);
    }

    /// <summary>Finding 2, the recent window: two disjoint fights of 1,000 damage over 5 s
    /// each are 2,000 over 10 s — 200/s, not 400.</summary>
    [Fact]
    public void RecentDpsIsSummedDamageOverTheUnionOfWindowSpans()
    {
        var combined = new OwnLogDuo("Garg")
            .Feed(T, "You slash a froglok for 500 points of damage.")
            .Feed(T.AddSeconds(5), "You slash a froglok for 500 points of damage.")
            .Feed(T.AddSeconds(30), "Garg slashes a froglok for 500 points of damage.")
            .Feed(T.AddSeconds(35), "Garg slashes a froglok for 500 points of damage.")
            .Combined(TimeSpan.FromMinutes(5));

        Assert.NotNull(combined.Recent);
        Assert.Equal(200, combined.Recent!.Dps, 3);
    }

    [Fact]
    public void TwoDisjointFightsUnionRatherThanTakeTheMax()
    {
        var duo = new OwnLogDuo("Garg");
        for (var s = 0; s <= 9; s += 3) duo.Feed(T.AddSeconds(s), "You slash a gnoll for 100 points of damage.");
        for (var s = 100; s <= 109; s += 3) duo.Feed(T.AddSeconds(s), "Garg slashes a gnoll for 100 points of damage.");

        var combined = duo.Combined();

        Assert.Equal(18, combined.CombatSeconds, 3);
        Assert.Equal(800 / 18.0, combined.SessionDps, 3);
    }

    // ---- The '#202 trap': the caller's window and rules survive the combine ----

    [Fact]
    public void DuoSnapshotKeepsTheCallersRecentWindowAndTrackedRules()
    {
        var rules = new List<TrackedRule> { new() { Name = "Gnolls", Pattern = "gnoll", Kind = WatchKind.Kill } };
        var duo = new OwnLogDuo("Garg")
            .Feed(T, "You have slain a gnoll!")
            .Feed(T.AddSeconds(5), "Garg slashes a gnoll for 50 points of damage.");

        var solo = duo.Primary.Snapshot(TimeSpan.FromMinutes(10), rules);
        var combined = duo.Combined(TimeSpan.FromMinutes(10), rules);

        Assert.NotSame(solo, combined);   // really combined
        Assert.NotNull(combined.Recent);
        Assert.NotEmpty(combined.Tracked);
        Assert.Equal(solo.Tracked.Select(t => (t.Name, t.TotalQuantity)), combined.Tracked.Select(t => (t.Name, t.TotalQuantity)));
    }

    // ---- Versions: the phone pump's gate and the pushed snapshot agree ----

    [Fact]
    public void DuoVersionEqualsTheDuoSnapshotsVersionInEveryState()
    {
        var duo = new OwnLogDuo();
        void AssertAgree() => Assert.Equal(duo.Primary.DuoVersion, duo.Combined(TimeSpan.FromMinutes(5)).Version);

        duo.Feed(T, "You slash a gnoll for 10 points of damage.");
        AssertAgree();                                    // no teammate known
        duo.Feed(T.AddSeconds(1), "Garg has joined the group.")
           .Feed(T.AddSeconds(2), "Garg slashes a gnoll for 10 points of damage.");
        AssertAgree();                                    // teammate active
        Assert.NotEqual(duo.Primary.CurrentVersion, duo.Primary.DuoVersion);
        duo.Feed(T.AddHours(3), "You slash a gnoll for 10 points of damage.");
        AssertAgree();                                    // right after a primary rollover
        duo.Feed(T.AddHours(3).AddSeconds(1), "Garg slashes a gnoll for 10 points of damage.");
        AssertAgree();                                    // teammate back after the rollover
        duo.Teammates.Reset();
        AssertAgree();                                    // re-selection
    }

    /// <summary>The trap reproduced: a gate fed the primary's CurrentVersion while the
    /// pushed snapshot carries the combined version never agrees, so the 50 ms pump
    /// re-pushes on every reconciliation tick forever. Fed DuoVersion, it pushes nothing
    /// while nothing changes.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ThePumpLeaksPushesOnlyWhenTheGateReadsADifferentVersionThanItObserves(bool gateUsesDuoVersion, bool expectLeak)
    {
        var duo = new OwnLogDuo("Garg")
            .Feed(T, "You slash a gnoll for 10 points of damage.")
            .Feed(T.AddSeconds(1), "Garg slashes a gnoll for 10 points of damage.");
        var gate = new CompanionPumpGate();
        long pushes = 0;
        for (var second = 0; second < 50; second++)
        {
            gate.Observe(duo.Combined(TimeSpan.FromMinutes(5)).Version);   // RefreshUi's reconciliation push
            for (var tick = 0; tick < 20; tick++)                           // PumpCompanion, every 50 ms
                if (gate.ShouldPush(hasClients: true,
                        gateUsesDuoVersion ? duo.Primary.DuoVersion : duo.Primary.CurrentVersion))
                    pushes++;
        }
        Assert.Equal(expectLeak, pushes > 0);
    }

    // ---- Lifecycle ----

    [Fact]
    public void APrimaryRolloverRestartsTeammateTotalsButKeepsWhoIsInTheGroup()
    {
        var duo = new OwnLogDuo()
            .Feed(T, "Garg has joined the group.")
            .Feed(T.AddSeconds(1), "Garg slashes a gnoll for 500 points of damage.")
            .Feed(T.AddHours(2), "Garg slashes a gnoll for 7 points of damage.");

        Assert.Contains("Garg", duo.Teammates.AutoDetected);
        Assert.Equal(7, duo.Combined().DamageDealt);
    }

    /// <summary>
    /// The COMBINED <see cref="SessionStats.DuoSnapshot"/> total keeps counting what a
    /// teammate did for the rest of the primary's session even after they leave the group,
    /// per <see cref="DerivedTeammates.KnownTeammates"/>'s own documented contract ("a name
    /// that left the group keeps what it already accrued"). <see cref="SessionStats.PerPersonDps"/>
    /// agrees rather than disagreeing with this: it keeps a departed teammate's row too
    /// (tagged <see cref="SessionStats.PersonDps.IsCurrent"/> = false — see
    /// <see cref="PerPersonDpsTests.ADepartedTeammateKeepsItsRowTaggedNotCurrent"/>), so the
    /// panel's own rows still add up to exactly this combined total instead of silently
    /// falling short the moment somebody left.
    /// </summary>
    [Fact]
    public void ADepartedTeammatesAccruedDamageStaysInTheCombinedTotalForTheSession()
    {
        var duo = new OwnLogDuo()
            .Feed(T, "Garg has joined the group.")
            .Feed(T.AddSeconds(1), "You slash a gnoll for 10 points of damage.")
            .Feed(T.AddSeconds(2), "Garg slashes a gnoll for 20 points of damage.")
            .Feed(T.AddSeconds(3), "Garg has left the group.")
            .Feed(T.AddSeconds(4), "You slash a gnoll for 15 points of damage.");

        Assert.DoesNotContain("Garg", duo.Teammates.AutoDetected);
        // 10 + 20 + 15 — Garg's 20 is still in the combined total though he has left.
        Assert.Equal(45, duo.Combined().DamageDealt);
    }

    /// <summary>What MainWindow's "Reset session" does: the primary's Reset plus the
    /// teammates' ResetSession. Nothing of the old session survives on either side, and
    /// the roster does — the group did not change.</summary>
    [Fact]
    public void AManualSessionResetEndsTheTeammatesSessionToo()
    {
        var duo = new OwnLogDuo()
            .Feed(T, "Garg has joined the group.")
            .Feed(T.AddSeconds(1), "Garg slashes a gnoll for 500 points of damage.")
            .Feed(T.AddSeconds(2), "Garg has been slain by a gnoll!");

        duo.Primary.Reset();
        duo.Teammates.ResetSession();
        duo.Feed(T.AddSeconds(30), "A gnoll has been slain by a gnoll warrior!")
           .Feed(T.AddSeconds(31), "Garg slashes a gnoll for 3 points of damage.");

        var combined = duo.Combined();
        Assert.Equal(3, combined.DamageDealt);
        Assert.Equal(1, combined.PartyKillCount);   // the new session's real party kill survives
        Assert.Equal(combined.PartyKillCount, combined.PartyKillsByKiller.Sum(nc => nc.Count));
        Assert.Contains("Garg", duo.Teammates.AutoDetected);
    }

    [Fact]
    public void ArchivesStaySoloWhileTheDisplayCombines()
    {
        var duo = new OwnLogDuo("Garg")
            .Feed(T, "You have slain a gnoll!")
            .Feed(T.AddSeconds(5), "A froglok has been slain by Garg!");

        // The archiver, the 5-minute checkpoint and the wiki pack call the plain
        // Snapshot(); only DuoSnapshot combines.
        Assert.Equal(1, duo.Primary.Snapshot().YourKillCount);
        Assert.Equal(1, duo.Primary.Snapshot().PartyKillCount);
        Assert.Equal(2, duo.Combined().YourKillCount);
        Assert.Equal(0, duo.Combined().PartyKillCount);
    }

    [Fact]
    public void WithNoTeammateTheDuoSnapshotIsTheSoloSnapshotItself()
    {
        var duo = new OwnLogDuo().Feed(T, "You slash a gnoll for 10 points of damage.");
        Assert.Same(duo.Primary.Snapshot(TimeSpan.FromMinutes(5), null), duo.Combined(TimeSpan.FromMinutes(5)));
        Assert.Equal(duo.Primary.CurrentVersion, duo.Primary.DuoVersion);
    }
}
