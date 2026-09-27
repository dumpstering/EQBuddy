namespace EQBuddy.UI.Shared;

/// <summary>
/// The words and the one rule behind Options → Behavior's "Teammates" row. Framework-free so
/// the rule a name has to pass is unit-tested rather than trusted to a WPF click handler.
/// </summary>
public static class TeammatesPresentation
{
    /// <summary>The live line under the heading: who is counted right now — the members
    /// the player's own log put in the group, and any hand-added name the log has not
    /// since ended.</summary>
    public static string DetectedLine(IReadOnlyCollection<string> members) => members.Count == 0
        ? "In your group now: nobody yet. A group join, an invite you accept, group chat or three of their kills that earn you party XP adds them; leaving, a disband or logging out ends it."
        : $"In your group now: {string.Join(", ", members.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))}.";

    /// <summary>Above the names added by hand for the watched character, or the empty state
    /// when there are none.</summary>
    public static string ManualHeading(int count) => count == 0
        ? "Added by you for this character: none."
        : "Added by you for this character:";

    /// <summary>One hand-added name, and whether it is counted now. A hand-added name is a
    /// join, not a standing member, so the row says when the log has ended it — and how to
    /// count them again.</summary>
    public static string ManualRow(string name, bool countedNow) => countedNow
        ? $"{name} — counted now"
        : $"{name} — not counted now: your log shows them leave, the group end or you log out. Add them again to count them from now.";

    public const string AddPlaceholderTip = "A character name, one word — for someone who was already in your group before your log began. They count from the start of this session until your log shows them leave, the group end or you log out.";

    /// <summary>Why a name cannot be added while no character's log is being read: a
    /// hand-added name belongs to one character.</summary>
    public const string NoLogRefusal = "Pick your character's log first — a name you add counts for that character only.";

    /// <summary>A typed name, checked and put in the shape the log prints it in
    /// ("garg" → "Garg"). Null <paramref name="name"/> means refused, and
    /// <paramref name="refusal"/> says why — the row prints it rather than ignoring the
    /// click. <paramref name="countedNow"/> is who is counted right now: adding one of them
    /// changes nothing, so it is refused; a name added before and since ended by the log is
    /// accepted again.</summary>
    public static bool TryNormalizeName(string? input, string? ownName, IReadOnlyCollection<string> countedNow,
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
        if (countedNow.Any(e => e.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
        {
            refusal = $"{name} is already counted — in your group now.";
            return false;
        }
        refusal = null;
        return true;
    }
}
