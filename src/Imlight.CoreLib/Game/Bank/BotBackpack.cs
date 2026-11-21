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
using Imlight.Common;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Bank;

/*
 * ========================================================================
 * BOT BACKPACK
 * ========================================================================
 *
 * PURPOSE:
 * Every bot the server plays gets a bigger backpack (Niduus, 2026-11-21: "give all bots 500 backpack space"):
 * Ultimus, the TCO7, the Quest Team, the Kitharas Family, True Form, the questing street bots and the Bot Players,
 * and any new bot later, since they are all found the same way (OnlineList.IsBotWizard: a seat's wizard, a Quest
 * Team wizard, or a Bot Players character id). The size is [Character] BotBackpack (500), read live; a player's
 * backpack is not touched.
 *
 * A wizard once seen as a bot keeps the bot size for the rest of the server run, so the backpack does not shrink for
 * the few seconds a bot update swaps the bots' code.
 */
public static class BotBackpack {

    private const string Key = "Character.BotBackpack";
    private const int DefaultSize = 500;

    private static readonly ConcurrentDictionary<ulong, byte> s_bots = new();

    /// <summary>The bot backpack size from Imlight.ini; 0 or less turns it off.</summary>
    public static int Size => ConfigurationManager.GetValue(Key, DefaultSize);

    /// <summary>The smallest backpack this wizard gets: the bot size for a bot, else 0 (no change).</summary>
    public static int SizeFor(Wizard wizard) {
        if (wizard is null || !IsBot(wizard)) {
            return 0;
        }

        return Math.Max(0, Size);
    }

    private static bool IsBot(Wizard wizard) {
        var charId = wizard.CharId;
        if (wizard.IsBot) {
            return true;
        }

        if (charId == 0) {
            return false;
        }

        if (s_bots.ContainsKey(charId)) {
            return true;
        }

        if (OnlineList.IsBotWizard(charId)) {
            s_bots.TryAdd(charId, 0);

            return true;
        }

        return false;
    }

}
