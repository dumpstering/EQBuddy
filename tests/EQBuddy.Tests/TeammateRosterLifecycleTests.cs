using EQBuddy.Core;

namespace EQBuddy.Tests;

/// <summary>
/// When auto-detected group membership starts and ends: a login ends it (unless the
/// group survived a quick relog), kill-plus-party-XP correlation starts it for a member
/// no group line names, and a "Reset session" that splits the log keeps it. Every line
/// shape here is copied from the player's real log. Each test failed before its fix.
/// </summary>
public sealed class TeammateRosterLifecycleTests : IDisposable
{
    private const string Primary = OwnLogDuo.PrimaryName;
    private static readonly DateTime T = new(2026, 9, 8, 21, 25, 30);
    private const string Login = "Welcome to EverQuest Legends!";
    private const string PartyXp = "You gain party experience! (0.887%)";

    private readonly string _logs = Directory.CreateTempSubdirectory("eqbuddy-roster-").FullName;
    private readonly string _path;

    public TeammateRosterLifecycleTests() => _path = Path.Combine(_logs, "eqlog_Smargush_rivervale.txt");

    public void Dispose()
    {
        try { Directory.Delete(_logs, recursive: true); } catch (IOException) { }
    }

    private static string L(int seconds, string msg) => $"[{T.AddSeconds(seconds):ddd MMM dd HH:mm:ss yyyy}] {msg}";

    private DateTime _t = T;

    private OwnLogDuo Feed(OwnLogDuo duo, params string[] msgs)
    {
        foreach (var msg in msgs) duo.Feed(_t = _t.AddSeconds(1), msg);
        return duo;
    }

    private IReadOnlyCollection<string> Roster(OwnLogDuo duo) => duo.Teammates.Roster(Primary, null, duo.Teammates.ManualNames);

    // ---------------------------------------------------------------------
    // Finding 1: a groupmate from days ago stayed whitelisted.
    // ---------------------------------------------------------------------

    /// <summary>The real log: the player accepts Garg's invite on Sep 08, camps with no
    /// leave line, and logs in solo on Sep 09. Garg fighting nearby after that login is a
    /// bystander — his damage and kill must stay out of the combined totals.</summary>
    [Fact]
    public void ALoginEndsTheDetectedGroup()
    {
        var duo = Feed(new OwnLogDuo(),
            "You notify Garg that you agree to join the group.",
            "It will take you about 30 seconds to prepare your camp.",
            Login,
            "Garg hits a gnoll for 100 points of damage.",
            "A gnoll has been slain by Garg!");

        Assert.DoesNotContain("Garg", Roster(duo));
        Assert.Empty(duo.Teammates.AutoDetected);
        var combined = duo.Combined();
        Assert.Equal(0, combined.DamageDealt);
        Assert.Equal(0, combined.YourKillCount);
        Assert.Equal(1, combined.PartyKillCount);
    }

    [Fact]
    public void ALoginLeavesTheManualNamesAlone()
    {
        var duo = Feed(new OwnLogDuo("Garg"), "Kellisanth has joined the group.", Login);

        Assert.Contains("Garg", Roster(duo));
        Assert.DoesNotContain("Kellisanth", Roster(duo));
    }

    /// <summary>The real log, Aug 16 15:49: a camp and a relog 35 seconds later, and the
    /// first party XP 14 seconds after that with no group line in between — the group
    /// survived. The members come back at that party-XP line.</summary>
    [Fact]
    public void AQuickRelogTheGroupSurvivedPutsTheMembersBackAtTheFirstPartyXp()
    {
        var duo = Feed(new OwnLogDuo(), "Garg has joined the group.", Login);
        Assert.DoesNotContain("Garg", Roster(duo));

        Feed(duo, PartyXp, "Garg punches a sand scarab for 10 points of damage.");

        Assert.Contains("Garg", Roster(duo));
        Assert.Equal(10, duo.Combined().DamageDealt);
    }

    [Fact]
    public void AGroupLineAfterALoginDropsTheMembersSetAside()
    {
        var duo = Feed(new OwnLogDuo(), "Garg has joined the group.", Login,
            "Yungweezy has joined the group.", PartyXp);

        Assert.DoesNotContain("Garg", Roster(duo));
        Assert.Contains("Yungweezy", Roster(duo));
    }

    [Fact]
    public void TheNextLoginDropsTheMembersSetAside()
    {
        var duo = Feed(new OwnLogDuo(), "Garg has joined the group.", Login, Login, PartyXp);

        Assert.DoesNotContain("Garg", Roster(duo));
    }

    /// <summary>A re-derivation reads the same lines in the same order, so it must end
    /// the group at the same login the live feed did.</summary>
    [Fact]
    public void ARederiveEndsTheGroupAtTheSameLogin()
    {
        var stats = new SessionStats();
        using var watcher = Watch(stats,
        [
            L(0, "Garg has joined the group."),
            L(1, "You slash a gnoll for 10 points of damage."),
            L(2, "Garg slashes a gnoll for 5 points of damage."),
            L(60, Login),
            L(61, "Garg slashes a gnoll for 1000 points of damage."),
        ]);
        Assert.Equal(15, stats.DuoSnapshot(null, null).DamageDealt);

        watcher.Teammates.ManualNames = ["Yungweezy"];
        watcher.RederiveTeammates();

        Assert.Equal(15, stats.DuoSnapshot(null, null).DamageDealt);
    }

