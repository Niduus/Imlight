/*
 * More boss cheats (Niduus, 2026-10-22: "Get more expansive with cheats, think out of the box"). The When and Do kinds
 * added to the ones of CombatResolver.BossCheats.cs; each is listed at the top of BossCheats.cs.
 *
 * Whens: PlayerFlees (a player ran from the battle, answered at the start of the next round), PlayerDefeated (a player
 * falls) and PlayerHealthBelow (a player's health first drops under Percent of their most, once each player).
 * Dos: Reflect, StealPips, RemoveAuras, AddCharm, Boost, Immune, Stun, HealthTo, SwapHealth and PickOne.
 *
 * For players and creatures alike (Niduus, 2026-10-23: "ALL CHEATS are to be compatible for PLAYERS and CREATURES"):
 * Whens EnemyJoins (someone joins the other side after round Round) and AllyHealthBelow (someone on its own side first
 * drops under Percent, or Health); the If on the creature the When is about (Creature, NotCreature); the Do Wait (the
 * steps after it go off at the start of a round Rounds rounds later); the targets Summoner, LastAttacker, WeakestAlly
 * and StrongestAlly.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Tools;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Combat;

public partial class CombatResolver {

    /// <summary>A player fled the battle (CombatDuelComponent): PlayerFlees cheats answer it at the start of the next round.</summary>
    internal void NotePlayerFled(CombatDuelSubCircle player) {
        try {
            if (player is not null && player.IsHumanPlayer) {
                _fledPlayers.Add((player.SlotIndex, player.OccupiedTeam));
            }
        }
        catch (Exception e) {
            Logger.Error("Duel {0} | Noting a flee for the boss cheats failed: {1}", Logger.Args(_duel.m_duelID.Full, e));
        }
    }

    private float FleeCheats(CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        if (_fledPlayers.Count == 0) {
            return 0;
        }

        var fled = _fledPlayers.ToList();
        _fledPlayers.Clear();
        var cinematicTime = 0.0f;
        foreach (var (slot, team) in fled) {
            foreach (var (boss, cheats) in BossesWithCheats().Where(b => b.Boss.OccupiedTeam != team).ToList()) {
                for (var i = 0; i < cheats.Count; i++) {
                    if (cheats[i].Is("PlayerFlees") && InPhase(boss, cheats[i])) {
                        cinematicTime += TryBossCheat(boss, i, cheats[i], null, null, $"the player in slot {slot} fleeing",
                            combatActionList, cinematicTimeSoFar + cinematicTime);
                    }
                }
            }
        }

        return cinematicTime;
    }

    // PlayerHealthBelow (someone on the other side) and AllyHealthBelow (someone else on its own side): their health first
    // drops under Percent, or Health hit points, once for each of them. "The attacker" is that one. (A player falling,
    // PlayerDefeated, is answered with MinionDefeated, MinionDefeatCheats.)
    private float PlayerStateCheats(CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        var cinematicTime = 0.0f;
        var standing = _subCircles.Where(c => c is not null && c.Occupied && c.AddedToDuel && c.IsAlive && c.ParticipantObject is not null)
            .ToList();
        foreach (var (boss, cheats) in BossesWithCheats().ToList()) {
            for (var i = 0; i < cheats.Count; i++) {
                var cheat = cheats[i];
                var ally = cheat.Is("AllyHealthBelow");
                if ((!ally && !cheat.Is("PlayerHealthBelow")) || !InPhase(boss, cheat)) {
                    continue;
                }

                foreach (var other in standing.Where(c => c != boss && (ally ? c.OccupiedTeam == boss.OccupiedTeam : IsFoe(boss, c)))) {
                    var most = MostHealth(other);
                    var health = other.ParticipantGameStats.m_currentHitpoints;
                    var under = cheat.Health > 0 ? health < cheat.Health : most > 0 && health * 100.0 / most < cheat.Percent;
                    if (!under || !_playerHealthSeen.Add((boss, _bossCheatTemplates.GetValueOrDefault(boss), i, other.ParticipantObject.m_globalID.Full))) {
                        continue;
                    }

                    cinematicTime += TryBossCheat(boss, i, cheat, other, other,
                        $"the health of {(ally ? "its ally" : "the player")} in slot {other.SlotIndex} dropping to {health}",
                        combatActionList, cinematicTimeSoFar + cinematicTime);
                }
            }
        }

        return cinematicTime;
    }

    // A free cast "first in the round" (FreeCast Timing First) of a cheat going off as the round starts, and one "before
    // my card" of a cheat on its own card (ICast): both are cast at once, as their queue has already been cast.
    private bool _cheatAtRoundStart;
    private bool _cheatOwnCard;

    // The last one on the other side whose spell hurt each participant (the target LastAttacker).
    private readonly Dictionary<CombatDuelSubCircle, CombatDuelSubCircle> _lastAttackers = [];

    // Each participant's first round in the battle, by its global id (EnemyJoins); the round the count began.
    private readonly Dictionary<ulong, int> _joinRounds = [];
    private int _joinFirstRound;

    /// <summary>The steps after a Wait step of a cheat, kept for the start of a later round.</summary>
    private sealed record WaitingCheat(CombatDuelSubCircle Owner, BossCheats.BossCheat Cheat, List<BossCheats.CheatAction> Steps, int Round,
                                       CombatDuelSubCircle Trigger, CombatDuelSubCircle Receiver, string Name, bool Custom);

    private readonly List<WaitingCheat> _waitingCheats = [];

    // The share of its most health a participant has (1 when it has no most).
    private static double HealthShare(CombatDuelSubCircle c)
        => MostHealth(c) > 0 ? c.ParticipantGameStats.m_currentHitpoints / (double) MostHealth(c) : 1;

    // Who summoned a minion into the battle (its CombatParticipant's m_minionSubCircle), or null.
    private CombatDuelSubCircle Summoner(CombatDuelSubCircle minion)
        => minion.IsSummonedMinion && minion.CombatParticipant is { } participant
            ? _subCircles.FirstOrDefault(c => c is not null && c != minion && c.Occupied && c.IsAlive && c.SlotIndex == participant.m_minionSubCircle)
            : null;

    // The If "only if it is this creature": the one the When is about (the one that fell for MinionDefeated and
    // PlayerDefeated, else the one whose move, health or coming in set it off) is of the template Creature, or with
    // NotCreature is any other one.
    private static bool CreatureAllows(BossCheats.BossCheat cheat, CombatDuelSubCircle trigger, CombatDuelSubCircle receiver) {
        if (cheat.Creature == 0) {
            return true;
        }

        // CreatureWho "Receiver": for a spell, the one it was aimed at (the one hit), not its caster.
        var who = cheat.Is("MinionDefeated") || cheat.Is("PlayerDefeated") || cheat.CreatureWho.Equals("Receiver", StringComparison.OrdinalIgnoreCase)
            ? receiver : trigger ?? receiver;
        var match = who?.ParticipantObject is { } obj && obj.m_templateID == cheat.Creature;

        return match != cheat.NotCreature;
    }

    // Each spell of the mover that landed and hurt: the mover is now the last attacker of whoever it hit on the other side.
    private void NoteAttackers(CombatDuelSubCircle mover, List<(CombatAction Action, SpellTemplate Spell)> landed) {
        foreach (var (action, spell) in landed.Where(m => Flatten(m.Spell).Any(e => s_damageEffects.Contains(e.m_effectType)))) {
            foreach (var slot in action.m_targetSubcircleList ?? []) {
                if (_subCircles.FirstOrDefault(c => c is not null && c.Occupied && c.SlotIndex == slot) is { } hit
                    && hit.OccupiedTeam != mover.OccupiedTeam) {
                    _lastAttackers[hit] = mover;
                }
            }
        }
    }

    // The steps after a Wait step, at the start of the round they waited for, if whoever's cheat it is still stands.
    private float WaitedCheats(CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        var round = (int) _duel.m_roundNum;
        var due = _waitingCheats.Where(w => w.Round <= round).ToList();
        if (due.Count == 0) {
            return 0;
        }

        _waitingCheats.RemoveAll(due.Contains);
        var cinematicTime = 0.0f;
        foreach (var waited in due) {
            if (!waited.Owner.IsAlive || !waited.Owner.AddedToDuel || BattleDecided()) {
                Logger.Information("Duel {0} | Slot {1} | {2}: the steps it waited for are dropped (it is no longer standing).",
                    Logger.Args(_duel.m_duelID.Full, waited.Owner.SlotIndex, waited.Name));

                continue;
            }

            Logger.Information("{0} | Duel {1} | {2} (slot {3}) goes on after waiting, round {4}: {5}.",
                Logger.Args(waited.Custom ? "Custom cheat" : "Boss cheat", _duel.m_duelID.Full, waited.Name, waited.Owner.SlotIndex, round,
                    string.Join(", ", waited.Steps.Select(StepText))));
            cinematicTime += RunCheatSteps(waited.Owner, waited.Cheat, waited.Steps, waited.Trigger, waited.Receiver, waited.Name, waited.Custom,
                combatActionList, cinematicTimeSoFar + cinematicTime);
        }

        return cinematicTime;
    }

    // When "EnemyJoins": someone came into the battle on the other side, and its first round is later than Round of the
    // battle (Round 2: from round 3 on). For a creature's cheats that is a wizard; Minions: a summoned minion counts too.
    // "The attacker" and "whoever that spell was aimed at" are the newcomer. Checked at the start of each round.
    private float JoinCheats(CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        var round = (int) _duel.m_roundNum;
        var first = _joinRounds.Count == 0;
        if (first) {
            _joinFirstRound = round;
        }

        var newcomers = _subCircles.Where(c => c is not null && c.Occupied && c.AddedToDuel && c.IsAlive && c.ParticipantObject is not null
                                               && _joinRounds.TryAdd(c.ParticipantObject.m_globalID.Full, round))
            .ToList();
        if (first || newcomers.Count == 0) {
            return 0;
        }

        var battleRound = round - _joinFirstRound + 1;
        var cinematicTime = 0.0f;
        foreach (var newcomer in newcomers) {
            Logger.Information("Duel {0} | Slot {1} | Came into the battle in round {2}.",
                Logger.Args(_duel.m_duelID.Full, newcomer.SlotIndex, battleRound));
            foreach (var (boss, cheats) in BossesWithCheats().Where(b => b.Boss != newcomer).ToList()) {
                for (var i = 0; i < cheats.Count; i++) {
                    var cheat = cheats[i];
                    if (!cheat.Is("EnemyJoins") || !InPhase(boss, cheat) || battleRound <= cheat.Round
                        || newcomer.OccupiedTeam == boss.OccupiedTeam
                        || (!cheat.Minions && (newcomer.IsSummonedMinion || !IsFoe(boss, newcomer)))) {
                        continue;
                    }

                    cinematicTime += TryBossCheat(boss, i, cheat, newcomer, newcomer, $"slot {newcomer.SlotIndex} joining in round {battleRound}",
                        combatActionList, cinematicTimeSoFar + cinematicTime);
                }
            }
        }

        return cinematicTime;
    }

    /// <summary>
    /// PickOne: of the choices after it (Count of them, or all the rest when 0), only one is kept, picked at random by
    /// Weights ("2, 1, 1": the first twice as likely as each other one; 1 each when left out). InTurn (Niduus,
    /// 2026-10-24: Dewey's blade cycle): the choices go in turn, the first the first time the cheat goes off, the
    /// second the next time, and round again. Each choice is Group steps (1 if 0), such as a line and its spell.
    /// </summary>
    /// <param name="turn">How many times the cheat went off before.</param>
    private static List<BossCheats.CheatAction> PickSteps(List<BossCheats.CheatAction> steps, int turn = 0) {
        if (!steps.Any(s => s.Is("PickOne") || s.Is("InTurn"))) {
            return steps;
        }

        var kept = new List<BossCheats.CheatAction>();
        for (var i = 0; i < steps.Count; i++) {
            if (!steps[i].Is("PickOne") && !steps[i].Is("InTurn")) {
                kept.Add(steps[i]);

                continue;
            }

            var pick = steps[i];
            var size = Math.Max(1, pick.Group);
            var rest = steps.Count - i - 1;
            var choices = pick.Count > 0 ? Math.Min(pick.Count, (rest + size - 1) / size) : (rest + size - 1) / size;
            var taken = Math.Min(rest, choices * size);
            var group = Enumerable.Range(0, choices).Select(g => steps.Skip(i + 1 + g * size).Take(Math.Min(size, taken - g * size)).ToList()).ToList();
            i += taken;
            if (group.Count == 0) {
                continue;
            }

            if (pick.Is("InTurn")) {
                kept.AddRange(group[Math.Max(0, turn) % group.Count]);

                continue;
            }

            var weights = (pick.Weights ?? "").Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)
                .Select(w => int.TryParse(w, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? Math.Max(0, n) : 1)
                .ToList();
            var chances = group.Select((_, g) => g < weights.Count ? weights[g] : 1).ToList();
            var total = chances.Sum();
            if (total <= 0) {
                continue;
            }

            var roll = Random.Shared.Next(total);
            for (var g = 0; g < group.Count; g++) {
                if (roll < chances[g]) {
                    kept.AddRange(group[g]);
                    break;
                }

                roll -= chances[g];
            }
        }

        return kept;
    }

    // The targets of the step before (the target "Last"), and the mark the running step means ("Marked").
    private List<CombatDuelSubCircle> _lastStepTargets = [];

    // How many effects the removal steps of the cheat now going off took off so far (for IfCleared).
    private int _clearedCount;
    private string _stepMark = "";

    // The marks on each participant, by its global id (Mark, Unmark, the If "marked"; Niduus, 2026-10-24: Shadowbones's
    // "Tag, you're it").
    private readonly Dictionary<ulong, HashSet<string>> _marks = [];

    private bool HasMark(CombatDuelSubCircle who, string mark)
        => who?.ParticipantObject is { } obj && !string.IsNullOrWhiteSpace(mark) && _marks.TryGetValue(obj.m_globalID.Full, out var marks)
           && marks.Contains(mark.Trim());

    private void SetMark(CombatDuelSubCircle who, string mark, bool on) {
        if (who?.ParticipantObject is not { } obj || string.IsNullOrWhiteSpace(mark)) {
            return;
        }

        if (!_marks.TryGetValue(obj.m_globalID.Full, out var marks)) {
            _marks[obj.m_globalID.Full] = marks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        if (on ? marks.Add(mark.Trim()) : marks.Remove(mark.Trim())) {
            Logger.Information("Duel {0} | Slot {1} | Cheat {2} the mark \"{3}\".",
                Logger.Args(_duel.m_duelID.Full, who.SlotIndex, on ? "puts on" : "takes off", mark.Trim()));
        }
    }

    // The one an If about someone looks at: Caster (the one who set it off), Receiver (whoever that spell was aimed at,
    // or the one hit) or Self.
    private static CombatDuelSubCircle WhoOf(string who, CombatDuelSubCircle boss, CombatDuelSubCircle trigger, CombatDuelSubCircle receiver)
        => (who ?? "").Trim().ToLowerInvariant() switch {
            "self" or "me" => boss,
            "receiver" or "hit" => receiver ?? trigger,
            _ => trigger ?? receiver,
        };

    // The If "marked": MarkWho has (or with NotMarked, has not) the mark. AnyEnemy: someone on the other side has it
    // (NotMarked: no one there has it).
    private bool MarkAllows(CombatDuelSubCircle boss, BossCheats.BossCheat cheat, CombatDuelSubCircle trigger, CombatDuelSubCircle receiver) {
        if (string.IsNullOrWhiteSpace(cheat.Mark)) {
            return true;
        }

        // Every enemy (CombatResolver.MoreCheats.cs).
        if (AllMarked(boss, cheat) is { } all) {
            return all != cheat.NotMarked;
        }

        var has = cheat.MarkWho.Equals("AnyEnemy", StringComparison.OrdinalIgnoreCase)
            ? _subCircles.Any(c => c is not null && c.Occupied && c.AddedToDuel && c.IsAlive && c.OccupiedTeam != boss.OccupiedTeam && HasMark(c, cheat.Mark))
            : HasMark(WhoOf(cheat.MarkWho, boss, trigger, receiver), cheat.Mark);

        return has != cheat.NotMarked;
    }

    // The If "has": HasWho has at least HasCount (one if 0) charms, wards, auras or over-times of HasKind, or from
    // HasSpell (with HasNot: fewer). AnyEnemy / AnyAlly: someone on the other side / its own side (itself too) has them
    // (with HasNot: no one there); the one found is the target "Matched" of its steps.
    private bool HasAllows(CombatDuelSubCircle boss, BossCheats.BossCheat cheat, CombatDuelSubCircle trigger, CombatDuelSubCircle receiver) {
        _hasMatch = null;
        if (string.IsNullOrWhiteSpace(cheat.HasKind) && string.IsNullOrWhiteSpace(cheat.HasSpell) && !cheat.HasNot) {
            return true;
        }

        var kinds = (cheat.HasKind ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var need = Math.Max(1, cheat.HasCount);
        bool Has(CombatDuelSubCircle who) => Count(who) >= need;

        int Count(CombatDuelSubCircle who) => (who?.CombatParticipant?.m_hangingEffects ?? [])
            .Count(e => e is not null && (kinds.Length == 0 || kinds.Any(k => IsKind(e, k)))
                        && (string.IsNullOrWhiteSpace(cheat.HasSpell) || FromSpell(e, cheat.HasSpell)));
        // Someone without it, or a whole side together (CombatResolver.MoreCheats.cs).
        if (MoreHasAllows(boss, cheat, Count, need) is { } more) {
            return more;
        }

        var whoText = (cheat.HasWho ?? "").Trim();
        var enemy = whoText.Equals("AnyEnemy", StringComparison.OrdinalIgnoreCase);
        if (enemy || whoText.Equals("AnyAlly", StringComparison.OrdinalIgnoreCase)) {
            var found = _subCircles.Where(c => c is not null && c.Occupied && c.AddedToDuel && c.IsAlive
                                               && (c.OccupiedTeam != boss.OccupiedTeam) == enemy && Has(c)).ToList();
            _hasMatch = found.Count > 0 ? found[Random.Shared.Next(found.Count)] : null;

            return (found.Count > 0) != cheat.HasNot;
        }

        return Has(WhoOf(cheat.HasWho, boss, trigger, receiver)) != cheat.HasNot;
    }

    // The one the If "has" found with AnyEnemy / AnyAlly, for the target "Matched".
    private CombatDuelSubCircle _hasMatch;
    private CombatDuelSubCircle _cheatMatched;

    // Whose cheat the running steps are (the target "Owner": for a CastBy, the one whose file it is, not the caster).
    private CombatDuelSubCircle _cheatOwner;

    // Whether a hanging effect is of a kind: Shield, Trap, Blade, Weakness, Charm (any blade, weakness or other charm),
    // Ward (any shield, trap or absorb), Aura, DoT, HoT or Dispel.
    private static bool IsKind(SpellEffect e, string kind) {
        var good = CombatRequirements.IsBeneficial(e);
        var overTime = CombatRequirements.IsOverTime(e);
        var aura = CombatRequirements.IsAura(e);
        var ward = s_wardEffects.Contains(e.m_effectType) && !aura;
        var charm = s_charmEffects.Contains(e.m_effectType) && !aura;
        var shieldLike = e.m_effectType is kSpellEffects.kModifyIncomingDamage or kSpellEffects.kModifyIncomingDamageFlat or kSpellEffects.kAbsorbDamage;
        var bladeLike = e.m_effectType is kSpellEffects.kModifyOutgoingDamage or kSpellEffects.kModifyOutgoingDamageFlat;

        return SpellNameFamily.KindSynonym(kind).ToLowerInvariant() switch {
            "shield" => ward && shieldLike && good,
            "trap" => ward && shieldLike && !good,
            "blade" => charm && bladeLike && good,
            "weakness" => charm && bladeLike && !good,
            "charm" => charm,
            "ward" => ward,
            "aura" => aura,
            "dot" => overTime && e.m_effectType != kSpellEffects.kHealOverTime,
            "hot" => e.m_effectType == kSpellEffects.kHealOverTime,
            // Niduus, 2026-10-26: a Dispel (Fire Dispel...) hanging on someone.
            "dispel" => e.m_effectType == kSpellEffects.kDispel,
            _ => false,
        };
    }

    // The Ifs on its side: AlliesUp each stand, none of AlliesDown does, and AlliesMin to AlliesMax others stand.
    private bool AlliesAllow(CombatDuelSubCircle boss, BossCheats.BossCheat cheat) {
        var others = _subCircles.Where(c => c is not null && c != boss && c.Occupied && c.AddedToDuel && c.IsAlive
                                            && c.OccupiedTeam == boss.OccupiedTeam && c.ParticipantObject is not null).ToList();
        if (cheat.AlliesMin > 0 && others.Count < cheat.AlliesMin) {
            return false;
        }

        if ((cheat.AlliesMax > 0 && others.Count > cheat.AlliesMax) || (cheat.AlliesMax < 0 && others.Count > 0)) {
            return false;
        }

        var standing = others.Select(c => (uint) c.ParticipantObject.m_templateID.Full).ToHashSet();

        return BossCheats.Ids(cheat.AlliesUp).All(standing.Contains) && !BossCheats.Ids(cheat.AlliesDown).Any(standing.Contains);
    }

    // Who casts a CastBy step: someone on its side of Creature's template (By Creature, or no By with a Creature), else
    // as By says (Ally, WeakestAlly, StrongestAlly, Summoner, or Self). Null when no one there stands.
    private CombatDuelSubCircle CheatCaster(CombatDuelSubCircle boss, BossCheats.CheatAction step) {
        var by = (step.By ?? "").Trim();
        if (step.Creature > 0 && (by.Length == 0 || by.Equals("Creature", StringComparison.OrdinalIgnoreCase))) {
            return _subCircles.FirstOrDefault(c => c is not null && c.Occupied && c.AddedToDuel && c.IsAlive && c.OccupiedTeam == boss.OccupiedTeam
                                                   && c.ParticipantObject is { } obj && obj.m_templateID.Full == step.Creature);
        }

        if (by.Length == 0 || by.Equals("Self", StringComparison.OrdinalIgnoreCase)) {
            return boss;
        }

        var hasOthers = HasAllies(boss);
        if (!hasOthers && by.ToLowerInvariant() is "ally" or "weakestally" or "strongestally") {
            return null;
        }

        var saved = _lastStepTargets;
        var who = StepTargets(boss, by, null, null, null).FirstOrDefault(c => c is not null && c.IsAlive);
        _lastStepTargets = saved;

        return who;
    }

    // A SetPhase back to a phase: its health cheats can go off again.
    private void RearmHealthCheats(CombatDuelSubCircle boss, int phase) {
        var cheats = BossesWithCheats(defeatedToo: true).FirstOrDefault(b => b.Boss == boss).Cheats ?? [];
        for (var i = 0; i < cheats.Count; i++) {
            if (cheats[i].Phase == phase && (cheats[i].Is("HealthBelow") || cheats[i].Is("MyHealthBelow"))) {
                _bossCheatsUsed.Remove(CheatKey(boss, i));
            }
        }
    }

    private static string SchoolText(string school) => string.IsNullOrWhiteSpace(school) || school.Equals("All", StringComparison.OrdinalIgnoreCase)
        ? "" : $"{school} ";

    private static string RoundsText(int rounds) => rounds <= 0 ? "the rest of the battle" : rounds == 1 ? "1 round" : $"{rounds} rounds";

    // An aura-like effect with rounds lasts that long and is never used up; 0 rounds is the rest of the battle.
    private const int REST_OF_BATTLE = 999;

    private static SpellEffect CheatEffect(kSpellEffects type, int amount, string school, int rounds, bool good) => new() {
        m_effectType = type,
        m_effectParam = amount,
        m_sDamageType = string.IsNullOrWhiteSpace(school) ? "All" : school.Trim(),
        m_disposition = good ? kHangingDisposition.kBeneficial : kHangingDisposition.kHarmful,
        m_effectTarget = kEffectTarget.kSelf,
        m_numRounds = rounds,
        m_paramPerRound = type == kSpellEffects.kAbsorbDamage ? amount : 0,
    };

    // An Announce step's words with {boss} (the cheat's owner: the boss, or the wizard of a player cheat), {player} (the
    // wizard who set it off, else the owner if a wizard, else the first wizard in the battle) and {round} filled in.
    private string AnnounceText(string text, CombatDuelSubCircle owner, CombatDuelSubCircle trigger) {
        text = (text ?? "").Trim();
        if (text.Length == 0 || !text.Contains('{')) {
            return text;
        }

        // A cheat variable's name in braces is its value ({rage}: SetVar; Niduus, 2026-10-26).
        text = FillVars(text, owner);

        string NameOf(CombatDuelSubCircle c) => c is null ? ""
            : c.IsHumanPlayer ? c._wizard?.PlayerNameBehavior?.GetWizardName() ?? ""
            : Customs.NameOf(c.ParticipantObject?.m_globalID.Full ?? 0) ?? CheaterName(c);
        var player = trigger is { IsHumanPlayer: true } ? trigger
            : owner.IsHumanPlayer ? owner
            : _subCircles.FirstOrDefault(c => c is not null && c.Occupied && c.IsHumanPlayer);

        return text.Replace("{boss}", NameOf(owner), StringComparison.OrdinalIgnoreCase)
            .Replace("{player}", NameOf(player), StringComparison.OrdinalIgnoreCase)
            .Replace("{round}", _duel.m_roundNum.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    // The lookalike AddCharm casts (CheatLookalikes.cs): the one for its cast animation, else the plain one of its kind.
    private string LookalikeName(BossCheats.CheatAction step, bool onCaster, CombatDuelSubCircle boss, string bossName) {
        if (CheatLookalikes.WithCast(step.Kind, step.School, step.Look, onCaster) is { } look) {
            return look.Spell;
        }

        Logger.Information("Duel {0} | Slot {1} | {2}: the game has no {3} {4} to cast with that animation; the normal one is cast.",
            Logger.Args(_duel.m_duelID.Full, boss.SlotIndex, bossName, step.Look, CheatLookalikes.KindWord(step.Kind)));

        return CheatLookalikes.Plain(step.Kind, step.School)?.Spell ?? "";
    }

    /// <summary>
    /// AddCharm Global, HealGlobal or GloomGlobal: a battlefield bubble of Amount percent (more damage of School; more
    /// healing; less healing). As a real one, it takes the current bubble's place. A real bubble of that school (or the
    /// one for the cast animation, or "shown as") is cast so the game draws it, and the bubble it puts up is then given
    /// this size and school on the server.
    /// </summary>
    private float AddCustomGlobal(CombatDuelSubCircle boss, BossCheats.BossCheat cheat, BossCheats.CheatAction step, CombatDuelSubCircle trigger,
                                  CombatDuelSubCircle receiver, string bossName, List<string> lines, CombatActionListObj combatActionList,
                                  float cinematicTimeSoFar, bool custom) {
        var kind = (step.Kind ?? "").Trim().ToLowerInvariant();
        var amount = Math.Abs(step.Amount);
        var type = kind == "global" ? kSpellEffects.kModifyOutgoingDamage : kSpellEffects.kModifyOutgoingHeal;
        var param = kind == "gloomglobal" ? -amount : amount;
        var named = step.Spell.Length > 0 || step.SpellID != 0;
        var lookName = named ? step.Spell : LookalikeName(step, onCaster: true, boss, bossName);
        var look = lookName.Length > 0 || step.SpellID != 0 ? FindSpell(lookName, named ? step.SpellID : 0) : default;
        var bubbles = _duel.m_duelModifier.m_battlefieldEffects;
        var cinematicTime = 0f;
        var resized = 0;
        if (look.Template is not null) {
            var lookId = look.Spell.m_templateID;
            var had = bubbles.ToHashSet(ReferenceEqualityComparer.Instance);
            // A global aims at no one; the cast goes on the caster's own circle.
            var cast = step with { Do = "Cast", Spell = (string) look.Template.m_name, SpellID = lookId, Target = "Self" };
            cinematicTime = CheatCast(boss, cheat, cast, trigger, receiver, bossName, lines, combatActionList, cinematicTimeSoFar, custom);
            for (var i = 0; i < bubbles.Count; i++) {
                var e = bubbles[i];
                if (e is null || had.Contains(e) || e.m_spellTemplateID != lookId) {
                    continue;
                }

                // Its own size and school; it keeps the lookalike's id, so the next global takes its place as in the game.
                bubbles[i] = e with {
                    m_effectType = type,
                    m_effectParam = param,
                    m_sDamageType = kind == "global" ? (string.IsNullOrWhiteSpace(step.School) ? "All" : step.School.Trim())
                        : string.IsNullOrWhiteSpace(step.School) ? e.m_sDamageType : step.School.Trim(),
                    m_disposition = kHangingDisposition.kBoth,
                };
                OneSided(bubbles[i], boss);
                resized++;
            }

            Logger.Information("Duel {0} | Slot {1} | {2}: {3} cast so the game shows it; {4} of its bubble effects made {5}% {6}{7}.",
                Logger.Args(_duel.m_duelID.Full, boss.SlotIndex, bossName, (string) look.Template.m_name, resized, param,
                    SchoolText(step.School), CheatLookalikes.KindWord(step.Kind)));
        }

        if (resized == 0) {
            // Nothing drawn (no lookalike, or it was dispelled): the bubble goes up on the server only, in the old one's place.
            Logger.Information("Duel {0} | Slot {1} | {2}: no bubble was cast for it; the global goes on unseen.",
                Logger.Args(_duel.m_duelID.Full, boss.SlotIndex, bossName));
            bubbles.Clear();
            bubbles.Add(CheatEffect(type, param, kind == "global" ? step.School : step.School is { Length: > 0 } s ? s : "Life", 0, good: true) with {
                m_effectTarget = kEffectTarget.kGlobal, m_disposition = kHangingDisposition.kBoth,
                m_spellTemplateID = look.Spell?.m_templateID ?? 0,
            });
            OneSided(bubbles[^1], boss);
        }

        return cinematicTime;
    }

    // A player's cheat global helps only the players' side ("+666% to all ally spells"). The game draws the lookalike at its
    // own size, so its size reaching the creatures made their hits much bigger on the server than on screen. A boss's
    // global stays a real one that helps everyone.
    private void OneSided(SpellEffect bubble, CombatDuelSubCircle owner) {
        if (owner is not { IsHumanPlayer: true }) {
            return;
        }

        CombatEffectApplicator.KeepToSide(bubble, owner.OccupiedTeam);
        Logger.Information("Duel {0} | Slot {1} | the player's global helps only the {2} side.",
            Logger.Args(_duel.m_duelID.Full, owner.SlotIndex, owner.OccupiedTeam));
    }

    /// <summary>The Dos of this file. False when the step is none of them.</summary>
    private bool TryMoreCheatSteps(CombatDuelSubCircle boss, BossCheats.BossCheat cheat, BossCheats.CheatAction step,
                                   CombatDuelSubCircle trigger, CombatDuelSubCircle receiver, string bossName, List<string> lines,
                                   CombatActionListObj combatActionList, float cinematicTimeSoFar, bool custom, out float cinematicTime) {
        cinematicTime = 0;
        List<CombatDuelSubCircle> On(string fallback) => StepTargets(boss, step.Target.Length > 0 ? step.Target : fallback, null, trigger, receiver);

        try {
            switch (step.Do.ToLowerInvariant()) {
                case "reflect": {
                    // The player's spell fizzles and the boss casts it back: a spell on the boss's side goes on the
                    // player who cast it, one a player cast on their own side goes on the boss's side.
                    _interruptNow = true;
                    if (_cheatSpellId == 0) {
                        Logger.Warning("Duel {0} | Boss cheat of {1}: Reflect has no spell to send back (it answers a player's spell).",
                            Logger.Args(_duel.m_duelID.Full, bossName));

                        return true;
                    }

                    var back = step with { Do = "Cast", Spell = "", SpellID = _cheatSpellId, Target = step.Target.Length > 0 ? step.Target : "" };
                    cinematicTime = CheatCast(boss, cheat, back, trigger, receiver, bossName, lines, combatActionList, cinematicTimeSoFar, custom);

                    return true;
                }
                case "stealpips": {
                    var taken = On("Caster").Sum(target => ChangePips(target, step.Kind, -Math.Max(1, Math.Abs(step.Amount))));
                    if (taken > 0) {
                        ChangePips(boss, step.Kind, taken);
                    }

                    return true;
                }
                case "mark":
                    // A mark of its own name on the target (the attacker if none is picked), for the If "marked".
                    foreach (var target in On("Caster")) {
                        SetMark(target, step.Mark, true);
                    }

                    return true;
                case "unmark":
                    foreach (var target in On("Marked")) {
                        SetMark(target, step.Mark, false);
                    }

                    return true;
                case "removeeffects": {
                    // Its hanging effects of Kind (Shield, Trap, Blade, Weakness, Charm, Ward, Aura, DoT, HoT; several
                    // with commas; empty for all) come off the target (itself if none is picked), the newest first, Count
                    // of them (0 = all); protected ones stay, unless Bypass.
                    var kinds = (step.Kind ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    foreach (var target in On("Self").Where(t => t?._hangingEffects is not null)) {
                        var gone = target._hangingEffects.Where(e => e is not null && (step.Bypass || !e.m_protected) && (kinds.Length == 0 || kinds.Any(k => IsKind(e, k)))
                                                                     && FromSpell(e, step.Spell))
                            .Reverse().Take(step.Count > 0 ? step.Count : int.MaxValue).ToList();
                        target._hangingEffects.RemoveAll(gone.Contains);
                        _clearedCount += gone.Count;
                        Logger.Information("Duel {0} | Slot {1} | Cheat takes off {2} of its {3} effects.",
                            Logger.Args(_duel.m_duelID.Full, target.SlotIndex, gone.Count, kinds.Length == 0 ? "hanging" : string.Join("/", kinds)));
                    }

                    return true;
                }
                case "removeauras":
                    foreach (var target in On("AllEnemies").Where(t => t?._hangingEffects is not null)) {
                        var gone = target._hangingEffects.Count(e => CombatRequirements.IsAura(e) && !e.m_protected);
                        _clearedCount += gone;
                        target._hangingEffects.RemoveAll(e => CombatRequirements.IsAura(e) && !e.m_protected);
                        Logger.Information("Duel {0} | Slot {1} | Cheat takes off {2} auras.", Logger.Args(_duel.m_duelID.Full, target.SlotIndex, gone));
                    }

                    return true;
                case "announce": {
                    // A server announcement to the wizards in this battle only, as the Manager's Announcement card sends
                    // it to everyone: a message box (Style Box, the default), a chat line (Chat), or both. Who: "" every
                    // wizard in the battle, Players only those in the players' circles. {boss}, {player}, {round}.
                    var text = AnnounceText(step.Text, boss, trigger);
                    if (text.Length == 0) {
                        return true;
                    }

                    var style = (step.Style ?? "").Trim().ToLowerInvariant();
                    var box = style is "" or "box" or "both";
                    var chat = style is "chat" or "both";
                    var sent = 0;
                    foreach (var wizard in _subCircles.Where(c => c is not null && c.Occupied && c.IsHumanPlayer && c.ParticipantActor is not null
                                                                  && (!step.Who.Equals("Players", StringComparison.OrdinalIgnoreCase)
                                                                      || c.SlotType == CombatSlotType.Player))) {
                        if (box) {
                            wizard.ParticipantActor.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = ManagerCommands.AnnouncementBox(text), Modal = 1 });
                        }

                        if (chat) {
                            wizard.ParticipantActor.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = ManagerCommands.AnnouncementChat(text), Modal = 0 });
                        }

                        sent++;
                    }

                    Logger.Information("Duel {0} | Slot {1} | {2} announces to {3} wizard(s) in the battle ({4}): {5}",
                        Logger.Args(_duel.m_duelID.Full, boss.SlotIndex, bossName, sent, box && chat ? "box and chat" : box ? "box" : "chat", text));

                    return true;
                }
                case "addcharm" when CheatLookalikes.IsGlobal(step.Kind):
                    cinematicTime = AddCustomGlobal(boss, cheat, step, trigger, receiver, bossName, lines, combatActionList, cinematicTimeSoFar, custom);

                    return true;
                case "addcharm": {
                    // Blade, Weakness, Shield, Trap or Absorb of Amount (percent; hit points for Absorb), used up as one;
                    // or a DoT or HoT of Amount in all over Rounds rounds (3 if 0). With a Spell, the effect carries that
                    // spell's id, so the game shows it as that spell (its icon and name); the size is still Amount.
                    var amount = Math.Abs(step.Amount);
                    var overRounds = step.Rounds > 0 ? step.Rounds : 3;
                    // As a real one: m_effectParam the whole amount, m_paramPerRound each round's share.
                    var perRound = amount / overRounds;
                    var effect = (step.Kind ?? "").ToLowerInvariant() switch {
                        "dot" => CheatEffect(kSpellEffects.kDamageOverTime, amount, step.School, overRounds, good: false) with { m_paramPerRound = perRound },
                        "hot" => CheatEffect(kSpellEffects.kHealOverTime, amount, step.School, overRounds, good: true) with { m_paramPerRound = perRound },
                        "weakness" => CheatEffect(kSpellEffects.kModifyOutgoingDamage, -amount, step.School, 0, good: false),
                        "shield" => CheatEffect(kSpellEffects.kModifyIncomingDamage, -amount, step.School, 0, good: true),
                        "trap" => CheatEffect(kSpellEffects.kModifyIncomingDamage, amount, step.School, 0, good: false),
                        "absorb" => CheatEffect(kSpellEffects.kAbsorbDamage, amount, step.School, 0, good: true),
                        _ => CheatEffect(kSpellEffects.kModifyOutgoingDamage, amount, step.School, 0, good: true),
                    };
                    var fallback = effect.m_disposition == kHangingDisposition.kHarmful ? "Caster" : "Self";
                    var named = step.Spell.Length > 0 || step.SpellID != 0;
                    var aim = step.Target.Length > 0 ? step.Target : fallback;
                    var lookName = named ? step.Spell : LookalikeName(step, aim.Equals("Self", StringComparison.OrdinalIgnoreCase), boss, bossName);

                    var look = lookName.Length > 0 || step.SpellID != 0 ? FindSpell(lookName, named ? step.SpellID : 0) : default;

                    // The game draws a charm or ward on a circle only from a spell it is shown being cast (it works out
                    // the round itself), so one put on by the server alone never showed (Niduus, 2026-10-25: a 666% Shadow
                    // blade "did not activate"). So a real spell of that kind and school is cast (named in "shown as", or
                    // picked here: Shadowblade for a Shadow blade), and the effect it puts on is then given this size and
                    // school on the server. With no "shown as", the effect keeps no spell id, so it counts on its own
                    // next to a real one of that spell. A DoT or HoT is not drawn (no lookalike is cast for it).
                    if (look.Template is not null && step.Kind.ToLowerInvariant() is not ("dot" or "hot")) {
                        var lookId = look.Spell.m_templateID;
                        var before = _subCircles.Where(c => c?._hangingEffects is not null)
                            .ToDictionary(c => c, c => c._hangingEffects.ToHashSet(ReferenceEqualityComparer.Instance));
                        var cast = step with {
                            Do = "Cast", Spell = (string) look.Template.m_name, SpellID = lookId,
                            Target = aim,
                        };
                        cinematicTime = CheatCast(boss, cheat, cast, trigger, receiver, bossName, lines, combatActionList, cinematicTimeSoFar, custom);
                        var resized = 0;
                        foreach (var (circle, had) in before) {
                            var hanging = circle._hangingEffects;
                            for (var i = 0; i < hanging.Count; i++) {
                                var e = hanging[i];
                                if (e is null || had.Contains(e) || e.m_spellTemplateID != lookId || e.m_effectType != effect.m_effectType) {
                                    continue;
                                }

                                hanging[i] = e with {
                                    m_effectParam = effect.m_effectParam,
                                    m_paramPerRound = effect.m_paramPerRound,
                                    m_sDamageType = effect.m_sDamageType,
                                    m_disposition = effect.m_disposition,
                                    m_spellTemplateID = named ? lookId : 0,
                                    // A lookalike that can't be removed (Weakness - 75 Protected Shadow...) does not make yours so.
                                    m_protected = false,
                                };
                                resized++;
                            }
                        }

                        Logger.Information("Duel {0} | Slot {1} | {2}: {3} cast so the game shows it; {4} of its effects made {5} {6}{7}.",
                            Logger.Args(_duel.m_duelID.Full, boss.SlotIndex, bossName, (string) look.Template.m_name, resized,
                                effect.m_effectParam, SchoolText(step.School), step.Kind));
                        if (resized > 0) {
                            return true;
                        }

                        Logger.Information("Duel {0} | Slot {1} | {2}: the lookalike put nothing on (dispelled, or another kind); the effect goes on unseen.",
                            Logger.Args(_duel.m_duelID.Full, boss.SlotIndex, bossName));
                    }

                    foreach (var target in On(fallback).Where(t => t?._hangingEffects is not null && t.IsAlive)) {
                        target._hangingEffects.Add(effect with { m_spellTemplateID = named && look.Spell is not null ? look.Spell.m_templateID : 0 });
                    }

                    return true;
                }
                case "boost": {
                    // Kind Damage (more damage out), Resist (less damage in), Heal (more healing out) or Accuracy, by
                    // Amount percent (below 0 lowers it), for Rounds rounds (0: the rest of the battle). Never used up.
                    var amount = step.Amount;
                    var rounds = step.Rounds > 0 ? step.Rounds + 1 : REST_OF_BATTLE;
                    var effect = (step.Kind ?? "").ToLowerInvariant() switch {
                        "resist" => CheatEffect(kSpellEffects.kModifyIncomingDamage, -amount, step.School, rounds, good: amount >= 0),
                        "heal" => CheatEffect(kSpellEffects.kModifyOutgoingHeal, amount, step.School, rounds, good: amount >= 0),
                        "accuracy" => CheatEffect(kSpellEffects.kModifyAccuracy, amount, step.School, rounds, good: amount >= 0),
                        _ => CheatEffect(kSpellEffects.kModifyOutgoingDamage, amount, step.School, rounds, good: amount >= 0),
                    };
                    foreach (var target in On("Self").Where(t => t?._hangingEffects is not null && t.IsAlive)) {
                        target._hangingEffects.Add(effect with { });
                    }

                    return true;
                }
                case "immune": {
                    // No damage of School (every school when empty) for Rounds rounds: a ward of -100% that is never used up.
                    var rounds = step.Rounds > 0 ? step.Rounds + 1 : REST_OF_BATTLE;
                    foreach (var target in On("Self").Where(t => t?._hangingEffects is not null && t.IsAlive)) {
                        target._hangingEffects.Add(CheatEffect(kSpellEffects.kModifyIncomingDamage, -100, step.School, rounds, good: true));
                    }

                    return true;
                }
                case "damagecap":
                    CapDamageOf(On("Self"), step);

                    return true;
                case "stun":
                    foreach (var target in On("Caster").Where(t => t?.CombatParticipant is not null && t.IsAlive)) {
                        if (!target.TryStun()) {
                            continue;
                        }

                        target.CombatParticipant.m_stunned = Math.Max(target.CombatParticipant.m_stunned, Math.Max(1, step.Rounds));
                        Logger.Information("Duel {0} | Slot {1} | Cheat stuns it for {2} rounds.",
                            Logger.Args(_duel.m_duelID.Full, target.SlotIndex, target.CombatParticipant.m_stunned));
                    }

                    return true;
                case "healthto":
                    // Its health (or Target's) becomes Amount percent of its most, never below 1.
                    foreach (var target in On("Self")) {
                        SetHealthTo(target, (int) Math.Round(MostHealth(target) * Math.Clamp(step.Amount, 1, 100) / 100.0));
                    }

                    return true;
                case "swaphealth": {
                    // The boss and Target (the player who set it off, else a random player) swap their share of health.
                    var other = On("Caster").FirstOrDefault(t => t is not null && t != boss && t.IsAlive);
                    if (other is null || MostHealth(boss) <= 0 || MostHealth(other) <= 0) {
                        return true;
                    }

                    var mine = boss.ParticipantGameStats.m_currentHitpoints / (double) MostHealth(boss);
                    var theirs = other.ParticipantGameStats.m_currentHitpoints / (double) MostHealth(other);
                    SetHealthTo(boss, (int) Math.Round(MostHealth(boss) * theirs));
                    SetHealthTo(other, (int) Math.Round(MostHealth(other) * mine));

                    return true;
                }
                default:
                    // Niduus, 2026-10-26 ("You can legit do anything"): CombatResolver.CreativeCheats.cs.
                    return TryCreativeCheatSteps(boss, cheat, step, trigger, receiver, bossName, lines, combatActionList, cinematicTimeSoFar,
                        custom, out cinematicTime);
            }
        }
        catch (Exception e) {
            Logger.Error("Duel {0} | Boss cheat of {1}: {2} failed: {3}", Logger.Args(_duel.m_duelID.Full, bossName, step.Do, e));

            return true;
        }
    }

    // A participant's health becomes this (1 to its most), up or down.
    private void SetHealthTo(CombatDuelSubCircle target, int health) {
        if (target?.ParticipantGameStats is not { } stats || !target.IsAlive) {
            return;
        }

        var want = Math.Clamp(health, 1, Math.Max(1, MostHealth(target)));
        var now = stats.m_currentHitpoints;
        if (want > now) {
            HealFromCheat(target, want - now);
        }
        else if (want < now) {
            target.DamageParticipant(now - want);
            if (target._wizard is null && target.CombatParticipant is not null) {
                target.CombatParticipant.m_playerHealth = stats.m_currentHitpoints;
            }

            Logger.Information("Duel {0} | Slot {1} | Cheat sets its health to {2}.", Logger.Args(_duel.m_duelID.Full, target.SlotIndex, stats.m_currentHitpoints));
        }
    }

}
