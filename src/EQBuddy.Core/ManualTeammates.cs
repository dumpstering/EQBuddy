namespace EQBuddy.Core;

/// <summary>
/// A teammate the player added by hand in Options → Behavior → Teammates, for someone the
/// log never announced (already in the group before the log began, or in a group the
/// player joined). It is NOT a standing member: it is a join the player vouches for, at
/// <see cref="Since"/> (log time), for ONE character on ONE server. From that moment the
/// name is a member exactly like one the log detected, so the same lines end it — "Garg has
/// left the group.", "You remove Garg from the party.", a disband, or the next login.
/// Nothing brings it back but a new group signal in the log or the player adding it again.
/// </summary>
public sealed record ManualTeammate(string Name, string Character, string Server, DateTime Since);

/// <summary>The rules over <see cref="AppSettings.ManualTeammates"/>: whose entry is whose,
/// and when a name added now joins. Pure, so the Options row only calls it.</summary>
public static class ManualTeammates
{
    /// <summary>Whether <paramref name="entry"/> belongs to the log of
    /// <paramref name="character"/> on <paramref name="server"/>. An entry for another
    /// character — or another server's character of the same name — never applies.</summary>
    public static bool IsFor(ManualTeammate? entry, string? character, string? server) =>
        entry is { Name.Length: > 0, Character.Length: > 0 }
        && character is { Length: > 0 }
        && entry.Character.Equals(character, StringComparison.OrdinalIgnoreCase)
        && string.Equals(entry.Server ?? "", server ?? "", StringComparison.OrdinalIgnoreCase);

    /// <summary>The entries for one character's log, oldest join first. A null list or a
    /// null entry (a hand-edited settings file) reads as nothing.</summary>
    public static IReadOnlyList<ManualTeammate> For(IEnumerable<ManualTeammate>? all, string? character, string? server) =>
        all is null ? [] : [.. all.Where(e => IsFor(e, character, server)).OrderBy(e => e.Since)];

    /// <summary>The distinct names added for one character's log, in the order first added.</summary>
    public static IReadOnlyList<string> NamesFor(IEnumerable<ManualTeammate>? all, string? character, string? server) =>
        [.. For(all, character, server).Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>When a name added now joins. The FIRST time it is added for this character,
    /// at the start of the current session (<paramref name="sessionStart"/>), so it counts
    /// from there — someone who was in the group all along. Added AGAIN (it was added
    /// before and the log has since ended it), or with no session yet, from
    /// <paramref name="now"/>: its earlier join stays where it was, so the stint the log
    /// already ended keeps what it counted and the time in between is not credited.</summary>
    public static DateTime JoinTime(bool addedBefore, DateTime? sessionStart, DateTime now) =>
        !addedBefore && sessionStart is { } start
            ? start
            : new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, now.Kind);   // log lines are whole seconds

    /// <summary>Stops counting <paramref name="name"/> for this character: every join the
    /// player added for it goes. Returns how many entries were removed.</summary>
    public static int Remove(List<ManualTeammate> all, string name, string? character, string? server) =>
        all.RemoveAll(e => IsFor(e, character, server) && e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
