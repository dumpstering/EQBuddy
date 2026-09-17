using System.Linq;
using System.Reflection;
using EQBuddy.Core;

namespace EQBuddy.Tests;

/// <summary>
/// The structural guarantee Step 3 rests on, proven directly rather than trusted:
/// <see cref="TeammateLogTail.Stats"/> is a plain, unattached <see cref="SessionStats"/>
/// instance — no durable store, no subscriber, no borrowed identity — which is what
/// makes it safe to call <c>Stats.Apply(evt)</c> UNCONDITIONALLY for every parsed event
/// (no gate, no <c>fromTeammate</c> flag). Three rounds of gating individual call sites
/// each closed a named leak and each following audit found the same class of bug in a
/// new place; these tests are the guard against the isolation invariant itself quietly
/// eroding — a future edit that attaches a store or subscribes an event on the teammate's
/// instance reintroduces the entire bug class in one line, and it must fail HERE.
/// </summary>
public class TeammateIsolationTests
{
    private const BindingFlags AnyInstance =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    /// <summary>
    /// A9(b): every durable store's CLASS implements the marker interface
    /// <see cref="IDurableSessionStore"/> — checked with <c>IsAssignableFrom</c>
    /// against the PROPERTY'S TYPE, not a hand-curated list of the three known
    /// today. A future store added to SessionStats is caught automatically as long
    /// as its class carries the marker, which is one line on the class declaration
    /// itself and costs nothing against SessionStats.cs's own sync budget — the
    /// marker lives on the STORE, not on the property that holds it.
    /// <see cref="InventoryDumpResolver"/> stays special-cased below: it's a
    /// delegate (<c>Func&lt;...&gt;</c>), which has no class to carry an interface.
    /// </summary>
    private static void AssertNoDurableResourceAttached(SessionStats stats)
    {
        var t = typeof(SessionStats);
        foreach (var prop in t.GetProperties(AnyInstance))
        {
            if (!typeof(IDurableSessionStore).IsAssignableFrom(prop.PropertyType)) continue;
            Assert.True(prop.GetValue(stats) is null,
                $"{prop.Name} must never be attached to the teammate's isolated instance");
        }
        Assert.Null(stats.InventoryDumpResolver);

        // Spells (SpellCatalog) never had AttachStore called on it — its persistence
        // hook is a private field with no public "is attached" getter, so this reads
        // it directly rather than trusting a wrapper that might not exist tomorrow.
        var storePathField = typeof(SpellCatalog).GetField("_storePath", AnyInstance)
            ?? throw new InvalidOperationException("SpellCatalog._storePath field not found — update this test");
        Assert.Null(storePathField.GetValue(stats.Spells));

        // RefreshTextPatterns was never called — the Text-watch prefilter stays empty.
        var textPatternsField = t.GetField("_textPatterns", AnyInstance)
            ?? throw new InvalidOperationException("SessionStats._textPatterns field not found — update this test");
        Assert.Empty((TrackedRule[])textPatternsField.GetValue(stats)!);

        // None of SessionStats' public events has a subscriber, with ONE reviewed
        // exception: repair round A2's carry-forward wires the teammate's OWN
        // SessionEnding to the PRIMARY's OnCompanionSessionEnding the moment
        // Companion is assigned — the teammate's isolated instance is the ONE firing
        // the event out, never a consumer of anything, so this does not reopen the
        // leak this test guards against. Narrowed to exactly that method rather than
        // waved through wholesale: anything else attached to SessionEnding, or
        // anything at all on any OTHER event, still fails here. Field-like events
        // compile to a private backing field of the same name holding the
        // invocation list (null when nobody has subscribed) — reflecting over
        // GetEvents() means a FUTURE event added to SessionStats is covered too.
        var events = t.GetEvents(AnyInstance);
        Assert.NotEmpty(events);   // sanity: this must actually be exercising something
        foreach (var evt in events)
        {
            var backing = t.GetField(evt.Name, AnyInstance);
            Assert.True(backing is not null,
                $"{evt.Name} is not a field-like event — this test's reflection needs updating");
            var subscribers = backing!.GetValue(stats) as Delegate;
            if (evt.Name == nameof(SessionStats.SessionEnding) && subscribers is not null)
            {
                foreach (var d in subscribers.GetInvocationList())
                    Assert.Equal("OnCompanionSessionEnding", d.Method.Name);
                continue;
            }
            Assert.Null(subscribers);
        }
    }

