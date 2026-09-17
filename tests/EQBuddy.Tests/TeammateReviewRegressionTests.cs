using EQBuddy.Core;
using EQBuddy.UI.Shared;

namespace EQBuddy.Tests;

public sealed class TeammateReviewRegressionTests
{
    private static readonly DateTime Start = new(2026, 7, 18, 15, 0, 0);

    [Fact]
    public void EqualClocksMatchEachRepeatedKillOnce()
    {
        using var f = new Logs(
            [Line(0, "You have slain sentry!"), Line(5, "an orc pawn has been slain by Buddy!"), Line(305, "an orc pawn has been slain by Buddy!")],
            [Line(5, "You have slain an orc pawn!"), Line(305, "You have slain an orc pawn!")]);
        f.Ingest();
        Assert.Equal(TimeSpan.Zero, f.Stats.EstimatedTeammateClockOffset);
        Assert.Equal(2, f.Stats.TeammateClockOffsetSampleCount);
        Assert.NotNull(f.Stats.DuoSnapshot(null, null).Mate);
        Assert.Equal(0, f.Stats.DuoSnapshot(null, null).PartyKillCount);
    }

    [Theory]
    [InlineData(-600)]
    [InlineData(600)]
    public void ExcessClockOffsetIsFoundInEitherDirection(int offset)
    {
        using var f = new Logs(
            [Line(0, "You have slain sentry!"), Line(5, "an orc pawn has been slain by Buddy!")],
            [Line(5 - offset, "You have slain an orc pawn!")]);
        f.Ingest();
        Assert.Equal(TimeSpan.FromSeconds(offset), f.Stats.EstimatedTeammateClockOffset);
        Assert.Null(f.Stats.DuoSnapshot(null, null).Mate);
        Assert.Contains("paused", TeammateStatusPresentation.Warning(f.Stats.EstimatedTeammateClockOffset));
    }

    [Fact]
    public void TeammateKillArrivingInALaterPollStillMatches()
    {
        using var f = new Logs([Line(0, "You have slain sentry!"), Line(5, "orc has been slain by Buddy!")], []);
        f.Ingest();
        Assert.Equal(0, f.Stats.TeammateClockOffsetSampleCount);
        File.AppendAllLines(f.Mate, [Line(5, "You have slain orc!")]);
        f.Watcher.PollForTests();
        Assert.Equal(TimeSpan.Zero, f.Stats.EstimatedTeammateClockOffset);
        Assert.Equal(1, f.Stats.TeammateClockOffsetSampleCount);
        f.Watcher.PollForTests();
        Assert.Equal(1, f.Stats.TeammateClockOffsetSampleCount);
    }

    [Fact]
    public void ReselectingPrimaryDoesNotAccumulateOldCarry()
    {
        using var f = RolloverLogs();
        f.Ingest();
        Assert.Equal(5, f.Stats.DuoSnapshot(null, null).YourKillCount);
        f.Watcher.Select(f.Own);
        f.Ingest();
        Assert.Equal(5, f.Stats.DuoSnapshot(null, null).YourKillCount);
        f.Watcher.Select(f.Own);
        f.Ingest();
        Assert.Equal(5, f.Stats.DuoSnapshot(null, null).YourKillCount);
    }

    [Fact]
    public void SwitchingCharactersClearsBothLiveAndCarriedTeammateTotals()
    {
        using var f = RolloverLogs();
        f.Ingest();
        var next = Path.Combine(f.Root, "eqlog_Next_freeport.txt");
        File.WriteAllLines(next, [Line(7200, "You have slain guard!")]);
        f.Watcher.Select(next);
        f.Ingest();
        Assert.Equal(1, f.Stats.DuoSnapshot(null, null).YourKillCount);
    }

    [Fact]
    public void LiveActivityAndVersionSurviveCarryFolding()
    {
        var now = DateTime.Now;
        var mine = new SessionStats();
        var mate = new SessionStats();
        mine.Companion = mate;
        Hit(mate, now.AddMinutes(-65));
        Hit(mine, now.AddMinutes(-60));
        Hit(mine, now.AddMinutes(-30));
        Hit(mate, now.AddSeconds(-5));
        Hit(mate, now);
        Hit(mine, now);
        var live = mate.Snapshot(TimeSpan.FromMinutes(30), null);
        var combined = mine.DuoSnapshot(TimeSpan.FromMinutes(30), null);
        Assert.True(live.CurrentDps > 0);
        Assert.True(live.Recent!.Dps > 0);
        Assert.Equal(live.CurrentDps, combined.Mate!.CurrentDps);
        Assert.Equal(live.Recent, combined.Mate.Recent);
        Assert.Equal(live.Effort, combined.Mate.Effort);
        Assert.Equal(mine.DuoVersion, combined.Version);
    }

