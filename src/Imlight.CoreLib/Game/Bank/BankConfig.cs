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
 * shared bank, the shared jewel cap, the object names of the NPCs that open the bank and the two settings of
 * an expansion.
 *
 * USAGE EXAMPLE:
 * if (BankConfig.IsEnabled) { ... }
 *
 * NOTE:
 * The capacities have no default. While [Character] MaxBankItems or MaxSharedBankItems is unset or 0 or less,
 * IsEnabled is false and nothing offers the bank.
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

    private static int s_warnedDisabled;

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
    /// The object names of the NPCs that open the bank.
    /// </summary>
    internal static IReadOnlySet<string> ObjectNames {
        get {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var configured = ConfigurationManager.GetValue(ObjectNamesKey, string.Empty);
            foreach (var name in configured.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
                names.Add(name);
            }

            return names;
        }
    }

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
