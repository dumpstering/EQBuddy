using System.Text;

namespace EQBuddy.Core;

/// <summary>
/// Everything a teammate's log needs — reading its new bytes, splitting them into
/// timestamped lines, holding them until the primary log catches up, and deciding
/// what of them reaches the shared pipeline (see <see cref="TeammateFeed"/>) — lives
/// HERE rather than inside <see cref="LogWatcher"/>. LogWatcher.Poll() (upstream's,
/// renamed PollPrimary) is a file this fork's upstream edits every few days; keeping
/// the teammate feature in its own class means LogWatcher's diff against upstream
/// stays a handful of added lines plus one hook call, instead of a rewritten Poll
/// body that has to be re-merged by hand on every upstream change.
///
/// <b>Known limitation (pre-existing from the first implementation, out of scope
/// here):</b> ordering is per-poll. If the sync tool that copies a teammate's log
/// delivers a burst of their lines LATE — more than 60 minutes after the fact — while
/// you kept playing, SessionStats' forward-only gap roll can fire on the teammate's
/// own internal gap between their last-synced line and the late burst, rolling the
/// shared session on a gap that was never really there in wall-clock time. Noted here
/// so nobody rediscovers it as a new bug.
/// </summary>
public sealed class TeammateLogTail
{
    private readonly SessionStats _stats;
    private readonly Func<MezTracker?> _mez;
    private readonly List<(DateTime Ts, string Msg)> _buffer = [];
    /// <summary>Index of the first undispatched entry in <see cref="_buffer"/>. Draining
    /// advances this instead of removing from the front of the list on every call — see
    /// <see cref="DrainBefore"/> for why that matters.</summary>
    private int _head;
    private readonly StringBuilder _remainder = new();
    private long _offset;
    /// <summary>Bumped by <see cref="Reset"/> and <see cref="Discard"/> — a consumer
    /// callback invoked from inside <see cref="DrainBefore"/> can re-entrantly call
    /// back into either of those (a Text-watch handler calling LogWatcher.Select or
    /// SelectTeammate; Monitor's lock is re-entrant, so nothing stops it), which clears
    /// <see cref="_buffer"/> and <see cref="_head"/> out from under the very loop that
    /// is still iterating them. DrainBefore captures this at entry and re-checks it
    /// before touching either field again, so a re-entrant reset always wins over the
    /// interrupted call's own idea of where the batch ended.</summary>
    private int _generation;

    public TeammateLogTail(string path, SessionStats stats, Func<MezTracker?> mez)
    {
        Path = path;
        _stats = stats;
        _mez = mez;
        Character = CharacterLog.FromPath(path)?.Character;
    }

    public string Path { get; }
    public string? Character { get; }
    public Exception? LastError { get; private set; }
    /// <summary>When this file last grew — kept separate from LogWatcher.LastGrowth,
    /// which MainWindow.UpdateLoggingStatus reads as "is my OWN logging on?"; folding
    /// the teammate's growth into that diagnostic would hide your own log going dead
    /// behind a teammate copy that keeps growing.</summary>
    public DateTime? LastGrowth { get; private set; }

