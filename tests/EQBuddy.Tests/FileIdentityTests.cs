using EQBuddy.Core;

namespace EQBuddy.Tests;

/// <summary>
/// Repair round R4: a normalized-path STRING compare cannot see that two different
/// spellings — a hard link, a junction, a symlink, an 8.3 alias, or a mapped drive
/// letter versus its UNC path — name the SAME underlying file, so a player working
/// around one guard by handing EQBuddy a differently-spelled path to the SAME file
/// defeats it. <see cref="FileIdentity"/> is best-effort: it opens a handle to read the
/// real (volume, file-index) identity, and falls back to today's normalized
/// case-insensitive path compare whenever a handle cannot be opened (missing file,
/// locked file, permission denied) — it must never throw or block.
///
/// Symlinks need elevation (or Developer Mode) to create on this machine and are not
/// exercised here. Hard links and junctions do not need elevation on Windows/NTFS and
/// are exercised directly — repair round A9(c): a setup failure for either now FAILS
/// the test loudly (<c>Assert.Fail</c>) rather than silently `return`ing green, which
/// used to read exactly like the identity check working. EQBuddy is Windows-only
/// (CLAUDE.md, 2026-09-04), so there is no longer a legitimate platform this could
/// skip on — a creation failure here means something is wrong with the test
/// environment, not an expected gap.
/// </summary>
public class FileIdentityTests
{
    [Fact]
    public void AHardLinkIsTheSameFileAsItsOriginal()
    {
        var dir = Directory.CreateTempSubdirectory("eqbuddy-fid-").FullName;
        try
        {
            var real = Path.Combine(dir, "real.txt");
            File.WriteAllText(real, "hello");
            var hard = Path.Combine(dir, "hard.txt");

            // Repair round A9(c): a silent `return` here reads as a PASS — the same
            // shape as a guard that never fires, indistinguishable from the identity
            // check genuinely working. EQBuddy is Windows-only (CLAUDE.md, 2026-09-04)
            // and hard links need no elevation on NTFS, so a creation failure here is
            // a real problem with the test environment, not a legitimate skip — fail
            // loudly rather than pass silently.
            if (!TryCreateHardLink(hard, real))
                Assert.Fail("Could not create a hard link in the test temp directory — " +
                    "this should always succeed on Windows/NTFS with no elevation. " +
                    "Check the temp volume's filesystem before trusting this test's other assertions.");

            Assert.True(FileIdentity.SamePath(real, hard));
            // A completely unrelated file must still compare different.
            var other = Path.Combine(dir, "other.txt");
            File.WriteAllText(other, "different");
            Assert.False(FileIdentity.SamePath(real, other));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void AFileReachedThroughAJunctionIsInsideTheRealFolder()
    {
        var dir = Directory.CreateTempSubdirectory("eqbuddy-fid-").FullName;
        try
        {
            var realFolder = Directory.CreateDirectory(Path.Combine(dir, "real")).FullName;
            var file = Path.Combine(realFolder, "eqlog_Buddy_freeport.txt");
            File.WriteAllText(file, "seed");

            var junctionPath = Path.Combine(dir, "junction");
            // Repair round A9(c): fail loudly rather than pass silently — see
            // AHardLinkIsTheSameFileAsItsOriginal's comment for why.
            if (!TryCreateJunction(junctionPath, realFolder))
                Assert.Fail("Could not create a junction in the test temp directory — " +
                    "this should always succeed on Windows with no elevation. " +
                    "Check the temp volume's filesystem before trusting this test's other assertions.");
            var viaJunction = Path.Combine(junctionPath, "eqlog_Buddy_freeport.txt");

            // The SAME file, reached through the junction alias, must be seen as
            // living inside the REAL folder — a lexical compare would miss this
            // entirely, since the two path strings share no prefix at all.
            Assert.True(FileIdentity.IsInside(viaJunction, realFolder));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void MissingFileBeneathAJunctionIntoTheLogsFolderIsStillInside()
    {
        // Repair round R7 (plan Part 5b-iii): for a MISSING file, TryGetFinalPath(filePath)
        // returns null, and the old fallback used the LEXICAL directory of filePath — so a
        // junction INTO the real Logs folder was never resolved, and a teammate path
        // beneath it (not yet synced down) was wrongly accepted as outside. The fix
        // resolves the PARENT directory, which exists even when the file inside it does
        // not, so the junction is followed to the real folder either way.
        var dir = Directory.CreateTempSubdirectory("eqbuddy-fid-").FullName;
        try
        {
            var realFolder = Directory.CreateDirectory(Path.Combine(dir, "real")).FullName;

            var junctionPath = Path.Combine(dir, "junction");
            // Repair round A9(c): fail loudly rather than pass silently — see
            // AHardLinkIsTheSameFileAsItsOriginal's comment for why.
            if (!TryCreateJunction(junctionPath, realFolder))
                Assert.Fail("Could not create a junction in the test temp directory — " +
                    "this should always succeed on Windows with no elevation. " +
                    "Check the temp volume's filesystem before trusting this test's other assertions.");
            // Deliberately never written — the teammate's sync tool hasn't dropped it yet.
            var missingViaJunction = Path.Combine(junctionPath, "eqlog_Mate_freeport.txt");
            Assert.False(File.Exists(missingViaJunction));

            Assert.True(FileIdentity.IsInside(missingViaJunction, realFolder));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void MissingFilesFallBackToLexicalComparisonWithoutThrowing()
    {
        var a = @"C:\nope\eqlog_Buddy_freeport.txt";
        var b = a.ToUpperInvariant();
        Assert.True(FileIdentity.SamePath(a, b));   // same string, case-insensitively — fallback path
        Assert.False(FileIdentity.SamePath(a, @"C:\nope\eqlog_Other_freeport.txt"));
    }

    private static bool TryCreateHardLink(string link, string target)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe", Arguments = $"/c mklink /H \"{link}\" \"{target}\"",
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit(5000);
            return p.ExitCode == 0 && File.Exists(link);
        }
        catch { return false; }
    }

    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe", Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit(5000);
            return p.ExitCode == 0 && Directory.Exists(link);
        }
        catch { return false; }
    }
}
