using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace EQBuddy.Core;

/// <summary>
/// Rewrites a line from the PRIMARY player's own log into a teammate's first-person
/// perspective, so the rewritten text can be fed through the UNCHANGED
/// <see cref="LogParser"/> and applied to that teammate's own isolated SessionStats.
///
/// Pure, static, no I/O, no durable state. Every output line is built so that it is one
/// of the exact shapes <see cref="LogParser"/> already accepts for the primary
/// character ("You slash …", "You have taken …", "X healed you …", …) — never a new
/// shape LogParser would have to learn. That is deliberate: it means this file can
/// change the fork's behaviour without a single edit to LogParser.cs.
///
/// A raw line names a roster member either by their bare character name, or — for the
/// one pet shape the log lets us attribute — as "&lt;Owner&gt;'s warder" /
/// "&lt;Owner&gt;`s warder" (a beastlord's pet). That form resolves to the owner's
/// actor with <see cref="TeammateLine.IsPet"/> set, so a caller can tag the
/// contributing row "(pet)" without a second stats bucket. Any other pet (a
/// generated single-word name with no possessive) is not attributable from the log
/// alone and is never folded in.
///
/// Matching is by an exact, whole word: a roster name never matches inside a longer
/// word ("Garg" must not match "Gargoyle"), and only an explicit list of line SHAPES is
/// tried — there is no generic find-and-replace of the name anywhere in this file.
/// </summary>
public static class TeammatePerspective
{
    /// <summary>One line rewritten from one raw log line, for one actor. A line
    /// naming two teammates (e.g. "Garg healed Jobantik") yields one
    /// <see cref="TeammateLine"/> per teammate named.</summary>
    public readonly record struct TeammateLine(string Actor, string Line, bool IsPet = false);

