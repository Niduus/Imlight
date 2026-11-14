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
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Battlegrounds;

/*
 * Going into a match: the event window's Join button (MSG_BATTLEGROUNDQUEUEPLAYER, Game/Services/BattlegroundService.cs)
 * and `.bg enter` both come here. The wizard gets a fresh copy of the match's arena of their own, a team (a PvE
 * match's one team starts where team A does) and a first form, which a statue in the base changes. Before the zone
 * changes the game is told the match's event (Battlegrounds.QueueMessage); that is what opens its match window there.
 */
internal static class BattlegroundEntry {

    /// <summary>Sends the wizard into the match. Returns why not, or null when they are on their way.</summary>
    /// <param name="ownerCharId">The wizard whose copy of the arena it is: 0 for the wizard's own, fresh copy; a group's
    /// leader for the others of a group who join with them (their copy is not reset).</param>
    internal static string SendIntoMatch(Wizard wizard, IActorRef session, string matchName,
                                         Battlegrounds.Team team = Battlegrounds.Team.Red, string formName = null,
                                         ulong ownerCharId = 0) {
        var template = Battlegrounds.Template(matchName);
        if (wizard is null || template is null) {
            return $"No match {matchName} in your game files.";
        }

        var zone = (string) template.m_battlegroundZone;
        if (template.m_pveMode) {
            team = Battlegrounds.Team.Red;
        }

        // A fresh copy of the arena replaces the old one, and the zone being stopped cannot be rebuilt while the
        // player is still in it (the new zone's name is taken, and the game is left on its loading screen).
        if (string.Equals(wizard.Zone, zone, StringComparison.OrdinalIgnoreCase)) {
            return $"You are already in {zone}. Leave it first.";
        }

        if (wizard.IsInDuel) {
            return "You can't join a match during a battle.";
        }

        var start = (string) (team == Battlegrounds.Team.Red ? template.m_teamStartA : template.m_teamStartB);

        // The form: the one asked for, else the match's first allowed one ("Life Cyclops"); a statue changes it.
        formName ??= template.m_unlockedPolymorphTags?.Select(t => BeastForm.FormOfTag((string) t)).FirstOrDefault(f => f is not null)
                     ?? "BG-Polymorph-Cyclops-Life-01";
        if (BeastForm.Choose(wizard, formName, BeastForm.Look.Both) is { } error) {
            return error;
        }

        Battlegrounds.SetTeam(wizard.CharId, team);
        Battlegrounds.GetOrAdd(wizard.CharId).MatchName = matchName;
        Logger.Information("Beastmoon | {0} goes into {1} ({2}) at {3} on the {4} team as {5}.",
            Logger.Args(wizard.CharId, matchName, zone, start, team, formName));

        var owner = ownerCharId == 0 ? wizard.CharId : ownerCharId;
        var fresh = owner == wizard.CharId;
        // The wizard attaches to the owner's copy (the way a raid's group does, RaidGateComponent).
        if (fresh) {
            ZoneInstanceOwners.Clear(wizard.CharId, zone);
        }
        else {
            ZoneInstanceOwners.Assign(wizard.CharId, zone, owner);
        }

        // The events and the wizard's progress in them: the Beast Form Select window reads the forms' levels there.
        session.Tell(Events.EventButton.InfoMessage(wizard, 0));
        if (Battlegrounds.QueueMessage(wizard, matchName) is { } queued) {
            session.Tell(queued);
        }
        else {
            Logger.Warning("Beastmoon | {0} has no battleground event in the game files; its match window will not open.", Logger.Args(matchName));
        }

        session.Tell(new CHARACTER_103_PROTOCOL.MSG_DOTELEPORTEFFECTS());
        session.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
            DestinationZone = zone,
            DestinationLocation = start,
            SendToClient = true,
            IsPrivate = true,
            OwnerCharId = owner,
            ResetInstance = fresh,
        });

        return null;
    }

}
