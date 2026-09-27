using System.Text.Json;
using EQBuddy.Core;

namespace EQBuddy.Tests;

/// <summary>
/// Finding: a name added by hand in Options → Behavior was ALWAYS on the roster. No leave,
/// disband or login line ended it, and the list was one global setting, so it carried
/// over to later sessions, other days and other characters — any line naming that player
/// (a bystander's hits, kills and heals) was rewritten into their stats and added to the
/// combined totals and the phone.
///
/// A hand-added name is now a join (<see cref="ManualTeammate"/>): for ONE character on
/// ONE server, at the start of the session it was added in, and from there a member like
/// any the log detected — the same lines end it, and only a new group signal or adding it
/// again brings it back. Each test here fails on the unconditional roster.
/// </summary>
public sealed class HandAddedTeammateTests : IDisposable
{
    private const string Primary = OwnLogDuo.PrimaryName;
    private const string Server = "rivervale";
    private const string Login = "Welcome to EverQuest Legends!";
    private static readonly DateTime T = new(2026, 9, 8, 20, 0, 0);

    private readonly string _logs = Directory.CreateTempSubdirectory("eqbuddy-handadded-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_logs, recursive: true); } catch (IOException) { }
    }

    private static string L(DateTime at, string msg) => $"[{at:ddd MMM dd HH:mm:ss yyyy}] {msg}";

    private static ManualTeammate Garg(DateTime since, string character = Primary, string server = Server) =>
        new("Garg", character, server, since);

    /// <summary>The way MainWindow runs it: the saved list is handed to the watcher, then a
    /// log is selected and read.</summary>
    private LogWatcher Watch(SessionStats stats, string fileName, IEnumerable<string> lines,
        params ManualTeammate[] manual)
    {
        var path = Path.Combine(_logs, fileName);
        File.WriteAllLines(path, lines);
        var watcher = new LogWatcher(stats) { DeferIngestForTests = true };
        watcher.Teammates.Manual = manual;
        watcher.Select(path);
        watcher.FinishInitialIngest(watcher.SelectGeneration);
        return watcher;
    }

    // ---------------------------------------------------------------------
    // The log's own lines end a hand-added name.
    // ---------------------------------------------------------------------

    [Fact]
    public void AHandAddedNameEndsOnItsLeaveLine()
    {
        var duo = new OwnLogDuo("Garg")
            .Feed(T, "Garg slashes a gnoll for 10 points of damage.")
            .Feed(T.AddSeconds(1), "Garg has left the group.")
            .Feed(T.AddSeconds(2), "Garg slashes a gnoll for 1000 points of damage.")
            .Feed(T.AddSeconds(3), "A gnoll has been slain by Garg!");

        Assert.DoesNotContain("Garg", duo.Teammates.Roster(Primary, null));
        var combined = duo.Combined();
        Assert.Equal(10, combined.DamageDealt);
        Assert.Equal(0, combined.YourKillCount);
        Assert.Equal(1, combined.PartyKillCount);
    }

    [Theory]
    [InlineData("You remove Garg from the party.")]
    [InlineData("Your group has been disbanded.")]
    [InlineData("You have been removed from the group.")]
    [InlineData("You have left the group.")]
    [InlineData("You remove Smargush from the party.")]   // the player's own /disband
    [InlineData(Login)]
    public void ARemovalADisbandOrALoginEndsAHandAddedName(string line)
    {
        var duo = new OwnLogDuo("Garg")
            .Feed(T, "Garg slashes a gnoll for 10 points of damage.")
            .Feed(T.AddSeconds(1), line)
            .Feed(T.AddSeconds(2), "Garg slashes a gnoll for 1000 points of damage.");

        Assert.DoesNotContain("Garg", duo.Teammates.Roster(Primary, null));
        Assert.Equal(10, duo.Combined().DamageDealt);
    }

    [Fact]
    public void OnlyANewGroupSignalBringsAnEndedHandAddedNameBack()
    {
        var duo = new OwnLogDuo("Garg")
            .Feed(T, "Garg has left the group.")
            .Feed(T.AddSeconds(1), "Garg slashes a gnoll for 1000 points of damage.")
            .Feed(T.AddSeconds(2), "Garg has joined the group.")
            .Feed(T.AddSeconds(3), "Garg slashes a gnoll for 4 points of damage.");

        Assert.Contains("Garg", duo.Teammates.Roster(Primary, null));
        Assert.Equal(4, duo.Combined().DamageDealt);
    }

