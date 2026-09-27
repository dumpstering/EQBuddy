using EQBuddy.Core;

namespace EQBuddy.UI.Shared;

/// <summary>
/// What the Money card says (Gate 5b): where the coin came from, how fast it is coming,
/// and what you sold.
///
/// Four lines of formatted currency that lived inside <c>RefreshUi</c> as one interpolated
/// string, and so could not be asserted — the widget has no test project
/// (docs/TestPlan.md §5). Coin formatting itself is Core's (<c>StatsSnapshot.FormatCoin</c>,
/// which is tested); what was untested is which numbers appear, in what order, and when a
/// line is worth printing at all.
/// </summary>
public static class MoneyPresentation
{
    /// <summary>The summary block, one line per fact. A list rather than a joined string
    /// so a test can name the line it means, and so the last line can be absent rather
    /// than empty.</summary>
    public static List<string> SummaryLines(StatsSnapshot s)
    {
        var lines = new List<string>(4)
        {
            $"Corpses {StatsSnapshot.FormatCoin(s.CorpseCopper)} " +
            $"({s.CoinDrops} drops, biggest {StatsSnapshot.FormatCoin(s.BiggestDrop)})",

            $"Merchant sales {StatsSnapshot.FormatCoin(s.VendorCopper)} ({s.SalesCount} sales)",

            // Both rates, because they differ by exactly the downtime.
            $"{StatsSnapshot.FormatCoin(s.CopperPerHour)} per hour · " +
            $"{StatsSnapshot.FormatCoin(s.CopperPerActiveHour)} per active hour",
        };
        // Only when there IS a recent window: "Last 0m: 0" reads as a dead session rather
        // than as a measurement nobody has taken yet.
        if (s.Recent is { } recent)
            lines.Add($"Last {(int)recent.Window.TotalMinutes}m: {StatsSnapshot.FormatCoin(recent.Copper)}");
        return lines;
    }

    /// <summary>What you sold, newest data first as the snapshot gives it.
    ///
    /// Sold items are drops too (#74, Snagglefern: "if an item is unknown on the wiki I
    /// definitely sold it"), so they carry the same click, hover and quest badge as the
    /// Loot card — the count moves into the value column so the NAME stays a clean lookup
    /// key. A stack of one prints no count: "×1" is noise on every row that has it.</summary>
    public static List<CardRow> SoldRows(StatsSnapshot s) =>
    [
        .. s.SoldItems.Select(i => new CardRow(
            i.Item,
            (i.Count > 1 ? $"×{i.Count} · " : "") + StatsSnapshot.FormatCoin(i.Copper),
            Item: true)),
    ];

    public static bool ShowSold(StatsSnapshot s) => s.SoldItems.Count > 0;

    public const string SoldLabel = "Sold to merchants";
}
