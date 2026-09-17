namespace EQBuddy.Core;

/// <summary>
/// The teammate-log picker's own validation (finding 4). The Behavior block's blurb
/// promises the file will be kept OUTSIDE the game's Logs folder, "or EQBuddy will
/// follow it as if it were you" — but until repair round R3 nothing enforced that
/// promise continuously: the picker checked it once, at file-dialog time, and a path
/// saved earlier (or a Logs-folder change afterward) bypassed it entirely. That is a
/// real hazard, not a hypothetical one: <c>MainWindow.FollowActiveCharacter</c> picks
/// <c>LogWatcher.MostRecentlyActive(Settings.LogFolder)</c> as the PRIMARY log,
/// finalizes the live session as a character change, and re-identifies the archiver —
/// so a teammate log dropped inside the Logs folder silently hijacks the user's own
/// identity and archives sessions under the wrong character.
///
/// Lives in <c>EQBuddy.Core</c> (moved from UI.Shared in R3) so <see cref="LogWatcher"/>
/// itself can call it on every <see cref="LogWatcher.SelectTeammate"/> and
/// <see cref="LogWatcher.Select(string, long, long)"/> — not just from the WPF picker —
/// which is what makes the check continuous: a Logs-folder change or a primary-log
/// change re-validates the ALREADY-installed teammate path the next time either method
/// runs, both of which the host already calls on every such change.
///
/// Same-file and inside-folder comparisons go through <see cref="FileIdentity"/>
/// (repair round R4) rather than a raw path-string compare, so a hard link, a
/// junction, a symlink, or an 8.3/UNC alias to the SAME file cannot slip past either
/// guard. The rule this repo does not bend on applies here too: a refusal must be an
/// observable, specific message, never a silent no-op.
/// </summary>
public static class TeammateLogPicker
{
    /// <summary>Null when <paramref name="chosenPath"/> is fine to adopt as the
    /// teammate log; otherwise the short, specific message to show the player instead
    /// of installing it. Checked in this order: existence, same file as the current
    /// primary log, and inside (or a subdirectory of) the Logs folder.
    ///
    /// <paramref name="requireExists"/> defaults to true for the file-dialog picker,
    /// where a chosen file that doesn't exist is a real user error worth catching
    /// immediately. LogWatcher's continuous re-validation (repair round R3) passes
    /// false: a persisted or already-installed teammate path may legitimately name a
    /// file that hasn't appeared yet (the teammate hasn't logged in this session, or
    /// their sync tool hasn't run) — <see cref="TeammateLogTail"/> already tolerates a
    /// missing file gracefully, and refusing the path outright here would regress
    /// that.</summary>
    public static string? Validate(
        string chosenPath, string? primaryLogPath, string? logFolder, bool requireExists = true)
    {
        if (requireExists && !File.Exists(chosenPath))
            return "That file doesn't exist. Pick your teammate's own eqlog file instead.";

        if (primaryLogPath is { Length: > 0 } && FileIdentity.SamePath(chosenPath, primaryLogPath))
            return "That's your own log file, not your teammate's. Pick their eqlog file instead.";

        if (logFolder is { Length: > 0 } && FileIdentity.IsInside(chosenPath, logFolder))
            return "That file is inside your Logs folder, so EQBuddy would follow it as if it "
                 + "were you. Copy it (or have it synced) somewhere else first.";

        return null;
    }
}
