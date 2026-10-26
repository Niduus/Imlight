/*
 * Cheats closer to the real bosses (Niduus, 2026-10-25: "Expand on existing cheats and search methods"). The Whens, Ifs
 * and Do of this file are listed at the top of BossCheats.cs; like every cheat, they work for players and creatures
 * alike.
 *
 * Whens: EffectRemoved (someone else's spell takes one of its effects off: Xibalba's Blessing when its aura is taken,
 * Empress of Flames's Scald once every DoT is cleared) and AllyCasts (someone on its own side casts: The Rat and The
 * Scorpion answering each other).
 * Ifs: what the spell does (SpellDoes: "a different spell that also clears DoTs"), the card's enchantment (Enchant),
 * a global being up (Global), school pips (SchoolPips), pips now (PipsMin / PipsMax), the circle someone stands in
 * (Circles), and the "has" and "marked" Ifs for someone without it, a whole side, and every enemy.
 * Do: DamageCap.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Combat;

public partial class CombatResolver {

    // The card that set off the cheat now going off (for the If "enchanted"), as _cheatSpellId is its spell.
    private Spell _cheatCard;

    // Everyone's hanging effects just before the move now being made (EffectRemoved), or null when no cheat asks.
    private Dictionary<CombatDuelSubCircle, List<SpellEffect>> _effectsBeforeMove;

    // What a spell does, for SpellDoes: the effect families it has.
    private static HashSet<string> SpellDoesOf(SpellTemplate spell) {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in Flatten(spell)) {
            var good = e.m_disposition is kHangingDisposition.kBeneficial or kHangingDisposition.kBoth;
            var bad = e.m_disposition is kHangingDisposition.kHarmful or kHangingDisposition.kBoth;
            switch (e.m_effectType) {
                case kSpellEffects.kRemoveCharm:
                    found.Add("RemoveCharm");
                    if (good) {
                        found.Add("RemoveBlade");
                    }

                    if (bad) {
                        found.Add("RemoveWeakness");
                    }

                    break;
                case kSpellEffects.kRemoveWard:
                    found.Add("RemoveWard");
                    if (good) {
                        found.Add("RemoveShield");
                    }

                    if (bad) {
                        found.Add("RemoveTrap");
                    }

                    break;
                case kSpellEffects.kRemoveOverTime:
                    found.Add("RemoveOverTime");
                    if (good) {
                        found.Add("RemoveHoT");
                    }

                    if (bad) {
                        found.Add("RemoveDoT");
                    }

                    break;
                case kSpellEffects.kRemoveAura:
                    found.Add("RemoveAura");
                    break;
                case kSpellEffects.kStealCharm or kSpellEffects.kStealWard or kSpellEffects.kStealOverTime:
                    found.Add("Steal");
                    break;
                case kSpellEffects.kPushCharm or kSpellEffects.kPushWard or kSpellEffects.kPushOverTime:
                    found.Add("Push");
                    break;
                case kSpellEffects.kSwapCharm or kSpellEffects.kSwapWard or kSpellEffects.kSwapOverTime or kSpellEffects.kSwapAll:
                    found.Add("Swap");
                    break;
                case kSpellEffects.kDetonateOverTime:
                    found.Add("Detonate");
                    break;
                case kSpellEffects.kDispel:
                    found.Add("Dispel");
                    break;
                case kSpellEffects.kStun:
                    found.Add("Stun");
                    break;
                case kSpellEffects.kPolymorph:
                    found.Add("Polymorph");
                    break;
                case kSpellEffects.kModifyPips or kSpellEffects.kModifyPowerPips or kSpellEffects.kModifyShadowPips or kSpellEffects.kModifySchoolPips:
                    found.Add("Pips");
                    break;
            }

            if (s_damageEffects.Contains(e.m_effectType)) {
                found.Add("Damage");
            }

            if (s_healEffects.Contains(e.m_effectType)) {
                found.Add("Heal");
            }
        }

        return found;
    }

    // "ClearJinx" is "RemoveTrap", "Remove Curse" is "RemoveWeakness".
    private static string DoesWord(string word) {
        var w = (word ?? "").Replace(" ", "").Trim();
        foreach (var verb in new[] { "Remove", "Clear", "Cleanse" }) {
            if (w.StartsWith(verb, StringComparison.OrdinalIgnoreCase) && w.Length > verb.Length) {
                return "Remove" + SpellNameFamily.KindSynonym(w[verb.Length..]);
            }
        }

        return w;
    }

    /// <summary>The If "the spell does": whether a spell does one of the cheat's SpellDoes (with NotSpellDoes, none).</summary>
    internal static bool SpellDoesAllows(BossCheats.BossCheat cheat, SpellTemplate spell) {
        if (string.IsNullOrWhiteSpace(cheat.SpellDoes)) {
            return true;
        }

        var does = SpellDoesOf(spell);
        var any = cheat.SpellDoes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(w => does.Contains(DoesWord(w)));

        return any != cheat.NotSpellDoes;
    }

    // The Ifs of this file that look at the battle now (Global, SchoolPips, PipsMin / PipsMax, Circles) and at the card
    // that set it off (Enchant). The one found with AnyEnemy / AnyAlly is the target Matched when the If "has" found no one.
    private bool MoreAllows(CombatDuelSubCircle boss, BossCheats.BossCheat cheat, CombatDuelSubCircle trigger, CombatDuelSubCircle receiver) {
        return EnchantAllows(cheat) && GlobalAllows(cheat) && SchoolPipsAllow(boss, cheat, trigger, receiver)
               && PipsNowAllow(boss, cheat, trigger, receiver) && CirclesAllow(boss, cheat, trigger, receiver);
    }

    private bool EnchantAllows(BossCheats.BossCheat cheat) {
        var wanted = (cheat.Enchant ?? "").Trim();
        if (wanted.Length == 0) {
            return true;
        }

        var enchantment = _cheatCard is { m_enchantment: not 0 } card ? CoreObjectFactory.GetCoreTemplate(card.m_enchantment) as SpellTemplate : null;
        bool has;
        if (wanted.Equals("Any", StringComparison.OrdinalIgnoreCase)) {
            has = enchantment is not null;
        }
        else if (wanted.Equals("None", StringComparison.OrdinalIgnoreCase)) {
            has = enchantment is null;
        }
        else if (enchantment is null) {
            has = false;
        }
        else {
            string name = enchantment.m_name;
            string key = enchantment.m_displayName;
            has = SpellNameFamily.IsOneOf(wanted.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), true,
                name, string.IsNullOrEmpty(key) ? "" : Locale.GetEnglishName(key), ((uint) _cheatCard.m_enchantment).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return has != cheat.NotEnchant;
    }

    private bool GlobalAllows(BossCheats.BossCheat cheat) {
        var global = (cheat.Global ?? "").Trim();
        if (global.Length == 0) {
            return true;
        }

        var school = (cheat.GlobalSchool ?? "").Trim();
        var up = (_duel.m_duelModifier?.m_battlefieldEffects ?? []).Any(e => e is not null
            && (school.Length == 0 || school.Equals((string) e.m_sDamageType, StringComparison.OrdinalIgnoreCase))
            && FromSpell(e, cheat.GlobalSpell));

        return global.Equals("None", StringComparison.OrdinalIgnoreCase) ? !up : up;
    }

    // Each school's pips of a participant.
    private static Dictionary<MagicSchool, int> SchoolPipsOf(CombatDuelSubCircle c) {
        var pips = c?.CombatParticipant?.m_pipCount;
        if (pips is null) {
            return [];
        }

        return new Dictionary<MagicSchool, int> {
            [MagicSchool.Balance] = pips.m_balancePips, [MagicSchool.Death] = pips.m_deathPips, [MagicSchool.Fire] = pips.m_firePips,
            [MagicSchool.Ice] = pips.m_icePips, [MagicSchool.Life] = pips.m_lifePips, [MagicSchool.Myth] = pips.m_mythPips,
            [MagicSchool.Storm] = pips.m_stormPips,
        };
    }

    // The ones an If about pips looks at: Self (if left out), Caster, Receiver, or everyone standing on a side.
    private List<CombatDuelSubCircle> WhoAll(string who, CombatDuelSubCircle boss, CombatDuelSubCircle trigger, CombatDuelSubCircle receiver,
                                             string fallback) {
        var w = (who ?? "").Trim();
        if (w.Length == 0) {
            w = fallback;
        }

        var enemy = w.Equals("AnyEnemy", StringComparison.OrdinalIgnoreCase);
        if (enemy || w.Equals("AnyAlly", StringComparison.OrdinalIgnoreCase)) {
            return [.. _subCircles.Where(c => c is not null && c.Occupied && c.AddedToDuel && c.IsAlive && (c.OccupiedTeam != boss.OccupiedTeam) == enemy)];
        }

        return WhoOf(w, boss, trigger, receiver) is { } one ? [one] : [];
    }

    // The one found by an If over a side, for the target Matched, when the If "has" found no one.
    private void Found(List<CombatDuelSubCircle> found, CombatDuelSubCircle boss) {
        if (_hasMatch is null && found.Count > 0 && found.Any(c => c != boss)) {
            var others = found.Where(c => c != boss).ToList();
            _hasMatch = others[Random.Shared.Next(others.Count)];
        }
    }

    private bool SchoolPipsAllow(CombatDuelSubCircle boss, BossCheats.BossCheat cheat, CombatDuelSubCircle trigger, CombatDuelSubCircle receiver) {
        if (cheat.SchoolPips <= 0) {
            return true;
        }

        var wanted = (cheat.SchoolPipsSchool ?? "").Trim();
        MagicSchool? school = Enum.TryParse<MagicSchool>(wanted, true, out var s) ? s : null;
        bool Fits(CombatDuelSubCircle c) {
            var pips = SchoolPipsOf(c);
            if (pips.Count == 0) {
                return false;
            }

            var most = pips.Values.Max();
            return school is { } one
                ? pips.GetValueOrDefault(one) >= cheat.SchoolPips && (!cheat.SchoolPipsMost || pips.GetValueOrDefault(one) == most)
                : most >= cheat.SchoolPips;
        }

        var found = WhoAll(cheat.SchoolPipsWho, boss, trigger, receiver, "Self").Where(Fits).ToList();
        Found(found, boss);

        return found.Count > 0;
    }

    // The Do Pips (and StealPips) with a school for Kind: that school's pips change. Null when Kind is no school.
    private int? ChangeSchoolPips(CombatDuelSubCircle target, string kind, int amount) {
        if (!Enum.TryParse<MagicSchool>((kind ?? "").Trim(), true, out var school) || !SchoolPipsOf(target).TryGetValue(school, out var before)) {
            return null;
        }

        target.ChangePips(school, amount);
        var changed = Math.Abs(SchoolPipsOf(target).GetValueOrDefault(school) - before);
        Logger.Information("Duel {0} | Slot {1} | Cheat {2} {3} {4} pips.", Logger.Args(_duel.m_duelID.Full, target.SlotIndex,
            amount > 0 ? "gives" : "takes", changed, school));

        return changed;
    }

    private bool PipsNowAllow(CombatDuelSubCircle boss, BossCheats.BossCheat cheat, CombatDuelSubCircle trigger, CombatDuelSubCircle receiver) {
        if (cheat.PipsMin <= 0 && cheat.PipsMax == 0) {
            return true;
        }

        var top = cheat.PipsMax < 0 ? 0 : cheat.PipsMax == 0 ? int.MaxValue : cheat.PipsMax;
        var found = WhoAll(cheat.PipsWho, boss, trigger, receiver, "Self")
            .Where(c => PipCount(c, cheat.PipsKind ?? "") is var n && n >= cheat.PipsMin && n <= top).ToList();
        Found(found, boss);

        return found.Count > 0;
    }

    /// <summary>The circle (1-4) someone stands in on its own side, as the game numbers them (e1-e4, p1-p4), or 0.</summary>
    internal static int SideCircleOf(CombatDuelSubCircle who) {
        if (who?._duelActor?.SubCircles is not { } circles) {
            return 0;
        }

        var side = circles.Where(c => c is not null && c.SlotType == who.SlotType).OrderBy(c => c.SlotIndex).ToList();

        return side.IndexOf(who) + 1;
    }

    private bool CirclesAllow(CombatDuelSubCircle boss, BossCheats.BossCheat cheat, CombatDuelSubCircle trigger, CombatDuelSubCircle receiver) {
        if (string.IsNullOrWhiteSpace(cheat.Circles)) {
            return true;
        }

        var wanted = cheat.Circles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => int.TryParse(w, out var n) ? n : 0).Where(n => n is >= 1 and <= 4).ToHashSet();
        var who = WhoOf(string.IsNullOrWhiteSpace(cheat.CirclesWho) ? "Caster" : cheat.CirclesWho, boss, trigger, receiver);

        return who is not null && wanted.Contains(SideCircleOf(who));
    }

    // The If "has" for someone without it (EnemyWithout, AllyWithout, AnyoneWithout) and for a whole side together
    // (EnemySide, AllySide). Null when HasWho is none of these.
    private bool? MoreHasAllows(CombatDuelSubCircle boss, BossCheats.BossCheat cheat, Func<CombatDuelSubCircle, int> count, int need) {
        var who = (cheat.HasWho ?? "").Trim().ToLowerInvariant();
        IEnumerable<CombatDuelSubCircle> Standing(bool? enemies) => _subCircles.Where(c => c is not null && c.Occupied && c.AddedToDuel && c.IsAlive
                                                                                      && (enemies is null || (c.OccupiedTeam != boss.OccupiedTeam) == enemies));
        switch (who) {
            case "enemywithout" or "allywithout" or "anyonewithout": {
                bool? side = who == "enemywithout" ? true : who == "allywithout" ? false : null;
                var without = Standing(side).Where(c => count(c) < need).ToList();
                _hasMatch = without.Count > 0 ? without[Random.Shared.Next(without.Count)] : null;

                return (without.Count > 0) != cheat.HasNot;
            }
            case "enemyside" or "allyside":
                return (Standing(who == "enemyside").Sum(count) >= need) != cheat.HasNot;
            default:
                return null;
        }
    }

    // The If "marked" for every enemy (MarkWho AllEnemies). Null when MarkWho is not that.
    private bool? AllMarked(CombatDuelSubCircle boss, BossCheats.BossCheat cheat) {
        if (!(cheat.MarkWho ?? "").Trim().Equals("AllEnemies", StringComparison.OrdinalIgnoreCase)) {
            return null;
        }

        var enemies = _subCircles.Where(c => c is not null && c.Occupied && c.AddedToDuel && c.IsAlive && c.OccupiedTeam != boss.OccupiedTeam).ToList();

        return enemies.Count > 0 && enemies.All(c => HasMark(c, cheat.Mark));
    }

    // Spell effects that take hanging effects off whoever they land on (or move them away), for EffectRemoved.
    private static readonly HashSet<kSpellEffects> s_removalEffects = [
        kSpellEffects.kRemoveCharm, kSpellEffects.kRemoveWard, kSpellEffects.kRemoveOverTime, kSpellEffects.kRemoveAura,
        kSpellEffects.kStealCharm, kSpellEffects.kStealWard, kSpellEffects.kStealOverTime, kSpellEffects.kPushCharm,
        kSpellEffects.kPushWard, kSpellEffects.kPushOverTime, kSpellEffects.kSwapCharm, kSpellEffects.kSwapWard,
        kSpellEffects.kSwapOverTime, kSpellEffects.kSwapAll, kSpellEffects.kDetonateOverTime,
    ];

    /// <summary>Everyone's hanging effects before a move, when someone's cheats answer an effect being removed.</summary>
    private void NoteEffectsBeforeMove() {
        _effectsBeforeMove = null;
        if (!BossesWithCheats().Any(b => b.Cheats.Any(c => c is { Off: false } && c.Is("EffectRemoved")))) {
            return;
        }

        _effectsBeforeMove = _subCircles.Where(c => c is not null && c.Occupied && c._hangingEffects is not null)
            .ToDictionary(c => c, c => c._hangingEffects.Where(e => e is not null).ToList());
    }

    // The effects that were on someone before the move and are not now (two alike count as two).
    private List<SpellEffect> GoneFrom(CombatDuelSubCircle who) {
        if (_effectsBeforeMove is null || !_effectsBeforeMove.TryGetValue(who, out var before)) {
            return [];
        }

        var now = (who._hangingEffects ?? []).Where(e => e is not null).ToList();
        var gone = new List<SpellEffect>();
        foreach (var e in before) {
            var i = now.IndexOf(e);
            if (i >= 0) {
                now.RemoveAt(i);
            }
            else {
                gone.Add(e);
            }
        }

        return gone;
    }

    /// <summary>
    /// The Whens of this file after a move: EffectRemoved (that move's spell took an effect off) and AllyCasts (someone
    /// else on its side cast a spell that landed).
    /// </summary>
    private float MoreWhenCheats(CombatDuelSubCircle mover, List<(CombatAction Action, SpellTemplate Spell)> landed,
                                 CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        var cinematicTime = 0.0f;
        var removing = landed.FirstOrDefault(m => Flatten(m.Spell).Any(e => s_removalEffects.Contains(e.m_effectType)));
        foreach (var (boss, cheats) in BossesWithCheats().ToList()) {
            for (var i = 0; i < cheats.Count; i++) {
                var cheat = cheats[i];
                if (!InPhase(boss, cheat) || cheat.Interrupts) {
                    continue;
                }

                if (cheat.Is("EffectRemoved") && removing.Spell is not null && mover != boss
                    && Matches(cheat, removing.Spell, removing.Action.m_targetSubcircleList ?? [], boss, mover)
                    && RemovedFrom(boss, cheat) is { } from) {
                    _cheatSpellId = removing.Action.m_spell.m_templateID;
                    _cheatCard = removing.Action.m_spell;
                    cinematicTime += TryBossCheat(boss, i, cheat, mover, from.Who,
                        $"{(string) removing.Spell.m_name} taking {from.Count} effects off slot {from.Who.SlotIndex}",
                        combatActionList, cinematicTimeSoFar + cinematicTime);
                    _cheatSpellId = 0;
                    _cheatCard = null;
                }
                else if (cheat.Is("AllyCasts") && mover != boss && mover.OccupiedTeam == boss.OccupiedTeam
                         && landed.FirstOrDefault(m => Matches(cheat, m.Spell, m.Action.m_targetSubcircleList ?? [], boss, mover))
                             is { Spell: not null } ally) {
                    _cheatSpellId = ally.Action.m_spell.m_templateID;
                    _cheatCard = ally.Action.m_spell;
                    cinematicTime += TryBossCheat(boss, i, cheat, mover, ReceiverOf(ally.Action, mover),
                        $"its ally in slot {mover.SlotIndex} casting {(string) ally.Spell.m_name}", combatActionList, cinematicTimeSoFar + cinematicTime);
                    _cheatSpellId = 0;
                    _cheatCard = null;
                }
            }
        }

        return cinematicTime;
    }

    // Whom an EffectRemoved cheat's effects came off (itself, or someone of RemovedFrom), and how many, or null.
    private (CombatDuelSubCircle Who, int Count)? RemovedFrom(CombatDuelSubCircle boss, BossCheats.BossCheat cheat) {
        var from = (cheat.RemovedFrom ?? "").Trim().ToLowerInvariant();
        var kinds = (cheat.RemovedKind ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var who = from switch {
            "anyally" => _subCircles.Where(c => c is not null && c.Occupied && c.OccupiedTeam == boss.OccupiedTeam),
            "anyenemy" => _subCircles.Where(c => c is not null && c.Occupied && c.OccupiedTeam != boss.OccupiedTeam),
            "anyone" => _subCircles.Where(c => c is not null && c.Occupied),
            _ => [boss],
        };
        foreach (var c in who) {
            var gone = GoneFrom(c).Count(e => (kinds.Length == 0 || kinds.Any(k => IsKind(e, k))) && FromSpell(e, cheat.RemovedSpell));
            if (gone > 0) {
                return (c, gone);
            }
        }

        return null;
    }

    // The Do DamageCap: no hit does more than Amount damage to the target for Rounds rounds (0 = the rest of the
    // battle). Amount 0 takes the target's caps off.
    private void CapDamageOf(List<CombatDuelSubCircle> targets, BossCheats.CheatAction step) {
        foreach (var target in targets.Where(t => t?._hangingEffects is not null && t.IsAlive)) {
            // A cheat's cap (no spell of its own) takes the place of the one before.
            target._hangingEffects.RemoveAll(e => e?.m_effectType == kSpellEffects.kMaximumIncomingDamage && e.m_spellTemplateID == 0);
            if (step.Amount > 0) {
                // For the rest of the battle it has no rounds, so no new aura takes its place and nothing counts it down;
                // protected, so no removal takes it.
                target._hangingEffects.Add(CheatEffect(kSpellEffects.kMaximumIncomingDamage, step.Amount, "", step.Rounds > 0 ? step.Rounds + 1 : 0,
                    good: true) with { m_protected = true });
            }

            Logger.Information("Duel {0} | Slot {1} | Cheat {2}.", Logger.Args(_duel.m_duelID.Full, target.SlotIndex,
                step.Amount > 0 ? $"caps the damage it takes at {step.Amount} for {RoundsText(step.Rounds)}" : "takes its damage caps off"));
        }
    }

}
