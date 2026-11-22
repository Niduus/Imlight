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
using Imlight.Common;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Character;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Makes Zoom what Niduus asked for (2026-11-22: "he is to replicate True Form, so he is at max level and able to one shot
/// all enemies"), before he logs in, as True Form is made (TrueFormMaker): the server's top level, Storm, Shadow Magic open,
/// the title "Quest Scout", full health and mana, the best top-level Storm gear for hitting (BotStyle.GlassCannon), and every
/// spell in his book (TrueFormSpells), so the trainers have nothing left to teach him and his quests never wait on training.
/// His school and level stay as they are made: his gear is never his reason to stop.
/// </summary>
internal static class ZoomMaker {

    /// <summary>The title shown under his name.</summary>
    internal const string TITLE = "Quest Scout";

    /// <summary>Zoom's school (his seat's).</summary>
    internal static MagicSchool School => (UltimusSeat.Current.IsScout ? UltimusSeat.Current : UltimusSeat.Zoom).School;

    /// <summary>Makes him ready (he must be logged out); writes what it did to the server log.</summary>
    internal static void Prepare(ulong charId) {
        // Zoom or Savitar: the scout seat this team is (StartTeam runs inside it).
        var me = UltimusSeat.Current.IsScout ? UltimusSeat.Current : UltimusSeat.Zoom;
        var wizard = WizardCollection.GetCharacter(charId);
        if (wizard is null) {
            Logger.Warning(me.Name + ": his wizard {0} was not found to make ready.", Logger.Args(charId));
            return;
        }

        lock (TrueFormMaker.Gate) {
            // The gear and spell files are written to his own folder (TrueFormMaker.MakerFolder reads the seat).
            using var seat = UltimusSeat.Use(me);
            try {
                if (wizard.MagicSchoolBehavior.MagicSchool != School) {
                    wizard.SetSchool(School);
                    Logger.Information(me.Name + ": now a {0} wizard.", Logger.Args(School));
                }

                var top = (byte) Math.Clamp(MagicLevelsConfig.MaxLevel, 1, 255);
                if (wizard.MagicSchoolBehavior.Level != top) {
                    wizard.SetLevel(top);
                    wizard.FitExperienceToLevel();
                    Logger.Information(me.Name + ": made level {0}.", Logger.Args(top));
                }

                if (wizard.GameStats is { } stats) {
                    if (!stats.m_shadowMagicUnlocked || stats.m_shadowPipSlots < 3) {
                        wizard.UnlockShadowMagic(3);
                    }

                    wizard.UpdateHealth(Math.Max(1, stats.m_baseHitpoints + stats.m_bonusHitpoints));
                    wizard.UpdateMana(Math.Max(1, stats.m_baseMana + stats.m_bonusMana));
                }

                if ((string) wizard.PlayerNameBehavior?.BadgeTitle != TITLE) {
                    wizard.SetBadgeOverride(TITLE);
                }

                TrueFormMaker.Dress(wizard, top, School.ToString(), BotStyle.GlassCannon, School.ToString());
                // Every socket of what he wears gets the best jewel there is for his hitting setup, picked by the shared
                // jewel picker every bot uses (BotJewelPicker), as True Form's are (Niduus, 2026-11-22: "Zoom has the slots
                // open but nothing in them").
                TrueFormJewels.Fill(wizard, School.ToString(), BotStyle.GlassCannon);
                // Every Storm spell and enchantment (ZoomSpells); when the game's spells are still being read, his director
                // gives them as soon as they are (never the wait-and-give-up of True Form's maker).
                ZoomSpells.Ensure(wizard);
                Directory.CreateDirectory(me.Folder);
                Logger.Information(me.Name + ": ready as a level {0} {1} quest scout.", Logger.Args(top, School));
            }
            catch (Exception e) {
                Logger.Warning(me.Name + ": could not make him ready: {0}", Logger.Args(e));
            }
        }
    }

}
