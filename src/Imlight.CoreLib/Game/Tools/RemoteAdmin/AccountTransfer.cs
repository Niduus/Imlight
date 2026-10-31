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

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Imlight.RemoteAdmin;

/// <summary>
/// A whole player account in one file, to move it from one Imlight server to another (Niduus, 2026-10-31: "Allow for
/// players using the Ahziael application whilst in another server, to copy their player data for easy transfer from one
/// to another"). The file holds the account and every document of the player database that belongs to it or to its
/// characters: the wizards, their items, bank, pets, snacks, reagents, quests, houses and attics. The same file builds
/// into the server (a player downloads their own account over Remote Admin) and into Ahziael (the host imports it), so
/// the two always agree.
///
/// Importing never touches anything already on the server: the account, its characters and every item, pet and house
/// get new numbers and new documents, and the account comes in as a plain player whatever rank it had.
/// </summary>
public static class AccountTransfer {

    public const string FORMAT = "Ahziael account";
    public const int FORMAT_VERSION = 1;
    public const string EXTENSION = ".ahziaelaccount";

    public const string ACCOUNTS = "Accounts";
    public const string WIZARDS = "Wizards";

    /// <summary>Each collection that holds a player's things, and the field naming its owner (a character, or the account for the shared bank).</summary>
    private static readonly (string Collection, string Owner)[] s_owned = [
        (WIZARDS, "AccountId"),
        ("WizardItems", "m_characterId"),
        ("WizardPetSnacks", "m_characterId"),
        ("WizardReagents", "m_characterId"),
        ("WizardPets", "CharId"),
        ("QuestInstances", "OwnerCharId"),
        ("DynamicMod", "CharId"),
        ("Houses", "OwnerCharId"),
        ("Attics", "CharId"),
    ];

    /// <summary>The numbers that name one thing on a server, by collection. They get new values on import.</summary>
    private static readonly Dictionary<string, string[]> s_identities = new() {
        [ACCOUNTS] = ["AccountId"],
        [WIZARDS] = ["CharId"],
        ["WizardItems"] = ["m_globalID"],
        ["WizardPetSnacks"] = ["m_globalID"],
        ["WizardReagents"] = ["m_globalID"],
        ["WizardPets"] = ["PetGlobalId"],
        ["QuestInstances"] = ["ID"],
        ["Houses"] = ["LotGid"],
    };

    // Template ids, counts and the like are 32-bit. Ids the server makes are random 64-bit numbers, so a number this big
    // that equals one of them is that id, wherever it is written.
    private const ulong SMALLEST_ID = 1UL << 32;

    private const int MAX_DOCUMENTS = 250_000;

    // ---------------------------------------------------------------------
    // Export
    // ---------------------------------------------------------------------

