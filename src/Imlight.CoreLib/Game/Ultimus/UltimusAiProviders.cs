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

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>One AI service Ultimus can talk through. All of them speak the same OpenAI-style chat address.</summary>
/// <param name="Id">What [Ultimus] AIProvider holds.</param>
/// <param name="Name">The name shown in Ahziael.</param>
/// <param name="Url">The chat address (without /chat/completions). Empty for the local one, which uses LocalAIUrl.</param>
/// <param name="Model">The model used when [Ultimus] AIModel is empty.</param>
/// <param name="Local">On this PC or the home network (Ollama, LM Studio): no key, nothing leaves the house.</param>
/// <param name="Free">Has a free way to use it (a free tier or free models).</param>
/// <param name="Cost">What it costs, in plain words, for the Ahziael page.</param>
/// <param name="KeyPage">Where a key is made.</param>
internal sealed record UltimusAiProvider(string Id, string Name, string Url, string Model, bool Local, bool Free, string Cost, string KeyPage);

/// <summary>
/// The AI services Ultimus's chat and stuck thinking can use (Niduus, 2026-11-15: "allow for Ultimus to also have API keys
/// for it's chat. Citing free and accurate ones for those who don't want to pay"). The local AI stays the default. Online
/// ones need a key, which is kept only in Config\ultimus-ai-keys.json beside Imlight.ini: never in Imlight.ini, the logs,
/// the log zips or the backups. No Microsoft services (Niduus's rule): no Azure, no Copilot, no Microsoft accounts.
/// Shared with Ahziael (linked into Imlight.Setup), so the page and the server always agree on the list.
/// </summary>
internal static class UltimusAiProviders {

    internal const string LOCAL = "local";
    internal const string KEYS_FILE = "ultimus-ai-keys.json";

    // Limits on free tiers change often, so the notes say where to see the current ones instead of numbers that go stale,
    // except OpenRouter's, which its own documentation states.
    internal static readonly UltimusAiProvider[] All = [
        new(LOCAL, "On this PC: Ollama or LM Studio (free, default)", "", "llama3.2", Local: true, Free: true,
            "Free. Runs on your own computer, needs no key and nothing leaves your PC or home network. Uses your PC's memory and graphics card while it runs.",
            "https://ollama.com/download"),
        new("gemini", "Google Gemini (free tier)", "https://generativelanguage.googleapis.com/v1beta/openai", "gemini-2.5-flash-lite", Local: false, Free: true,
            "Free tier: make a key in Google AI Studio with a Google account, no card needed. The free daily limits are small and change often; " +
            "AI Studio shows yours. On the free tier Google may use what is sent to improve its products. Paid: pay per use once you turn on billing.",
            "https://aistudio.google.com/apikey"),
        new("groq", "Groq (free tier)", "https://api.groq.com/openai/v1", "llama-3.1-8b-instant", Local: false, Free: true,
            "Free tier: sign up and make a key, no card needed. There are limits per minute and per day for each model; the Groq console shows yours. " +
            "Very fast answers. Paid: pay per use for higher limits.",
            "https://console.groq.com/keys"),
        new("openrouter", "OpenRouter (free models)", "https://openrouter.ai/api/v1", "meta-llama/llama-3.3-70b-instruct:free", Local: false, Free: true,
            "Free models (their names end in :free) need no card: up to 20 a minute and 50 a day, or 1,000 a day once you have ever bought 10 dollars of credit. " +
            "Which free models exist changes; Test connection lists them. Some free models' makers may keep what is sent. Every other model is pay per use.",
            "https://openrouter.ai/keys"),
        new("cerebras", "Cerebras (free tier)", "https://api.cerebras.ai/v1", "llama3.1-8b", Local: false, Free: true,
            "Free tier: sign up and make a key, no card needed, with daily limits (the Cerebras cloud page shows yours). Very fast. " +
            "Which models it offers changes; Test connection lists them. Paid plans give higher limits.",
            "https://cloud.cerebras.ai"),
        new("mistral", "Mistral (free plan)", "https://api.mistral.ai/v1", "mistral-small-latest", Local: false, Free: true,
            "Free \"Experiment\" plan: sign up and check your phone number, no card needed. Low limits, and Mistral may use what is sent to train its models. " +
            "Paid: pay per use.",
            "https://console.mistral.ai/api-keys"),
        new("anthropic", "Anthropic Claude (paid)", "https://api.anthropic.com/v1", "claude-haiku-5-5", Local: false, Free: false,
            "Paid only: buy credit first in the Anthropic console, then you pay per use. A small, fast model like Haiku costs very little per chat line. " +
            "Anthropic does not train on what is sent through a key by default.",
            "https://console.anthropic.com/settings/keys"),
        new("openai", "OpenAI (paid)", "https://api.openai.com/v1", "gpt-4.1-mini", Local: false, Free: false,
            "Paid only: add credit first, then you pay per use. A small model like gpt-4.1-mini costs very little per chat line. " +
            "OpenAI does not train on what is sent through a key by default.",
            "https://platform.openai.com/api-keys"),
    ];

