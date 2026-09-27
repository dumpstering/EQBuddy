using EQBuddy.Core;

namespace EQBuddy.Tests;

/// <summary>
/// Re-deriving the current session's teammates after the manual names change
/// (<see cref="LogWatcher.RederiveTeammates"/>), and how a teammate's pet reaches their
/// counters. Each test here failed before the fix it names.
/// </summary>
public sealed class TeammateRederiveTests : IDisposable
{
    private static readonly DateTime T = new(2026, 9, 26, 13, 0, 0);
    private readonly string _logs = Directory.CreateTempSubdirectory("eqbuddy-rederive-").FullName;
    private readonly string _path;

    public TeammateRederiveTests() => _path = Path.Combine(_logs, "eqlog_Smargush_rivervale.txt");

    public void Dispose()
    {
        try { Directory.Delete(_logs, recursive: true); } catch (IOException) { }
    }

    private static string L(int seconds, string msg) => $"[{T.AddSeconds(seconds):ddd MMM dd HH:mm:ss yyyy}] {msg}";

    private LogWatcher Watch(SessionStats stats, IEnumerable<string> lines, long startOffset = 0)
    {
        File.WriteAllLines(_path, lines);
        var watcher = new LogWatcher(stats) { DeferIngestForTests = true };
        watcher.Select(_path, startOffset, long.MaxValue);
        watcher.FinishInitialIngest(watcher.SelectGeneration);
        return watcher;
    }

    // Garg fights next to the player for a while as a bystander, kills a gnoll, and only
    // then joins the group.
    private static readonly string[] JoinsLate =
    [
        L(0, "You slash a gnoll for 10 points of damage."),
        L(1, "Garg slashes a gnoll for 500 points of damage."),
        L(2, "A gnoll has been slain by Garg!"),
        L(60, "Garg has joined the group."),
        L(61, "Garg slashes a gnoll for 7 points of damage."),
    ];

    /// <summary>Finding: the replay used the END-of-session roster, so a member who
    /// joined late was credited from the session's first line — their bystander damage
    /// and kill included — the moment any manual name was added or removed.</summary>
    [Fact]
    public void ARederiveDoesNotCreditAMemberWithWhatTheyDidBeforeJoining()
    {
        var stats = new SessionStats();
        using var watcher = Watch(stats, JoinsLate);
        var live = stats.DuoSnapshot(null, null);
        Assert.Equal(17, live.DamageDealt);
        Assert.Equal(0, live.YourKillCount);
        Assert.Equal(1, live.PartyKillCount);

        watcher.Teammates.Manual = [new ManualTeammate("Yungweezy", "Smargush", "rivervale", T)];   // any edit in Options re-derives
        watcher.RederiveTeammates();

        var after = stats.DuoSnapshot(null, null);
        Assert.Equal(17, after.DamageDealt);
        Assert.Equal(0, after.YourKillCount);
        Assert.Equal(1, after.PartyKillCount);
        Assert.Contains("Garg", watcher.Teammates.AutoDetected);
    }

    /// <summary>A member who left and came back is credited for both stints and not the
    /// gap between them — the replay rebuilds membership line by line.</summary>
    [Fact]
    public void ARederiveFollowsLeavesAndRejoinsInLogOrder()
    {
        var stats = new SessionStats();
        using var watcher = Watch(stats,
        [
            L(0, "Garg has joined the group."),
            L(1, "Garg slashes a gnoll for 3 points of damage."),
            L(2, "Garg has left the group."),
            L(3, "Garg slashes a gnoll for 1000 points of damage."),
            L(4, "Garg has joined the group."),
            L(5, "Garg slashes a gnoll for 4 points of damage."),
        ]);
        Assert.Equal(7, stats.DuoSnapshot(null, null).DamageDealt);

        watcher.Teammates.Manual = [new ManualTeammate("Yungweezy", "Smargush", "rivervale", T)];
        watcher.RederiveTeammates();

        Assert.Equal(7, stats.DuoSnapshot(null, null).DamageDealt);
    }

    /// <summary>Review mode replays one session out of a file: a join line before the
    /// selected range was never read by the poll, and the replay must not read it either.</summary>
    [Fact]
    public void ARederiveInReviewModeStartsWhereTheSelectStarted()
    {
        string[] earlier = [L(-7200, "Garg has joined the group."), L(-7199, "You slash a rat for 1 point of damage.")];
        string[] reviewed = [L(0, "You slash a gnoll for 10 points of damage."), L(1, "Garg slashes a gnoll for 500 points of damage.")];
        var startOffset = earlier.Sum(l => l.Length + Environment.NewLine.Length);
        var stats = new SessionStats();
        using var watcher = Watch(stats, [.. earlier, .. reviewed], startOffset);
        Assert.Equal(10, stats.DuoSnapshot(null, null).DamageDealt);

        watcher.Teammates.Manual = [new ManualTeammate("Yungweezy", "Smargush", "rivervale", T)];
        watcher.RederiveTeammates();

        Assert.Equal(10, stats.DuoSnapshot(null, null).DamageDealt);
        Assert.Empty(watcher.Teammates.KnownTeammates);
    }

