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
}
