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
using System.IO;
using System.Linq;
using Imlight.Common;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Character;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Makes True Form what Niduus asked for (2026-11-19: "He is to be max level, Myth with the best 190 Myth gear statistically
/// speaking in the game"), before he logs in: the server's top level, Myth, Shadow Magic open (for the shadow spells), the
/// title "True Form", and per slot the best piece of gear a Myth wizard of his level can get in the real game, by its stats
/// (BotGear: every hat, robe, boots, wand, athame, amulet, ring and deck a shop, quest or boss gives; never Crowns-only,
/// retired, dev or test gear). His gear is scored for a wizard who hits, blades and keeps the team up (the Blading set's
/// weights: damage, pierce and resist all count). Done again whenever the top level changes or a better piece appears.
/// </summary>
internal static class TrueFormMaker {

    /// <summary>The title shown under his name.</summary>
    internal const string TITLE = "True Form";

    private const string DRESSED_FILE = "dressed.txt";

    /// <summary>Makes him ready (he must be logged out); writes what it did to the server log.</summary>
    internal static void Prepare(ulong charId) {
        var wizard = WizardCollection.GetCharacter(charId);
        if (wizard is null) {
            Logger.Warning("True Form: his wizard {0} was not found to make ready.", Logger.Args(charId));
            return;
        }

        try {
            if (wizard.MagicSchoolBehavior.MagicSchool != MagicSchool.Myth) {
                wizard.SetSchool(MagicSchool.Myth);
            }

            var top = (byte) Math.Clamp(MagicLevelsConfig.MaxLevel, 1, 255);
            if (wizard.MagicSchoolBehavior.Level != top) {
                wizard.SetLevel(top);
                wizard.FitExperienceToLevel();
                Logger.Information("True Form: made level {0}.", Logger.Args(top));
            }

            if (wizard.GameStats is { } stats) {
                if (!stats.m_shadowMagicUnlocked || stats.m_shadowPipSlots < 3) {
                    wizard.UnlockShadowMagic(3);
                }

                // Full health and mana to start with.
                wizard.UpdateHealth(Math.Max(1, stats.m_baseHitpoints + stats.m_bonusHitpoints));
                wizard.UpdateMana(Math.Max(1, stats.m_baseMana + stats.m_bonusMana));
            }

            if ((string) wizard.PlayerNameBehavior?.BadgeTitle != TITLE) {
                wizard.SetBadgeOverride(TITLE);
            }

            Dress(wizard, top);
        }
        catch (Exception e) {
            Logger.Warning("True Form: could not make him ready: {0}", Logger.Args(e));
        }
    }

    // The best gear per slot for a Myth wizard of his level; put on when what he wears differs.
    private static void Dress(Wizard wizard, int level) {
        // The gear list is read on the first ask, in the background: up to a minute for it.
        for (var i = 0; i < 120 && BotGear.Catalog.Count == 0; i++) {
            System.Threading.Thread.Sleep(500);
        }

        if (BotGear.Catalog.Count == 0) {
            Logger.Warning("True Form: the gear list could not be read; he keeps what he wears.");
            return;
        }

        var best = BotGear.BestFor("Myth", level, BotStyle.Backup, p => !p.CrownsOnly && !p.Retired && !BotGear.Forbidden(p.Id) && !BotGear.PartlyRead(p.Id));
        var outfit = string.Join(",", best.OrderBy(b => b.Key).Select(b => b.Value.Id));
        var marker = Path.Combine(UltimusSeat.TrueForm.Folder, DRESSED_FILE);
        var worn = BotGear.Slots.Select(s => (Slot: s, Item: wizard.EquipmentBehavior?.GetItemInSlot(s))).ToList();
        var wearsIt = best.All(b => worn.FirstOrDefault(w => w.Slot == b.Key).Item is { } item && (uint) Math.Min((ulong) item.m_templateID, uint.MaxValue) == b.Value.Id);
        if (wearsIt && File.Exists(marker) && File.ReadAllText(marker).Trim() == outfit) {
            return;
        }

        var put = 0;
        foreach (var (slot, piece) in best) {
            try {
                if (wizard.EquipmentBehavior?.GetItemInSlot(slot) is { } on && (uint) Math.Min((ulong) on.m_templateID, uint.MaxValue) == piece.Id) {
                    continue;
                }

                if (!wizard.TryCreateItem(piece.Id, out var item) || !wizard.AddInitializedItemToInventory(item, ignoreLimit: true)) {
                    Logger.Warning("True Form: could not get {0} ({1}).", Logger.Args(piece.Name, slot));
                    continue;
                }

                if (wizard.InventoryToEquipmentTransfer(item.m_globalID.Full, out _, out _)) {
                    put++;
                    Logger.Information("True Form: wears {0} ({1}, level {2}).", Logger.Args(piece.Name, slot, piece.Level));
                }
            }
            catch (Exception e) {
                Logger.Warning("True Form: could not put on {0}: {1}", Logger.Args(piece.Name, e.Message));
            }
        }

        try {
            Directory.CreateDirectory(UltimusSeat.TrueForm.Folder);
            File.WriteAllText(marker, outfit);
            File.WriteAllLines(Path.Combine(UltimusSeat.TrueForm.Folder, "gear.txt"), BotGear.Describe(wizard));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            Logger.Debug("True Form: could not write his gear list: {0}", Logger.Args(e.Message));
        }

        Logger.Information("True Form: dressed in the best level {0} Myth gear ({1} pieces put on).", Logger.Args(level, put));
    }

}
