/*
 * Imlight
 * Copyright (C) 2026 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Battlegrounds;

/*
 * A wizard in a Beastmoon form fights as the beast, not with their own deck. The form says what the beast is:
 * - the creature it stands for, WizPolymorphTemplate m_mobTemplateID (BG-Polymorph-Colossus-Myth-01 is
 *   BG_Polymorph_Player_Colossus_Myth_01): its health (NPCBehaviorTemplate m_nStartingHealth, 925 at rank 1), and its
 *   deck, which its equipment item names (DeckBehaviorTemplate m_defaultDeck MDeck-M-BG-Polymorph-Colossus-A-01). The
 *   deck file is in Root.wad's Decks/Battlegrounds/ (a DeckTemplate: spell names, one entry per copy, here
 *   BG_Poly046_Spell_01 x3 ... _06 x2). SpiralDB has none of these decks, so the creature reader
 *   (CombatCreatureDeckComponent) found only a stand-in deck for them.
 * - PolymorphBattlegroundsBehaviorTemplate: the hand (m_maxHandSize, 3) and the pips the beast starts a battle with
 *   (m_maxTempStartingPips, m_maxTempStartingPowerPips).
 * A beast that flees or falls goes back to its team's base in the arena, not to the world hub (HandleDefeat).
 * The beast keeps its health from one battle to the next (Battlegrounds.Player.BeastHealth); a new form, or getting up
 * after a fall, is full health again. The arenas' health wisps (BG_WispHealth, WispHealthComponent) heal the beast, and
 * the game's health globe shows the beast's health while the wizard is one (ShowHealth); leaving the arena rebuilds the
 * wizard, whose own health comes back with it.
 * The battle uses a copy of the wizard's stats (CombatDuelSubCircle.FightsWithCopy), so the beast's health never
 * touches the wizard's own. The wizard's gear still adds its damage and resist to the beast; the live game's
 * battleground stats (WizBGPolymorphStatisticOverrideEffect, the sigil's PvE/PvP scalars) are not applied yet.
 */
internal static class BeastCombat {

    /// <summary>What a beast fights with.</summary>
    internal sealed record Loadout(string FormName, string DeckName, List<CombatDeckSpellData> Deck, byte HandSize,
                                   int Health, int StartingPips, int StartingPowerPips);

    private static readonly ConcurrentDictionary<string, Loadout> s_loadouts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<ulong, DateTime> s_lastDefeat = new();

    /// <summary>
    /// What the wizard fights with in this battle when they are a beast in a battleground, or null (their own deck).
    /// </summary>
    internal static Loadout For(Wizard wizard) {
        if (wizard is null || !Battlegrounds.Enabled || !Battlegrounds.IsBattlegroundZone(wizard.Zone)
            || Battlegrounds.Of(wizard.CharId) is not { FormTemplateId: not 0, FormName: { } formName }) {
            return null;
        }

        if (!s_loadouts.TryGetValue(formName, out var loadout)) {
            loadout = Read(formName);
            if (loadout is null) {
                return null;
            }

            s_loadouts[formName] = loadout;
        }

        // The battle cards the wizard bought for this form at the Battle Card Vendor join its deck.
        var bought = BattleCards.DeckCards(wizard.CharId, formName);

        return bought.Count == 0 ? loadout : loadout with { Deck = [.. loadout.Deck, .. bought] };
    }

    // A health wisp gives back this share of the beast's health (the world's wisps, WispHealthComponent, give 25%).
    private const float WISP_HEAL_SHARE = 0.25f;

    /// <summary>The health the beast starts this battle with: what it had left, or full.</summary>
    internal static int CurrentHealth(ulong charId, Loadout loadout) {
        var kept = Battlegrounds.Of(charId)?.BeastHealth ?? 0;

        return kept > 0 ? Math.Min(kept, loadout.Health) : loadout.Health;
    }

