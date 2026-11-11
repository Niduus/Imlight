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
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Commands;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.RemoteAdmin;

namespace Imlight.CoreLib.Game.Tools.RemoteAdmin;

/// <summary>
/// Players run their cheats and commands from their own Ahziael (Niduus, 2026-11-11: "when a Hooked Ahziael client wants
/// to activate their cheats or click on buttons on their UI, it sends a ENCRYPTED command to the server"). It goes over
/// the Remote Admin port, not through game chat, so it has no length limit and never shows in chat.
///
/// A player signs in once in Ahziael with their game username and password (op "login", purpose "commands"); the server
/// answers with a key: a name and a random secret, kept here in Config/remote-admin/player-keys.json and on the player's
/// PC. The password is never kept. Every command after that (op "pcmd") carries the key's name, a number that only goes
/// up, the time it was sent and an HMAC-SHA256 signature made with the secret (RemoteAdminWire.SignCommand), so a
/// command can't be made without the secret, changed on the way or sent twice. The server then checks that:
///   - [Overlay] PlayerCommands is on (live);
///   - the account can still log in (not locked, not banned) and the key was used in the last 30 days;
///   - the account's wizard is in the game, connected from the same address the command came from;
///   - it is not more than 10 commands in 5 seconds.
/// The command then runs as that wizard, exactly as if the player had typed it in chat: the server's own rank checks
/// decide what it may do, and the player cheats it switches or presses are only its own.
/// </summary>
public static class PlayerCommands {

    /// <summary>[Overlay] PlayerCommands: players may run their cheats and commands from Ahziael. On unless turned off.</summary>
    public static bool On => ConfigurationManager.GetValue("Overlay.PlayerCommands", true);

    private const int MAX_KEYS_PER_ACCOUNT = 6;
    private const int MAX_KEYS = 4000;
    private static readonly TimeSpan s_unusedFor = TimeSpan.FromDays(30);
    private const int RATE_COUNT = 10;
    private static readonly TimeSpan s_rateWindow = TimeSpan.FromSeconds(5);
    private const int MAX_VALUE = 300;

    private sealed class Key {

        public string Name { get; set; } = "";
        public ulong AccountId { get; set; }
        public string Secret { get; set; } = "";
        public DateTime CreatedUtc { get; set; }
        public DateTime UsedUtc { get; set; }
        public long Number { get; set; }

    }

    private static readonly object s_lock = new();
    private static Dictionary<string, Key> s_keys;
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<DateTime>> s_recent = new();

    private static string FilePath => Path.Combine(RemoteAdminServer.Folder, "player-keys.json");

    /// <summary>A new key for an account that just signed in with its password: its name and secret, for the reply.</summary>
    internal static JsonObject Issue(ulong accountId, string username) {
        var secret = RandomNumberGenerator.GetBytes(32);
        var key = new Key {
            Name = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant(),
            AccountId = accountId,
            Secret = Convert.ToBase64String(secret),
            CreatedUtc = DateTime.UtcNow,
            UsedUtc = DateTime.UtcNow,
        };
        lock (s_lock) {
            Load();
            // An account keeps its newest few keys (one for each PC it plays on).
            foreach (var old in s_keys.Values.Where(k => k.AccountId == accountId).OrderByDescending(k => k.UsedUtc).Skip(MAX_KEYS_PER_ACCOUNT - 1).ToList()) {
                s_keys.Remove(old.Name);
            }

            s_keys[key.Name] = key;
            Save();
        }

        Logger.Information("Player commands: {0} signed in from Ahziael.", Logger.Args(username));
        var reply = RemoteAdminWire.Ok();
        reply["key"] = key.Name;
        reply["secret"] = key.Secret;
        reply["account"] = username;
        reply["name"] = ConfigurationManager.GetValue("Remote Admin.ServerName", "");

        return reply;
    }

    /// <summary>The account's keys are all forgotten (it signs in again on each PC).</summary>
    internal static void Forget(ulong accountId) {
        lock (s_lock) {
            Load();
            if (s_keys.Values.Any(k => k.AccountId == accountId)) {
                foreach (var key in s_keys.Values.Where(k => k.AccountId == accountId).ToList()) {
                    s_keys.Remove(key.Name);
                }

                Save();
            }
        }
    }

