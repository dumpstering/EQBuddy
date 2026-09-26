using EQBuddy.Core;

namespace EQBuddy.Tests;

/// <summary>
/// TeammatePerspective rewrites a line from the primary player's own log into a
/// teammate's first-person perspective. Every rewritten line here is then run through
/// the UNCHANGED <see cref="LogParser"/> — never eyeballed — so a bug in the rewrite
/// table shows up as a parse failure or a wrong field, not as "the text looks right".
///
/// Fixture lines in the (a) corpus test are real (sanitized) EverQuest Legends log
/// lines from the user's own character (a level 41 Paladin/Monk/Berserker Ogre) with a
/// duo partner "Garg" (a Paladin/Monk/Berserker), gathered by a template survey of the
/// live log (scratchpad/garg_tpl.txt, 891 distinct templates with counts). Every
/// template that carries a number (damage, heal, kill, death) is either exercised below
/// or listed at the bottom of this file, under "intentionally SKIPPED", with the reason
/// it cannot be rewritten.
/// </summary>
public class TeammatePerspectiveTests
{
    private const string Primary = "Smargush";
    private static readonly string[] Roster = ["Garg"];
    private static readonly string[] DuoRoster = ["Garg", "Jobantik"];

    private static TeammatePerspective.TeammateLine[] Rewrite(string msg, string[]? roster = null) =>
        TeammatePerspective.Rewrite(msg, Primary, roster ?? Roster).ToArray();

    private static T ParseOne<T>(string msg) where T : GameEvent
    {
        var single = Rewrite(msg);
        Assert.Single(single);
        var evt = LogParser.Parse(DateTime.UtcNow, single[0].Line);
        Assert.NotNull(evt);
        return Assert.IsType<T>(evt);
    }

    // ---------------------------------------------------------------------
    // (a) CORPUS: every numeric Garg line shape, rewritten then parsed for real.
    // ---------------------------------------------------------------------

    // ---- melee dealt (Garg hits/misses a mob) ----

    [Theory]
    // template: "@P slashes MOB for N points of damage." (7,222 lines)
    [InlineData("Garg slashes a froglok tsu shaman for 91 points of damage.", "Slash", "Froglok tsu shaman", 91, false, null)]
    // template: "@P bashes MOB for N point of damage." (4,436) — singular "point"
    [InlineData("Garg bashes a bloodthirsty ghoul for 1 point of damage.", "Bash", "Bloodthirsty ghoul", 1, false, null)]
    // template: "@P frenzies on MOB for N points of damage." (4,616) — two-word verb
    [InlineData("Garg frenzies on a frenzied ghoul for 45 points of damage.", "Frenzy", "Frenzied ghoul", 45, false, null)]
    // template: "@P slashes MOB for N points of damage. (Critical)" (1,237)
    [InlineData("Garg slashes a froglok shin knight for 173 points of damage. (Critical)", "Slash", "Froglok shin knight", 173, true, "Critical")]
    // template: "@P kicks MOB for N points of damage. (Finishing Blow)" (128)
    [InlineData("Garg kicks a bloodthirsty ghoul for 302 points of damage. (Finishing Blow)", "Kick", "Bloodthirsty ghoul", 302, false, "Finishing Blow")]
    // template: "@P strikes MOB for N points of damage. (Slay Undead)" (93)
    [InlineData("Garg strikes a bloodthirsty ghoul for 534 points of damage. (Slay Undead)", "Strike", "Bloodthirsty ghoul", 534, false, "Slay Undead")]
    // template: "@P slashes MOB for N points of damage. (Riposte)" (250)
    [InlineData("Garg slashes a vampire bat for 105 points of damage. (Riposte)", "Slash", "Vampire bat", 105, false, "Riposte")]
    // template: "@P hit MOB for N points of magic damage by Smiting Strike." handled separately below (school damage)
    public void CorpusMeleeDealt(string line, string skill, string target, int amount, bool crit, string? note)
    {
        var evt = ParseOne<DamageDealtEvent>(line);
        Assert.Equal(target, evt.Target);
        Assert.Equal(amount, evt.Amount);
        Assert.Equal(DamageKind.Melee, evt.Kind);
        Assert.Equal(skill, evt.Source);
        Assert.Equal(crit, evt.Critical);
        Assert.Equal(note, evt.Note);
    }

