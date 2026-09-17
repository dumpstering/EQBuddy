using System.Diagnostics;
using EQBuddy.Core;

namespace EQBuddy.Tests;

/// <summary>
/// The watcher's identity parsing and Select lifecycle (audit findings 4/5):
/// archive filenames must not mint phantom servers, and only the latest Select may
/// declare its ingest done. The ExitReview ordering half of finding 5 lives in WPF
/// code this suite can't host; the generation seam it leans on is pinned here.
/// </summary>
public class LogWatcherTests
{
    // ---- FromPath: live names, janitor-archive names, stamp look-alikes ----

    [Theory]
    [InlineData("eqlog_Dranak_legends.txt", "Dranak", "legends")]
    [InlineData("eqlog_Dranak_legends_20260813120000.txt", "Dranak", "legends")]        // janitor archive
    [InlineData("eqlog_Dranak_legends_20260813120000-2.txt", "Dranak", "legends")]      // same-second dedup copy
    [InlineData("eqlog_Aenari_erollisi_marr_20260813120000.txt", "Aenari", "erollisi_marr")]
    [InlineData("eqlog_Aenari_erollisi_marr.txt", "Aenari", "erollisi_marr")]
    [InlineData("eqlog_Bob_1234567890123.txt", "Bob", "1234567890123")]                 // 13 digits ≠ stamp
    [InlineData("eqlog_Bob_123456789012345.txt", "Bob", "123456789012345")]             // 15 digits ≠ stamp
    [InlineData("eqlog_Bob_20260813120000.txt", "Bob", "20260813120000")]               // server can't be empty
    public void FromPathReadsCharacterAndServerThroughArchiveStamps(
        string file, string character, string server)
    {
        var log = CharacterLog.FromPath(Path.Combine("C:\\Logs", file))!;
        Assert.Equal(character, log.Character);
        Assert.Equal(server, log.Server);
    }

    [Theory]
    [InlineData("eqlog_Dranak.txt")]      // no server segment at all
    [InlineData("dbg.txt")]
    public void FromPathRejectsNonCharacterLogs(string file) =>
        Assert.Null(CharacterLog.FromPath(Path.Combine("C:\\Logs", file)));

    // ---- Select generations (finding 5) ----

