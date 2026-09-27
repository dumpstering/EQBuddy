using System.IO;
using System.Text;

namespace EQBuddy.Core;

/// <summary>
/// The teammate half of the watcher. Teammates are derived from the watched character's
/// OWN log — there is no second file — so the upstream poll needs exactly two lines for
/// them (one hook that hands every primary line to <see cref="SessionStats.Teammates"/>,
/// one reset in <see cref="Select(string, long, long)"/>); everything else lives here.
/// </summary>
public sealed partial class LogWatcher
{
    /// <summary>The teammates derived from the watched log — the Options row reads the
    /// auto-detected names and writes the manual ones here.</summary>
    public DerivedTeammates Teammates => _stats.Teammates;

    /// <summary>Re-derives the CURRENT session's teammates from the lines the watcher has
    /// already read, after the manual name list changed — so a name added mid-session
    /// counts from the session's start, not from the moment it was typed. Runs off the
    /// caller's thread; the poll waits on the watcher lock meanwhile, and resumes with
    /// the bytes after the ones re-read here, so no line is fed twice.</summary>
    public Task RederiveTeammatesAsync() => Task.Run(RederiveTeammates);

    internal void RederiveTeammates()
    {
        lock (_lock)
        {
            var teammates = _stats.Teammates;
            teammates.ResetSession();
            if (_path is null || _stats.Snapshot().SessionStart is not { } sessionStart) return;
            try
            {
                using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                // Latin1 is one char per byte, so reading _offset chars reads exactly the
                // bytes the poll has consumed. A trailing partial line is still in the
                // poll's remainder and reaches the teammates when it completes.
                using var reader = new StreamReader(fs, Encoding.Latin1, detectEncodingFromByteOrderMarks: false);
                var consumed = Math.Min(_offset, fs.Length);
                long read = 0;
                var line = new StringBuilder();
                var buffer = new char[64 * 1024];
                while (read < consumed)
                {
                    var n = reader.Read(buffer, 0, (int)Math.Min(buffer.Length, consumed - read));
                    if (n <= 0) break;
                    read += n;
                    for (var i = 0; i < n; i++)
                    {
                        var c = buffer[i];
                        if (c != '\n') { line.Append(c); continue; }
                        if (line.Length > 0 && line[^1] == '\r') line.Length--;
                        if (line.Length > 0 && LogParser.TrySplitLine(line.ToString(), out var ts, out var msg)
                            && ts >= sessionStart)
                            teammates.ObservePrimaryLine(ts, msg, LogParser.Parse(ts, msg));
                        line.Clear();
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The log is busy or gone: the teammates restart from the next line the
                // poll reads, which is what a fresh session would do anyway.
                CoreLog.Error(ex);
            }
        }
    }
}
