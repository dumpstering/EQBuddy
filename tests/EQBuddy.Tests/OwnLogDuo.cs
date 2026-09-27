using EQBuddy.Core;

namespace EQBuddy.Tests;

/// <summary>
/// Test harness shaped exactly like <see cref="LogWatcher"/>'s poll: each line is parsed
/// once, applied to the PRIMARY <see cref="SessionStats"/>, and then handed with that
/// same parsed event to the primary's own <see cref="SessionStats.Teammates"/>. No
/// teammate file anywhere — the whole point of the feature.
/// </summary>
internal sealed class OwnLogDuo
{
    public const string PrimaryName = "Smargush";

    public SessionStats Primary { get; } = new() { CharacterName = PrimaryName };

    public OwnLogDuo(params string[] manualNames) => Primary.Teammates.ManualNames = manualNames;

    public DerivedTeammates Teammates => Primary.Teammates;

    public OwnLogDuo Feed(DateTime t, string msg)
    {
        var evt = LogParser.Parse(t, msg);
        if (evt is not null) Primary.Apply(evt);
        Primary.Teammates.ObservePrimaryLine(t, msg, evt);
        return this;
    }

    /// <summary>What every display surface reads (MainWindow.BuildSnapshot).</summary>
    public StatsSnapshot Combined(TimeSpan? window = null, IReadOnlyList<TrackedRule>? rules = null) =>
        Primary.DuoSnapshot(window, rules);
}