    /// <summary>One signed command (op "pcmd") from <paramref name="address"/>: checked, run, and answered.</summary>
    internal static async Task<JsonObject> RunAsync(JsonObject header, string address) {
        var name = (string) header["key"] ?? "";
        var number = (long?) header["n"] ?? 0;
        var at = (long?) header["at"] ?? 0;
        var kind = ((string) header["kind"] ?? "").Trim().ToLowerInvariant();
        var value = ((string) header["value"] ?? "").ReplaceLineEndings(" ").Trim();
        var signature = (string) header["sig"] ?? "";
        if (!On) {
            return RemoteAdminWire.Error("The host has turned off cheats and commands from Ahziael on this server.");
        }

        if (value.Length > MAX_VALUE || name.Length is 0 or > 64 || signature.Length is 0 or > 100) {
            return RemoteAdminWire.Error("That command is too long.");
        }

        Key key;
        lock (s_lock) {
            Load();
            key = s_keys.GetValueOrDefault(name);
        }

        if (key is null || DateTime.UtcNow - key.UsedUtc > s_unusedFor) {
            return SignInAgain("This server doesn't know your sign-in any more. Sign in again in Ahziael (Play > Overlay > My cheats on this server).");
        }

        var expected = RemoteAdminWire.SignCommand(Convert.FromBase64String(key.Secret), name, number, at, kind, value);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature))) {
            Logger.Warning("Player commands: a command from {0} had a wrong signature and was refused.", Logger.Args(address));

            return SignInAgain("That command wasn't signed right. Sign in again in Ahziael.");
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(now - at) > RemoteAdminWire.COMMAND_WINDOW_SECONDS) {
            return RemoteAdminWire.Error("Your PC's clock and the server's are more than 2 minutes apart. Set your clock right (Windows: Settings > Time > Sync now) and try again.");
        }

        lock (s_lock) {
            // Each number is good once: a copy of an old command is refused.
            if (number <= key.Number) {
                return RemoteAdminWire.Error("That command was already sent.");
            }

            key.Number = number;
            key.UsedUtc = DateTime.UtcNow;
            Save();
        }

        var times = s_recent.GetOrAdd(name, _ => new ConcurrentQueue<DateTime>());
        while (times.TryPeek(out var oldest) && DateTime.UtcNow - oldest > s_rateWindow) {
            times.TryDequeue(out _);
        }

        if (times.Count >= RATE_COUNT) {
            return RemoteAdminWire.Error("Slow down a little: at most 10 commands every 5 seconds.");
        }

        times.Enqueue(DateTime.UtcNow);

        var account = AccountCollection.GetAccountForCharacterList(key.AccountId);
        if (account is null || account.IsLocked || account.InfractionHistory?.IsCurrentlyBanned == true) {
            return SignInAgain("Your account can't log in to this server right now.");
        }

        string text;
        switch (kind) {
            case "cheats":
                text = "";
                break;
            case "cheat":
                if (value.Length == 0) {
                    return RemoteAdminWire.Error("Say which cheat: on, off or use, then its name.");
                }

                text = "cheat " + value;
                break;
            case "command":
                text = value.TrimStart('.').Trim();
                if (text.Length == 0) {
                    return RemoteAdminWire.Error("Type a command first.");
                }

                break;
            default:
                return RemoteAdminWire.Error($"This server doesn't know \"{kind}\". Update Ahziael on both PCs.");
        }

        var output = new ConcurrentQueue<string>();
        var found = new TaskCompletionSource<(ulong CharId, bool SameAddress)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new OverlayPlayerCommand(key.AccountId, OverlayGate.Normalize(address), text, output, found);
        foreach (var service in AgentBridge.CommandServices) {
            service.Tell(request);
        }

        if (await Task.WhenAny(found.Task, Task.Delay(2000)) != found.Task || found.Task.Result.CharId == 0) {
            return RemoteAdminWire.Error("Your wizard isn't in the game on this server. Log in to the game first, then try again.");
        }

        var (charId, sameAddress) = found.Task.Result;
        if (!sameAddress) {
            return RemoteAdminWire.Error("Your wizard is playing from another PC. Commands only work from the PC the game runs on.");
        }

        if (text.Length > 0) {
            Logger.Information("Player commands: {0} ran \"{1}\" from Ahziael.", Logger.Args(account.Username, text));
            // The command's answers (they show in the game too).
            for (var i = 0; i < 15; i++) {
                await Task.Delay(100);
                if (!output.IsEmpty && i >= 2) {
                    break;
                }
            }
        }

        var reply = RemoteAdminWire.Ok();
        reply["text"] = string.Join("\n", output);
        if (kind is "cheats" or "cheat") {
            reply["cheats"] = CheatList(charId);
        }

        return reply;
    }

    private static JsonObject SignInAgain(string message) {
        var reply = RemoteAdminWire.Error(message);
        reply["signIn"] = true;

        return reply;
    }

    /// <summary>The player cheats a wizard has, for Ahziael's list.</summary>
    private static JsonArray CheatList(ulong charId) {
        var list = new JsonArray();
        foreach (var cheat in PlayerCheats.Owned(charId)) {
            list.Add(new JsonObject {
                ["number"] = cheat.Number,
                ["name"] = PlayerCheatSwitches.LabelOf(cheat),
                ["on"] = PlayerCheatSwitches.IsOn(charId, cheat),
                ["canSwitch"] = cheat.PlayersSwitch,
                ["canUse"] = cheat.Is("Pressed"),
            });
        }

        return list;
    }

    private static void Load() {
        if (s_keys is not null) {
            return;
        }

        try {
            s_keys = File.Exists(FilePath)
                ? (JsonSerializer.Deserialize<List<Key>>(File.ReadAllText(FilePath)) ?? []).Where(k => k?.Name is { Length: > 0 }).ToDictionary(k => k.Name)
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) {
            Logger.Warning("Player commands: the sign-ins could not be read ({0}); players sign in again.", Logger.Args(ex.Message));
            s_keys = [];
        }
    }

    private static void Save() {
        var cutoff = DateTime.UtcNow - s_unusedFor;
        foreach (var old in s_keys.Values.Where(k => k.UsedUtc < cutoff).ToList()) {
            s_keys.Remove(old.Name);
        }

        foreach (var old in s_keys.Values.OrderByDescending(k => k.UsedUtc).Skip(MAX_KEYS).ToList()) {
            s_keys.Remove(old.Name);
        }

        try {
            Directory.CreateDirectory(RemoteAdminServer.Folder);
            File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(s_keys.Values.ToList()));
            File.Move(FilePath + ".tmp", FilePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Warning("Player commands: the sign-ins could not be saved: {0}", Logger.Args(ex.Message));
        }
    }

}
