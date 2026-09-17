using System.Text;

namespace EQBuddy.Core;

/// <summary>
/// Everything a teammate's log needs — reading its new bytes, splitting them into
/// timestamped lines, holding them until the primary log catches up, applying them to
/// the teammate's OWN isolated session, and feeding the shared mez tracker (see
/// <see cref="TeammateFeed"/>) — lives HERE rather than inside <see cref="LogWatcher"/>.
/// LogWatcher.Poll() (upstream's, renamed PollPrimary) is a file this fork's upstream
/// edits every few days; keeping the teammate feature in its own class means
/// LogWatcher's diff against upstream stays a handful of added lines plus one hook
/// call, instead of a rewritten Poll body that has to be re-merged by hand on every
/// upstream change.
///
/// <b>The isolation invariant, and why it makes contamination impossible BY
/// CONSTRUCTION rather than by remembering to gate each call site:</b> three rounds
/// of gating individual <see cref="SessionStats.Apply"/> call sites behind a
/// <c>fromTeammate</c> flag each closed the named leaks an audit found, and each
/// following audit found the same class of bug in a new place the flag had not
/// reached yet. <see cref="Stats"/> is a plain <c>new SessionStats()</c> — the
/// trivial constructor, no file I/O, no ambient lookups, no registration anywhere —
/// and NOTHING in this class, or anywhere else in the codebase, may ever:
///   - attach a durable store (<c>AaStore</c>, <c>QuestStore</c>, <c>StackingStore</c>,
///     <c>InventoryDumpResolver</c>) to it,
///   - call <c>Spells.AttachStore</c> or <c>RefreshTextPatterns</c> on it,
///   - subscribe to any of its events (<c>TextMatched</c>, <c>OutputfileWritten</c>,
///     <c>SessionRolledOver</c>) — see below for <c>SessionEnding</c>'s one exception,
///   - or hand its snapshot to the archiver, the checkpoint writer, or the Mobile wire.
/// Every one of those resources is an OPT-IN property only <c>MainWindow</c> ever sets
/// on the PRIMARY instance — never touching <see cref="Stats"/> here is what makes
/// durable isolation free: no code path exists that COULD leak the teammate's data
/// into the watched character's ledgers, learning, or history, because nothing here
/// ever hands it the keys. Every parsed event reaches it via <see cref="Stats"/>.
/// <c>Apply(evt)</c> UNCONDITIONALLY — no gate, no flag — precisely because there is
/// nothing left for a flag to protect.
///
/// <see cref="ObserveRawLine"/> is the one DISPATCH exception, and it is a genuine
/// exception, not a gap in the invariant above: it is never called on
/// <see cref="Stats"/> at all (see <see cref="DrainBefore"/>'s own doc).
///
/// <b><c>SessionEnding</c> is the one SUBSCRIPTION exception (repair round A2's
/// carry-forward), and the direction is what keeps it safe:</b> the PRIMARY's
/// <c>DuoCompanion.cs</c> subscribes ITS OWN <c>OnCompanionSessionEnding</c> to
/// <c>Stats.SessionEnding</c> the moment <c>SessionStats.Companion</c> is assigned to
/// this tail's <see cref="Stats"/>. This instance only ever FIRES the event — it is
/// never handed a callback that could read from, or write into, anything of the
/// primary's — so it is not a channel the primary's data (or anything else) could
/// leak back through. It exists so a 60-minute gap in only the teammate's log, which
/// still autonomously rolls THEIR session (that internal roll lives in the
/// budget-locked SessionStats.cs and cannot be suppressed), does not silently drop
/// their pre-roll contribution from the duo total the moment it fires.
/// <see cref="TeammateIsolationTests.TeammateStatsCarriesNoDurableStoreAndNoSubscriber"/>
/// narrows this to EXACTLY that one method rather than waving the whole event
/// through.
///
/// <b>Known limitation (pre-existing from the first implementation, out of scope
/// here):</b> ordering is per-poll. If the sync tool that copies a teammate's log
/// delivers a burst of their lines LATE — more than 60 minutes after the fact — while
/// you kept playing, the teammate's OWN forward-only gap roll can fire on their
/// internal gap between their last-synced line and the late burst, rolling THEIR
/// session on a gap that was never really there in wall-clock time. Isolation means
/// this can no longer wipe the WATCHED character's session (the historical bug this
/// limitation note originally described) — only the teammate's own totals, which is a
/// much smaller blast radius. Noted here so nobody rediscovers it as a new bug.
/// </summary>
public sealed class TeammateLogTail
{
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