    [Theory]
    // "@P tries to kick MOB, but misses!" (3,282) — Garg's own miss: misses -> miss
    [InlineData("Garg tries to kick a frenzied ghoul, but misses!", "Kick", "Frenzied ghoul", "miss")]
    // "@P tries to slash MOB, but MOB parries!" (485) — target's own avoidance, untouched
    [InlineData("Garg tries to slash a shin ghoul knight, but a shin ghoul knight parries!", "Slash", "Shin ghoul knight", "a shin ghoul knight parries")]
    // "@P tries to hit MOB, but MOB's magical skin absorbs the blow!" (21) — mob's OWN rune, untouched
    [InlineData("Garg tries to hit a tal ghoul wizard, but a tal ghoul wizard's magical skin absorbs the blow!",
        "Hit", "Tal ghoul wizard", "a tal ghoul wizard's magical skin absorbs the blow")]
    // "@P tries to frenzy on MOB, but misses!" (3,127) — two-word verb, own miss
    [InlineData("Garg tries to frenzy on a frenzied ghoul, but misses!", "Frenzy", "Frenzied ghoul", "miss")]
    public void CorpusMeleeMissDealt(string line, string skill, string target, string reason)
    {
        var evt = ParseOne<MissEvent>(line);
        Assert.True(evt.Outgoing);
        Assert.Equal(skill, evt.Ability);
        Assert.Equal(target, evt.Target);
        Assert.Equal(reason, evt.Reason);
    }

    // "@P hit MOB for N points of magic damage by Smiting Strike." (3,392)
    [Fact]
    public void CorpusSchoolDamageDealt()
    {
        var evt = ParseOne<DamageDealtEvent>("Garg hit a bloodthirsty ghoul for 46 points of magic damage by Smiting Strike.");
        Assert.Equal("Bloodthirsty ghoul", evt.Target);
        Assert.Equal(46, evt.Amount);
        Assert.Equal(DamageKind.Spell, evt.Kind);
        Assert.Equal("Smiting Strike", evt.Source);
        Assert.False(evt.Critical);
    }

    // "@P is pierced by MOB's thorns for N points of non-melee damage." (1,316) — Garg's DAMAGE TAKEN
    [Fact]
    public void CorpusDamageShieldTaken()
    {
        var evt = ParseOne<DamageTakenEvent>("Garg is pierced by a Tesch Mas Gnoll's thorns for 10 points of non-melee damage.");
        Assert.Equal("Tesch Mas Gnoll", evt.Attacker);
        Assert.Equal(10, evt.Amount);
        Assert.False(evt.Melee);
    }

    // "MOB is pierced by @P's thorns for N points of non-melee damage." (86) — Garg's DAMAGE DEALT (his own DS)
    [Fact]
    public void CorpusDamageShieldDealt()
    {
        var evt = ParseOne<DamageDealtEvent>("A lizard ritualist is pierced by Garg's thorns for 8 points of non-melee damage.");
        Assert.Equal("Lizard ritualist", evt.Target);
        Assert.Equal(8, evt.Amount);
        Assert.True(evt.IsAux);
        Assert.Equal("Damage shield", evt.Source);
    }

    // "@P has taken N damage from Boil Blood by MOB." (71) — Garg's DoT taken
    [Fact]
    public void CorpusDotTaken()
    {
        var evt = ParseOne<DamageTakenEvent>("Garg has taken 67 damage from Boil Blood by a Rosch Mal Gnoll.");
        Assert.Equal("Rosch Mal Gnoll", evt.Attacker);
        Assert.Equal(67, evt.Amount);
        Assert.False(evt.Melee);
        Assert.Equal("Boil Blood", evt.Ability);
        Assert.True(evt.OverTime);
    }

    // ---- melee/spell taken (a mob attacks Garg) ----

    [Theory]
    // "MOB cleaves @P for N points of damage." (1,372) — attacker Normalize()d, like target elsewhere
    [InlineData("A zol ghoul knight cleaves Garg for 85 points of damage.", "Cleave", 85, "Zol ghoul knight", false)]
    // "MOB hits @P for N points of damage. (Riposte)" (626)
    [InlineData("A bloodthirsty ghoul hits Garg for 5 points of damage. (Riposte)", "Hit", 5, "Bloodthirsty ghoul", false)]
    public void CorpusMeleeTaken(string line, string skill, int amount, string attacker, bool self)
    {
        var evt = ParseOne<DamageTakenEvent>(line);
        Assert.Equal(attacker, evt.Attacker);
        Assert.Equal(amount, evt.Amount);
        Assert.True(evt.Melee);
        Assert.Equal(skill, evt.Ability);
        Assert.Equal(self, evt.Self);
    }

