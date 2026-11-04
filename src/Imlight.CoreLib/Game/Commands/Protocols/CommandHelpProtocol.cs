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
using System.Globalization;
using System.Linq;
using System.Text;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.help` (Niduus, 2026-11-04: "implement a majority of what ahziael can do as commands to help people execute them
/// properly"): the commands this player may use, by topic, from <see cref="CommandGuide"/> (the same list Ahziael's
/// Commands tab shows). `.help` lists the topics, `.help battles` a topic's commands, `.help joinme` how one command
/// works, and any other words search every command. Commands the account may not use are left out. Every player can
/// use it. Also .commands and .cmds.
/// </summary>
internal class CommandHelpProtocol : CommandProtocol {

    // More lines make the game's message box taller than the screen, hiding its OK button (CommandLookupProtocol).
    private const int PAGE_SIZE = 10;
    // Several commands matching a name are each shown in full, up to this many.
    private const int MAX_DETAILS = 3;

    internal override string Group { get; set; } = "";

    [Command("help")]
    [Alias("commands", "cmds", "cmd")]
    private void HelpCommand([Remainder] string text = "") {
        var words = (text ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var page = 1;
        if (words.Count > 1 && int.TryParse(words[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var asked) && asked > 0) {
            page = asked;
            words.RemoveAt(words.Count - 1);
        }

        var mine = CommandGuide.All.Where(MayUse).ToList();
        if (words.Count == 0) {
            ShowTopics(mine);

            return;
        }

        var query = string.Join(' ', words);

        // A topic: ".help battles", ".help items".
        var section = FindSection(query);
        if (section is not null) {
            var inSection = mine.Where(c => c.Section == section).ToList();
            ShowList($"{section}", inSection, page, $".help {query}",
                inSection.Count == 0 ? $"None of the {section} commands are open to your account." : null);

            return;
        }

        // A command: ".help joinme", ".help .quest offer".
        var named = ByCommand(CommandGuide.All, query);
        if (named.Count > 0) {
            var open = named.Where(MayUse).ToList();
            if (open.Count == 0) {
                InformSenderClient($"Only staff can use .{query.TrimStart('.')} ({named[0].Who.ToLowerInvariant()}).");

                return;
            }

            if (open.Count <= MAX_DETAILS) {
                ShowDetails(open);
            }
            else {
                ShowList($".{query.TrimStart('.')}", open, page, $".help {query}", null);
            }

            return;
        }

        // Any words: ".help pet level", ".help teleport".
        var found = mine.Where(c => Matches(c, words)).ToList();
        if (found.Count == 0) {
            InformSenderClient($"No command matches \"{query}\". Type .help to see the topics.");

            return;
        }

        if (found.Count == 1) {
            ShowDetails(found);

            return;
        }

        ShowList($"Commands about \"{query}\"", found, page, $".help {query}", null);
    }

    private bool MayUse(CommandGuideEntry command) {
        var level = Context.Account?.AuthLevel ?? AuthLevel.None;
        var needed = command.Who switch {
            CommandGuide.ANYONE_AT_ALL => AuthLevel.None,
            CommandGuide.ANYONE or CommandGuide.QA => AuthLevel.QualityAssurance,
            CommandGuide.HM => AuthLevel.HallMonitor,
            CommandGuide.ADMIN => AuthLevel.Administrator,
            _ => AuthLevel.QualityAssurance,
        };

        return level >= needed;
    }

    private void ShowTopics(List<CommandGuideEntry> mine) {
        var text = new StringBuilder()
            .AppendLine($"<center>Commands you can use: {mine.Count}</center>")
            .AppendLine();
        foreach (var section in CommandGuide.Sections) {
            var count = mine.Count(c => c.Section == section);
            if (count > 0) {
                text.AppendLine($"{section} ({count})   .help {Keyword(section)}");
            }
        }

        text.AppendLine()
            .AppendLine("Type .help and a command to see how it works, such as .help code.")
            .Append("Or type .help and any words to search, such as .help teleport.");
        InformSenderClient(text.ToString(), true);
    }

    private void ShowList(string title, List<CommandGuideEntry> commands, int page, string again, string empty) {
        if (commands.Count == 0) {
            InformSenderClient(empty ?? "Nothing to show.");

            return;
        }

        var pages = (commands.Count + PAGE_SIZE - 1) / PAGE_SIZE;
        page = Math.Clamp(page, 1, pages);
        var text = new StringBuilder()
            .AppendLine($"<center>{title}{(pages > 1 ? $" (page {page} of {pages})" : "")}</center>")
            .AppendLine();
        foreach (var command in commands.Skip((page - 1) * PAGE_SIZE).Take(PAGE_SIZE)) {
            text.AppendLine($"{Plain(FirstWay(command.Typed))}  -  {Plain(command.Name)}");
        }

        text.AppendLine();
        if (page < pages) {
            text.AppendLine($"More: type {again} {page + 1}");
        }

        text.Append("Type .help and a command to see how it works.");
        InformSenderClient(text.ToString(), true);
    }

    private void ShowDetails(List<CommandGuideEntry> commands) {
        var text = new StringBuilder();
        foreach (var command in commands) {
            if (text.Length > 0) {
                text.AppendLine().AppendLine();
            }

            text.AppendLine($"<center>{Plain(command.Name)}</center>")
                .AppendLine($"Type: {Plain(command.Typed)}")
                .AppendLine($"Who: {command.Who}")
                .Append(Plain(command.Help));
        }

        InformSenderClient(text.ToString(), true);
    }

    /// <summary>The topic a word names: "battles", "items", "spells", "Getting around"... or null.</summary>
    private static string FindSection(string query) {
        var wanted = Squash(query);
        if (wanted.Length < 3) {
            return null;
        }

        return CommandGuide.Sections.FirstOrDefault(s => Squash(s) == wanted)
            ?? CommandGuide.Sections.FirstOrDefault(s => Squash(Keyword(s)) == wanted)
            ?? CommandGuide.Sections.FirstOrDefault(s => s.Split(' ', ',').Any(w => w.Length > 3 && Squash(w).StartsWith(wanted, StringComparison.Ordinal)));
    }

    /// <summary>
    /// The one word `.help` takes for a topic: its first word ("Battles", "Items, gear and crafting" = items), or its last
    /// when the first says little ("Your wizard" = wizard, "Running the server" = server).
    /// </summary>
    private static string Keyword(string section) {
        var words = section.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);

        return (words[0].ToLowerInvariant() is "your" or "running" or "finding" ? words[^1] : words[0]).ToLowerInvariant();
    }

    /// <summary>The commands a typed name starts: "joinme", ".quest offer", "quest".</summary>
    private static List<CommandGuideEntry> ByCommand(IEnumerable<CommandGuideEntry> all, string query) {
        var wanted = "." + query.Trim().TrimStart('.').ToLowerInvariant();
        var exact = all.Where(c => Ways(c.Typed).Any(way => way.Equals(wanted, StringComparison.OrdinalIgnoreCase)
                                                             || way.StartsWith(wanted + " ", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return exact;
    }

    // Each way of typing a command, without what follows it: ".hat 1 Malistaire Veil   or   .pet 1" = .hat, .pet...
    private static IEnumerable<string> Ways(string typed)
        => typed.Split([" or ", " / ", "   "], StringSplitOptions.RemoveEmptyEntries)
            .Select(way => way.Trim())
            .Where(way => way.StartsWith('.'))
            .Select(way => way.ToLowerInvariant());

    // The first way of typing a command: ".fishing   or   .fishing basket" and ".level MAX or .level 5" give the first.
    private static string FirstWay(string typed) {
        var split = typed.IndexOf("   ", StringComparison.Ordinal);
        var or = typed.IndexOf(" or .", StringComparison.Ordinal);
        if (split < 0 || (or >= 0 && or < split)) {
            split = or;
        }

        return split > 0 ? typed[..split].Trim() : typed.Trim();
    }

    // The game reads <...> as its own markup, so "<item ID>" would vanish from the box.
    private static string Plain(string text) => System.Text.RegularExpressions.Regex.Replace(text, "<([^<>]*)>", "[$1]");

    private static bool Matches(CommandGuideEntry command, List<string> words) {
        var haystack = Squash($"{command.Section} {command.Name} {command.Typed} {command.Help}", keepSpaces: true);

        return words.Select(w => Squash(w)).Where(w => w.Length > 0).All(haystack.Contains);
    }

    private static string Squash(string text, bool keepSpaces = false) {
        var squashed = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant()) {
            if (char.IsLetterOrDigit(c) || (keepSpaces && c == ' ')) {
                squashed.Append(c);
            }
        }

        return squashed.ToString();
    }

}