    /// <summary>Repair round C8: the last time THIS teammate's own log self-reported
    /// killing each target ("You have slain X!"), keyed by target name only —
    /// overwritten on each new kill of the same name rather than kept as a full
    /// history, since this exists only to feed <see cref="ClockDriftEstimator"/> a
    /// rough sample, not to run an exact join. Deliberately does not attempt to
    /// correlate a PET-delivered kill (the teammate's own log reports those as
    /// "&lt;pet&gt; has slain X!", not "You have slain X!") — scoped to the shape
    /// that is easy to get right rather than chasing every kill line the log can
    /// produce. Read by <see cref="LogWatcher"/>'s own promoted-kill dispatch, on
    /// the same single poll thread that writes it below, so no lock is needed here
    /// any more than elsewhere in this class.</summary>
    private readonly Dictionary<string, DateTime> _ownKillTimestampsByTarget = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>See <see cref="_ownKillTimestampsByTarget"/>'s own doc. Null when this
    /// target has never been self-killed in this teammate's log (or only ever by
    /// their pet).</summary>
    internal DateTime? LastOwnKillTimestamp(string target) =>
        _ownKillTimestampsByTarget.TryGetValue(target, out var ts) ? ts : null;

    public TeammateLogTail(string path, Func<MezTracker?> mez)
    {
        Path = path;
        _mez = mez;
        var info = CharacterLog.FromPath(path);
        Character = info?.Character;
        // The trivial constructor (Part 1a): no file I/O, no ambient lookups, no
        // registration. See this class's own doc for the invariant this instance
        // must never violate — no store, no subscriber, ever.
        Stats = new SessionStats { CharacterName = info?.Character, ServerName = info?.Server };
    }

    public string Path { get; }
    public string? Character { get; }

    /// <summary>Repair round A2 (part i): the primary's CURRENT session start, read
    /// fresh on every dispatch — mirrors the existing <c>LogWatcher.LogFolder</c>
    /// pattern rather than a value captured once at construction, because a mid-
    /// session pick's whole point is that the primary's own session already exists
    /// when this tail starts its initial full-file ingest. Anything the teammate's
    /// log carries strictly before this bound is a session of theirs that predates
    /// yours entirely — two hours of their unrelated kills must not get averaged into
    /// five minutes of your own DPS — so <see cref="DrainBefore"/> rejects it outright
    /// rather than letting it land on <see cref="Stats"/>.
    ///
    /// <b>Repair round C2:</b> a WIRED accessor that currently answers null (the
    /// primary hasn't applied its first event yet — a teammate picked before your
    /// own log, an ordinary startup order) HOLDS all draining rather than admitting
    /// everything: the old "null admits" reading let anything with an EARLIER
    /// timestamp than the bound slip through unfiltered during the exact window the
    /// bound didn't exist yet to reject against, and it stayed in <see cref="Stats"/>
    /// permanently once the bound later appeared. Nothing this holds is lost — the
    /// same buffered lines are re-offered, correctly filtered, the next time
    /// <see cref="DrainBefore"/> runs after the primary's first event sets a real
    /// bound. This accessor being null OUTRIGHT (never wired at all — a caller with
    /// no gating concept, or a test) is a different condition and admits
    /// everything, unchanged from before.</summary>
    public Func<DateTime?>? PrimarySessionStart { get; set; }

    /// <summary>The teammate's own, fully isolated session — see this class's doc for
    /// the invariant that makes it safe to apply every parsed event unconditionally.
    /// Never attach a store, never subscribe an event, never hand its snapshot to the
    /// archiver or the Mobile wire.</summary>
    public SessionStats Stats { get; }
    internal SessionStats? PrimaryStats { get; set; }
    private KillEvent? _pendingPrimaryKill;

    /// <summary>The sole primary-poll extension: finish accounting for the previous
    /// primary line (which upstream has now applied), drain earlier teammate lines,
    /// then remember this primary line for the next hook or trailing drain.</summary>
    public void DrainBefore(DateTime ts, string primaryMessage)
    {
        DrainBefore(ts);
        _pendingPrimaryKill = LogParser.Parse(ts, primaryMessage) as KillEvent;
    }

