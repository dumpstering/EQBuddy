using System.Reflection;
using System.Text.RegularExpressions;
using EQBuddy.Companion;
using EQBuddy.Core;
using EQBuddy.UI.Shared;

namespace EQBuddy.Tests;

/// <summary>
/// End to end, the way the player runs it: ONE log file — theirs — in a Logs folder, the
/// REAL <see cref="LogWatcher"/> replaying it and then tailing appended lines, and the two
/// snapshot paths <c>MainWindow</c> builds from it: the desktop's
/// (<c>BuildSnapshot()</c> → <see cref="SessionStats.DuoSnapshot"/> with the settings'
/// recent window and tracked rules) and the phone's (the 50 ms pump's
/// <see cref="CompanionPumpGate"/> on <see cref="SessionStats.DuoVersion"/>, then
/// <see cref="CompanionProjection.Build(CompanionInputs, DateTime)"/> over that same
/// snapshot). No teammate file is configured, or even possible: the only thing that
/// makes Garg a teammate is the join line in the player's own log.
///
/// The expected Garg numbers are counted straight off the excerpt's text with a regex of
/// this test's own — not by the rewrite code under test.
/// </summary>
public partial class OwnLogTeammatesEndToEndTests : IDisposable
{
    private readonly string _logs = Directory.CreateTempSubdirectory("eqbuddy-ownlog-").FullName;
    private readonly string _path;
    private readonly string[] _lines = RealLogExcerpt.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(l => l.TrimEnd('\r')).ToArray();

    public OwnLogTeammatesEndToEndTests() => _path = Path.Combine(_logs, "eqlog_Smargush_rivervale.txt");

    public void Dispose()
    {
        try { Directory.Delete(_logs, recursive: true); } catch (IOException) { }
    }

    // What MainWindow.BuildSnapshot passes, from a default profile.
    private static readonly AppSettings Settings = new();
    private static TimeSpan RecentWindow => TimeSpan.FromMinutes(Math.Max(1, Settings.RecentWindowMinutes));
    private static StatsSnapshot BuildSnapshot(SessionStats stats) => stats.DuoSnapshot(RecentWindow, Settings.TrackedRules);

    private static readonly Regex GargDamage = new(
        @"^Garg (?!tries )(?:hit|\w+(?: on)?) .+ for (?<n>\d+) points? of (?:\w+ )?damage", RegexOptions.CultureInvariant);

    private static (long Damage, int Kills) CountGarg(IEnumerable<string> lines)
    {
        long damage = 0;
        var kills = 0;
        foreach (var line in lines)
        {
            if (!LogParser.TrySplitLine(line, out _, out var msg)) continue;
            if (GargDamage.Match(msg) is { Success: true } m) damage += long.Parse(m.Groups["n"].Value);
            if (msg.EndsWith(" has been slain by Garg!", StringComparison.Ordinal)) kills++;
        }
        return (damage, kills);
    }

    private LogWatcher StartWatching(SessionStats stats, int initialLines)
    {
        File.WriteAllLines(_path, _lines.Take(initialLines));
        var watcher = new LogWatcher(stats) { DeferIngestForTests = true };
        watcher.Teammates.Manual = Settings.ManualTeammates;   // MainWindow's load line: empty by default
        watcher.Select(_path);
        watcher.FinishInitialIngest(watcher.SelectGeneration);
        return watcher;
    }

    [Fact]
    public void GargsDamageAndKillsReachTheDesktopAndThePhoneFromTheWatchedLogAlone()
    {
        Assert.Empty(Settings.ManualTeammates);
        Assert.DoesNotContain(typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance),
            p => p.Name.Contains("TeammateLog", StringComparison.OrdinalIgnoreCase));

        var stats = new SessionStats();
        // Replay up to and including Garg's first kill; the rest arrives as a live tail.
        var initial = Array.FindIndex(_lines, l => l.EndsWith(" has been slain by Garg!", StringComparison.Ordinal)) + 1;
        using var watcher = StartWatching(stats, initial);
        Assert.Null(watcher.LastError);

