using EQBuddy.Core;

namespace EQBuddy.Tests;

/// <summary>
/// Repair round C8: unit tests for the pure estimator behind the clock-drift guard —
/// see <see cref="ClockDriftEstimator"/>'s own doc for why it exists and what it
/// deliberately does not do (no timestamp normalisation). The end-to-end pipeline
/// (LogWatcher observing a bystander-visible kill, joining it against the teammate's
/// own self-reported timestamp, and DuoCompanion.DuoSnapshot refusing to combine) is
/// covered separately in LogWatcherTests.cs, against real files through the real
/// dispatch path.
/// </summary>
public class ClockDriftEstimatorTests
{
    [Fact]
    public void WithNoSamplesTheEstimateCannotBeDetermined()
    {
        var e = new ClockDriftEstimator();

        Assert.Null(e.Estimate);
        Assert.Equal(0, e.SampleCount);
        // "Cannot be determined" must never read as "exceeds the threshold" — a
        // fresh pairing with no data yet must not refuse to combine before it has
        // ever had the chance to measure anything.
        Assert.False(e.ExceedsThreshold);
    }

    [Fact]
    public void ASingleSampleIsTheEstimateVerbatim()
    {
        var e = new ClockDriftEstimator();
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0);

        e.RecordSample(primaryTimestamp: t0.AddSeconds(30), teammateTimestamp: t0);

        Assert.Equal(TimeSpan.FromSeconds(30), e.Estimate);
        Assert.Equal(1, e.SampleCount);
    }

    [Fact]
    public void NegativeOffsetsAreCarriedThroughVerbatim()
    {
        // The teammate's clock reads AHEAD of the primary's — a negative sample.
        var e = new ClockDriftEstimator();
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0);

        e.RecordSample(primaryTimestamp: t0, teammateTimestamp: t0.AddSeconds(45));

        Assert.Equal(TimeSpan.FromSeconds(-45), e.Estimate);
    }

    [Fact]
    public void TheEstimateIsTheMedianSoOneMismatchedOutlierCannotDominateIt()
    {
        // Five real, consistent samples around ~10s, and ONE wildly mismatched pair
        // (a coincidental same-named kill joined to the wrong occurrence, landing a
        // 40-MINUTE "sample"). A mean would be dragged to several minutes by that
        // single bad join; the median must stay inside the real cluster.
        var e = new ClockDriftEstimator();
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0);
        foreach (var seconds in new[] { 9, 10, 10, 11, 12 })
            e.RecordSample(t0.AddSeconds(seconds), t0);
        e.RecordSample(t0.AddMinutes(40), t0);   // the mismatched outlier

        Assert.NotNull(e.Estimate);
        Assert.True(e.Estimate!.Value < TimeSpan.FromSeconds(15),
            $"expected the median to stay near the real cluster, got {e.Estimate}");
        Assert.False(e.ExceedsThreshold);
    }

    [Fact]
    public void OldSamplesAgeOutOnceTheCapIsExceeded()
    {
        // Fill past the cap with one offset, then push it out entirely with a very
        // different one — the estimate must end up reflecting only the NEW value,
        // proving the old samples were actually dropped rather than merely diluted.
        var e = new ClockDriftEstimator();
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0);
        for (var i = 0; i < 8; i++)
            e.RecordSample(t0.AddSeconds(5), t0);   // fills the cap with a 5s offset
        Assert.Equal(TimeSpan.FromSeconds(5), e.Estimate);

        for (var i = 0; i < 8; i++)
            e.RecordSample(t0.AddMinutes(3), t0);   // enough to fully evict the old value

        Assert.Equal(TimeSpan.FromMinutes(3), e.Estimate);
    }

    [Fact]
    public void ExceedsThresholdIsFalseJustUnderTwoMinutesAndTrueJustOver()
    {
        var under = new ClockDriftEstimator();
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0);
        under.RecordSample(t0.Add(ClockDriftEstimator.Threshold).AddSeconds(-1), t0);
        Assert.False(under.ExceedsThreshold);

        var over = new ClockDriftEstimator();
        over.RecordSample(t0.Add(ClockDriftEstimator.Threshold).AddSeconds(1), t0);
        Assert.True(over.ExceedsThreshold);
    }

    [Fact]
    public void ExceedsThresholdTreatsTheOffsetSignSymmetrically()
    {
        // A teammate clock that reads far BEHIND is exactly as untrustworthy as one
        // that reads far ahead — the magnitude is what matters, not the sign.
        var e = new ClockDriftEstimator();
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0);
        e.RecordSample(t0, t0.Add(ClockDriftEstimator.Threshold).AddMinutes(1));

        Assert.True(e.ExceedsThreshold);
    }

    [Fact]
    public void ResetClearsEverySample()
    {
        var e = new ClockDriftEstimator();
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0);
        e.RecordSample(t0.AddSeconds(30), t0);
        Assert.NotNull(e.Estimate);

        e.Reset();

        Assert.Null(e.Estimate);
        Assert.Equal(0, e.SampleCount);
    }
}
