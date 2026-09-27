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

    private string[]? _handAdded;

    /// <summary><paramref name="handAdded"/>: names the player added by hand in Options for
    /// this character, joining at the first line fed — "in the group before the log began".</summary>
    public OwnLogDuo(params string[] handAdded) => _handAdded = handAdded;

    public DerivedTeammates Teammates => Primary.Teammates;

    public OwnLogDuo Feed(DateTime t, string msg)
    {
        if (_handAdded is { } names)
        {
            Primary.Teammates.Manual = [.. names.Select(n => new ManualTeammate(n, PrimaryName, "", t))];
            _handAdded = null;
        }
        var evt = LogParser.Parse(t, msg);
        if (evt is not null) Primary.Apply(evt);
        Primary.Teammates.ObservePrimaryLine(t, msg, evt);
        return this;
    }

    /// <summary>What every display surface reads (MainWindow.BuildSnapshot).</summary>
    public StatsSnapshot Combined(TimeSpan? window = null, IReadOnlyList<TrackedRule>? rules = null) =>
        Primary.DuoSnapshot(window, rules);
}
