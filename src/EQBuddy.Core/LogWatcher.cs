using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

namespace EQBuddy.Core;

public sealed record CharacterLog(string FilePath, string Character, string Server)
{
    public string Display => $"{Character} ({Server})";
    public static CharacterLog? FromPath(string path)
    {
        // Archive filenames carry a _yyyyMMddHHmmss stamp, optionally a -N dedup
        // suffix (EqConfig.ArchiveDest); without stripping it, reviewing an archive
        // parsed server="server_20260813120000" and every per-character store wrote
        // under phantom keys (audit finding 4). The stamp is exactly 14 trailing
        // digits; the server group can't be empty, so a genuine two-segment name
        // whose server IS 14 digits still reads them as the server, not a stamp.
        var m = Regex.Match(Path.GetFileName(path),
            @"^eqlog_(?<char>[^_]+)_(?<server>.+?)(?:_\d{14}(?:-\d+)?)?\.txt$",
            RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        return new CharacterLog(path, m.Groups["char"].Value, m.Groups["server"].Value);
    }
}

/// <summary>
/// Tails a single character's log file: initial ingest of the whole file (SessionStats
/// auto-rolls on 60-minute gaps, so only the latest play session survives), then
/// incremental reads of appended bytes.
/// </summary>
public sealed class LogWatcher : IDisposable
{
    private readonly SessionStats _stats;
    private readonly System.Timers.Timer _timer;
    private readonly object _lock = new();

    private string? _path;
    private long _offset;
    /// <summary>Read cap for session-range review (#74): Poll never reads past this.
    /// long.MaxValue = live tailing, the normal state.</summary>
    private long _endOffset = long.MaxValue;
    /// <summary>Bumped by every Select (audit finding 5): the ingest task captures
    /// its generation, and only the LATEST one may declare ingest done and start the
    /// tail timer — a superseded Select's completion used to flip InitialIngestDone
    /// mid-way through its successor's ingest and let historical lines fire alerts.</summary>
    private long _selectGen;
    private readonly StringBuilder _remainder = new();

    /// <summary>The teammate's log, or null when the feed is off (see SelectTeammate).
    /// Everything about reading and dispatching it lives in <see cref="TeammateLogTail"/>
    /// so this file's diff against upstream stays small.</summary>
    private TeammateLogTail? _teammate;
    /// <summary>The gated view of <see cref="_teammate"/> for THIS poll — null outside
    /// live tailing — that PollPrimary's hook line reads. Keeping the gate here, rather
    /// than in PollPrimary itself, means the archive-review invariant does not rest
    /// solely on Select always Resetting the buffer: even a non-empty buffer left over
    /// by some future bug dispatches nothing while this is null.</summary>
    private TeammateLogTail? _activeMate;
    /// <summary>The last <see cref="TeammateLogTail.LastError"/> this watcher has already
    /// copied onto <see cref="LastError"/> — LastError is STICKY on the teammate side (it
    /// never clears itself), so without this an old teammate read error would overwrite a
    /// newer primary error every single tick, 150 ms apart.</summary>
    private Exception? _seenMateError;

    public DateTime? LastGrowth { get; private set; }
    public string? CurrentPath => _path;
    public bool InitialIngestDone { get; private set; }
    public Exception? LastError { get; private set; }
    public string? TeammatePath => _teammate?.Path;
    public string? TeammateName => _teammate?.Character;
    /// <summary>The teammate feed's own last-growth timestamp — kept separate from
    /// <see cref="LastGrowth"/>, which is the OWN log's diagnostic (MainWindow's
    /// UpdateLoggingStatus reads it as "is my logging on?"); a teammate copy that keeps
    /// growing must never mask your own log going dead.</summary>
    public DateTime? TeammateLastGrowth => _teammate?.LastGrowth;