    /// <summary>Finding: the teammates were erased before the file was opened, so a log
    /// that could not be read cost the whole session's teammate totals.</summary>
    [Fact]
    public void ALogThatCannotBeReadLeavesTheTeammatesAsTheyWere()
    {
        var stats = new SessionStats();
        using var watcher = Watch(stats, JoinsLate);
        var before = stats.DuoSnapshot(null, null);
        Assert.Equal(17, before.DamageDealt);

        File.Delete(_path);
        watcher.Teammates.Manual = [new ManualTeammate("Yungweezy", "Smargush", "rivervale", T)];
        watcher.RederiveTeammates();

        Assert.Equal(before.DamageDealt, stats.DuoSnapshot(null, null).DamageDealt);
        Assert.Contains("Garg", watcher.Teammates.KnownTeammates);
    }

    /// <summary>Finding: a replay published every line as it went. It now builds the
    /// session in a staging instance: nothing reaches the live teammates until the
    /// commit, and a session that ended meanwhile refuses the commit.</summary>
    [Fact]
    public void AReplayIsInvisibleUntilCommittedAndDiscardedIfTheSessionEnded()
    {
        var duo = new OwnLogDuo()
            .Feed(T, "Garg has joined the group.")
            .Feed(T.AddSeconds(1), "Garg slashes a gnoll for 5 points of damage.");
        var versionBefore = duo.Teammates.Version;

        var (staging, generation) = duo.Teammates.BeginReplay();
        staging.ObserveRosterLine(T, "Garg has joined the group.");
        staging.ObservePrimaryLine(T.AddSeconds(1), "Garg slashes a gnoll for 9 points of damage.", null);
        Assert.Equal(5, duo.Combined().DamageDealt);
        Assert.Equal(versionBefore, duo.Teammates.Version);

        Assert.True(duo.Teammates.CommitReplay(staging, generation));
        Assert.Equal(9, duo.Combined().DamageDealt);
        Assert.Equal(versionBefore + 1, duo.Teammates.Version);

        var (late, lateGeneration) = duo.Teammates.BeginReplay();
        duo.Teammates.ResetSession();                            // "Reset session" meanwhile
        late.ObserveRosterLine(T, "Garg has joined the group.");
        late.ObservePrimaryLine(T.AddSeconds(1), "Garg slashes a gnoll for 1000 points of damage.", null);
        Assert.False(duo.Teammates.CommitReplay(late, lateGeneration));
        Assert.Empty(duo.Teammates.KnownTeammates);
    }

    /// <summary>Finding: a teammate's warder went through the ordinary player path, so
    /// its hits, crits and misses landed in the owner's accuracy counters — which the
    /// combine sums — unlike the primary's own pet. Its damage still counts.</summary>
    [Fact]
    public void ATeammatesWarderAddsDamageButNotToTheOwnersAccuracyCounters()
    {
        var duo = new OwnLogDuo()
            .Feed(T, "Sinista has joined the group.")
            .Feed(T.AddSeconds(1), "Sinista slashes a gnoll for 10 points of damage.")
            .Feed(T.AddSeconds(2), "Sinista`s warder claws a gnoll for 20 points of damage. (Critical)")
            .Feed(T.AddSeconds(3), "Sinista`s warder tries to claw a gnoll, but misses!")
            .Feed(T.AddSeconds(4), "Sinista`s warder bites a gnoll for 30 points of damage.");

        var mate = duo.Teammates.Snapshots()["Sinista"];
        Assert.Equal(60, mate.DamageDealt);
        Assert.Equal(1, mate.HitCount);
        Assert.Equal(0, mate.CritCount);
        Assert.Equal(0, mate.MissCount);

        var combined = duo.Combined();
        Assert.Equal(60, combined.DamageDealt);
        Assert.Equal(1, combined.HitCount);
        Assert.Equal(0, combined.CritCount);
        Assert.Equal(0, combined.MissCount);
    }

    /// <summary>The keep-alive tick is skipped for a line stamped the same second as the
    /// teammate's last event; the teammate's session must still end exactly with the
    /// primary's, never on its own.</summary>
    [Fact]
    public void AQuietTeammateStillRollsOnlyWithThePrimary()
    {
        var duo = new OwnLogDuo()
            .Feed(T, "Garg has joined the group.")
            .Feed(T.AddSeconds(1), "Garg slashes a gnoll for 5 points of damage.");
        // The primary plays on alone for two hours, a line every 30 minutes.
        for (var m = 30; m <= 120; m += 30)
        {
            duo.Feed(T.AddMinutes(m), "You slash a gnoll for 1 point of damage.");
            duo.Feed(T.AddMinutes(m), "You slash a gnoll for 1 point of damage.");
        }
        duo.Feed(T.AddMinutes(121), "Garg slashes a gnoll for 2 points of damage.");

        Assert.Equal(7, duo.Teammates.Snapshots()["Garg"].DamageDealt);
        Assert.Equal(8 + 7, duo.Combined().DamageDealt);
    }
}
