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
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Bank;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Codes;

/// <summary>One item a code gives.</summary>
internal sealed class PromoCodeItem {
    public ulong ItemId { get; set; }
    /// <summary>The name the Manager showed when it was picked; the game's name is used when it is empty.</summary>
    public string ItemName { get; set; } = "";
    public int Count { get; set; } = 1;
}

/// <summary>One code: its text, when it can be redeemed, how often per account, and what it gives.</summary>
internal sealed class PromoCode {
    /// <summary>What the player types; case does not matter.</summary>
    public string Code { get; set; } = "";
    public bool On { get; set; } = true;
    /// <summary>"yyyy-MM-dd HH:mm" in the server's time, or empty: no start.</summary>
    public string Starts { get; set; } = "";
    /// <summary>"yyyy-MM-dd HH:mm" in the server's time, or empty: it never ends.</summary>
    public string Ends { get; set; } = "";
    /// <summary>How many times one account can redeem it; 0 means no limit.</summary>
    public int UsesPerAccount { get; set; } = 1;
    public int Gold { get; set; }
    public int Crowns { get; set; }
    public List<PromoCodeItem> Items { get; set; } = [];
    public string Note { get; set; } = "";
}

/// <summary>What a code gave that a wizard has not received yet (redeemed at character select).</summary>
internal sealed class PromoCodeReward {
    public string Code { get; set; } = "";
    public int Gold { get; set; }
    public int Crowns { get; set; }
    public List<PromoCodeItem> Items { get; set; } = [];
}

internal sealed class PromoCodeAccount {
    public string Username { get; set; } = "";
    /// <summary>Times each code (upper case) was redeemed.</summary>
    public Dictionary<string, int> Used { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<PromoCodeReward> Waiting { get; set; } = [];
}

internal sealed class PromoCodeFile {
    public List<PromoCode> Codes { get; set; } = [];
}

internal sealed class PromoRedemptionFile {
    /// <summary>By account ID.</summary>
    public Dictionary<string, PromoCodeAccount> Accounts { get; set; } = [];
}

/// <summary>
/// Promo codes (Niduus, 2026-10-23: "Make a code section in the manager under server. This will allow the creation of
/// codes and their time limits. So if I type in HYPER it will redeem the code and say "Success on redeeming: [reward]"").
/// The Manager's Codes card writes codes.json next to the server; it is read again whenever it changes. Who redeemed
/// what is kept in code-redemptions.json. A code redeemed in game is given to that wizard at once; one redeemed at
/// character select is given to the next wizard of the account that enters the game.
/// </summary>
internal static class PromoCodes {

    internal const string FILE = "codes.json";
    internal const string REDEMPTIONS_FILE = "code-redemptions.json";

    // The Hyper Elixir (ObjectData/Elixirs/Elixir-043.xml, WizardElixirs_00000088): the example code with no file.
    internal const ulong HYPER_ELIXIR = 1441704;

    private static readonly JsonSerializerOptions s_options = new() {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        WriteIndented = true,
    };

    private static readonly object s_lock = new();
    private static PromoCodeFile s_codes = Defaults();
    private static DateTime s_codesWritten = DateTime.MinValue;
    private static PromoRedemptionFile s_redemptions;
    private static DateTime s_redemptionsWritten = DateTime.MinValue;

    private static PromoCodeFile Defaults() => new() {
        Codes = [new PromoCode {
            Code = "HYPER", UsesPerAccount = 1, Note = "Example code",
            Items = [new PromoCodeItem { ItemId = HYPER_ELIXIR, ItemName = "Hyper Elixir", Count = 1 }],
        }],
    };

