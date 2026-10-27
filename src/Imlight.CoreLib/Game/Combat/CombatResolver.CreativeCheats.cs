/*
 * Creative cheats (Niduus, 2026-10-26: "expand more on conditions on the overall cheats blocks. Add new stuff that's
 * just spewing out creativity desires. I really want people to look at the system and be like 'WOW! You can legit do
 * anything'"). For players and creatures alike, as every cheat (BossCheats.cs).
 *
 * Conditions (a cheat's "Conditions" list, each { "If": kind, ... }): every entry must hold, except that the entries of one
 * "Any" group (1, 2...) need only one of them to hold (OR); "Not": true turns an entry round (NOT). Who is a target word
 * (Self, Caster, Receiver, Matched, LastAttacker, Summoner, p1-p4, e1-e4...) or a side: AnyEnemy / AnyAlly / Anyone (someone
 * there), EveryEnemy / EveryAlly / Everyone (all of them), EnemySide / AllySide / BothSides (added together, or for a share of
 * health their average); the one found on a side is the target Matched. Op compares with Value: <, <=, >, >=, =, != (>= when
 * empty).
 *   Health        Who's health (Percent: share of the most) Op Value.
 *   HealthLost    health Who lost since the round started (Percent: of the most) Op Value. HealthGained: gained.
 *   Standing      how many stand on Side (Us, Them, All) Op Value.          Outnumbered: fewer stand on its side.
 *   Round         the battle's round is Offset of every Every (Every 2, Offset 1: odd rounds; Every 3, Offset 0: 3, 6, 9).
 *   GoesFirst     its side moves first this round.
 *   Fired         how many times its cheat number Cheat (0: this one) went off this battle Op Value.
 *   Casts         how many spells Who (the casters: Self, Allies, Enemies, Anyone, Caster or a target word) cast on On, of
 *                 Spell (names or IDs, commas), School, Kind (a category), Result (Landed, Crit, Fizzled, "" any), Within
 *                 (Round, LastRound, "" the battle) Op Value; Op and Value empty: at least one. "No one cast Tower Shield this
 *                 battle": Casts, Spell Tower Shield, Op =, Value 0.
 *   LastSpell     the last spell Who cast is Spell, of School, Kind, CostMin-CostMax pips, Treasure / Enchanted (Yes, No),
 *                 Result (Landed, Crit, Fizzled).
 *   Effects       how many charms, wards, auras or over-times of Kind (commas) from Spell Who has Op Value.
 *   Pips          Who's pips of Kind ("" all, Normal, Power, Shadow, or a school's) Op Value.
 *   Level         Who's level Op Value.  School: Who is of School.  Name: Who's name holds Name.  Creature: Who is of that
 *                 template.  Boss: Who is a boss.  Player: Who is a wizard.
 *   InBattle      someone of Creature (or whose name holds Name) stands on Side (Us, Them, "" either).
 *   Roll          a Value percent chance.
 *   Var           the variable Var (Scope "" its own, Battle the battle's) Op Value.
 *
 * Dos:
 *   SetVar        Var (Scope) is Set to, Add, Subtract, Multiply by Amount, or Random from 0 to Amount. A message's {Var} shows it.
 *   IfVar         the next Count steps (1) only if Var Op Amount.         Repeat: the next Count steps (1) Times times.
 *   MaxHealth     Target's most health is set to Amount (Op Add: raised by it; Percent: Amount is a share of the most now);
 *                 its health stays, and is never above the new most. Only the server knows it: the game's bar keeps its top.
 *   Clone         a copy of Target (a creature, or a custom) joins its side, Count of them (1).
 *   CopySpell     it casts the spell that set the cheat off too, on Target ("" as that spell aims).
 *   Redirect      the attacker's spell fizzles, and the attacker casts it (free) on Target instead: the first one Target
 *                 names, as the cheat's owner sees it (Self: the owner; Enemy, if left out: a random one of the attacker's side).
 *   StealEffects  Count (0: all) of Target's charms, wards or auras of Kind come off and go on it. The game does not draw the
 *                 moved ones (they work).
 *   RunCheat      the steps of its cheat number Cheat (with Check: only if that cheat's Ifs hold too).
 *   CheatOnOff    its cheat number Cheat is turned Kind Off (or On again) for the rest of the battle.
 *   Silence       Target's spells fizzle for Rounds rounds (1).
 *
 * Hand tricks, extra turns, pips and school (Niduus, 2026-10-26: "if possible, add it"; CombatDuelSubCircle.Cheats.cs):
 *   AddCard       Count (1) cards of Spell / SpellID go into Target's (Self) next hand, in place of random ones when it is full.
 *   Discard       Count (0: all that fit) cards of Spell (names or IDs, commas) and Kind (a category), or random ones, come
 *                 out of Target's (Caster) next hand, used up as a discard.
 *   ShuffleHand   Target's (Caster) hand goes back into the deck before the next one is drawn (DeckToo: the used cards too).
 *   LockCards     cards of Spell / Kind are kept out of Target's (Caster) next Rounds (1) hands; they stay in the deck.
 *                 A wizard sees each of these in the hand the game shows at the next planning phase; a creature's AI picks
 *                 its next move from the hand as it is then.
 *   ExtraTurn     Target (Self) casts again at the end of the round, Times (1) times, for Rounds (1) rounds: the spell it cast
 *                 that round (at the same one), or Spell / SpellID. A free cast, drawn as one; nothing if it cast nothing.
 *   PipGain       Target (Self) gets Amount pips a round (0: none; 1 is the usual) and Power percent of them as power pips
 *                 ("" as usual), from the next round's pips for Rounds rounds (0: the rest of the battle). The game shows the pips.
 *   ChangeSchool  Target (Self) counts as of School for Rounds rounds (0: the rest of the battle): the school Ifs, spell
 *                 requirements, its school pips, mastery and archmastery; with Stats its boosts, resists, pierce and other
 *                 school stats of its own school and School trade places. The game shows nothing of it.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Combat;

public partial class CombatResolver {

    // A spell someone cast this battle: the round, the caster and its side, the spell, whom it was aimed at and how it went.
    private sealed record CastRecord(int Round, CombatDuelSubCircle Caster, CombatTeam Team, uint SpellId, SpellTemplate Spell,
                                     HashSet<int> Targets, bool Crit, bool Fizzled, bool Enchanted);

    private readonly List<CastRecord> _castLog = [];

    // The cheats' variables (SetVar), by owner's global id (BATTLE_VARS for the battle's) and name.
    private readonly Dictionary<(ulong Owner, string Name), int> _cheatVars = [];

    // Everyone's health as the round started (HealthLost, HealthGained).
    private readonly Dictionary<CombatDuelSubCircle, int> _roundStartHealth = [];
    private int _roundStartRound = -1;

    // Cheats turned off by a CheatOnOff step, by owner, template and cheat.
    private readonly HashSet<(CombatDuelSubCircle Boss, ulong Template, int Cheat)> _cheatsTurnedOff = [];

    // Who is silenced, up to and with which round.
    private readonly Dictionary<CombatDuelSubCircle, int> _silencedUntil = [];

    // RunCheat inside RunCheat, so a cheat that runs itself stops.
    private int _runCheatDepth;

    private int BattleRound => (int) _duel.m_roundNum;

    private void NoteRoundStart() {
        if (BattleRound == _roundStartRound) {
            return;
        }

        _roundStartRound = BattleRound;
        _roundStartHealth.Clear();
        foreach (var c in _subCircles.Where(c => c is not null && c.Occupied && c.ParticipantGameStats is not null)) {
            _roundStartHealth[c] = c.ParticipantGameStats.m_currentHitpoints;
        }
    }

    private void NoteCasts(CombatDuelSubCircle mover, List<(CombatAction Action, SpellTemplate Spell)> cast) {
        foreach (var (action, spell) in cast) {
            _castLog.Add(new CastRecord(BattleRound, mover, mover.OccupiedTeam, action.m_spell.m_templateID, spell,
                [.. (action.m_targetSubcircleList ?? []).Select(t => (int) t)], action.m_spellHits == (char) 2, action.m_spellHits == (char) 0,
                action.m_spell.m_enchantment != 0));
        }

        // A long battle keeps its last few hundred.
        if (_castLog.Count > 600) {
            _castLog.RemoveRange(0, _castLog.Count - 500);
        }
    }

    private bool CheatTurnedOff(CombatDuelSubCircle boss, int index) => _cheatsTurnedOff.Contains(CheatKey(boss, index));

    private bool IsSilenced(CombatDuelSubCircle who) => _silencedUntil.TryGetValue(who, out var until) && BattleRound <= until;

    // ---- Variables ----

    // The battle's variables are kept under an owner no one has.
    private const ulong BATTLE_VARS = ulong.MaxValue;

    private static (ulong, string) VarKey(CombatDuelSubCircle owner, string name, string scope)
        => ((scope ?? "").Trim().Equals("Battle", StringComparison.OrdinalIgnoreCase) ? BATTLE_VARS : owner?.ParticipantObject?.m_globalID.Full ?? 0,
            (name ?? "").Trim().ToLowerInvariant());

    private int GetVar(CombatDuelSubCircle owner, string name, string scope) => _cheatVars.GetValueOrDefault(VarKey(owner, name, scope));

    private static readonly Regex s_varWord = new(@"\{([A-Za-z_][A-Za-z0-9_ ]*)\}", RegexOptions.Compiled);

    // A message's {name}: the owner's variable of that name, else the battle's; any other word stays as it is.
    private string FillVars(string text, CombatDuelSubCircle owner) => s_varWord.Replace(text, m => {
        var name = m.Groups[1].Value.Trim().ToLowerInvariant();
        var own = (owner?.ParticipantObject?.m_globalID.Full ?? 0, name);

        return _cheatVars.TryGetValue(own, out var mine) ? mine.ToString(CultureInfo.InvariantCulture)
            : _cheatVars.TryGetValue((BATTLE_VARS, name), out var battle) ? battle.ToString(CultureInfo.InvariantCulture) : m.Value;
    });

    private static bool Compare(double value, string op, double than) => (op ?? "").Trim() switch {
        "<" => value < than,
        "<=" => value <= than,
        ">" => value > than,
        "=" or "==" => Math.Abs(value - than) < 0.0001,
        "!=" or "<>" => Math.Abs(value - than) >= 0.0001,
        _ => value >= than,
    };

    private bool VarHolds(CombatDuelSubCircle boss, BossCheats.CheatAction step)
        => Compare(GetVar(boss, step.Var, step.Scope), step.Op, step.Amount);

    // ---- Conditions ----

    private bool ConditionsAllow(CombatDuelSubCircle boss, int index, BossCheats.BossCheat cheat, CombatDuelSubCircle trigger,
                                 CombatDuelSubCircle receiver) {
        if (cheat.Conditions is not { Count: > 0 } conditions) {
            return true;
        }

        var groups = new Dictionary<int, bool>();
        foreach (var condition in conditions.Where(c => c is not null && !string.IsNullOrWhiteSpace(c.If))) {
            bool holds;
            try {
                holds = Holds(boss, index, condition, trigger, receiver) != condition.Not;
            }
            catch (Exception e) {
                Logger.Warning("Duel {0} | Cheat If \"{1}\" failed: {2}", Logger.Args(_duel.m_duelID.Full, condition.If, e.Message));
                holds = false;
            }

            if (condition.Any <= 0) {
                if (!holds) {
                    return false;
                }
            }
            else {
                groups[condition.Any] = groups.GetValueOrDefault(condition.Any) || holds;
            }
        }

        return groups.Values.All(g => g);
    }

    // Who a condition looks at, and how: 'a' someone of them, 'e' every one of them, 's' the side added together.
    private (List<CombatDuelSubCircle> Who, char Mode) ConditionWho(CombatDuelSubCircle boss, string who, CombatDuelSubCircle trigger,
                                                                  CombatDuelSubCircle receiver) {
        var standing = _subCircles.Where(c => c is not null && c.Occupied && c.AddedToDuel && c.IsAlive && c.ParticipantGameStats is not null).ToList();
        var enemies = standing.Where(c => c.OccupiedTeam != boss.OccupiedTeam).ToList();
        var allies = standing.Where(c => c.OccupiedTeam == boss.OccupiedTeam).ToList();
        var code = (who ?? "").Trim().ToLowerInvariant();

        return code switch {
            "" or "self" or "me" => ([boss], 'a'),
            "anyenemy" => (enemies, 'a'),
            "anyally" => (allies, 'a'),
            "anyone" => (standing, 'a'),
            "everyenemy" or "allenemies" => (enemies, 'e'),
            "everyally" or "allallies" => (allies, 'e'),
            "everyone" => (standing, 'e'),
            // Niduus, 2026-10-27: every wizard (ALL PLAYERS), without whoever's cheat it is, or any one of them.
            "allplayers" => ([.. standing.Where(c => c.IsHumanPlayer)], 'e'),
            "allplayersbutself" => ([.. standing.Where(c => c.IsHumanPlayer && c != boss && c != _cheatOwner)], 'e'),
            "anyplayer" => ([.. standing.Where(c => c.IsHumanPlayer)], 'a'),
            "enemyside" => (enemies, 's'),
            "allyside" => (allies, 's'),
            "bothsides" => (standing, 's'),
            "caster" or "attacker" => (trigger is null ? [] : [trigger], 'a'),
            "receiver" => (receiver is null ? [] : [receiver], 'a'),
            _ when FreeTargetCircle(boss, code) is { } circle => (circle.Occupied && circle.IsAlive ? [circle] : [], 'a'),
            _ => (TargetsFor(code, boss, null, trigger, receiver, enemies, allies, [.. allies.Where(c => c != boss)]), 'a'),
        };
    }

    // A number of each one (or of the side together: summed, or averaged for a share) compared with the condition's Value.
    private bool Measured(CombatDuelSubCircle boss, BossCheats.CheatCondition c, CombatDuelSubCircle trigger, CombatDuelSubCircle receiver,
                          Func<CombatDuelSubCircle, double> measure, bool average = false) {
        var (who, mode) = ConditionWho(boss, c.Who, trigger, receiver);
        if (mode == 's') {
            var values = who.Select(measure).ToList();
            var total = values.Count == 0 ? 0 : average ? values.Average() : values.Sum();

            return Compare(total, c.Op, c.Value);
        }

        return who.Count > 0 && (mode == 'e' ? who.All(w => Compare(measure(w), c.Op, c.Value)) : Found(who, w => Compare(measure(w), c.Op, c.Value)));
    }

    // Someone of them it holds for: that one is the target Matched ("the one the If found") of the cheat's steps.
    private bool Found(List<CombatDuelSubCircle> who, Func<CombatDuelSubCircle, bool> test) {
        var found = who.FirstOrDefault(test);
        if (found is not null) {
            _hasMatch = found;
        }

        return found is not null;
    }

    // A school's rival (its opposite): Fire and Ice, Storm and Myth, Life and Death. Balance and the astral and shadow
    // schools have none.
    private static MagicSchool RivalOf(MagicSchool school) => school switch {
        MagicSchool.Fire => MagicSchool.Ice, MagicSchool.Ice => MagicSchool.Fire,
        MagicSchool.Storm => MagicSchool.Myth, MagicSchool.Myth => MagicSchool.Storm,
        MagicSchool.Life => MagicSchool.Death, MagicSchool.Death => MagicSchool.Life,
        _ => MagicSchool.None,
    };

    private bool IsTrueOf(CombatDuelSubCircle boss, BossCheats.CheatCondition c, CombatDuelSubCircle trigger, CombatDuelSubCircle receiver,
                          Func<CombatDuelSubCircle, bool> test) {
        var (who, mode) = ConditionWho(boss, c.Who, trigger, receiver);

        return who.Count > 0 && (mode == 'a' ? Found(who, test) : who.All(test));
    }

    private int StartHealth(CombatDuelSubCircle c)
        => _roundStartHealth.TryGetValue(c, out var hp) ? hp : c.ParticipantGameStats.m_currentHitpoints;

    private double OfMost(CombatDuelSubCircle c, double amount, bool percent)
        => !percent ? amount : MostHealth(c) > 0 ? amount * 100.0 / MostHealth(c) : 0;

    private static int PipsOfKind(CombatDuelSubCircle c, string kind) {
        if (c.CombatParticipant?.m_pipCount is not { } pips) {
            return 0;
        }

        return (kind ?? "").Trim().ToLowerInvariant() switch {
            "normal" => pips.m_genericPips,
            "power" => pips.m_powerPips,
            "shadow" => pips.m_shadowPips,
            "fire" => pips.m_firePips,
            "ice" => pips.m_icePips,
            "storm" => pips.m_stormPips,
            "myth" => pips.m_mythPips,
            "life" => pips.m_lifePips,
            "death" => pips.m_deathPips,
            "balance" => pips.m_balancePips,
            _ => PipCount(c),
        };
    }

    // A fighter's name: the wizard's, a custom's, else the creature's as the game shows it.
    private static string FighterName(CombatDuelSubCircle c) {
        if (c is null) {
            return "";
        }

        if (c.IsHumanPlayer) {
            return c._wizard?.PlayerNameBehavior?.GetWizardName() ?? "";
        }

        if (Customs.NameOf(c.ParticipantObject?.m_globalID.Full ?? 0) is { Length: > 0 } custom) {
            return custom;
        }

        var template = CoreObjectFactory.GetCoreTemplate(c.ParticipantObject?.m_templateID.Full ?? 0) as GameObjectTemplate;
        string key = template?.m_displayName;
        string objectName = template?.m_objectName;

        return !string.IsNullOrEmpty(key) && Locale.GetEnglishName(key) is { Length: > 0 } shown ? shown : objectName ?? "";
    }

    private static ulong TemplateOf(CombatDuelSubCircle c) => c?.IsHumanPlayer == false ? c.ParticipantObject?.m_templateID.Full ?? 0 : 0;

    private static bool IsBossCreature(CombatDuelSubCircle c)
        => !c.IsHumanPlayer && (CoreObjectFactory.GetCoreTemplate(TemplateOf(c)) as GameObjectTemplate)?.m_behaviors?
            .OfType<NPCBehaviorTemplate>().Any(npc => npc.m_bossMob) == true;

    // Whether a spell is one of these names or IDs (commas between; any version of a name).
    private static bool SpellIsOneOf(string words, uint id, SpellTemplate spell) {
        if (string.IsNullOrWhiteSpace(words)) {
            return true;
        }

        string name = spell?.m_name;
        string key = spell?.m_displayName;

        return SpellNameFamily.IsOneOf(words.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), true,
            name, string.IsNullOrEmpty(key) ? "" : Locale.GetEnglishName(key), id.ToString(CultureInfo.InvariantCulture));
    }

    // Whether a spell of the log was cast by those the word names, as seen from the cheat's owner.
    private bool CastBy(CombatDuelSubCircle boss, CastRecord record, string who, CombatDuelSubCircle trigger, CombatDuelSubCircle receiver) {
        var code = (who ?? "").Trim().ToLowerInvariant();

        return code switch {
            "" or "anyone" or "everyone" or "bothsides" => true,
            "self" or "me" => record.Caster == boss,
            "allies" or "anyally" or "everyally" or "allyside" or "allallies" => record.Team == boss.OccupiedTeam,
            "otherallies" => record.Team == boss.OccupiedTeam && record.Caster != boss,
            "enemies" or "anyenemy" or "everyenemy" or "enemyside" or "allenemies" => record.Team != boss.OccupiedTeam,
            _ => ConditionWho(boss, who, trigger, receiver).Who.Contains(record.Caster),
        };
    }

    private bool ResultFits(CastRecord record, string result) => (result ?? "").Trim().ToLowerInvariant() switch {
        "landed" => !record.Fizzled,
        "crit" or "critical" => record.Crit,
        "fizzled" or "fizzle" => record.Fizzled,
        _ => true,
    };

    private static bool YesNo(string wanted, bool value) => (wanted ?? "").Trim().ToLowerInvariant() switch {
        "yes" or "true" => value,
        "no" or "false" => !value,
        _ => true,
    };

    private bool Holds(CombatDuelSubCircle boss, int index, BossCheats.CheatCondition c, CombatDuelSubCircle trigger, CombatDuelSubCircle receiver) {
        switch (c.If.Trim().ToLowerInvariant()) {
            case "health":
                return Measured(boss, c, trigger, receiver, w => c.Percent ? HealthShare(w) * 100 : w.ParticipantGameStats.m_currentHitpoints, average: c.Percent);
            case "healthlost":
                return Measured(boss, c, trigger, receiver, w => OfMost(w, Math.Max(0, StartHealth(w) - w.ParticipantGameStats.m_currentHitpoints), c.Percent),
                    average: c.Percent);
            case "healthgained":
                return Measured(boss, c, trigger, receiver, w => OfMost(w, Math.Max(0, w.ParticipantGameStats.m_currentHitpoints - StartHealth(w)), c.Percent),
                    average: c.Percent);
            case "standing": {
                var side = (c.Side ?? "").Trim().ToLowerInvariant();
                var count = _subCircles.Count(x => x is not null && x.Occupied && x.AddedToDuel && x.IsAlive
                                                   && (side == "all" || (side == "them") != (x.OccupiedTeam == boss.OccupiedTeam)));

                return Compare(count, c.Op, c.Value);
            }
            case "outnumbered": {
                var standing = _subCircles.Where(x => x is not null && x.Occupied && x.AddedToDuel && x.IsAlive).ToList();

                return standing.Count(x => x.OccupiedTeam == boss.OccupiedTeam) < standing.Count(x => x.OccupiedTeam != boss.OccupiedTeam);
            }
            case "round": {
                var every = Math.Max(1, c.Every);

                return BattleRound % every == ((c.Offset % every) + every) % every;
            }
            case "goesfirst":
                return _duel.m_firstTeamToAct == (int) boss.OccupiedTeam;
            case "fired":
                return Compare(_bossCheatCounts.GetValueOrDefault(CheatKey(boss, c.Cheat > 0 ? c.Cheat - 1 : index)).Times, c.Op, c.Value);
            case "casts": {
                var within = (c.Within ?? "").Trim().ToLowerInvariant();
                var on = (c.On ?? "").Trim().Length == 0 ? null : ConditionWho(boss, c.On, trigger, receiver).Who.Select(x => x.SlotIndex).ToHashSet();
                var count = _castLog.Count(r => (within != "round" || r.Round == BattleRound) && (within != "lastround" || r.Round == BattleRound - 1)
                                               && CastBy(boss, r, c.Who, trigger, receiver) && (on is null || r.Targets.Overlaps(on))
                                               && SpellIsOneOf(c.Spell, r.SpellId, r.Spell)
                                               && (c.School.Length == 0 || c.School.Equals((string) r.Spell.m_sMagicSchoolName, StringComparison.OrdinalIgnoreCase))
                                               && SpellNameFamily.CategoryMatches(c.Kind, CategoriesOf(r.Spell)) && ResultFits(r, c.Result));

                return (c.Op ?? "").Trim().Length == 0 && c.Value == 0 ? count >= 1 : Compare(count, c.Op, c.Value);
            }
            case "lastspell": {
                var last = _castLog.LastOrDefault(r => CastBy(boss, r, c.Who, trigger, receiver));

                return last is not null && SpellIsOneOf(c.Spell, last.SpellId, last.Spell)
                       && (c.School.Length == 0 || c.School.Equals((string) last.Spell.m_sMagicSchoolName, StringComparison.OrdinalIgnoreCase))
                       && SpellNameFamily.CategoryMatches(c.Kind, CategoriesOf(last.Spell))
                       && (c.CostMin <= 0 && c.CostMax == 0 || SpellNameFamily.CostMatches(CostOf(last.Spell), c.CostMin, c.CostMax))
                       && YesNo(c.Treasure, last.Spell.m_Treasure) && YesNo(c.Enchanted, last.Enchanted) && ResultFits(last, c.Result);
            }
            case "effects": {
                var kinds = (c.Kind ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                return Measured(boss, c, trigger, receiver, w => (w._hangingEffects ?? []).Count(e => e is not null
                    && (kinds.Length == 0 || kinds.Any(k => IsKind(e, k))) && FromSpell(e, c.Spell)));
            }
            case "pips":
                return Measured(boss, c, trigger, receiver, w => PipsOfKind(w, c.Kind));
            case "level":
                return Measured(boss, c, trigger, receiver, w => w.ParticipantGameStats.Level, average: true);
            case "school": {
                var match = (c.Match ?? "").Trim();
                if (match.Equals("RivalOfIt", StringComparison.OrdinalIgnoreCase)) {
                    var rival = RivalOf(boss.SchoolNow);

                    return rival != MagicSchool.None && IsTrueOf(boss, c, trigger, receiver, w => w.SchoolNow == rival);
                }

                if (match.Equals("Rival", StringComparison.OrdinalIgnoreCase)) {
                    var rival = Enum.TryParse<MagicSchool>(c.School.Trim(), true, out var picked) ? RivalOf(picked) : MagicSchool.None;

                    return rival != MagicSchool.None && IsTrueOf(boss, c, trigger, receiver, w => w.SchoolNow == rival);
                }

                return IsTrueOf(boss, c, trigger, receiver, w => c.School.Trim().Equals(w.SchoolNow.ToString(), StringComparison.OrdinalIgnoreCase));
            }
            case "name":
                return IsTrueOf(boss, c, trigger, receiver, w => c.Name.Trim().Length > 0 && FighterName(w).Contains(c.Name.Trim(), StringComparison.OrdinalIgnoreCase));
            case "creature":
                return IsTrueOf(boss, c, trigger, receiver, w => c.Creature != 0 && TemplateOf(w) == c.Creature);
            case "boss":
                return IsTrueOf(boss, c, trigger, receiver, IsBossCreature);
            case "player":
                return IsTrueOf(boss, c, trigger, receiver, w => w.IsHumanPlayer);
            case "inbattle": {
                var side = (c.Side ?? "").Trim().ToLowerInvariant();

                return _subCircles.Any(x => x is not null && x.Occupied && x.AddedToDuel && x.IsAlive
                                            && (side is not ("us" or "them") || (side == "them") != (x.OccupiedTeam == boss.OccupiedTeam))
                                            && (c.Creature == 0 || TemplateOf(x) == c.Creature)
                                            && (c.Name.Trim().Length == 0 || FighterName(x).Contains(c.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                                            && (c.Creature != 0 || c.Name.Trim().Length > 0));
            }
            case "roll" or "chance":
                return Random.Shared.Next(100) < c.Value;
            case "var":
                return Compare(GetVar(boss, c.Var, c.Scope), c.Op, c.Value);
            default:
                Logger.Warning("Duel {0} | \"{1}\" is not an If a cheat knows; it does not hold.", Logger.Args(_duel.m_duelID.Full, c.If));

                return false;
        }
    }

    // ---- Dos ----

    private bool TryCreativeCheatSteps(CombatDuelSubCircle boss, BossCheats.BossCheat cheat, BossCheats.CheatAction step,
                                       CombatDuelSubCircle trigger, CombatDuelSubCircle receiver, string bossName, List<string> lines,
                                       CombatActionListObj combatActionList, float cinematicTimeSoFar, bool custom, out float cinematicTime) {
        cinematicTime = 0;
        List<CombatDuelSubCircle> On(string fallback) => StepTargets(boss, step.Target.Length > 0 ? step.Target : fallback, null, trigger, receiver);
        var duel = _duel.m_duelID.Full;

        switch (step.Do.ToLowerInvariant()) {
            case "lockcircle" or "unlockcircle": {
                // Niduus, 2026-10-27 ("add a lock zones block so I can like lock the moon slot or gem in battle"): no one
                // new takes the circle (no wizard or creature joining, no minion summoned) for Rounds rounds (0: the rest
                // of the battle; from this one when the round is under way). Kind: the circle's symbol (Dagger, Key, Gem,
                // Spiral on the creature side; Sun, Eye, Star, Moon on the wizards'), OwnSide, OtherSide or All.
                var locking = step.Is("LockCircle");
                var circles = CirclesNamed(boss, step.Kind);
                foreach (var circle in circles) {
                    circle.LockedUntil = !locking ? 0
                        : step.Rounds <= 0 ? int.MaxValue : BattleRound + step.Rounds - (_cheatAtRoundStart ? 1 : 0);
                }

                Logger.Information("Duel {0} | {1}: {2} circle {3} ({4}){5}.", Logger.Args(duel, bossName, locking ? "locked" : "unlocked",
                    string.Join(", ", circles.Select(c => c.Symbol)), step.Kind,
                    locking ? step.Rounds <= 0 ? " for the rest of the battle" : $" for {step.Rounds} rounds" : ""));

                return true;
            }

            case "setvar": {
                if (string.IsNullOrWhiteSpace(step.Var)) {
                    return true;
                }

                var key = VarKey(boss, step.Var, step.Scope);
                var now = _cheatVars.GetValueOrDefault(key);
                var value = (step.Op ?? "").Trim().ToLowerInvariant() switch {
                    "add" or "+" => now + step.Amount,
                    "subtract" or "-" => now - step.Amount,
                    "multiply" or "*" => now * step.Amount,
                    "random" => Random.Shared.Next(Math.Min(0, step.Amount), Math.Max(0, step.Amount) + 1),
                    _ => step.Amount,
                };
                _cheatVars[key] = value;
                Logger.Information("Duel {0} | Slot {1} | {2}: {3} is now {4}.", Logger.Args(duel, boss.SlotIndex, bossName, step.Var.Trim(), value));

                return true;
            }
            case "maxhealth":
                foreach (var target in On("Self").Where(t => t?.ParticipantGameStats is not null && t.IsAlive)) {
                    var most = MostHealth(target);
                    var amount = step.Percent ? (int) Math.Round(most * step.Amount / 100.0) : step.Amount;
                    var want = Math.Max(1, (step.Op ?? "").Trim().Equals("Add", StringComparison.OrdinalIgnoreCase) ? most + amount : amount);
                    if (!target.IsHumanPlayer) {
                        target.ParticipantGameStats.m_baseHitpoints = Math.Max(1, want - target.ParticipantGameStats.m_bonusHitpoints);
                    }

                    if (target.CombatParticipant is not null) {
                        target.CombatParticipant.m_maxPlayerHealth = want;
                    }

                    if (target.ParticipantGameStats.m_currentHitpoints > want) {
                        SetHealthTo(target, want);
                    }

                    Logger.Information("Duel {0} | Slot {1} | Cheat sets its most health to {2} (was {3}).", Logger.Args(duel, target.SlotIndex, want, most));
                }

                return true;
            case "clone":
                foreach (var target in On("Caster").Where(t => t is not null && t.IsAlive && !t.IsHumanPlayer && t.ParticipantObject is not null)) {
                    var key = Customs.KeyOf(target.ParticipantObject);
                    var copy = Customs.IsCustom(key)
                        ? step with { Creature = 0, Custom = Customs.IdOf(key) }
                        : step with { Creature = (uint) target.ParticipantObject.m_templateID.Full, Custom = 0 };
                    for (var i = 0; i < Math.Clamp(step.Count, 1, 3); i++) {
                        if (SpawnCheatCreature(boss, copy, asForm: false) is null) {
                            Logger.Information("Duel {0} | Slot {1} | Cheat clone of slot {2} found no free circle.", Logger.Args(duel, boss.SlotIndex, target.SlotIndex));
                            break;
                        }
                    }
                }

                return true;
            case "copyspell": {
                if (_cheatSpellId == 0) {
                    Logger.Information("Duel {0} | {1}: CopySpell has no spell to copy (it answers a spell).", Logger.Args(duel, bossName));

                    return true;
                }

                var copy = step with { Do = "Cast", Spell = "", SpellID = _cheatSpellId };
                cinematicTime = CheatCast(boss, cheat, copy, trigger, receiver, bossName, lines, combatActionList, cinematicTimeSoFar, custom);

                return true;
            }
            case "redirect": {
                _interruptNow = true;
                if (_cheatSpellId == 0 || trigger is null || !trigger.IsAlive) {
                    Logger.Information("Duel {0} | {1}: Redirect has no spell to send on (it answers a spell).", Logger.Args(duel, bossName));

                    return true;
                }

                var spellId = _cheatSpellId;
                var to = On("Enemy").FirstOrDefault(t => t is not null && t.IsAlive);
                var code = to is null ? null : CodeOf(trigger, to);
                if (code is null || AddFreeDelayedCast(trigger, spellId, true, out _, code, false, NOW_QUEUE, cheat: true) == 0) {
                    return true;
                }

                cinematicTime = CastDelayed(trigger, atStart: true, combatActionList, cinematicTimeSoFar, NOW_QUEUE);
                _delayedCasts.RemoveAll(d => d.When == NOW_QUEUE && d.Action.SpellCaster == trigger);
                Logger.Information("Duel {0} | Slot {1} | {2} redirects slot {3}'s spell onto slot {4}.",
                    Logger.Args(duel, boss.SlotIndex, bossName, trigger.SlotIndex, to.SlotIndex));

                return true;
            }
            case "stealeffects": {
                var kinds = (step.Kind ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (boss._hangingEffects is null) {
                    return true;
                }

                foreach (var target in On("Caster").Where(t => t?._hangingEffects is not null && t != boss)) {
                    var taken = target._hangingEffects.Where(e => e is not null && !e.m_protected && !CombatRequirements.IsOverTime(e)
                                                                  && (kinds.Length == 0 ? s_charmEffects.Contains(e.m_effectType) || s_wardEffects.Contains(e.m_effectType)
                                                                                           || CombatRequirements.IsAura(e)
                                                                      : kinds.Any(k => IsKind(e, k))))
                        .Reverse().Take(step.Count > 0 ? step.Count : int.MaxValue).ToList();
                    target._hangingEffects.RemoveAll(taken.Contains);
                    boss._hangingEffects.AddRange(taken.Select(e => e with { }));
                    _clearedCount += taken.Count;
                    Logger.Information("Duel {0} | Slot {1} | {2} steals {3} effects from slot {4}.",
                        Logger.Args(duel, boss.SlotIndex, bossName, taken.Count, target.SlotIndex));
                }

                return true;
            }
            case "runcheat": {
                var cheats = BossesWithCheats(defeatedToo: true).FirstOrDefault(b => b.Boss == boss).Cheats ?? [];
                var at = step.Cheat - 1;
                if (at < 0 || at >= cheats.Count || cheats[at] == cheat || _runCheatDepth >= 4) {
                    Logger.Information("Duel {0} | {1}: RunCheat {2} is not one it can run here.", Logger.Args(duel, bossName, step.Cheat));

                    return true;
                }

                _runCheatDepth++;
                try {
                    cinematicTime = step.Check
                        ? TryBossCheat(boss, at, cheats[at], trigger, receiver, $"cheat run by another", combatActionList, cinematicTimeSoFar)
                        : RunCheatSteps(boss, cheats[at], PickSteps(cheats[at].Steps(), _bossCheatCounts.GetValueOrDefault(CheatKey(boss, at)).Times),
                            trigger, receiver, bossName, custom, combatActionList, cinematicTimeSoFar);
                }
                finally {
                    _runCheatDepth--;
                }

                return true;
            }
            case "cheatonoff": {
                var key = CheatKey(boss, Math.Max(0, step.Cheat - 1));
                if ((step.Kind ?? "").Trim().Equals("On", StringComparison.OrdinalIgnoreCase)) {
                    _cheatsTurnedOff.Remove(key);
                }
                else {
                    _cheatsTurnedOff.Add(key);
                }

                Logger.Information("Duel {0} | Slot {1} | {2} turns its cheat {3} {4}.",
                    Logger.Args(duel, boss.SlotIndex, bossName, step.Cheat, _cheatsTurnedOff.Contains(key) ? "off" : "on"));

                return true;
            }
            case "silence":
                foreach (var target in On("Caster").Where(t => t is not null && t.IsAlive)) {
                    // A silence set while a round is under way counts that round as one of its rounds.
                    _silencedUntil[target] = BattleRound + Math.Max(1, step.Rounds) - (_cheatAtRoundStart ? 1 : 0);
                    Logger.Information("Duel {0} | Slot {1} | Cheat silences it up to round {2}.", Logger.Args(duel, target.SlotIndex, _silencedUntil[target]));
                }

                return true;
            case "addcard": {
                var (card, _) = FindSpell(step.Spell, step.SpellID);
                if (card is null) {
                    Logger.Warning("Duel {0} | {1}: AddCard found no spell \"{2}\".", Logger.Args(duel, bossName, step.Spell.Length > 0 ? step.Spell : step.SpellID));

                    return true;
                }

                foreach (var target in On("Self").Where(t => t is not null && t.IsAlive)) {
                    target.CheatHandRules.Add(new CombatDuelSubCircle.CheatHandRule("Add", Math.Clamp(step.Count, 1, 7), "", "", card.m_templateID, false));
                }

                return true;
            }
            case "discard":
                foreach (var target in On("Caster").Where(t => t is not null && t.IsAlive)) {
                    target.CheatHandRules.Add(new CombatDuelSubCircle.CheatHandRule("Discard", Math.Max(0, step.Count), step.Spell, step.Kind, 0, false));
                }

                return true;
            case "shufflehand":
                foreach (var target in On("Caster").Where(t => t is not null && t.IsAlive)) {
                    target.CheatHandRules.Add(new CombatDuelSubCircle.CheatHandRule("Shuffle", 0, "", "", 0, step.DeckToo));
                }

                return true;
            case "lockcards":
                if (string.IsNullOrWhiteSpace(step.Spell) && string.IsNullOrWhiteSpace(step.Kind)) {
                    Logger.Information("Duel {0} | {1}: LockCards names no spell or kind, so it locks nothing.", Logger.Args(duel, bossName));

                    return true;
                }

                foreach (var target in On("Caster").Where(t => t is not null && t.IsAlive)) {
                    target.CheatHandRules.Add(new CombatDuelSubCircle.CheatHandRule("Lock", 0, step.Spell, step.Kind, 0, false) { Hands = Math.Max(1, step.Rounds) });
                }

                return true;
            case "extraturn": {
                var (chosen, _) = step.Spell.Length > 0 || step.SpellID != 0 ? FindSpell(step.Spell, step.SpellID) : default;
                foreach (var target in On("Self").Where(t => t is not null && t.IsAlive)) {
                    // Rounds 1 is this round's end; each one more, one more round.
                    _extraTurns[target] = (BattleRound + Math.Max(1, step.Rounds) - 1, chosen?.m_templateID ?? 0, Math.Clamp(step.Times, 1, 3));
                    Logger.Information("Duel {0} | Slot {1} | Cheat gives it an extra turn up to round {2}.", Logger.Args(duel, target.SlotIndex, _extraTurns[target].Until));
                }

                return true;
            }
            case "pipgain": {
                var power = int.TryParse((step.Power ?? "").Trim().TrimEnd('%'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? Math.Clamp(p, 0, 100) : -1;
                foreach (var target in On("Self").Where(t => t is not null)) {
                    target.CheatPipGain = Math.Clamp(step.Amount, 0, 14);
                    target.CheatPowerPipChance = power;
                    // From the next round's pips: Rounds of them (0: the rest of the battle).
                    target.CheatPipUntil = step.Rounds <= 0 ? int.MaxValue : BattleRound + step.Rounds;
                    Logger.Information("Duel {0} | Slot {1} | Cheat: {2} pips a round{3}, up to round {4}.", Logger.Args(duel, target.SlotIndex,
                        target.CheatPipGain, power >= 0 ? $", {power}% power" : "", step.Rounds <= 0 ? "the end" : target.CheatPipUntil.ToString(CultureInfo.InvariantCulture)));
                }

                return true;
            }
            case "changeschool": {
                if (!Enum.TryParse<Imlight.CoreLib.Shared.Behaviors.MagicSchool>((step.School ?? "").Trim(), true, out var school)
                    || school == Imlight.CoreLib.Shared.Behaviors.MagicSchool.None) {
                    Logger.Warning("Duel {0} | {1}: ChangeSchool has no school (\"{2}\").", Logger.Args(duel, bossName, step.School));

                    return true;
                }

                foreach (var target in On("Self").Where(t => t is not null && t.IsAlive)) {
                    target.CheatSchool = school.ToString();
                    target.CheatSchoolStats = step.Stats;
                    // As Silence: a change made while the round is under way counts that round as one of its rounds.
                    target.CheatSchoolUntil = step.Rounds <= 0 ? int.MaxValue : BattleRound + step.Rounds - (_cheatAtRoundStart ? 1 : 0);
                    Logger.Information("Duel {0} | Slot {1} | Cheat: it counts as {2}{3} up to round {4}.", Logger.Args(duel, target.SlotIndex, school,
                        step.Stats ? " (school stats swapped)" : "", step.Rounds <= 0 ? "the end" : target.CheatSchoolUntil.ToString(CultureInfo.InvariantCulture)));
                }

                return true;
            }
            default:
                return false;
        }
    }

    // Extra turns: who, up to which round, the spell (0: the one it cast this round) and how many.
    private readonly Dictionary<CombatDuelSubCircle, (int Until, uint Spell, int Times)> _extraTurns = [];

    // At the end of the round (before the round's last free casts): each one with an extra turn casts again, for free.
    private void QueueExtraTurns() {
        foreach (var (who, turn) in _extraTurns.ToList()) {
            if (BattleRound > turn.Until || who is null || !who.IsAlive || !who.AddedToDuel) {
                _extraTurns.Remove(who);

                continue;
            }

            // Its own spell of this round, again at the one it went on (a spell on many goes as it aims).
            var mine = turn.Spell == 0 ? _castLog.LastOrDefault(r => r.Round == BattleRound && r.Caster == who) : null;
            var spell = turn.Spell != 0 ? turn.Spell : mine?.SpellId ?? 0;
            if (spell == 0) {
                Logger.Information("Duel {0} | Slot {1} | Extra turn: it cast nothing this round, so it has nothing to cast again.",
                    Logger.Args(_duel.m_duelID.Full, who.SlotIndex));

                continue;
            }

            var on = mine is { Targets.Count: 1 } ? _subCircles.FirstOrDefault(c => c is not null && c.SlotIndex == mine.Targets.First()) : null;
            var code = on is { IsAlive: true } ? CodeOf(who, on) : null;
            for (var i = 0; i < turn.Times; i++) {
                AddFreeDelayedCast(who, spell, true, out _, code, false, 'l', cheat: true);
            }

            Logger.Information("Duel {0} | Slot {1} | Extra turn: {2} again{3}, last in the round.", Logger.Args(_duel.m_duelID.Full, who.SlotIndex,
                spell, code is null ? "" : $" on {code}"));
        }
    }

    // The circles a LockCircle names: a symbol (or several, commas between), its own side's, the other side's, or all.
    private List<CombatDuelSubCircle> CirclesNamed(CombatDuelSubCircle boss, string names) {
        var all = _subCircles.Where(c => c is not null).ToList();
        var found = new List<CombatDuelSubCircle>();
        foreach (var name in (names ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            found.AddRange(name.ToLowerInvariant() switch {
                "all" => all,
                "ownside" => all.Where(c => c.SlotType == boss.SlotType),
                "otherside" => all.Where(c => c.SlotType != boss.SlotType),
                _ => all.Where(c => c.Symbol.Equals(name, StringComparison.OrdinalIgnoreCase)),
            });
        }

        return [.. found.Distinct()];
    }

}
