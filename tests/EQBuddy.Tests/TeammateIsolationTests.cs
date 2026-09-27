using System.Linq;
using System.Reflection;
using EQBuddy.Core;

namespace EQBuddy.Tests;

/// <summary>
/// The structural guarantee the own-log teammate feature rests on, proven directly
/// rather than trusted: every teammate <see cref="DerivedTeammates"/> builds is a
/// plain, unattached <see cref="SessionStats"/> — no durable store, no subscriber, no
/// borrowed identity — which is what makes it safe to apply every rewritten teammate
/// event to it UNCONDITIONALLY (no gate, no <c>fromTeammate</c> flag). Gating
/// individual call sites was tried first and each audit found the same class of leak
/// in a new place; a future edit that attaches a store or subscribes an event on a
/// teammate's instance would reintroduce the whole class in one line, and it must fail
/// HERE.
/// </summary>
public class TeammateIsolationTests
{
    private const BindingFlags AnyInstance =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private const string Primary = "Smargush";
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0);

    /// <summary>Every property SHAPED like an attachable durable store — a reference
    /// type declared in EQBuddy.Core with a public setter (the AA, quest and stacking
    /// stores today, and whatever is added tomorrow) — found by reflection rather than
    /// named, so a new store is covered the day it is added.</summary>
    internal static IReadOnlyList<PropertyInfo> StoreShapedProperties() =>
        typeof(SessionStats).GetProperties(AnyInstance)
            .Where(p => p.SetMethod is { IsPublic: true })
            .Where(p => !p.PropertyType.IsValueType)
            .Where(p => !typeof(Delegate).IsAssignableFrom(p.PropertyType))
            .Where(p => p.PropertyType.Assembly == typeof(SessionStats).Assembly)
            .ToList();

    internal static void AssertNoDurableResourceAttached(SessionStats stats)
    {
        var t = typeof(SessionStats);
        foreach (var prop in StoreShapedProperties())
            Assert.True(prop.GetValue(stats) is null,
                $"{prop.Name} must never be attached to a teammate's isolated instance");
        Assert.Null(stats.InventoryDumpResolver);

        // Spells (SpellCatalog) never had AttachStore called on it.
        var storePathField = typeof(SpellCatalog).GetField("_storePath", AnyInstance)
            ?? throw new InvalidOperationException("SpellCatalog._storePath field not found — update this test");
        Assert.Null(storePathField.GetValue(stats.Spells));

        // RefreshTextPatterns was never called — the Text-watch prefilter stays empty.
        var textPatternsField = t.GetField("_textPatterns", AnyInstance)
            ?? throw new InvalidOperationException("SessionStats._textPatterns field not found — update this test");
        Assert.Empty((TrackedRule[])textPatternsField.GetValue(stats)!);

        // No event on a teammate's instance has ANY subscriber: nothing listens to it,
        // so nothing it does can reach a history archive, an alert or a ledger.
        // Field-like events compile to a private backing field of the same name holding
        // the invocation list, and reflecting over GetEvents() covers a future event too.
        var events = t.GetEvents(AnyInstance);
        Assert.NotEmpty(events);
        foreach (var evt in events)
        {
            var backing = t.GetField(evt.Name, AnyInstance);
            Assert.True(backing is not null,
                $"{evt.Name} is not a field-like event — this test's reflection needs updating");
            Assert.Null(backing!.GetValue(stats) as Delegate);
        }
    }

    [Fact]
    public void TheStoreShapedPropertyScanFindsTheKnownStores()
    {
        // Sanity: the reflective scan must actually be exercising something, and the
        // three durable stores MainWindow attaches to the primary must be in it.
        var names = StoreShapedProperties().Select(p => p.Name).ToList();
        Assert.Contains(nameof(SessionStats.AaStore), names);
        Assert.Contains(nameof(SessionStats.QuestStore), names);
        Assert.Contains(nameof(SessionStats.StackingStore), names);
    }

    [Fact]
    public void ADerivedTeammateCarriesNoDurableStoreAndNoSubscriber()
    {
        var derived = new DerivedTeammates();
        string[] roster = ["Garg"];
        derived.Observe(T0, "Garg slashes a gnoll for 91 points of damage.", Primary, null, roster);
        derived.Observe(T0.AddSeconds(2), "A gnoll has been slain by Garg!", Primary, null, roster);
        derived.Observe(T0.AddSeconds(3), "Garg has been slain by a gnoll!", Primary, null, roster);

        var garg = Assert.Single(derived.LiveStats(Primary, null, roster)).Value;
        AssertNoDurableResourceAttached(garg);
    }

    [Fact]
    public void ATeammatesLootDoesNotAppearInYourInventoryOverlay()
    {
        // MainWindow reads `_stats.ItemsGainedSince(...)` for the inventory overlay
        // reconcile — it walks the per-instance journal, so a teammate's instance can
        // never add to it.
        var stats = new SessionStats();
        stats.Apply(new LootEvent(T0.AddSeconds(1), "Rusty Dagger", "orc pawn", null));

        var mate = new SessionStats();
        mate.Apply(new LootEvent(T0.AddSeconds(1), "Iron Shield", "orc guard", null));

        Assert.True(stats.ItemsGainedSince(T0).ContainsKey("Rusty Dagger"));
        Assert.False(stats.ItemsGainedSince(T0).ContainsKey("Iron Shield"));
    }

    [Fact]
    public void ATeammateEventNeverBumpsTheWatchedSessionsOwnVersion()
    {
        // CurrentVersion is the PRIMARY's own "did anything change"; the combined
        // answer is DuoVersion. A rewritten teammate event lands on the teammate's
        // instance alone.
        var primary = new SessionStats { CharacterName = Primary };
        primary.Apply(LogParser.Parse(T0, "You slash a gnoll for 10 points of damage.")!);
        var before = primary.CurrentVersion;

        var derived = new DerivedTeammates();
        derived.Observe(T0.AddSeconds(1), "Garg healed himself for 50 hit points by Healing.", Primary, null, ["Garg"]);

        Assert.Equal(before, primary.CurrentVersion);
        Assert.Single(derived.KnownTeammates);
    }
}
