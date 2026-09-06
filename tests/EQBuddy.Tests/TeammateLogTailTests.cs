using System.Diagnostics;
using System.Globalization;
using System.Text;
using EQBuddy.Core;

namespace EQBuddy.Tests;

/// <summary>
/// TeammateLogTail in isolation — see LogWatcherTests for the end-to-end version (a
/// teammate's log actually joining a session through LogWatcher).
/// </summary>
public class TeammateLogTailTests
{
    private static string WriteLog(string dir, string name, params string[] lines)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void FillThenDrainDispatchesInTimestampOrderUpToTheBound()
    {
        var dir = Directory.CreateTempSubdirectory("eqbuddy-tail-").FullName;
        try
        {
            var path = WriteLog(dir, "eqlog_Buddy_freeport.txt",
                "[Sat Jul 18 20:02:00 2026] You have slain orc guard!",
                "[Sat Jul 18 20:04:00 2026] You have slain orc sentry!",
                "[Sat Jul 18 20:06:00 2026] You have slain orc centurion!");

            var stats = new SessionStats();
            var tail = new TeammateLogTail(path, stats, () => null);

            Assert.True(tail.Fill());

            // Strict bound: a drain to 20:04 dispatches ONLY lines stamped strictly
            // before it, so the 20:04 line itself stays buffered (see
            // ALineStampedExactlyAtTheBoundStaysBuffered) and only the 20:02 kill lands.
            tail.DrainBefore(new DateTime(2026, 7, 18, 20, 4, 0));
            var snap = stats.Snapshot();
            Assert.Equal(1, snap.YourKillCount);
            Assert.Equal(new DateTime(2026, 7, 18, 20, 2, 0), snap.LastEventTime);

            tail.DrainBefore(DateTime.MaxValue);
            Assert.Equal(3, stats.Snapshot().YourKillCount);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void ALineStampedExactlyAtTheBoundStaysBuffered()
    {
        // EQ log stamps are 1-second granular, so ties between the primary and a
        // teammate's line are common in a duo. DrainBefore uses a STRICT bound so a
        // tie resolves primary-first: a line stamped exactly at the bound must wait
        // for a strictly-later bound (or the trailing MaxValue drain), never dispatch
        // as part of the call that names its own timestamp.
        var dir = Directory.CreateTempSubdirectory("eqbuddy-tail-").FullName;
        try
        {
            var path = WriteLog(dir, "eqlog_Buddy_freeport.txt",
                "[Sat Jul 18 20:02:00 2026] You have slain orc guard!",
                "[Sat Jul 18 20:04:00 2026] You have slain orc sentry!");

            var stats = new SessionStats();
            var tail = new TeammateLogTail(path, stats, () => null);
            Assert.True(tail.Fill());

            tail.DrainBefore(new DateTime(2026, 7, 18, 20, 4, 0));
            Assert.Equal(1, stats.Snapshot().YourKillCount);   // only the strictly-earlier line

            tail.DrainBefore(DateTime.MaxValue);
            Assert.Equal(2, stats.Snapshot().YourKillCount);   // the boundary line now lands
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void DrainBeforeStaysLinearNotQuadratic()
    {
        // Regression for the O(n^2) DrainBefore: the old implementation removed
        // dispatched entries from the FRONT of the list on every call, which is O(n) in
        // whatever remains — and the hook runs once per primary line, so replaying two
        // 200k-line logs measured ~17s vs ~3s. This drains one line at a time (the exact
        // shape that made the bug quadratic).
        //
        // A fixed absolute bound (the old version of this test: n=20,000 under a
        // 2,000 ms budget) CANNOT FAIL on a fast machine — the linear implementation
        // finishes in ~130 ms, so the bound proves nothing about whether the O(n^2)
        // code came back. A RATIO discriminates instead — but every line here also
        // pays SessionStats.Apply/ObserveRawLine's own per-line cost (journal
        // append+periodic prune, active-play buckets, mob lookup), which is real work
        // this fix does not own and must not touch, and which itself does not scale
        // perfectly flat with n. Measured directly (no TeammateLogTail involved at
        // all): that shared pipeline alone already costs ~5.7x at 4x input and ~8.4x
        // at 6x input, not 1x — so a small input multiplier leaves too little room
        // between "linear DrainBefore riding that baseline" and "quadratic DrainBefore
        // riding the same baseline" to be a reliable pass/fail line, and it measured
        // flaky in practice (a 4x/threshold-8 version of this test intermittently
        // failed against the CORRECT implementation, ~8.0-8.3 against an 8.0 cutoff).
        // A larger 6x multiplier (20,000 -> 120,000) widens the gap enough to be
        // reliable: this implementation lands ~14 (many runs, low variance), a
        // reverted RemoveRange-per-call implementation lands ~28-33 — roughly double,
        // with a comfortable margin either side of the 20 cutoff below. A warm-up run
        // at the smaller size settles JIT before either measured run.
        Time(20_000);   // warm-up — discarded
        double tBase = Time(20_000);
        double tBig = Time(120_000);
        double ratio = tBig / Math.Max(1.0, tBase);

        Assert.True(ratio < 20,
            $"120k/20k drain-time ratio was {ratio:F2} (20k={tBase:F1} ms, 120k={tBig:F1} ms) — " +
            "expected well under 20 for this implementation (observed ~14 across many runs); " +
            "a reverted RemoveRange-per-call implementation measures ~28-33.");

        // One file + tail per size, timing only the one-line-at-a-time drain loop —
        // the exact shape that made the old bug quadratic.
        static double Time(int n)
        {
            var dir = Directory.CreateTempSubdirectory("eqbuddy-tail-").FullName;
            try
            {
                var start = new DateTime(2026, 7, 18, 20, 0, 0);
                var timestamps = new DateTime[n];
                var lines = new string[n];
                for (int i = 0; i < n; i++)
                {
                    timestamps[i] = start.AddSeconds(i);
                    lines[i] = $"[{timestamps[i].ToString("ddd MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture)}] You have slain orc pawn!";
                }
                var path = Path.Combine(dir, "eqlog_Buddy_freeport.txt");
                File.WriteAllLines(path, lines);

                var stats = new SessionStats();
                var tail = new TeammateLogTail(path, stats, () => null);
                Assert.True(tail.Fill());

                var sw = Stopwatch.StartNew();
                // Advance the bound one line at a time, mirroring PollPrimary calling
                // DrainBefore once per primary line during a full-file ingest.
                for (int i = 0; i < n; i++)
                    tail.DrainBefore(timestamps[i].AddTicks(1));
                sw.Stop();

                Assert.Equal(n, stats.Snapshot().YourKillCount);
                return sw.Elapsed.TotalMilliseconds;
            }
            finally { try { Directory.Delete(dir, recursive: true); } catch { } }
        }
    }

    [Fact]
    public void ALatin1ByteDecodesAsExtendedAscii()
    {
        // Fill decodes with Encoding.Latin1, same as upstream's primary-log read, so a
        // raw byte 0xE9 in a mob/item name must come back as 'é' rather than mangled or
        // rejected — this is a duo player's log, encoded exactly like the primary's.
        var dir = Directory.CreateTempSubdirectory("eqbuddy-tail-").FullName;
        try
        {
            var path = Path.Combine(dir, "eqlog_Buddy_freeport.txt");
            var line = "[Sat Jul 18 20:02:00 2026] --You have looted a Café Ring from orc guard's corpse.--\n";
            File.WriteAllBytes(path, Encoding.Latin1.GetBytes(line));

            var stats = new SessionStats();
            var tail = new TeammateLogTail(path, stats, () => null);

            Assert.True(tail.Fill());
            tail.DrainBefore(DateTime.MaxValue);

            var snap = stats.Snapshot();
            Assert.Contains(snap.Loot, l => l.Item.Contains('é'));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void ResetReplaysFromTheTop()
    {
        // The old version of this test passed even with a Reset() that only rewound
        // _offset, leaving _buffer and _head untouched: Fill() APPENDS newly split
        // lines onto whatever is already buffered, so re-reading from byte 0 onto a
        // buffer nobody cleared would just add the whole file a second time on top of
        // it. This version proves both halves: it drains PARTIALLY before Reset() (so
        // something is genuinely left buffered — undispatched — when Reset() hits),
        // and it ends the file in an UNTERMINATED split line (so _remainder is
        // non-empty at Reset() time too), then checks the exact count survives the
        // replay with neither the buffered lines nor the remainder double-applied.
        var dir = Directory.CreateTempSubdirectory("eqbuddy-tail-").FullName;
        try
        {
            var path = Path.Combine(dir, "eqlog_Buddy_freeport.txt");
            File.WriteAllText(path,
                "[Sat Jul 18 20:02:00 2026] You have slain orc guard!\n" +
                "[Sat Jul 18 20:04:00 2026] You have slain orc sentry!\n" +
                "[Sat Jul 18 20:06:00 2026] You have slain orc centurion!\n" +
                "[Sat Jul 18 20:08:00 2026] You have sla");   // unterminated — no trailing newline

            var stats = new SessionStats();
            var tail = new TeammateLogTail(path, stats, () => null);

            Assert.True(tail.Fill());
            // Strict bound: only the 20:02 line is before it, so the 20:04 and 20:06
            // lines stay buffered — undispatched — right when Reset() is about to hit.
            tail.DrainBefore(new DateTime(2026, 7, 18, 20, 4, 0));
            Assert.Equal(1, stats.Snapshot().YourKillCount);

            tail.Reset();
            Assert.True(tail.Fill());
            tail.DrainBefore(DateTime.MaxValue);
            // (lines drained before Reset) + (every complete line in the file,
            // replayed exactly once from the top) = 1 + 3. An offset-only Reset would
            // instead double-dispatch the 20:04/20:06 lines that were left buffered
            // (they'd be applied once before Reset and once more after), landing on 6.
            Assert.Equal(4, stats.Snapshot().YourKillCount);

            // The unterminated trailing line still hasn't split — completing it now
            // must dispatch it exactly once: not zero (lost with the old remainder)
            // and not twice (re-spliced against stale buffered state).
            File.AppendAllText(path, "in orc pawn!\n");
            Assert.True(tail.Fill());
            tail.DrainBefore(DateTime.MaxValue);
            Assert.Equal(5, stats.Snapshot().YourKillCount);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void MissingFileIsQuietlyEmpty()
    {
        var stats = new SessionStats();
        var tail = new TeammateLogTail(@"C:\nope\eqlog_Buddy_freeport.txt", stats, () => null);

        Assert.False(tail.Fill());
        Assert.Null(tail.LastError);
    }

    [Fact]
    public void FillReturnsFalseWhenNothingNew()
    {
        var dir = Directory.CreateTempSubdirectory("eqbuddy-tail-").FullName;
        try
        {
            var path = WriteLog(dir, "eqlog_Buddy_freeport.txt",
                "[Sat Jul 18 20:02:00 2026] You have slain orc guard!");

            var stats = new SessionStats();
            var tail = new TeammateLogTail(path, stats, () => null);

            Assert.True(tail.Fill());
            Assert.False(tail.Fill());   // nothing new since the first read
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void AConsumerThrowMidDrainDoesNotRedispatchOnTheNextCall()
    {
        // Regression: DrainBefore used to trim the buffer only up to the poisoned line,
        // so a throw left every line buffered AFTER it re-visitable — the next call
        // (Poll's own trailing DrainBefore(MaxValue), or the next tick) re-applied the
        // first line a second time, and a line buffered past the thrower within the
        // SAME bound could survive to fire alerts on some later poll as if it had just
        // arrived live. Upstream's own containment abandons the WHOLE batch on a
        // throw, so this must match: nothing in this bound survives, thrower or not.
        var dir = Directory.CreateTempSubdirectory("eqbuddy-tail-").FullName;
        try
        {
            var path = WriteLog(dir, "eqlog_Buddy_freeport.txt",
                "[Sat Jul 18 20:02:00 2026] You have slain orc guard!",             // KillEvent — not admitted for mez
                "[Sat Jul 18 20:04:00 2026] You slash orc pawn for 10 points of damage.", // DamageDealtEvent — IS, throws
                "[Sat Jul 18 20:06:00 2026] You have slain orc centurion!");        // AFTER the thrower — must not survive

            var stats = new SessionStats();
            var tail = new TeammateLogTail(path, stats, () => throw new InvalidOperationException());

            Assert.True(tail.Fill());
            Assert.Throws<InvalidOperationException>(() => tail.DrainBefore(DateTime.MaxValue));
            Assert.Equal(1, stats.Snapshot().YourKillCount);   // the kill line applied before the throw

            // Neither the poisoned line nor the kill line buffered AFTER it may linger:
            // a second call must not throw again, and must not apply either kill.
            tail.DrainBefore(DateTime.MaxValue);
            Assert.Equal(1, stats.Snapshot().YourKillCount);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void AnIOExceptionFromAConsumerIsRethrownAsANonIOFailure()
    {
        // PollPrimary's catch (IOException) means "log file busy, retry next tick" and
        // records nothing. A consumer's IOException escaping DrainBefore unchanged
        // would therefore read as a HEALTHY poll, and Poll() would drain the rest of
        // the teammate batch past a primary chunk that gave up early. The tail must
        // re-raise it as a non-IO failure so PollPrimary's catch (Exception) records
        // it and Poll() discards the remainder; the batch-drop containment still holds.
        var dir = Directory.CreateTempSubdirectory("eqbuddy-tail-").FullName;
        try
        {
            var path = WriteLog(dir, "eqlog_Buddy_freeport.txt",
                "[Sat Jul 18 20:02:00 2026] You have slain orc guard!",
                "[Sat Jul 18 20:04:00 2026] You slash orc pawn for 10 points of damage.", // admitted for mez — throws
                "[Sat Jul 18 20:06:00 2026] You have slain orc centurion!");

            var stats = new SessionStats();
            var tail = new TeammateLogTail(path, stats, () => throw new IOException("ledger write failed"));

            Assert.True(tail.Fill());
            var ex = Assert.Throws<InvalidOperationException>(() => tail.DrainBefore(DateTime.MaxValue));
            Assert.IsType<IOException>(ex.InnerException);
            Assert.Equal(1, stats.Snapshot().YourKillCount);

            tail.DrainBefore(DateTime.MaxValue);              // the whole batch was dropped
            Assert.Equal(1, stats.Snapshot().YourKillCount);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void DiscardDropsBufferedLinesButKeepsOffsetAndRemainder()
    {
        // Discard() is for a poll LogWatcher has already decided to abandon (the
        // primary side threw and swallowed it) — the bytes were genuinely read off
        // disk, so unlike Reset() the read position must NOT rewind, or the next Fill
        // would re-read and re-split lines that were already accounted for.
        var dir = Directory.CreateTempSubdirectory("eqbuddy-tail-").FullName;
        try
        {
            var path = Path.Combine(dir, "eqlog_Buddy_freeport.txt");
            File.WriteAllText(path,
                "[Sat Jul 18 20:02:00 2026] You have slain orc guard!\n" +
                "[Sat Jul 18 20:04:00 2026] You have slain orc sentry!\n" +
                "[Sat Jul 18 20:06:00 2026] You have sla");   // unterminated split line

            var stats = new SessionStats();
            var tail = new TeammateLogTail(path, stats, () => null);

            Assert.True(tail.Fill());   // buffers 2 complete lines; the split one waits in _remainder
            tail.Discard();
            tail.DrainBefore(DateTime.MaxValue);
            Assert.Equal(0, stats.Snapshot().YourKillCount);   // both buffered lines were dropped, not just deferred

            // Offset kept: no new bytes to read, so Fill reports nothing new rather
            // than re-splitting the same two lines a second time.
            Assert.False(tail.Fill());

            // Remainder kept: completing the split line still finishes it, proving
            // Discard() did not clear the in-flight partial line either.
            File.AppendAllText(path, "in orc centurion!\n");
            Assert.True(tail.Fill());
            tail.DrainBefore(DateTime.MaxValue);
            Assert.Equal(1, stats.Snapshot().YourKillCount);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void AReentrantResetDuringDrainLeavesNoStaleState()
    {
        // Regression: Monitor is re-entrant, so a consumer callback invoked from
        // inside DrainBefore (SessionStats, the mez tracker, or a TextMatched
        // subscriber) can synchronously call back into LogWatcher.Select or
        // SelectTeammate, which Resets this tail mid-drain. Without a generation
        // guard, the finally clause would overwrite the just-cleared buffer with a
        // stale `_head = end` — pointing past a buffer Reset() just cleared to zero —
        // and a later Compact() could RemoveRange past the (now much shorter) list's
        // Count, or silently mis-splice the replay. This drives it the simpler way
        // the fix's own note allows: Reset() called from inside a throwing handler.
        var dir = Directory.CreateTempSubdirectory("eqbuddy-tail-").FullName;
        try
        {
            var path = WriteLog(dir, "eqlog_Buddy_freeport.txt",
                "[Sat Jul 18 20:02:00 2026] You have slain orc guard!",
                "[Sat Jul 18 20:04:00 2026] You slash orc pawn for 10 points of damage.",
                "[Sat Jul 18 20:06:00 2026] You have slain orc centurion!");

            var stats = new SessionStats();
            var resetDone = false;
            TeammateLogTail? tail = null;
            tail = new TeammateLogTail(path, stats, () =>
            {
                if (!resetDone)
                {
                    resetDone = true;
                    // Mirrors what a real re-entrant Select() does: the whole session,
                    // stats included, is torn down and replayed from scratch mid-drain.
                    stats.Reset();
                    tail!.Reset();
                    throw new InvalidOperationException();
                }
                return null;
            });

            Assert.True(tail.Fill());
            Assert.Throws<InvalidOperationException>(() => tail.DrainBefore(DateTime.MaxValue));

            // The re-entrant Reset() must win: no stale _head write, no Compact() over
            // a buffer generation no longer matches, and the kill from before the
            // throw was wiped by the SAME reset that cleared the tail.
            Assert.Equal(0, stats.Snapshot().YourKillCount);

            // A subsequent Fill + DrainBefore(MaxValue) must replay the file exactly
            // once from the top, with no exception and no double-dispatch.
            Assert.True(tail.Fill());
            tail.DrainBefore(DateTime.MaxValue);
            Assert.Equal(2, stats.Snapshot().YourKillCount);   // the file's two kills, once each
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void AReentrantResetTriggeredFromStatsApplyStopsTheLaterConsumersInTheSameLine()
    {
        // Regression: the generation check used to run ONCE, at the bottom of the
        // loop body (after ObserveRawLine). SessionStats.SessionRolledOver fires
        // SYNCHRONOUSLY from inside _stats.Apply(evt) itself (a >60-minute forward
        // gap rolls the session before Apply returns) — a re-entrant Reset() from
        // that callback returns NORMALLY, with no exception to unwind through, so a
        // check only at the bottom would let the SAME iteration go on to call
        // ObserveRawLine against a SessionStats the very call above it just reset.
        // The fix checks generation immediately after EACH of the three consumer
        // calls, so this line's own Apply(evt) catches the reset itself.
        var dir = Directory.CreateTempSubdirectory("eqbuddy-tail-").FullName;
        try
        {
            var path = WriteLog(dir, "eqlog_Buddy_freeport.txt",
                "[Sat Jul 18 20:02:00 2026] You have slain orc guard!",
                "[Sat Jul 18 21:03:00 2026] You have slain orc sentry!",     // >60 min gap — rolls the session INSIDE Apply
                "[Sat Jul 18 21:04:00 2026] You have slain orc centurion!"); // buffered after the resetter — must not survive

            var stats = new SessionStats();
            var textMatches = new List<string>();
            stats.TextMatched += raw => textMatches.Add(raw.Line);
            // A rule for each line that must never reach ObserveRawLine once the
            // mid-batch reset fires: if the generation check did not run immediately
            // after _stats.Apply(evt), ObserveRawLine would still see them and
            // refill the patterns against a session Apply just wiped.
            stats.RefreshTextPatterns(
            [
                new TrackedRule { Name = "sentry", Pattern = "orc sentry", Kind = WatchKind.Text },
                new TrackedRule { Name = "centurion", Pattern = "orc centurion", Kind = WatchKind.Text },
            ]);

            var resetDone = false;
            TeammateLogTail? tail = null;
            stats.SessionRolledOver += () =>
            {
                if (resetDone) return;
                resetDone = true;
                // Mirrors what a real re-entrant Select() does: the whole session,
                // stats included, is torn down and replayed from scratch mid-drain —
                // triggered by SessionStats itself, not by the mez callback.
                stats.Reset();
                tail!.Reset();
            };
            tail = new TeammateLogTail(path, stats, () => null);

            Assert.True(tail.Fill());
            // No throw this time: Apply's own SessionRolledOver handler resets stats
            // and the tail synchronously, then returns normally to the loop. The
            // generation check placed right after _stats.Apply(evt) must catch this
            // on its own, with no exception to force an early exit.
            tail.DrainBefore(DateTime.MaxValue);

            // The re-entrant Reset() must win: the kill that rolled the session (orc
            // sentry) was wiped by the SAME stats.Reset() its own roll handler ran,
            // and the line buffered after it (orc centurion) never got a chance to
            // apply at all.
            Assert.Equal(0, stats.Snapshot().YourKillCount);
            // Neither line reached ObserveRawLine: a Text rule that would have
            // matched either one never fired, and neither line is in the raw-line
            // ring — proving the skip happened right after Apply, not merely that
            // the write-back was suppressed afterward.
            Assert.Empty(textMatches);
            Assert.DoesNotContain(stats.RecentLines(), l => l.Message.Contains("orc sentry"));
            Assert.DoesNotContain(stats.RecentLines(), l => l.Message.Contains("orc centurion"));

            // A subsequent Fill + DrainBefore(MaxValue) must replay the file exactly
            // once from the top, with no double-dispatch — resetDone is now true, so
            // the second roll on replay no longer re-enters Reset().
            Assert.True(tail.Fill());
            tail.DrainBefore(DateTime.MaxValue);
            Assert.Equal(2, stats.Snapshot().YourKillCount);   // orc sentry + orc centurion, once each
            Assert.Contains(textMatches, l => l.Contains("orc sentry"));
            Assert.Contains(textMatches, l => l.Contains("orc centurion"));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void ASplitLineIsCarriedAcrossTwoFills()
    {
        var dir = Directory.CreateTempSubdirectory("eqbuddy-tail-").FullName;
        try
        {
            var path = Path.Combine(dir, "eqlog_Buddy_freeport.txt");
            File.WriteAllText(path, "[Sat Jul 18 20:02:00 2026] You have sla");

            var stats = new SessionStats();
            var tail = new TeammateLogTail(path, stats, () => null);

            Assert.True(tail.Fill());
            tail.DrainBefore(DateTime.MaxValue);
            Assert.Equal(0, stats.Snapshot().YourKillCount);   // the partial line hasn't split yet

            File.AppendAllText(path,
                "in orc guard!\n[Sat Jul 18 20:03:00 2026] You have slain orc sentry!\n");
            Assert.True(tail.Fill());
            tail.DrainBefore(DateTime.MaxValue);
            Assert.Equal(2, stats.Snapshot().YourKillCount);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void CrlfAndLfLinesBothSplit()
    {
        var dir = Directory.CreateTempSubdirectory("eqbuddy-tail-").FullName;
        try
        {
            var path = Path.Combine(dir, "eqlog_Buddy_freeport.txt");
            File.WriteAllText(path,
                "[Sat Jul 18 20:02:00 2026] You have slain orc guard!\r\n" +
                "[Sat Jul 18 20:03:00 2026] You have slain orc sentry!\n");

            var stats = new SessionStats();
            var tail = new TeammateLogTail(path, stats, () => null);

            Assert.True(tail.Fill());
            tail.DrainBefore(DateTime.MaxValue);
            var snap = stats.Snapshot();
            Assert.Equal(2, snap.YourKillCount);
            Assert.Equal(new DateTime(2026, 7, 18, 20, 3, 0), snap.LastEventTime);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void ATruncatedFileReanchorsFromByteZero()
    {
        var dir = Directory.CreateTempSubdirectory("eqbuddy-tail-").FullName;
        try
        {
            var path = WriteLog(dir, "eqlog_Buddy_freeport.txt",
                "[Sat Jul 18 20:02:00 2026] You have slain orc guard!",
                "[Sat Jul 18 20:04:00 2026] You have slain orc sentry!");

            var stats = new SessionStats();
            var tail = new TeammateLogTail(path, stats, () => null);
            tail.Fill();
            tail.DrainBefore(DateTime.MaxValue);
            Assert.Equal(2, stats.Snapshot().YourKillCount);

            // Shorter than what's already been read — the same shape as a janitor
            // truncation of the primary log.
            File.WriteAllLines(path, ["[Sat Jul 18 20:06:00 2026] You have slain orc centurion!"]);
            Assert.True(tail.Fill());
            tail.DrainBefore(DateTime.MaxValue);
            Assert.Equal(3, stats.Snapshot().YourKillCount);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }
}
