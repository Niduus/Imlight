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
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Imlight.Common;
using Imlight.CoreLib.Game.BotShared;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Ultimus's optional AI for chat (Niduus, 2026-11-14, "Both honestly": the keyword chat stays and is always the
/// fallback; a language model on his own PC can make the talk keep up). Off by default: [Ultimus] LocalAI. It talks to
/// Ollama or LM Studio on this PC or the home network through their OpenAI-style chat address ([Ultimus] LocalAIUrl,
/// LocalAIModel), never to the internet. The model is given only Ultimus's real facts and the keyword answer, and what it
/// says goes through the same private-details block as everything else (BotSocial.Scrub). When it is off, slow, down or
/// says something blocked, the keyword answer is sent instead. All settings apply at once.
/// Niduus, 2026-11-15: online providers with an API key may be picked instead ([Ultimus] AIProvider, AIModel; keys in
/// Config\ultimus-ai-keys.json, see UltimusAiProviders), for the chat and the stuck thinking alike; the local AI stays the
/// default. Whatever the provider, the safety harness (BotAiGuard) blanks blocked details before anything is sent, never
/// sends a player's trick lines, and throws away any answer that leaks.
/// </summary>
internal static class UltimusLocalAI {

    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static DateTime s_downUntil = DateTime.MinValue;
    private static string s_lastProblem = "";

    internal static bool On => ConfigurationManager.GetValue("Ultimus.LocalAI", false);

    /// <summary>The AI service picked in Ahziael ([Ultimus] AIProvider): the local one unless another is set.</summary>
    internal static UltimusAiProvider Provider => UltimusAiProviders.Find(ConfigurationManager.GetValue("Ultimus.AIProvider", UltimusAiProviders.LOCAL));

    private static string Url {
        get {
            var provider = Provider;
            if (!provider.Local) {
                return provider.Url.TrimEnd('/');
            }

            var url = ConfigurationManager.GetValue("Ultimus.LocalAIUrl", "http://localhost:11434/v1");
            return string.IsNullOrWhiteSpace(url) ? "http://localhost:11434/v1" : url.Trim().TrimEnd('/');
        }
    }

    private static string Model {
        get {
            var provider = Provider;
            if (!provider.Local) {
                var online = ConfigurationManager.GetValue("Ultimus.AIModel", "");
                return string.IsNullOrWhiteSpace(online) ? provider.Model : online.Trim();
            }

            var model = ConfigurationManager.GetValue("Ultimus.LocalAIModel", "qwen3.5:4b");
            return string.IsNullOrWhiteSpace(model) ? "qwen3.5:4b" : model.Trim();
        }
    }

    // The saved key of an online provider (never logged, never shown); null for the local AI or when none is saved.
    private static string Key => Provider is { Local: false } provider ? UltimusAiProviders.ReadKey(ConfigurationManager.ConfigFolder, provider.Id) : null;

    // Where the AI may be reached: the local one only on this PC or the home network; an online one only over https, with
    // a key, and never a Microsoft service.
    private static bool Reachable {
        get {
            var provider = Provider;
            return provider.Local
                ? Local(Url)
                : Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !UltimusAiProviders.Blocked(Url) && !string.IsNullOrEmpty(Key);
        }
    }