        // ---- The roster came from the join line, nothing else ----
        Assert.Equal(["Garg"], watcher.Teammates.AutoDetected);
        Assert.Equal(["Garg"], watcher.Teammates.KnownTeammates);

        var (gargDamageSoFar, gargKillsSoFar) = CountGarg(_lines.Take(initial));
        Assert.True(gargDamageSoFar > 0 && gargKillsSoFar > 0, "the excerpt's first half must already hold Garg's fighting");
        AssertDesktopCombines(stats, gargDamageSoFar, gargKillsSoFar);

        // ---- Live tail: the rest of the excerpt arrives ----
        File.AppendAllLines(_path, _lines.Skip(initial));
        watcher.FinishInitialIngest(watcher.SelectGeneration);   // one more poll, synchronously
        Assert.Null(watcher.LastError);
        var (gargDamage, gargKills) = CountGarg(_lines);
        Assert.Equal(3, gargKills);
        var desktop = AssertDesktopCombines(stats, gargDamage, gargKills);

        // ---- The phone: the pump's gate reads DuoVersion, the pushed snapshot carries it ----
        var gate = new CompanionPumpGate();
        Assert.True(gate.ShouldPush(hasClients: true, stats.DuoVersion));
        var pushed = BuildSnapshot(stats);
        Assert.Equal(stats.DuoVersion, pushed.Version);
        var phone = CompanionProjection.Build(new CompanionInputs
        {
            Stats = pushed,
            Timers = [],
            Character = "Smargush",
            AppVersion = "test",
            Offered = [CompanionSurfaces.Session, CompanionSurfaces.Combat],
        }, DateTime.Now);

        Assert.Equal(desktop.YourKillCount, phone.Session!.Kills);
        Assert.Equal(desktop.SessionDps, phone.Session.SessionDps, 6);
        var phoneDamage = phone.Combat!.Boards.Single(b => b.Key == "damage");
        Assert.Equal(desktop.DamageBySource.Sum(d => d.Total), phoneDamage.Session.Sum(r => r.Total));
        // Garg's rows arrive on the phone under a readable "(Garg)" label, never the
        // internal actor marker.
        Assert.Contains(phoneDamage.Session, r => r.Name.EndsWith(" (Garg)", StringComparison.Ordinal));
        Assert.DoesNotContain(phoneDamage.Session, r => r.Name.Contains(''));