    // Third-person melee verb (as ThirdMeleeRx / MeleeInRx accept it, e.g. "Garg
    // slashes X") -> the bare first-person form LogParser's own MeleeOutRx accepts
    // for "You slash X". This is the ONLY verb conjugation this file needs: the
    // "tries to <verb>" miss shape already uses the bare form for every subject
    // (that's why LogParser's own MeleeMissRx has no such table), and every other
    // rewrite below only replaces or reorders whole clauses, never a verb.
    private static readonly IReadOnlyDictionary<string, string> ThirdToFirstVerb =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["hits"] = "hit",
            ["slashes"] = "slash",
            ["kicks"] = "kick",
            ["bashes"] = "bash",
            ["pierces"] = "pierce",
            ["crushes"] = "crush",
            ["punches"] = "punch",
            ["backstabs"] = "backstab",
            ["bites"] = "bite",
            ["claws"] = "claw",
            ["mauls"] = "maul",
            ["gores"] = "gore",
            ["stings"] = "sting",
            ["strikes"] = "strike",
            ["slices"] = "slice",
            ["cleaves"] = "cleave",
            ["smashes"] = "smash",
            ["rends"] = "rend",
            ["slams"] = "slam",
            ["shoots"] = "shoot",
            ["reaves"] = "reave",
            ["smites"] = "smite",
            ["frenzies on"] = "frenzy on",
        };

    // The exact melee-verb catalog ThirdMeleeRx / MeleeInRx accept in LogParser.cs.
    // Kept as one literal so every shape below that needs "someone's melee verb"
    // stays byte-identical to what upstream will actually parse.
    private const string ThirdVerbAlt =
        "hits|slashes|kicks|bashes|pierces|crushes|punches|backstabs|bites|claws|mauls|" +
        "gores|stings|strikes|slices|cleaves|smashes|rends|slams|shoots|reaves|smites|frenzies on";

    private const string PetSuffixApos = "'s warder";
    private const string PetSuffixTick = "`s warder";

    // "<Healer> healed <Target>[ over time] for N[ (Attempted)] hit points[ by
    // Spell]." — every heal line in the log, whoever healed whom. Not roster-scoped:
    // membership is decided afterward against the roster set, because a heal line's
    // healer AND target both need to be checked independently (a line can be a
    // teammate's outgoing heal, a teammate's incoming heal, or both at once).
    private static readonly Regex HealGeneralRx = new(
        @"^(?<healer>.+?) healed (?<target>.+?)(?<hot> over time)? for (?<amount>\d+)(?: \((?<attempted>\d+)\))? hit points(?: by (?<spell>.+?))?\.$",
        RegexOptions.CultureInvariant);

    private sealed record ShapeSet(
        Regex SubjectMelee,
        Regex SubjectMeleeMiss,
        Regex SubjectSchoolDamage,
        Regex SubjectDamageShieldDealt,
        Regex SubjectDotDealt,
        Regex SubjectKill,
        Regex SubjectCast,
        Regex SubjectDeathBy,
        Regex SubjectDeathPlain,
        Regex ObjectMelee,
        Regex ObjectMeleeMissRune,
        Regex ObjectMeleeMissGeneric,
        Regex ObjectSchoolDamage,
        Regex ObjectDamageShieldTaken,
        Regex ObjectDotTaken);

    // One ShapeSet per distinct roster (its actor alternation), so a replay of a
    // long log with a fixed roster builds each regex exactly once.
    private static readonly ConcurrentDictionary<string, ShapeSet> ShapeCache = new();

    /// <summary>Rewrite one already-timestamp-stripped log message into zero or more
    /// (actor, first-person line) pairs. <paramref name="primaryName"/> is excluded
    /// from <paramref name="roster"/> even if a caller passes it in by mistake.</summary>
    public static IReadOnlyList<TeammateLine> Rewrite(string msg, string primaryName, IReadOnlyCollection<string> roster)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in roster)
            if (!string.IsNullOrEmpty(n) && !string.Equals(n, primaryName, StringComparison.Ordinal))
                names.Add(n);

        if (names.Count == 0 || !ContainsAnyName(msg, names))
            return [];

        // Heals name a healer and a target independently, either of which may be a
        // roster member (or "You"/"himself" referring back to one) — checked first
        // because it is the only shape that can yield two lines from one input.
        var healResults = TryRewriteHeal(msg, primaryName, names);
        if (healResults.Count > 0) return healResults;

        var alt = BuildAlternation(names);
        var shapes = ShapeCache.GetOrAdd(alt, BuildShapes);
        Match m;

        // ---- subject forms: the teammate is the one acting ----

        if ((m = shapes.SubjectMelee.Match(msg)).Success)
        {
            var (actor, isPet) = ResolveActor(m.Groups["actor"].Value);
            var verb = m.Groups["verb"].Value;
            var first = ThirdToFirstVerb.TryGetValue(verb, out var f) ? f : verb;
            return [new TeammateLine(actor, $"You {first} {m.Groups["rest"].Value}", isPet)];
        }

        if ((m = shapes.SubjectMeleeMiss.Match(msg)).Success)
        {
            var (actor, isPet) = ResolveActor(m.Groups["actor"].Value);
            var reason = m.Groups["reason"].Value;
            if (reason == "misses") reason = "miss";   // Garg's OWN miss, 3rd->2nd person
            var line = $"You try to {m.Groups["verb"].Value}{m.Groups["on"].Value} " +
                       $"{m.Groups["target"].Value}, but {reason}!{m.Groups["note"].Value}";
            return [new TeammateLine(actor, line, isPet)];
        }

        if ((m = shapes.SubjectSchoolDamage.Match(msg)).Success)
        {
            var (actor, isPet) = ResolveActor(m.Groups["actor"].Value);
            return [new TeammateLine(actor, $"You hit {m.Groups["rest"].Value}", isPet)];
        }

        if ((m = shapes.SubjectDamageShieldDealt.Match(msg)).Success)
        {
            var (actor, isPet) = ResolveActor(m.Groups["actor"].Value);
            var line = $"{m.Groups["target"].Value} is {m.Groups["how"].Value} by YOUR {m.Groups["rest"].Value}";
            return [new TeammateLine(actor, line, isPet)];
        }

        if ((m = shapes.SubjectDotDealt.Match(msg)).Success)
        {
            var (actor, isPet) = ResolveActor(m.Groups["actor"].Value);
            var line = $"{m.Groups["target"].Value} has taken {m.Groups["dmg"].Value} damage from " +
                       $"your {m.Groups["spell"].Value}.{m.Groups["note"].Value}";
            return [new TeammateLine(actor, line, isPet)];
        }

        if ((m = shapes.SubjectKill.Match(msg)).Success)
        {
            var (actor, isPet) = ResolveActor(m.Groups["actor"].Value);
            return [new TeammateLine(actor, $"You have slain {m.Groups["target"].Value}!", isPet)];
        }

        // A pet's own "cast" (school-DoT and heal spells still parse fine because
        // they land on SubjectSchoolDamage/SubjectDotDealt/the heal shapes above —
        // this is only the *cast-begins* announcement) is not attributable to the
        // owner: upstream never counts the primary's own pet's casts either, so a
        // pet actor here is dropped rather than inflating the owner's _castsStarted.
        if ((m = shapes.SubjectCast.Match(msg)).Success)
        {
            var (actor, isPet) = ResolveActor(m.Groups["actor"].Value);
            if (isPet) return [];
            var line = $"You begin {m.Groups["how"].Value} {m.Groups["spell"].Value}.";
            return [new TeammateLine(actor, line, isPet)];
        }

        // A pet's own death is not observed anywhere in the corpus and is dropped
        // rather than guessed at — see the object-form note below for why a pet
        // actor is refused here rather than folded into the owner.
        if ((m = shapes.SubjectDeathBy.Match(msg)).Success)
        {
            var (actor, isPet) = ResolveActor(m.Groups["actor"].Value);
            if (isPet) return [];
            return [new TeammateLine(actor, $"You have been slain by {m.Groups["killer"].Value}!", isPet)];
        }

        if ((m = shapes.SubjectDeathPlain.Match(msg)).Success)
        {
            var (actor, isPet) = ResolveActor(m.Groups["actor"].Value);
            if (isPet) return [];
            return [new TeammateLine(actor, "You died.", isPet)];
        }

        // ---- object forms: the teammate is the one being acted on ----
        //
        // A pet actor is refused on every object-form shape below: upstream only
        // ever calls TrackCombat for the PRIMARY's own pet being hit (SessionStats.cs
        // §"the primary's own pet"), never folds the damage/avoidance itself into the
        // primary's own DamageTaken/MeleeHitsTaken/HealingReceived. Folding it here
        // would inflate a teammate's own defensive numbers with their warder's, which
        // nothing upstream does for the primary's pet either (design survey finding).

        if ((m = shapes.ObjectMelee.Match(msg)).Success)
        {
            var (actor, isPet) = ResolveActor(m.Groups["actor"].Value);
            if (isPet) return [];
            var line = $"{m.Groups["attacker"].Value} {m.Groups["verb"].Value} YOU for {m.Groups["rest"].Value}";
            return [new TeammateLine(actor, line, isPet)];
        }

        // Checked BEFORE the generic object miss: only this shape's reason text
        // ("<actor>'s magical skin absorbs the blow!") must become the literal
        // "YOUR magical skin absorbs the blow!" RuneBlockInRx requires. Every other
        // reason (miss/parry/dodge/block/riposte) is discarded by MeleeInMissRx
        // regardless of its wording, so it is passed through untouched below.
        if ((m = shapes.ObjectMeleeMissRune.Match(msg)).Success)
        {
            var (actor, isPet) = ResolveActor(m.Groups["actor"].Value);
            if (isPet) return [];
            var line = $"{m.Groups["attacker"].Value} tries to {m.Groups["verb"].Value}{m.Groups["on"].Value} " +
                       $"YOU, but YOUR magical skin absorbs the blow!{m.Groups["note"].Value}";
            return [new TeammateLine(actor, line, isPet)];
        }

        if ((m = shapes.ObjectMeleeMissGeneric.Match(msg)).Success)
        {
            var (actor, isPet) = ResolveActor(m.Groups["actor"].Value);
            if (isPet) return [];
            var line = $"{m.Groups["attacker"].Value} tries to {m.Groups["verb"].Value}{m.Groups["on"].Value} " +
                       $"YOU, but {m.Groups["reason"].Value}!{m.Groups["note"].Value}";
            return [new TeammateLine(actor, line, isPet)];
        }

        if ((m = shapes.ObjectSchoolDamage.Match(msg)).Success)
        {
            var (actor, isPet) = ResolveActor(m.Groups["actor"].Value);
            if (isPet) return [];
            var line = $"{m.Groups["attacker"].Value} hit YOU for {m.Groups["rest"].Value}";
            return [new TeammateLine(actor, line, isPet)];
        }

        if ((m = shapes.ObjectDamageShieldTaken.Match(msg)).Success)
        {
            var (actor, isPet) = ResolveActor(m.Groups["actor"].Value);
            if (isPet) return [];
            return [new TeammateLine(actor, $"YOU are {m.Groups["phrase"].Value}!", isPet)];
        }

        if ((m = shapes.ObjectDotTaken.Match(msg)).Success)
        {
            var (actor, isPet) = ResolveActor(m.Groups["actor"].Value);
            if (isPet) return [];
            return [new TeammateLine(actor, $"You have taken {m.Groups["rest"].Value}", isPet)];
        }

        // "<actor> has taken N damage by <spell>." (no "from ... by <caster>" clause —
        // e.g. "Garg has taken 11 damage by Cancelling of Life.") is DELIBERATELY not
        // rewritten. LogParser's only "taken damage" shape for the primary
        // (DotInRx: "You have taken N damage from <spell> by <attacker>.") requires a
        // caster this line never names, and every other field this file fills in is
        // something the raw line actually states — inventing a caster name to force a
        // match would put a fabricated row in DamageByAttacker, which the "only what
        // the log actually shows" rule (class doc) refuses. Documented as
        // unobservable rather than guessed at; see the finding this traces to.

        return [];
    }

    private static List<TeammateLine> TryRewriteHeal(string msg, string primaryName, HashSet<string> names)
    {
        var results = new List<TeammateLine>();
        var m = HealGeneralRx.Match(msg);
        if (!m.Success) return results;

        var healerRaw = m.Groups["healer"].Value;
        var targetRaw = m.Groups["target"].Value;
        var hot = m.Groups["hot"].Success ? " over time" : "";
        var amount = m.Groups["amount"].Value;
        var attempted = m.Groups["attempted"].Success ? $" ({m.Groups["attempted"].Value})" : "";
        var spellSuffix = m.Groups["spell"].Success ? $" by {m.Groups["spell"].Value}" : "";
        var tail = $"{hot} for {amount}{attempted} hit points{spellSuffix}.";

        // The healer is a teammate: an outgoing heal for their own stats. Their
        // target may be themselves ("himself"/"herself"/"itself"), the primary
        // ("you"), a fellow teammate, or a bystander — all become a plain "You
        // healed <target>" line, HealOutRx's own shape.
        //
        // A PET healing ITSELF ("Kanaddar`s warder healed itself for 20 hit points")
        // is folded to the owner's HealingDone (tagged "(pet)" by the caller) but must
        // NOT read as the owner receiving anything: substituting the owner's own name
        // as the target — the same substitution a non-pet reflexive heal gets, two
        // lines below — would match SessionStats' "You healed <own name>" self-heal
        // rule and add a "Yourself" row + HealingReceived the owner never got (the
        // warder's hit points are not the owner's). "(pet)" is not a roster name and
        // not the owner's CharacterName, so it reads as an ordinary heal of a
        // bystander: HealingDone counts, nothing is received.
        var (healerActor, healerIsPet) = ResolveIfRoster(healerRaw, names);
        if (healerActor != null)
        {
            var realTarget = healerIsPet && targetRaw is "himself" or "herself" or "itself" ? "(pet)"
                : targetRaw is "himself" or "herself" or "itself" ? healerActor
                : targetRaw == "you" ? primaryName
                : targetRaw;
            results.Add(new TeammateLine(healerActor, $"You healed {realTarget}{tail}", healerIsPet));
        }

        // The target is a teammate: an incoming heal for their own stats, whoever
        // cast it (the primary player, another teammate, or a bystander) — EXCEPT:
        //   - when the target is a PET, per the object-form rule (a pet being healed
        //     is not attributable to the owner, same reasoning as damage taken); and
        //   - when the target resolves to the SAME actor as the healer above (a pet
        //     healing its own owner, "Kellisanth`s warder healed Kellisanth for 147
        //     hit points") — the healerActor line just above ALREADY counts this
        //     exact amount as both HealingDone and (self-heal) HealingReceived on
        //     Kellisanth's own stats; emitting this second line too doubled
        //     HealingReceived for every such line in the log (the "+147" bug).
        var (targetActor, targetIsPet) = ResolveIfRoster(targetRaw, names);
        if (targetActor != null && !targetIsPet && targetActor != healerActor)
        {
            var realHealer = healerRaw == "You" ? primaryName : healerRaw;
            results.Add(new TeammateLine(targetActor, $"{realHealer} healed you{tail}", false));
        }

        return results;
    }

    private static (string? Actor, bool IsPet) ResolveIfRoster(string token, HashSet<string> names)
    {
        if (names.Contains(token)) return (token, false);
        if (token.EndsWith(PetSuffixApos, StringComparison.Ordinal))
        {
            var owner = token[..^PetSuffixApos.Length];
            if (names.Contains(owner)) return (owner, true);
        }
        if (token.EndsWith(PetSuffixTick, StringComparison.Ordinal))
        {
            var owner = token[..^PetSuffixTick.Length];
            if (names.Contains(owner)) return (owner, true);
        }
        return (null, false);
    }

    /// <summary>Every roster-name match this file's shape regexes can produce comes
    /// from an alternation built by <see cref="BuildAlternation"/>, which is either a
    /// plain roster name or "&lt;name&gt;'s warder" / "&lt;name&gt;`s warder". This
    /// strips the pet suffix when present.</summary>
    private static (string Actor, bool IsPet) ResolveActor(string matched)
    {
        if (matched.EndsWith(PetSuffixApos, StringComparison.Ordinal))
            return (matched[..^PetSuffixApos.Length], true);
        if (matched.EndsWith(PetSuffixTick, StringComparison.Ordinal))
            return (matched[..^PetSuffixTick.Length], true);
        return (matched, false);
    }

    private static bool ContainsAnyName(string msg, HashSet<string> names)
    {
        foreach (var n in names)
        {
            var idx = msg.IndexOf(n, StringComparison.Ordinal);
            while (idx >= 0)
            {
                var before = idx == 0 || !char.IsLetterOrDigit(msg[idx - 1]);
                var after = idx + n.Length >= msg.Length || !char.IsLetterOrDigit(msg[idx + n.Length]);
                if (before && after) return true;
                idx = msg.IndexOf(n, idx + 1, StringComparison.Ordinal);
            }
        }
        return false;
    }

    private static string BuildAlternation(HashSet<string> names)
    {
        var parts = new List<string>();
        foreach (var n in names.OrderByDescending(x => x.Length))
        {
            var esc = Regex.Escape(n);
            // Pet form first: if the text is "<name>'s warder"/"<name>`s warder",
            // the alternation must be able to consume the whole thing, not stop
            // early at the bare name and leave "'s warder" dangling for the shape's
            // own required literal to choke on.
            parts.Add(esc + "['\x60]s warder");
            parts.Add(esc);
        }
        return string.Join("|", parts);
    }

    private static ShapeSet BuildShapes(string alt)
    {
        const RegexOptions Opt = RegexOptions.CultureInvariant;
        return new ShapeSet(
            // Garg slashes/hits/… T for N point(s) of damage.(note)
            SubjectMelee: new Regex(
                $@"^(?<actor>{alt}) (?<verb>{ThirdVerbAlt}) (?<rest>.+ for \d+ points? of damage\.(?: \([^)]+\))?)$", Opt),

            // Garg tries to <verb>( on) T, but <reason>!(note)
            SubjectMeleeMiss: new Regex(
                $@"^(?<actor>{alt}) tries to (?<verb>\w+)(?<on> on)? (?<target>.+?), but (?<reason>.+)!(?<note>(?: \([^)]+\))?)$", Opt),

            // Garg hit T for N points of <school> damage by S.(note)
            SubjectSchoolDamage: new Regex(
                $@"^(?<actor>{alt}) hit (?<rest>.+ points? of \w+ damage by .+?\.(?: \([^)]+\))?)$", Opt),

            // T is <how> by Garg's/`s <thing> for N points of non-melee damage.
            SubjectDamageShieldDealt: new Regex(
                $@"^(?<target>.+?) is (?<how>\w+) by (?<actor>{alt})['\x60]s (?<rest>.+ for \d+ points? of non-melee damage\.)$", Opt),

            // T has taken N damage from S by Garg.(note)
            SubjectDotDealt: new Regex(
                $@"^(?<target>.+?) has taken (?<dmg>\d+) damage from (?<spell>.+?) by (?<actor>{alt})\.(?<note>(?: \([^)]+\))?)$", Opt),

            // T has been slain by Garg!
            SubjectKill: new Regex(
                $@"^(?<target>.+?) has been slain by (?<actor>{alt})!$", Opt),

            // Garg begins casting/singing/to sing S.
            SubjectCast: new Regex(
                $@"^(?<actor>{alt}) begins (?<how>casting|singing|to sing) (?<spell>.+?)\.$", Opt),

            // Garg has been slain by <killer>!
            SubjectDeathBy: new Regex(
                $@"^(?<actor>{alt}) has been slain by (?<killer>.+)!$", Opt),

            // Garg died.
            SubjectDeathPlain: new Regex(
                $@"^(?<actor>{alt}) died\.$", Opt),

            // <npc> hits/slashes/… Garg for N point(s) of damage.(note)
            ObjectMelee: new Regex(
                $@"^(?<attacker>.+?) (?<verb>{ThirdVerbAlt}) (?<actor>{alt}) for (?<rest>\d+ points? of damage\.(?: \([^)]+\))?)$", Opt),

            // <npc> tries to <verb>( on) Garg, but Garg's/`s magical skin absorbs the blow!(note)
            ObjectMeleeMissRune: new Regex(
                $@"^(?<attacker>.+?) tries to (?<verb>\w+)(?<on> on)? (?<actor>{alt}), but \k<actor>['\x60]s magical skin absorbs the blow!(?<note>(?: \([^)]+\))?)$", Opt),

            // <npc> tries to <verb>( on) Garg, but <anything else>!(note)
            ObjectMeleeMissGeneric: new Regex(
                $@"^(?<attacker>.+?) tries to (?<verb>\w+)(?<on> on)? (?<actor>{alt}), but (?<reason>.+)!(?<note>(?: \([^)]+\))?)$", Opt),

            // <npc> hit Garg for N points of <school> damage by S.
            ObjectSchoolDamage: new Regex(
                $@"^(?<attacker>.+?) hit (?<actor>{alt}) for (?<rest>\d+ points? of \w+ damage by .+?\.)$", Opt),

            // Garg is <how> by <npc>'s <thing> for N points of non-melee damage.
            ObjectDamageShieldTaken: new Regex(
                $@"^(?<actor>{alt}) is (?<phrase>\w+ by .+? for \d+ points? of non-melee damage)\.$", Opt),

            // Garg has taken N damage from S by <npc>.
            ObjectDotTaken: new Regex(
                $@"^(?<actor>{alt}) has taken (?<rest>\d+ damage from .+? by .+?\.)$", Opt));
    }
}
