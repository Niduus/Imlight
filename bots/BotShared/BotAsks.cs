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
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// What a player asks a bot to do with them (Niduus, 2026-11-16): "walk me through this quest" (teaching mode) and "help me
/// fight" / "heal me" / "be my hitter" (group helper), and the words that end it ("thanks, I got it", "stop"). Shared, so
/// every bot body (Ultimus, the Quest Team, street bots) reads a player's ask the same way. Also a player's quest step in
/// plain words, read from their own quest book as the game's Quest Tracker shows it.
/// </summary>
internal static class BotAsks {

    internal enum HelpRole { Any, Healer, Hitter }

    private static readonly Regex s_teach = new(
        @"\b(walk|talk|guide|lead|take)\s+me\s+(through|thru|on|along)\b|\bguide\s+me\b|\bteach\s+me\b|\bshow\s+me\s+(how|where|what)\b" +
        @"|\bhelp\s+me\s+(with|on|finish|do)\s+(this|my|the|a)\s+quest\b|\bhow\s+do\s+i\s+(do|finish|beat)\s+(this|my|the)\s+quest\b|\bstep\s+by\s+step\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex s_help = new(
        @"\b(help|join|come)\s+(me\s+)?(in\s+|with\s+)?(my\s+|this\s+|the\s+)?(fight|battle|duel|boss)\b|\bjoin\s+(me|us)\b|\bcome\s+help\b" +
        @"|\bhelp\s+me\s+(kill|beat|fight|defeat)\b|\b(heal|shield)\s+me\b|\bbe\s+my\s+(healer|hitter|support|dps|tank)\b" +
        @"|\bi\s+need\s+(a\s+)?(healer|hitter|heals|damage|dps|help\s+(fighting|in\s+(my|this)\s+(fight|battle)))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex s_healer = new(@"\b(heal|heals|healer|healing|support|shield|tank)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex s_hitter = new(@"\b(hit|hitter|damage|dps|kill|attack|beat)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex s_stop = new(
        @"^\s*(stop|ok\s+stop|that'?s\s+(all|enough)|never\s*mind|nvm|i\s+got\s+(it|this)|got\s+it|i'?m\s+good|im\s+good|no\s+thanks|thanks?(\s+you)?[,!.\s]*(i\s+got\s+it|that'?s\s+all|bye)?|thx|ty|you\s+can\s+go)\s*[.!]*\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>"walk me through this quest", "guide me", "how do I do this quest"...</summary>
    internal static bool IsTeachAsk(string text) => !string.IsNullOrWhiteSpace(text) && s_teach.IsMatch(text);

    /// <summary>"help me fight", "join my battle", "heal me", "be my hitter"..., with the role they asked for.</summary>
    internal static bool IsHelpAsk(string text, out HelpRole role) {
        role = HelpRole.Any;
        if (string.IsNullOrWhiteSpace(text) || !s_help.IsMatch(text)) {
            return false;
        }

        role = s_healer.IsMatch(text) ? HelpRole.Healer : s_hitter.IsMatch(text) ? HelpRole.Hitter : HelpRole.Any;
        return true;
    }

    /// <summary>"stop", "thanks, I got it", "never mind"...: ends teaching or helping.</summary>
    internal static bool IsStop(string text) => !string.IsNullOrWhiteSpace(text) && s_stop.IsMatch(text);

    /// <summary>A quest named in the ask ("walk me through Gathering Evidence"), as typed; "" when none.</summary>
    internal static string NamedQuest(string text) {
        var m = Regex.Match(text ?? "", @"\b(?:through|thru|with|on|do|finish)\s+(?:the\s+quest\s+)?[""']?(?<q>[A-Za-z0-9' :!?,-]{4,60}?)[""']?\s*[?.!]*$", RegexOptions.IgnoreCase);
        if (!m.Success) {
            return "";
        }

        var q = m.Groups["q"].Value.Trim();
        return Regex.IsMatch(q, @"^(this|my|the|a|it|that)(\s+quest)?$", RegexOptions.IgnoreCase) ? "" : q;
    }

    /// <summary>A player's quest as their Quest Tracker shows it now: its title, its first step and where, and how to get there.</summary>
    internal sealed record QuestNow(string Id, string Title, string Step, string Where, bool Here, string Way, string World);

    /// <summary>
    /// The quest a player wants help with: the one they named (by title, loosely), else the one they last asked about, else
    /// their first story quest (the tracker's own order). Null when they hold none.
    /// </summary>
    internal static QuestNow QuestOf(Wizard wizard, string named, string sticky = "") {
        if (wizard is null) {
            return null;
        }

        JsonObject book;
        try {
            book = OverlayQuestTracker.Build(wizard, null, sticky ?? "");
        }
        catch (Exception) {
            return null;
        }

        var rows = (book["quests"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (rows.Count == 0) {
            return null;
        }

        JsonObject row = null;
        if (!string.IsNullOrWhiteSpace(named)) {
            row = rows.FirstOrDefault(r => Same((string) r["t"], named))
                  ?? rows.FirstOrDefault(r => ((string) r["t"] ?? "").Contains(named.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (row is null && !string.IsNullOrEmpty(sticky)) {
            row = rows.FirstOrDefault(r => string.Equals((string) r["id"], sticky, StringComparison.OrdinalIgnoreCase));
            if (row is null) {
                // The quest they were on is gone from their book: done (or dropped).
                return null;
            }
        }

        row ??= rows[0];
        if (!string.IsNullOrEmpty(sticky) && !string.Equals((string) row["id"], sticky, StringComparison.OrdinalIgnoreCase)) {
            row = rows.FirstOrDefault(r => string.Equals((string) r["id"], sticky, StringComparison.OrdinalIgnoreCase)) ?? row;
        }

        // Ask the tracker again with this quest picked, for the way to its step.
        if (!string.Equals(sticky, (string) row["id"], StringComparison.OrdinalIgnoreCase)) {
            try {
                var again = OverlayQuestTracker.Build(wizard, null, (string) row["id"] ?? "");
                row = (again["quests"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(r => string.Equals((string) r["id"], (string) row["id"], StringComparison.OrdinalIgnoreCase)) ?? row;
            }
            catch (Exception) {
                // The first read will do.
            }
        }

        var step = (row["steps"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault();
        // Read safely: a field the tracker sends as an object or list (not a plain value) made every chat line to Ultimus
        // throw, so he heard nothing a player said, "come here" and teleport asks included (Niduus, 2026-11-16, Big Ben).
        return new QuestNow(Text(row["id"]), Text(row["t"]), Text(step?["s"]), Text(step?["where"]),
            Flag(step?["here"]) ?? Flag(row["here"]) ?? false, Text(row["way"]), Text(row["w"]));
    }

    private static string Text(JsonNode node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text ?? "" : "";

    private static bool? Flag(JsonNode node)
        => node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    private static bool Same(string a, string b)
        => string.Equals(Squash(a), Squash(b), StringComparison.OrdinalIgnoreCase);

    private static string Squash(string s) => new((s ?? "").Where(char.IsLetterOrDigit).ToArray());

    /// <summary>A step's words with its counts taken out ("Defeat Rotting Fodder (2 of 5)" -> "Defeat Rotting Fodder"), to tell a new step from progress on the same one.</summary>
    internal static string StepKey(string step) => Regex.Replace(step ?? "", @"\(?\d+\s*(of|/)\s*\d+\)?", "").Trim();

}
