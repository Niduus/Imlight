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
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;

namespace Imlight.Setup;

/// <summary>One line of an AI Guide chat: who said it and what.</summary>
internal sealed record AiGuideTurn(bool FromPlayer, string Text);

/// <summary>
/// The AI Guide's talk with Claude (Niduus, 2026-10-28: "a AI section in the manager that is soley BYOK ... a guide of
/// Ahziael and will not be able to create/modify the program"). Bring your own key only: the person's own Anthropic key,
/// saved in the install folder on this PC and sent nowhere but to Anthropic. The guide is given no tools at all, so it
/// can only answer in words: it cannot change Ahziael, the server, its settings or any file.
/// </summary>
internal static class AiGuide {

    private const string SETTINGS_FILE = "ai-guide.json";

    /// <summary>Where a new key is made.</summary>
    internal const string KEYS_PAGE = "https://console.anthropic.com/settings/keys";

    /// <summary>Where credit is added (the API is paid as you go, apart from a Claude.ai subscription).</summary>
    internal const string BILLING_PAGE = "https://console.anthropic.com/settings/billing";

    /// <summary>The models offered: its ID, the name the app shows, and a line on cost.</summary>
    internal static readonly (string Id, string Name, string Line)[] MODELS = [
        ("claude-opus-5-5", "Claude Opus 5.5 (best answers)",
            "The smartest. About 20 cents for the first question, then 1 to 3 cents each while you keep asking."),
        ("claude-sonnet-5-5", "Claude Sonnet 5.5 (balanced)",
            "Nearly as good for half the price: about 10 cents for the first question, then about a cent each."),
        ("claude-haiku-5-5", "Claude Haiku 5.5 (cheapest)",
            "Quick and very cheap, for simple questions: well under a cent a question."),
    ];

    internal sealed record Settings(string Key, string Model);

    internal static Settings Load(string installFolder) {
        try {
            var path = Path.Combine(installFolder, SETTINGS_FILE);
            if (File.Exists(path)) {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                var key = root.TryGetProperty("Key", out var k) ? k.GetString() ?? "" : "";
                var model = root.TryGetProperty("Model", out var m) ? m.GetString() ?? "" : "";

                return new Settings(key, Array.Exists(MODELS, x => x.Id == model) ? model : MODELS[0].Id);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            // A broken file counts as no key; saving again replaces it.
        }

        return new Settings("", MODELS[0].Id);
    }

    internal static void Save(string installFolder, Settings settings) {
        Directory.CreateDirectory(installFolder);
        File.WriteAllText(Path.Combine(installFolder, SETTINGS_FILE),
            JsonSerializer.Serialize(new Dictionary<string, string> { ["Key"] = settings.Key, ["Model"] = settings.Model }));
    }

    internal static void Forget(string installFolder) => File.Delete(Path.Combine(installFolder, SETTINGS_FILE));

    /// <summary>Whether a key looks like an Anthropic API key (they start "sk-ant-").</summary>
    internal static bool LooksLikeKey(string key) => key.StartsWith("sk-ant-", StringComparison.Ordinal) && key.Length > 20;

    /// <summary>
    /// The guide's answer to the last question in the chat, a piece at a time as it is written. The reference (rules and
    /// what the app knows) goes first and is cached, so later questions within a few minutes cost far less.
    /// </summary>
    internal static async IAsyncEnumerable<string> AskAsync(Settings settings, string reference, IReadOnlyList<AiGuideTurn> chat,
        [EnumeratorCancellation] CancellationToken cancel) {
        var client = new AnthropicClient { ApiKey = settings.Key, BaseUrl = "https://api.anthropic.com" };
        // Always Anthropic itself: a base address set on the PC for other tools is not used, so the key goes nowhere else.
        var messages = new List<MessageParam>();
        foreach (var turn in chat) {
            messages.Add(turn.FromPlayer
                ? new UserMessageParam { Content = turn.Text }
                : new AssistantMessageParam { Content = turn.Text });
        }

        var parameters = new MessageCreateParams {
            Model = settings.Model,
            MaxTokens = 16000,
            System = new List<TextBlockParam> { new() { Text = reference, CacheControl = new CacheControlEphemeral() } },
            // The chat so far is cached too, so each new question only pays in full for itself.
            CacheControl = new CacheControlEphemeral(),
            OutputConfig = new OutputConfig { Effort = Effort.Low },
            Messages = messages,
        };

        var stream = client.Messages.CreateStreaming(parameters, cancel).GetAsyncEnumerator(cancel);
        var refused = false;
        try {
            while (true) {
                try {
                    if (!await stream.MoveNextAsync()) {
                        break;
                    }
                }
                catch (AnthropicUnauthorizedException) {
                    throw new PanelException("Anthropic did not accept the key. Check that it was copied whole, or make a new one with Get a key.");
                }
                catch (AnthropicForbiddenException) {
                    throw new PanelException("This key may not use that model. Pick another model, or check the key on Anthropic's site.");
                }
                catch (AnthropicRateLimitException) {
                    throw new PanelException("Anthropic says too many questions at once, or the account is out of credit. Wait a minute, " +
                                             "or add credit with Add credit.");
                }
                catch (AnthropicBadRequestException ex) when (ex.Message.Contains("credit", StringComparison.OrdinalIgnoreCase)) {
                    throw new PanelException("The Anthropic account has no credit left. Add some with Add credit, then ask again.");
                }
                catch (Anthropic5xxException) {
                    throw new PanelException("Anthropic is busy or down right now. Try again in a minute.");
                }
                catch (AnthropicIOException) {
                    throw new PanelException("Could not reach Anthropic. Check the internet connection and try again.");
                }

                var piece = stream.Current;
                if (piece.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text)) {
                    yield return text.Text;
                }
                else if (piece.TryPickDelta(out var end) && end.Delta.StopReason is { } reason && reason.Raw() == "refusal") {
                    refused = true;
                }
            }
        }
        finally {
            await stream.DisposeAsync();
        }

        if (refused) {
            yield return "\n\n(The guide stopped here and would not answer this one. Try asking it another way.)";
        }
    }

}
