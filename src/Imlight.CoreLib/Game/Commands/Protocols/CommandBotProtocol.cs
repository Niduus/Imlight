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
using System.Linq;
using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Game.BotPlayers;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.bot` (Niduus, 2026-11-12: Bot Players, "add commands and UI overlay"): the test stage's commands. A bot comes out
/// next to you (.bot test), joins your battle (.bot join), gets kicked out of it (.bot kick), or leaves (.bot remove).
/// Bot Players must be on in Ahziael; its mini server decides who the bots are and how they walk (Game/BotPlayers).
/// </summary>
internal class CommandBotProtocol : CommandProtocol {

    internal override string Group { get; set; } = "bot";

    // How long a bot waits before it joins a player's battle, so a real friend can take the circle first (Niduus: 5 s).
    private static int JoinWaitSeconds => Math.Clamp(ConfigurationManager.GetValue("BotPlayers.JoinWaitSeconds", 5), 0, 60);

    [Command("test")]
    [Alias("spawn")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void TestCommand()
        => InformSenderClient(BotBodies.SpawnNear(Context.Character, Context.SessionActor, Context.ZoneActor));

    [Command("join")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void JoinCommand([Remainder] string name = "") {
        var battle = CombatDuelComponent.BattleOf(Context.Character.GameObjectID);
        if (battle is null) {
            InformSenderClient("You're not in a battle. Start one, then type .bot join.");

            return;
        }

        var here = BotBodies.InZone(Context.ZoneActor).Where(b => !CombatDuelComponent.IsInBattle(b.GlobalId)).ToList();
        var bot = Pick(here, name);
        if (bot is null) {
            InformSenderClient(here.Count == 0
                ? "No free bot is in this area. Type .bot test first."
                : $"No bot here is called \"{name}\". Type .bot list to see them.");

            return;
        }

        var wait = TimeSpan.FromSeconds(JoinWaitSeconds);
        bot.Body.Tell(new BotGoJoin(battle, wait, Context.Character.PlayerNameBehavior.GetWizardName()));
        InformSenderClient($"{bot.Name} joins your battle in {wait.TotalSeconds:0} seconds.");
    }

    [Command("kick")]
    private void KickCommand([Remainder] string name = "") {
        var battle = CombatDuelComponent.BattleOf(Context.Character.GameObjectID);
        if (battle is null) {
            InformSenderClient("You're not in a battle. .bot kick takes bots out of the battle you are in.");

            return;
        }

        ulong which = 0;
        if (!string.IsNullOrWhiteSpace(name)) {
            var bot = Pick([.. BotBodies.InZone(Context.ZoneActor)], name);
            if (bot is null) {
                InformSenderClient($"No bot here is called \"{name}\".");

                return;
            }

            which = bot.GlobalId;
        }

        battle.Tell(new BotKickFromBattle(which, Context.Character.PlayerNameBehavior.GetWizardName()));
        InformSenderClient(which == 0
            ? "The bots in your battle were kicked out. No bot joins this battle again."
            : "That bot was kicked out of your battle. No bot joins this battle again.");
    }

    [Command("remove")]
    [Alias("leave")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void RemoveCommand([Remainder] string name = "") {
        var here = BotBodies.InZone(Context.ZoneActor);
        var leaving = string.IsNullOrWhiteSpace(name) || name.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? here
            : Pick([.. here], name) is { } one ? [one] : [];
        if (leaving.Count == 0) {
            InformSenderClient(here.Count == 0 ? "No bot is in this area." : $"No bot here is called \"{name}\".");

            return;
        }

        foreach (var bot in leaving) {
            bot.Body.Tell(new BotLeave($"{Context.Character.PlayerNameBehavior.GetWizardName()} sent it away"));
        }

        InformSenderClient(leaving.Count == 1 ? $"{leaving[0].Name} leaves." : $"{leaving.Count} bots leave.");
    }

    [Command("list")]
    private void ListCommand() {
        var all = BotBodies.All;
        if (all.Count == 0) {
            InformSenderClient(BotWire.Enabled ? "No bots are out." : "Bot Players is off. Turn it on in Ahziael (Game Rules > Battles and World).");

            return;
        }

        InformSenderClient($"{all.Count} bot{(all.Count == 1 ? "" : "s")} out: " + string.Join(", ", all.Select(b =>
            $"{b.Name} ({b.ZonePath}{(CombatDuelComponent.IsInBattle(b.GlobalId) ? ", in a battle" : "")})")));
    }

    // A bot by (part of) its name; with no name, the first one.
    private static BotBodies.Out Pick(System.Collections.Generic.IReadOnlyList<BotBodies.Out> bots, string name)
        => string.IsNullOrWhiteSpace(name)
            ? bots.FirstOrDefault()
            : bots.FirstOrDefault(b => b.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
              ?? bots.FirstOrDefault(b => b.Name.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase));

}