    /// <summary>
    /// Repair round C10: the guard above only ever catches a future store if its OWN
    /// class remembers to carry <see cref="IDurableSessionStore"/> — forget the marker
    /// and <c>AssertNoDurableResourceAttached</c>'s loop skips the property entirely
    /// (<c>if (!typeof(IDurableSessionStore).IsAssignableFrom(prop.PropertyType))
    /// continue;</c>), so the isolation guard would never even look at it. This test
    /// stands independently of the marker: it finds every property on
    /// <see cref="SessionStats"/> that is SHAPED like an attachable durable
    /// store — a reference type declared in this assembly (not a BCL type such as
    /// <c>string</c>, and not a delegate), with a PUBLIC setter — and requires every
    /// one of them to actually carry the marker. The three known stores
    /// (<c>AaStore</c>, <c>QuestStore</c>, <c>StackingStore</c>) all fit this shape and
    /// all pass today. <see cref="SessionStats.Companion"/> is excluded because its
    /// setter is <c>internal</c>, not public (repair round A8) — it is the carry
    /// mechanism, not a durable store. <see cref="SessionStats.InventoryDumpResolver"/>
    /// is excluded because a delegate type has no class to carry an interface (the
    /// existing test above checks it stays null directly). <see cref="SessionStats.Spells"/>
    /// is excluded because it has no setter at all — it is always present, never
    /// attached. A future property that fits the shape and forgets the marker fails
    /// HERE, at the point it was added, rather than three audits from now.
    /// </summary>
    [Fact]
    public void EveryStoreShapedSessionStatsPropertyCarriesTheMarkerInterface()
    {
        var t = typeof(SessionStats);
        var storeShaped = t.GetProperties(AnyInstance)
            .Where(p => p.SetMethod is { IsPublic: true })
            .Where(p => !p.PropertyType.IsValueType)
            .Where(p => !typeof(Delegate).IsAssignableFrom(p.PropertyType))
            .Where(p => p.PropertyType.Assembly == t.Assembly)
            .ToList();

        Assert.NotEmpty(storeShaped);   // sanity: this must actually be exercising something
        foreach (var prop in storeShaped)
        {
            Assert.True(typeof(IDurableSessionStore).IsAssignableFrom(prop.PropertyType),
                $"{prop.Name} ({prop.PropertyType.Name}) looks like an attachable " +
                "durable store (reference type declared in EQBuddy.Core, public setter) " +
                "but its class does not implement IDurableSessionStore — either add the " +
                "marker to its class, or exempt it here by name with a reason.");
        }
    }

    [Fact]
    public void TeammateStatsCarriesNoDurableStoreAndNoSubscriber()
    {
        var tail = new TeammateLogTail(@"C:\nope\eqlog_Buddy_freeport.txt", () => null);
        AssertNoDurableResourceAttached(tail.Stats);
    }

    /// <summary>
    /// A9(b), the second half: the guard above only ever inspected a FRESHLY
    /// CONSTRUCTED tail, but <see cref="LogWatcher.TeammateStats"/> and
    /// <see cref="TeammateLogTail.Stats"/> are both PUBLIC — nothing in the type
    /// system stops some other call site from attaching a store to
    /// <c>w.TeammateStats</c> after the fact, the way <c>MainWindow</c> legitimately
    /// does to the PRIMARY instance. This drives a real <see cref="LogWatcher"/>
    /// through SelectTeammate → Select → a poll (the actual production lifecycle,
    /// not a bare constructor call) and re-asserts the same invariant on the far
    /// side of it.
    /// </summary>
    [Fact]
    public void TheFullyWiredWatchersTeammateStatsStillCarriesNoDurableResource()
    {
        var ownDir = Directory.CreateTempSubdirectory("eqbuddy-iso-own-").FullName;
        var mateDir = Directory.CreateTempSubdirectory("eqbuddy-iso-mate-").FullName;
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
            w.PollForTests();

            // Repair round C10: TeammateStats is now a read-only wrapper (see its own
            // doc), so this reflection-based assertion — which must inspect the REAL
            // instance's properties/fields/events — reaches it via the internal
            // test-only escape hatch instead.
            AssertNoDurableResourceAttached(w.TeammateStatsForTests!);
        }
        finally
        {
            try { Directory.Delete(ownDir, recursive: true); } catch { }
            try { Directory.Delete(mateDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ATeammatesLootDoesNotAppearInYourInventoryOverlay()
    {
        // MainWindow.xaml.cs reads `_stats.ItemsGainedSince(...)` for the inventory
        // overlay reconcile — it walks `_journal`, which is per-instance, so this leak
        // closes FOR FREE the moment the journal itself is isolated (Step 3).
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0);
        var stats = new SessionStats();
        stats.Apply(new LootEvent(t0.AddSeconds(1), "Rusty Dagger", "orc pawn", null));

        var mate = new SessionStats();
        mate.Apply(new LootEvent(t0.AddSeconds(1), "Iron Shield", "orc guard", null));

        Assert.True(stats.ItemsGainedSince(t0).ContainsKey("Rusty Dagger"));
        Assert.False(stats.ItemsGainedSince(t0).ContainsKey("Iron Shield"));
        Assert.True(mate.ItemsGainedSince(t0).ContainsKey("Iron Shield"));
        Assert.False(mate.ItemsGainedSince(t0).ContainsKey("Rusty Dagger"));
    }

    private static string WriteLog(string dir, string name, params string[] lines)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void ATeammateEventNeverBumpsTheWatchedSessionsVersion()
    {
        // CurrentVersion gates the 50 ms Mobile pump and every "did anything change"
        // check in the widget — a teammate's activity bumping the WATCHED character's
        // version would wake every one of those for a change nobody asked to see yet
        // (Step 4's DuoVersion is the deliberate, combined answer; this is about the
        // PRIMARY instance's own version staying its own).
        var ownDir = Directory.CreateTempSubdirectory("eqbuddy-iso-own-").FullName;
        var mateDir = Directory.CreateTempSubdirectory("eqbuddy-iso-mate-").FullName;
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

            var versionAfterOwnIngest = stats.CurrentVersion;
            Assert.True(versionAfterOwnIngest > 0);

            // New activity on the TEAMMATE's side only — nothing for the primary to read.
            File.AppendAllText(mate, "[Sat Jul 18 15:00:10 2026] You have slain orc legionnaire!\n");
            w.PollForTests();

            Assert.Equal(versionAfterOwnIngest, stats.CurrentVersion);
            // Sanity: the teammate's OWN instance DID move — proving the assertion
            // above isn't just "nothing happened at all".
            Assert.True(w.TeammateStats!.CurrentVersion > 0);
        }
        finally
        {
            try { Directory.Delete(ownDir, recursive: true); } catch { }
            try { Directory.Delete(mateDir, recursive: true); } catch { }
        }
    }
}