    /// <summary>The standalone feed (tests, tools) follows the same rule: a name passed
    /// in joins once, and passing it again after the log ended it does not re-join it.</summary>
    [Fact]
    public void TheStandaloneFeedsHandAddedNameJoinsOnceAndTheLogCanEndIt()
    {
        var derived = new DerivedTeammates();
        string[] names = ["Garg"];
        derived.Observe(T, "Garg slashes a gnoll for 10 points of damage.", Primary, null, names);
        derived.Observe(T.AddSeconds(1), "Garg has left the group.", Primary, null, names);
        derived.Observe(T.AddSeconds(2), "Garg slashes a gnoll for 1000 points of damage.", Primary, null, names);

        Assert.Equal(10, derived.Snapshots()["Garg"].DamageDealt);
        Assert.DoesNotContain("Garg", derived.Roster(Primary, null));
    }

    // ---------------------------------------------------------------------
    // Another day, another character, another server.
    // ---------------------------------------------------------------------

    /// <summary>The finding's own example: Garg added once, on an evening he was grouped.
    /// The next day the player logs in and plays solo while Garg fights nearby — a
    /// bystander, whose damage and kill must stay out of the totals.</summary>
    [Fact]
    public void AHandAddedNameFromAnEarlierDayDoesNotCountAfterTheNextLogin()
    {
        var nextDay = T.AddDays(1);
        var stats = new SessionStats();
        using var watcher = Watch(stats, "eqlog_Smargush_rivervale.txt",
        [
            L(T, "You slash a gnoll for 10 points of damage."),
            L(T.AddSeconds(1), "Garg slashes a gnoll for 5 points of damage."),
            L(nextDay, Login),
            L(nextDay.AddSeconds(10), "You slash a rat for 3 points of damage."),
            L(nextDay.AddSeconds(11), "Garg slashes a rat for 700 points of damage."),
            L(nextDay.AddSeconds(12), "A rat has been slain by Garg!"),
        ], Garg(T));

        var combined = stats.DuoSnapshot(null, null);
        Assert.Equal(3, combined.DamageDealt);
        Assert.Equal(0, combined.YourKillCount);
        Assert.DoesNotContain("Garg", watcher.Teammates.CountedNow());
    }

    [Theory]
    [InlineData("eqlog_Smargush_rivervale.txt", 15)]    // the character it was added for: counted
    [InlineData("eqlog_Altchar_rivervale.txt", 10)]     // another character: never
    [InlineData("eqlog_Smargush_bristlebane.txt", 10)]  // the same name on another server: never
    public void AHandAddedNameCountsForTheCharacterItWasAddedForOnly(string fileName, long damage)
    {
        var stats = new SessionStats();
        using var watcher = Watch(stats, fileName,
        [
            L(T, "You slash a gnoll for 10 points of damage."),
            L(T.AddSeconds(1), "Garg slashes a gnoll for 5 points of damage."),
        ], Garg(T));

        Assert.Equal(damage, stats.DuoSnapshot(null, null).DamageDealt);
    }

    /// <summary>A join from before the first line read is not placed at that line: the log
    /// from its moment is not being read, so whether the group still held is unknown.</summary>
    [Fact]
    public void AHandAddedJoinOlderThanTheLogBeingReadIsNotPlaced()
    {
        var stats = new SessionStats();
        using var watcher = Watch(stats, "eqlog_Smargush_rivervale.txt",
        [
            L(T, "You slash a gnoll for 10 points of damage."),
            L(T.AddSeconds(1), "Garg slashes a gnoll for 5 points of damage."),
        ], Garg(T.AddDays(-3)));

        Assert.Equal(10, stats.DuoSnapshot(null, null).DamageDealt);
    }

    // ---------------------------------------------------------------------
    // Adding by hand mid-session, and adding again.
    // ---------------------------------------------------------------------

