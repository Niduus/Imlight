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
 *
 * ========================================================================
 * BANK CONFIG
 * ========================================================================
 *
 * PURPOSE:
 * Reads the bank settings of Imlight.ini: the default capacities of a character's bank and of an account's
 * shared bank, the shared jewel cap, the object names of the objects that open the bank and the two settings of
 * an expansion.
 *
 * USAGE EXAMPLE:
 * if (BankConfig.IsEnabled) { ... }
 *
 * NOTE:
 * The capacities have no default. While [Character] MaxBankItems or MaxSharedBankItems is unset or 0 or less,
 * IsEnabled is false and nothing offers the bank.
 * The game's own bank objects (BuiltInObjectNames) always open the bank: the dorm bank WC_RVW-Bank and the six
 * house bank chests HOUSE_BankChest_01 to _06, the only templates of Root.wad whose service text is GUI_UseBank.
 * [Bank] ObjectNames only adds names. Setup keeps an existing ini key as it is, so a list there from an older
 * version cannot drop the chests.
 *
 * TODO:
 *
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/14/2026
 */

using System;
using System.Collections.Generic;
using System.Threading;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Bank;

/// <summary>
/// Settings of the bank, read from the [Character] and [Bank] sections of Imlight.ini.
/// </summary>
internal static class BankConfig {

    private const string MaxBankItemsKey = "Character.MaxBankItems";
    private const string MaxSharedBankItemsKey = "Character.MaxSharedBankItems";
    private const string SharedJewelCapKey = "Bank.SharedJewelCap";
    private const string ObjectNamesKey = "Bank.ObjectNames";
    private const string ExpandElixirAmountKey = "Bank.ExpandElixirAmount";
    private const string MaxBankLimitKey = "Bank.MaxBankItems";
    private const string ChestNpcKey = "Bank.ChestNpc";
    private const string ItemFinderAtBanksKey = "Bank.ItemFinderAtBanks";
    private const uint DormBankTemplateId = 44162;

    private static int s_warnedDisabled;

    /// <summary>
    /// The templates of the game that open the bank (Root.wad, BasicNPCServiceBehavior with GUI_UseBank and a
    /// BankingBehavior): the bank of the dorm, an NPC, and the six bank chests a player places in a house.
    /// </summary>
    internal static readonly IReadOnlyList<string> BuiltInObjectNames = [
        "WC_RVW-Bank",
        "HOUSE_BankChest_01", "HOUSE_BankChest_02", "HOUSE_BankChest_03",
        "HOUSE_BankChest_04", "HOUSE_BankChest_05", "HOUSE_BankChest_06",
    ];

    /// <summary>
    /// The default capacity of a character's bank. Zero when the key is unset or not a positive number.
    /// </summary>
    internal static int MaxBankItems => Math.Max(0, ConfigurationManager.GetValue(MaxBankItemsKey, 0));

    /// <summary>
    /// The default capacity of an account's shared bank. Zero when the key is unset or not a positive number.
    /// </summary>
    internal static int MaxSharedBankItems => Math.Max(0, ConfigurationManager.GetValue(MaxSharedBankItemsKey, 0));

    /// <summary>
    /// The most jewels the shared bank holds. Zero when the key is unset, which keeps jewels out of the shared bank.
    /// </summary>
    internal static int SharedJewelCap => Math.Max(0, ConfigurationManager.GetValue(SharedJewelCapKey, 0));

    /// <summary>
    /// How much capacity one Expand Bank Elixir adds. Zero when the key is unset, which keeps the bank from being expanded.
    /// </summary>
    internal static int ExpandElixirAmount => Math.Max(0, ConfigurationManager.GetValue(ExpandElixirAmountKey, 0));

    /// <summary>
    /// The largest capacity an expansion can reach. Zero when the key is unset, which means no limit.
    /// </summary>
    internal static int MaxBankLimit => Math.Max(0, ConfigurationManager.GetValue(MaxBankLimitKey, 0));

    /// <summary>
    /// The object names of the objects that open the bank: the built-in names and the ones of [Bank] ObjectNames.
    /// </summary>
    internal static IReadOnlySet<string> ObjectNames {
        get {
            var names = new HashSet<string>(BuiltInObjectNames, StringComparer.OrdinalIgnoreCase);
            var configured = ConfigurationManager.GetValue(ObjectNamesKey, string.Empty);
            foreach (var name in configured.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
                names.Add(name);
            }

            return names;
        }
    }

    /// <summary>
    /// The template of the invisible bank that stands on a placed bank chest. Default: the dorm bank WC_RVW-Bank,
    /// which is itself an invisible NPC on the dorm's chest.
    /// </summary>
    internal static uint ChestNpcTemplateId => ConfigurationManager.GetValue(ChestNpcKey, DormBankTemplateId) is var id && id != 0
        ? id
        : DormBankTemplateId;

    /// <summary>
    /// Whether the banks also offer the Item Finder next to the bank. A placed Backpack Butler always offers it.
    /// </summary>
    internal static bool ItemFinderAtBanks => ConfigurationManager.GetValue(ItemFinderAtBanksKey, true);

    /// <summary>
    /// Whether the bank is on: both default capacities are set above 0.
    /// </summary>
    internal static bool IsEnabled {
        get {
            var maxBankItems = MaxBankItems;
            var maxSharedBankItems = MaxSharedBankItems;
            if (maxBankItems > 0 && maxSharedBankItems > 0) {
                return true;
            }

            if (Interlocked.Exchange(ref s_warnedDisabled, 1) == 0) {
                Logger.Warning("[Character] MaxBankItems and MaxSharedBankItems must both be set above 0, so the bank is not offered. MaxBankItems={Bank}, MaxSharedBankItems={Shared}.",
                    Logger.Args(maxBankItems, maxSharedBankItems));
            }

            return false;
        }
    }

}
