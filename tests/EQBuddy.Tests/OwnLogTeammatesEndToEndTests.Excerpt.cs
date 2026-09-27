namespace EQBuddy.Tests;

public partial class OwnLogTeammatesEndToEndTests
{
    /// <summary>
    /// 292 lines of the player's REAL log (eqlog_Smargush_rivervale.txt, Sat Sep 26 2026):
    /// the two lines that put Garg in the group at 13:42:24, then a continuous stretch of
    /// duo combat from 13:55:58 to 13:56:55 with Garg's melee, procs, misses, kills and
    /// the mobs hitting him. Sanitized: chat lines (tells, says, shouts, channel traffic)
    /// were removed, and two lines of a bystander (Jthomn — real lines from Sep 05,
    /// re-stamped into this window) were inserted to prove a player who is NOT in the
    /// group is never counted.
    /// </summary>
    internal const string RealLogExcerpt = """
[Sat Sep 26 13:42:24 2026] You notify Garg that you agree to join the group.
[Sat Sep 26 13:42:24 2026] You have joined the group.
[Sat Sep 26 13:55:58 2026] Beginning to memorize Quickness...
[Sat Sep 26 13:55:59 2026] You have finished memorizing Quickness.
[Sat Sep 26 13:55:59 2026] Beginning to memorize Cannibalize...
[Sat Sep 26 13:56:00 2026] You have finished memorizing Cannibalize.
[Sat Sep 26 13:56:00 2026] Beginning to memorize Regeneration...
[Sat Sep 26 13:56:01 2026] You have finished memorizing Regeneration.
[Sat Sep 26 13:56:02 2026] Beginning to memorize Feign Death...
[Sat Sep 26 13:56:02 2026] You have finished memorizing Feign Death.
[Sat Sep 26 13:56:02 2026] Beginning to memorize Calm...
[Sat Sep 26 13:56:03 2026] You have finished memorizing Calm.
[Sat Sep 26 13:56:04 2026] You feel smaller.
[Sat Sep 26 13:56:19 2026] An elemental warrior hits Garg for 36 points of damage.
[Sat Sep 26 13:56:19 2026] An elemental warrior tries to hit Garg, but misses!
[Sat Sep 26 13:56:19 2026] An elemental warrior tries to cleave Garg, but Garg ripostes!
[Sat Sep 26 13:56:19 2026] Garg tries to slash an elemental warrior, but an elemental warrior parries!
[Sat Sep 26 13:56:19 2026] An elemental warrior tries to bash Garg, but Garg parries!
[Sat Sep 26 13:56:19 2026] An elemental warrior tries to bash Garg, but misses!
[Sat Sep 26 13:56:19 2026] An elemental warrior kicks Garg for 17 points of damage.
[Sat Sep 26 13:56:20 2026] An elemental warrior hits Garg for 26 points of damage.
[Sat Sep 26 13:56:20 2026] An elemental warrior cleaves Garg for 13 points of damage.
[Sat Sep 26 13:56:20 2026] An elemental warrior tries to bash Garg, but misses!
[Sat Sep 26 13:56:20 2026] An elemental warrior tries to bash Garg, but misses!
[Sat Sep 26 13:56:20 2026] An elemental warrior tries to kick Garg, but misses!
[Sat Sep 26 13:56:20 2026] Auto attack is on.
[Sat Sep 26 13:56:20 2026] You try to bash an elemental warrior, but miss!
[Sat Sep 26 13:56:20 2026] You bash an elemental warrior for 110 points of damage. (Critical)
[Sat Sep 26 13:56:20 2026] You reave an elemental warrior for 34 points of damage.
[Sat Sep 26 13:56:20 2026] You hit an elemental warrior for 266 points of magic damage by Reaving Strike.
[Sat Sep 26 13:56:20 2026] An elemental warrior staggers.
[Sat Sep 26 13:56:20 2026] You try to reave an elemental warrior, but miss!
[Sat Sep 26 13:56:20 2026] You slash an elemental warrior for 105 points of damage.
[Sat Sep 26 13:56:22 2026] An elemental warrior tries to hit Garg, but Garg blocks!
[Sat Sep 26 13:56:23 2026] Garg tries to slash an elemental warrior, but misses!
[Sat Sep 26 13:56:23 2026] An elemental warrior tries to hit YOU, but misses!
[Sat Sep 26 13:56:23 2026] You try to slash an elemental warrior, but miss!
[Sat Sep 26 13:56:23 2026] You slash an elemental warrior for 159 points of damage.
[Sat Sep 26 13:56:23 2026] Targeted (NPC): an elemental warrior
[Sat Sep 26 13:56:23 2026] Stand close to and right click on the NPC to attack it.
[Sat Sep 26 13:56:23 2026] Garg strikes an elemental warrior for 175 points of damage.
[Sat Sep 26 13:56:24 2026] An elemental warrior tries to hit Garg, but Garg blocks!
[Sat Sep 26 13:56:24 2026] An elemental warrior hits Garg for 28 points of damage.
[Sat Sep 26 13:56:24 2026] Garg tries to bash an elemental warrior, but misses!
[Sat Sep 26 13:56:25 2026] An elemental warrior tries to hit YOU, but misses!
[Sat Sep 26 13:56:25 2026] Garg kicks an elemental warrior for 220 points of damage.
[Sat Sep 26 13:56:25 2026] Garg kicks an elemental warrior for 220 points of damage.
[Sat Sep 26 13:56:26 2026] Garg tries to slash an elemental warrior, but misses!
[Sat Sep 26 13:56:26 2026] Garg slashes an elemental warrior for 96 points of damage.
[Sat Sep 26 13:56:27 2026] You bash an elemental warrior for 101 points of damage.
[Sat Sep 26 13:56:27 2026] You slash an elemental warrior for 96 points of damage.
[Sat Sep 26 13:56:27 2026] You slash an elemental warrior for 120 points of damage.
[Sat Sep 26 13:56:27 2026] An elemental warrior tries to hit Garg, but misses!
[Sat Sep 26 13:56:27 2026] An elemental warrior bashes Garg for 10 points of damage.
[Sat Sep 26 13:56:27 2026] An elemental warrior bashes Garg for 10 points of damage.
[Sat Sep 26 13:56:27 2026] An elemental warrior hits Garg for 40 points of damage.
[Sat Sep 26 13:56:27 2026] An elemental warrior cleaves Garg for 63 points of damage.
[Sat Sep 26 13:56:27 2026] An elemental warrior tries to bash Garg, but misses!
[Sat Sep 26 13:56:27 2026] An elemental warrior tries to hit YOU, but misses!
[Sat Sep 26 13:56:27 2026] An elemental warrior tries to hit YOU, but misses!
[Sat Sep 26 13:56:27 2026] An elemental warrior tries to kick Garg, but misses!
[Sat Sep 26 13:56:27 2026] An elemental warrior tries to kick Garg, but misses!
[Sat Sep 26 13:56:27 2026] An elemental warrior tries to hit Garg, but misses!
[Sat Sep 26 13:56:27 2026] An elemental warrior hits Garg for 21 points of damage.
[Sat Sep 26 13:56:27 2026] An elemental warrior tries to cleave Garg, but Garg parries!
[Sat Sep 26 13:56:27 2026] An elemental warrior tries to bash Garg, but misses!
[Sat Sep 26 13:56:27 2026] An elemental warrior bashes Garg for 10 points of damage.
[Sat Sep 26 13:56:27 2026] An elemental warrior tries to kick Garg, but misses!
[Sat Sep 26 13:56:27 2026] Garg strikes an elemental warrior for 120 points of damage.
[Sat Sep 26 13:56:27 2026] Garg strikes an elemental warrior for 175 points of damage. (Critical)
[Sat Sep 26 13:56:27 2026] Garg strikes an elemental warrior for 160 points of damage.
[Sat Sep 26 13:56:27 2026] An elemental warrior tries to bash YOU, but misses!
[Sat Sep 26 13:56:27 2026] An elemental warrior bashes YOU for 10 points of damage.
[Sat Sep 26 13:56:28 2026] An elemental warrior tries to kick Garg, but Garg's magical skin absorbs the blow!
[Sat Sep 26 13:56:28 2026] An elemental warrior tries to kick Garg, but Garg's magical skin absorbs the blow!
[Sat Sep 26 13:56:28 2026] An elemental warrior tries to hit YOU, but misses! (Riposte)
[Sat Sep 26 13:56:28 2026] An elemental warrior tries to kick YOU, but misses!
[Sat Sep 26 13:56:28 2026] An elemental warrior kicks YOU for 36 points of damage.
[Sat Sep 26 13:56:29 2026] An elemental warrior hits Garg for 40 points of damage.
[Sat Sep 26 13:56:29 2026] Garg frenzies on an elemental warrior for 105 points of damage.
[Sat Sep 26 13:56:29 2026] Garg frenzies on an elemental warrior for 180 points of damage. (Critical)
[Sat Sep 26 13:56:29 2026] Garg tries to smite an elemental warrior, but misses!
[Sat Sep 26 13:56:29 2026] Garg smites an elemental warrior for 24 points of damage.
[Sat Sep 26 13:56:29 2026] Garg hit an elemental warrior for 67 points of magic damage by Smiting Strike.
[Sat Sep 26 13:56:29 2026] An elemental warrior staggers.
[Sat Sep 26 13:56:29 2026] Garg bashes an elemental warrior for 401 points of damage.
[Sat Sep 26 13:56:29 2026] An elemental warrior hits YOU for 22 points of damage.
[Sat Sep 26 13:56:29 2026] An elemental warrior tries to hit Garg, but misses!
[Sat Sep 26 13:56:29 2026] An elemental warrior tries to hit Garg, but Garg ripostes!
[Sat Sep 26 13:56:29 2026] Garg tries to slash an elemental warrior, but misses! (Riposte)
[Sat Sep 26 13:56:30 2026] An elemental warrior tries to hit Garg, but misses!
[Sat Sep 26 13:56:30 2026] Garg kicks an elemental warrior for 315 points of damage.
[Sat Sep 26 13:56:30 2026] You reave an elemental warrior for 72 points of damage.
[Sat Sep 26 13:56:30 2026] You hit an elemental warrior for 266 points of magic damage by Reaving Strike.
[Sat Sep 26 13:56:30 2026] An elemental warrior staggers.
[Sat Sep 26 13:56:30 2026] You try to slash an elemental warrior, but miss!
[Sat Sep 26 13:56:30 2026] Jthomn slashes a tal ghoul wizard for 22 points of damage.
[Sat Sep 26 13:56:30 2026] A tal ghoul wizard has been slain by Jthomn!
[Sat Sep 26 13:56:31 2026] Garg tries to strike an elemental warrior, but misses!
[Sat Sep 26 13:56:31 2026] Garg tries to strike an elemental warrior, but misses!
[Sat Sep 26 13:56:31 2026] Garg strikes an elemental warrior for 160 points of damage.
[Sat Sep 26 13:56:31 2026] Garg tries to strike an elemental warrior, but misses!
[Sat Sep 26 13:56:31 2026] An elemental warrior cleaves YOU for 13 points of damage.
[Sat Sep 26 13:56:31 2026] Garg tries to slash an elemental warrior, but misses!
[Sat Sep 26 13:56:31 2026] Garg slashes an elemental warrior for 366 points of damage. (Critical)
[Sat Sep 26 13:56:32 2026] You can't use that command right now...
[Sat Sep 26 13:56:32 2026] An elemental warrior hits YOU for 72 points of damage.
[Sat Sep 26 13:56:32 2026] An elemental warrior hits YOU for 85 points of damage.
[Sat Sep 26 13:56:32 2026] You bash an elemental warrior for 101 points of damage.
[Sat Sep 26 13:56:32 2026] An elemental warrior tries to hit Garg, but Garg's magical skin absorbs the blow!
[Sat Sep 26 13:56:32 2026] An elemental warrior tries to hit Garg, but misses!
[Sat Sep 26 13:56:32 2026] An elemental warrior tries to cleave Garg, but Garg's magical skin absorbs the blow!
[Sat Sep 26 13:56:32 2026] An elemental warrior tries to hit Garg, but Garg ripostes!
[Sat Sep 26 13:56:32 2026] Garg tries to slash an elemental warrior, but misses! (Riposte)
[Sat Sep 26 13:56:32 2026] You begin casting Siphon Life VI.
[Sat Sep 26 13:56:34 2026] An elemental warrior tries to hit Garg, but misses!
[Sat Sep 26 13:56:34 2026] An elemental warrior tries to hit Garg, but Garg blocks!
[Sat Sep 26 13:56:34 2026] Garg tries to bash an elemental warrior, but an elemental warrior dodges!
[Sat Sep 26 13:56:34 2026] Garg tries to bash an elemental warrior, but an elemental warrior parries!
[Sat Sep 26 13:56:34 2026] An elemental warrior hits YOU for 36 points of damage.
[Sat Sep 26 13:56:34 2026] An elemental warrior hits YOU for 29 points of damage.
[Sat Sep 26 13:56:34 2026] An elemental warrior hits Garg for 34 points of damage.
[Sat Sep 26 13:56:34 2026] An elemental warrior hits Garg for 21 points of damage.
[Sat Sep 26 13:56:34 2026] You regain your concentration and continue your casting.
[Sat Sep 26 13:56:34 2026] Your Nisch Mas Ilkvel flickers with a pale light.
[Sat Sep 26 13:56:34 2026] Your Polished Mithril Mask (Exaltation) feels alive with power.
[Sat Sep 26 13:56:34 2026] You hit an elemental warrior for 252 points of magic damage by Siphon Life VI.
[Sat Sep 26 13:56:34 2026] An elemental warrior staggers.
[Sat Sep 26 13:56:34 2026] You healed Smargush for 252 hit points by Siphon Life.
[Sat Sep 26 13:56:34 2026] You try to slash an elemental warrior, but miss!
[Sat Sep 26 13:56:34 2026] You slash an elemental warrior for 41 points of damage.
[Sat Sep 26 13:56:35 2026] Garg slashes an elemental warrior for 221 points of damage.
[Sat Sep 26 13:56:35 2026] Garg slashes an elemental warrior for 121 points of damage.
[Sat Sep 26 13:56:35 2026] An elemental warrior tries to bash Garg, but misses!
[Sat Sep 26 13:56:35 2026] An elemental warrior bashes Garg for 10 points of damage.
[Sat Sep 26 13:56:35 2026] Garg strikes an elemental warrior for 45 points of damage.
[Sat Sep 26 13:56:35 2026] Garg strikes an elemental warrior for 55 points of damage.
[Sat Sep 26 13:56:35 2026] Garg tries to strike an elemental warrior, but an elemental warrior parries!
[Sat Sep 26 13:56:35 2026] Garg tries to strike an elemental warrior, but misses!
[Sat Sep 26 13:56:35 2026] Garg strikes an elemental warrior for 175 points of damage. (Critical)
[Sat Sep 26 13:56:35 2026] An elemental warrior kicks Garg for 6 points of damage.
[Sat Sep 26 13:56:35 2026] Garg kicks an elemental warrior for 220 points of damage.
[Sat Sep 26 13:56:35 2026] Garg kicks an elemental warrior for 110 points of damage.
[Sat Sep 26 13:56:35 2026] An elemental warrior tries to kick Garg, but misses!
[Sat Sep 26 13:56:35 2026] An elemental warrior tries to kick Garg, but misses!
[Sat Sep 26 13:56:35 2026] You slash an elemental warrior for 73 points of damage.
[Sat Sep 26 13:56:35 2026] You try to slash an elemental warrior, but miss!
[Sat Sep 26 13:56:35 2026] An elemental warrior bashes Garg for 10 points of damage.
[Sat Sep 26 13:56:35 2026] An elemental warrior tries to bash Garg, but misses!
[Sat Sep 26 13:56:35 2026] An elemental warrior tries to bash YOU, but misses!
[Sat Sep 26 13:56:35 2026] An elemental warrior tries to kick YOU, but misses!
[Sat Sep 26 13:56:35 2026] An elemental warrior tries to kick YOU, but misses!
[Sat Sep 26 13:56:37 2026] An elemental warrior hits YOU for 13 points of damage.
[Sat Sep 26 13:56:37 2026] An elemental warrior hits Garg for 13 points of damage.
[Sat Sep 26 13:56:37 2026] An elemental warrior hits Garg for 78 points of damage.
[Sat Sep 26 13:56:37 2026] An elemental warrior hits Garg for 68 points of damage.
[Sat Sep 26 13:56:37 2026] You try to bash an elemental warrior, but an elemental warrior parries!
[Sat Sep 26 13:56:37 2026] You bash an elemental warrior for 110 points of damage. (Critical)
[Sat Sep 26 13:56:37 2026] An elemental warrior tries to hit Garg, but misses!
[Sat Sep 26 13:56:37 2026] You slash an elemental warrior for 178 points of damage.
[Sat Sep 26 13:56:38 2026] Garg slashes an elemental warrior for 117 points of damage.
[Sat Sep 26 13:56:38 2026] Garg tries to slash an elemental warrior, but misses!
[Sat Sep 26 13:56:38 2026] Garg frenzies on an elemental warrior for 346 points of damage. (Finishing Blow)
[Sat Sep 26 13:56:38 2026] You gain party experience! (0.536%)
[Sat Sep 26 13:56:38 2026] You receive 5 gold and 7 silver from the corpse.
[Sat Sep 26 13:56:38 2026] An elemental warrior has been slain by Garg!
[Sat Sep 26 13:56:39 2026] An elemental warrior tries to cleave Garg, but misses!
[Sat Sep 26 13:56:39 2026] An elemental warrior cleaves Garg for 13 points of damage.
[Sat Sep 26 13:56:39 2026] Garg strikes an elemental warrior for 306 points of damage. (Critical)
[Sat Sep 26 13:56:39 2026] Garg strikes an elemental warrior for 160 points of damage.
[Sat Sep 26 13:56:39 2026] Garg strikes an elemental warrior for 186 points of damage. (Critical)
[Sat Sep 26 13:56:39 2026] Garg tries to strike an elemental warrior, but misses!
[Sat Sep 26 13:56:39 2026] Garg bashes an elemental warrior for 401 points of damage.
[Sat Sep 26 13:56:39 2026] Garg bashes an elemental warrior for 401 points of damage.
[Sat Sep 26 13:56:39 2026] An elemental warrior tries to hit YOU, but YOU dodge!
[Sat Sep 26 13:56:39 2026] An elemental warrior hits YOU for 89 points of damage.
[Sat Sep 26 13:56:39 2026] An elemental warrior tries to hit Garg, but misses!
[Sat Sep 26 13:56:40 2026] You begin to change your invocation.
[Sat Sep 26 13:56:40 2026] You slash an elemental warrior for 193 points of damage. (Critical)
[Sat Sep 26 13:56:40 2026] You slash an elemental warrior for 103 points of damage.
[Sat Sep 26 13:56:40 2026] You begin reciting the divine invocation.
[Sat Sep 26 13:56:40 2026] You reave an elemental warrior for 48 points of damage.
[Sat Sep 26 13:56:40 2026] You hit an elemental warrior for 266 points of magic damage by Reaving Strike.
[Sat Sep 26 13:56:40 2026] An elemental warrior staggers.
[Sat Sep 26 13:56:40 2026] You try to reave an elemental warrior, but miss!
[Sat Sep 26 13:56:40 2026] An elemental warrior tries to hit Garg, but misses! (Riposte)
[Sat Sep 26 13:56:40 2026] Your Quickness spell on Garg has been overwritten.
[Sat Sep 26 13:56:40 2026] Garg begins to move faster.
[Sat Sep 26 13:56:40 2026] Garg tries to slash an elemental warrior, but misses!
[Sat Sep 26 13:56:40 2026] Garg slashes an elemental warrior for 97 points of damage.
[Sat Sep 26 13:56:41 2026] An elemental warrior tries to hit Garg, but misses!
[Sat Sep 26 13:56:41 2026] An elemental warrior tries to hit YOU, but misses!
[Sat Sep 26 13:56:41 2026] An elemental warrior tries to hit Garg, but misses!
[Sat Sep 26 13:56:42 2026] An elemental warrior tries to hit Garg, but Garg's magical skin absorbs the blow!
[Sat Sep 26 13:56:42 2026] You slash an elemental warrior for 94 points of damage.
[Sat Sep 26 13:56:42 2026] An elemental warrior tries to bash Garg, but Garg parries!
[Sat Sep 26 13:56:43 2026] You bash an elemental warrior for 101 points of damage.
[Sat Sep 26 13:56:43 2026] You try to bash an elemental warrior, but miss!
[Sat Sep 26 13:56:43 2026] Garg strikes an elemental warrior for 150 points of damage.
[Sat Sep 26 13:56:43 2026] Garg tries to strike an elemental warrior, but an elemental warrior parries!
[Sat Sep 26 13:56:43 2026] Garg strikes an elemental warrior for 10 points of damage.
[Sat Sep 26 13:56:43 2026] An elemental warrior tries to cleave YOU, but misses!
[Sat Sep 26 13:56:43 2026] An elemental warrior bashes YOU for 10 points of damage.
[Sat Sep 26 13:56:43 2026] An elemental warrior tries to kick Garg, but Garg's magical skin absorbs the blow!
[Sat Sep 26 13:56:43 2026] An elemental warrior tries to kick Garg, but Garg blocks!
[Sat Sep 26 13:56:43 2026] An elemental warrior tries to kick Garg, but misses!
[Sat Sep 26 13:56:43 2026] An elemental warrior tries to kick Garg, but Garg's magical skin absorbs the blow!
[Sat Sep 26 13:56:43 2026] An elemental warrior tries to bash Garg, but misses!
[Sat Sep 26 13:56:43 2026] An elemental warrior tries to bash Garg, but misses!
[Sat Sep 26 13:56:43 2026] An elemental warrior kicks YOU for 29 points of damage.
[Sat Sep 26 13:56:44 2026] Garg tries to slash an elemental warrior, but misses!
[Sat Sep 26 13:56:44 2026] An elemental warrior tries to hit YOU, but misses!
[Sat Sep 26 13:56:44 2026] An elemental warrior tries to hit Garg, but Garg ripostes!
[Sat Sep 26 13:56:44 2026] Garg slashes an elemental warrior for 132 points of damage. (Riposte Critical)
[Sat Sep 26 13:56:44 2026] Garg slashes an elemental warrior for 184 points of damage. (Riposte)
[Sat Sep 26 13:56:44 2026] Garg bashes an elemental warrior for 401 points of damage.
[Sat Sep 26 13:56:44 2026] Garg bashes an elemental warrior for 401 points of damage.
[Sat Sep 26 13:56:44 2026] An elemental warrior tries to hit Garg, but misses!
[Sat Sep 26 13:56:44 2026] Auto attack is off.
[Sat Sep 26 13:56:45 2026] Garg kicks an elemental warrior for 454 points of damage. (Critical)
[Sat Sep 26 13:56:45 2026] Garg tries to kick an elemental warrior, but misses!
[Sat Sep 26 13:56:46 2026] An elemental warrior tries to hit YOU, but misses!
[Sat Sep 26 13:56:46 2026] An elemental warrior tries to hit Garg, but Garg dodges!
[Sat Sep 26 13:56:46 2026] An elemental warrior tries to hit Garg, but Garg's magical skin absorbs the blow! (Riposte)
[Sat Sep 26 13:56:46 2026] Garg strikes an elemental warrior for 185 points of damage.
[Sat Sep 26 13:56:46 2026] Garg strikes an elemental warrior for 105 points of damage.
[Sat Sep 26 13:56:46 2026] Garg tries to strike an elemental warrior, but misses!
[Sat Sep 26 13:56:47 2026] You begin casting Siphon Life VI.
[Sat Sep 26 13:56:47 2026] Garg frenzies on an elemental warrior for 107 points of damage.
[Sat Sep 26 13:56:47 2026] Your faction standing with Minions of Underfoot has been adjusted by -3.
[Sat Sep 26 13:56:47 2026] You gain party experience! (0.536%)
[Sat Sep 26 13:56:47 2026] You receive 8 gold from the corpse.
[Sat Sep 26 13:56:47 2026] An elemental warrior has been slain by Garg!
[Sat Sep 26 13:56:48 2026] Garg slashes an elemental warrior for 93 points of damage.
[Sat Sep 26 13:56:48 2026] Garg bashes an elemental warrior for 401 points of damage.
[Sat Sep 26 13:56:48 2026] Garg bashes an elemental warrior for 401 points of damage.
[Sat Sep 26 13:56:48 2026] An elemental warrior hits YOU for 89 points of damage.
[Sat Sep 26 13:56:48 2026] An elemental warrior hits YOU for 45 points of damage.
[Sat Sep 26 13:56:49 2026] You regain your concentration and continue your casting.
[Sat Sep 26 13:56:49 2026] Your Nisch Mas Ilkvel flickers with a pale light.
[Sat Sep 26 13:56:49 2026] Your Polished Mithril Mask (Exaltation) feels alive with power.
[Sat Sep 26 13:56:49 2026] You hit an elemental warrior for 243 points of magic damage by Siphon Life VI.
[Sat Sep 26 13:56:49 2026] An elemental warrior staggers.
[Sat Sep 26 13:56:49 2026] You healed Smargush for 243 hit points by Siphon Life.
[Sat Sep 26 13:56:49 2026] You healed Garg for 88 hit points.
[Sat Sep 26 13:56:49 2026] You healed Garg for 29 hit points.
[Sat Sep 26 13:56:50 2026] Garg tries to slash an elemental warrior, but misses!
[Sat Sep 26 13:56:50 2026] Garg strikes an elemental warrior for 175 points of damage.
[Sat Sep 26 13:56:50 2026] Garg strikes an elemental warrior for 335 points of damage.
[Sat Sep 26 13:56:50 2026] Garg kicks an elemental warrior for 260 points of damage.
[Sat Sep 26 13:56:50 2026] An elemental warrior tries to bash YOU, but misses!
[Sat Sep 26 13:56:51 2026] An elemental warrior tries to hit Garg, but misses!
[Sat Sep 26 13:56:51 2026] An elemental warrior hits Garg for 15 points of damage.
[Sat Sep 26 13:56:51 2026] An elemental warrior tries to cleave Garg, but misses!
[Sat Sep 26 13:56:51 2026] An elemental warrior tries to bash Garg, but misses!
[Sat Sep 26 13:56:51 2026] An elemental warrior tries to bash Garg, but Garg ripostes!
[Sat Sep 26 13:56:51 2026] Garg slashes an elemental warrior for 89 points of damage. (Riposte)
[Sat Sep 26 13:56:51 2026] Garg slashes an elemental warrior for 95 points of damage. (Riposte)
[Sat Sep 26 13:56:51 2026] An elemental warrior tries to kick Garg, but Garg blocks!
[Sat Sep 26 13:56:51 2026] An elemental warrior tries to kick YOU, but misses!
[Sat Sep 26 13:56:51 2026] An elemental warrior hits YOU for 89 points of damage.
[Sat Sep 26 13:56:51 2026] You begin casting Siphon Life VI.
[Sat Sep 26 13:56:51 2026] An elemental warrior tries to hit Garg, but misses!
[Sat Sep 26 13:56:53 2026] Garg begins to move faster.
[Sat Sep 26 13:56:53 2026] Garg slashes an elemental warrior for 197 points of damage.
[Sat Sep 26 13:56:53 2026] An elemental warrior tries to hit YOU, but misses!
[Sat Sep 26 13:56:53 2026] Garg bashes an elemental warrior for 401 points of damage.
[Sat Sep 26 13:56:53 2026] Garg bashes an elemental warrior for 401 points of damage.
[Sat Sep 26 13:56:53 2026] You regain your concentration and continue your casting.
[Sat Sep 26 13:56:53 2026] Your Nisch Mas Ilkvel flickers with a pale light.
[Sat Sep 26 13:56:53 2026] Your Polished Mithril Mask (Exaltation) feels alive with power.
[Sat Sep 26 13:56:53 2026] Your faction standing with Minions of Underfoot has been adjusted by -3.
[Sat Sep 26 13:56:53 2026] You gain party experience! (0.633%)
[Sat Sep 26 13:56:53 2026] You receive 3 platinum, 8 silver and 1 copper from the corpse.
[Sat Sep 26 13:56:53 2026] You hit an elemental warrior for 109 points of magic damage by Siphon Life.
[Sat Sep 26 13:56:53 2026] An elemental warrior staggers.
[Sat Sep 26 13:56:53 2026] You have slain an elemental warrior!
[Sat Sep 26 13:56:53 2026] You healed Smargush for 109 hit points by Siphon Life.
[Sat Sep 26 13:56:53 2026] You healed Garg for 88 hit points.
[Sat Sep 26 13:56:53 2026] You healed Garg for 29 hit points.
[Sat Sep 26 13:56:53 2026] --You have looted a Crystallized Sulfur from an elemental warrior's corpse.--
[Sat Sep 26 13:56:53 2026] An elemental warrior hits Garg for 13 points of damage.
[Sat Sep 26 13:56:54 2026] Garg tries to strike an elemental warrior, but misses!
[Sat Sep 26 13:56:55 2026] Garg kicks an elemental warrior for 557 points of damage. (Critical)
[Sat Sep 26 13:56:55 2026] Garg kicks an elemental warrior for 361 points of damage. (Finishing Blow)
[Sat Sep 26 13:56:55 2026] Your faction standing with Minions of Underfoot has been adjusted by -3.
[Sat Sep 26 13:56:55 2026] You gain party experience! (0.583%)
[Sat Sep 26 13:56:55 2026] You receive 4 platinum, 9 gold, 8 silver and 5 copper from the corpse.
[Sat Sep 26 13:56:55 2026] An elemental warrior has been slain by Garg!
[Sat Sep 26 13:56:55 2026] --You have looted a Crystallized Sulfur from an elemental warrior's corpse.--
[Sat Sep 26 13:56:55 2026] --You have looted a Mote of Minor Potential from an elemental warrior's corpse.--
[Sat Sep 26 13:56:57 2026] You begin casting Siphon Life VI.
""";
}