    /// <summary>
    /// How many bytes of the selected log the tail has NOT consumed yet — 0 when every
    /// byte the file holds has been parsed. Diagnostic only: it exists for the
    /// <c>EQBUDDY_EXPAND</c> dump, which is the WPF layer's one test seam
    /// (docs/TestPlan.md §5), and it separates two failures that look identical from the
    /// outside. A counter that will not move with bytes still pending is a TAIL that has
    /// stopped; the same counter with nothing pending is a line that was read and did not
    /// COUNT. The E2E suite spent a round on the wrong one of those.
    ///
    /// -1 when no log is selected or the file cannot be measured.
    ///
    /// **Deliberately does NOT take `_lock`.** `Poll` holds it for the whole full-file
    /// ingest, so a caller on the UI thread — the dump runs on the widget's tick — would
    /// block behind the very replay it is trying to measure. The fields it reads are a
    /// `string?` and two `long`s, whose reads are atomic on the platforms this ships to;
    /// the answer is a diagnostic sample and is allowed to be one poll out of date.
    /// </summary>
    public long PendingBytes
    {
        get
        {
            if (_path is not { } path) return -1;
            try { return Math.Max(0, Math.Min(new FileInfo(path).Length, _endOffset) - _offset); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return -1; }
        }
    }

    /// <summary>Optional second consumer of the parsed event stream. Spawn timers ride
    /// the same replay-then-tail pipeline as stats, which is what lets a restart
    /// re-derive running countdowns from the log's own timestamps.</summary>
    public SpawnTimers? Spawns { get; set; }

    /// <summary>Optional third consumer: the mez-target tracker, same replay-safe
    /// pipeline (its entries are short-lived, so replay mostly proves them expired).</summary>
    public MezTracker? Mez { get; set; }

    /// <summary>Optional fourth consumer: attack-speed debuffs on the player (#94) —
    /// same replay-safe pipeline; replay proves old slows expired, live tail alerts.</summary>
    public SlowTracker? Slow { get; set; }

    /// <summary>Optional fifth consumer: buff countdowns — replay re-derives the
    /// timers still running from casts already in today's log.</summary>
    public BuffTracker? Buffs { get; set; }

    /// <summary>Optional sixth consumer: the raid-kill ledger — its own high-water
    /// mark makes replay idempotent, so it just rides the pipeline.</summary>
    public RaidKillLedger? Raids { get; set; }

    /// <summary>Optional seventh consumer: the per-zone spawn-point archive (the
    /// map's circles) — per-zone high-water marks, same replay discipline.</summary>
    public SpawnPointLedger? SpawnPoints { get; set; }

    /// <summary>Optional eighth consumer: the lost-buff history's evidence intake
    /// (#120 stage 3) — fades, hostile landings and deaths, buffered with their log
    /// times; the transition detection itself runs on the UI tick (Observe).</summary>
    public BuffLossLog? BuffLosses { get; set; }

    public LogWatcher(SessionStats stats)
    {
        _stats = stats;
        // 150 ms, not 500: this interval is the floor on how fast a watch rule can alert,
        // and Text rules are used for time-critical calls (a heal rotation announced by
        // someone else's raid script) where half a second of polling lag is the difference
        // between a useful cue and a late one. A poll on an unchanged file is a length
        // check — cheap enough to run ~7×/s and still be invisible next to the game.
        _timer = new System.Timers.Timer(150) { AutoReset = true };
        _timer.Elapsed += (_, _) => Poll();
    }

    /// <summary>The installed-game folder names, newest product first: a "EverQuest Legends"
    /// install and a plain "EverQuest" one can sit side by side under the same publisher
    /// directory.</summary>
    private static readonly string[] GameFolders = ["EverQuest Legends", "EverQuest"];

    public static string? FindDefaultLogFolder()
    {
        if (OperatingSystem.IsWindows() && FindLogFolderInRegistry() is { } installed)
            return installed;

        return PickLogFolder(CandidateLogFolders());
    }

    /// <summary>The Daybreak installer records the install location in the uninstall registry
    /// key, so custom install paths are found without any user configuration.</summary>
    [SupportedOSPlatform("windows")]
    private static string? FindLogFolderInRegistry()
    {
        foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
        foreach (var subkey in new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\DGC-EverQuest Legends",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\DGC-EverQuest Legends",
        })
        {
            try
            {
                using var key = hive.OpenSubKey(subkey);
                var marker = key?.GetValue("UninstallString") as string
                             ?? key?.GetValue("DisplayIcon") as string;
                if (marker is null) continue;
                var root = Path.GetDirectoryName(marker.Trim('"'));
                if (root is null) continue;
                var logs = Path.Combine(root, "Logs");
                if (Directory.Exists(logs)) return logs;
            }
            catch { /* registry access denied — fall through */ }
        }
        return null;
    }

