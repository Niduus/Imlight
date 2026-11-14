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

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Ultimus's optional local AI for chat (Niduus, 2026-11-14, "Both honestly": the keyword chat stays and is always the
/// fallback; a language model on his own PC can make the talk keep up). Off by default: [Ultimus] LocalAI. It talks to
/// Ollama or LM Studio on this PC or the home network through their OpenAI-style chat address ([Ultimus] LocalAIUrl,
/// LocalAIModel), never to the internet. The model is given only Ultimus's real facts and the keyword answer, and what it
/// says goes through the same private-details block as everything else (BotSocial.Scrub). When it is off, slow, down or
/// says something blocked, the keyword answer is sent instead. All settings apply at once.
/// </summary>
internal static class UltimusLocalAI {

    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static DateTime s_downUntil = DateTime.MinValue;
    private static string s_lastProblem = "";

    internal static bool On => ConfigurationManager.GetValue("Ultimus.LocalAI", false);

    private static string Url {
        get {
            var url = ConfigurationManager.GetValue("Ultimus.LocalAIUrl", "http://localhost:11434/v1");
            return string.IsNullOrWhiteSpace(url) ? "http://localhost:11434/v1" : url.Trim().TrimEnd('/');
        }
    }

    private static string Model {
        get {
            var model = ConfigurationManager.GetValue("Ultimus.LocalAIModel", "llama3.2");
            return string.IsNullOrWhiteSpace(model) ? "llama3.2" : model.Trim();
        }
    }

    /// <summary>What went wrong last, for the log ("" when it works).</summary>
    internal static string LastProblem => s_lastProblem;

    /// <summary>Usable now: switched on, the address is on this PC or the home network, and it did not just fail.</summary>
    internal static bool Ready => On && Local(Url) && DateTime.UtcNow >= s_downUntil;

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
    internal static async Task<string> Ask(string facts, IReadOnlyList<(string Who, string Text)> history, string heard, string keywordAnswer) {
        if (!Ready) {
            return null;
        }

        var system = new StringBuilder();
        system.AppendLine("You are Ultimus, a Balance wizard in a private Wizard101 server, played by the server itself (a bot). You quest the main story alone, like a player.");
        system.AppendLine("Answer the player's last chat line in 1 or 2 short, friendly sentences, under 200 characters, plain text, no quote marks, no emojis, no lists.");
        system.AppendLine("Use only the facts below and the conversation. If you don't know something, say so honestly. Never invent quests, places, items or memories.");
        system.AppendLine("You are a bot and never pretend to be human. Niduus made you.");
        system.AppendLine("Never share or guess server addresses, IPs, ports, account names, usernames, passwords, keys, emails, file paths or anyone's real name. If asked, politely refuse.");
        system.AppendLine("Do not promise to do things, follow someone, give items or gold, or change your plans: say what you are doing instead.");
        system.AppendLine("Stay on the player's current topic. A short follow-up (like 'what about ice?' or 'why?') is about the last topic; a new full question starts a new topic.");
        system.AppendLine();
        system.AppendLine("FACTS:");
        system.AppendLine(facts);
        if (!string.IsNullOrWhiteSpace(keywordAnswer)) {
            system.AppendLine();
            system.AppendLine("A correct answer from your own data, to keep the facts right (reword it naturally, keep its facts): " + keywordAnswer);
        }

        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system.ToString() } };
        foreach (var (who, text) in history.TakeLast(10)) {
            messages.Add(new JsonObject { ["role"] = who == "me" ? "assistant" : "user", ["content"] = text });
        }

        messages.Add(new JsonObject { ["role"] = "user", ["content"] = heard });
        var body = new JsonObject {
            ["model"] = Model, ["messages"] = messages, ["temperature"] = 0.6, ["max_tokens"] = 120, ["stream"] = false,
        };

        try {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            using var response = await s_http.PostAsync(Url + "/chat/completions",
                new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), cancel.Token).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(cancel.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) {
                Failed($"the local AI answered {(int) response.StatusCode} (is the model \"{Model}\" downloaded?)");
                return null;
            }

            var text = JsonNode.Parse(json)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
            s_lastProblem = "";

            return Clean(text);
        }
        catch (Exception ex) {
            Failed($"the local AI at {Url} could not be reached ({ex.GetType().Name}); is Ollama or LM Studio running?");
            return null;
        }
    }

    private static void Failed(string why) {
        // Down: the keyword chat answers alone for a minute before it is tried again.
        s_downUntil = DateTime.UtcNow.AddMinutes(1);
        if (why != s_lastProblem) {
            Logger.Information("Ultimus: {0}. The keyword chat answers meanwhile.", Logger.Args(why));
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
