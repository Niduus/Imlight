/*
 * Shadow pacts, shadow selves, backlash and shadow creatures (Niduus, 2026-10-28: "we need shadow creatures and pacts").
 * Before this the server skipped every ShadowPactSpellEffect ("shadow pact is not done by the server"), so Shadow
 * Shrike, Seraph, Sentinel and Trickster gave nothing but their pip, and no pact ever did anything.
 *
 * What the game files say (Root.wad Spells/*.xml and Locale/en-US Spell.lang / CombatMessages.lang):
 *   Shadow self    A ShadowPactSpellEffect with m_effectType kShadowSelf and its buffs in m_effectList, for m_numRounds
 *                  rounds (Shadow Shrike +45% damage; Seraph +35% heal, +25% damage; Sentinel -30% damage in and
 *                  Intercept 50%; Trickster +25% damage). The trainer text: "The Sentinel ... can Intercept damage
 *                  directed at teammates". Here the buffs are auras on the caster that are never used up, last their
 *                  rounds, and do not push another aura off (or get pushed off by one). One shadow self at a time.
 *   Shadow pact    Every shadow spell's ShadowPactSpellEffect: m_pactEffectKind (Charm, Curse = harmful charm, Ward,
 *                  Jinx = harmful ward, HOT, DOT), m_effectTarget (on the caster, or on the enemies), m_numRounds (3)
 *                  and m_backlashPerRound (5, 10 on the fusion spells). The cards say "ShadowPact: Charm on Self";
 *                  the trainer says "Dark Shrike ... DOTs on the caster keeps him happy". A round that ends with the
 *                  pact's kind on its side is a fed round; one without is "[Unfed pact: +5 Backlash]".
 *   Backlash       CombatParticipant.m_backlash. When the caster's last pact runs out, the backlash hits them
 *                  ("Shadow Backlash Damage", kBacklashDamage): a share of their current health, 1% for each point
 *                  (the old game's "+30 Backlash = -30% of your current health"), then it is back to 0.
 *   Creatures      A fed round keeps the caster's shadow creature happy: it grows (m_shadowCreatureLevel, 1 to 4)
 *                  and may step in with the game's own shadow creature spells (spellSourceType kShadowCreature):
 *                  Fiend: Shadow Creature Fiend Attack 580/690/810/945 to all enemies; Nova: Shadow Creature Big Bomb
 *                  Attack 400/800/1200/1600 to one; Shepherd and Seraph: Shadow Creature Healer Attack (drains)
 *                  300-750 from all; Shrike: Shadow Creature Shadow Blade; Sentinel: Shadow Shield; Trickster: Shadow
 *                  Creature Armor Piercing.
 *
 * The server's own rules for when backlash strikes and when a creature steps in are not in the game files (they live
 * on KingsIsle's servers), so those parts follow the game's texts and spells and every number is a [Shadow] setting.
 * Players and creatures alike: a monster's Dark Fiend has a pact and backlash the same as a wizard's.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Spells;

namespace Imlight.CoreLib.Game.Combat;

public partial class CombatResolver {

    // [Shadow] settings, read each time so a save of Imlight.ini works at once.
    internal static bool ShadowSelfOn => ConfigurationManager.GetValue("Shadow.ShadowSelf", true);
    internal static bool ShadowPactsOn => ConfigurationManager.GetValue("Shadow.Pacts", true);
    internal static bool InterceptOn => ConfigurationManager.GetValue("Shadow.Intercept", true);
    private static bool s_backlashDamageOn => ConfigurationManager.GetValue("Shadow.BacklashDamage", true);
    private static int s_backlashPercentPerPoint => Math.Clamp(ConfigurationManager.GetValue("Shadow.BacklashPercentPerPoint", 1), 0, 10);
    private static int s_backlashMostPercent => Math.Clamp(ConfigurationManager.GetValue("Shadow.BacklashMostPercent", 90), 0, 100);
    private static bool s_creaturesOn => ConfigurationManager.GetValue("Shadow.Creatures", true);
    private static int s_creatureChance => Math.Clamp(ConfigurationManager.GetValue("Shadow.CreatureChance", 30), 0, 100);
    private static int s_fedRoundsPerLevel => Math.Max(1, ConfigurationManager.GetValue("Shadow.FedRoundsPerLevel", 2));
    private static bool s_chatLines => ConfigurationManager.GetValue("Shadow.ChatLines", true);

    private const int MOST_CREATURE_LEVEL = 4;
    private const int WHOLE_BATTLE = 999;

    // The shadow self buffs on participants: they are auras, kept apart from the one-aura-at-a-time rule.
    private static readonly ConditionalWeakTable<SpellEffect, object> s_shadowSelf = new();

    internal static bool IsShadowSelf(SpellEffect effect) => effect is not null && s_shadowSelf.TryGetValue(effect, out _);

    private sealed class ShadowPact {
        internal CombatDuelSubCircle Caster;
        // Who the pact watches: the caster, or every enemy (the pact is fed if any of them has its kind).
        internal CombatDuelSubCircle[] Watched;
        internal kEffectKinds Kind;
        internal int RoundsLeft;
        internal int BacklashPerRound;
        internal string Creature;
        internal string SpellName;
        internal int AddedInRound;
    }

    private readonly List<ShadowPact> _shadowPacts = [];
    // Fed rounds per caster this battle: what grows their shadow creature.
    private readonly Dictionary<CombatDuelSubCircle, int> _fedRounds = [];

    /// <summary>
    /// A shadow self (kShadowSelf): its m_effectList goes on the caster as auras for the effect's rounds, in place of
    /// any shadow self the caster had. A boss's lasting one (m_numRounds -1, or 0 on its own) lasts the whole battle.
    /// </summary>
    /// <returns>How many buffs went on.</returns>
    internal static int ApplyShadowSelf(ShadowSpellEffect shadow, CombatDuelSubCircle caster, uint spellTemplateId) {
        if (caster?._hangingEffects is not { } hanging || shadow.m_effectList is not { Count: > 0 } list) {
            return 0;
        }

        hanging.RemoveAll(IsShadowSelf);
        var outer = shadow.m_numRounds > 0 ? shadow.m_numRounds : WHOLE_BATTLE;
        var added = 0;
        foreach (var inner in list.Where(e => e is not null)) {
            var buff = inner with {
                m_numRounds = inner.m_numRounds > 0 ? inner.m_numRounds : outer,
                m_spellTemplateID = spellTemplateId,
                m_disposition = inner.m_disposition == kHangingDisposition.kBoth ? kHangingDisposition.kBeneficial : inner.m_disposition,
            };
            s_shadowSelf.AddOrUpdate(buff, null);
            hanging.Add(buff);
            added++;
        }

        Logger.Information("Slot {0} | Shadow self: {1} for {2} rounds.",
            Logger.Args(caster.SlotIndex, string.Join(", ", list.Where(e => e is not null).Select(e => $"{e.m_effectType} {e.m_effectParam}")), outer));

        return added;
    }

    /// <summary>
    /// Sentinel's Intercept: a standing teammate of <paramref name="target"/> with an Intercept shadow self takes that
    /// share of the hit. Returns the teammate and the share (0 when no one intercepts).
    /// </summary>
    internal static (CombatDuelSubCircle Who, int Percent) Interceptor(CombatDuelSubCircle target) {
        if (!InterceptOn || target?._duelActor is null) {
            return (null, 0);
        }

        foreach (var mate in target._duelActor.ActiveSubCircles) {
            if (mate is null || mate == target || !mate.IsAlive || !mate.AddedToDuel || mate.OccupiedTeam != target.OccupiedTeam) {
                continue;
            }

            var intercept = mate._hangingEffects?.FirstOrDefault(e => e?.m_effectType == kSpellEffects.kIntercept && IsShadowSelf(e));
            if (intercept is not null && intercept.m_effectParam > 0) {
                return (mate, Math.Clamp(intercept.m_effectParam, 0, 100));
            }
        }

        return (null, 0);
    }

    /// <summary>Puts a pact from <paramref name="pact"/> on the duel (CombatActionResolver).</summary>
    internal void AddShadowPact(ShadowPactSpellEffect pact, CombatDuelSubCircle caster, CombatDuelSubCircle selected, SpellTemplate spell) {
        if (caster is null || pact.m_numRounds == 0) {
            return;
        }

        CombatDuelSubCircle[] watched = pact.m_effectTarget switch {
            kEffectTarget.kSelf => [caster],
            kEffectTarget.kFriendlySingle or kEffectTarget.kFriendlySingleNotMe => [selected ?? caster],
            kEffectTarget.kFriendlyTeam or kEffectTarget.kFriendlyTeamAllAtOnce or kEffectTarget.kMultiTargetFriendly
                => [.. _subCircles.Where(c => c is not null && c.Occupied && c.OccupiedTeam == caster.FightsFor)],
            // On the enemy (one or all): the card says "on All Enemies", so any enemy feeds it.
            _ => [.. _subCircles.Where(c => c is not null && c.Occupied && c.OccupiedTeam != caster.FightsFor)],
        };

        var name = (string) spell?.m_name ?? "";
        var item = new ShadowPact {
            Caster = caster,
            Watched = watched,
            Kind = pact.m_pactEffectKind,
            RoundsLeft = pact.m_numRounds > 0 ? pact.m_numRounds : WHOLE_BATTLE,
            BacklashPerRound = Math.Max(0, pact.m_backlashPerRound),
            Creature = CreatureOf(name),
            SpellName = name,
            AddedInRound = (int) _duel.m_roundNum,
        };
        _shadowPacts.Add(item);
        if (pact.m_initialBacklash > 0) {
            AddBacklash(caster, pact.m_initialBacklash);
        }

        Logger.Information("Duel {0} | Slot {1} | Shadow pact from {2}: {3} on {4} for {5} rounds, {6} backlash a round unfed.",
            Logger.Args(_duel.m_duelID.Full, caster.SlotIndex, name, KindWords(item.Kind),
                pact.m_effectTarget == kEffectTarget.kSelf ? "the caster" : "the other side", item.RoundsLeft, item.BacklashPerRound));
        Tell(caster, $"Shadow Pact: keep {KindWords(item.Kind)} on {(pact.m_effectTarget == kEffectTarget.kSelf ? "yourself" : "the enemy")} for {item.RoundsLeft} rounds.");
    }

    // The shadow creature a spell belongs to, from its name (Shadow Creature Shrike is Dark Shrike, Shadow Self Fiend
    // is Shadow Fiend, Big Bomb is Dark Nova, Healer is Dark Shepherd). A school's shadow spell gets the Fiend.
    private static string CreatureOf(string spellName) {
        foreach (var (word, creature) in new[] {
                     ("Shrike", "Shrike"), ("DPS", "Shrike"), ("Sentinel", "Sentinel"), ("Tank", "Sentinel"),
                     ("Seraph", "Seraph"), ("Self Heal", "Seraph"), ("Trickster", "Trickster"), ("Crit", "Trickster"),
                     ("Nova", "Nova"), ("Big Bomb", "Nova"), ("Shepherd", "Shepherd"), ("Healer", "Shepherd"), ("Fiend", "Fiend"),
                 }) {
            if (spellName.Contains(word, StringComparison.OrdinalIgnoreCase)) {
                return creature;
            }
        }

        return "Fiend";
    }

    private static string KindWords(kEffectKinds kind) => kind switch {
        kEffectKinds.kCharm => "a helpful charm (blade)",
        kEffectKinds.kCurse => "a harmful charm (weakness)",
        kEffectKinds.kWard => "a helpful ward (shield)",
        kEffectKinds.kJinx => "a harmful ward (trap)",
        kEffectKinds.kHOT => "a heal over time",
        kEffectKinds.kDOT => "a damage over time",
        _ => "its effect",
    };

    // Is the pact fed: does anyone it watches have an effect of its kind on them now?
    private static bool Fed(ShadowPact pact)
        => pact.Watched.Any(w => w is { IsAlive: true } && (w._hangingEffects ?? []).Any(e => e is not null && !IsShadowSelf(e) && Feeds(e, pact.Kind)));

    private static bool Feeds(SpellEffect e, kEffectKinds kind) => kind switch {
        kEffectKinds.kCharm => CombatRequirements.IsCharm(e) && CombatRequirements.IsBeneficial(e),
        kEffectKinds.kCurse => CombatRequirements.IsCharm(e) && !CombatRequirements.IsBeneficial(e),
        kEffectKinds.kWard => CombatRequirements.IsWard(e) && CombatRequirements.IsBeneficial(e),
        kEffectKinds.kJinx => CombatRequirements.IsWard(e) && !CombatRequirements.IsBeneficial(e),
        kEffectKinds.kHOT => e.m_effectType == kSpellEffects.kHealOverTime,
        kEffectKinds.kDOT => e.m_effectType is kSpellEffects.kDamageOverTime or kSpellEffects.kDeferredDamage,
        _ => false,
    };

    private static void AddBacklash(CombatDuelSubCircle who, int amount) {
        if (who?.CombatParticipant is { } participant) {
            participant.m_backlash = Math.Max(0, participant.m_backlash + amount);
        }
    }

    /// <summary>
    /// The end of a round: each pact is fed or not, a happy creature may step in, and a caster whose last pact ran out
    /// takes their backlash. The creature's spells and the backlash go in the round's last casts (the 'l' queue).
    /// </summary>
    private float TickShadowPacts() {
        if (_shadowPacts.Count == 0) {
            return 0;
        }

        try {
            var endedFor = new HashSet<CombatDuelSubCircle>();
            foreach (var pact in _shadowPacts.ToList()) {
                var caster = pact.Caster;
                if (caster is null || !caster.IsAlive || !caster.AddedToDuel) {
                    _shadowPacts.Remove(pact);
                    if (caster?.CombatParticipant is { } gone) {
                        gone.m_backlash = 0;
                    }

                    continue;
                }

                if (!ShadowPactsOn) {
                    continue;
                }

                if (Fed(pact)) {
                    FedRound(caster, pact);
                }
                else if (pact.BacklashPerRound > 0) {
                    AddBacklash(caster, pact.BacklashPerRound);
                    Logger.Information("Duel {0} | Slot {1} | Unfed pact ({2}): +{3} backlash, now {4}.",
                        Logger.Args(_duel.m_duelID.Full, caster.SlotIndex, pact.SpellName, pact.BacklashPerRound, caster.CombatParticipant?.m_backlash));
                    Tell(caster, $"[Unfed pact: +{pact.BacklashPerRound} Backlash] (Backlash {caster.CombatParticipant?.m_backlash})");
                }

                pact.RoundsLeft--;
                if (pact.RoundsLeft <= 0) {
                    _shadowPacts.Remove(pact);
                    endedFor.Add(caster);
                }
            }

            foreach (var caster in endedFor.Where(c => _shadowPacts.All(p => p.Caster != c))) {
                QueueBacklash(caster);
            }
        }
        catch (Exception e) {
            Logger.Error("Duel {0} | Shadow pacts failed: {1}", Logger.Args(_duel.m_duelID.Full, e));
        }

        return 0;
    }

    private void FedRound(CombatDuelSubCircle caster, ShadowPact pact) {
        _fedRounds[caster] = _fedRounds.GetValueOrDefault(caster) + 1;
        var level = Math.Min(MOST_CREATURE_LEVEL, 1 + (_fedRounds[caster] - 1) / s_fedRoundsPerLevel);
        if (caster.CombatParticipant is { } participant) {
            participant.m_pastShadowCreatureLevel = participant.m_shadowCreatureLevel;
            participant.m_shadowCreatureLevel = level;
        }

        if (!s_creaturesOn || Random.Shared.Next(100) >= s_creatureChance) {
            Logger.Debug("Duel {0} | Slot {1} | Fed pact ({2}): the {3} is happy (level {4}).",
                Logger.Args(_duel.m_duelID.Full, caster.SlotIndex, pact.SpellName, pact.Creature, level));

            return;
        }

        var (spellName, target) = CreatureSpell(pact.Creature, level);
        if (!SpellFactory.Exists(spellName) || SpellFactory.GetSpell(spellName) is not { } spell
            || AddFreeDelayedCast(caster, spell.m_templateID, false, out _, target, false, 'l') == 0) {
            Logger.Warning("Duel {0} | Slot {1} | Shadow creature spell {2} is not in the game files.",
                Logger.Args(_duel.m_duelID.Full, caster.SlotIndex, spellName));

            return;
        }

        Logger.Information("Duel {0} | Slot {1} | Fed pact ({2}): the {3} (level {4}) steps in with {5}.",
            Logger.Args(_duel.m_duelID.Full, caster.SlotIndex, pact.SpellName, pact.Creature, level, spellName));
        Tell(caster, $"Your shadow creature ({pact.Creature}, level {level}) steps in!");
    }

    // The game's own shadow creature spells (Root.wad Spells/Shadow Spells), by creature and level.
    private static (string Spell, string Target) CreatureSpell(string creature, int level) {
        var tier = level <= 1 ? "" : $"0{level}";

        return creature switch {
            "Nova" => ($"Shadow Creature Big Bomb Attack{tier}", null),
            "Shepherd" or "Seraph" => ($"Shadow Creature Healer Attack{tier}", null),
            "Shrike" => ("Shadow Creature Shadow Blade", "me"),
            "Sentinel" => ("Shadow Shield", "me"),
            "Trickster" => ("Shadow Creature Armor Piercing", "me"),
            _ => ($"Shadow Creature Fiend Attack{tier}", null),
        };
    }

    private void QueueBacklash(CombatDuelSubCircle caster) {
        var backlash = caster.CombatParticipant?.m_backlash ?? 0;
        if (backlash <= 0) {
            return;
        }

        if (!s_backlashDamageOn || s_backlashPercentPerPoint == 0) {
            caster.CombatParticipant.m_backlash = 0;
            return;
        }

        const string BACKLASH_SPELL = "Shadow Backlash Damage";
        if (SpellFactory.Exists(BACKLASH_SPELL) && SpellFactory.GetSpell(BACKLASH_SPELL) is { } spell
            && AddFreeDelayedCast(caster, spell.m_templateID, false, out _, "me", false, 'l') > 0) {
            Logger.Information("Duel {0} | Slot {1} | Last pact ran out: backlash {2} strikes at the end of the round.",
                Logger.Args(_duel.m_duelID.Full, caster.SlotIndex, backlash));

            return;
        }

        // No backlash spell in the game files: the hit goes on without its animation.
        ApplyBacklashDamage([caster]);
    }

    /// <summary>
    /// kBacklashDamage ("Shadow Backlash Damage", CombatEffectApplicator): each target loses [Shadow]
    /// BacklashPercentPerPoint percent of their current health for each point of backlash (at most
    /// BacklashMostPercent), and their backlash is back to 0.
    /// </summary>
    internal static void ApplyBacklashDamage(CombatDuelSubCircle[] targets) {
        foreach (var target in targets.Where(t => t?.CombatParticipant is not null && t.ParticipantGameStats is not null && t.IsAlive)) {
            var backlash = target.CombatParticipant.m_backlash;
            target.CombatParticipant.m_pastBacklash = backlash;
            target.CombatParticipant.m_backlash = 0;
            if (backlash <= 0 || !s_backlashDamageOn) {
                continue;
            }

            var percent = Math.Min(s_backlashMostPercent, backlash * s_backlashPercentPerPoint);
            var damage = (int) Math.Floor(target.ParticipantGameStats.m_currentHitpoints * percent / 100.0);
            if (damage > 0) {
                target.DamageParticipant(damage);
            }

            Logger.Information("Slot {0} | Backlash {1} hits for {2} ({3}% of current health).",
                Logger.Args(target.SlotIndex, backlash, damage, percent));
            Tell(target, $"Backlash! You lose {damage} health ({percent}% of your health).");
        }
    }

    private static void Tell(CombatDuelSubCircle who, string text) {
        if (!s_chatLines || who is not { IsHumanPlayer: true, ParticipantActor: not null } || string.IsNullOrEmpty(text)) {
            return;
        }

        try {
            who.ParticipantActor.Tell(new Imcodec.MessageLayer.Generated.EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = text, Modal = 0 });
        }
        catch (Exception e) {
            Logger.Debug("Shadow line to slot {0} failed: {1}", Logger.Args(who.SlotIndex, e.Message));
        }
    }
}