    /// <summary>The beast's health changed in battle; it is kept for the next one.</summary>
    internal static void NoteHealth(ulong charId, int health) {
        if (Battlegrounds.Of(charId) is { } player) {
            player.BeastHealth = Math.Max(0, health);
        }
    }

    /// <summary>Puts the beast's health on the wizard's health globe (out of battle), if they are a beast in an arena.</summary>
    internal static void ShowHealth(Wizard wizard, IActorRef session) {
        if (session is null || For(wizard) is not { } loadout) {
            return;
        }

        session.Tell(new WIZARD_12_PROTOCOL.MSG_UPDATEHEALTH {
            CharacterID = wizard.GameObjectID,
            NewHealth = CurrentHealth(wizard.CharId, loadout),
            NewHealthMax = loadout.Health,
            DisplayDiff = 0,
        });
    }

    /// <summary>
    /// A health wisp in an arena heals the beast, not the wizard. Returns false when the wizard is not a beast there (the
    /// wisp heals them as anywhere else), true when it was for the beast (also when it was full and nothing was taken).
    /// </summary>
    internal static bool HealFromWisp(Wizard wizard, IActorRef session, out bool used) {
        used = false;
        if (For(wizard) is not { } loadout) {
            return false;
        }

        var now = CurrentHealth(wizard.CharId, loadout);
        if (now >= loadout.Health) {
            return true;
        }

        var healed = Math.Min(loadout.Health, now + (int) Math.Ceiling(loadout.Health * WISP_HEAL_SHARE));
        NoteHealth(wizard.CharId, healed);
        session?.Tell(new WIZARD_12_PROTOCOL.MSG_UPDATEHEALTH {
            CharacterID = wizard.GameObjectID,
            NewHealth = healed,
            NewHealthMax = loadout.Health,
            DisplayDiff = 1,
        });
        used = true;

        return true;
    }

    // Seconds a fallen beast stays down (DeathEffect): the base teleporters refuse it meanwhile
    // (Trigger_TeleInactive_Notify, WizardActorDialogue_00000836). The game data does not say how long; a guess.
    internal static int DeathSeconds => Math.Max(0, ConfigurationManager.GetValue("Beastmoon.DeathSeconds", 30));