    /// <summary>
    /// The candidate that looks most like the install actually being played: the one whose
    /// newest character log was written most recently.
    ///
    /// Existence alone is too weak a signal once several candidates are in play. A Mac with
    /// two Wine wrappers installed has two complete game trees, each with a Logs folder the
    /// installer created — but only the one that has been played holds any `eqlog_*.txt`,
    /// and someone who moved from one wrapper to the other leaves the abandoned tree behind
    /// forever. Falls back to the first existing folder when nothing has been played yet,
    /// which is the pre-existing behaviour for a fresh install.
    /// </summary>
    internal static string? PickLogFolder(IEnumerable<string> candidates)
    {
        var existing = candidates.Where(Directory.Exists).ToList();
        return existing
            .Select(folder => (folder, played: NewestLogWrite(folder)))
            .Where(c => c.played is not null)
            .OrderByDescending(c => c.played)
            .Select(c => c.folder)
            .FirstOrDefault()
            ?? existing.FirstOrDefault();
    }

    /// <summary>When this folder last saw play, or null if it holds no character logs.</summary>
    private static DateTime? NewestLogWrite(string folder)
    {
        // DiscoverCharacters already orders by write time, so the head is the newest.
        if (DiscoverCharacters(folder).FirstOrDefault() is not { } newest) return null;
        try { return File.GetLastWriteTimeUtc(newest.FilePath); }
        catch (IOException) { return null; }
    }

    private static IEnumerable<string> CandidateLogFolders()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        foreach (var game in GameFolders)
            yield return Path.Combine(@"C:\Users\Public\Daybreak Game Company\Installed Games", game, "Logs");

        yield return Path.Combine(home, ".local", "share", "Daybreak Game Company",
            "Installed Games", "EverQuest Legends", "Logs");

        if (!OperatingSystem.IsMacOS()) yield break;

