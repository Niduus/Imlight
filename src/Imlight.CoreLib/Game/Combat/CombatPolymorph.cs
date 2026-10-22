/*
 * Polymorph (Niduus, 2026-10-21): a kPolymorph effect turns its target into another creature, such as Malistaire the
 * Undying into Haunted Malistaire ("Afterlife - Malistaire 02", polymorph template 1336084, creature 1336037).
 *
 * The game works the spell out on its own from the cast: its CombatResolver has a kPolymorph handler that calls
 * CombatParticipant::ApplyPolymorphStatChanges and gives kResult_Polymorphed, and the spell's cinematic ("Shadow
 * Malistaire") has a PolymorphCinematicAction that swaps the model in place and keeps the same share of health.
 * A game that comes into the battle later reads the polymorph from the participant: ClientCinematic::HandleNewCombat
 * looks at m_nPolymorphTurnLength, m_polymorphSpellTemplateID and m_polymorphEffect. So the server keeps those three,
 * and gives the participant the new creature's health and deck; its cheats come from the new creature's file
 * (CombatResolver.BossCheats.cs). The participant keeps its own template, so drops and quests stay with it.
 *
 * Only a polymorph that lasts the rest of the battle (m_numRounds below 0) on a creature is kept by the server; the
 * game still shows any other, but the server does not turn it back or change a wizard's deck.
 */

using System;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Combat;

internal static class CombatPolymorph {

    // The hand a creature draws, as CombatDuelSubCircle deals it.
    private const byte HAND_SIZE = 7;

    /// <summary>The creature a polymorph template turns into, or 0.</summary>
    internal static ulong MobOf(int polymorphTemplateId)
        => polymorphTemplateId > 0 && CoreObjectFactory.GetCoreTemplate((ulong) polymorphTemplateId) is WizPolymorphTemplate polymorph
            ? polymorph.m_mobTemplateID
            : 0;

    /// <summary>The creature a participant has been turned into, or 0.</summary>
    internal static ulong FormOf(CombatDuelSubCircle circle)
        => circle?.CombatParticipant is { m_nPolymorphTurnLength: not 0, m_polymorphEffect: { } effect } ? MobOf(effect.m_effectParam) : 0;

    /// <summary>
    /// Turns a creature into the polymorph's creature until the battle ends: its health (the same share of the new
    /// most health), its deck and the participant's polymorph fields.
    /// </summary>
    /// <returns>Whether the server keeps the polymorph.</returns>
    internal static bool Apply(CombatDuelSubCircle target, SpellEffect effect, uint spellTemplateId) {
        if (target?.CombatParticipant is not { } participant || target.ParticipantGameStats is not { } stats
            || target.IsHumanPlayer || !target.IsAlive || effect.m_numRounds >= 0) {
            return false;
        }

        var mobId = MobOf(effect.m_effectParam);
        if (mobId == 0 || CoreObjectFactory.GetCoreTemplate(mobId) is not GameObjectTemplate mob) {
            Logger.Warning("Slot {0} | Polymorph {1} names no creature.", Logger.Args(target.SlotIndex, effect.m_effectParam));

            return false;
        }

        participant.m_polymorphSpellTemplateID = spellTemplateId;
        participant.m_polymorphEffect = effect;
        participant.m_nPolymorphTurnLength = effect.m_numRounds;

        // A boss changed in the app's Bosses tab keeps its changes in its new form too; a custom's form is the custom
        // its Forms names (Customs.cs), if any.
        var changes = BossOverrides.For(Customs.FormKey(target.ParticipantObject, mobId));
        var npc = mob.m_behaviors?.OfType<NPCBehaviorTemplate>().FirstOrDefault();
        var most = changes?.Health ?? npc?.m_nStartingHealth ?? 0;
        if (most > 0) {
            var before = participant.m_maxPlayerHealth > 0 ? participant.m_maxPlayerHealth : stats.m_baseHitpoints + stats.m_bonusHitpoints;
            var share = before > 0 ? Math.Clamp(stats.m_currentHitpoints / (float) before, 0f, 1f) : 1f;
            var now = Math.Max(1, (int) (most * share));
            stats.m_baseHitpoints = Math.Max(1, most - stats.m_bonusHitpoints);
            stats.m_currentHitpoints = now;
            participant.m_maxPlayerHealth = most;
            participant.m_playerHealth = now;
        }

        var deck = (changes is null ? null : BossOverrides.DeckOf(changes))
                   ?? [.. CombatCreatureDeckComponent.GameSpellIds(mob)
                       .Select(id => new CombatDeckSpellData { TemplateId = id, Quantity = BossOverrides.UNLIMITED })];
        if (deck.Count > 0) {
            target._combatDeck = new CombatDeck(deck, [], HAND_SIZE);
        }

        Logger.Information("Slot {0} | Polymorphed into {1} ({2}): {3} health, {4} cards.",
            Logger.Args(target.SlotIndex, mobId, mob.m_objectName, stats.m_currentHitpoints, deck.Count));

        return true;
    }

}