    /// <summary>
    /// A wizard fled or fell in a battle. In a battleground they go back to their team's base in the same arena (not to
    /// the world hub, which would take them out of the match); a fallen beast is down for [Beastmoon] DeathSeconds.
    /// Returns whether it was handled here.
    /// </summary>
    internal static bool HandleDefeat(Wizard wizard, IActorRef session, IActorRef zone, bool died) {
        if (wizard is null || !Battlegrounds.IsBattlegroundZone(wizard.Zone)
            || Battlegrounds.Of(wizard.CharId) is not { Team: not Battlegrounds.Team.None } player) {
            return false;
        }

        // A lost battle is reported twice (as a fall, then as a flee); the first one counts.
        if (s_lastDefeat.TryGetValue(wizard.CharId, out var last) && DateTime.UtcNow - last < TimeSpan.FromSeconds(5)) {
            return true;
        }

        s_lastDefeat[wizard.CharId] = DateTime.UtcNow;
        // In a PvE match the monsters score for a beast they beat ("Defeated", BattlegroundScore).
        if (died && player.MatchName is { } matchName && Battlegrounds.Template(matchName)?.m_pveMode == true) {
            BattlegroundScore.Give(zone, Battlegrounds.Team.Blue, BattlegroundScore.DefeatPoints, BattlegroundScore.PE_KILL_PLAYER);
        }

        var start = player.Team == Battlegrounds.Team.Blue ? Battlegrounds.BLUE_START : Battlegrounds.RED_START;
        var charId = wizard.CharId;
        var zonePath = wizard.Zone;
        // The move asks the zone where the base is, so it runs off the session's own thread.
        Task.Run(() => Imlight.CoreLib.Game.Results.Handlers.RaidResult.MoveTo(session, zone, zonePath, start));

        var seconds = DeathSeconds;
        if (!died) {
            ShowHealth(wizard, session);
        }

        if (died && seconds > 0) {
            Battlegrounds.SetEffect(charId, Battlegrounds.DEATH_EFFECT, true);
            Task.Delay(TimeSpan.FromSeconds(seconds)).ContinueWith(_ => {
                Battlegrounds.SetEffect(charId, Battlegrounds.DEATH_EFFECT, false);
                // Up again with full health.
                NoteHealth(charId, 0);
                ShowHealth(wizard, session);
                session.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = "Your beast has recovered. The way out of the base is open.", Modal = 0 });
            });
            session.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE {
                Message = $"Your beast was defeated. It recovers in your base in {seconds} seconds.", Modal = 0,
            });
        }

        Logger.Information("Beastmoon | {0} {1} a battle and goes back to {2}{3}.",
            Logger.Args(charId, died ? "fell in" : "fled", start, died && seconds > 0 ? $", down for {seconds}s" : ""));

        return true;
    }

    private static Loadout Read(string formName) {
        var form = Battlegrounds.Form(formName, out _);
        if (form is null) {
            return null;
        }

        var mob = CoreObjectFactory.GetCoreTemplate((ulong) form.m_mobTemplateID) as GameObjectTemplate;
        var npc = mob?.m_behaviors?.OfType<NPCBehaviorTemplate>().FirstOrDefault();
        var behavior = form.m_behaviors?.OfType<PolymorphBattlegroundsBehaviorTemplate>().FirstOrDefault();

        // The deck is named on the creature, or (as here) on an item it wears.
        var behaviors = (mob?.m_behaviors ?? [])
            .Concat(mob?.m_behaviors?.OfType<EquipmentBehaviorTemplate>()
                .SelectMany(e => e.m_itemList ?? [])
                .Select(id => CoreObjectFactory.GetCoreTemplate(id))
                .Where(t => t is not null)
                .SelectMany(t => t.m_behaviors ?? []) ?? []);
        var deckName = behaviors.OfType<DeckBehaviorTemplate>().Select(d => (string) d.m_defaultDeck).FirstOrDefault(n => !string.IsNullOrEmpty(n));
        var deckFile = ClientDecks.SpellNames(deckName);

        var deck = new List<CombatDeckSpellData>();
        var missing = new List<string>();
        foreach (var group in (deckFile ?? []).GroupBy(n => n)) {
            if (SpellFactory.GetSpell(group.Key) is { } spell) {
                deck.Add(new CombatDeckSpellData { TemplateId = spell.m_templateID, Quantity = (uint) group.Count() });
            }
            else {
                missing.Add(group.Key);
            }
        }

        if (deck.Count == 0) {
            Logger.Warning("Beastmoon | The form {0} has no deck the server can read (creature {1}, deck {2}); it fights with the wizard's own.",
                Logger.Args(formName, form.m_mobTemplateID, deckName ?? "none"));

            return null;
        }

        var loadout = new Loadout(
            formName,
            deckName,
            deck,
            (byte) Math.Clamp(behavior?.m_maxHandSize ?? 7, 1, 7),
            Math.Max(1, npc?.m_nStartingHealth ?? 1),
            Math.Max(0, behavior?.m_maxTempStartingPips ?? 0),
            Math.Max(0, behavior?.m_maxTempStartingPowerPips ?? 0));
        Logger.Information("Beastmoon | {0} fights with {1}: {2} cards ({3} kinds){4}, a hand of {5}, {6} health, {7} pips and {8} power pips.",
            Logger.Args(formName, deckName, deck.Sum(d => (int) d.Quantity), deck.Count,
                missing.Count == 0 ? "" : $", {missing.Count} unknown to the server ({string.Join(", ", missing)})",
                loadout.HandSize, loadout.Health, loadout.StartingPips, loadout.StartingPowerPips));

        return loadout;
    }

}
