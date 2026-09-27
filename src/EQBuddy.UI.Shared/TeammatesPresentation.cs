namespace EQBuddy.UI.Shared;

/// <summary>
/// The words and the one rule behind Options → Behavior's "Teammates" row. Framework-free so
/// the rule a name has to pass is unit-tested rather than trusted to a WPF click handler.
/// </summary>
public static class TeammatesPresentation
{
    /// <summary>The live line under the heading: who the player's own log has put in the
    /// group this session.</summary>
    public static string DetectedLine(IReadOnlyCollection<string> detected) => detected.Count == 0
        ? "Detected in your log: nobody yet. A group join, an invite you accept, group chat or three of their kills that earn you party XP adds them; logging out ends it."
        : $"Detected in your log: {string.Join(", ", detected.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))}.";

    /// <summary>Above the hand-added names, or the empty state when there are none.</summary>
    public static string ManualHeading(int count) => count == 0
        ? "Added by you: none."
        : "Added by you:";

    public const string AddPlaceholderTip = "A character name, one word — for someone who was already in your group before your log began.";

    /// <summary>A typed name, checked and put in the shape the log prints it in
    /// ("garg" → "Garg"). Null <paramref name="name"/> means refused, and
    /// <paramref name="refusal"/> says why — the row prints it rather than ignoring the
    /// click.</summary>
    public static bool TryNormalizeName(string? input, string? ownName, IReadOnlyCollection<string> existing,
        out string name, out string? refusal)
    {
        name = "";
        var trimmed = (input ?? "").Trim();
        if (trimmed.Length == 0)
        {
            refusal = "Type a character name first.";
            return false;
        }
        if (trimmed.Length < 2 || !trimmed.All(char.IsAsciiLetter))
        {
            refusal = $"\"{trimmed}\" is not a character name — one word, letters only.";
            return false;
        }
        name = char.ToUpperInvariant(trimmed[0]) + trimmed[1..].ToLowerInvariant();
        if (ownName is { Length: > 0 } && name.Equals(ownName, StringComparison.OrdinalIgnoreCase))
        {
            refusal = $"{name} is you — your own numbers are always counted.";
            return false;
        }
        var candidate = name;
        if (existing.Any(e => e.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
        {
            refusal = $"{name} is already on the list.";
            return false;
        }
        refusal = null;
        return true;
    }
}
