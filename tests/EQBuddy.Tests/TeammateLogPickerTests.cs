using EQBuddy.Core;

namespace EQBuddy.Tests;

/// <summary>
/// Finding 4: the teammate-log picker enforced nothing that its own help text
/// promises. This is the pure validation <c>SettingsBehaviorView.OnChooseTeammateLog</c>
/// (WPF, untestable directly) calls — moved into EQBuddy.Core in repair round R3 so
/// <see cref="LogWatcher"/> can call it too; see the class doc there for why.
/// </summary>
public class TeammateLogPickerTests
{
    private static string WriteFile(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, "seed");
        return path;
    }

    [Fact]
    public void AMissingFileIsRefused()
    {
        var dir = Directory.CreateTempSubdirectory("eqbuddy-picker-").FullName;
        try
        {
            var missing = Path.Combine(dir, "eqlog_Buddy_freeport.txt");
            var reason = TeammateLogPicker.Validate(missing, primaryLogPath: null, logFolder: null);
            Assert.NotNull(reason);
            Assert.Contains("doesn't exist", reason);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void APathEqualToThePrimaryLogIsRefused()
    {
        var dir = Directory.CreateTempSubdirectory("eqbuddy-picker-").FullName;
        try
        {
            var path = WriteFile(dir, "eqlog_Kaybek_freeport.txt");
            var reason = TeammateLogPicker.Validate(path, primaryLogPath: path, logFolder: null);
            Assert.NotNull(reason);
            Assert.Contains("your own log", reason);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void APathEqualToThePrimaryLogThroughDifferentCasingIsRefused()
    {
        var dir = Directory.CreateTempSubdirectory("eqbuddy-picker-").FullName;
        try
        {
            var path = WriteFile(dir, "eqlog_Kaybek_freeport.txt");
            var reason = TeammateLogPicker.Validate(path.ToUpperInvariant(), primaryLogPath: path, logFolder: null);
            Assert.NotNull(reason);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void AFileInsideTheLogsFolderIsRefused()
    {
        var dir = Directory.CreateTempSubdirectory("eqbuddy-picker-").FullName;
        try
        {
            var path = WriteFile(dir, "eqlog_Buddy_freeport.txt");
            var reason = TeammateLogPicker.Validate(path, primaryLogPath: null, logFolder: dir);
            Assert.NotNull(reason);
            Assert.Contains("Logs folder", reason);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void AFileInASubdirectoryOfTheLogsFolderIsRefused()
    {
        var dir = Directory.CreateTempSubdirectory("eqbuddy-picker-").FullName;
        try
        {
            var sub = Directory.CreateDirectory(Path.Combine(dir, "archive")).FullName;
            var path = WriteFile(sub, "eqlog_Buddy_freeport.txt");
            var reason = TeammateLogPicker.Validate(path, primaryLogPath: null, logFolder: dir);
            Assert.NotNull(reason);
            Assert.Contains("Logs folder", reason);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void AGenuinelyDifferentFileOutsideTheLogsFolderIsAccepted()
    {
        var logsDir = Directory.CreateTempSubdirectory("eqbuddy-picker-logs-").FullName;
        var syncDir = Directory.CreateTempSubdirectory("eqbuddy-picker-sync-").FullName;
        try
        {
            var primary = WriteFile(logsDir, "eqlog_Kaybek_freeport.txt");
            var mate = WriteFile(syncDir, "eqlog_Buddy_freeport.txt");

            var reason = TeammateLogPicker.Validate(mate, primaryLogPath: primary, logFolder: logsDir);
            Assert.Null(reason);
        }
        finally
        {
            try { Directory.Delete(logsDir, recursive: true); } catch { }
            try { Directory.Delete(syncDir, recursive: true); } catch { }
        }
    }
}
