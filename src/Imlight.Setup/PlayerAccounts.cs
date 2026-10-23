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
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Imlight.Setup;

/// <summary>
/// A player account as the control panel shows it. The password hash never leaves this class.
/// </summary>
internal sealed record AccountSummary(string Username, string AuthLevel, bool IsLocked, int Characters,
    string? CreationTime, string? LastLoginTime, string AccountId);

/// <summary>
/// Reads and changes player accounts in the server's embedded RavenDB, through its HTTP API.
/// The database only runs while the server does. Documents keep the shape Imlight writes:
/// the same fields as its Account class, enums stored the way it already stores them, and the SHA-512 password hash it checks.
/// </summary>
internal sealed class PlayerAccounts : IDisposable {

    internal static readonly string[] AUTH_LEVELS = ["None", "QualityAssurance", "HallMonitor", "Developer", "Administrator"];

    private const string COLLECTION = "Accounts";

    private readonly HttpClient _http;
    private readonly string _database;

    internal PlayerAccounts(string baseUrl, string database) {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(15) };
        _database = database;
    }

    /// <summary>
    /// The embedded database of an install, or null with a reason when the server uses a remote database.
    /// </summary>
    internal static PlayerAccounts? FromConfig(string configFile, out string? reason) {
        reason = null;
        if (!string.IsNullOrWhiteSpace(IniFile.Get(configFile, "Database", "PlayerDatabaseUrl"))) {
            reason = "This server uses a remote database. Manage its accounts in that database instead.";

            return null;
        }

        var port = IniFile.Get(configFile, "Database", "EmbeddedDatabasePort") ?? "8080";
        var name = IniFile.Get(configFile, "Database", "PlayerDatabaseName") ?? "Playerdata";

        return new PlayerAccounts($"http://127.0.0.1:{port}", name);
    }

    internal async Task<List<AccountSummary>> ListAsync() {
        var results = await QueryAsync($"from {COLLECTION}", null);

        return [.. results.Select(Summarize).OrderBy(a => a.Username, StringComparer.OrdinalIgnoreCase)];
    }

    internal async Task CreateAsync(string username, string password, string authLevel) {
        username = username.Trim();
        if (username.Length == 0 || password.Length == 0) {
            throw new AccountException("Enter a username and a password.");
        }

        var all = await QueryAsync($"from {COLLECTION}", null);
        if (all.Any(a => string.Equals((string?) a["Username"], username, StringComparison.OrdinalIgnoreCase))) {
            throw new AccountException($"There is already an account named {username}.");
        }

        // The server makes the admin account on its first start; it shows the stored shape.
        var template = all.FirstOrDefault()
            ?? throw new AccountException("Start the server once so it can set up its database, then try again.");

        var metadata = new JsonObject { ["@collection"] = COLLECTION };
        if (template["@metadata"]?["Raven-Clr-Type"] is JsonNode clrType) {
            metadata["Raven-Clr-Type"] = clrType.DeepClone();
        }

        var document = new JsonObject {
            ["AccountId"] = (ulong) Random.Shared.NextInt64(1, long.MaxValue),
            ["Username"] = username,
            ["Email"] = "",
            ["PasswordHash"] = HashPassword(password),
            ["AuthLevel"] = AuthLevelValue(template, authLevel),
            ["ChatMode"] = template["ChatMode"]?.GetValueKind() == System.Text.Json.JsonValueKind.String ? "Open" : 0,
            ["CharacterIds"] = new JsonArray(),
            ["InfractionIds"] = new JsonArray(),
            ["CreationTime"] = DateTime.UtcNow.ToString("o"),
            ["LastLoginTime"] = DateTime.MinValue.ToString("o"),
            ["LastLoginMachineId"] = 0,
            ["LastLoginIp"] = null,
            ["IsLocked"] = false,
            ["PurchasedCharacterSlots"] = 0,
            ["@metadata"] = metadata,
        };

        // An id ending in a slash asks the database to number the document itself.
        await PutAsync("accounts/", document);
    }

    internal Task SetPasswordAsync(string username, string password) {
        if (password.Length == 0) {
            throw new AccountException("Enter a new password.");
        }

        return UpdateAsync(username, doc => doc["PasswordHash"] = HashPassword(password));
    }

    /// <summary>
    /// Gives an account a new username. The server finds accounts by their Username field at login and links characters
    /// by AccountId, so changing the field is all it takes: the account keeps its id, its document and its characters.
    /// </summary>
    internal async Task RenameAsync(string username, string newName) {
        newName = newName.Trim();
        if (newName.Length == 0) {
            throw new AccountException("Enter a new username.");
        }

        var all = await QueryAsync($"from {COLLECTION}", null);
        if (all.Any(a => string.Equals((string?) a["Username"], newName, StringComparison.OrdinalIgnoreCase)
                && !string.Equals((string?) a["Username"], username, StringComparison.OrdinalIgnoreCase))) {
            throw new AccountException($"There is already an account named {newName}.");
        }

        await UpdateAsync(username, doc => doc["Username"] = newName);
    }

    internal Task SetAuthLevelAsync(string username, string authLevel)
        => UpdateAsync(username, doc => doc["AuthLevel"] = AuthLevelValue(doc, authLevel));

    internal Task SetLockedAsync(string username, bool locked)
        => UpdateAsync(username, doc => doc["IsLocked"] = locked);

    /// <summary>
    /// Every account's Tamed Drops pity meters (the server's Account.TamedPity): "&lt;boss&gt;:&lt;tier&gt;" to runs filled.
    /// </summary>
    internal async Task<List<(string Username, Dictionary<string, double> Meters)>> TamedPityAsync() {
        var results = await QueryAsync($"from {COLLECTION}", null);

        return [.. results
            .Select(doc => ((string?) doc["Username"] ?? "?", (doc["TamedPity"] as JsonObject ?? [])
                .Where(m => m.Value is JsonValue value && value.TryGetValue<double>(out _))
                .ToDictionary(m => m.Key, m => m.Value!.GetValue<double>())))
            .OrderBy(a => a.Item1, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Empties an account's Tamed Drops pity meters.</summary>
    internal Task ResetTamedPityAsync(string username)
        => UpdateAsync(username, doc => doc["TamedPity"] = new JsonObject());

    private async Task UpdateAsync(string username, Action<JsonObject> change) {
        var results = await QueryAsync($"from {COLLECTION} where Username = $username", new JsonObject { ["username"] = username });
        var document = results.FirstOrDefault() ?? throw new AccountException($"There is no account named {username}.");
        var id = (string?) document["@metadata"]?["@id"] ?? throw new AccountException("The database did not say where this account is stored.");

        change(document);
        await PutAsync(id, document);
    }

    private async Task<List<JsonObject>> QueryAsync(string query, JsonObject? parameters) {
        var body = new JsonObject { ["Query"] = query };
        if (parameters is not null) {
            body["QueryParameters"] = parameters;
        }

        using var response = await SendAsync(() => _http.PostAsJsonAsync($"databases/{Uri.EscapeDataString(_database)}/queries", body));
        var json = await response.Content.ReadFromJsonAsync<JsonObject>();

        return [.. (json?["Results"] as JsonArray ?? []).OfType<JsonObject>()];
    }

    private async Task PutAsync(string id, JsonObject document) {
        // Keep only the metadata a client may write; the rest is the database's own bookkeeping.
        if (document["@metadata"] is JsonObject metadata) {
            foreach (var key in metadata.Select(p => p.Key).ToList()) {
                if (key is not ("@collection" or "Raven-Clr-Type")) {
                    metadata.Remove(key);
                }
            }
        }

        var content = new StringContent(document.ToJsonString(), Encoding.UTF8, "application/json");
        using var _ = await SendAsync(() => _http.PutAsync($"databases/{Uri.EscapeDataString(_database)}/docs?id={Uri.EscapeDataString(id)}", content));
    }

    private static async Task<HttpResponseMessage> SendAsync(Func<Task<HttpResponseMessage>> send) {
        HttpResponseMessage response;
        try {
            response = await send();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) {
            throw new AccountException("The server's database is not running. Start the server, wait until it is ready, then try again.");
        }

        if (!response.IsSuccessStatusCode) {
            var detail = await response.Content.ReadAsStringAsync();
            response.Dispose();
            throw new AccountException($"The database refused the change ({(int) response.StatusCode}). {FirstLine(detail)}");
        }

        return response;
    }

    private static AccountSummary Summarize(JsonObject doc) {
        var level = doc["AuthLevel"];
        var levelName = level?.GetValueKind() == System.Text.Json.JsonValueKind.Number
            ? AUTH_LEVELS.ElementAtOrDefault(level.GetValue<int>()) ?? level.ToString()
            : (string?) level ?? "None";

        return new AccountSummary(
            (string?) doc["Username"] ?? "?",
            levelName,
            doc["IsLocked"]?.GetValueKind() == System.Text.Json.JsonValueKind.True,
            (doc["CharacterIds"] as JsonArray)?.Count ?? 0,
            (string?) doc["CreationTime"],
            (string?) doc["LastLoginTime"],
            // The number the server knows the account by (characters, gifts and logs use it), not the document's own id.
            doc["AccountId"] is JsonValue id ? id.ToString() : "?");
    }

    /// <summary>
    /// The value to store for an auth level, as a number or a name to match how the server already stores it.
    /// </summary>
    private static JsonNode AuthLevelValue(JsonObject sample, string authLevel) {
        var index = Array.FindIndex(AUTH_LEVELS, l => l.Equals(authLevel, StringComparison.OrdinalIgnoreCase));
        if (index < 0) {
            throw new AccountException($"Unknown account level {authLevel}.");
        }

        return sample["AuthLevel"]?.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? JsonValue.Create(AUTH_LEVELS[index])
            : JsonValue.Create(index);
    }

    /// <summary>
    /// The same hash the server checks at login: SHA-512 of the UTF-8 password, in base64.
    /// </summary>
    internal static string HashPassword(string password)
        => Convert.ToBase64String(SHA512.HashData(Encoding.UTF8.GetBytes(password)));

    private static string FirstLine(string text) {
        var line = text.Split('\n').FirstOrDefault()?.Trim() ?? "";

        return line.Length > 200 ? line[..200] : line;
    }

    public void Dispose()
        => _http.Dispose();

}

/// <summary>
/// An account change that could not be made. The message is written for the person using the panel.
/// </summary>
internal sealed class AccountException(string message) : Exception(message);
