using System.IO;
using System.Text;

namespace EQBuddy.Core;

/// <summary>
/// The teammate half of the watcher. Teammates are derived from the watched character's
/// OWN log — there is no second file — so the upstream code needs exactly three lines for
/// them (one hook in the poll that hands every primary line to
/// <see cref="SessionStats.Teammates"/>, one <see cref="ResetTeammates"/> in
/// <see cref="Select(string, long, long)"/>, one <see cref="TeammatesLogRestarted"/> where
/// the poll re-anchors a truncated file); everything else lives here.
/// </summary>
public sealed partial class LogWatcher
{
    /// <summary>The teammates derived from the watched log — the Options row reads the
    /// auto-detected names and writes the manual ones here.</summary>
    public DerivedTeammates Teammates => _stats.Teammates;

    /// <summary>Where the current Select started reading: a re-derivation replays the
    /// group lines from here, as the poll first read them.</summary>
    private long _teammatesFrom;

    /// <summary>Select's reset, under the watcher lock, after it set the start offset.</summary>
    private void ResetTeammates()
    {
        _teammatesFrom = _offset;
        _stats.Teammates.Reset();
    }

    /// <summary>The poll found the file shorter than what it had read (a "Reset session"
    /// with archiving on moved it to Logsrchive) and starts again from byte 0: a
    /// re-derivation now replays the new file, from the members of the group at the split.</summary>
    private void TeammatesLogRestarted()
    {
        _teammatesFrom = 0;
        _stats.Teammates.LogRestarted();
    }

    /// <summary>Re-derives the CURRENT session's teammates from the lines the watcher has
    /// already read, after the manual name list changed — so a name added mid-session
    /// counts from the session's start, not from the moment it was typed. Runs off the
    /// caller's thread; the poll waits on the watcher lock meanwhile, and resumes with
    /// the bytes after the ones re-read here, so no line is fed twice.</summary>
    public Task RederiveTeammatesAsync() => Task.Run(RederiveTeammates);

    /// <summary>The replay goes into a staging instance and is committed in one step, so
    /// the widget and the phone never see a half-replayed session, and a log that cannot
    /// be read leaves the teammates exactly as they were. Every line since the Select is
    /// read: the ones before the session start only rebuild group membership, in log
    /// order, so a member who joined late is not credited with what they did before
    /// joining.</summary>
    internal void RederiveTeammates()
    {
        lock (_lock)
        {
            if (_path is null || _stats.Snapshot().SessionStart is not { } sessionStart) return;
            var teammates = _stats.Teammates;
            var (staging, generation) = teammates.BeginReplay();
            try
            {
                using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                // Latin1 is one char per byte, so reading up to _offset reads exactly the
                // bytes the poll has consumed. A trailing partial line is still in the
                // poll's remainder and reaches the teammates when it completes.
                var consumed = Math.Min(_offset, fs.Length);
                var from = _teammatesFrom <= consumed ? _teammatesFrom : 0;   // truncated since
                fs.Seek(from, SeekOrigin.Begin);
                using var reader = new StreamReader(fs, Encoding.Latin1, detectEncodingFromByteOrderMarks: false);
                var remaining = consumed - from;
                var line = new StringBuilder();
                var buffer = new char[64 * 1024];
                while (remaining > 0)
                {
                    var n = reader.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (n <= 0) break;
                    remaining -= n;
                    for (var i = 0; i < n; i++)
                    {
                        var c = buffer[i];
                        if (c != '\n') { line.Append(c); continue; }
                        if (line.Length > 0 && line[^1] == '\r') line.Length--;
                        if (line.Length > 0 && LogParser.TrySplitLine(line.ToString(), out var ts, out var msg))
                        {
                            if (ts < sessionStart) staging.ObserveRosterLine(ts, msg);
                            else staging.ObservePrimaryLine(ts, msg, LogParser.Parse(ts, msg));
                        }
                        line.Clear();
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The log is busy or gone: keep what was derived live, rather than lose
                // the session's teammates to a replay that could not run.
                CoreLog.Error(ex);
                return;
            }
            teammates.CommitReplay(staging, generation);
        }
    }
}