    /// <summary>The codes as codes.json has them now (read again when the file changed).</summary>
    internal static List<PromoCode> Codes {
        get {
            try {
                var written = File.Exists(FILE) ? File.GetLastWriteTimeUtc(FILE) : DateTime.MinValue;
                if (written != s_codesWritten) {
                    lock (s_lock) {
                        if (written != s_codesWritten) {
                            s_codes = written == DateTime.MinValue
                                ? Defaults()
                                : JsonSerializer.Deserialize<PromoCodeFile>(File.ReadAllText(FILE), s_options) ?? new();
                            s_codes.Codes ??= [];
                            s_codesWritten = written;
                            Logger.Information("Codes | {0}: {1}", Logger.Args(
                                written == DateTime.MinValue ? "No codes.json, using the example" : $"Read {FILE}",
                                string.Join(", ", s_codes.Codes.Select(c => c.Code))));
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) {
                // The app may still be writing the file; it is read again the next time it is asked for.
                Logger.Debug("Codes | Could not read {0} yet: {1}", Logger.Args(FILE, ex.Message));
            }

            return s_codes.Codes;
        }
    }

    /// <summary>
    /// Redeems a code for an account. With a wizard (in game) the rewards are given now; without one (character
    /// select) they wait for the next wizard of the account that enters the game.
    /// </summary>
    /// <returns>True when it was redeemed. The answer says so ("Success on redeeming: 1 Hyper Elixir") or why not.</returns>
    internal static bool Redeem(Account account, string typed, Wizard wizard, IActorRef session, out string answer) {
        var text = (typed ?? "").Trim();
        if (text.Length == 0) {
            answer = "Type a code first.";

            return false;
        }

        if (account is null) {
            answer = "Codes cannot be redeemed right now. Try again in a moment.";

            return false;
        }

        var code = Codes.FirstOrDefault(c => string.Equals((c.Code ?? "").Trim(), text, StringComparison.OrdinalIgnoreCase));
        var key = text.ToUpperInvariant();
        if (code is null || !code.On) {
            answer = $"\"{text}\" is not a code that works right now.";
            Logger.Information("Codes | {0} tried \"{1}\": {2}.", Logger.Args(account.Username, text, code is null ? "no such code" : "it is off"));

            return false;
        }

        var now = DateTime.Now;
        if (When(code.Starts) is { } starts && now < starts) {
            answer = $"The code {key} can be redeemed from {Shown(starts)}.";
            Logger.Information("Codes | {0} tried {1} before it starts ({2}).", Logger.Args(account.Username, key, code.Starts));

            return false;
        }

        if (When(code.Ends) is { } ends && now > ends) {
            answer = $"The code {key} ended on {Shown(ends)}.";
            Logger.Information("Codes | {0} tried {1} after it ended ({2}).", Logger.Args(account.Username, key, code.Ends));

            return false;
        }

        var reward = new PromoCodeReward {
            Code = key, Gold = Math.Max(0, code.Gold), Crowns = Math.Max(0, code.Crowns),
            Items = [.. (code.Items ?? []).Where(i => i is not null && i.ItemId > 0 && i.Count > 0)
                .Select(i => new PromoCodeItem { ItemId = i.ItemId, ItemName = i.ItemName ?? "", Count = Math.Min(i.Count, 999) })],
        };

        lock (s_lock) {
            var redemptions = Redemptions();
            var id = account.AccountId.ToString(CultureInfo.InvariantCulture);
            if (!redemptions.Accounts.TryGetValue(id, out var entry)) {
                entry = new PromoCodeAccount();
                redemptions.Accounts[id] = entry;
            }

            entry.Username = account.Username ?? "";
            entry.Used ??= new(StringComparer.OrdinalIgnoreCase);
            entry.Waiting ??= [];
            var used = entry.Used.GetValueOrDefault(key);
            if (code.UsesPerAccount > 0 && used >= code.UsesPerAccount) {
                answer = code.UsesPerAccount == 1
                    ? $"This account already redeemed {key}."
                    : $"This account already redeemed {key} {used} times, the most it can.";
                Logger.Information("Codes | {0} tried {1} again ({2} of {3} uses).", Logger.Args(account.Username, key, used, code.UsesPerAccount));

                return false;
            }

            entry.Used[key] = used + 1;
            if (wizard is null) {
                entry.Waiting.Add(reward);
            }

            SaveRedemptions(redemptions);
        }

        var given = wizard is null ? Describe(reward) : Give(account, wizard, session, reward);
        answer = $"Success on redeeming: {given}" + (wizard is null ? ". It is given to the next wizard you play." : "");
        Logger.Information("Codes | {0} redeemed {1}{2}: {3}.", Logger.Args(account.Username, key,
            wizard is null ? " at character select" : $" in game ({wizard.CharId})", given));

        return true;
    }

    /// <summary>Gives a wizard that enters the game what the account's codes from character select are waiting to give.</summary>
    internal static List<string> DeliverWaiting(Account account, Wizard wizard, IActorRef session) {
        var notes = new List<string>();
        if (account is null || wizard is null) {
            return notes;
        }

        List<PromoCodeReward> waiting;
        lock (s_lock) {
            var redemptions = Redemptions();
            if (!redemptions.Accounts.TryGetValue(account.AccountId.ToString(CultureInfo.InvariantCulture), out var entry)
                || entry.Waiting is not { Count: > 0 }) {
                return notes;
            }

            waiting = [.. entry.Waiting];
            entry.Waiting.Clear();
            SaveRedemptions(redemptions);
        }

        foreach (var reward in waiting) {
            var given = Give(account, wizard, session, reward);
            notes.Add($"The code {reward.Code} gave you: {given}.");
            Logger.Information("Codes | {0} received {1} from character select: {2}.", Logger.Args(wizard.CharId, reward.Code, given));
        }

        return notes;
    }

    private static string Give(Account account, Wizard wizard, IActorRef session, PromoCodeReward reward) {
        var parts = new List<string>();
        foreach (var line in reward.Items) {
            var name = NameOf(line);
            var given = 0;
            var lost = false;
            for (var i = 0; i < line.Count; i++) {
                if (!wizard.TryCreateItem(line.ItemId, out var item)) {
                    break;
                }

                var placement = ItemPlacement.Place(wizard, item);
                ItemPlacement.Announce(session, wizard, item, placement);
                if (placement == Placement.Lost) {
                    lost = true;

                    break;
                }

                given++;
            }

            if (given > 0) {
                parts.Add($"{given} {name}");
            }

            if (given < line.Count) {
                parts.Add(lost ? $"no room for {line.Count - given} {name}" : $"{name} could not be made ({line.ItemId})");
            }
        }

        if (reward.Gold > 0) {
            LootGranter.UpdateWizardGold(session, wizard, reward.Gold);
            parts.Add($"{reward.Gold:N0} Gold");
        }

        if (reward.Crowns > 0) {
            account.SetCrowns(account.Crowns + reward.Crowns);
            session.Tell(new WIZARD_12_PROTOCOL.MSG_CROWNBALANCE {
                Failure = 0,
                TotalCrowns = account.Crowns,
                CharacterID = wizard.CharId,
                CacheBalanceForCSSegmentation = 1
            });
            parts.Add($"{reward.Crowns:N0} Crowns");
        }

        return parts.Count == 0 ? "nothing (this code gives nothing yet)" : string.Join(", ", parts);
    }

    private static string Describe(PromoCodeReward reward) {
        var parts = reward.Items.Select(i => $"{i.Count} {NameOf(i)}").ToList();
        if (reward.Gold > 0) {
            parts.Add($"{reward.Gold:N0} Gold");
        }

        if (reward.Crowns > 0) {
            parts.Add($"{reward.Crowns:N0} Crowns");
        }

        return parts.Count == 0 ? "nothing (this code gives nothing yet)" : string.Join(", ", parts);
    }

    private static string NameOf(PromoCodeItem line) {
        if (!string.IsNullOrWhiteSpace(line.ItemName)) {
            return line.ItemName.Trim();
        }

        try {
            if (CoreObjectFactory.GetCoreTemplate(line.ItemId) is WizItemTemplate template) {
                string key = template.m_displayName;
                var shown = string.IsNullOrEmpty(key) ? "" : Locale.GetEnglishName(key);
                if (!string.IsNullOrWhiteSpace(shown)) {
                    return shown;
                }
            }
        }
        catch (Exception ex) {
            Logger.Debug("Codes | No name for item {0}: {1}", Logger.Args(line.ItemId, ex.Message));
        }

        return $"item {line.ItemId}";
    }

    private static DateTime? When(string text)
        => !string.IsNullOrWhiteSpace(text) && DateTime.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var when)
            ? when
            : null;

    private static string Shown(DateTime when) => when.ToString("MMMM d, yyyy h:mm tt", CultureInfo.InvariantCulture);

    // Under s_lock. Read again when the app changed the file (it can forget who redeemed a code).
    private static PromoRedemptionFile Redemptions() {
        try {
            var written = File.Exists(REDEMPTIONS_FILE) ? File.GetLastWriteTimeUtc(REDEMPTIONS_FILE) : DateTime.MinValue;
            if (s_redemptions is null || written != s_redemptionsWritten) {
                s_redemptions = written == DateTime.MinValue
                    ? new()
                    : JsonSerializer.Deserialize<PromoRedemptionFile>(File.ReadAllText(REDEMPTIONS_FILE), s_options) ?? new();
                s_redemptions.Accounts ??= [];
                s_redemptionsWritten = written;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) {
            Logger.Error("Codes | Could not read {0}: {1}", Logger.Args(REDEMPTIONS_FILE, ex.Message));
            s_redemptions ??= new();
        }

        return s_redemptions;
    }

    private static void SaveRedemptions(PromoRedemptionFile redemptions) {
        try {
            var temp = $"{REDEMPTIONS_FILE}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(redemptions, s_options));
            File.Move(temp, REDEMPTIONS_FILE, overwrite: true);
            s_redemptionsWritten = File.GetLastWriteTimeUtc(REDEMPTIONS_FILE);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Error("Codes | Could not save {0}: {1}", Logger.Args(REDEMPTIONS_FILE, ex.Message));
        }
    }

}
