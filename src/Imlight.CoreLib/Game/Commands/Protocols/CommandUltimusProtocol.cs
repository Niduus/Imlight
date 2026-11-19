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
using System.IO;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Game.Ultimus;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// .ultimus tp: goes right next to Ultimus (the Solo Quester), wherever it is, a dungeon's own copy included, to watch it
/// play (forked from .questteam tp). .ultimus where says where it is. Ahziael's Ultimus page has a button for it.
/// </summary>
internal class CommandUltimus : CommandProtocol {

    internal override string Group { get; set; } = "ultimus";

    [Command("tp")]
    [Alias("goto", "go")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void TeleportCommand([Remainder] string who = "") {
        who = (who ?? "").Trim();
        // Ultimus, or a TCO7 member named (Niduus, 2026-11-16: the TCO7 are each their own Ultimus): ".ultimus tp Tulkar".
        var seat = UltimusSeat.All.FirstOrDefault(s => s.IsTco7 && who.Length > 0
                                                      && s.Name.Equals(who, StringComparison.OrdinalIgnoreCase))
                   ?? UltimusSeat.Ultimus;
        var members = UltimusDirector.MembersOf(seat);
        if (members.Count == 0) {
            InformSenderClient(seat.IsUltimus
                ? "The Ultimus is not playing right now. Turn it on in Ahziael (Ultimus > Ultimus) with the server running."
                : $"{seat.Name} of the TCO7 is not playing right now. Turn the TCO7 on in Ahziael (Ultimus > TCO7) with the server running.");

            return;
        }

        var member = who.Length == 0 || seat.IsTco7 ? members[0]
            : members.FirstOrDefault(m => m.Role.Equals(who, StringComparison.OrdinalIgnoreCase))
              ?? members.FirstOrDefault(m => (m.Name ?? "").Contains(who, StringComparison.OrdinalIgnoreCase));
        if (member is null) {
            InformSenderClient($"No Ultimus wizard is called {who}. Use .ultimus tp on its own.");

            return;
        }

        var zone = member.Zone;
        if (member.State != "playing" || string.IsNullOrEmpty(zone)) {
            InformSenderClient($"{member.Name} ({member.Role}) is not in the game right now ({member.State}).");

            return;
        }

        var me = Context.Character;
        var inInstance = member.Wizard?.ZoneIsInstance == true;
        var owner = inInstance ? ZoneInstanceOwners.TryGetOwner(member.CharId, zone, out var assigned) ? assigned : member.CharId : me.CharId;
        if (inInstance) {
            // The attach after the transfer lands in the same copy of the dungeon as the wizard.
            ZoneInstanceOwners.Assign(me.CharId, zone, owner);
        }

        // A step beside it, facing the same way.
        var location = string.Create(CultureInfo.InvariantCulture, $"{member.X + 40},{member.Y},{member.Z + 5},0");
        Logger.Information("Ultimus: {0} goes to {1} ({2}) in {3}{4}.",
            Logger.Args(me.CharId, member.Name, member.Role, zone, inInstance ? $" (copy of {owner})" : ""));
        InformSenderClient($"Going to {member.Name} ({member.Role}) in {ZoneNames.Shown(zone)}.");

        Context.SessionActor.Tell(new CHARACTER_103_PROTOCOL.MSG_DOTELEPORTEFFECTS());
        Task.Delay(1500).Wait();
        Context.SessionActor.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
            DestinationZone = zone,
            DestinationLocation = location,
            SendToClient = true,
            OwnerCharId = owner,
            IsPrivate = inInstance,
            // Another copy of the same zone the player stands in loads anew; the same copy just moves them.
            ForceReload = inInstance && string.Equals(me.Zone, zone, StringComparison.OrdinalIgnoreCase),
        });
    }

    // ---------------------------------------------------------------------
    // More commands for the bots (Niduus, 2026-11-19: "Expand on commands to give the bots (Ulti and TCO7 included)").
    // A name picks the bot: none = Ultimus, a TCO7 name (Tulkar, Cyndr, ...), or "all". They use the same files as Ahziael's
    // buttons, so nothing here moves a bot or does a step for it (the Ultimus human rule).
    // ---------------------------------------------------------------------

    private static List<UltimusSeat> Seats(string who) {
        who = (who ?? "").Trim();
        if (who.Equals("all", StringComparison.OrdinalIgnoreCase)) {
            return [.. UltimusSeat.All];
        }

        if (who.Equals("tco7", StringComparison.OrdinalIgnoreCase)) {
            return [.. UltimusSeat.Tco7];
        }

        var seat = who.Length == 0 || who.Equals("ultimus", StringComparison.OrdinalIgnoreCase) ? UltimusSeat.Ultimus
            : UltimusSeat.All.FirstOrDefault(s => s.Name.Equals(who, StringComparison.OrdinalIgnoreCase));

        return seat is null ? [] : [seat];
    }

    private bool PickSeats(string who, out List<UltimusSeat> seats) {
        seats = Seats(who);
        if (seats.Count == 0) {
            InformSenderClient($"No bot is called {who}. Use Ultimus, a TCO7 name (Tulkar, Cyndr, Kurrent, Mythis, Idunn, Garm, Ra), tco7 or all.");
            return false;
        }

        return true;
    }

    [Command("help")]
    [Alias("commands")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void HelpCommand() {
        InformSenderClient("Bot commands (a name = Ultimus, a TCO7 name, tco7 or all; none = Ultimus):");
        InformSenderClient(".ultimus where | tp [name] | why [name] | pause [name] | resume [name]");
        InformSenderClient(".ultimus explain [name] (pause and write his whole plan) | story [name] (back to the story)");
        InformSenderClient(".ultimus squad Tulkar Cyndr Kurrent Mythis | squad off | squad (shows it) | watchdog [name]");
        InformSenderClient("Quest Team: .questteam where | tp | why | pause | resume");
    }

    [Command("why")]
    [Alias("doing", "status")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void WhyCommand([Remainder] string who = "") {
        if (!PickSeats(who, out var seats)) {
            return;
        }

        foreach (var seat in seats) {
            var members = UltimusDirector.MembersOf(seat);
            if (members.Count == 0) {
                InformSenderClient($"{seat.Name}: not playing right now.");
                continue;
            }

            var (doing, thought, quest) = UltimusNow.Of(seat);
            var paused = File.Exists(Path.Combine(seat.Folder, UltimusWire.PAUSE_FILE));
            InformSenderClient($"{seat.Name}{(paused ? " (paused)" : "")}: {(doing.Length > 0 ? doing : members[0].State)}" +
                               (quest.Length > 0 ? $", on {UltimusText.Title(quest)}" : "") + ".");
            if (thought.Length > 0) {
                InformSenderClient($"  Last thought: {UltimusText.Short(thought, 200)}");
            }
        }
    }

    [Command("pause")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void PauseCommand([Remainder] string who = "") {
        if (!PickSeats(who, out var seats)) {
            return;
        }

        foreach (var seat in seats) {
            try {
                Directory.CreateDirectory(seat.Folder);
                File.WriteAllText(Path.Combine(seat.Folder, UltimusWire.PAUSE_FILE), $"Paused in game with .ultimus pause at {DateTime.Now:yyyy-MM-dd HH:mm}.");
                InformSenderClient($"{seat.Name} is paused: it stands where it is. .ultimus resume {seat.Name} lets it go on.");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                InformSenderClient($"Could not pause {seat.Name}: {e.Message}");
            }
        }
    }

    [Command("resume")]
    [Alias("unpause")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void ResumeCommand([Remainder] string who = "") {
        if (!PickSeats(who, out var seats)) {
            return;
        }

        foreach (var seat in seats) {
            try {
                File.Delete(Path.Combine(seat.Folder, UltimusWire.PAUSE_FILE));
                InformSenderClient($"{seat.Name} goes on with its quest.");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                InformSenderClient($"Could not resume {seat.Name}: {e.Message}");
            }
        }
    }

    [Command("explain")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void ExplainCommand([Remainder] string who = "") => Request(who, UltimusWire.EXPLAIN_FILE,
        "pauses and writes its whole plan to explain.txt in its folder (Ahziael shows it on its page).");

    [Command("story")]
    [Alias("backtostory")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void StoryCommand([Remainder] string who = "") => Request(who, UltimusWire.BACK_TO_STORY_FILE,
        "drops its side trip and goes back to its story quest.");

    private void Request(string who, string file, string what) {
        if (!PickSeats(who, out var seats)) {
            return;
        }

        foreach (var seat in seats) {
            try {
                Directory.CreateDirectory(seat.Folder);
                File.WriteAllText(Path.Combine(seat.Folder, file), DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
                InformSenderClient($"{seat.Name} {what}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                InformSenderClient($"Could not ask {seat.Name}: {e.Message}");
            }
        }
    }

    [Command("squad")]
    [Alias("team")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void SquadCommand([Remainder] string names = "") {
        names = (names ?? "").Trim();
        if (names.Length > 0) {
            if (names.Equals("off", StringComparison.OrdinalIgnoreCase) || names.Equals("none", StringComparison.OrdinalIgnoreCase)) {
                UltimusTco7.SetSquadOverride("off");
            }
            else if (names.Equals("ini", StringComparison.OrdinalIgnoreCase) || names.Equals("default", StringComparison.OrdinalIgnoreCase)) {
                UltimusTco7.SetSquadOverride(null);
            }
            else {
                var picked = names.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
                    .Where(n => UltimusSeat.Tco7.Any(s => s.Name.Equals(n.Trim(), StringComparison.OrdinalIgnoreCase))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (picked.Count < 2) {
                    InformSenderClient("A squad needs two to four TCO7 names: .ultimus squad Tulkar Cyndr Kurrent Mythis");
                    return;
                }

                UltimusTco7.SetSquadOverride(string.Join(",", picked.Take(4)));
            }
        }

        var squad = UltimusTco7.Squad;
        if (squad.Count < 2) {
            InformSenderClient("No TCO7 squad: each member quests at its own pace. .ultimus squad Tulkar Cyndr Kurrent Mythis makes one (until the server restarts; Ahziael's TCO7 page keeps it).");
            return;
        }

        var captain = UltimusTco7.Captain();
        InformSenderClient($"TCO7 squad: {string.Join(", ", squad.Select(s => s.Name))}." +
                           (captain is null ? " Fewer than two are playing, so it waits." : $" {captain.Name} leads (furthest behind in the story); the others help it catch up."));
    }

    [Command("watchdog")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void WatchdogCommand([Remainder] string who = "") {
        if (!PickSeats(who, out var seats)) {
            return;
        }

        InformSenderClient($"Watchdog book: {BotShared.BotWatchdogBook.Summary()} (logs\\ultimus\\watchdog-book.txt).");
        foreach (var seat in seats) {
            if (UltimusDirector.MembersOf(seat) is not { Count: > 0 } members || members[0].Wizard is null) {
                continue;
            }

            var lines = BotShared.BotWatchdogBook.Lines(members[0].Zone, members[0].X, members[0].Y).ToList();
            InformSenderClient(lines.Count == 0 ? $"{seat.Name}: no watchdog trouble known where it stands." : $"{seat.Name}, where it stands now:");
            foreach (var line in lines) {
                InformSenderClient("  " + line);
            }
        }
    }

    [Command("where")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void WhereCommand() {
        var members = UltimusSeat.All.SelectMany(UltimusDirector.MembersOf).ToList();
        if (members.Count == 0) {
            InformSenderClient("The Ultimus is not playing right now.");

            return;
        }

        foreach (var member in members) {
            InformSenderClient($"{member.Role}: {member.Name}, level {member.Wizard?.MagicSchoolBehavior?.Level ?? 0}, " +
                               $"{(string.IsNullOrEmpty(member.Zone) ? member.State : ZoneNames.Shown(member.Zone))}. .ultimus tp {member.Name} goes there.");
        }
    }

}