    /// <summary>Added mid-session, the name counts from the session's start — until the
    /// leave line the log already holds — through the re-derivation the Options row runs.
    /// Added AGAIN, it counts from the moment it was re-added, not from the gap.</summary>
    [Fact]
    public void AddedMidSessionItCountsUntilTheLogEndedItAndAddedAgainFromThen()
    {
        var stats = new SessionStats();
        using var watcher = Watch(stats, "eqlog_Smargush_rivervale.txt",
        [
            L(T, "You slash a gnoll for 10 points of damage."),
            L(T.AddSeconds(1), "Garg slashes a gnoll for 5 points of damage."),
            L(T.AddSeconds(2), "Garg has left the group."),
            L(T.AddSeconds(3), "Garg slashes a gnoll for 1000 points of damage."),
        ]);
        Assert.Equal(10, stats.DuoSnapshot(null, null).DamageDealt);

        var first = watcher.Teammates.JoinTimeForHandAdded("Garg", T.AddSeconds(4));
        Assert.Equal(T, first);                                           // the session's start
        List<ManualTeammate> saved = [Garg(first)];
        watcher.Teammates.Manual = saved;
        watcher.RederiveTeammates();
        Assert.Equal(15, stats.DuoSnapshot(null, null).DamageDealt);
        Assert.DoesNotContain("Garg", watcher.Teammates.CountedNow());

        var again = watcher.Teammates.JoinTimeForHandAdded("Garg", T.AddSeconds(9).AddMilliseconds(400));
        Assert.Equal(T.AddSeconds(9), again);                             // from now, whole seconds
        saved.Add(Garg(again));
        watcher.Teammates.Manual = saved;
        watcher.RederiveTeammates();
        Assert.Equal(15, stats.DuoSnapshot(null, null).DamageDealt);     // the gap is not credited

        File.AppendAllLines(Path.Combine(_logs, "eqlog_Smargush_rivervale.txt"),
            [L(T.AddSeconds(10), "Garg slashes a gnoll for 7 points of damage.")]);
        watcher.FinishInitialIngest(watcher.SelectGeneration);
        Assert.Equal(22, stats.DuoSnapshot(null, null).DamageDealt);
        Assert.Contains("Garg", watcher.Teammates.CountedNow());
    }

    [Fact]
    public void JoinTimeIsTheSessionStartTheFirstTimeAndNowOtherwise()
    {
        var now = T.AddMinutes(30).AddMilliseconds(750);
        Assert.Equal(T, ManualTeammates.JoinTime(addedBefore: false, sessionStart: T, now));
        Assert.Equal(T.AddMinutes(30), ManualTeammates.JoinTime(addedBefore: true, sessionStart: T, now));
        Assert.Equal(T.AddMinutes(30), ManualTeammates.JoinTime(addedBefore: false, sessionStart: null, now));
    }

    [Fact]
    public void AnEntryBelongsToOneCharacterOnOneServer()
    {
        List<ManualTeammate> all =
        [
            Garg(T),
            new("Kellisanth", "Altchar", Server, T),
            new("Yungweezy", Primary, "bristlebane", T),
            null!,                                   // a hand-edited settings.json
            new(null!, Primary, Server, T),
        ];

        Assert.Equal(new[] { "Garg" }, ManualTeammates.NamesFor(all, "smargush", "Rivervale"));
        Assert.Equal(new[] { "Kellisanth" }, ManualTeammates.NamesFor(all, "Altchar", Server));
        Assert.Empty(ManualTeammates.NamesFor(all, null, null));

        Assert.Equal(1, ManualTeammates.Remove(all, "garg", Primary, Server));
        Assert.Empty(ManualTeammates.NamesFor(all, Primary, Server));
        Assert.Equal(new[] { "Yungweezy" }, ManualTeammates.NamesFor(all, Primary, "bristlebane"));
    }

    /// <summary>The old global list is left behind rather than guessed into a character: a
    /// profile that still carries it loads, with no hand-added teammate, and the new list
    /// survives a save.</summary>
    [Fact]
    public void TheOldGlobalListIsDroppedAndTheNewOneRoundTrips()
    {
        var old = JsonSerializer.Deserialize<AppSettings>("""{ "TeammateNames": ["Garg"] }""")!;
        Assert.Empty(old.ManualTeammates);

        // AppSettings' own options: unplaced window positions are NaN.
        var opts = new JsonSerializerOptions
        {
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };
        var settings = new AppSettings { ManualTeammates = [Garg(T)] };
        var back = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, opts), opts)!;
        Assert.Equal(Garg(T), Assert.Single(back.ManualTeammates));
    }
}