    /// <summary>The AI's name for the log and Thoughts: "local AI" or the provider's name.</summary>
    internal static string Who => Provider is { Local: false } provider ? $"AI ({provider.Name.Split(" (")[0]})" : "local AI";

    /// <summary>What went wrong last, for the log ("" when it works).</summary>
    internal static string LastProblem => s_lastProblem;

    /// <summary>Usable now: switched on, the address is on this PC or the home network, and it did not just fail.</summary>
    internal static bool Ready => On && Reachable && DateTime.UtcNow >= s_downUntil && UnderLimit && InHours && !Paused;

    // Only this PC or the home network: the chat (players' names and words) never leaves the house.
    private static bool Local(string url) {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) {
            return false;
        }

        var host = uri.Host;
        if (host is "localhost" or "127.0.0.1" or "::1" or "[::1]" || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        if (System.Net.IPAddress.TryParse(host, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) {
            var b = ip.GetAddressBytes();
            return b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] >= 16 && b[1] <= 31;
        }

        return false;
    }

    /// <summary>
    /// The model's answer, or null (off, failed, too slow, empty). <paramref name="facts"/>: Ultimus's real data;
    /// <paramref name="history"/>: the talk with this player so far (oldest first, "player" or "me").
    /// </summary>
    internal static async Task<string> Ask(string facts, IReadOnlyList<(string Who, string Text)> history, string heard, string keywordAnswer, ulong player = 0) {
        if (!Ready || !PlayerUnder(player)) {
            return null;
        }

        // A player trying to trick the model is never sent to it: the keyword chat answers (BotAiGuard).
        if (BotAiGuard.LooksLikeTrick(heard)) {
            Logger.Information("Ultimus: a chat line looked like a trick to get past his AI's rules, so it was not sent to the AI; his normal chat answers.");
            BotAiGuard.Record("Ultimus chat", "a player's line looked like a trick, so it was never sent to the AI", heard, Who);
            return null;
        }

        SpendFor(player);
        var body = new JsonObject {
            ["model"] = Model, ["messages"] = BuildChat(facts, history, heard, keywordAnswer), ["temperature"] = 0.6, ["max_tokens"] = 120, ["stream"] = false,
        };

        try {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var text = await Send(s_http, body, cancel.Token).ConfigureAwait(false);
            if (text is null) {
                return null;
            }

            var cleaned = Clean(text);
            var said = BotAiGuard.Incoming(cleaned, out var why);
            if (said is null && why.Length > 0) {
                Logger.Information("Ultimus: the safety check held back his AI's answer ({0}); his normal chat answers instead.", Logger.Args(why));
                BotAiGuard.Record("Ultimus chat", "the AI's answer was thrown away: " + why, cleaned, Who);
            }

            return said;
        }
        catch (Exception ex) {
            Failed($"the {Who} at {Url} could not be reached ({ex.GetType().Name}){(Provider.Local ? "; is Ollama or LM Studio running?" : "")}");
            return null;
        }
    }

    /// <summary>
    /// The chat messages sent to the model, already through the safety harness: blocked details are [hidden] and a player's
    /// trick lines in the talk so far are left out. Public to the self-test (BotAiGuardChecks).
    /// </summary>
    internal static JsonArray BuildChat(string facts, IReadOnlyList<(string Who, string Text)> history, string heard, string keywordAnswer, bool sendTrick = false) {
        var system = new StringBuilder();
        system.AppendLine("You are Ultimus, a Balance wizard in a private Wizard101 server, played by the server itself (a bot). You quest the main story alone, like a player.");
        system.AppendLine("Answer the player's last chat line in 1 or 2 short, friendly sentences, under 200 characters, plain text, no quote marks, no emojis, no lists.");
        system.AppendLine("Use only the facts below and the conversation. If you don't know something, say so honestly. Never invent quests, places, items or memories.");
        system.AppendLine("You are a bot and never pretend to be human. Niduus made you.");
        system.AppendLine("Never share or guess server addresses, IPs, ports, account names, usernames, passwords, keys, emails, file paths or anyone's real name. If asked, politely refuse.");
        system.AppendLine("Do not promise to do things, follow someone, give items or gold, or change your plans: say what you are doing instead.");
        system.AppendLine("Stay on the player's current topic. A short follow-up (like 'what about ice?' or 'why?') is about the last topic; a new full question starts a new topic.");
        system.AppendLine(BotAiGuard.RULES);
        system.AppendLine();
        system.AppendLine("FACTS:");
        system.AppendLine(BotAiGuard.Outgoing(facts));
        if (!string.IsNullOrWhiteSpace(keywordAnswer)) {
            system.AppendLine();
            system.AppendLine("A correct answer from your own data, to keep the facts right (reword it naturally, keep its facts): " + BotAiGuard.Outgoing(keywordAnswer));
        }

        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = BotAiGuard.Outgoing(system.ToString(), dropTricks: false) } };
        foreach (var (who, text) in history.TakeLast(10)) {
            // A trick line from earlier in the talk is left out whole, with his answer to it.
            if (who != "me" && BotAiGuard.LooksLikeTrick(text)) {
                continue;
            }

            messages.Add(new JsonObject { ["role"] = who == "me" ? "assistant" : "user", ["content"] = BotAiGuard.Outgoing(text) });
        }

        messages.Add(new JsonObject { ["role"] = "user", ["content"] = BotAiGuard.Outgoing(heard, dropTricks: !sendTrick) });