    // ---------------------------------------------------------------------
    // Finding 2: kill-plus-party-XP correlation, and a split log keeps its group.
    // ---------------------------------------------------------------------

    private static string[] PartyKills(string name, int n) =>
        [.. Enumerable.Range(0, n).SelectMany(_ => new[] { PartyXp, $"A gnoll has been slain by {name}!" })];

    [Fact]
    public void ThreeKillsRightAfterPartyXpMakeAGroupmateWithNoGroupLine()
    {
        var duo = Feed(new OwnLogDuo(), PartyKills("Garg", TeammateRoster.PartyKillsToJoin - 1));
        Assert.DoesNotContain("Garg", Roster(duo));

        Feed(duo, PartyKills("Garg", 1));
        Feed(duo, "Garg slashes a gnoll for 7 points of damage.");

        Assert.Contains("Garg", duo.Teammates.AutoDetected);
        var combined = duo.Combined();
        Assert.Equal(7, combined.DamageDealt);
        Assert.Equal(1, combined.YourKillCount);   // the kill that promoted him is his
    }

    [Fact]
    public void ABystandersKillsWithoutPartyXpNeverMakeATeammate()
    {
        var duo = Feed(new OwnLogDuo(),
            [.. Enumerable.Repeat("A gnoll has been slain by Axercis!", 10)]);
        // The player's own kill takes the party XP; a bystander's kill a line later does not.
        for (var i = 0; i < 5; i++)
            Feed(duo, PartyXp, "You have slain a gnoll!", "A gnoll has been slain by Axercis!");

        Assert.DoesNotContain("Axercis", duo.Teammates.AutoDetected);
    }

    /// <summary>The player's own SK pet (renamed on every summon) lands kills right after
    /// party XP too; it says "Attacking a bat Master." first, and "told you" is only ever
    /// an NPC's or a pet's line.</summary>
    [Fact]
    public void APetsPartyXpKillsNeverMakeATeammate()
    {
        var duo = Feed(new OwnLogDuo(), "Kann told you, 'Attacking a bat Master.'");
        Feed(duo, PartyKills("Kann", 5));

        Assert.DoesNotContain("Kann", Roster(duo));
        Assert.DoesNotContain("Kann", duo.Teammates.AutoDetected);
    }

    [Fact]
    public void ALoginRestartsTheCorrelationCount()
    {
        var duo = Feed(new OwnLogDuo(), PartyKills("Garg", TeammateRoster.PartyKillsToJoin - 1));
        Feed(duo, Login);
        Feed(duo, PartyKills("Garg", 1));

        Assert.DoesNotContain("Garg", duo.Teammates.AutoDetected);
    }

    private LogWatcher Watch(SessionStats stats, IEnumerable<string> lines)
    {
        File.WriteAllLines(_path, lines);
        var watcher = new LogWatcher(stats) { DeferIngestForTests = true };
        watcher.Select(_path);
        watcher.FinishInitialIngest(watcher.SelectGeneration);
        return watcher;
    }

    /// <summary>The harness from the finding: a "Reset session" with archiving on moves
    /// the log (join line and all) to Logs\archive and starts an empty one. Editing the
    /// manual names then replayed the new file from a roster of nobody and silently
    /// dropped Garg while he was still in the group.</summary>
    [Fact]
    public void ARederiveAfterTheLogWasSplitKeepsTheGroupAtTheSplit()
    {
        var stats = new SessionStats();
        using var watcher = Watch(stats,
        [
            L(0, "Garg has joined the group."),
            L(1, "You slash a gnoll for 10 points of damage."),
            L(2, "Garg slashes a gnoll for 100 points of damage."),
        ]);
        Assert.Equal(110, stats.DuoSnapshot(null, null).DamageDealt);

        // MainWindow.OnReset, archiving on.
        stats.Reset(); stats.Teammates.ResetSession();
        Assert.NotNull(EqConfig.SplitLog(_path));
        File.AppendAllLines(_path,
        [
            L(10, "You slash a gnoll for 10 points of damage."),
            L(11, "Garg slashes a gnoll for 5 points of damage."),
        ]);
        watcher.FinishInitialIngest(watcher.SelectGeneration);   // one more poll: the truncated file
        Assert.Equal(15, stats.DuoSnapshot(null, null).DamageDealt);

        watcher.Teammates.ManualNames = ["Yungweezy"];
        watcher.RederiveTeammates();

        Assert.Equal(15, stats.DuoSnapshot(null, null).DamageDealt);
        Assert.Contains("Garg", watcher.Teammates.AutoDetected);
    }

    /// <summary>The same split, then a relaunch: nothing carries across a process, so the
    /// fresh watcher finds Garg the way it finds any member no group line names.</summary>
    [Fact]
    public void ARelaunchOnASplitLogFindsTheGroupmateFromPartyXpKills()
    {
        var lines = new List<string> { L(0, "You slash a gnoll for 10 points of damage.") };
        var s = 1;
        for (var i = 0; i < TeammateRoster.PartyKillsToJoin; i++)
        {
            lines.Add(L(s++, PartyXp));
            lines.Add(L(s++, "A gnoll has been slain by Garg!"));
        }
        lines.Add(L(s, "Garg slashes a gnoll for 5 points of damage."));

        var stats = new SessionStats();
        using var watcher = Watch(stats, lines);

        Assert.Contains("Garg", watcher.Teammates.AutoDetected);
        Assert.Equal(15, stats.DuoSnapshot(null, null).DamageDealt);
    }
}
