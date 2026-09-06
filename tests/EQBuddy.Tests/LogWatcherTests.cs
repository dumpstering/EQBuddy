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

    // ---- teammate feed (a second log riding the same pipeline; see TeammateFeed) ----

    [Fact]
    public void TeammateLinesJoinTheSessionAfterTheOwnersIngest()
    {
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
            Assert.Equal(3, snap.YourKillCount);            // 1 own + 2 from the teammate's log
            Assert.Equal("Kaybek", stats.CharacterName);    // owner identity untouched
            Assert.Equal("Buddy", w.TeammateName);
            Assert.Contains(snap.Loot, l => l.Item == "Rusty Dagger");
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
            Assert.Equal(2, stats.Snapshot().YourKillCount);   // both joined

            // A LIVE primary is selected now, so clearing the teammate auto-replays it
            // (SelectTeammate) — otherwise the teammate's already-applied kill would
            // linger in the session forever, with no future Poll able to undo it.
            w.SelectTeammate(null);
            w.FinishInitialIngest(w.SelectGeneration);

            Assert.Equal(1, stats.Snapshot().YourKillCount);   // the teammate's kill no longer rides along
            Assert.Null(w.TeammateName);
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
        // Regression for the merge fix: ingesting the whole primary file and THEN the
        // whole teammate file let the clock run backwards the moment the teammate's
        // lines were older, and the teammate file's own internal 60-minute gap then
        // rolled the session — wiping everything the primary had already contributed.
        // Dispatching both files' lines in true timestamp order is what keeps
        // SessionStats' existing forward-gap roll seeing a real session.
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

            var snap = stats.Snapshot();
            // The Friday pair is far enough before Saturday's play that the FIRST
            // Saturday line (own's 20:00:00) rolls the session — same as it always
            // would on a real, single, chronologically-ordered log. Only the four
            // Saturday kills (2 own + 2 teammate) survive into the current session.
            Assert.Equal(4, snap.YourKillCount);
            Assert.Equal(new DateTime(2026, 7, 18, 20, 0, 0), snap.SessionStart);
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
        var ownDir = Directory.CreateTempSubdirectory("eqbuddy-watch-own-").FullName;
        var mateDir = Directory.CreateTempSubdirectory("eqbuddy-watch-mate-").FullName;
        try
        {
            var own = WriteLog(ownDir, "eqlog_Kaybek_freeport.txt",
                "[Sat Jul 18 20:00:00 2026] You have slain orc pawn!",
                "[Sat Jul 18 20:05:00 2026] You have slain orc centurion!");
            var mate = WriteLog(mateDir, "eqlog_Buddy_freeport.txt",
                "[Sat Jul 18 20:02:00 2026] You have slain orc guard!",
                "[Sat Jul 18 20:04:00 2026] You have slain orc sentry!");

            var stats = new SessionStats();
            using var w = new LogWatcher(stats) { DeferIngestForTests = true };
            w.SelectTeammate(mate);
            w.Select(own);
            w.FinishInitialIngest(w.SelectGeneration);

            var snap = stats.Snapshot();
            // The primary's LAST line (20:05) is the latest timestamp overall, even
            // though the teammate file is read second on every poll — not by which
            // file was read first: TeammateLogTail buffers and PollPrimary drains it
            // per primary line.
            Assert.Equal(new DateTime(2026, 7, 18, 20, 5, 0), snap.LastEventTime);
            Assert.Equal(4, snap.YourKillCount);

            // Count and LastEventTime hold even for a dispatch order that never
            // actually interleaved (e.g. both files concatenated whole, own-first) —
            // assert the EXACT order instead, via the raw-line ring both files feed.
            var recent = stats.RecentLines();   // oldest first
            Assert.Equal(4, recent.Count);
            Assert.Contains("orc pawn", recent[0].Message);        // own, 20:00
            Assert.Contains("orc guard", recent[1].Message);       // mate, 20:02
            Assert.Contains("orc sentry", recent[2].Message);      // mate, 20:04
            Assert.Contains("orc centurion", recent[3].Message);   // own, 20:05
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
        // uses a STRICT bound specifically so a tie resolves primary-first — this is
        // the LogWatcher-level proof of that rule, via the exact dispatch order
        // rather than via TeammateLogTail's own bound-line unit tests.
        var ownDir = Directory.CreateTempSubdirectory("eqbuddy-watch-own-").FullName;
        var mateDir = Directory.CreateTempSubdirectory("eqbuddy-watch-mate-").FullName;
        try
        {
            var own = WriteLog(ownDir, "eqlog_Kaybek_freeport.txt",
                "[Sat Jul 18 20:04:00 2026] You have slain orc pawn!");
            var mate = WriteLog(mateDir, "eqlog_Buddy_freeport.txt",
                "[Sat Jul 18 20:04:00 2026] You have slain orc guard!");

            var stats = new SessionStats();
            using var w = new LogWatcher(stats) { DeferIngestForTests = true };
            w.SelectTeammate(mate);
            w.Select(own);
            w.FinishInitialIngest(w.SelectGeneration);

            var recent = stats.RecentLines();   // oldest first
            Assert.Equal(2, recent.Count);
            Assert.Contains("orc pawn", recent[0].Message);    // own line, same second — dispatches first
            Assert.Contains("orc guard", recent[1].Message);   // teammate line, same second — dispatches after
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

            // A second, otherwise-uneventful poll (no new bytes anywhere) is where
            // the old bug dispatched the deferred line — "live", after ingest.
            w.PollForTests();

            Assert.Equal(2, stats.Snapshot().YourKillCount);   // both OWN kills only
            Assert.DoesNotContain(otherMatches, l => l.Contains("orc legionnaire"));
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
        }
        finally
        {
            try { Directory.Delete(ownDir, recursive: true); } catch { }
            try { Directory.Delete(mateDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void PickingATeammateWhileLiveReplaysTheSession()
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
            w.Select(own);
            w.FinishInitialIngest(w.SelectGeneration);
            Assert.True(w.InitialIngestDone);
            Assert.Equal(1, stats.Snapshot().YourKillCount);

            var genBefore = w.SelectGeneration;
            w.SelectTeammate(mate);   // a LIVE primary is selected — this auto-replays it
            Assert.True(w.SelectGeneration > genBefore);
            Assert.False(w.InitialIngestDone);   // the auto-replay's ingest is deferred too

            w.FinishInitialIngest(w.SelectGeneration);
            Assert.Equal(2, stats.Snapshot().YourKillCount);   // now includes the teammate's kill
            Assert.Equal("Kaybek", stats.CharacterName);       // owner identity unchanged
        }
        finally
        {
            try { Directory.Delete(ownDir, recursive: true); } catch { }
            try { Directory.Delete(mateDir, recursive: true); } catch { }
        }
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
            // log: BuffTracker (and every consumer but SessionStats/Mez) must see
            // nothing from them at all — proven alongside the POSITIVE paths, so the
            // negative claim ("only stats and mez") isn't standing on top of a feed
            // that reached nothing at all. The teammate log also carries a kill
            // (proving SessionStats) and a mez cast whose landing is on the PRIMARY
            // log (proving Mez) — "Mesmerize" is the same spell MezTrackerTests casts.
            var mateStats = new SessionStats();
            var mateBuffs = new BuffTracker();
            var mez = new MezTracker();
            var primaryLog = WriteLog(ownDir, "eqlog_Owner_freeport.txt",
                "[Sat Jul 18 15:00:00 2026] You have slain orc pawn!",
                "[Sat Jul 18 15:00:06 2026] an orc pawn has been mesmerized.");
            var mateLog = WriteLog(mateDir, "eqlog_Buddy_freeport.txt",
                castLine, landLine,
                "[Sat Jul 18 15:00:03 2026] You begin casting Mesmerize.",
                "[Sat Jul 18 15:00:04 2026] You have slain orc centurion!");

            using var w = new LogWatcher(mateStats)
                { Buffs = mateBuffs, Mez = mez, DeferIngestForTests = true };
            w.SelectTeammate(mateLog);
            w.Select(primaryLog);
            w.FinishInitialIngest(w.SelectGeneration);

            Assert.Equal(0, mateBuffs.ActiveCount);   // never reached BuffTracker
            // SessionStats DID get the teammate's kill: YourKillCount rises by
            // exactly one over the primary's own "orc pawn", because of the
            // teammate's "orc centurion" — proving the admitted path actually
            // dispatches, not just that the excluded one doesn't.
            Assert.Equal(2, mateStats.Snapshot().YourKillCount);
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
}