    [Theory]
    // "MOB tries to hit @P, but misses!" (5,500) — attacker's own miss
    [InlineData("A bloodthirsty ghoul tries to hit Garg, but misses!")]
    // "MOB tries to hit @P, but @P dodges!" (676) — Garg's OWN avoidance (reason discarded by LogParser either way)
    [InlineData("A zol ghoul knight tries to hit Garg, but Garg dodges!")]
    [InlineData("A shin ghoul knight tries to hit Garg, but Garg blocks!")]
    [InlineData("A froglok tsu shaman tries to hit Garg, but Garg parries!")]
    [InlineData("A zol ghoul knight tries to hit Garg, but Garg ripostes!")]
    public void CorpusMeleeMissTaken(string line)
    {
        var evt = ParseOne<MissEvent>(line);
        Assert.False(evt.Outgoing);
    }

    // "MOB tries to hit @P, but @P's magical skin absorbs the blow!" (222) — Garg's OWN rune
    [Fact]
    public void CorpusRuneBlockTaken()
    {
        var evt = ParseOne<RuneBlockEvent>("A shin ghoul knight tries to hit Garg, but Garg's magical skin absorbs the blow!");
        Assert.Equal("Shin ghoul knight", evt.Attacker);
    }

    // backtick apostrophe variant of the same shape ("`s" instead of "'s")
    [Fact]
    public void CorpusRuneBlockTakenBacktick()
    {
        var evt = ParseOne<RuneBlockEvent>("A shin ghoul knight tries to bash Garg, but Garg`s magical skin absorbs the blow!");
        Assert.Equal("Shin ghoul knight", evt.Attacker);
    }

    // "MOB hit @P for N points of magic damage by Lightning Bolt." (493)
    [Fact]
    public void CorpusSchoolDamageTaken()
    {
        var evt = ParseOne<DamageTakenEvent>("a tal ghoul wizard hit Garg for 138 points of magic damage by Lightning Bolt.");
        Assert.Equal("Tal ghoul wizard", evt.Attacker);
        Assert.Equal(138, evt.Amount);
        Assert.False(evt.Melee);
        Assert.Equal("Lightning Bolt", evt.Ability);
    }

    // ---- kills and deaths ----

    // "MOB has been slain by @P!" (1,035)
    [Fact]
    public void CorpusKillBySubject()
    {
        var evt = ParseOne<KillEvent>("A frenzied ghoul has been slain by Garg!");
        Assert.Equal("Frenzied ghoul", evt.Target);
        Assert.Equal("You", evt.Killer);
    }

    // "@P has been slain by MOB!" (10, full log)
    [Fact]
    public void CorpusDeathBySubject()
    {
        var evt = ParseOne<DeathEvent>("Garg has been slain by a Tesch Mal Gnoll!");
        Assert.Equal("a Tesch Mal Gnoll", evt.Killer);
    }

    // "@P died." (1, full log — no killer named)
    [Fact]
    public void CorpusDeathPlain()
    {
        var evt = ParseOne<DeathEvent>("Garg died.");
        Assert.Equal("", evt.Killer);
    }

    // ---- casts ----

    // "@P begins casting Healing." (103)
    [Fact]
    public void CorpusCast()
    {
        var evt = ParseOne<SpellCastEvent>("Garg begins casting Healing.");
        Assert.Equal("Healing", evt.Spell);
        Assert.False(evt.Song);
    }

    // ---- heals ----

    // "@P healed himself for N hit points by Healing." (65) — self-heal
    [Fact]
    public void CorpusHealSelf()
    {
        var evt = ParseOne<HealEvent>("Garg healed himself for 214 hit points by Healing.");
        Assert.Equal("Garg", evt.Target);
        Assert.Equal(214, evt.Amount);
        Assert.Equal("Healing", evt.Spell);
        Assert.True(evt.Outgoing);
    }