    /// <summary>Reads whatever new bytes the file has grown by since the last call —
    /// same truncation-safe, byte-offset, Latin1, remainder-splicing logic
    /// upstream's Poll uses for the primary log — and appends the newly split
    /// (timestamp, message) lines to the internal buffer in file order. Returns
    /// true only if the offset actually advanced. A missing file, or a read that
    /// finds nothing new, quietly returns false; an <see cref="IOException"/>
    /// (file busy — the same condition upstream's Poll shrugs off for the primary
    /// log) also returns false without recording an error, since the next tick
    /// will simply try again. Any other exception is recorded on
    /// <see cref="LastError"/> before returning false.</summary>
    public bool Fill()
    {
        if (!File.Exists(Path)) return false;
        try
        {
            using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var readable = fs.Length;
            if (readable < _offset)
            {
                // File truncated (session cleanup) — re-anchor, same as the primary.
                _offset = 0;
                _remainder.Clear();
                readable = fs.Length;
            }
            if (readable == _offset) return false;

            fs.Seek(_offset, SeekOrigin.Begin);
            var buf = new byte[readable - _offset];
            fs.ReadExactly(buf);
            var chunk = Encoding.Latin1.GetString(buf);
            _offset = readable;

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
                    if (LogParser.TrySplitLine(line, out var ts, out var msg))
                        _buffer.Add((ts, msg));
                }
                start = nl + 1;
            }
            LastGrowth = DateTime.Now;
            return true;
        }
        catch (IOException)
        {
            return false;   // file busy — try again next tick
        }
        catch (Exception ex)
        {
            LastError = ex;
            return false;
        }
    }

    /// <summary>Dispatches every buffered line whose timestamp is STRICTLY BEFORE
    /// <paramref name="ts"/>, in file order. EQ's log stamps are 1-second granular, so
    /// ties between the primary and a teammate's line are common in a duo session — a
    /// STRICT bound (not <![CDATA[<=]]>) means a line stamped exactly at <paramref name="ts"/>
    /// stays buffered rather than dispatching ahead of the primary line that carries the
    /// same stamp. It waits for the next primary line (whose own DrainBefore call passes
    /// a strictly-later stamp) or for the trailing <c>DrainBefore(DateTime.MaxValue)</c> —
    /// so on a tie the PRIMARY line wins, matching the ordering from before the teammate
    /// feed existed at all.
    ///
    /// Advances an internal head index rather than removing dispatched entries from the
    /// front of the list on every call — <see cref="List{T}.RemoveRange"/> is O(n) in what
    /// remains, and the hook runs once per PRIMARY line, so doing it per call made the
    /// initial ingest of two 200k-line logs O(n²) (measured ~17s vs ~3s before this fix).
    /// The list is compacted only once fully drained, or once the dead prefix grows past
    /// roughly half of it — see <see cref="Compact"/> — so the amortised cost per line
    /// stays O(1).
    ///
    /// Dispatch mirrors LogWatcher's own teammate handling: parse the line, hand the
    /// event to SessionStats when <see cref="TeammateFeed.AdmitForStats"/> allows it and
    /// to the mez tracker when <see cref="TeammateFeed.AdmitForMez"/> allows it, then
    /// ALWAYS feed the raw (timestamp, message) pair to
    /// <see cref="SessionStats.ObserveRawLine"/> — a Text watch rule matches the line's
    /// words whether or not it parsed into an event.</summary>
    public void DrainBefore(DateTime ts)
    {
        int end = _head;
        while (end < _buffer.Count && _buffer[end].Ts < ts) end++;
        if (end == _head) return;

        // Captured before the loop touches anything: see the generation's own doc for
        // why a re-entrant Reset()/Discard() from inside a consumer callback below must
        // be detectable both mid-loop (the early-return check) and in the finally.
        int generation = _generation;

        // try/finally, not a plain loop: a consumer throwing mid-batch (SessionStats,
        // the mez tracker, or a TextMatched subscriber raised synchronously off
        // ObserveRawLine) must not leave the already-dispatched lines re-visitable on
        // the next call. Matching PollPrimary's own containment, the WHOLE bound batch
        // — not just the lines up to the poisoned one — is dropped on a throw: _head
        // jumps straight to `end` in the finally, so a line buffered AFTER the thrower
        // within this same bound cannot survive to fire a Text watch alert on some later
        // poll as if it had just arrived live.
        try
        {
            for (; _head < end; _head++)
            {
                var (lineTs, msg) = _buffer[_head];
                var evt = LogParser.Parse(lineTs, msg);
                // Checked after EACH of the three consumer calls below, not just once
                // at the bottom: any one of them can re-entrantly call back into
                // Reset() or Discard() — e.g. a TextMatched handler that calls
                // LogWatcher.Select, or SessionStats itself raising SessionRolledOver
                // synchronously from inside Apply on a session-gap roll. _buffer and
                // _head belong to whichever call did that now, not to this loop, so
                // bail out immediately rather than letting a LATER consumer in this
                // same iteration run against state an EARLIER one just reset —
                // ObserveRawLine could otherwise repopulate a just-reset SessionStats
                // with the very line whose own processing triggered the reset.
                if (evt is not null)
                {
                    if (TeammateFeed.AdmitForStats(evt))
                    {
                        _stats.Apply(evt);
                        if (_generation != generation) return;
                    }
                    if (TeammateFeed.AdmitForMez(evt))
                    {
                        _mez()?.Apply(evt);
                        if (_generation != generation) return;
                    }
                }
                _stats.ObserveRawLine(lineTs, msg);
                if (_generation != generation) return;
            }
        }
        catch (IOException ex)
        {
            // PollPrimary's catch (IOException) means "the log file is busy, retry next
            // tick" and records nothing — so an IOException escaping a CONSUMER (a
            // ledger's disk write, a subscriber) would read as a healthy poll and let
            // Poll() drain the rest of this batch past a primary chunk that gave up
            // early. Re-raise it as a non-IO failure so PollPrimary's catch (Exception)
            // records it and Poll() discards the remainder. (The same hole for a
            // consumer throwing inside PollPrimary's own dispatch is upstream's shape
            // and is left alone.)
            throw new InvalidOperationException("A teammate-line consumer threw an IOException.", ex);
        }
        finally
        {
            // Skip the write-back entirely when a re-entrant Reset()/Discard() ran
            // during the loop (whether it returned early above or the callback threw
            // straight through): _head and _buffer already reflect THAT call's idea of
            // where things stand, and stomping `_head = end` here would either point
            // past a cleared buffer or resurrect entries a Discard() meant to drop.
            // Compact() is skipped for the same reason — RemoveRange(0, _head) against
            // a buffer that generation no longer matches is exactly the out-of-range
            // throw (or silent wrong-splice) this guard exists to prevent.
            if (_generation == generation)
            {
                _head = end;
                Compact();
            }
        }
    }

    /// <summary>Reclaims the dead prefix left behind as <see cref="DrainBefore"/> advances
    /// <see cref="_head"/>, without paying an O(n) shift on every drain. Two triggers: the
    /// buffer is fully drained (the common case, once per poll) — Clear it outright and
    /// drop the backing array if it had grown large; or the dead prefix has grown past
    /// roughly half the buffer — RemoveRange it in one shot rather than letting it grow
    /// unbounded. Either way this runs O(log n) times total across a whole replay, not
    /// once per line.</summary>
    private void Compact()
    {
        if (_head == _buffer.Count)
        {
            _buffer.Clear();
            _head = 0;
            if (_buffer.Capacity > 4096) _buffer.TrimExcess();
        }
        else if (_head > 1024 && _head > _buffer.Count / 2)
        {
            _buffer.RemoveRange(0, _head);
            _head = 0;
        }
    }

    /// <summary>A primary replay restarts the session from byte 0, so the teammate
    /// file must replay from the top too, or its already-dispatched half of the
    /// session would linger while the primary's half starts over.</summary>
    public void Reset()
    {
        _offset = 0;
        _remainder.Clear();
        _buffer.Clear();
        _head = 0;
        _generation++;
    }

    /// <summary>Drops whatever is still buffered and undispatched, WITHOUT rewinding
    /// <see cref="_offset"/> or <see cref="_remainder"/> — unlike <see cref="Reset"/>,
    /// which replays the file from the top, this is for a poll LogWatcher has already
    /// decided to abandon (PollPrimary swallowed a consumer throw on the primary side).
    /// Those bytes have already been read off disk and split into lines; the fact that
    /// nothing consumed them yet doesn't make them re-readable, so they're dropped
    /// exactly like the primary's own abandoned chunk — never re-buffered, never
    /// re-fetched from the file. Without this, a poisoned poll left the trailing lines
    /// buffered instead of drained, and the NEXT poll's trailing
    /// <c>DrainBefore(DateTime.MaxValue)</c> — often after initial ingest has already
    /// flipped <see cref="LogWatcher.InitialIngestDone"/> — dispatched them as if they
    /// had just arrived live.</summary>
    public void Discard()
    {
        _buffer.Clear();
        _head = 0;
        _generation++;
    }
}