    /// <summary>
    /// Everything of one account, as the file's JSON. <paramref name="characters"/> describes its wizards for the person
    /// importing it (name, school, level), when the caller can tell.
    /// </summary>
    public static async Task<JsonObject> ExportAsync(RavenHttp database, string username, string serverName, JsonArray? characters) {
        var account = (await database.QueryAsync($"from {ACCOUNTS} where Username = $username", new JsonObject { ["username"] = username }))
            .FirstOrDefault(a => string.Equals((string?) a["Username"], username, StringComparison.OrdinalIgnoreCase))
            ?? throw new AccountTransferException($"There is no account named {username} on this server.");
        var accountId = account["AccountId"]?.DeepClone() ?? throw new AccountTransferException("That account has no id.");

        var documents = new List<JsonObject> { account };
        var wizards = await database.QueryAsync($"from {WIZARDS} where AccountId = $id", new JsonObject { ["id"] = accountId.DeepClone() });
        documents.AddRange(wizards);

        // Owners: the account (its shared bank) and every character it lists or that names it.
        var owners = new Dictionary<string, JsonNode> { [NumberText(accountId)] = accountId };
        foreach (var id in (account["CharacterIds"] as JsonArray ?? []).Concat(wizards.Select(w => w["CharId"]))) {
            if (id is JsonValue && NumberText(id) is { Length: > 0 } text) {
                owners.TryAdd(text, id.DeepClone());
            }
        }

        var ownerList = new JsonArray([.. owners.Values.Select(v => v.DeepClone())]);
        foreach (var (collection, owner) in s_owned.Where(o => o.Collection != WIZARDS)) {
            documents.AddRange(await database.QueryAsync($"from {collection} where {owner} in ($owners)",
                new JsonObject { ["owners"] = ownerList.DeepClone() }));
        }

        var list = new JsonArray();
        var counts = new Dictionary<string, int>();
        foreach (var document in documents) {
            var metadata = document["@metadata"] as JsonObject;
            var collection = (string?) metadata?["@collection"] ?? "";
            var id = (string?) metadata?["@id"] ?? "";
            document["@metadata"] = KeptMetadata(metadata);
            if (collection == ACCOUNTS) {
                ForgetWhereFrom(document);
            }

            counts[collection] = counts.GetValueOrDefault(collection) + 1;
            list.Add(new JsonObject { ["Collection"] = collection, ["Id"] = id, ["Data"] = document });
        }

        return new JsonObject {
            ["Format"] = FORMAT,
            ["FormatVersion"] = FORMAT_VERSION,
            ["MadeWith"] = "Ahziael",
            ["Made"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ["Server"] = serverName,
            ["Account"] = (string?) account["Username"] ?? username,
            ["Characters"] = characters ?? [],
            ["Counts"] = new JsonObject([.. counts.Select(c => KeyValuePair.Create(c.Key, (JsonNode?) c.Value))]),
            ["Documents"] = list,
        };
    }

    public static byte[] ToBytes(JsonObject file) => Encoding.UTF8.GetBytes(file.ToJsonString());

    // ---------------------------------------------------------------------
    // Reading a file
    // ---------------------------------------------------------------------

    /// <summary>What an account file holds, checked: one account, and only things that belong to it.</summary>
    public sealed record AccountFile(string Username, string Server, string Made, List<string> Characters,
        Dictionary<string, int> Counts, bool HasPassword, List<(string Collection, JsonObject Data)> Documents);

    /// <summary>Reads and checks an account file. Throws <see cref="AccountTransferException"/> with the reason when it can't be used.</summary>
    public static AccountFile Read(byte[] bytes) {
        JsonObject root;
        try {
            root = JsonNode.Parse(bytes) as JsonObject ?? throw new JsonException();
        }
        catch (JsonException) {
            throw new AccountTransferException("That is not an account file. Pick the file Ahziael saved with \"Download my account\".");
        }

        if ((string?) root["Format"] != FORMAT) {
            throw new AccountTransferException((string?) root["Format"] == "Ahziael customs"
                ? "That is a customs file. Open it on Create > Share instead."
                : "That is not an account file. Pick the file Ahziael saved with \"Download my account\".");
        }

        if (root["FormatVersion"] is not JsonValue version || !version.TryGetValue<int>(out var number) || number > FORMAT_VERSION) {
            throw new AccountTransferException("That account file was made by a newer Ahziael. Update Ahziael, then try again.");
        }

        var allowed = s_owned.Select(o => o.Collection).Append(ACCOUNTS).ToHashSet(StringComparer.Ordinal);
        var documents = new List<(string, JsonObject)>();
        foreach (var entry in root["Documents"] as JsonArray ?? []) {
            var collection = (string?) entry?["Collection"] ?? "";
            if (!allowed.Contains(collection) || entry?["Data"] is not JsonObject data) {
                throw new AccountTransferException($"The file holds something that is not part of a player account ({collection}), so Ahziael won't import it.");
            }

            documents.Add((collection, data));
        }

        if (documents.Count > MAX_DOCUMENTS) {
            throw new AccountTransferException("The file is too big to be one player's account.");
        }

        var accounts = documents.Where(d => d.Item1 == ACCOUNTS).ToList();
        if (accounts.Count != 1) {
            throw new AccountTransferException("The file must hold exactly one account.");
        }

        var account = accounts[0].Item2;
        var accountId = IdOf(account["AccountId"]) ?? throw new AccountTransferException("The account in the file has no id.");
        var characters = new HashSet<ulong>();
        foreach (var id in account["CharacterIds"] as JsonArray ?? []) {
            characters.Add(IdOf(id) ?? throw new AccountTransferException("The account in the file lists a character without an id."));
        }

        foreach (var (collection, data) in documents.Where(d => d.Item1 == WIZARDS)) {
            if (IdOf(data["AccountId"]) != accountId) {
                throw new AccountTransferException("The file holds a wizard of another account, so Ahziael won't import it.");
            }

            characters.Add(IdOf(data["CharId"]) ?? throw new AccountTransferException("A wizard in the file has no id."));
        }

        // Every other thing must belong to the account or one of its characters.
        var owners = characters.Append(accountId).ToHashSet();
        foreach (var (collection, data) in documents.Where(d => d.Item1 is not (ACCOUNTS or WIZARDS))) {
            var field = s_owned.First(o => o.Collection == collection).Owner;
            if (IdOf(data[field]) is not { } owner || !owners.Contains(owner)) {
                throw new AccountTransferException($"The file holds {collection} of someone else, so Ahziael won't import it.");
            }
        }

        var names = (root["Characters"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(c => string.Join(", ", new[] {
                (string?) c["Name"], (string?) c["School"], c["Level"] is JsonValue level ? $"level {level}" : null,
            }.Where(s => !string.IsNullOrWhiteSpace(s))))
            .Where(s => s.Length > 0)
            .ToList();
        var counts = documents.GroupBy(d => d.Item1).ToDictionary(g => g.Key, g => g.Count());

        return new AccountFile((string?) account["Username"] ?? "", (string?) root["Server"] ?? "", (string?) root["Made"] ?? "",
            names, counts, account["PasswordHash"] is JsonValue hash && ((string?) hash)?.Length > 0, documents);
    }

    /// <summary>The things in a file, in words: "2 wizards, 340 items, 6 pets, 120 quests, 1 house".</summary>
    public static string Describe(Dictionary<string, int> counts) {
        string Part(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
        var parts = new List<string> {
            Part(counts.GetValueOrDefault(WIZARDS), "wizard", "wizards"),
            Part(counts.GetValueOrDefault("WizardItems"), "item", "items"),
            Part(counts.GetValueOrDefault("WizardPets"), "pet", "pets"),
        };
        if (counts.GetValueOrDefault("WizardReagents") + counts.GetValueOrDefault("WizardPetSnacks") is var stacks and > 0) {
            parts.Add(Part(stacks, "stack of reagents or snacks", "stacks of reagents and snacks"));
        }

        parts.Add(Part(counts.GetValueOrDefault("QuestInstances"), "quest", "quests"));
        if (counts.GetValueOrDefault("Houses") is var houses and > 0) {
            parts.Add(Part(houses, "house", "houses"));
        }

        return string.Join(", ", parts);
    }

    // ---------------------------------------------------------------------
    // Import
    // ---------------------------------------------------------------------

    /// <summary>
    /// Adds the account in <paramref name="file"/> to this server as <paramref name="username"/>, in one go: either all of it
    /// arrives or nothing does. <paramref name="passwordHash"/> replaces the password the file carries (null keeps it).
    /// </summary>
    public static async Task ImportAsync(RavenHttp database, AccountFile file, string username, string? passwordHash) {
        username = username.Trim();
        if (username.Length is 0 or > 64) {
            throw new AccountTransferException("Enter a username for the account.");
        }

        if (passwordHash is null && !file.HasPassword) {
            throw new AccountTransferException("The file has no password for the account. Type one in.");
        }

        var taken = await database.QueryAsync($"from {ACCOUNTS} where Username = $username", new JsonObject { ["username"] = username });
        if (taken.Any(a => string.Equals((string?) a["Username"], username, StringComparison.OrdinalIgnoreCase))) {
            throw new AccountTransferException($"There is already an account named {username} on this server. Type another name.");
        }

        var map = NewNumbers(file);
        var puts = new List<(string Id, JsonObject Document)>();
        foreach (var (collection, original) in file.Documents) {
            var document = (JsonObject) Renumber(original.DeepClone(), map)!;
            document["@metadata"] = KeptMetadata(document["@metadata"] as JsonObject);
            document["@metadata"]!["@collection"] = collection;
            if (collection == ACCOUNTS) {
                document["Username"] = username;
                if (passwordHash is not null) {
                    document["PasswordHash"] = passwordHash;
                }

                document["PasswordNotSet"] = false;
                document["IsLocked"] = false;
                document["InfractionIds"] = new JsonArray();
                // A rank is the old server's to give: here the account starts as a player.
                document["AuthLevel"] = document["AuthLevel"]?.GetValueKind() == JsonValueKind.String ? "None" : 0;
                ForgetWhereFrom(document);
            }

            // Houses and attics are found by their lot and their character; everything else by a query, so any new id does.
            var id = collection switch {
                "Houses" => $"Houses/{IdOf(document["LotGid"])}",
                "Attics" => $"Attics/{IdOf(document["CharId"])}",
                _ => $"{collection}/{Guid.NewGuid():N}",
            };
            puts.Add((id, document));
        }

        // The account last, so it can only log in once everything it owns is there (the batch is one change anyway).
        await database.PutAllAsync([.. puts.OrderBy(p => (string?) p.Document["@metadata"]?["@collection"] == ACCOUNTS)]);
    }

    // Each id of the file gets a fresh random one. A character's game object is its id + 2, so that moves along.
    private static Dictionary<ulong, ulong> NewNumbers(AccountFile file) {
        var map = new Dictionary<ulong, ulong>();
        var used = new HashSet<ulong>();
        ulong Fresh() {
            while (true) {
                var value = (BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)) & ((1UL << 62) - 1)) | (1UL << 40);
                if (used.Add(value) && used.Add(value + 2)) {
                    return value;
                }
            }
        }

        foreach (var (collection, data) in file.Documents) {
            if (!s_identities.TryGetValue(collection, out var fields)) {
                continue;
            }

            foreach (var field in fields) {
                if (IdOf(data[field]) is { } old && old >= SMALLEST_ID && !map.ContainsKey(old)) {
                    var fresh = Fresh();
                    map[old] = fresh;
                    if (collection == WIZARDS) {
                        map.TryAdd(old + 2, fresh + 2);
                    }
                }
            }
        }

        // A character the account lists without a wizard document still moves to a new number.
        var account = file.Documents.First(d => d.Collection == ACCOUNTS).Data;
        foreach (var id in account["CharacterIds"] as JsonArray ?? []) {
            if (IdOf(id) is { } old && old >= SMALLEST_ID && !map.ContainsKey(old)) {
                var fresh = Fresh();
                map[old] = fresh;
                map.TryAdd(old + 2, fresh + 2);
            }
        }

        if (IdOf(account["AccountId"]) is not { } accountId || !map.ContainsKey(accountId)
            || file.Documents.Where(d => d.Collection == WIZARDS).Any(d => IdOf(d.Data["CharId"]) is not { } c || !map.ContainsKey(c))) {
            throw new AccountTransferException("The account or one of its wizards has an id Ahziael can't move.");
        }

        return map;
    }

    private static readonly Regex s_digits = new(@"\d{10,20}", RegexOptions.CultureInvariant);

    // Every place an old id is written, as a number, inside text (a document id) or as a key (a dictionary by id).
    private static JsonNode? Renumber(JsonNode? node, Dictionary<ulong, ulong> map) {
        switch (node) {
            case JsonObject obj: {
                var copy = new JsonObject();
                foreach (var (key, value) in obj.ToList()) {
                    obj.Remove(key);
                    var newKey = key == "@metadata" ? key : RenumberText(key, map);
                    copy[newKey] = Renumber(value, map);
                }

                return copy;
            }
            case JsonArray array: {
                var items = array.ToList();
                array.Clear();
                foreach (var item in items) {
                    array.Add(Renumber(item, map));
                }

                return array;
            }
            case JsonValue value when value.GetValueKind() == JsonValueKind.Number:
                return IdOf(value) is { } number && map.TryGetValue(number, out var fresh) ? JsonValue.Create(fresh) : value;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                var text = (string?) value ?? "";
                var renumbered = RenumberText(text, map);

                return ReferenceEquals(text, renumbered) ? value : JsonValue.Create(renumbered);
            default:
                return node;
        }
    }

    private static string RenumberText(string text, Dictionary<ulong, ulong> map)
        => !s_digits.IsMatch(text) ? text : s_digits.Replace(text, m =>
            ulong.TryParse(m.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && map.TryGetValue(n, out var fresh)
                ? fresh.ToString(CultureInfo.InvariantCulture)
                : m.Value);

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    /// <summary>A whole number however the database wrote it (big ids can come back as text or with an exponent).</summary>
    private static ulong? IdOf(JsonNode? node) {
        if (node is not JsonValue value) {
            return null;
        }

        var text = NumberText(value);
        if (ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) {
            return number;
        }

        return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var big) && big >= 0 && big <= ulong.MaxValue
            && decimal.Truncate(big) == big ? (ulong) big : null;
    }

    private static string NumberText(JsonNode node)
        => node.GetValueKind() == JsonValueKind.String ? (string?) node ?? "" : node.ToJsonString();

    // Only what a client may write; the rest is the database's own bookkeeping.
    private static JsonObject KeptMetadata(JsonObject? metadata) {
        var kept = new JsonObject();
        if (metadata?["@collection"] is { } collection) {
            kept["@collection"] = collection.DeepClone();
        }

        if (metadata?["Raven-Clr-Type"] is { } type) {
            kept["Raven-Clr-Type"] = type.DeepClone();
        }

        return kept;
    }

    // Where the player logged in from stays with the old server.
    private static void ForgetWhereFrom(JsonObject account) {
        if (account.ContainsKey("LastLoginIp")) {
            account["LastLoginIp"] = null;
        }

        foreach (var key in new[] { "LastLoginMachineId", "FirstLoginMachineId" }) {
            if (account.ContainsKey(key)) {
                account[key] = 0;
            }
        }
    }

}

/// <summary>An account move that could not be made. The message is written for the person using Ahziael.</summary>
public sealed class AccountTransferException(string message) : Exception(message);

/// <summary>
/// Raw documents of a RavenDB, through its HTTP API: as they are stored, so nothing is lost on the way through.
/// </summary>
public sealed class RavenHttp(HttpClient http, string baseUrl, string database) {

    private const int PAGE = 1024;

    private string Root => $"{baseUrl.TrimEnd('/')}/databases/{Uri.EscapeDataString(database)}";

    /// <summary>Every result of a query, page by page, once the database has caught up with recent changes.</summary>
    public async Task<List<JsonObject>> QueryAsync(string query, JsonObject parameters) {
        var all = new List<JsonObject>();
        for (var start = 0; ; start += PAGE) {
            var body = new JsonObject {
                ["Query"] = $"{query} limit {start}, {PAGE}",
                ["QueryParameters"] = parameters.DeepClone(),
                ["WaitForNonStaleResults"] = true,
                ["WaitForNonStaleResultsTimeout"] = "00:00:30",
            };
            using var response = await SendAsync(() => http.PostAsJsonAsync($"{Root}/queries", body));
            var json = await response.Content.ReadFromJsonAsync<JsonObject>();
            var page = (json?["Results"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            foreach (var result in page) {
                all.Add((JsonObject) result.DeepClone());
            }

            if (page.Count < PAGE) {
                return all;
            }
        }
    }

    /// <summary>Stores all the documents in one transaction: all of them, or none if anything fails.</summary>
    public async Task PutAllAsync(List<(string Id, JsonObject Document)> documents) {
        var commands = new JsonArray();
        foreach (var (id, document) in documents) {
            commands.Add(new JsonObject { ["Type"] = "PUT", ["Id"] = id, ["ChangeVector"] = null, ["Document"] = document });
        }

        var content = new StringContent(new JsonObject { ["Commands"] = commands }.ToJsonString(), Encoding.UTF8, "application/json");
        using var _ = await SendAsync(() => http.PostAsync($"{Root}/bulk_docs", content));
    }

    private static async Task<HttpResponseMessage> SendAsync(Func<Task<HttpResponseMessage>> send) {
        HttpResponseMessage response;
        try {
            response = await send();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) {
            throw new AccountTransferException("The server's database is not running. Start the server, wait until it is ready, then try again.");
        }

        if (!response.IsSuccessStatusCode) {
            var detail = await response.Content.ReadAsStringAsync();
            response.Dispose();
            var line = detail.Split('\n').FirstOrDefault()?.Trim() ?? "";
            throw new AccountTransferException($"The database refused ({(int) response.StatusCode}). {(line.Length > 200 ? line[..200] : line)}");
        }

        return response;
    }

}