    // "@P healed you for N hit points by Healing." (22) — Garg heals the primary
    [Fact]
    public void CorpusHealSubjectHealsPrimary()
    {
        var evt = ParseOne<HealEvent>("Garg healed you for 183 hit points by Healing.");
        Assert.Equal(Primary, evt.Target);
        Assert.Equal(183, evt.Amount);
        Assert.True(evt.Outgoing);
    }

    // "You healed @P for N hit points." (1,925, no spell name) — primary heals Garg
    [Fact]
    public void CorpusHealPrimaryHealsSubjectNoSpell()
    {
        var evt = ParseOne<HealEvent>("You healed Garg for 104 hit points.");
        Assert.Equal(Primary, evt.Healer);
        Assert.Equal(104, evt.Amount);
        Assert.False(evt.Outgoing);
        Assert.Equal("Unknown", evt.Spell);
    }

    // "You healed @P over time for N hit points by Celestial Echo." (154) — HoT, named spell
    [Fact]
    public void CorpusHealPrimaryHealsSubjectOverTime()
    {
        var evt = ParseOne<HealEvent>("You healed Garg over time for 140 hit points by Celestial Echo.");
        Assert.Equal(Primary, evt.Healer);
        Assert.Equal(140, evt.Amount);
        Assert.Equal("Celestial Echo", evt.Spell);
        Assert.True(evt.OverTime);
    }

    // "You healed @P for N (N) hit points by Symbol of Ryltan." (26) — attempted/overheal pair
    [Fact]
    public void CorpusHealPrimaryHealsSubjectAttempted()
    {
        var evt = ParseOne<HealEvent>("You healed Garg for 157 (163) hit points by Symbol of Ryltan.");
        Assert.Equal(157, evt.Amount);
        Assert.Equal("Symbol of Ryltan", evt.Spell);
    }

    // heal by a bystander who is not the primary (from the full log): third party -> Garg
    [Fact]
    public void CorpusHealBystanderHealsSubject()
    {
        var evt = ParseOne<HealEvent>("Kellisanth healed Garg for 320 hit points by Greater Healing.");
        Assert.Equal("Kellisanth", evt.Healer);
        Assert.Equal(320, evt.Amount);
        Assert.False(evt.Outgoing);
    }

    // ---------------------------------------------------------------------
    // (b) name-boundary: things that must NOT be rewritten as combat.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("A basalt gargoyle hits Smargush for 4 points of damage.")]           // lowercase, different word
    [InlineData("Gargoyle slashes a shin ghoul knight for 12 points of damage.")]      // a DIFFERENT PC literally named "Gargoyle"
    [InlineData("Garg`s familiar regards you indifferently. (Lvl: 5)")]               // Garg's FAMILIAR, not Garg himself, not a warder
    [InlineData("Garg tells the group, 'inc train'")]                                 // chat
    [InlineData("Garg tells you, 'ready?'")]                                          // tell
    [InlineData("You will not evade me, Garg!")]                                      // NPC speech naming Garg mid-sentence
    public void NameBoundaryLinesAreNotRewritten(string line) =>
        Assert.Empty(Rewrite(line));

    // template: "@P tries to hit MOB, but MOB dodges!" (80) — a mob whose own name
    // happens to be "a basalt gargoyle" must not confuse the "Garg" word-boundary
    // check: this IS a real Garg line and must still be rewritten normally.
    [Fact]
    public void MobNamedGargoyleDoesNotBlockARealGargLine()
    {
        var evt = ParseOne<MissEvent>("Garg tries to hit a basalt gargoyle, but a basalt gargoyle dodges!");
        Assert.True(evt.Outgoing);
        Assert.Equal("Basalt gargoyle", evt.Target);
    }

    // ---------------------------------------------------------------------
    // (c) two-teammate line: both actors get their own rewritten perspective.
    // ---------------------------------------------------------------------

    [Fact]
    public void TwoTeammateHealLineYieldsBothPerspectives()
    {
        var pairs = Rewrite("Garg healed Jobantik for 120 hit points by Healing.", DuoRoster);
        Assert.Equal(2, pairs.Length);

        var gargPair = Assert.Single(pairs, p => p.Actor == "Garg");
        var gargEvt = Assert.IsType<HealEvent>(LogParser.Parse(DateTime.UtcNow, gargPair.Line));
        Assert.Equal("Jobantik", gargEvt.Target);
        Assert.True(gargEvt.Outgoing);
        Assert.Equal(120, gargEvt.Amount);

        var jobPair = Assert.Single(pairs, p => p.Actor == "Jobantik");
        var jobEvt = Assert.IsType<HealEvent>(LogParser.Parse(DateTime.UtcNow, jobPair.Line));
        Assert.False(jobEvt.Outgoing);
        Assert.Equal("Garg", jobEvt.Healer);
        Assert.Equal(120, jobEvt.Amount);
    }

