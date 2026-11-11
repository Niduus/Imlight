/*
 * Tags on the effects a cheat puts on (Niduus, 2026-10-27: "my cheat for casting 7% x3 Chromatic blades on my self, I
 * want to add a tag on it like BABA. If I have 0 BABA tag blades, it will immediately cast the cheat again"). Like
 * every cheat, for players and creatures alike.
 *
 * Do blocks: any step with a Tag ("BABA"; several with commas) tags each charm, ward, aura or over-time it puts on
 * anyone (Cast, FreeCast in any timing, AddCharm, AddEffects, CastBy, Aura, Boost, Immune...). RemoveEffects with a
 * Tag takes off only effects with that tag.
 * Ifs: the "has" If's HasTag and the Conditions entry "effects" (Tag) count only effects with the tag.
 * When: TagGone, the moment TagWho ("" itself, AnyAlly, AnyEnemy, Anyone) has no effect tagged Tag left after having
 * had one: checked after every move and at the start and end of each round. TagStart: also at the start of a round
 * when there is none (so a first round's cheat needs no other When). "The attacker" and "the one hit" are the one who
 * ran out.
 * When: Always (Niduus, 2026-10-27: "I want it so IF tags = 0, THEN CAST tags. Not check WHEN tags"): no event; it
 * goes off whenever its Ifs hold, looked at after every move and at the start and end of each round (at most once
 * each look). "If it has no blade tagged BABA" then "add three 7% blades tagged BABA" keeps them up. "The attacker"
 * and "the one hit" are itself.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Combat;

public partial class CombatResolver {

    // The tags on each hanging effect, by the effect itself (not its value: three alike 7% blades are three).
    private static readonly ConditionalWeakTable<SpellEffect, HashSet<string>> s_effectTags = new();

    private static string[] TagsOf(string tag)
        => (tag ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Whether a hanging effect has every one of these tags (no tags asked for: any effect).</summary>
    internal static bool HasTag(SpellEffect effect, string tag) {
        var wanted = TagsOf(tag);
        if (wanted.Length == 0) {
            return true;
        }

        return effect is not null && s_effectTags.TryGetValue(effect, out var tags) && wanted.All(tags.Contains);
    }

    private static void AddTags(SpellEffect effect, string tag) {
        if (effect is null) {
            return;
        }

        var tags = s_effectTags.GetValue(effect, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        foreach (var t in TagsOf(tag)) {
            tags.Add(t);
        }
    }

    /// <summary>An effect made from another (more rounds, its protection gone) keeps the other's tags.</summary>
    internal static void CarryTags(SpellEffect from, SpellEffect to) {
        if (from is not null && to is not null && s_effectTags.TryGetValue(from, out var tags)) {
            s_effectTags.AddOrUpdate(to, new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase));
        }
    }

    // Everyone's hanging effects before a tagged step, so the ones it puts on can be told apart.
    private Dictionary<CombatDuelSubCircle, HashSet<SpellEffect>> HangingNow()
        => _subCircles.Where(c => c?._hangingEffects is not null)
            .ToDictionary(c => c, c => new HashSet<SpellEffect>(c._hangingEffects.Where(e => e is not null), ReferenceEqualityComparer.Instance));

    // Tags every effect on anyone that was not there before the step; returns how many.
    private int TagNewEffects(Dictionary<CombatDuelSubCircle, HashSet<SpellEffect>> before, string tag) {
        var tagged = 0;
        foreach (var circle in _subCircles.Where(c => c?._hangingEffects is not null)) {
            var had = before.GetValueOrDefault(circle);
            foreach (var e in circle._hangingEffects.Where(e => e is not null && (had is null || !had.Contains(e)))) {
                AddTags(e, tag);
                tagged++;
            }
        }

        if (tagged > 0) {
            Logger.Information("Duel {0} | Cheat tags {1} effects \"{2}\".", Logger.Args(_duel.m_duelID.Full, tagged, tag.Trim()));
        }

        return tagged;
    }

    // How many effects tagged Tag someone has.
    private static int TaggedCount(CombatDuelSubCircle who, string tag)
        => (who?._hangingEffects ?? []).Count(e => e is not null && HasTag(e, tag));

    // TagGone: whether each one watched had a tagged effect at the last look, by cheat.
    private readonly Dictionary<(CombatDuelSubCircle Owner, BossCheats.BossCheat Cheat, CombatDuelSubCircle Who), bool> _tagsHeld = [];

    // A look of Always and TagGone is under way (a cheat's own steps never start another).
    private bool _watchingCheats;

    /// <summary>The Whens Always and TagGone, after a move and at a round's start (<paramref name="roundStart"/>) or end.</summary>
    private float TagGoneCheats(bool roundStart, CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        if (_watchingCheats) {
            return 0;
        }

        _watchingCheats = true;
        try {
            return WatchCheats(roundStart, combatActionList, cinematicTimeSoFar);
        }
        finally {
            _watchingCheats = false;
        }
    }

    private float WatchCheats(bool roundStart, CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        var cinematicTime = 0.0f;
        foreach (var (boss, cheats) in BossesWithCheats().ToList()) {
            for (var i = 0; i < cheats.Count; i++) {
                var cheat = cheats[i];
                // Always: whenever its Ifs hold (TryBossCheat looks at them), about itself.
                if (cheat.Is("Always") && boss.IsAlive && InPhase(boss, cheat)) {
                    cinematicTime += TryBossCheat(boss, i, cheat, boss, boss, "its Ifs holding", combatActionList,
                        cinematicTimeSoFar + cinematicTime);

                    continue;
                }

                if (!cheat.Is("TagGone") || TagsOf(cheat.Tag).Length == 0 || !boss.IsAlive || !InPhase(boss, cheat)) {
                    continue;
                }

                var whoText = (cheat.TagWho ?? "").Trim().ToLowerInvariant();
                var watched = whoText switch {
                    "anyally" => _subCircles.Where(c => c is not null && c.Occupied && c.AddedToDuel && c.OccupiedTeam == boss.OccupiedTeam),
                    "anyenemy" => _subCircles.Where(c => c is not null && c.Occupied && c.AddedToDuel && c.OccupiedTeam != boss.OccupiedTeam),
                    "anyone" => _subCircles.Where(c => c is not null && c.Occupied && c.AddedToDuel),
                    _ => [boss],
                };
                foreach (var who in watched.ToList()) {
                    var key = (boss, cheat, who);
                    if (!who.IsAlive) {
                        _tagsHeld[key] = false;
                        continue;
                    }

                    if (TaggedCount(who, cheat.Tag) > 0) {
                        _tagsHeld[key] = true;
                        continue;
                    }

                    var ranOut = _tagsHeld.GetValueOrDefault(key);
                    if (!ranOut && !(roundStart && cheat.TagStart)) {
                        continue;
                    }

                    // Once each time they run out: it goes off again only after they have one again.
                    _tagsHeld[key] = false;
                    cinematicTime += TryBossCheat(boss, i, cheat, who, who,
                        ranOut ? $"slot {who.SlotIndex} running out of effects tagged \"{cheat.Tag}\""
                            : $"round {BossRound(boss)} with no effect tagged \"{cheat.Tag}\" on slot {who.SlotIndex}",
                        combatActionList, cinematicTimeSoFar + cinematicTime);
                }
            }
        }

        return cinematicTime;
    }

}
