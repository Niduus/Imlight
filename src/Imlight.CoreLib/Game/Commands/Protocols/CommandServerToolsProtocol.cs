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
using System.Linq;
using System.Text;
using System.Text.Json;
using Imlight.Common;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// Ahziael's server cards from the game's chat (Niduus, 2026-11-04: "implement a majority of what ahziael can do as
/// commands to help people execute them properly"):
/// <list type="bullet">
/// <item>`.announce box|chat|both &lt;words&gt;`: the Announcements card's message to everyone online (hall monitors and up).</item>
/// <item>`.benefit`, `.benefit on &lt;name|all&gt; [time]`, `.benefit off &lt;name|all&gt;`: the Member benefits card (administrators).</item>
/// <item>`.questcheck`: Quest Testing's "Check all quests" button; the report shows on that tab (administrators).</item>
/// </list>
/// </summary>
internal class CommandServerToolsProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("announce")]
    [Alias("announcement")]
    [AuthRequired(AuthLevel.HallMonitor)]
    private void AnnounceCommand([Remainder] string text = "") {
        var words = (text ?? "").Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var where = words.Length > 0 ? words[0].ToLowerInvariant() : "";
        var (box, chat) = where switch {
            "box" => (true, false),
            "chat" => (false, true),
            "both" => (true, true),
            _ => (true, false),
        };
        var message = where is "box" or "chat" or "both" ? (words.Length > 1 ? words[1].Trim() : "") : (text ?? "").Trim();
        if (message.Length == 0) {
            InformSenderClient("Type .announce and the words to send everyone. Start with box, chat or both to pick where it shows "
                + "(a box on screen is the default), such as .announce chat Double XP starts in 10 minutes!");

            return;
        }

        var sent = ManagerCommands.Announce(box, chat, message);
        Logger.Information("Announcement from {0} in game ({1}): {2}", Logger.Args(Context.Account?.Username,
            box && chat ? "box and chat" : box ? "box" : "chat", message));
        InformSenderClient(sent == 0
            ? "The announcement could not be sent: the server isn't ready yet."
            : $"Sent to {sent} player{(sent == 1 ? "" : "s")} online{(box && chat ? " as a box and a chat line" : box ? " as a box on screen" : " as a chat line")}.");
    }

    [Command("benefit")]
    [Alias("benefits", "memberbenefits")]
    [AuthRequired(AuthLevel.Administrator)]
    private void BenefitCommand([Remainder] string text = "") {
        var words = (text ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count == 0) {
            ShowBenefits();

            return;
        }

        var verb = words[0].ToLowerInvariant();
        if (verb is not ("on" or "off") || words.Count < 2) {
            InformSenderClient("Type .benefit on and a benefit (or all) to switch it on, with a time to end it like 2d, "
                + "or .benefit off and a benefit (or all). Such as .benefit on double pet xp 3d. .benefit lists them.");

            return;
        }

        // A time at the end ("3d", "12h") ends it then.
        TimeSpan? length = null;
        if (verb == "on" && words.Count > 2 && CommandUtilities.TryParseDuration(words[^1], out var parsed) && parsed > TimeSpan.Zero) {
            length = parsed;
            words.RemoveAt(words.Count - 1);
        }

        var name = string.Join(' ', words.Skip(1));
        List<(string Key, string Name, uint Flag)> picked;
        if (name.Equals("all", StringComparison.OrdinalIgnoreCase)) {
            picked = [.. MemberBenefits.All];
        }
        else {
            var wanted = Squash(name);
            picked = [.. MemberBenefits.All.Where(b => Squash(b.Key) == wanted || Squash(b.Name) == wanted)];
            if (picked.Count == 0) {
                picked = [.. MemberBenefits.All.Where(b => Squash(b.Name).Contains(wanted, StringComparison.Ordinal))];
            }

            if (picked.Count != 1) {
                InformSenderClient((picked.Count == 0 ? $"No member benefit is called \"{name}\"." : $"\"{name}\" matches several benefits.")
                    + $" They are: {string.Join(", ", MemberBenefits.All.Select(b => b.Name))}, or all.");

                return;
            }
        }

        try {
            MemberBenefits.Switch(picked.Select(b => b.Key), verb == "on", length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) {
            InformSenderClient($"The member benefits could not be changed: {ex.Message}");

            return;
        }

        var shown = picked.Count == MemberBenefits.All.Length ? "Every member benefit" : picked[0].Name;
        Logger.Information("Member benefits changed in game by {0}: {1} {2}{3}.", Logger.Args(Context.Account?.Username, shown, verb,
            length is null ? "" : $" for {length}"));
        InformSenderClient(verb == "on"
            ? $"{shown} is on{(length is { } l ? $" for {Words(l)}" : " with no end")}. Everyone online is told in a moment."
            : $"{shown} is off. Everyone online is told in a moment.");
    }

    [Command("questcheck")]
    [Alias("checkquests")]
    [AuthRequired(AuthLevel.Administrator)]
    private void QuestCheckCommand() {
        var started = QuestChecker.Start(IdCatalog.OutputPath);
        InformSenderClient(started
            ? "Checking every main quest against the game's zone files. It takes a few minutes; the report shows on Ahziael's Quests > Quest Testing tab."
            : "The quest check is running already. Its report shows on Ahziael's Quests > Quest Testing tab.");
    }

    private void ShowBenefits() {
        var text = new StringBuilder().AppendLine("<center>Member benefits</center>").AppendLine();
        foreach (var (_, name, on, left) in MemberBenefits.Status()) {
            text.AppendLine($"{name}: {(on ? "on" + left : "off")}");
        }

        text.AppendLine().Append(".benefit on <name or all> [time like 3d]   .benefit off <name or all>".Replace('<', '[').Replace('>', ']'));
        InformSenderClient(text.ToString(), true);
    }

    private static string Words(TimeSpan length)
        => length.TotalDays >= 1 ? $"{(int) length.TotalDays}d{(length.Hours > 0 ? $" {length.Hours}h" : "")}"
            : length.TotalHours >= 1 ? $"{(int) length.TotalHours}h{(length.Minutes > 0 ? $" {length.Minutes}m" : "")}"
            : $"{Math.Max(1, (int) length.TotalMinutes)}m";

    private static string Squash(string text) => string.Concat(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant));

}
