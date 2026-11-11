/*
 * Cheats that don't get in each other's way (Niduus, 2026-11-11: "these cheats are colliding when they are specified.
 * Look into this and fix future problems").
 *
 * What happened: every wizard played on the server's PC runs the same player cheat list. Walter's "put a 300% trap on
 * all allies but me" put a trap on Lena, and Lena's own "turn traps on me into shields" took it at once, so Walter's
 * "turn traps on allies into blades" found nothing to turn a round later. The same goes for creatures on one side that
 * share cheats.
 *
 * Now each effect a cheat puts on remembers whose cheat made it (the owner: a wizard or a creature). A cheat step that
 * takes effects off or changes them (ConvertEffects, RemoveEffects, RemoveCharms, RemoveWards, StealEffects, the other
 * side of SwapEffects, ScaleEffects, EffectsSchool, ProtectEffects) leaves alone the ones an ALLY's cheat made: each
 * owner's cheats work on what their own cheats made, on what enemies (and their cheats) made, and on ordinary spells.
 * A step with a Tag reaches them anyway (it names them on purpose). For players and creatures alike.
 * [BossCheats] KeepAllyCheatEffects (live, default true) turns this off.
 */

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Combat;

public partial class CombatResolver {

    // [BossCheats] KeepAllyCheatEffects: a cheat leaves alone the effects an ally's cheat put on (see above). Live.
    internal static bool KeepAllyCheatEffects => ConfigurationManager.GetValue("BossCheats.KeepAllyCheatEffects", true);

    // Whose cheat put each hanging effect on, by the effect itself.
    private static readonly ConditionalWeakTable<SpellEffect, CombatDuelSubCircle> s_effectMakers = new();

    // Steps that swap hanging effects for changed copies of themselves: what they make is not new, so it keeps its maker
    // (CarryTags) and gets none of its own.
    private static readonly HashSet<string> s_changeSteps = new(System.StringComparer.OrdinalIgnoreCase) {
        "ScaleEffects", "EffectsSchool", "ProtectEffects",
    };

    /// <summary>Whose cheat put this effect on (null: no cheat, or not known).</summary>
    internal static CombatDuelSubCircle MakerOf(SpellEffect effect)
        => effect is not null && s_effectMakers.TryGetValue(effect, out var maker) ? maker : null;

    // Marks every effect on anyone that was not there before a cheat's step (or free cast) as made by its owner. One that
    // already has a maker keeps it (a converted copy of an ally's trap stays theirs only when copied with CarryTags).
    private void MarkCheatEffects(Dictionary<CombatDuelSubCircle, HashSet<SpellEffect>> before, CombatDuelSubCircle owner) {
        if (before is null || owner is null) {
            return;
        }

        foreach (var circle in _subCircles) {
            if (circle?._hangingEffects is null) {
                continue;
            }

            var had = before.GetValueOrDefault(circle);
            foreach (var e in circle._hangingEffects) {
                if (e is not null && (had is null || !had.Contains(e))) {
                    s_effectMakers.TryAdd(e, owner);
                }
            }
        }
    }

    /// <summary>
    /// Whether a cheat of <paramref name="owner"/> must leave this effect alone: an ally's cheat (another wizard or
    /// creature on its side) put it on. A step with a Tag (<paramref name="tag"/>) reaches it anyway.
    /// </summary>
    private static bool AllyCheatMade(CombatDuelSubCircle owner, SpellEffect effect, string tag = null) {
        if (owner is null || effect is null || !string.IsNullOrWhiteSpace(tag) || !KeepAllyCheatEffects) {
            return false;
        }

        var maker = MakerOf(effect);

        return maker is not null && maker != owner && maker.OccupiedTeam == owner.OccupiedTeam;
    }

    // Logs how many effects a step left alone for an ally's cheat (none: nothing).
    private void LogKeptForAllies(CombatDuelSubCircle owner, CombatDuelSubCircle target, int kept, string what) {
        if (kept > 0) {
            Logger.Information("Duel {0} | Slot {1} | Cheat leaves {2} effects on slot {3} alone: an ally's cheat put them on ({4}).",
                Logger.Args(_duel.m_duelID.Full, owner?.SlotIndex, kept, target?.SlotIndex, what));
        }
    }

}