    /// <summary>The provider with this id, or the local one when it is empty or unknown.</summary>
    internal static UltimusAiProvider Find(string? id) {
        var wanted = (id ?? "").Trim();
        return All.FirstOrDefault(p => p.Id.Equals(wanted, StringComparison.OrdinalIgnoreCase)) ?? All[0];
    }

    // No Microsoft services (Niduus's standing rule): never Azure, Copilot, Bing or a Microsoft account sign-in.
    private static readonly string[] s_microsoft = [
        "azure", "microsoft", "windows.net", "copilot", "bing.com", "live.com", "msn.com", "office.com", "office365", "cognitive", "msft",
    ];

    /// <summary>An address the AI may never be reached at: Microsoft's, or not a web address at all.</summary>
    internal static bool Blocked(string? url) {
        if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) {
            return true;
        }

        var host = uri.Host.ToLowerInvariant();
        return s_microsoft.Any(m => host.Contains(m, StringComparison.Ordinal));
    }

    /// <summary>The keys file beside Imlight.ini.</summary>
    internal static string KeysFile(string configFolder) => Path.Combine(configFolder, KEYS_FILE);

    private static readonly object s_lock = new();
    private static string s_cachedFile = "";
    private static DateTime s_cachedAt = DateTime.MinValue;
    private static Dictionary<string, string> s_cached = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every saved key by provider id (read again when the file changes, so a new key counts at once).</summary>
    internal static Dictionary<string, string> ReadKeys(string configFolder) {
        if (string.IsNullOrEmpty(configFolder)) {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var file = KeysFile(configFolder);
        lock (s_lock) {
            try {
                var info = new FileInfo(file);
                if (!info.Exists) {
                    s_cachedFile = file;
                    s_cachedAt = DateTime.MinValue;
                    s_cached = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    return s_cached;
                }

                if (file == s_cachedFile && info.LastWriteTimeUtc == s_cachedAt) {
                    return s_cached;
                }

                var read = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? new Dictionary<string, string>();
                s_cached = new Dictionary<string, string>(read.Where(k => !string.IsNullOrWhiteSpace(k.Value))
                    .ToDictionary(k => k.Key, k => k.Value.Trim()), StringComparer.OrdinalIgnoreCase);
                s_cachedFile = file;
                s_cachedAt = info.LastWriteTimeUtc;
            }
            catch (Exception) {
                // A broken file counts as no keys; it is never shown or logged, since it may hold one.
                s_cached = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            return s_cached;
        }
    }

    /// <summary>The saved key for one provider, or null.</summary>
    internal static string? ReadKey(string configFolder, string id)
        => ReadKeys(configFolder).TryGetValue(id, out var key) && key.Length > 0 ? key : null;

    /// <summary>Saves (or, when empty, forgets) one provider's key. The other providers' keys stay.</summary>
    internal static void SaveKey(string configFolder, string id, string? key) {
        var keys = new Dictionary<string, string>(ReadKeys(configFolder), StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(key)) {
            keys.Remove(id);
        }
        else {
            keys[id] = key.Trim();
        }

        Directory.CreateDirectory(configFolder);
        File.WriteAllText(KeysFile(configFolder), JsonSerializer.Serialize(keys, new JsonSerializerOptions { WriteIndented = true }));
    }

}