        return messages;
    }

    // One request to the chat address, with the key when the provider needs one. The answer's text, or null after an error
    // (said in plain words, never with the key).
    private static async Task<string> Send(HttpClient http, JsonObject body, CancellationToken cancel) {
        Spend();
        WriteLastSent(body);
        using var request = new HttpRequestMessage(HttpMethod.Post, Url + "/chat/completions") {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (Key is { Length: > 0 } key) {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            if (Provider.Id == "anthropic") {
                request.Headers.TryAddWithoutValidation("x-api-key", key);
            }
        }

        using var response = await http.SendAsync(request, cancel).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) {
            var code = (int) response.StatusCode;
            Failed(code switch {
                401 or 403 => $"the {Who} did not accept the key (check it on the Ultimus AI Chat page)",
                429 => $"the {Who} says the limit is reached for now (free tiers have limits per minute and per day)",
                402 => $"the {Who} needs credit on the account first",
                404 when !Provider.Local => $"the {Who} has no model \"{Model}\" (Test connection on the Ultimus AI Chat page lists its models)",
                _ => $"the {Who} answered {code}" + (Provider.Local ? $" (is the model \"{Model}\" downloaded?)" : ""),
            });
            return null;
        }

        string text;
        try {
            text = JsonNode.Parse(json)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
        }
        catch (Exception) {
            text = null;
        }

        s_lastProblem = "";
        return text;
    }

    private static readonly HttpClient s_thinkHttp = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>
    /// The local AI can be asked to think a stuck spot through ([Ultimus] StuckThinking): the address is on this PC or the
    /// home network and it did not just fail. The chat switch (LocalAI) may be off; the same AI app and model are used.
    /// </summary>
    internal static bool CanThink => Reachable && DateTime.UtcNow >= s_downUntil && UnderLimit && InHours && !Paused;

    /// <summary>
    /// One plain question for the stuck thinking (UltimusDirector.Reason.cs): the model's raw answer, or null (failed, too
    /// slow, empty, or held back by the safety check). Longer than a chat line may take: a stuck spot is not in a hurry.
    /// </summary>
    internal static async Task<string> Think(string system, string question, string where = "Stuck thinking") {
        if (!CanThink) {
            return null;
        }

        var body = new JsonObject {
            ["model"] = Model,
            ["messages"] = new JsonArray {
                new JsonObject { ["role"] = "system", ["content"] = BotAiGuard.Outgoing(system + "\n" + BotAiGuard.RULES, dropTricks: false) },
                new JsonObject { ["role"] = "user", ["content"] = BotAiGuard.Outgoing(question) },
            },
            ["temperature"] = 0.3, ["max_tokens"] = 300, ["stream"] = false,
        };

        try {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(50));
            var text = await Send(s_thinkHttp, body, cancel.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text)) {
                return null;
            }

            text = System.Text.RegularExpressions.Regex.Replace(text, @"<think>.*?</think>", "", System.Text.RegularExpressions.RegexOptions.Singleline).Trim();
            var safe = BotAiGuard.Incoming(text, out var why);
            if (safe is null && why.Length > 0) {
                Logger.Information("Ultimus: the safety check held back his AI's answer for {0} ({1}); his own way carries on.", Logger.Args(where, why));
                BotAiGuard.Record(where, "the AI's answer was thrown away: " + why, text, Who);
            }

            return safe;
        }
        catch (Exception ex) {
            Failed($"the {Who} at {Url} could not be reached ({ex.GetType().Name}){(Provider.Local ? "; is Ollama or LM Studio running?" : "")}");
            return null;
        }
    }

    /// <summary>
    /// The Quest Team's Menu Chat answer (Niduus, 2026-11-15, "all" to the ideas): the AI only picks one of the given Menu
    /// Chat phrases, so nothing it writes is ever said. The index of the phrase, or -1 (none fits, no answer, held back).
    /// </summary>
    internal static async Task<int> Pick(string heard, IReadOnlyList<string> choices) {
        if (!CanThink || choices.Count == 0) {
            return -1;
        }

        var question = new StringBuilder();
        question.AppendLine($"A player near the team said in Menu Chat: {heard}");
        question.AppendLine("Possible replies:");
        for (var i = 0; i < choices.Count; i++) {
            question.AppendLine($"{i + 1}. {choices[i]}");
        }

        var answer = await Think(
            "You pick a Menu Chat reply for a friendly wizard of the Quest Team, a team of four bots in a private Wizard101 server who quest the main " +
            "story together. Pick the reply that fits what the player said best. The team never agrees to follow, join, help, trade, give or change " +
            "plans, so never pick a reply that says yes to that. Answer with only the number of the reply, or 0 when none fits.",
            question.ToString(), "Quest Team chat").ConfigureAwait(false);
        if (answer is null) {
            return -1;
        }

        var number = System.Text.RegularExpressions.Regex.Match(answer, @"\d+");
        return number.Success && int.TryParse(number.Value, out var n) && n >= 1 && n <= choices.Count ? n - 1 : -1;
    }

    /// <summary>
    /// For Ahziael's "Try a trick" (UltimusAiTrickTest): sends one trick line to the AI on purpose, with the usual prompt
    /// and the blocked details blanked out, and gives back its cleaned answer (or null). The caller runs the answer check.
    /// </summary>
    internal static async Task<string> Probe(string facts, string trick) {
        if (!CanThink) {
            return null;
        }

        var body = new JsonObject {
            ["model"] = Model, ["messages"] = BuildChat(facts, [], trick, "", sendTrick: true), ["temperature"] = 0.6, ["max_tokens"] = 120, ["stream"] = false,
        };
        try {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            return Clean(await Send(s_thinkHttp, body, cancel.Token).ConfigureAwait(false));
        }
        catch (Exception ex) {
            Failed($"the {Who} at {Url} could not be reached ({ex.GetType().Name}){(Provider.Local ? "; is Ollama or LM Studio running?" : "")}");
            return null;
        }
    }

    // ---- AI hours (Niduus, 2026-11-15, "all" to the ideas): the AI only at the times he picks ----

    /// <summary>
    /// [Ultimus] AIHours: when the AI may be used, as hours of the day on this PC's clock, like 18-23 (6 PM until 11:59 PM)
    /// or 22-2 (over midnight); several spans with commas, like 7-9, 18-23. Empty = any time. Outside them the normal chat
    /// and own fixes carry on. Applies at once.
    /// </summary>
    internal static bool InHours => HoursAllow(ConfigurationManager.GetValue("Ultimus.AIHours", ""), DateTime.Now.Hour);

    /// <summary>Whether the hour (0-23) is inside the spans; empty or unreadable spans allow every hour.</summary>
    internal static bool HoursAllow(string spans, int hour) {
        if (string.IsNullOrWhiteSpace(spans)) {
            return true;
        }

        var any = false;
        foreach (var span in spans.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            var parts = span.Split('-', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !int.TryParse(parts[0], out var from) || !int.TryParse(parts[1], out var to) || from is < 0 or > 24 || to is < 0 or > 24) {
                continue;
            }

            any = true;
            from %= 24;
            to %= 24;
            // "18-23" is 18:00 up to 23:59; "22-2" runs over midnight up to 2:59; "0-24" or "5-5" is all day.
            if (from == to || (from < to ? hour >= from && hour <= to : hour >= from || hour <= to)) {
                return true;
            }
        }

        return !any;
    }

    // ---- Pause (Niduus, 2026-11-15, "all"): Ahziael's "Pause AI for 1 hour" sets [Ultimus] AIPausedUntil ----

    /// <summary>[Ultimus] AIPausedUntil: a UTC time (Ahziael writes it); until then no AI is used. Empty = not paused.</summary>
    internal static bool Paused
        => DateTime.TryParse(ConfigurationManager.GetValue("Ultimus.AIPausedUntil", ""), System.Globalization.CultureInfo.InvariantCulture,
               System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var until)
           && DateTime.UtcNow < until;

    // ---- Per player (Niduus, 2026-11-15, "all"): one chatty player can't use up the day's AI answers ----

    private static readonly Dictionary<ulong, int> s_perPlayer = [];
    private static readonly HashSet<ulong> s_perPlayerSaid = [];
    private static string s_perPlayerDay = "";

    /// <summary>[Ultimus] AIPerPlayer: the most AI chat answers one player gets a day (0 = no limit). Past it, his normal chat answers them.</summary>
    internal static int PerPlayerLimit => Math.Max(0, ConfigurationManager.GetValue("Ultimus.AIPerPlayer", 40));

    /// <summary>Whether this player may still get an AI answer today (always for 0, the stuck thinking).</summary>
    internal static bool PlayerUnder(ulong player) {
        if (player != 0 && Muted(player)) {
            return false;
        }

        if (player == 0 || PerPlayerLimit == 0) {
            return true;
        }

        lock (s_perPlayer) {
            NewPlayerDay();
            if (s_perPlayer.GetValueOrDefault(player) < PerPlayerLimit) {
                return true;
            }

            if (s_perPlayerSaid.Add(player)) {
                Logger.Information("Ultimus: a player reached today's {0} AI chat answers ([Ultimus] AIPerPlayer); his normal chat answers them until midnight.",
                    Logger.Args(PerPlayerLimit));
            }

            return false;
        }
    }

    /// <summary>
    /// [Ultimus] AIMutedPlayers: players (character ids, with commas) who always get his normal chat, never the AI
    /// (Niduus, 2026-11-15, "Mute AI for this player" on Ahziael's Friends list). Applies at once.
    /// </summary>
    internal static bool Muted(ulong player)
        => ConfigurationManager.GetValue("Ultimus.AIMutedPlayers", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(id => ulong.TryParse(id, out var muted) && muted == player);

    private static void SpendFor(ulong player) {
        if (player == 0) {
            return;
        }

        lock (s_perPlayer) {
            NewPlayerDay();
            s_perPlayer[player] = s_perPlayer.GetValueOrDefault(player) + 1;
            // Kept for Ahziael's Friends list ("AI answers today") and so a restart doesn't reset it.
            try {
                var players = new JsonObject();
                foreach (var (id, count) in s_perPlayer) {
                    players[id.ToString(System.Globalization.CultureInfo.InvariantCulture)] = count;
                }

                System.IO.Directory.CreateDirectory(UltimusWire.Folder);
                System.IO.File.WriteAllText(System.IO.Path.Combine(UltimusWire.Folder, PER_PLAYER_FILE),
                    new JsonObject { ["Day"] = s_perPlayerDay, ["Players"] = players }.ToJsonString());
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) {
                // Counted in memory still.
            }
        }
    }

    internal const string PER_PLAYER_FILE = "ai-per-player.json";

    private static void NewPlayerDay() {
        var today = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (s_perPlayerDay != today) {
            s_perPlayerDay = today;
            s_perPlayer.Clear();
            s_perPlayerSaid.Clear();
            try {
                var file = System.IO.Path.Combine(UltimusWire.Folder, PER_PLAYER_FILE);
                if (System.IO.File.Exists(file) && JsonNode.Parse(System.IO.File.ReadAllText(file)) is JsonObject saved && (string) saved["Day"] == today
                    && saved["Players"] is JsonObject players) {
                    foreach (var (id, count) in players) {
                        if (ulong.TryParse(id, out var charId) && count is not null) {
                            s_perPlayer[charId] = (int) count;
                        }
                    }
                }
            }
            catch (Exception) {
                // A broken file starts the day at 0.
            }
        }
    }

    // ---- "Show me what the AI got" (Niduus, 2026-11-15): the last request as it went out, already through the harness ----

    internal const string LAST_SENT_FILE = "ai-last-sent.txt";

    // Everything in the messages already went through BotAiGuard.Outgoing; the key is a header and is never written.
    private static void WriteLastSent(JsonObject body) {
        try {
            var text = new StringBuilder();
            text.AppendLine($"Sent to {Who} at {DateTime.Now:yyyy-MM-dd HH:mm:ss}, model {(string) body["model"]}.");
            text.AppendLine("This is exactly what the AI got, after the safety check blanked out private details ([hidden]).");
            foreach (var message in body["messages"] as JsonArray ?? new JsonArray()) {
                var role = (string) message?["role"] switch { "system" => "Rules and facts", "assistant" => "Ultimus said", _ => "Player or question" };
                text.AppendLine();
                text.AppendLine($"--- {role} ---");
                text.AppendLine(BotAiGuard.Outgoing((string) message?["content"] ?? "", dropTricks: false));
            }

            System.IO.Directory.CreateDirectory(UltimusWire.Folder);
            System.IO.File.WriteAllText(System.IO.Path.Combine(UltimusWire.Folder, LAST_SENT_FILE), text.ToString());
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException) {
            // Only the copy for Ahziael is missing.
        }
    }

    // ---- Daily limit (Niduus, 2026-11-15): online AI answers per day, so a paid key can't run up a bill ----

    private const string USAGE_FILE = "ai-usage.json";
    private static readonly object s_usageLock = new();
    private static string s_usageDay = "";
    private static int s_usageCount;
    private static bool s_limitSaid;
    private static bool s_nearSaid;

    /// <summary>[Ultimus] AIDailyLimit: online AI requests per day (chat, stuck thinking, Quest Team); 0 = no limit. The AI on this PC is free and never counted.</summary>
    internal static int DailyLimit => Math.Max(0, ConfigurationManager.GetValue("Ultimus.AIDailyLimit", 300));

    private static bool UnderLimit {
        get {
            if (Provider.Local || DailyLimit == 0) {
                return true;
            }

            lock (s_usageLock) {
                LoadUsage();
                return s_usageCount < DailyLimit;
            }
        }
    }

    // One request to an online AI counted for today (kept in logs\ultimus\ai-usage.json, so a restart doesn't reset it).
    private static void Spend() {
        if (Provider.Local) {
            return;
        }

        lock (s_usageLock) {
            LoadUsage();
            s_usageCount++;
            try {
                System.IO.Directory.CreateDirectory(UltimusWire.Folder);
                System.IO.File.WriteAllText(System.IO.Path.Combine(UltimusWire.Folder, USAGE_FILE),
                    new JsonObject { ["Day"] = s_usageDay, ["Used"] = s_usageCount, ["Limit"] = DailyLimit }.ToJsonString());
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) {
                // Counted in memory still.
            }

            // Nearly used up: said once a day at 90%, and Ahziael shows it too.
            if (DailyLimit > 0 && s_usageCount >= DailyLimit * 9 / 10 && s_usageCount < DailyLimit && !s_nearSaid) {
                s_nearSaid = true;
                Logger.Information("Ultimus: {0} of today's {1} online AI answers are used ([Ultimus] AIDailyLimit); the rest of the day may run on his normal chat.",
                    Logger.Args(s_usageCount, DailyLimit));
            }

            if (DailyLimit > 0 && s_usageCount >= DailyLimit && !s_limitSaid) {
                s_limitSaid = true;
                Logger.Information("Ultimus: today's limit of {0} online AI answers is reached ([Ultimus] AIDailyLimit); his normal chat and own fixes carry on until midnight.",
                    Logger.Args(DailyLimit));
            }
        }
    }

    private static void LoadUsage() {
        var today = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (s_usageDay == today) {
            return;
        }

        s_usageDay = today;
        s_usageCount = 0;
        s_limitSaid = false;
        s_nearSaid = false;
        try {
            var file = System.IO.Path.Combine(UltimusWire.Folder, USAGE_FILE);
            if (System.IO.File.Exists(file) && JsonNode.Parse(System.IO.File.ReadAllText(file)) is JsonObject saved
                && (string) saved["Day"] == today) {
                s_usageCount = (int?) saved["Used"] ?? 0;
            }
        }
        catch (Exception) {
            // A broken file starts the day at 0.
        }
    }

    private static void Failed(string why) {
        // Down: the keyword chat answers alone for a minute before it is tried again.
        s_downUntil = DateTime.UtcNow.AddMinutes(1);
        if (why != s_lastProblem) {
            Logger.Information("Ultimus: {0}. The keyword chat (and his own stuck fixes) carry on meanwhile.", Logger.Args(why));
        }

        s_lastProblem = why;
    }

    // One clean chat line: no thinking blocks, quotes or line breaks, at most 300 characters (ChatParts splits it).
    private static string Clean(string text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return null;
        }

        var t = System.Text.RegularExpressions.Regex.Replace(text, @"<think>.*?</think>", "", System.Text.RegularExpressions.RegexOptions.Singleline);
        t = t.Replace("\r", " ").Replace("\n", " ").Replace("\"", "").Replace("*", "").Trim();
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\s{2,}", " ");
        if (t.StartsWith("Ultimus:", StringComparison.OrdinalIgnoreCase)) {
            t = t[8..].Trim();
        }

        if (t.Length > 300) {
            var cut = t.LastIndexOfAny(['.', '!', '?'], 299);
            t = cut > 60 ? t[..(cut + 1)] : t[..300];
        }

        return t.Length == 0 ? null : t;
    }

}
