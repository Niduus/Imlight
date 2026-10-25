/*
 * Imlight
 * Copyright (C) 2025 Revive101
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
using System.IO;
using System.Linq;
using System.Text.Json;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Randomization;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Spells;

/// <summary>
/// Resetting training points (the game's Respec, Niduus 2026-10-24): every spell a wizard bought with training points
/// leaves their spellbook and decks, and they get those points back. Spells of their own school, free spells and spells
/// from quests and drops stay.
///
/// Which spells were bought: from now on each training is written down in trained-spells.json next to the server (by
/// character, each spell with what it cost). A spell the file does not have, learned before it existed, counts as
/// bought for 1 point when a spell trainer of the game teaches it (after the randomizer's swap) and it is not of the
/// wizard's school, the price trainers ask.
///
/// It is free. Mr. Lincoln offers it ([Trainers] RespecAtTrainers), which opens the game's own reset window (MSG_RESPEC);
/// `.respec` does it from the chat for anyone.
/// </summary>
internal static class TrainingReset {

    internal const string FILE = "trained-spells.json";

    internal static bool AtTrainers => ConfigurationManager.GetValue("Trainers.RespecAtTrainers", true);

    private static readonly JsonSerializerOptions s_options = new() { WriteIndented = true };
    private static readonly object s_lock = new();

    // Character id -> spell id -> the training points it cost.
    private static Dictionary<ulong, Dictionary<uint, int>> s_trained;

    // The wizards a reset window was opened for, and when, so the window's answer is only taken from them.
    private static readonly ConcurrentDictionary<ulong, DateTime> s_windowOpen = new();

    // Every spell some trainer of the game teaches, before randomizer swaps.
    private static readonly Lazy<HashSet<uint>> s_trainerSpells = new(() => [
        .. SpiralDB.NpcSpellInventories.Values
            .Where(inventory => inventory?.Spells is not null)
            .SelectMany(inventory => inventory.Spells)
            .Select(entry => (uint) entry.TemplateID)
    ]);

    // The same after each wizard's randomizer swaps (their swaps never change).
    private static readonly ConcurrentDictionary<ulong, HashSet<uint>> s_taughtByWizard = new();

    /// <summary>A spell that training points can be taken back from, and how many.</summary>
    internal readonly record struct BoughtSpell(uint SpellId, int Cost);

    /// <summary>Writes down that a wizard trained a spell for this many points.</summary>
    internal static void Record(Wizard wizard, uint spellId, int cost) {
        if (wizard is null) {
            return;
        }

        lock (s_lock) {
            var trained = Load();
            if (!trained.TryGetValue(wizard.CharId, out var spells)) {
                trained[wizard.CharId] = spells = [];
            }

            spells[spellId] = Math.Max(0, cost);
            Save(trained);
        }
    }

    /// <summary>The spells a reset would take away from a wizard, with what each gives back.</summary>
    internal static List<BoughtSpell> BoughtSpells(Wizard wizard) {
        var bought = new List<BoughtSpell>();
        if (wizard?.SpellbookBehavior?.LearnedSpellTemplateIds is not { } known) {
            return bought;
        }

        Dictionary<uint, int> recorded;
        lock (s_lock) {
            recorded = Load().TryGetValue(wizard.CharId, out var spells) ? new(spells) : [];
        }

        // The spells trainers teach this wizard, after the randomizer's swaps.
        var taught = Randomizer.RandomizeSpells
            ? s_taughtByWizard.GetOrAdd(wizard.CharId, _ => s_trainerSpells.Value.Select(id => Randomizer.MapSpell(wizard, id)).ToHashSet())
            : s_trainerSpells.Value;

        foreach (var spellId in known.Distinct()) {
            if (recorded.TryGetValue(spellId, out var cost)) {
                if (cost > 0) {
                    bought.Add(new(spellId, cost));
                }

                continue;
            }

            if (taught.Contains(spellId)
                    && CoreObjectFactory.GetCoreTemplate(spellId) is SpellTemplate template
                    && (string) template.m_sMagicSchoolName != wizard.MagicSchoolBehavior.MagicSchool.ToString()) {
                bought.Add(new(spellId, 1));
            }
        }

        return bought;
    }

    /// <summary>How many training points a reset would give a wizard back.</summary>
    internal static int PointsBack(Wizard wizard)
        => BoughtSpells(wizard).Sum(spell => spell.Cost);

    /// <summary>
    /// Opens the game's reset window for a wizard. Its answer is taken for the next ten minutes.
    /// </summary>
    internal static void OpenWindow(IActorRef playerActor, Wizard wizard, ulong npcGlobalId) {
        s_windowOpen[wizard.CharId] = DateTime.UtcNow;
        var points = PointsBack(wizard);
        Logger.Information("Respec | Opening the reset window for {0}: {1} training points back for {2} spells.",
            Logger.Args(wizard.PlayerNameBehavior?.GetWizardName(), points, BoughtSpells(wizard).Count));

        playerActor.Tell(new WIZARDHOUSING_50_PROTOCOL.MSG_RESPEC {
            GlobalID = npcGlobalId,
            Points = points,
            WebFailure = 0,
            Credits = wizard.Account?.Crowns ?? 0,
            LevelIsLow = 0,
            EncounteredUpgradedTierSpell = 0,
            Cost = 0,
            Buffer = ""
        });
        Tell(playerActor, $"Reset training points: you would get {points} back. If no window opened, type .respec yes to do it.");
    }

    /// <summary>Takes the window's answer, if this wizard was shown it lately.</summary>
    internal static bool TakeWindowAnswer(Wizard wizard)
        => wizard is not null
            && s_windowOpen.TryRemove(wizard.CharId, out var opened)
            && DateTime.UtcNow - opened < TimeSpan.FromMinutes(10);

    /// <summary>
    /// Resets a wizard's training points: takes every bought spell out of their spellbook and decks, tells the game, and
    /// gives the points back.
    /// </summary>
    /// <returns>How many spells left and how many points came back.</returns>
    internal static (int Spells, int Points) Reset(IActorRef playerActor, Wizard wizard) {
        var bought = BoughtSpells(wizard);
        var points = 0;
        var removed = 0;
        foreach (var spell in bought) {
            try {
                RemoveFromDecks(playerActor, wizard, spell.SpellId);
                if (!wizard.UnlearnSpell(spell.SpellId)) {
                    continue;
                }

                playerActor.Tell(new WIZARD_12_PROTOCOL.MSG_REMOVESPELLFROMBOOK { SpellID = (int) spell.SpellId });
                points += spell.Cost;
                removed++;
            }
            catch (Exception ex) {
                Logger.Error("Respec | Could not take spell {0} from {1}: {2}",
                    Logger.Args(spell.SpellId, wizard.PlayerNameBehavior?.GetWizardName(), ex.Message));
            }
        }

        var newPoints = wizard.MagicSchoolBehavior.TrainingPoints + points;
        wizard.UpdateTrainingPoints(newPoints);
        playerActor.Tell(new WIZARD_12_PROTOCOL.MSG_UPDATETRAINING { TrainingPoints = newPoints });

        lock (s_lock) {
            var trained = Load();
            if (trained.TryGetValue(wizard.CharId, out var spells)) {
                foreach (var spell in bought) {
                    spells.Remove(spell.SpellId);
                }

                Save(trained);
            }
        }

        Logger.Information("Respec | {0} reset their training points: {1} spells left their spellbook, {2} points back, {3} now.",
            Logger.Args(wizard.PlayerNameBehavior?.GetWizardName(), removed, points, newPoints));

        return (removed, points);
    }

    // Every copy of the spell leaves every deck the wizard has, worn or in the backpack.
    private static void RemoveFromDecks(IActorRef playerActor, Wizard wizard, uint spellId) {
        var decks = (wizard.EquipmentBehavior?.EquippedItems ?? []).Cast<CoreObject>()
            .Concat((wizard.InventoryBehavior?.Items ?? []).Cast<CoreObject>());
        foreach (var deck in decks.ToList()) {
            if (deck is null || !CoreObjectFactory.FindBehaviorInstance<DeckBehavior>(deck, out var deckBehavior)) {
                continue;
            }

            var copies = (int) (deckBehavior.m_spellList?.Where(s => s.m_templateID == spellId).Sum(s => (long) s.m_quantity) ?? 0);
            for (var i = 0; i < copies; i++) {
                if (!wizard.RemoveSpellFromDeck(spellId, deck.m_globalID)) {
                    break;
                }

                playerActor.Tell(new WIZARD_12_PROTOCOL.MSG_REMOVESPELLFROMDECK {
                    SpellID = (int) spellId,
                    DeckID = deck.m_globalID,
                    Success = 1
                });
            }
        }
    }

    internal static void Tell(IActorRef playerActor, string text)
        => playerActor.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = text, Modal = 0 });

    // Under s_lock.
    private static Dictionary<ulong, Dictionary<uint, int>> Load() {
        if (s_trained is not null) {
            return s_trained;
        }

        try {
            s_trained = File.Exists(FILE)
                ? JsonSerializer.Deserialize<Dictionary<ulong, Dictionary<uint, int>>>(File.ReadAllText(FILE), s_options) ?? []
                : [];
        }
        catch (Exception ex) {
            Logger.Error("Respec | Could not read {0}: {1}", Logger.Args(FILE, ex.Message));
            s_trained = [];
        }

        return s_trained;
    }

    // Under s_lock.
    private static void Save(Dictionary<ulong, Dictionary<uint, int>> trained) {
        try {
            var temp = $"{FILE}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(trained, s_options));
            File.Move(temp, FILE, overwrite: true);
        }
        catch (Exception ex) {
            Logger.Error("Respec | Could not save {0}: {1}", Logger.Args(FILE, ex.Message));
        }
    }

}