    private static string WriteLog(string dir, string name, params string[] lines)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void ASupersededSelectCannotDeclareItsSuccessorsIngestDone()
    {
        // Overlapping Selects: the first one's queued completion used to set
        // InitialIngestDone unconditionally, so alerts fired for lines the SECOND
        // Select was still replaying as history. DeferIngestForTests suppresses the
        // background task so the interleaving runs deterministically.
        var dir = Directory.CreateTempSubdirectory("eqbuddy-watch-").FullName;
        try
        {
            var f1 = WriteLog(dir, "eqlog_Kaybek_freeport.txt",
                "[Sat Jul 18 15:00:00 2026] You have slain orc pawn!");
            var f2 = WriteLog(dir, "eqlog_Douglas_freeport.txt",
                "[Sat Jul 18 16:00:00 2026] You have slain orc centurion!",
                "[Sat Jul 18 16:00:05 2026] You have slain orc legionnaire!");

            var stats = new SessionStats();
            using var w = new LogWatcher(stats) { DeferIngestForTests = true };
            w.Select(f1);
            var g1 = w.SelectGeneration;
            w.Select(f2);
            var g2 = w.SelectGeneration;

            w.FinishInitialIngest(g1);              // the stale completion lands late
            Assert.False(w.InitialIngestDone);      // …and may not hand over the tail

            w.FinishInitialIngest(g2);
            Assert.True(w.InitialIngestDone);
            Assert.Equal(f2, w.CurrentPath);
            Assert.Equal("Douglas", stats.CharacterName);
            Assert.Equal(2, stats.Snapshot().YourKillCount);   // f2's content, once
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    // ---- FileIdentity I/O off the watcher lock (plan Part 5b-i) ----

    [Fact]
    public void ValidationIOHappensOutsideTheWatcherLock()
    {
        // TeammateLogPicker.Validate reads LogFolder — a host-supplied accessor that
        // can, in principle, do arbitrary work (or simply be slow). Before the hoist,
        // SelectTeammate called it from INSIDE _lock, so a slow accessor stalled every
        // other _lock-holding operation (the 150 ms poll, on the same watcher) for as
        // long as the accessor took. The fix reads LogFolder OUTSIDE the lock, so a
        // concurrent Poll is never held hostage by it.
        var dir = Directory.CreateTempSubdirectory("eqbuddy-watch-").FullName;
        try
        {
            var mate = WriteLog(dir, "eqlog_Buddy_freeport.txt",
                "[Sat Jul 18 15:00:00 2026] You have slain orc pawn!");

            var stats = new SessionStats();
            using var w = new LogWatcher(stats) { DeferIngestForTests = true };
            var gate = new ManualResetEventSlim(false);
            var accessorEntered = new ManualResetEventSlim(false);
            w.LogFolder = () =>
            {
                accessorEntered.Set();
                gate.Wait(TimeSpan.FromSeconds(5));
                return null;
            };

            var picker = new Thread(() => w.SelectTeammate(mate));
            picker.Start();
            // Wait for SelectTeammate to actually be inside the (slow) accessor before
            // measuring — a race on the START of the block would be a flaky assertion
            // about scheduling, not about the lock.
            Assert.True(accessorEntered.Wait(TimeSpan.FromSeconds(2)),
                "SelectTeammate never reached the LogFolder accessor");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            w.PollForTests();   // must not block behind SelectTeammate's still-pending accessor
            sw.Stop();

            gate.Set();
            Assert.True(picker.Join(TimeSpan.FromSeconds(5)), "SelectTeammate never returned");

            Assert.True(sw.ElapsedMilliseconds < 1000,
                $"Poll took {sw.ElapsedMilliseconds}ms — validation I/O must run outside the watcher lock");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    // ---- A7: TOCTOU races in the off-lock hoists ----

    [Fact]
    public void SelectDoesNotDiscardATeammateInstalledDuringItsOwnUnlockedProbe()
    {
        // Repair round A7: Select validates a captured `m0` OFF-lock (right above), then
        // re-enters the lock and acted on `_teammate` (re-read fresh) using that STALE
        // verdict. A concurrent SelectTeammate landing a DIFFERENT teammate in between
        // used to have the NEW teammate discarded on the OLD one's refusal — teammate A
        // conflicts with the primary path being selected (same file); teammate B does
        // not. If Select acts on A's refusal against B, B is wrongly discarded.
        var dir = Directory.CreateTempSubdirectory("eqbuddy-watch-").FullName;
        var mateDir = Directory.CreateTempSubdirectory("eqbuddy-watch-mateB-").FullName;
        try
        {
            var primary = WriteLog(dir, "eqlog_Kaybek_freeport.txt",
                "[Sat Jul 18 15:00:00 2026] You have slain orc pawn!");
            // Teammate A is given the SAME path Select is about to adopt as primary —
            // a real same-file conflict, refused by TeammateLogPicker.Validate.
            var mateB = WriteLog(mateDir, "eqlog_Buddy_freeport.txt",
                "[Sat Jul 18 15:00:05 2026] You have slain orc centurion!");

            var stats = new SessionStats();
            using var w = new LogWatcher(stats) { DeferIngestForTests = true };
            w.SelectTeammate(primary);   // teammate A: same path Select(primary) will use

            var gate = new ManualResetEventSlim(false);
            var accessorEntered = new ManualResetEventSlim(false);
            var releaseSelectTeammate = false;
            w.LogFolder = () =>
            {
                if (!releaseSelectTeammate) { accessorEntered.Set(); gate.Wait(TimeSpan.FromSeconds(5)); }
                return null;
            };

            var selectThread = new Thread(() => w.Select(primary));
            selectThread.Start();
            Assert.True(accessorEntered.Wait(TimeSpan.FromSeconds(2)),
                "Select never reached the LogFolder accessor during its off-lock probe");

            // While Select's off-lock probe against teammate A is still pending, swap
            // in teammate B — unrelated to the primary path, so it validates cleanly.
            releaseSelectTeammate = true;   // SelectTeammate's own probe must not block
            w.SelectTeammate(mateB);

            gate.Set();   // release Select's stale probe against A
            Assert.True(selectThread.Join(TimeSpan.FromSeconds(5)), "Select never returned");

            w.FinishInitialIngest(w.SelectGeneration);

            // B must survive — A's stale refusal must never be applied to it.
            Assert.Equal("Buddy", w.TeammateName);
            Assert.NotNull(w.TeammateStats);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
            try { Directory.Delete(mateDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Repair round A7, the second race: Poll's continuous revalidation captures
    /// `teammateToRevalidate` and probes <c>LogFolder</c> off-lock, then stamps
    /// <c>_teammateFilePresentLastCheck</c> UNCONDITIONALLY — describing whichever
    /// tail was captured, even if a concurrent SelectTeammate installed a DIFFERENT
    /// one while the probe was in flight. Reflection reads the private field directly
    /// because the bug is specifically about internal bookkeeping the public surface
    /// doesn't expose: "Poll can finish validating an old tail and overwrite
    /// _teammateFilePresentLastCheck for a new missing tail, so when that file
    /// appears its identity revalidation is skipped."
    /// </summary>
    [Fact]
    public void PollDoesNotStampFilePresenceForATeammateReplacedDuringItsProbe()
    {
        var dirA = Directory.CreateTempSubdirectory("eqbuddy-watch-mateA-").FullName;
        try
        {
            // Teammate A's file EXISTS; teammate B's does not (yet).
            var mateA = WriteLog(dirA, "eqlog_Alice_freeport.txt",
                "[Sat Jul 18 15:00:00 2026] You have slain orc pawn!");
            var mateBPath = Path.Combine(dirA, "eqlog_Buddy_freeport.txt");   // never written

            var stats = new SessionStats();
            using var w = new LogWatcher(stats) { DeferIngestForTests = true };
            w.SelectTeammate(mateA);

            var gate = new ManualResetEventSlim(false);
            var accessorEntered = new ManualResetEventSlim(false);
            var releaseSelectTeammate = false;
            w.LogFolder = () =>
            {
                if (!releaseSelectTeammate) { accessorEntered.Set(); gate.Wait(TimeSpan.FromSeconds(5)); }
                return null;
            };

            var pollThread = new Thread(() => w.PollForTests());
            pollThread.Start();
            Assert.True(accessorEntered.Wait(TimeSpan.FromSeconds(2)),
                "Poll never reached the LogFolder accessor during its off-lock probe");

            // While Poll's probe against A is still pending, swap the teammate to B —
            // B's file does not exist, so its OWN true file-presence is false.
            releaseSelectTeammate = true;
            w.SelectTeammate(mateBPath);

            gate.Set();   // release Poll's stale probe against A
            Assert.True(pollThread.Join(TimeSpan.FromSeconds(5)), "Poll never returned");

            // A's file DOES exist, so the old unconditional write would have stamped
            // `true` here — describing A, not the now-current B, whose file is absent.
            var field = typeof(LogWatcher).GetField("_teammateFilePresentLastCheck",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            Assert.False((bool)field.GetValue(w)!,
                "Poll must not stamp file-presence describing a teammate that was replaced during its own off-lock probe");
        }
        finally { try { Directory.Delete(dirA, recursive: true); } catch { } }
    }

    // ---- teammate feed (a second log riding the same pipeline; see TeammateFeed) ----

    [Fact]
    public void TeammateLinesApplyToTheirOwnIsolatedSessionNotYours()
    {
        // Step 3: the teammate's log drives its OWN SessionStats (TeammateLogTail.Stats)
        // rather than a shared "join" — no combining happens at this layer at all (that
        // is Step 4's DuoStats.Combine, over two independent snapshots).
        var ownDir = Directory.CreateTempSubdirectory("eqbuddy-watch-own-").FullName;
        // A DIFFERENT temp dir — a teammate's synced copy lives outside the owner's
        // own Logs folder, or character-follow would flip to it.
        var mateDir = Directory.CreateTempSubdirectory("eqbuddy-watch-mate-").FullName;
        try
        {
            var own = WriteLog(ownDir, "eqlog_Kaybek_freeport.txt",
                "[Sat Jul 18 15:00:00 2026] You have slain orc pawn!");
            var mate = WriteLog(mateDir, "eqlog_Buddy_freeport.txt",
                "[Sat Jul 18 15:00:05 2026] You have slain orc centurion!",
                "[Sat Jul 18 15:00:10 2026] You have slain orc legionnaire!",
                "[Sat Jul 18 15:00:15 2026] --You have looted a Rusty Dagger from orc pawn's corpse.--");

            var stats = new SessionStats();
            using var w = new LogWatcher(stats) { DeferIngestForTests = true };
            w.SelectTeammate(mate);
            w.Select(own);
            w.FinishInitialIngest(w.SelectGeneration);

            var snap = stats.Snapshot();
            Assert.Equal(1, snap.YourKillCount);            // the watched character's own kill ONLY
            Assert.Equal("Kaybek", stats.CharacterName);    // owner identity untouched
            Assert.Equal("Buddy", w.TeammateName);

            var mateSnap = w.TeammateStats!.Snapshot();
            Assert.Equal(2, mateSnap.YourKillCount);        // the teammate's own two kills
            Assert.Equal("Buddy", w.TeammateStats.CharacterName);
            Assert.Contains(mateSnap.Loot, l => l.Item == "Rusty Dagger");
            Assert.DoesNotContain(snap.Loot, l => l.Item == "Rusty Dagger");   // never touches the watched character's own loot
        }
        finally
        {
            try { Directory.Delete(ownDir, recursive: true); } catch { }
            try { Directory.Delete(mateDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TeammateFeedIsSuppressedDuringArchiveReview()
    {
        var ownDir = Directory.CreateTempSubdirectory("eqbuddy-watch-own-").FullName;
        var mateDir = Directory.CreateTempSubdirectory("eqbuddy-watch-mate-").FullName;
        try
        {
            var own = WriteLog(ownDir, "eqlog_Kaybek_freeport.txt",
                "[Sat Jul 18 15:00:00 2026] You have slain orc pawn!");
            var mate = WriteLog(mateDir, "eqlog_Buddy_freeport.txt",
                "[Sat Jul 18 15:00:05 2026] You have slain orc centurion!",
                "[Sat Jul 18 15:00:10 2026] You have slain orc legionnaire!");

            var stats = new SessionStats();
            using var w = new LogWatcher(stats) { DeferIngestForTests = true };
            w.SelectTeammate(mate);
            // A bounded [0, endOffset) range is a session-range review (#74), never the
            // live tail — the teammate's file has no matching slice to bound against.
            var endOffset = new FileInfo(own).Length;
            w.Select(own, 0, endOffset);
            w.FinishInitialIngest(w.SelectGeneration);

            Assert.Equal(1, stats.Snapshot().YourKillCount);   // the teammate's kills never rode along
            // The teammate's OWN isolated instance never even got Fill()'d/drained
            // during archive review (Poll's `_activeMate` gate stays null whenever
            // `_endOffset != long.MaxValue`) — it exists, but shows nothing.
            Assert.Equal(0, w.TeammateStats!.Snapshot().YourKillCount);
        }
        finally
        {
            try { Directory.Delete(ownDir, recursive: true); } catch { }
            try { Directory.Delete(mateDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ClearingTheTeammateStopsTheFeed()
    {
        var ownDir = Directory.CreateTempSubdirectory("eqbuddy-watch-own-").FullName;
        var mateDir = Directory.CreateTempSubdirectory("eqbuddy-watch-mate-").FullName;
        try
        {
            var own = WriteLog(ownDir, "eqlog_Kaybek_freeport.txt",
                "[Sat Jul 18 15:00:00 2026] You have slain orc pawn!");
            var mate = WriteLog(mateDir, "eqlog_Buddy_freeport.txt",
                "[Sat Jul 18 15:00:05 2026] You have slain orc centurion!");

            var stats = new SessionStats();
            using var w = new LogWatcher(stats) { DeferIngestForTests = true };
            w.SelectTeammate(mate);   // no primary selected yet — nothing to auto-replay
            w.Select(own);
            w.FinishInitialIngest(w.SelectGeneration);
            Assert.Equal(1, stats.Snapshot().YourKillCount);       // the watched character's own kill only
            Assert.Equal(1, w.TeammateStats!.Snapshot().YourKillCount);   // the teammate's, isolated

            // A LIVE primary is selected now, so clearing the teammate auto-replays it
            // (SelectTeammate) — proving the auto-replay leaves the watched character's
            // OWN count untouched (it was never derived from the teammate's feed).
            w.SelectTeammate(null);
            w.FinishInitialIngest(w.SelectGeneration);

            Assert.Equal(1, stats.Snapshot().YourKillCount);   // unchanged — it never included the teammate's kill
            Assert.Null(w.TeammateName);
            Assert.Null(w.TeammateStats);   // the isolated instance is gone with the tail that owned it
        }
        finally
        {
            try { Directory.Delete(ownDir, recursive: true); } catch { }
            try { Directory.Delete(mateDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TeammateHistoryOlderThanYourSessionCannotWipeIt()
    {
        // The bug this regression test originally proved fixed (concatenating whole
        // files let the teammate's OWN internal 60-minute gap roll the SHARED session,
        // wiping what the primary had already contributed) is now STRUCTURALLY
        // IMPOSSIBLE (Step 3, per the plan): the teammate's history rolls only THEIR
        // OWN isolated instance. There is no shared session left for it to wipe.
        var ownDir = Directory.CreateTempSubdirectory("eqbuddy-watch-own-").FullName;
        var mateDir = Directory.CreateTempSubdirectory("eqbuddy-watch-mate-").FullName;
        try
        {
            var own = WriteLog(ownDir, "eqlog_Kaybek_freeport.txt",
                "[Sat Jul 18 20:00:00 2026] You have slain orc pawn!",
                "[Sat Jul 18 20:05:00 2026] You have slain orc centurion!");
            // A >60-minute gap INSIDE the teammate file (Friday to Saturday) — the
            // exact shape that used to trip the roll once the two files were
            // concatenated rather than merged.
            var mate = WriteLog(mateDir, "eqlog_Buddy_freeport.txt",
                "[Fri Jul 17 10:00:00 2026] You have slain orc legionnaire!",
                "[Fri Jul 17 10:05:00 2026] You have slain orc soldier!",
                "[Sat Jul 18 20:02:00 2026] You have slain orc guard!",
                "[Sat Jul 18 20:04:00 2026] You have slain orc sentry!");

            var stats = new SessionStats();
            using var w = new LogWatcher(stats) { DeferIngestForTests = true };
            w.SelectTeammate(mate);
            w.Select(own);
            w.FinishInitialIngest(w.SelectGeneration);

            // The watched character's own session is untouched by ANYTHING in the
            // teammate's file, gap or no gap — it never saw the teammate's Friday play
            // at all, because the teammate's log never reaches this instance.
            var snap = stats.Snapshot();
            Assert.Equal(2, snap.YourKillCount);
            Assert.Equal(new DateTime(2026, 7, 18, 20, 0, 0), snap.SessionStart);

            // The teammate's OWN instance still rolls on ITS OWN internal gap, exactly
            // as any single log would — only its OWN Friday history is wiped, not
            // the watched character's session.
            var mateSnap = w.TeammateStats!.Snapshot();
            Assert.Equal(2, mateSnap.YourKillCount);   // only the Saturday pair survives the teammate's own roll
            Assert.Equal(new DateTime(2026, 7, 18, 20, 2, 0), mateSnap.SessionStart);
        }
        finally
        {
            try { Directory.Delete(ownDir, recursive: true); } catch { }
            try { Directory.Delete(mateDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TeammateLinesInterleaveByTimestamp()
    {
        // Stats are isolated now (Step 3), so the shared combat journal this test used
        // to read dispatch order through is gone — but the ORDERING GUARANTEE it
        // proved still matters for the ONE thing both logs still feed: the shared
        // MezTracker (plan Part 3a, guarantees O1/O2). A cast genuinely INTERLEAVED
        // between two primary lines must be recorded (and so become the explaining
        // cast for a later landing) at the right chronological point — not only once
        // the primary's whole file has been read.
        var ownDir = Directory.CreateTempSubdirectory("eqbuddy-watch-own-").FullName;
        var mateDir = Directory.CreateTempSubdirectory("eqbuddy-watch-mate-").FullName;
        try
        {
            var own = WriteLog(ownDir, "eqlog_Kaybek_freeport.txt",
                "[Sat Jul 18 20:00:00 2026] You begin casting Mesmerization.",
                "[Sat Jul 18 20:00:03 2026] You have slain orc pawn!",             // advances the primary poll past 20:00:02
                "[Sat Jul 18 20:00:06 2026] an orc pawn has been mesmerized.");    // landing — within CastToLand (8s) of BOTH casts
            // Chronologically BETWEEN the primary's first two lines — a truly
            // interleaved dispatch must apply this cast before the 20:00:06 landing is
            // reached, at the point the 20:00:03 primary line drains the teammate buffer.
            var mate = WriteLog(mateDir, "eqlog_Buddy_freeport.txt",
                "[Sat Jul 18 20:00:02 2026] You begin casting Mesmerization.");

            var stats = new SessionStats();
            var mez = new MezTracker();
            using var w = new LogWatcher(stats) { Mez = mez, DeferIngestForTests = true };
            w.SelectTeammate(mate);
            w.Select(own);
            w.FinishInitialIngest(w.SelectGeneration);

            // OnLanding picks the MOST RECENTLY RECORDED matching cast. If the
            // teammate's 20:00:02 cast had instead been dispatched only after the
            // primary's WHOLE file was read (the batched-order bug this test used to
            // catch via the shared journal), it would still have been recorded before
            // the 20:00:06 landing — so the discriminating fact is that it explains the
            // landing at ALL, proving it was actually applied rather than dropped or
            // reordered past the point that matters.
            var chip = Assert.Single(mez.Snapshot(new DateTime(2026, 7, 18, 20, 0, 7)));
            Assert.Equal("Buddy", chip.Caster);

            // And stats stayed isolated throughout: the teammate's cast never touched
            // the watched character's own kill count.
            Assert.Equal(1, stats.Snapshot().YourKillCount);
        }
        finally
        {
            try { Directory.Delete(ownDir, recursive: true); } catch { }
            try { Directory.Delete(mateDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TeammateLinesAtTheSameSecondGoAfterYourOwnLine()
    {
        // EQ log stamps are 1-second granular, so a tie between the primary and a
        // teammate's line is common in a duo session. TeammateLogTail.DrainBefore
        // uses a STRICT bound specifically so a tie resolves primary-first (plan Part
        // 3a, guarantee O5) — observed here through the mez tracker's cast-order
        // attribution (stats no longer share a journal to read dispatch order
        // through): both cast the identical spell at the identical second, so
        // whichever is recorded SECOND is the one OnLanding's "most recent matching
        // cast" rule credits — and primary-first-on-a-tie means that is the teammate.
        var ownDir = Directory.CreateTempSubdirectory("eqbuddy-watch-own-").FullName;
        var mateDir = Directory.CreateTempSubdirectory("eqbuddy-watch-mate-").FullName;
        try
        {
            var own = WriteLog(ownDir, "eqlog_Kaybek_freeport.txt",
                "[Sat Jul 18 20:00:00 2026] You begin casting Mesmerization.",
                "[Sat Jul 18 20:00:05 2026] an orc pawn has been mesmerized.");   // within CastToLand (8s)
            var mate = WriteLog(mateDir, "eqlog_Buddy_freeport.txt",
                "[Sat Jul 18 20:00:00 2026] You begin casting Mesmerization.");   // same second, same spell

            var stats = new SessionStats();
            var mez = new MezTracker();
            using var w = new LogWatcher(stats) { Mez = mez, DeferIngestForTests = true };
            w.SelectTeammate(mate);
            w.Select(own);
            w.FinishInitialIngest(w.SelectGeneration);

            var chip = Assert.Single(mez.Snapshot(new DateTime(2026, 7, 18, 20, 0, 6)));
            Assert.Equal("Buddy", chip.Caster);   // the tied line recorded SECOND (primary-first) wins the pick
        }
        finally
        {
            try { Directory.Delete(ownDir, recursive: true); } catch { }
            try { Directory.Delete(mateDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void APoisonedPollDiscardsTheTeammatesRemainderInsteadOfDeferringIt()
    {
        // Regression: when PollPrimary swallows a consumer throw (its catch sets
        // LastError), the old code skipped the trailing teammate drain but left
        // whatever was still buffered in place — deferred, not dropped. On the NEXT
        // poll, with LastError unchanged, the trailing drain ran normally and
        // dispatched that leftover line as though it had just arrived live. Since
        // this can happen during the very poll that finishes initial ingest, the
        // leftover can dispatch on a LATER poll after InitialIngestDone has already
        // flipped true — firing a Text watch alert for a historical line.
        var ownDir = Directory.CreateTempSubdirectory("eqbuddy-watch-own-").FullName;
        var mateDir = Directory.CreateTempSubdirectory("eqbuddy-watch-mate-").FullName;
        try
        {
            // The second own line is the poison: its text matches a Text watch rule
            // whose handler throws, so PollPrimary's own catch swallows it and sets
            // LastError partway through the SAME poll that ingests everything below.
            var own = WriteLog(ownDir, "eqlog_Kaybek_freeport.txt",
                "[Sat Jul 18 15:00:00 2026] You have slain orc pawn!",
                "[Sat Jul 18 15:00:05 2026] You have slain orc sentry!");
            // Stamped AFTER the primary's last line, so it is still buffered —
            // undispatched — the instant PollPrimary throws on the line above.
            var mate = WriteLog(mateDir, "eqlog_Buddy_freeport.txt",
                "[Sat Jul 18 15:00:10 2026] You have slain orc legionnaire!");

            var stats = new SessionStats();
            var otherMatches = new List<string>();
            stats.TextMatched += raw =>
            {
                if (raw.Line.Contains("orc sentry")) throw new InvalidOperationException("poison");
                otherMatches.Add(raw.Line);
            };
            // Seeds the prefilter before any ingest — RefreshTextPatterns is what a
            // real host calls before Select; without it these lines would never
            // reach TextMatched at all, poisoned or not.
            stats.RefreshTextPatterns(
            [
                new TrackedRule { Name = "poison", Pattern = "orc sentry", Kind = WatchKind.Text },
                new TrackedRule { Name = "mate-kill", Pattern = "orc legionnaire", Kind = WatchKind.Text },
            ]);

            using var w = new LogWatcher(stats) { DeferIngestForTests = true };
            w.SelectTeammate(mate);
            w.Select(own);
            // The one full-file ingest poll: PollPrimary throws on the "orc sentry"
            // line, swallows it into LastError, and — with the fix — Discards the
            // teammate's still-buffered "orc legionnaire" line rather than deferring
            // it. InitialIngestDone still flips true; PollPrimary's own containment
            // means the throw never reaches FinishInitialIngest.
            w.FinishInitialIngest(w.SelectGeneration);
            Assert.True(w.InitialIngestDone);
            Assert.NotNull(w.LastError);
            // The teammate's OWN isolated instance must not have the "orc legionnaire"
            // kill either — discarded, not merely deferred, by the same poisoned tick.
            Assert.Equal(0, w.TeammateStats!.Snapshot().YourKillCount);

            // A second, otherwise-uneventful poll (no new bytes anywhere) is where
            // the old bug dispatched the deferred line — "live", after ingest.
            w.PollForTests();

            Assert.Equal(2, stats.Snapshot().YourKillCount);   // both OWN kills only
            Assert.DoesNotContain(otherMatches, l => l.Contains("orc legionnaire"));
            Assert.Equal(0, w.TeammateStats.Snapshot().YourKillCount);   // still never dispatched
        }
        finally
        {
            try { Directory.Delete(ownDir, recursive: true); } catch { }
            try { Directory.Delete(mateDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void APoisonedPollIsDetectedEvenWhenTheSameExceptionInstanceRecurs()
    {
        // Regression for the exception-IDENTITY blind spot: the old Poll() detected a
        // poisoned PollPrimary by `!ReferenceEquals(LastError, before)`. A consumer
        // that caches ONE exception instance and rethrows it on every matching
        // primary line reads as "unchanged" — and therefore as SUCCESS — on the
        // second and every later poll, because the rethrown instance is reference-
        // equal to what was already sitting in LastError. The fix clears LastError to
        // null immediately before PollPrimary and tests for non-null after, which
        // still catches a rethrown identical instance. Proven across TWO polls, with
        // the SAME exception object both times, and a teammate line buffered past the
        // bound each time that must be discarded on BOTH — never dispatched.
        var ownDir = Directory.CreateTempSubdirectory("eqbuddy-watch-own-").FullName;
        var mateDir = Directory.CreateTempSubdirectory("eqbuddy-watch-mate-").FullName;
        try
        {
            var poison = new InvalidOperationException("poison");   // ONE instance, rethrown every poll
            var own = WriteLog(ownDir, "eqlog_Kaybek_freeport.txt",
                "[Sat Jul 18 15:00:00 2026] You have slain orc sentry!");   // matches the poison rule
            var mate = WriteLog(mateDir, "eqlog_Buddy_freeport.txt",
                "[Sat Jul 18 15:00:05 2026] You have slain orc legionnaire!");   // buffered past the bound

            var stats = new SessionStats();
            stats.TextMatched += raw => { if (raw.Line.Contains("orc sentry")) throw poison; };
            stats.RefreshTextPatterns(
            [
                new TrackedRule { Name = "poison", Pattern = "orc sentry", Kind = WatchKind.Text },
            ]);

            using var w = new LogWatcher(stats) { DeferIngestForTests = true };
            w.SelectTeammate(mate);
            w.Select(own);
            // Poll #1 (full-file ingest): PollPrimary throws `poison` on "orc sentry".
            // The teammate's buffered "orc legionnaire" line (timestamped after it)
            // must be discarded, not deferred.
            w.FinishInitialIngest(w.SelectGeneration);
            Assert.True(w.InitialIngestDone);
            Assert.Same(poison, w.LastError);
            Assert.Equal(1, stats.Snapshot().YourKillCount);   // own's "orc sentry" only
            Assert.DoesNotContain(stats.RecentLines(), l => l.Message.Contains("orc legionnaire"));
            // The teammate's OWN isolated instance must not have the discarded kill.
            Assert.Equal(0, w.TeammateStats!.Snapshot().YourKillCount);

            // A new matching line on each side, between polls.
            File.AppendAllText(own, "[Sat Jul 18 15:00:10 2026] You have slain orc sentry!\n");
            File.AppendAllText(mate, "[Sat Jul 18 15:00:15 2026] You have slain orc bandit!\n");

            // Poll #2, driven directly via the test seam: PollPrimary throws the SAME
            // `poison` instance again. The old ReferenceEquals check saw LastError
            // holding that same reference both before and after the call and read it
            // as unchanged; the fix must still detect this poll as poisoned.
            w.PollForTests();
            Assert.Same(poison, w.LastError);
            Assert.Equal(2, stats.Snapshot().YourKillCount);   // both own "orc sentry" kills only
            Assert.DoesNotContain(stats.RecentLines(), l => l.Message.Contains("orc legionnaire"));
            Assert.DoesNotContain(stats.RecentLines(), l => l.Message.Contains("orc bandit"));
            Assert.Equal(0, w.TeammateStats.Snapshot().YourKillCount);   // both discards held
        }
        finally
        {
            try { Directory.Delete(ownDir, recursive: true); } catch { }
            try { Directory.Delete(mateDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AMidSessionTeammatePickDoesNotReplayThePrimary()
    {
        // Step 5 (plan Part 3b, U1): the old design forced a full re-Select of the
        // PRIMARY log on every mid-session teammate pick, purely to stop the
        // teammate's history from landing in the SHARED session clock with old
        // timestamps. With the teammate driving its own isolated SessionStats (Step
        // 3), that hazard is gone by construction — a pick no longer touches the
        // primary's SelectGeneration, InitialIngestDone, or its own kill count at all.
        var ownDir = Directory.CreateTempSubdirectory("eqbuddy-watch-own-").FullName;
        var mateDir = Directory.CreateTempSubdirectory("eqbuddy-watch-mate-").FullName;
        try
        {
            var own = WriteLog(ownDir, "eqlog_Kaybek_freeport.txt",
                "[Sat Jul 18 15:00:00 2026] You have slain orc pawn!");
            var mate = WriteLog(mateDir, "eqlog_Buddy_freeport.txt",
                "[Sat Jul 18 15:00:05 2026] You have slain orc centurion!");

            var stats = new SessionStats();
            using var w = new LogWatcher(stats) { DeferIngestForTests = true };
            w.Select(own);
            w.FinishInitialIngest(w.SelectGeneration);
            Assert.True(w.InitialIngestDone);
            Assert.Equal(1, stats.Snapshot().YourKillCount);

            var genBefore = w.SelectGeneration;
            w.SelectTeammate(mate);   // no re-Select of the primary

            Assert.Equal(genBefore, w.SelectGeneration);   // unchanged — no replay was triggered
            Assert.True(w.InitialIngestDone);              // never touched, let alone reset
            Assert.Equal(1, stats.Snapshot().YourKillCount);
            Assert.Equal("Kaybek", stats.CharacterName);

            // The teammate's own data arrives on the NEXT poll (the ordinary 150 ms
            // tick in production), not immediately at pick time.
            w.PollForTests();
            Assert.Equal(1, w.TeammateStats!.Snapshot().YourKillCount);
            Assert.Equal(1, stats.Snapshot().YourKillCount);   // still untouched
        }
        finally
        {
            try { Directory.Delete(ownDir, recursive: true); } catch { }
            try { Directory.Delete(mateDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SelectTeammateRefusesAPathEqualToThePrimaryLog()
    {
        // Finding 3: a teammate path equal to the primary log keeps its own
        // independent offset and re-dispatches every line the primary already
        // dispatched, doubling every stat. The refusal must be OBSERVABLE, not a
        // silent no-op — LastError is how every other watcher problem surfaces.
        var dir = Directory.CreateTempSubdirectory("eqbuddy-watch-").FullName;
        try
        {
            var path = WriteLog(dir, "eqlog_Kaybek_freeport.txt",
                "[Sat Jul 18 15:00:00 2026] You have slain orc pawn!");

            var stats = new SessionStats();
            using var w = new LogWatcher(stats) { DeferIngestForTests = true };
            w.Select(path);
            w.FinishInitialIngest(w.SelectGeneration);

            w.SelectTeammate(path);   // same file as the primary — must be refused

            Assert.Null(w.TeammatePath);
            Assert.NotNull(w.LastError);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void SelectTeammateRefusalIsCaseInsensitiveAndPathNormalized()
    {
        var dir = Directory.CreateTempSubdirectory("eqbuddy-watch-").FullName;
        try
        {
            var path = WriteLog(dir, "eqlog_Kaybek_freeport.txt",
                "[Sat Jul 18 15:00:00 2026] You have slain orc pawn!");

            var stats = new SessionStats();
            using var w = new LogWatcher(stats) { DeferIngestForTests = true };
            w.Select(path);
            w.FinishInitialIngest(w.SelectGeneration);

            // Same file, different casing — a raw string comparison would miss this.
            w.SelectTeammate(path.ToUpperInvariant());

            Assert.Null(w.TeammatePath);
            Assert.NotNull(w.LastError);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void SelectingAPrimaryEqualToAnAlreadyChosenTeammateClearsTheTeammateTail()
    {
        // Finding 3's reverse ordering: Select(path) landing the PRIMARY on a path
        // already chosen as the teammate must not leave a self-referencing tail live.
        var dir = Directory.CreateTempSubdirectory("eqbuddy-watch-").FullName;
        try
        {
            var path = WriteLog(dir, "eqlog_Kaybek_freeport.txt",
                "[Sat Jul 18 15:00:00 2026] You have slain orc pawn!");

            var stats = new SessionStats();
            using var w = new LogWatcher(stats) { DeferIngestForTests = true };
            w.SelectTeammate(path);   // teammate first — nothing primary yet
            Assert.Equal(path, w.TeammatePath);

            w.Select(path);   // primary now lands on the SAME file
            w.FinishInitialIngest(w.SelectGeneration);

            Assert.Null(w.TeammatePath);   // no self-referencing tail left live
            Assert.NotNull(w.LastError);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void PickingATeammateBeforeAnyLogIsSelectedDoesNotReplay()
    {
        var mateDir = Directory.CreateTempSubdirectory("eqbuddy-watch-mate-").FullName;
        try
        {
            var mate = WriteLog(mateDir, "eqlog_Buddy_freeport.txt",
                "[Sat Jul 18 15:00:05 2026] You have slain orc centurion!");

            var stats = new SessionStats();
            using var w = new LogWatcher(stats) { DeferIngestForTests = true };
            w.SelectTeammate(mate);   // nothing selected yet — the constructor case

            Assert.Equal(0, w.SelectGeneration);
            Assert.Null(w.CurrentPath);
        }
        finally { try { Directory.Delete(mateDir, recursive: true); } catch { } }
    }

    // ---- Step 6b: session-boundary coupling (plan Part 6) ----

    [Fact]
    public void AWatchedSessionRolloverResetsTheTeammateTotals()
    {
        // The duo session IS the watched character's session (plan Part 2c) — when
        // THEIR session rolls (a forward 60-minute gap), a stale teammate total from
        // the previous session must not go on combining with the freshly-rolled one.
        var ownDir = Directory.CreateTempSubdirectory("eqbuddy-watch-own-").FullName;
        var mateDir = Directory.CreateTempSubdirectory("eqbuddy-watch-mate-").FullName;
        try
        {
            var own = WriteLog(ownDir, "eqlog_Kaybek_freeport.txt",
                "[Sat Jul 18 15:00:00 2026] You have slain orc pawn!");
            var mate = WriteLog(mateDir, "eqlog_Buddy_freeport.txt",
                "[Sat Jul 18 15:00:05 2026] You have slain orc centurion!");

            var stats = new SessionStats();
            using var w = new LogWatcher(stats) { DeferIngestForTests = true };
            w.Select(own);
            w.FinishInitialIngest(w.SelectGeneration);   // InitialIngestDone flips true here
            w.SelectTeammate(mate);                      // wires the rollover hook
            w.PollForTests();
            Assert.Equal(1, w.TeammateStats!.Snapshot().YourKillCount);

            // A >60-minute forward gap in the PRIMARY's own log rolls ITS session —
            // live, well after InitialIngestDone.
            File.AppendAllText(own, "[Sat Jul 18 16:05:00 2026] You have slain orc sentry!\n");
            w.PollForTests();

            Assert.Equal(0, w.TeammateStats.Snapshot().YourKillCount);   // wiped by the roll
        }
        finally
        {
            try { Directory.Delete(ownDir, recursive: true); } catch { }
            try { Directory.Delete(mateDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TheInitialIngestReplayResetsTheTeammateOnEveryHistoricalRollToo()
    {
        // Repair round C2, correcting this test's own prior assumption: the primary's
        // OWN initial full-file ingest replays every HISTORICAL roll in its log, and
        // the OLD guard (InitialIngestDone, staying false for the whole of this
        // ingest poll) skipped resetting the teammate on every one of them — so a
        // teammate kill from a session the primary's log had already MOVED PAST
        // survived into whatever became the FINAL (current) session once replay
        // finished. `PrimarySessionStart`'s own draining gate (repair round C2, part
        // i) rejects a teammate line strictly before the CURRENT bound, but the two
        // lines here straddle the roll itself: the teammate's kill lands one minute
        // BEFORE the primary's own roll-triggering line, while the bound this poll
        // still reads is the OLD (pre-roll) session's start — so the drain gate alone
        // admits it, and only a reset AT the roll (now unconditional) discards it.
        var ownDir = Directory.CreateTempSubdirectory("eqbuddy-watch-own-").FullName;
        var mateDir = Directory.CreateTempSubdirectory("eqbuddy-watch-mate-").FullName;
        try
        {
            // A historical gap INSIDE the primary's own file — replaying it rolls the
            // primary's OWN session mid-ingest, before InitialIngestDone ever flips.
            var own = WriteLog(ownDir, "eqlog_Kaybek_freeport.txt",
                "[Fri Jul 17 10:00:00 2026] You have slain orc legionnaire!",
                "[Sat Jul 18 20:00:00 2026] You have slain orc pawn!");   // >60 min later — rolls mid-ingest
            // Strictly before the primary's roll line, so it dispatches first within
            // the SAME ingest poll, using the STILL-OLD bound.
            var mate = WriteLog(mateDir, "eqlog_Buddy_freeport.txt",
                "[Sat Jul 18 19:59:00 2026] You have slain orc centurion!");

            var stats = new SessionStats();
            using var w = new LogWatcher(stats) { DeferIngestForTests = true };
            w.SelectTeammate(mate);   // wires the rollover hook — BEFORE any ingest has run
            w.Select(own);
            w.FinishInitialIngest(w.SelectGeneration);   // one full-file ingest poll

            // The historical roll happened DURING this same ingest — the teammate's
            // kill, applied just before it under the STALE bound, must NOT survive
            // into the session the primary actually ended replay on.
            Assert.Equal(0, w.TeammateStats!.Snapshot().YourKillCount);
        }
        finally
        {
            try { Directory.Delete(ownDir, recursive: true); } catch { }
            try { Directory.Delete(mateDir, recursive: true); } catch { }
        }
    }

    /// <summary>The positive case beside the one above: a teammate kill that lands
    /// AFTER the primary's own final historical roll (i.e., genuinely within what
    /// becomes the current session once replay finishes) must survive it.</summary>
    [Fact]
    public void ATeammateKillAfterTheFinalHistoricalRollSurvivesInitialIngest()
    {
        var ownDir = Directory.CreateTempSubdirectory("eqbuddy-watch-own-").FullName;
        var mateDir = Directory.CreateTempSubdirectory("eqbuddy-watch-mate-").FullName;
        try
        {
            var own = WriteLog(ownDir, "eqlog_Kaybek_freeport.txt",
                "[Fri Jul 17 10:00:00 2026] You have slain orc legionnaire!",
                "[Sat Jul 18 20:00:00 2026] You have slain orc pawn!");   // rolls mid-ingest
            var mate = WriteLog(mateDir, "eqlog_Buddy_freeport.txt",
                "[Sat Jul 18 20:00:05 2026] You have slain orc centurion!");   // after the roll

            var stats = new SessionStats();
            using var w = new LogWatcher(stats) { DeferIngestForTests = true };
            w.SelectTeammate(mate);
            w.Select(own);
            w.FinishInitialIngest(w.SelectGeneration);

            Assert.Equal(1, w.TeammateStats!.Snapshot().YourKillCount);
        }
        finally
        {
            try { Directory.Delete(ownDir, recursive: true); } catch { }
            try { Directory.Delete(mateDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TeammateLinesReachOnlyStatsAndMez()
    {
        // BuffTracker models the WATCHED character's own body: a cast + landing pair
        // from a teammate's log must never open an "active" buff entry, or a spell you
        // never cast would start a countdown on your own HUD. A landing alone is
        // enough to prove it — BuffTracker.OnLanding opens an active entry on ANY
        // BuffLandedEvent it can attribute (resolved to a cast or not) — so this
        // deliberately stops short of a worn-off line: OnFade clears the active entry
        // unconditionally regardless of source, which would erase the very difference
        // this test exists to show.
        var castLine = "[Sat Jul 18 15:00:00 2026] You begin casting Armor of Faith.";
        var landLine = "[Sat Jul 18 15:00:02 2026] You feel the favor of the gods upon you.";

        var ownDir = Directory.CreateTempSubdirectory("eqbuddy-watch-own-").FullName;
        var mateDir = Directory.CreateTempSubdirectory("eqbuddy-watch-mate-").FullName;
        try
        {
            // Fed as the PRIMARY: BuffTracker sees both lines and opens an active entry.
            var ownStats = new SessionStats();
            var ownBuffs = new BuffTracker();   // no AttachStore — nothing persisted
            var ownLog = WriteLog(ownDir, "eqlog_Kaybek_freeport.txt", castLine, landLine);
            using (var wOwn = new LogWatcher(ownStats) { Buffs = ownBuffs, DeferIngestForTests = true })
            {
                wOwn.Select(ownLog);
                wOwn.FinishInitialIngest(wOwn.SelectGeneration);
            }
            Assert.Equal(1, ownBuffs.ActiveCount);

            // The SAME two lines fed as the TEAMMATE, alongside an unrelated primary
            // log: BuffTracker (and every consumer but the teammate's OWN Stats and
            // the shared Mez) must see nothing from them at all — proven alongside the
            // POSITIVE paths, so the negative claim ("only Stats and Mez") isn't
            // standing on top of a feed that reached nothing at all. The teammate log
            // also carries a kill (proving Stats — now the teammate's OWN isolated
            // instance, Step 3) and a mez cast whose landing is on the PRIMARY log
            // (proving Mez) — "Mesmerize" is the same spell MezTrackerTests casts.
            var primaryStats = new SessionStats();
            var mateBuffs = new BuffTracker();
            var mez = new MezTracker();
            var primaryLog = WriteLog(ownDir, "eqlog_Owner_freeport.txt",
                "[Sat Jul 18 15:00:00 2026] You have slain orc pawn!",
                "[Sat Jul 18 15:00:06 2026] an orc pawn has been mesmerized.");
            var mateLog = WriteLog(mateDir, "eqlog_Buddy_freeport.txt",
                castLine, landLine,
                "[Sat Jul 18 15:00:03 2026] You begin casting Mesmerize.",
                "[Sat Jul 18 15:00:04 2026] You have slain orc centurion!");

            using var w = new LogWatcher(primaryStats)
                { Buffs = mateBuffs, Mez = mez, DeferIngestForTests = true };
            w.SelectTeammate(mateLog);
            w.Select(primaryLog);
            w.FinishInitialIngest(w.SelectGeneration);

            Assert.Equal(0, mateBuffs.ActiveCount);   // never reached BuffTracker
            // The PRIMARY's own Stats shows only its own kill — the teammate's never
            // touches it (Step 3's isolation).
            Assert.Equal(1, primaryStats.Snapshot().YourKillCount);
            // The teammate's OWN isolated instance DID get their kill — proving the
            // unconditional dispatch actually applies, not just that BuffTracker's
            // exclusion works.
            Assert.Equal(1, w.TeammateStats!.Snapshot().YourKillCount);
            Assert.Contains(w.TeammateStats.Snapshot().YourKills, kv => kv.Name == "Orc centurion");
            // Mez DID get the teammate's cast: paired with the landing line in the
            // PRIMARY log (mez landings are bystander-visible — anyone's log sees
            // them), the tracker shows an active mez on "Orc pawn".
            var activeMezzes = mez.Snapshot(new DateTime(2026, 7, 18, 15, 0, 6));
            Assert.Contains(activeMezzes, m => m.Target == "Orc pawn");
        }
        finally
        {
            try { Directory.Delete(ownDir, recursive: true); } catch { }
            try { Directory.Delete(mateDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ATeammateCastBetweenOurCastAndItsLandingDoesNotStealTheCharmCorrelation()
    {
        // Finding 1 end-to-end: _pendingCast is the correlation key CharmTracker.OnCharmed
        // reads to decide whether a "has been charmed." landing completes OUR cast. Before
        // the fix, a teammate's SpellCastEvent landing between our own cast and its landing
        // overwrote _pendingCast with THEIR spell, so the landing correlated against the
        // wrong spell (not Charm) and the pet was never claimed.
        var ownDir = Directory.CreateTempSubdirectory("eqbuddy-watch-own-").FullName;
        var mateDir = Directory.CreateTempSubdirectory("eqbuddy-watch-mate-").FullName;
        try
        {
            var ownLog = WriteLog(ownDir, "eqlog_Kaybek_freeport.txt",
                "[Sat Jul 18 15:00:00 2026] You begin casting Charm.",
                "[Sat Jul 18 15:00:02 2026] orc pawn has been charmed.");
            var mateLog = WriteLog(mateDir, "eqlog_Buddy_freeport.txt",
                "[Sat Jul 18 15:00:01 2026] You begin casting Minor Healing.");

            var stats = new SessionStats();
            using var w = new LogWatcher(stats) { DeferIngestForTests = true };
            w.SelectTeammate(mateLog);
            w.Select(ownLog);
            w.FinishInitialIngest(w.SelectGeneration);

            // The charm landed on OUR cast, not the teammate's unrelated one in between,
            // so the pet is claimed and credited by name.
            Assert.Equal("Orc pawn", stats.Snapshot().PetName);
        }
        finally
        {
            try { Directory.Delete(ownDir, recursive: true); } catch { }
            try { Directory.Delete(mateDir, recursive: true); } catch { }
        }
    }

    // ---- ACL helpers for the finding-6 regression below: icacls /deny "(RD)" (Read
    // Data) leaves File.Exists — a lighter, attributes-only check — reporting the file
    // still exists, while an actual data read throws UnauthorizedAccessException. That
    // is exactly the shape of a real ACL-denied synced teammate log. ----

    private static void DenyRead(string path) =>
        RunIcacls($"\"{path}\" /deny \"{Environment.UserName}:(RD)\"");

    private static void ResetAcl(string path) => RunIcacls($"\"{path}\" /reset");

    private static void RunIcacls(string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = "icacls",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        });
        p!.WaitForExit(5000);
    }

    [Fact]
    public void ATeammateReadErrorSurvivingAPoisonedTickIsNotSuppressedForever()
    {
        // Finding 6: `LastError = mateErr; _seenMateError = mateErr;` used to be
        // committed TOGETHER, before learning whether PollPrimary's own throw on the
        // SAME tick would overwrite LastError with its own exception. When both
        // happen on one tick (an ACL-denied teammate file plus one consumer throw),
        // the primary's exception correctly wins THAT tick — but _seenMateError had
        // already advanced, so every LATER tick with the same (type, message) mate
        // error was silently suppressed forever, even once the primary stopped
        // throwing. The fix only commits _seenMateError once the mate error actually
        // survives to reach LastError.
        var ownDir = Directory.CreateTempSubdirectory("eqbuddy-watch-own-").FullName;
        var mateDir = Directory.CreateTempSubdirectory("eqbuddy-watch-mate-").FullName;
        var matePath = "";
        try
        {
            var own = WriteLog(ownDir, "eqlog_Kaybek_freeport.txt",
                "[Sat Jul 18 15:00:00 2026] You have slain orc sentry!");   // matches the poison rule
            matePath = WriteLog(mateDir, "eqlog_Buddy_freeport.txt",
                "[Sat Jul 18 15:00:01 2026] You have slain orc legionnaire!");
            DenyRead(matePath);   // File.Exists stays true; a real read throws UnauthorizedAccessException

            var stats = new SessionStats();
            stats.TextMatched += raw =>
            {
                if (raw.Line.Contains("orc sentry")) throw new InvalidOperationException("poison");
            };
            stats.RefreshTextPatterns(
                [new TrackedRule { Name = "poison", Pattern = "orc sentry", Kind = WatchKind.Text }]);

            using var w = new LogWatcher(stats) { DeferIngestForTests = true };
            w.SelectTeammate(matePath);
            w.Select(own);
            // Tick 1 (full-file ingest): the teammate's ACL-denied file throws AND the
            // primary's own consumer throws on the SAME tick — the primary's own
            // exception wins this tick, matching existing (unchanged) behaviour.
            w.FinishInitialIngest(w.SelectGeneration);
            Assert.IsType<InvalidOperationException>(w.LastError);

            // Tick 2: the primary has nothing new (no throw this time); the teammate's
            // file is STILL denied, with the identical (type, message). The mate
            // error must surface now — not stay silenced by tick 1's own exception,
            // which has nothing further to say on a tick it never ran on.
            w.PollForTests();
            Assert.IsType<UnauthorizedAccessException>(w.LastError);
        }
        finally
        {
            if (matePath.Length > 0) ResetAcl(matePath);
            try { Directory.Delete(ownDir, recursive: true); } catch { }
            try { Directory.Delete(mateDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SelectTeammateRefusesAPathInsideTheConfiguredLogsFolder()
    {
        // Repair round R3: LogWatcher now carries the Logs folder itself (set once by
        // the host, the same pattern as Mez/Buffs/Raids), so it can enforce F4's rule
        // continuously rather than only at the moment the file picker ran.
        var dir = Directory.CreateTempSubdirectory("eqbuddy-watch-").FullName;
        try
        {
            var mate = WriteLog(dir, "eqlog_Buddy_freeport.txt",
                "[Sat Jul 18 15:00:00 2026] You have slain orc pawn!");

            var stats = new SessionStats();
            using var w = new LogWatcher(stats) { DeferIngestForTests = true, LogFolder = () => dir };
            w.SelectTeammate(mate);

            Assert.Null(w.TeammatePath);
            Assert.NotNull(w.LastError);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void SelectRevalidatesTheTeammatePathAgainstALogsFolderConfiguredLater()
    {
        // Repair round R3: a teammate path saved earlier (or picked before the Logs
        // folder was known) must not survive a LATER Logs-folder change that now
        // contains it — F4's promise is continuous, not a one-time file-dialog check.
        // LogWatcher.LogFolder is read fresh by every Select(), which the host already
        // calls after every Logs-folder change (FollowActiveCharacter).
        var ownDir = Directory.CreateTempSubdirectory("eqbuddy-watch-own-").FullName;
        var mateDir = Directory.CreateTempSubdirectory("eqbuddy-watch-mate-").FullName;
        try
        {
            var own = WriteLog(ownDir, "eqlog_Kaybek_freeport.txt",
                "[Sat Jul 18 15:00:00 2026] You have slain orc pawn!");
            var mate = WriteLog(mateDir, "eqlog_Buddy_freeport.txt",
                "[Sat Jul 18 15:00:05 2026] You have slain orc centurion!");

            var stats = new SessionStats();
            using var w = new LogWatcher(stats) { DeferIngestForTests = true };
            w.SelectTeammate(mate);   // fine — mateDir isn't a Logs folder yet
            Assert.Equal(mate, w.TeammatePath);

            // The Logs folder is (re)configured to be the teammate's OWN directory —
            // exactly the shape of a player pointing EQBuddy at a folder that happens
            // to already contain a synced teammate copy.
            w.LogFolder = () => mateDir;
            w.Select(own);   // the host's own next step after any Logs-folder change

            Assert.Null(w.TeammatePath);
            Assert.NotNull(w.LastError);
        }
        finally
        {
            try { Directory.Delete(ownDir, recursive: true); } catch { }
            try { Directory.Delete(mateDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ALogsFolderChangeClearsATeammatePathThatIsNowInsideItEvenWithoutASelect()
    {
        // Step 5 (plan Part 5b-iv): the PREVIOUS test proves revalidation happens when
        // the host's own next step (Select, via FollowActiveCharacter) runs — but that
        // does not always happen (no active file, or the primary path is unchanged).
        // The fix lives in the WATCHER's own Poll(), not the host: this proves the
        // SAME refusal fires from a poll tick ALONE, with no Select() in between.
        var ownDir = Directory.CreateTempSubdirectory("eqbuddy-watch-own-").FullName;
        var mateDir = Directory.CreateTempSubdirectory("eqbuddy-watch-mate-").FullName;
        try
        {
            var own = WriteLog(ownDir, "eqlog_Kaybek_freeport.txt",
                "[Sat Jul 18 15:00:00 2026] You have slain orc pawn!");
            var mate = WriteLog(mateDir, "eqlog_Buddy_freeport.txt",
                "[Sat Jul 18 15:00:05 2026] You have slain orc centurion!");

            var stats = new SessionStats();
            using var w = new LogWatcher(stats) { DeferIngestForTests = true };
            w.Select(own);
            w.FinishInitialIngest(w.SelectGeneration);
            w.SelectTeammate(mate);   // fine — mateDir isn't a Logs folder yet
            Assert.Equal(mate, w.TeammatePath);

            // The Logs folder is (re)configured to be the teammate's OWN directory —
            // deliberately with NO Select() call afterward.
            w.LogFolder = () => mateDir;
            w.PollForTests();   // an ordinary tick — no active-character change, nothing else

            Assert.Null(w.TeammatePath);
            Assert.NotNull(w.LastError);
        }
        finally
        {
            try { Directory.Delete(ownDir, recursive: true); } catch { }
            try { Directory.Delete(mateDir, recursive: true); } catch { }
        }
    }

    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe", Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true,
            });
            p!.WaitForExit(5000);
            return p.ExitCode == 0 && Directory.Exists(link);
        }
        catch { return false; }
    }

    [Fact]
    public void ATeammateFileThatAppearsInsideTheLogsFolderIsDroppedOnItsFirstRead()
    {
        // Closes the residual half of Part 5b-iii: the parent-directory fallback
        // (Step 1) resolves a junction for a MISSING file whose PARENT already exists
        // — but here neither the sync directory nor the file exists yet at pick time,
        // so even that fallback has nothing to resolve and pick-time validation is
        // purely lexical (correctly sees no overlap: the two path strings share no
        // prefix). Only once the sync tool materializes the directory as a junction
        // INTO the Logs folder, and drops the file inside it, does real identity say
        // "inside" — and nothing but a poll tick's own re-validation catches that.
        var root = Directory.CreateTempSubdirectory("eqbuddy-watch-appear-").FullName;
        var ownDir = Directory.CreateTempSubdirectory("eqbuddy-watch-own-").FullName;
        try
        {
            var logsFolder = Directory.CreateDirectory(Path.Combine(root, "logs")).FullName;
            var own = WriteLog(ownDir, "eqlog_Kaybek_freeport.txt",
                "[Sat Jul 18 15:00:00 2026] You have slain orc pawn!");

            var syncDir = Path.Combine(root, "sync");
            var matePath = Path.Combine(syncDir, "eqlog_Buddy_freeport.txt");
            Assert.False(Directory.Exists(syncDir));

            var stats = new SessionStats();
            using var w = new LogWatcher(stats) { DeferIngestForTests = true, LogFolder = () => logsFolder };
            w.Select(own);
            w.FinishInitialIngest(w.SelectGeneration);
            w.SelectTeammate(matePath);
            Assert.Equal(matePath, w.TeammatePath);   // accepted — nothing to see yet

            if (!TryCreateJunction(syncDir, logsFolder))
                Assert.Fail("Could not create the junction required to exercise file-appearance revalidation.");
            File.WriteAllText(matePath, "[Sat Jul 18 15:00:05 2026] You have slain orc centurion!\n");

            w.PollForTests();

            Assert.Null(w.TeammatePath);
            Assert.NotNull(w.LastError);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
            try { Directory.Delete(ownDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AReentrantSelectLandingOnTheTeammatesOwnPathDiscardsItInsteadOfLeavingItLive()
    {
        // Repair round R5: the reverse-order clear (finding 3) used to set
        // `_teammate = null` WITHOUT calling Discard() first — unlike SelectTeammate's
        // own clear path. Discard() is what bumps the tail's generation, which is the
        // ONLY thing that stops an in-flight DrainBefore (a re-entrant Select from a
        // consumer callback, synchronously, mid-drain) from continuing to dispatch its
        // still-buffered lines into the session that SAME re-entrant Select() just
        // reset — exactly the hazard TeammateLogTail's own generation field exists to
        // prevent (see AReentrantResetDuringDrainLeavesNoStaleState).
        //
        // Step 3 update: the gap that used to roll the SHARED SessionStats now rolls
        // only the teammate's OWN isolated instance — proving the hazard is unchanged
        // means subscribing to THAT instance's SessionRolledOver instead of the
        // primary's (which a teammate's gap can no longer reach at all).
        var ownDir = Directory.CreateTempSubdirectory("eqbuddy-watch-own-").FullName;
        var mateDir = Directory.CreateTempSubdirectory("eqbuddy-watch-mate-").FullName;
        try
        {
            var own = WriteLog(ownDir, "eqlog_Kaybek_freeport.txt",
                "[Sat Jul 18 15:00:00 2026] You have slain orc pawn!");
            var mate = WriteLog(mateDir, "eqlog_Buddy_freeport.txt",
                "[Sat Jul 18 15:00:00 2026] You have slain orc guard!",
                "[Sat Jul 18 16:05:00 2026] You have slain orc sentry!",     // >60 min gap — rolls the TEAMMATE's own session INSIDE Apply
                "[Sat Jul 18 16:05:01 2026] You have slain orc centurion!"); // buffered right after the reentrant Select — must not survive it

            var stats = new SessionStats();
            using var w = new LogWatcher(stats) { DeferIngestForTests = true };
            w.SelectTeammate(mate);   // nothing selected yet — the constructor case
            // Repair round C10: TeammateStats is now a read-only wrapper that carries no
            // event to subscribe to — this test deliberately wires a real event handler
            // on the teammate's real instance to exercise the reentrancy hazard, so it
            // reaches it via the internal test-only escape hatch instead.
            var mateStats = w.TeammateStatsForTests!;   // captured before the reentrant call orphans it
            var reentered = false;
            mateStats.SessionRolledOver += () =>
            {
                if (reentered) return;
                reentered = true;
                // A real trigger for this exact hazard: the primary lands on the SAME
                // file as the already-installed teammate — the reverse ordering
                // finding 3 (and R3/R4) already refuse this from OUTSIDE a drain; this
                // proves it is handled correctly from INSIDE one too.
                w.Select(mate);
            };

            w.Select(own);
            w.FinishInitialIngest(w.SelectGeneration);
            // The reentrant Select bumped SelectGeneration again; finish that ingest
            // too so the watcher is left in a settled state.
            w.FinishInitialIngest(w.SelectGeneration);

            // The DISCRIMINATING fact is "Orc centurion", buffered immediately after
            // the reentrant Select() within the SAME drain batch: a properly
            // Discard()-ed outgoing tail never reaches it at all (the generation check
            // trips and the loop returns first) — checked against the CAPTURED
            // reference, since the tail (and the teammate slot) is gone by now.
            Assert.DoesNotContain(mateStats.Snapshot().YourKills, kv => kv.Name == "Orc centurion");
            // "Orc sentry" (the line that rolled the teammate's own session and fired
            // the reentrant handler) DID apply before the reentrancy fired — proving
            // the discriminating line is specifically the one buffered AFTER it.
            Assert.Contains(mateStats.Snapshot().YourKills, kv => kv.Name == "Orc sentry");
            // The primary landed on the teammate's own path, so the teammate slot is
            // cleared entirely — matching SelectTeammate's own self-reference refusal.
            Assert.Null(w.TeammatePath);
            Assert.Null(w.TeammateStats);
        }
        finally
        {
            try { Directory.Delete(ownDir, recursive: true); } catch { }
            try { Directory.Delete(mateDir, recursive: true); } catch { }
        }
    }

    // ---- C3: subtracting a teammate's SELF-REPORTED per-target kills from the
    // primary's own (target-only) party breakdown can delete a THIRD groupmate's
    // real, visible kill of the same target name — full pipeline, real files. ----

    [Fact]
    public void ATeammatesInvisibleKillsNeverDeleteAThirdGroupmatesVisibleKillInTheRealDuoSnapshot()
    {
        var ownDir = Directory.CreateTempSubdirectory("eqbuddy-watch-own-").FullName;
        var mateDir = Directory.CreateTempSubdirectory("eqbuddy-watch-mate-").FullName;
        try
        {
            // The primary's own log sees ONE third-party kill of "an orc pawn" —
            // from Grouper, a THIRD player, never from the teammate at all.
            var own = WriteLog(ownDir, "eqlog_Kaybek_freeport.txt",
                "[Sat Jul 18 15:00:00 2026] an orc pawn has been slain by Grouper!");
            // The teammate's OWN log self-reports FIVE kills of the same target
            // name, entirely out of the primary's sight (no line for them ever
            // appears in the primary's own log above).
            var mate = WriteLog(mateDir, "eqlog_Buddy_freeport.txt",
                "[Sat Jul 18 15:00:01 2026] You have slain an orc pawn!",
                "[Sat Jul 18 15:00:02 2026] You have slain an orc pawn!",
                "[Sat Jul 18 15:00:03 2026] You have slain an orc pawn!",
                "[Sat Jul 18 15:00:04 2026] You have slain an orc pawn!",
                "[Sat Jul 18 15:00:05 2026] You have slain an orc pawn!");

            var stats = new SessionStats();
            using var w = new LogWatcher(stats) { DeferIngestForTests = true };
            w.SelectTeammate(mate);
            w.Select(own);
            w.FinishInitialIngest(w.SelectGeneration);

            var duo = stats.DuoSnapshot(null, null);

            Assert.Equal(5, duo.YourKillCount);      // the teammate's kills still promote
            Assert.Equal(1, duo.PartyKillCount);      // Grouper's real, visible kill survives
            Assert.Contains(duo.PartyKillsByTarget, nc => nc.Name == "Orc pawn" && nc.Count == 1);
        }
        finally
        {
            try { Directory.Delete(ownDir, recursive: true); } catch { }
            try { Directory.Delete(mateDir, recursive: true); } catch { }
        }
    }

    // ---- C8: two logs, two clocks — full pipeline, real files. The primary's own
    // log sees a bystander-visible kill of the teammate; the teammate's own log
    // self-reports the identical kill, stamped by ITS OWN clock. Joining the two
    // samples the clock offset between them. ----

    [Fact]
    public void AnOrdinaryFewSecondsOfClockSkewIsEstimatedAndStillCombines()
    {
        var ownDir = Directory.CreateTempSubdirectory("eqbuddy-watch-own-").FullName;
        var mateDir = Directory.CreateTempSubdirectory("eqbuddy-watch-mate-").FullName;
        try
        {
            // A primer line establishes the primary's own SessionStart (repair round
            // C2's drain-hold gate refuses to drain ANY teammate line before that
            // bound exists at all) — then the SAME real-world kill: the primary's
            // own log sees it as a bystander 3 seconds "later" than the teammate's
            // own log reports it, ordinary clock skew between two unsynchronised
            // machines, not a timezone gap.
            var own = WriteLog(ownDir, "eqlog_Kaybek_freeport.txt",
                "[Sat Jul 18 15:00:00 2026] You have slain orc sentry!",
                "[Sat Jul 18 15:00:08 2026] an orc pawn has been slain by Buddy!");
            var mate = WriteLog(mateDir, "eqlog_Buddy_freeport.txt",
                "[Sat Jul 18 15:00:05 2026] You have slain an orc pawn!");

            var stats = new SessionStats();
            using var w = new LogWatcher(stats) { DeferIngestForTests = true };
            w.SelectTeammate(mate);
            w.Select(own);
            w.FinishInitialIngest(w.SelectGeneration);

            Assert.Equal(TimeSpan.FromSeconds(3), stats.EstimatedTeammateClockOffset);
            Assert.Equal(1, stats.TeammateClockOffsetSampleCount);
            Assert.False(stats.TeammateClockOffsetExceedsThreshold);

            var duo = stats.DuoSnapshot(null, null);
            Assert.NotNull(duo.Mate);   // ordinary skew must not block the combine
        }
        finally
        {
            try { Directory.Delete(ownDir, recursive: true); } catch { }
            try { Directory.Delete(mateDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void WithNoBystanderVisibleKillYetTheOffsetCannotBeDeterminedAndCombiningStillProceeds()
    {
        // Neither log has produced a joinable event yet — the estimate must read as
        // "cannot be determined" (null), never as a silent zero, and that must never
        // by itself block combining (only a CONFIRMED excess offset does).
        var ownDir = Directory.CreateTempSubdirectory("eqbuddy-watch-own-").FullName;
        var mateDir = Directory.CreateTempSubdirectory("eqbuddy-watch-mate-").FullName;
        try
        {
            var own = WriteLog(ownDir, "eqlog_Kaybek_freeport.txt",
                "[Sat Jul 18 15:00:00 2026] You have slain orc pawn!");
            var mate = WriteLog(mateDir, "eqlog_Buddy_freeport.txt",
                "[Sat Jul 18 15:00:05 2026] You have slain orc centurion!");

            var stats = new SessionStats();
            using var w = new LogWatcher(stats) { DeferIngestForTests = true };
            w.SelectTeammate(mate);
            w.Select(own);
            w.FinishInitialIngest(w.SelectGeneration);

            Assert.Null(stats.EstimatedTeammateClockOffset);
            Assert.Equal(0, stats.TeammateClockOffsetSampleCount);
            Assert.False(stats.TeammateClockOffsetExceedsThreshold);

            var duo = stats.DuoSnapshot(null, null);
            Assert.NotNull(duo.Mate);
        }
        finally
        {
            try { Directory.Delete(ownDir, recursive: true); } catch { }
            try { Directory.Delete(mateDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AClockOffsetBeyondTheThresholdIsSurfacedAndCombiningIsRefused()
    {
        var ownDir = Directory.CreateTempSubdirectory("eqbuddy-watch-own-").FullName;
        var mateDir = Directory.CreateTempSubdirectory("eqbuddy-watch-mate-").FullName;
        try
        {
            // A primer line establishes SessionStart (see the sibling test above for
            // why), then the SAME real-world kill, 10 minutes apart between the two
            // logs' own clocks — far beyond ordinary NTP-class skew
            // (ClockDriftEstimator.Threshold is 2 minutes), and enough that combining
            // the two timelines would be actively misleading rather than merely
            // imprecise.
            var own = WriteLog(ownDir, "eqlog_Kaybek_freeport.txt",
                "[Sat Jul 18 15:00:00 2026] You have slain orc sentry!",
                "[Sat Jul 18 15:10:00 2026] an orc pawn has been slain by Buddy!");
            var mate = WriteLog(mateDir, "eqlog_Buddy_freeport.txt",
                "[Sat Jul 18 15:00:00 2026] You have slain an orc pawn!");

            var stats = new SessionStats();
            using var w = new LogWatcher(stats) { DeferIngestForTests = true };
            w.SelectTeammate(mate);
            w.Select(own);
            w.FinishInitialIngest(w.SelectGeneration);

            Assert.Equal(TimeSpan.FromMinutes(10), stats.EstimatedTeammateClockOffset);
            Assert.True(stats.TeammateClockOffsetExceedsThreshold);

            // Refused, not merely degraded: the duo snapshot falls back to the SOLO
            // one exactly as it does with no teammate selected at all — Mate is null,
            // not a combine built on an untrustworthy timeline.
            var duo = stats.DuoSnapshot(null, null);
            Assert.Null(duo.Mate);
        }
        finally
        {
            try { Directory.Delete(ownDir, recursive: true); } catch { }
            try { Directory.Delete(mateDir, recursive: true); } catch { }
        }
    }
}