    [Fact]
    public void CarriedCombatSpansStillUnionWithPrimaryHistory()
    {
        var mine = new SessionStats();
        var mate = new SessionStats();
        mine.Companion = mate;
        Hit(mine, Start); Hit(mate, Start);
        Hit(mine, Start.AddSeconds(10)); Hit(mate, Start.AddSeconds(10));
        mine.Apply(new LootEvent(Start.AddMinutes(30), "item", "orc", null));
        mine.Apply(new LootEvent(Start.AddMinutes(60), "item", "orc", null));
        Hit(mate, Start.AddMinutes(65)); Hit(mate, Start.AddMinutes(65).AddSeconds(10));
        var combined = mine.DuoSnapshot(null, null);
        Assert.Equal(600, combined.DamageDealt);
        Assert.Equal(20, combined.CombatSeconds);
        Assert.Equal(30, combined.SessionDps);
    }

    [Fact]
    public void ClearingCarryInvalidatesAnAlreadyMemoizedPair()
    {
        var mine = new SessionStats();
        var mate = new SessionStats();
        mine.Companion = mate;
        mate.Apply(new KillEvent(Start, "rat", "You"));
        mate.Apply(new KillEvent(Start.AddMinutes(65), "rat", "You"));
        Assert.Equal(2, mine.DuoSnapshot(null, null).YourKillCount);
        mine.ClearCompanionCarry();
        Assert.Equal(1, mine.DuoSnapshot(null, null).YourKillCount);
    }

    [Fact]
    public async Task ConcurrentPrimarySelectionCannotInstallASelfFeed()
    {
        using var f = new Logs([Line(0, "You have slain rat!")], [Line(0, "You have slain rat!")], selectMate: false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var calls = 0;
        f.Watcher.LogFolder = () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.SetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Validation was not released.");
            }
            return null;
        };
        var selection = Task.Run(() => f.Watcher.SelectTeammate(f.Mate));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            f.Watcher.Select(f.Mate);
        }
        finally { release.Set(); }
        await selection.WaitAsync(TimeSpan.FromSeconds(10));
        f.Ingest();
        Assert.Null(f.Watcher.TeammatePath);
        Assert.NotNull(f.Watcher.LastError);
        Assert.Equal(1, f.Stats.DuoSnapshot(null, null).YourKillCount);
    }

    [Fact]
    public void ActorLabelsAreReadableAndLiteralParenthesesArePreserved()
    {
        var tagged = DuoStats.TagWithActor("Kick", "Buddy");
        var s = new StatsSnapshot { SpecialHits = [new NameCount(tagged, 2)] };
        var text = string.Join("\n", CombatPresentation.SummaryLines(s));
        Assert.Contains("Kick (Buddy) 2", text);
        Assert.DoesNotContain('\uE000', text);
        Assert.Equal("Spell (Rank II)", DuoStats.DisplayActorTag("Spell (Rank II)"));
        Assert.Equal(("Kick", "Buddy"), DuoStats.SplitActorTag(tagged));
    }

    [Fact]
    public void UnknownAndAcceptableClockOffsetsDoNotShowARefusal()
    {
        Assert.Null(TeammateStatusPresentation.Warning(null));
        Assert.Null(TeammateStatusPresentation.Warning(TimeSpan.FromMinutes(2)));
        Assert.Contains("not determined", TeammateStatusPresentation.ClockStatus(null));
    }

    private static Logs RolloverLogs() => new(
        [Line(0, "You have slain orc!"), Line(1800, "You have slain orc!"), Line(3600, "You have slain orc!")],
        [Line(1, "You have slain rat!"), Line(3900, "You have slain rat!")]);

    private static string Line(int seconds, string message) =>
        $"[{Start.AddSeconds(seconds).ToString("ddd MMM dd HH:mm:ss yyyy", System.Globalization.CultureInfo.InvariantCulture)}] {message}";

    private static void Hit(SessionStats stats, DateTime time) =>
        stats.Apply(new DamageDealtEvent(time, "orc", 100, DamageKind.Melee, "slash", false));

    private sealed class Logs : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("eqbuddy-review-").FullName;
        public string Own { get; }
        public string Mate { get; }
        public SessionStats Stats { get; } = new();
        public LogWatcher Watcher { get; }
        public Logs(string[] own, string[] mate, bool selectMate = true)
        {
            Own = Path.Combine(Root, "eqlog_Main_freeport.txt");
            Mate = Path.Combine(Root, "eqlog_Buddy_freeport.txt");
            File.WriteAllLines(Own, own); File.WriteAllLines(Mate, mate);
            Watcher = new LogWatcher(Stats) { DeferIngestForTests = true };
            if (selectMate) Watcher.SelectTeammate(Mate);
            Watcher.Select(Own);
        }
        public void Ingest() => Watcher.FinishInitialIngest(Watcher.SelectGeneration);
        public void Dispose() { Watcher.Dispose(); Directory.Delete(Root, recursive: true); }
    }
}
