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

            var tail = new TeammateLogTail(path, () => null);

            Assert.True(tail.Fill());

            // Strict bound: a drain to 20:04 dispatches ONLY lines stamped strictly
            // before it, so the 20:04 line itself stays buffered (see
            // ALineStampedExactlyAtTheBoundStaysBuffered) and only the 20:02 kill lands.
            tail.DrainBefore(new DateTime(2026, 7, 18, 20, 4, 0));
            var snap = tail.Stats.Snapshot();
            Assert.Equal(1, snap.YourKillCount);
            Assert.Equal(new DateTime(2026, 7, 18, 20, 2, 0), snap.LastEventTime);

            tail.DrainBefore(DateTime.MaxValue);
            Assert.Equal(3, tail.Stats.Snapshot().YourKillCount);
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

            var tail = new TeammateLogTail(path, () => null);
            Assert.True(tail.Fill());

            tail.DrainBefore(new DateTime(2026, 7, 18, 20, 4, 0));
            Assert.Equal(1, tail.Stats.Snapshot().YourKillCount);   // only the strictly-earlier line

            tail.DrainBefore(DateTime.MaxValue);
            Assert.Equal(2, tail.Stats.Snapshot().YourKillCount);   // the boundary line now lands
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

                var tail = new TeammateLogTail(path, () => null);
                Assert.True(tail.Fill());

                var sw = Stopwatch.StartNew();
                // Advance the bound one line at a time, mirroring PollPrimary calling
                // DrainBefore once per primary line during a full-file ingest.
                for (int i = 0; i < n; i++)
                    tail.DrainBefore(timestamps[i].AddTicks(1));
                sw.Stop();

                Assert.Equal(n, tail.Stats.Snapshot().YourKillCount);
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

            var tail = new TeammateLogTail(path, () => null);

            Assert.True(tail.Fill());
            tail.DrainBefore(DateTime.MaxValue);

            var snap = tail.Stats.Snapshot();
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
        //
        // Step 3: Reset() also resets Stats now (a primary re-Select restarts duo
        // totals with it — see Reset()'s own doc) — so the ONE kill applied before
        // Reset() is wiped BY Reset() itself, not carried forward. The exact count
        // that matters is still "replayed exactly once, not twice".
        var dir = Directory.CreateTempSubdirectory("eqbuddy-tail-").FullName;
        try
        {
            var path = Path.Combine(dir, "eqlog_Buddy_freeport.txt");
            File.WriteAllText(path,
                "[Sat Jul 18 20:02:00 2026] You have slain orc guard!\n" +
                "[Sat Jul 18 20:04:00 2026] You have slain orc sentry!\n" +
                "[Sat Jul 18 20:06:00 2026] You have slain orc centurion!\n" +
                "[Sat Jul 18 20:08:00 2026] You have sla");   // unterminated — no trailing newline

            var tail = new TeammateLogTail(path, () => null);

            Assert.True(tail.Fill());
            // Strict bound: only the 20:02 line is before it, so the 20:04 and 20:06
            // lines stay buffered — undispatched — right when Reset() is about to hit.
            tail.DrainBefore(new DateTime(2026, 7, 18, 20, 4, 0));
            Assert.Equal(1, tail.Stats.Snapshot().YourKillCount);

            tail.Reset();   // wipes the 1 kill above along with the buffer/offset/remainder
            Assert.True(tail.Fill());
            tail.DrainBefore(DateTime.MaxValue);
            // Every complete line in the file, replayed exactly once from the top = 3.
            // An offset-only Reset would instead double-dispatch the 20:04/20:06 lines
            // that were left buffered (applied once before Reset, once more after),
            // landing on 1 (pre-Reset) + 6 (double-counted replay) = 7, not 3.
            Assert.Equal(3, tail.Stats.Snapshot().YourKillCount);

            // The unterminated trailing line still hasn't split — completing it now
            // must dispatch it exactly once: not zero (lost with the old remainder)
            // and not twice (re-spliced against stale buffered state).
            File.AppendAllText(path, "in orc pawn!\n");
            Assert.True(tail.Fill());
            tail.DrainBefore(DateTime.MaxValue);
            Assert.Equal(4, tail.Stats.Snapshot().YourKillCount);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void MissingFileIsQuietlyEmpty()
    {
        var tail = new TeammateLogTail(@"C:\nope\eqlog_Buddy_freeport.txt", () => null);

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

            var tail = new TeammateLogTail(path, () => null);

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

            var tail = new TeammateLogTail(path, () => throw new InvalidOperationException());

            Assert.True(tail.Fill());
            Assert.Throws<InvalidOperationException>(() => tail.DrainBefore(DateTime.MaxValue));
            Assert.Equal(1, tail.Stats.Snapshot().YourKillCount);   // the kill line applied before the throw

            // Neither the poisoned line nor the kill line buffered AFTER it may linger:
            // a second call must not throw again, and must not apply either kill.
            tail.DrainBefore(DateTime.MaxValue);
            Assert.Equal(1, tail.Stats.Snapshot().YourKillCount);
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

            var tail = new TeammateLogTail(path, () => throw new IOException("ledger write failed"));

            Assert.True(tail.Fill());
            var ex = Assert.Throws<InvalidOperationException>(() => tail.DrainBefore(DateTime.MaxValue));
            Assert.IsType<IOException>(ex.InnerException);
            Assert.Equal(1, tail.Stats.Snapshot().YourKillCount);

            tail.DrainBefore(DateTime.MaxValue);              // the whole batch was dropped
            Assert.Equal(1, tail.Stats.Snapshot().YourKillCount);
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

            var tail = new TeammateLogTail(path, () => null);

            Assert.True(tail.Fill());   // buffers 2 complete lines; the split one waits in _remainder
            tail.Discard();
            tail.DrainBefore(DateTime.MaxValue);
            Assert.Equal(0, tail.Stats.Snapshot().YourKillCount);   // both buffered lines were dropped, not just deferred

            // Offset kept: no new bytes to read, so Fill reports nothing new rather
            // than re-splitting the same two lines a second time.
            Assert.False(tail.Fill());

            // Remainder kept: completing the split line still finishes it, proving
            // Discard() did not clear the in-flight partial line either.
            File.AppendAllText(path, "in orc centurion!\n");
            Assert.True(tail.Fill());
            tail.DrainBefore(DateTime.MaxValue);
            Assert.Equal(1, tail.Stats.Snapshot().YourKillCount);
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

            var resetDone = false;
            TeammateLogTail? tail = null;
            tail = new TeammateLogTail(path, () =>
            {
                if (!resetDone)
                {
                    resetDone = true;
                    // Mirrors what a real re-entrant Select() does: the whole session
                    // (this tail's OWN Stats, since Step 3) is torn down and replayed
                    // from scratch mid-drain. Reset() resets Stats too.
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
            Assert.Equal(0, tail.Stats.Snapshot().YourKillCount);

            // A subsequent Fill + DrainBefore(MaxValue) must replay the file exactly
            // once from the top, with no exception and no double-dispatch.
            Assert.True(tail.Fill());
            tail.DrainBefore(DateTime.MaxValue);
            Assert.Equal(2, tail.Stats.Snapshot().YourKillCount);   // the file's two kills, once each
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void AReentrantResetTriggeredFromStatsApplyStopsTheLaterConsumersInTheSameLine()
    {
        // Regression: the generation check used to run ONCE, at the bottom of the
        // loop body. SessionStats.SessionRolledOver fires SYNCHRONOUSLY from inside
        // _stats.Apply(evt) itself (a >60-minute forward gap rolls the session before
        // Apply returns) — a re-entrant Reset() from that callback returns NORMALLY,
        // with no exception to unwind through, so a check only at the bottom would
        // let the SAME iteration go on to call the mez tracker against a SessionStats
        // the very call above it just reset. The fix checks generation immediately
        // after EACH consumer call, so this line's own Apply(evt) catches the reset
        // before the mez factory is even reached for it. (Finding 5 removed the
        // third, ObserveRawLine-based consumer this test used to observe through —
        // it now observes via the mez factory instead, since DamageTakenEvent is
        // admitted for both stats and mez.)
        var dir = Directory.CreateTempSubdirectory("eqbuddy-tail-").FullName;
        try
        {
            var path = WriteLog(dir, "eqlog_Buddy_freeport.txt",
                "[Sat Jul 18 20:02:00 2026] You have slain orc guard!",
                "[Sat Jul 18 21:03:00 2026] Orc sentry hits YOU for 5 points of damage.",     // >60 min gap — rolls the session INSIDE Apply; admitted for BOTH stats and mez
                "[Sat Jul 18 21:04:00 2026] Orc centurion hits YOU for 5 points of damage."); // buffered after the resetter — must not survive

            var mezCalls = 0;
            var resetDone = false;
            // Returning null still proves the point: the factory itself only runs
            // when TeammateFeed.AdmitForMez(evt) is reached for a given line, which
            // is exactly the code path a missed generation check would let run anyway.
            var tail = new TeammateLogTail(path, () => { mezCalls++; return null; });
            tail.Stats.SessionRolledOver += () =>
            {
                if (resetDone) return;
                resetDone = true;
                // Mirrors what a real re-entrant Select() does: the whole session
                // (this tail's OWN Stats, since Step 3) is torn down and replayed
                // from scratch mid-drain — triggered by SessionStats itself, not by
                // the mez callback. Reset() resets Stats too.
                tail.Reset();
            };

            Assert.True(tail.Fill());
            // No throw this time: Apply's own SessionRolledOver handler resets the
            // tail's Stats synchronously, then returns normally to the loop. The
            // generation check placed right after Stats.Apply(evt) must catch this
            // on its own, with no exception to force an early exit.
            tail.DrainBefore(DateTime.MaxValue);

            // The re-entrant Reset() must win: the damage that rolled the session
            // (orc sentry) was wiped by the SAME stats.Reset() its own roll handler
            // ran, and the line buffered after it (orc centurion) never got a chance
            // to apply at all — proven by the mez factory never having been reached
            // for either line.
            Assert.Equal(0, tail.Stats.Snapshot().DamageTaken);
            Assert.Equal(0, mezCalls);

            // A subsequent Fill + DrainBefore(MaxValue) must replay the file exactly
            // once from the top, with no double-dispatch — resetDone is now true, so
            // the second roll on replay no longer re-enters Reset(), and both
            // admitted lines reach the mez factory normally this time.
            Assert.True(tail.Fill());
            tail.DrainBefore(DateTime.MaxValue);
            Assert.Equal(10, tail.Stats.Snapshot().DamageTaken);   // orc sentry + orc centurion, 5 each, once each
            Assert.Equal(2, mezCalls);
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

            var tail = new TeammateLogTail(path, () => null);

            Assert.True(tail.Fill());
            tail.DrainBefore(DateTime.MaxValue);
            Assert.Equal(0, tail.Stats.Snapshot().YourKillCount);   // the partial line hasn't split yet

            File.AppendAllText(path,
                "in orc guard!\n[Sat Jul 18 20:03:00 2026] You have slain orc sentry!\n");
            Assert.True(tail.Fill());
            tail.DrainBefore(DateTime.MaxValue);
            Assert.Equal(2, tail.Stats.Snapshot().YourKillCount);
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

            var tail = new TeammateLogTail(path, () => null);

            Assert.True(tail.Fill());
            tail.DrainBefore(DateTime.MaxValue);
            var snap = tail.Stats.Snapshot();
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

            var tail = new TeammateLogTail(path, () => null);
            tail.Fill();
            tail.DrainBefore(DateTime.MaxValue);
            Assert.Equal(2, tail.Stats.Snapshot().YourKillCount);

            // Shorter than what's already been read — the same shape as a janitor
            // truncation of the primary log.
            File.WriteAllLines(path, ["[Sat Jul 18 20:06:00 2026] You have slain orc centurion!"]);
            Assert.True(tail.Fill());
            tail.DrainBefore(DateTime.MaxValue);
            Assert.Equal(3, tail.Stats.Snapshot().YourKillCount);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void TeammateLinesNeverReachObserveRawLine()
    {
        // Finding 5: DrainBefore fed EVERY teammate line to ObserveRawLine
        // unconditionally, with no TeammateFeed gate at all. Lines both logs carry
        // verbatim (group/guild chat, third-party combat, "X has been slain by Y")
        // were observed twice per occurrence once merged with the primary — one
        // raid-call alert fired twice, and the recent-lines ring filled with
        // duplicates. The user's own log already carries every world/chat line,
        // which is exactly what TeammateFeed's class doc claims this feature skips.
        var dir = Directory.CreateTempSubdirectory("eqbuddy-tail-").FullName;
        try
        {
            var path = WriteLog(dir, "eqlog_Buddy_freeport.txt",
                "[Sat Jul 18 20:02:00 2026] Someone tells the guild, 'Incoming!'");

            var textMatches = new List<string>();
            var tail = new TeammateLogTail(path, () => null);
            // Deliberately wired anyway (this class's own doc says NOTHING may ever
            // subscribe to the teammate instance's events in production) — the point
            // of this test is that DrainBefore itself never gives ObserveRawLine the
            // chance to fire it, regardless of whether something is listening.
            tail.Stats.TextMatched += raw => textMatches.Add(raw.Line);
            tail.Stats.RefreshTextPatterns(
                [new TrackedRule { Name = "incoming", Pattern = "Incoming!", Kind = WatchKind.Text }]);

            Assert.True(tail.Fill());
            tail.DrainBefore(DateTime.MaxValue);

            Assert.Empty(textMatches);
            Assert.DoesNotContain(tail.Stats.RecentLines(), l => l.Message.Contains("Incoming!"));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    /// <summary>
    /// A2 (part i): a mid-session pick used to import the teammate's ENTIRE latest
    /// contiguous session regardless of how long the primary's own session had been
    /// running, so five minutes of your session got combined with two hours of their
    /// kills and divided by five minutes. <see cref="TeammateLogTail.PrimarySessionStart"/>
    /// lets the caller (LogWatcher, wired from the primary SessionStats) supply the
    /// bound; anything the teammate's log carries strictly before it must not reach
    /// their isolated Stats at all.
    /// </summary>
    [Fact]
    public void EventsBeforeThePrimarysSessionStartAreRejected()
    {
        var dir = Directory.CreateTempSubdirectory("eqbuddy-tail-").FullName;
        try
        {
            var path = WriteLog(dir, "eqlog_Buddy_freeport.txt",
                "[Sat Jul 18 18:00:00 2026] You have slain orc guard!",   // two hours before the primary's session started
                "[Sat Jul 18 20:00:00 2026] You have slain orc sentry!"); // after — must still count

            var tail = new TeammateLogTail(path, () => null)
            {
                PrimarySessionStart = () => new DateTime(2026, 7, 18, 19, 55, 0),
            };

            Assert.True(tail.Fill());
            tail.DrainBefore(DateTime.MaxValue);

            Assert.Equal(1, tail.Stats.Snapshot().YourKillCount);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    /// <summary>
    /// Repair round C2: the ORIGINAL bootstrap tolerance ("null means admit
    /// everything") was itself the bug — a per-line reject can only fire once a
    /// bound EXISTS to compare against, so every line offered while
    /// <see cref="TeammateLogTail.PrimarySessionStart"/> still reports null sailed
    /// through unfiltered, including a teammate kill hours before the primary's log
    /// even existed. Once the primary logs its first event and DrainBefore runs
    /// again with the SAME buffered line still sitting there, the (now-established)
    /// bound must still be free to reject it — which it cannot do if the line was
    /// already dispatched during the null window. The fix HOLDS draining — not just
    /// individual lines — for as long as the bound is unknown.</summary>
    [Fact]
    public void WithNoPrimarySessionStartYetNothingIsAdmittedAtAll()
    {
        var dir = Directory.CreateTempSubdirectory("eqbuddy-tail-").FullName;
        try
        {
            var path = WriteLog(dir, "eqlog_Buddy_freeport.txt",
                "[Sat Jul 18 18:00:00 2026] You have slain orc guard!");

            var tail = new TeammateLogTail(path, () => null) { PrimarySessionStart = () => null };

            Assert.True(tail.Fill());
            tail.DrainBefore(DateTime.MaxValue);

            Assert.Equal(0, tail.Stats.Snapshot().YourKillCount);   // held, not dropped and not admitted
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    /// <summary>The other half: once the primary's own first event establishes a
    /// REAL bound, whatever was held catches up — filtered by that bound exactly
    /// like a line offered after it always was. A teammate picked before the
    /// primary's own log is a normal startup order, not a permanent gap.</summary>
    [Fact]
    public void OnceThePrimarySessionStartIsEstablishedHeldLinesCatchUpAndAreFiltered()
    {
        var dir = Directory.CreateTempSubdirectory("eqbuddy-tail-").FullName;
        try
        {
            var path = WriteLog(dir, "eqlog_Buddy_freeport.txt",
                "[Sat Jul 18 18:00:00 2026] You have slain orc guard!",   // before the eventual bound — must stay rejected
                "[Sat Jul 18 20:00:00 2026] You have slain orc sentry!"); // after — must land once the bound exists

            DateTime? bound = null;
            var tail = new TeammateLogTail(path, () => null) { PrimarySessionStart = () => bound };

            Assert.True(tail.Fill());
            tail.DrainBefore(DateTime.MaxValue);
            Assert.Equal(0, tail.Stats.Snapshot().YourKillCount);   // still held — no bound yet

            bound = new DateTime(2026, 7, 18, 19, 55, 0);           // the primary just logged its first event
            tail.DrainBefore(DateTime.MaxValue);

            Assert.Equal(1, tail.Stats.Snapshot().YourKillCount);   // only the 20:00 kill — 18:00 stays rejected
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }
}
