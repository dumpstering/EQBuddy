namespace EQBuddy.E2E;

[Collection("e2e")]
public sealed class TeammateWarningTests
{
    [Fact]
    public void AClockRefusalIsVisibleInTheRealWidget()
    {
        var root = Directory.CreateTempSubdirectory("eqbuddy-mate-warning-").FullName;
        try
        {
            var mate = Path.Combine(root, "eqlog_Buddy_test.txt");
            File.WriteAllText(mate, "");
            using var app = new AppHarness(s => s.TeammateLogPath = mate);
            app.Launch();
            app.WaitForDump("teammateWarning", 0, "no refusal before any clock evidence");
            app.AppendLogLines("a clock probe has been slain by Buddy!");
            File.AppendAllText(mate, FixtureLog.Stamp(DateTime.Now.AddMinutes(10),
                "You have slain a clock probe!") + "\r\n");
            app.WaitForDump("teammateWarning", 1, "the clock refusal to be rendered");
            app.WaitForDump("teammateWarningExplainsClock", 1, "the warning to explain the clock difference");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