    private void CompletePrimaryLine()
    {
        var kill = _pendingPrimaryKill;
        _pendingPrimaryKill = null;
        if (kill is null || PrimaryStats is not { } primary || kill.Killer == "You"
            || primary.IsMyPet(kill.Killer)) return;
        var ownKill = Character is { Length: > 0 }
            && kill.Killer.Equals(Character, StringComparison.OrdinalIgnoreCase);
        var petKill = Stats.LivePetName is { Length: > 0 } pet
            && kill.Killer.Equals(pet, StringComparison.OrdinalIgnoreCase);
        if (!ownKill && !petKill) return;
        primary.RecordTeammatePartyKill(kill.Target);
        if (ownKill) primary.ObservePrimaryTeammateKill(kill.Target, kill.Time);
    }
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
    /// Dispatch: parse the line, hand the event to <see cref="Stats"/> UNCONDITIONALLY
    /// (Step 3 — no gate, no flag; see this class's own doc for why that is safe by
    /// construction), then to the mez tracker when
    /// <see cref="TeammateFeed.AdmitForMez"/> allows it, tagged with this teammate's own
    /// name so a cast/worn-off/fizzle is attributed to (and only cancellable or
    /// end-able by) THEM. Deliberately never calls <see cref="SessionStats.ObserveRawLine"/>
    /// (finding 5): the two logs carry every bystander-visible world/chat line VERBATIM
    /// when you play together, so feeding a teammate's copy through the time-critical
    /// Text-watch path fired one raid-call alert twice per occurrence and filled the
    /// recent-lines ring with duplicates — your own log already gives ObserveRawLine
    /// everything it needs, which is exactly what <see cref="TeammateFeed"/>'s class doc
    /// claims this feature skips.</summary>
    public void DrainBefore(DateTime ts)
    {
        CompletePrimaryLine();
        // Repair round C2: HOLD draining entirely — not just individual lines —
        // while the primary's own session-start bound is still unknown. The old
        // per-line reject (still below, for once the bound DOES exist) could not
        // reject anything before a bound existed to compare against, so every line
        // offered during that window — including a teammate kill from hours before
        // the primary's log even existed — sailed through unfiltered. Nothing here
        // is DROPPED: the buffer and _head are untouched, so the exact same lines
        // are re-offered, correctly filtered, the next time this runs after the
        // bound is established (LogWatcher polls every 150ms, and the primary
        // logging its first event is what sets PrimarySessionStart.Invoke() non-null
        // — ordinarily seconds away, not indefinite). `PrimarySessionStart` itself
        // being null (never wired at all, e.g. a caller with no gating concept) is
        // a DIFFERENT condition from it being wired and currently answering null —
        // only the latter holds.
        if (PrimarySessionStart is not null && PrimarySessionStart() is null) return;

        int end = _head;
        while (end < _buffer.Count && _buffer[end].Ts < ts) end++;
        if (end == _head) return;

        // Captured before the loop touches anything: see the generation's own doc for
        // why a re-entrant Reset()/Discard() from inside a consumer callback below must
        // be detectable both mid-loop (the early-return check) and in the finally.
        int generation = _generation;

        // try/finally, not a plain loop: a consumer throwing mid-batch (SessionStats,
        // or the mez tracker) must not leave the already-dispatched lines re-visitable
        // on the next call. Matching PollPrimary's own containment, the WHOLE bound batch
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
                // Checked after EACH of the two consumer calls below, not just once at
                // the bottom: either one can re-entrantly call back into Reset() or
                // Discard() — e.g. SessionStats itself raising SessionRolledOver
                // synchronously from inside Apply on a session-gap roll, or a
                // TextMatched subscriber the mez tracker's own consumers reach. _buffer
                // and _head belong to whichever call did that now, not to this loop, so
                // bail out immediately rather than letting the LATER consumer in this
                // same iteration run against state the EARLIER one just reset.
                if (evt is not null)
                {
                    // Clock evidence must survive the session-start filter: a clock
                    // behind ours can put the matching kill before that raw bound.
                    if (evt is KillEvent { Killer: "You" } clockKill)
                        PrimaryStats?.ObserveTeammateOwnKill(clockKill.Target, clockKill.Time);
                    // Repair round A2 (part i): reject anything strictly earlier than
                    // the primary's OWN current session start — see PrimarySessionStart's
                    // own doc. The mez feed below is deliberately NOT gated by this:
                    // the audit's fix names Stats.Apply specifically, and a stale mez
                    // chip from before the primary's session existed is bounded by the
                    // tracker's own AwakeMemory/CastToLand windows regardless.
                    if (PrimarySessionStart?.Invoke() is { } bound && evt.Time < bound)
                        continue;
                    Stats.Apply(evt);
                    if (_generation != generation) return;
                    // Repair round C8: record this teammate's own self-reported kill
                    // timestamp, keyed by target — LogWatcher's promoted-kill dispatch
                    // (the primary's own bystander view of the same kill) joins against
                    // this to sample the clock offset between the two logs. See
                    // _ownKillTimestampsByTarget's own doc for the pet-kill scoping.
                    if (evt is KillEvent { Killer: "You" } selfKill)
                        _ownKillTimestampsByTarget[selfKill.Target] = evt.Time;
                    if (TeammateFeed.AdmitForMez(evt))
                    {
                        // CharacterLog.FromPath returns null for a renamed synced file
                        // (e.g. mid-transfer or a nonstandard name) — a stable sentinel
                        // keeps cast/worn-off caster matching self-consistent even then.
                        _mez()?.Apply(evt, Character ?? "(teammate)");
                        if (_generation != generation) return;
                    }
                }
                // Deliberately no ObserveRawLine call here (finding 5) — see the
                // DrainBefore doc comment above.
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
    /// session would linger while the primary's half starts over — and because
    /// <see cref="Stats"/> now belongs to THIS tail rather than being shared, restarting
    /// its replay without also restarting <see cref="Stats"/> would double every duo
    /// total on the next full-file replay. A primary re-Select (character switch, or an
    /// explicit re-pick of the same file) is exactly when "duo totals restart with it"
    /// should hold.</summary>
    public void Reset()
    {
        _pendingPrimaryKill = null;
        _ownKillTimestampsByTarget.Clear();
        _offset = 0;
        _remainder.Clear();
        _buffer.Clear();
        _head = 0;
        _generation++;
        Stats.ClearCharacterState();
        Stats.Reset();
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
        _pendingPrimaryKill = null;
        _buffer.Clear();
        _head = 0;
        _generation++;
    }
}

/// <summary>
/// Repair round C10: a READ-ONLY view over a teammate's isolated <see cref="SessionStats"/>,
/// wrapping the mutable instance instead of handing it out directly. The isolation
/// invariant this class's own doc describes ("never attach a store, never subscribe an
/// event") was, until this round, enforced only by convention on a fully public,
/// fully mutable reference — <see cref="LogWatcher.TeammateStats"/> handed callers the
/// real <see cref="SessionStats"/>, and nothing in the type system stopped some future
/// call site from doing <c>w.TeammateStats.AaStore = someStore</c> the moment it
/// compiled, the exact leak three prior repair rounds spent gating call sites against.
/// Exposes exactly what a legitimate consumer needs to READ — a snapshot, the version
/// gate, the character name — and nothing that could attach a store, subscribe an
/// event, or otherwise durable-ize an instance that must stay a throwaway. Tests that
/// need the real mutable instance (to prove NOTHING is attached, or to exercise a
/// reentrancy hazard by subscribing to its events on purpose) reach it through
/// <see cref="LogWatcher.TeammateStatsForTests"/> instead, never through this wrapper.
/// </summary>
public sealed class ReadOnlyTeammateStats
{
    private readonly SessionStats _inner;
    internal ReadOnlyTeammateStats(SessionStats inner) => _inner = inner;

    /// <inheritdoc cref="SessionStats.Snapshot()"/>
    public StatsSnapshot Snapshot() => _inner.Snapshot();

    /// <inheritdoc cref="SessionStats.Snapshot(TimeSpan?, IReadOnlyList{TrackedRule}?)"/>
    public StatsSnapshot Snapshot(TimeSpan? recentWindow, IReadOnlyList<TrackedRule>? rules) =>
        _inner.Snapshot(recentWindow, rules);

    /// <inheritdoc cref="SessionStats.CurrentVersion"/>
    public long CurrentVersion => _inner.CurrentVersion;

    /// <inheritdoc cref="SessionStats.CharacterName"/>
    public string? CharacterName => _inner.CharacterName;
}
