using EQBuddy.Core;
using EQBuddy.UI.Shared;

namespace EQBuddy.Tests;

/// <summary>
/// 2026-09-17: the user reversed the earlier "Mobile stays primary-only" call — duo
/// totals now reach EQBuddy Mobile too, exactly like the desktop widget. That reopens
/// the trap this branch already found and fixed once (docs/Architecture.md's "The
/// version-plumbing trap"): <see cref="CompanionPumpGate.ShouldPush"/> and
/// <see cref="CompanionPumpGate.Observe"/> MUST be fed the SAME version number as each
/// other, or the low-latency pump and the 1 Hz reconciliation tick disagree forever
/// about what "already pushed" means, and the pump leaks a push every reconciliation
/// tick for as long as a teammate is assigned — silent, because nothing about it shows
/// in a diff, a build, or a screenshot; only a counted push total over many ticks can.
///
/// <see cref="MainWindow.xaml.cs"/> has no test project (docs/TestPlan.md §5), so these
/// tests pin the mechanism it now depends on: <see cref="SessionStats.DuoVersion"/> is
/// the single version number both call sites must share, and
/// <see cref="SessionStats.DuoSnapshot"/>'s own <c>Version</c> field always equals it.
/// </summary>
public class MobileDuoPumpVersionTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0);

    private static GameEvent Kill(DateTime t) => LogParser.Parse(
        $"[{t.ToString("ddd MMM d HH:mm:ss yyyy", System.Globalization.CultureInfo.InvariantCulture)}] You have slain orc pawn!")!;

    // ---- What the phone receives ----

    [Fact]
    public void PhoneReceivesTheWatchedCharactersOwnTotalsWhenNoTeammateIsConfigured()
    {
        var mine = new SessionStats();
        mine.Apply(Kill(T0));

        // BuildSnapshot() (MainWindow) is a direct call-through to DuoSnapshot with no
        // teammate assigned — this is the Core-level guarantee that claim rests on.
        var mobile = mine.DuoSnapshot(null, null);

        Assert.Null(mobile.Mate);
        Assert.Equal(1, mobile.YourKillCount);
        Assert.Equal(mine.CurrentVersion, mobile.Version);
    }

    [Fact]
    public void PhoneReceivesCombinedTotalsWhenATeammateIsConfigured()
    {
        var mine = new SessionStats();
        var mate = new SessionStats();
        mine.Companion = mate;
        mine.Apply(Kill(T0));
        mate.Apply(Kill(T0.AddSeconds(5)));

        var mobile = mine.DuoSnapshot(null, null);

        Assert.NotNull(mobile.Mate);
        Assert.Equal(2, mobile.YourKillCount);   // both sides, summed — the whole point of this reversal
    }

    // ---- The trap: gate and observed version must be the SAME number ----

    /// <summary>
    /// Reproduces MainWindow's two Mobile call sites directly: one simulated "PumpCompanion"
    /// gate check per tick, one simulated "RefreshUi" <c>Observe</c> per 20 ticks (the 50 ms
    /// pump vs. the 1 Hz reconciliation tick) — against a session that is completely frozen
    /// except for a teammate's own, already-applied, non-zero contribution. If the two paths
    /// are fed different version numbers, this must show a permanent per-reconciliation-tick
    /// leak; fed the SAME number (<see cref="SessionStats.DuoVersion"/>), it must show zero.
    /// </summary>
    private static long CountPushesOverManyTicks(SessionStats mine, bool gateUsesDuoVersion)
    {
        var gate = new CompanionPumpGate();
        long pushes = 0;
        for (var second = 0; second < 50; second++)
        {
            // RefreshUi: always observes the version of the snapshot it just pushed —
            // the combined one, since BuildSnapshot() now defaults to DuoSnapshot.
            gate.Observe(mine.DuoSnapshot(null, null).Version);
            for (var tick = 0; tick < 20; tick++)
            {
                var gateVersion = gateUsesDuoVersion ? mine.DuoVersion : mine.CurrentVersion;
                if (gate.ShouldPush(hasClients: true, gateVersion)) pushes++;
            }
        }
        return pushes;
    }

    [Fact]
    public void TheTrapIsReal_FeedingTheGateCurrentVersionInsteadOfDuoVersionLeaksAPushForever()
    {
        // Proves the failure mode exists at all, so the next assertion's "0" is not
        // vacuously true of a scenario that could never have leaked in the first place.
        var mine = new SessionStats();
        var mate = new SessionStats();
        mine.Companion = mate;
        mine.Apply(Kill(T0));            // mine.CurrentVersion moves once, then freezes
        mate.Apply(Kill(T0.AddSeconds(5))); // mate.CurrentVersion moves once, then freezes

        var leakedPushes = CountPushesOverManyTicks(mine, gateUsesDuoVersion: false);

        Assert.True(leakedPushes > 0,
            $"expected the CurrentVersion/DuoVersion mismatch to leak a push per " +
            $"reconciliation tick, got {leakedPushes} — the reproduction itself is broken");
    }

    [Fact]
    public void ThePumpDoesNotPushRepeatedlyWhenNothingHasChangedButATeammateIsActive()
    {
        var mine = new SessionStats();
        var mate = new SessionStats();
        mine.Companion = mate;
        mine.Apply(Kill(T0));
        mate.Apply(Kill(T0.AddSeconds(5)));

        var pushes = CountPushesOverManyTicks(mine, gateUsesDuoVersion: true);

        Assert.Equal(0, pushes);
    }

    // ---- The four rollover cases: gate value and observed value must always agree ----

    [Fact]
    public void GateAndObservedVersionAgree_NoTeammateConfigured()
    {
        var mine = new SessionStats();
        mine.Apply(Kill(T0));

        Assert.Equal(mine.DuoVersion, mine.DuoSnapshot(null, null).Version);
    }

    [Fact]
    public void GateAndObservedVersionAgree_TeammateActive()
    {
        var mine = new SessionStats();
        var mate = new SessionStats();
        mine.Companion = mate;
        mine.Apply(Kill(T0));
        mate.Apply(Kill(T0.AddSeconds(5)));

        Assert.Equal(mine.DuoVersion, mine.DuoSnapshot(null, null).Version);
    }

    [Fact]
    public void GateAndObservedVersionAgree_ImmediatelyAfterAPrimarySessionRollover()
    {
        var mine = new SessionStats();
        var mate = new SessionStats();
        mine.Companion = mate;
        mine.Apply(Kill(T0));
        mate.Apply(Kill(T0.AddSeconds(5)));

        // > SessionStats.SessionGap (60 min) on MINE's own log rolls the primary session.
        mine.Apply(Kill(T0.AddMinutes(65)));

        Assert.Equal(mine.DuoVersion, mine.DuoSnapshot(null, null).Version);
    }

    [Fact]
    public void GateAndObservedVersionAgree_ImmediatelyAfterATeammateOnlyRollover()
    {
        var mine = new SessionStats();
        var mate = new SessionStats();
        mine.Companion = mate;
        mate.Apply(Kill(T0));
        mine.Apply(Kill(T0.AddSeconds(5)));
        // Mine stays under 60 min throughout; only mate's own gap rolls (mirrors
        // DuoStatsTests.ACompanionOnlyGapRolloverKeepsItsPreRollContributionInTheDuoTotal).
        mine.Apply(Kill(T0.AddMinutes(30)));
        mate.Apply(Kill(T0.AddMinutes(65)));   // > 60 min since mate's last event only

        Assert.Equal(mine.DuoVersion, mine.DuoSnapshot(null, null).Version);
    }
}