    // ---------------------------------------------------------------------
    // (d) bystander not in roster -> nothing.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("Jthomn slashes a shin ghoul knight for 44 points of damage.")]
    [InlineData("A shin ghoul knight hits Jthomn for 12 points of damage.")]
    [InlineData("Jthomn healed Greko for 90 hit points by Healing.")]
    public void BystanderNotInRosterYieldsNothing(string line) =>
        Assert.Empty(Rewrite(line));

    // ---------------------------------------------------------------------
    // (e) the user's own lines -> nothing.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("You slash a shin ghoul knight for 44 points of damage.")]
    [InlineData("You try to slash a shin ghoul knight, but miss!")]
    [InlineData("You have slain a shin ghoul knight!")]
    [InlineData("You have been slain by a rock golem!")]
    [InlineData("You died.")]
    public void PrimaryOwnLinesYieldNothing(string line) =>
        Assert.Empty(Rewrite(line));

    // ---------------------------------------------------------------------
    // Teammate pets: "<Owner>'s warder" / "<Owner>`s warder" fold into the owner,
    // tagged IsPet, using the SAME rewrite rules as the owner acting directly.
    // ---------------------------------------------------------------------

    [Fact]
    public void OwnedPetFoldsIntoOwnerTaggedAsPet()
    {
        var pairs = Rewrite("Kellisanth`s warder hits a shin ghoul knight for 40 points of damage.", ["Kellisanth"]);
        var pair = Assert.Single(pairs);
        Assert.Equal("Kellisanth", pair.Actor);
        Assert.True(pair.IsPet);
        var evt = Assert.IsType<DamageDealtEvent>(LogParser.Parse(DateTime.UtcNow, pair.Line));
        Assert.Equal("Shin ghoul knight", evt.Target);
        Assert.Equal(40, evt.Amount);
    }

    [Fact]
    public void AnonymousPetIsIgnored()
    {
        // Garg's OWN combat pet (an SK/necro pet gets a generated single-word name,
        // never a possessive) is not attributable from the log at all — only the
        // explicit "<Owner>'s warder" shape is. "Jarartik" fighting on its own must
        // not be folded into Garg just because Garg is in the roster.
        Assert.Empty(Rewrite("Jarartik hits a shin ghoul knight for 20 points of damage.", ["Garg"]));
    }

    // =======================================================================
    // Templates from garg_tpl.txt intentionally SKIPPED (not rewritten), with why:
    //
    // - "@P's magical skin absorbs the damage of MOB's thorns/flames."  (92+67+6+1 lines)
    //   No first-person form of this line exists anywhere in the 66 MB log (zero
    //   "Your magical skin absorbs the damage" lines), so LogParser has no regex for
    //   it even for the primary character — there is nothing to rewrite INTO.
    // - "@P has captured MOB's attention!" / "... with an unparalleled approach!" /
    //   "@P was partially successful in capturing MOB's attention." /
    //   "@P failed to taunt MOB."  (175+11+13+16 lines)
    //   LogParser has no regex for taunt/capture-attention lines at all (verified:
    //   no match for "captured"/"taunt" anywhere in LogParser.cs).
    // - "@P activates Skull Bash."  (78 lines)
    //   LogParser only recognises the single fixed string "You activate Quick
    //   Buff." (DRA-339) — there is no general "You activate <ability>" line, so an
    //   arbitrary ability name (Skull Bash) has no parseable first-person form.
    // - Flavor/emote lines with no number ("@P feels much better.", "@P stumbles.",
    //   "@P begins to regenerate.", "@P's blood boils.", "@P is burning.", buff/debuff
    //   landing lines, /consider lines, "@P tells the group, …") — out of scope: the
    //   brief only requires every NUMERIC (damage/heal/kill/death) line to round-trip,
    //   and none of these carry a number LogParser tracks; several (chat, considers)
    //   are exercised above as name-boundary negatives instead.
    // =======================================================================
}