        foreach (var prefix in WinePrefixRoots(home))
        foreach (var game in GameFolders)
            yield return Path.Combine(prefix, "drive_c", "users", "Public",
                "Daybreak Game Company", "Installed Games", game, "Logs");
    }

    /// <summary>
    /// Directories that may hold a Wine `drive_c` on macOS. EverQuest Legends has no Mac
    /// build, so a Mac player is running it under some Windows compatibility wrapper, and
    /// each wrapper parks its prefix somewhere different. Bottle names are the user's own
    /// words (CrossOver) or a generated id (Whisky), so bottle containers are enumerated
    /// rather than guessed at.
    /// </summary>
    private static IEnumerable<string> WinePrefixRoots(string home)
    {
        var appSupport = Path.Combine(home, "Library", "Application Support");

        // An explicit WINEPREFIX wins: whoever set it means it, and it is the only way to
        // find hand-rolled prefixes and Game Porting Toolkit setups, which have no fixed home.
        if (Environment.GetEnvironmentVariable("WINEPREFIX") is { Length: > 0 } chosen)
            yield return chosen;

        yield return Path.Combine(appSupport, "osxEQL", "prefix");
        yield return Path.Combine(home, ".wine");

        foreach (var container in new[]
        {
            Path.Combine(appSupport, "CrossOver", "Bottles"),
            Path.Combine(home, "Library", "Containers", "com.isaacmarovitz.Whisky", "Bottles"),
            Path.Combine(home, "Library", "PlayOnMac", "wineprefix"),
        })
        foreach (var bottle in ChildDirectories(container))
            yield return bottle;
    }

    private static IEnumerable<string> ChildDirectories(string parent)
    {
        try
        {
            return Directory.Exists(parent) ? Directory.EnumerateDirectories(parent) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CoreLog.Error(ex);
            return [];
        }
    }

    public static List<CharacterLog> DiscoverCharacters(string logFolder)
    {
        if (!Directory.Exists(logFolder)) return [];
        return Directory.EnumerateFiles(logFolder, "eqlog_*.txt")
            .Select(CharacterLog.FromPath)
            .Where(c => c is not null)
            .Select(c => c!)
            .OrderByDescending(c => File.GetLastWriteTimeUtc(c.FilePath))
            .ToList();
    }

    /// <summary>The character whose log grew most recently (the one being played).</summary>
    public static CharacterLog? MostRecentlyActive(string logFolder) =>
        DiscoverCharacters(logFolder).FirstOrDefault();

    public void Select(string path) => Select(path, 0, long.MaxValue);

    /// <summary>Pick (or clear, with null) the teammate log this watcher also tails —
    /// see <see cref="TeammateFeed"/> for what of it reaches the shared pipeline.
    ///
    /// If a primary log is already selected and LIVE (not an archive review), this
    /// re-Selects it — a full replay of both files, merged. Poll merges the two
    /// files' new lines by timestamp on every tick, but that only works for lines
    /// that arrive AFTER the pick: a teammate chosen mid-session would otherwise
    /// have its whole file ingested from byte 0, with OLD timestamps, on top of a
    /// session whose clock already reads "now" — the same backward-clock wipe the
    /// merge exists to prevent, just triggered at pick time instead of at a Poll.
    /// <see cref="Select(string)"/> already resets and replays from scratch;
    /// <c>SessionStats.Reset()</c> does not raise <c>SessionRolledOver</c>, and
    /// re-Selecting the SAME path is the character-switch replay the app already
    /// does elsewhere. Nothing replays before the first Select (the constructor
    /// case): <see cref="SelectTeammate"/> runs before it, and that first Select's
    /// own initial ingest merges both files from the start.</summary>
    public void SelectTeammate(string? path)
    {
        lock (_lock)
        {
            // Bump the OUTGOING tail's generation before it is replaced, so any
            // DrainBefore still dispatching on it (a re-entrant call from inside a
            // consumer callback invoked below it on the stack) stops rather than going
            // on feeding a session this Select is about to rebuild. The only realistic
            // callers of SelectTeammate are UI actions on the dispatcher thread, but a
            // consumer callback could in principle re-enter here on the poll thread —
            // Monitor is re-entrant, so nothing stops it. What the outer PollPrimary
            // loop does after a nested Select/SelectTeammate returns is upstream's own
            // re-entrancy semantics; this does not attempt to change PollPrimary.
            _teammate?.Discard();
            // A fresh tail's first read failure must surface once even when it repeats
            // the outgoing tail's (type, message) — the guard in Poll is per tail.
            _seenMateError = null;
            _teammate = path is null ? null : new TeammateLogTail(path, _stats, () => Mez);
            // Inside the lock, not after releasing it: Monitor is re-entrant, so Select's
            // own lock scope nests fine here, and its Task.Run ingest just waits for our
            // release like anything else queued behind it. Calling it AFTER releasing
            // left a window for a timer Poll already blocked on _lock to win the race,
            // Fill the teammate's WHOLE history and drain it into the live session with
            // OLD timestamps before the replay below ever ran — the very backward-clock
            // wipe this replay exists to prevent, just reachable from the pick itself.
            if (_path is { } p && _endOffset == long.MaxValue) Select(p);
        }
    }

    /// <summary>Select with a byte range — review mode replaying ONE session out of a
    /// multi-session file (#74). [startOffset, endOffset) must fall on line boundaries
    /// (LogSessions.Scan guarantees it); live tailing is the (0, MaxValue) case.</summary>
    public void Select(string path, long startOffset, long endOffset)
    {
        long gen;
        lock (_lock)
        {
            _timer.Stop();
            gen = ++_selectGen;
            _path = path;
            var charInfo = CharacterLog.FromPath(path);
            _stats.CharacterName = charInfo?.Character;
            _stats.ServerName = charInfo?.Server;
            if (Spawns is { } sp) sp.Server = charInfo?.Server ?? "";
            _offset = startOffset;
            _endOffset = endOffset;
            _remainder.Clear();
            // A primary replay restarts the session, so the teammate file (if any)
            // must replay from the top too.
            _teammate?.Reset();
            InitialIngestDone = false;
            _stats.ClearCharacterState();
            _stats.Reset();
            // Every Select is a replay starting over; the ledger's boundary-second
            // counters must not carry over from the previous pass (finding 3).
            SpawnPoints?.ReplayStarting();
            // Buff sights and active buffs are per character/session, and the tracker
            // used to be process-lifetime — character A's landings leaked into B's
            // Missing/NotSeen claims (#120 stage-2 fix). The full-file ingest below
            // re-derives the new character's state.
            Buffs?.ResetSession();
            // Same isolation for the loss history (#120 stage 3): its entries are
            // session claims about ONE character; the reset also re-arms its
            // first-look rule for the replay below.
            BuffLosses?.ResetSession();
        }
        if (!DeferIngestForTests) Task.Run(() => FinishInitialIngest(gen));
        // Note (finding 5, scoped out): Select still blocks its caller only for the
        // state reset above, but the ingest itself stays a background task — making
        // Select fully non-blocking end-to-end is a bigger change than this pass.
    }

    /// <summary>The queued half of Select: full-file ingest, then the live-tail
    /// handoff — which belongs only to the latest Select. Internal so tests can
    /// replay the overlapping-Select interleaving Task.Run won't order on demand.</summary>
    internal void FinishInitialIngest(long gen)
    {
        Poll(); // full-file ingest
        lock (_lock)
        {
            if (gen != _selectGen) return;   // a newer Select owns the watcher now
            InitialIngestDone = true;
            _timer.Start();   // under the lock: a Select racing in can't be un-stopped
        }
    }

    /// <summary>Tests only: suppress Select's background ingest task so the
    /// generation handoff runs deterministically via <see cref="FinishInitialIngest"/>.</summary>
    internal bool DeferIngestForTests;

    /// <summary>Tests only: the generation the most recent Select minted.</summary>
    internal long SelectGeneration { get { lock (_lock) return _selectGen; } }

    /// <summary>How many times a log has been SELECTED on this watcher — 1 after a normal
    /// launch. Diagnostic, for the same dump as <see cref="PendingBytes"/>: a Select
    /// resets the session and replays the file underneath whatever is reading it, and
    /// nothing else in the dump would say so. Lock-free for the same reason as
    /// <see cref="PendingBytes"/>.</summary>
    public long SelectCount => _selectGen;

    /// <summary>Upstream-tracking seam. <see cref="PollPrimary"/> is upstream's poll body,
    /// verbatim, plus one hook line; keep it that way so a rebase onto upstream stays a
    /// three-line merge. The teammate tail fills its buffer before the primary read and
    /// drains it in timestamp order: per primary line inside PollPrimary, and here for
    /// whatever remains. Never during archive review (_endOffset bounds one past session
    /// of YOUR log; the teammate file has no matching slice). SessionStats rolls the
    /// session on a FORWARD 60-minute gap only, which is why the two files must be
    /// interleaved chronologically rather than read one after the other.</summary>
    private void Poll()
    {
        lock (_lock)
        {
            // Live-only: _path is not null alone matches 9dcbadd5's own teammate gating
            // (a teammate-only session, primary path set but its file absent, is
            // unchanged from that shipped behaviour and not a regression of this seam).
            var mate = _path is not null && _endOffset == long.MaxValue ? _teammate : null;
            _activeMate = mate;   // PollPrimary's hook reads this, never _teammate directly
            mate?.Fill();
            // The teammate feed's OWN read failures (e.g. a permission-denied synced
            // file) used to vanish into a tail nothing read; surface them the same way
            // a primary read failure already does. Copy only on CHANGE — LastError is
            // sticky, so copying it unconditionally every tick would let a stale
            // teammate error stomp a newer primary error 150 ms later. Compared by
            // (type, message) rather than by reference: a persistent non-IO failure
            // (e.g. UnauthorizedAccessException on an ACL-denied synced file) gets a
            // FRESH exception instance every tick from TeammateLogTail.Fill, so a
            // reference comparison never once matched and this guard was overwriting
            // LastError every 150 ms anyway for exactly the failure it exists to quiet.
            if (mate?.LastError is { } mateErr && (_seenMateError is null ||
                mateErr.GetType() != _seenMateError.GetType() || mateErr.Message != _seenMateError.Message))
            {
                LastError = mateErr;
                _seenMateError = mateErr;
            }
            // Upstream's single try/catch aborted the WHOLE poll on a consumer throw;
            // splitting primary and teammate into two calls must not let the trailing
            // drain run anyway after PollPrimary swallowed one — skip it so a poisoned
            // tick doesn't feed teammate lines past a primary chunk that gave up early.
            // PollPrimary's catch cannot be edited (see the seam note above), so
            // detecting a swallowed throw means reading LastError from OUTSIDE it —
            // exactly what a real UI diagnostic reader would see, and no more. Clearing
            // LastError to null immediately before the call and testing for non-null
            // after is EXACT: any assignment PollPrimary's catch makes, even of the
            // SAME exception instance a consumer rethrows on consecutive polls, leaves
            // LastError non-null. Comparing the reference of LastError before/after
            // instead (as this used to) has a blind spot on exactly that case — the
            // rethrown instance is reference-equal to what was already sitting in
            // LastError, so "unchanged" reads as "succeeded" and the trailing drain
            // runs anyway. LastError has a private setter and this all runs under
            // _lock, so clearing and restoring it here is legal; a UI reader may
            // observe a transient null for the duration of one poll, which is a
            // diagnostic sample and allowed to be one poll stale (the same caveat
            // PendingBytes documents above).
            var before = LastError;
            LastError = null;
            PollPrimary();
            var poisoned = LastError is not null;
            if (!poisoned) LastError = before;   // PollPrimary didn't throw — restore whatever (if anything) was already there
            if (!poisoned)
            {
                try { mate?.DrainBefore(DateTime.MaxValue); }
                catch (Exception ex) { LastError = ex; }   // same containment as the primary's own catch
            }
            else
            {
                // PollPrimary swallowed a consumer throw this tick (LastError changed).
                // Upstream drops the REST of a poisoned poll's own chunk on a throw —
                // match it on the teammate side: discard whatever is still buffered
                // rather than deferring it, or those lines survive to dispatch on the
                // NEXT poll — which, during initial ingest, can be after
                // InitialIngestDone has already flipped true, making a historical
                // teammate line fire a Text watch alert as though it had just arrived
                // live.
                mate?.Discard();
            }
        }
    }

    /// <summary>Tests only: <see cref="Poll"/> is private because nothing outside this
    /// class should be able to trigger a tick — the timer and <see cref="FinishInitialIngest"/>
    /// are the only two callers production code needs. A test that wants to drive a
    /// SECOND poll deterministically (no timer, no new bytes required) needs a seam.</summary>
    internal void PollForTests() => Poll();

    private void PollPrimary()
    {
        lock (_lock)
        {
            if (_path is null || !File.Exists(_path)) return;
            try
            {
                using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var readable = Math.Min(fs.Length, _endOffset);
                if (readable < _offset)
                {
                    // File truncated (session cleanup) — re-anchor but keep current stats;
                    // the 60-minute gap rule rolls the session when new play begins.
                    _offset = 0;
                    _remainder.Clear();
                    readable = Math.Min(fs.Length, _endOffset);
                }
                if (readable == _offset) return;

                fs.Seek(_offset, SeekOrigin.Begin);
                var buf = new byte[readable - _offset];
                fs.ReadExactly(buf);
                var chunk = Encoding.Latin1.GetString(buf);
                _offset = readable;
                LastGrowth = DateTime.Now;

                var text = _remainder.ToString() + chunk;
                _remainder.Clear();
                int start = 0;
                while (true)
                {
                    int nl = text.IndexOf('\n', start);
                    if (nl < 0)
                    {
                        _remainder.Append(text, start, text.Length - start);
                        break;
                    }
                    int end = nl > start && text[nl - 1] == '\r' ? nl - 1 : nl;
                    if (end > start)
                    {
                        var line = text[start..end];
                        // Split ONCE (perf audit #13): Parse and ObserveRawLine each
                        // used to re-run the line regex + timestamp parse. A line
                        // whose stamp doesn't split was ignored by both before too.
                        if (LogParser.TrySplitLine(line, out var ts, out var msg))
                        {
                            _activeMate?.DrainBefore(ts);   // teammate lines stamped strictly before this one go first; ties go primary-first
                            var evt = LogParser.Parse(ts, msg);
                            if (evt is not null)
                            {
                                _stats.Apply(evt);
                                Spawns?.Apply(evt);
                                Mez?.Apply(evt);
                                Slow?.Apply(evt);
                                Buffs?.Apply(evt);
                                BuffLosses?.Apply(evt);
                                Raids?.Apply(evt);
                                SpawnPoints?.Apply(evt);
                            }
                            // Every line, parsed or not: a Text watch rule matches the
                            // line's words, not whatever event we did or didn't make of it.
                            _stats.ObserveRawLine(ts, msg);
                        }
                    }
                    start = nl + 1;
                }
            }
            catch (IOException)
            {
                // File busy — try again next tick.
            }
            catch (Exception ex)
            {
                LastError = ex;
            }
        }
    }

    public void Dispose() => _timer.Dispose();
}