        // Nothing changed since the push: the pump stays quiet (the DuoVersion trap).
        gate.Observe(pushed.Version);
        for (var tick = 0; tick < 40; tick++)
            Assert.False(gate.ShouldPush(hasClients: true, stats.DuoVersion));
    }

    private static StatsSnapshot AssertDesktopCombines(SessionStats stats, long gargDamage, int gargKills)
    {
        var solo = stats.Snapshot(RecentWindow, Settings.TrackedRules);
        var desktop = BuildSnapshot(stats);

        // Garg's numbers are ADDED to the player's own…
        Assert.Equal(solo.DamageDealt + gargDamage, desktop.DamageDealt);
        Assert.Equal(solo.YourKillCount + gargKills, desktop.YourKillCount);
        // …and his kills move out of the party-kill rows rather than showing twice. The
        // bystander's kill stays a party kill; the killer rows still sum to the header.
        Assert.Equal(solo.PartyKillCount - gargKills, desktop.PartyKillCount);
        Assert.DoesNotContain(desktop.PartyKillsByKiller, k => k.Name == "Garg");
        Assert.Contains(desktop.PartyKillsByKiller, k => k.Name == "Jthomn");
        Assert.Equal(desktop.PartyKillCount, desktop.PartyKillsByKiller.Sum(k => k.Count));
        // The bystander's swing is nobody's: not the player's, not Garg's.
        Assert.DoesNotContain(desktop.DamageBySource, d => d.Name.Contains("Jthomn", StringComparison.Ordinal));
        // What the player's log never says about Garg stays the player's own.
        Assert.Equal(solo.LootTotal, desktop.LootTotal);
        Assert.Equal(solo.Copper, desktop.Copper);
        Assert.Equal(solo.XpPercent, desktop.XpPercent);
        // The recent window and the rules survived the combine (the #202 trap).
        Assert.NotNull(desktop.Recent);
        Assert.Equal(solo.Tracked.Count, desktop.Tracked.Count);
        // One version for the desktop and the phone.
        Assert.Equal(stats.DuoVersion, desktop.Version);
        return desktop;
    }

    [Fact]
    public void ASecondReplayOfTheSameLogDerivesTheSameTeammatesNotDoubleThem()
    {
        var stats = new SessionStats();
        using var watcher = StartWatching(stats, _lines.Length);
        var first = BuildSnapshot(stats);

        watcher.Select(_path);                                   // e.g. following the same character again
        watcher.FinishInitialIngest(watcher.SelectGeneration);
        var second = BuildSnapshot(stats);

        Assert.Equal(first.DamageDealt, second.DamageDealt);
        Assert.Equal(first.YourKillCount, second.YourKillCount);
        Assert.Equal(first.PartyKillCount, second.PartyKillCount);
    }

    [Fact]
    public void SwitchingToAnotherCharactersLogDropsTheTeammates()
    {
        var stats = new SessionStats();
        using var watcher = StartWatching(stats, _lines.Length);
        Assert.NotEmpty(watcher.Teammates.KnownTeammates);

        var other = Path.Combine(_logs, "eqlog_Alt_rivervale.txt");
        File.WriteAllLines(other, ["[Sat Sep 26 14:00:00 2026] You slash a rat for 5 points of damage."]);
        watcher.Select(other);
        watcher.FinishInitialIngest(watcher.SelectGeneration);

        Assert.Empty(watcher.Teammates.KnownTeammates);
        Assert.Empty(watcher.Teammates.AutoDetected);
        Assert.Equal(5, BuildSnapshot(stats).DamageDealt);
    }

    /// <summary>A teammate the log never announced (grouped before the log began) is
    /// added by hand in Options; the watcher re-derives the CURRENT session from the
    /// lines it already read, so the name counts from the session's start — and the
    /// next live poll does not feed those lines a second time.</summary>
    [Fact]
    public void AManualNameAddedMidSessionCountsFromTheSessionStartExactlyOnce()
    {
        var unannounced = _lines.Where(l => !l.Contains(" group.", StringComparison.Ordinal)).ToArray();
        var half = unannounced.Length / 2;
        File.WriteAllLines(_path, unannounced.Take(half));
        var stats = new SessionStats();
        using var watcher = new LogWatcher(stats) { DeferIngestForTests = true };
        watcher.Select(_path);
        watcher.FinishInitialIngest(watcher.SelectGeneration);
        Assert.Empty(watcher.Teammates.KnownTeammates);           // nobody announced, nobody counted

        // The rest of the log is already on disk but not yet polled when the name is
        // added: the re-derivation must stop at the bytes the watcher has read, or the
        // next poll would feed those lines to Garg a second time.
        File.AppendAllLines(_path, unannounced.Skip(half));
        // What the Options row stores: typed in lower case, joining at the session start.
        var since = watcher.Teammates.JoinTimeForHandAdded("garg", DateTime.Now);
        Assert.Equal(stats.Snapshot().SessionStart, since);
        watcher.Teammates.Manual = [new ManualTeammate("garg", "Smargush", "rivervale", since)];
        watcher.RederiveTeammatesAsync().Wait();
        var (sofar, _) = CountGarg(unannounced.Take(half));
        Assert.Equal(stats.Snapshot().DamageDealt + sofar, BuildSnapshot(stats).DamageDealt);

        watcher.FinishInitialIngest(watcher.SelectGeneration);
        var (all, kills) = CountGarg(unannounced);
        var desktop = BuildSnapshot(stats);
        Assert.Equal(stats.Snapshot().DamageDealt + all, desktop.DamageDealt);
        Assert.Equal(stats.Snapshot().YourKillCount + kills, desktop.YourKillCount);
    }
}
