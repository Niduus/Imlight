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
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Core;

/// <summary>
/// Removes, for one player, an object the game builds itself from its zone file once that player's quest mods take it
/// away, such as Tyrian Gorge's invisible KR-Gorge-MilkwebBlockerCollision after "Pass of the Spider".
/// </summary>
/// <remarks>
/// The game builds a zone file's STATIC_CLIENT_SERVER and STATIC_CLIENT objects itself, and a dynamod never reaches such a
/// placed object. It does give each one a global id the server can work out (<see cref="ClientBuiltObjects"/>), so the
/// player is sent MSG_REMOVEOBJECT with that id, once per visit. The game only builds them again with the zone, so a
/// later mod that shows the object again takes effect on the next visit.
/// </remarks>
public partial class Zone {

    // Players and the client-built objects already removed for them in this visit.
    private readonly Dictionary<IActorRef, HashSet<string>> _clientBuiltRemoved = [];
    private ClientBuiltObjects.Entry[] _clientBuilt;

    private void RemoveClientBuiltObjects(ZONE_102_PROTOCOL.MSG_PLAYERMOVE message) {
        try {
            var actor = message.PlayerActor;
            var wizard = message.PlayerWizard;
            if (actor is null || wizard is null) {
                return;
            }

            _clientBuilt ??= ClientBuiltObjects.For(ZonePath);
            if (_clientBuilt.Length == 0) {
                return;
            }

            var (removed, stateless) = RemovedTags(wizard);
            if (removed.Count == 0 && stateless.Count == 0) {
                return;
            }

            if (!_clientBuiltRemoved.TryGetValue(actor, out var done)) {
                _clientBuiltRemoved[actor] = done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            foreach (var entry in _clientBuilt) {
                // "Off" is a state for an object that has states; for one without any it takes the object away.
                var goes = removed.Contains(entry.Tag) || (!entry.HasStates && stateless.Contains(entry.Tag));
                if (!goes || !done.Add($"{entry.Tag}#{entry.Index}")) {
                    continue;
                }

                var id = ClientBuiltObjects.GlobalIdOf(_dynamicZoneId, entry.Index);
                actor.Tell(new GAME_5_PROTOCOL.MSG_REMOVEOBJECT { GameObjectID = id }, ActorRefs.NoSender);
                Logger.Information("Zone {0}: {1}'s quest took {2} away; the game built it from its zone file as {3}, removed for them.",
                    Logger.Args(ZonePath, wizard.CharId, entry.Tag, id));
            }
        }
        catch (Exception ex) {
            Logger.Warning("Client-built objects in {0}: {1}", Logger.Args(ZonePath, ex.Message));
        }
    }

    private void ForgetClientBuiltRemovals(IActorRef actor) {
        if (actor is not null) {
            _clientBuiltRemoved.Remove(actor);
        }
    }

    // The tags the wizard's saved mods take away in this zone, without " instance": with no state (removed) and with
    // "Off" (stateless). The last mod for a tag decides, as it does in the game.
    private (HashSet<string> Removed, HashSet<string> Stateless) RemovedTags(Wizard wizard) {
        var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stateless = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in wizard.DynamodSet?.Dynamods?.ToArray() ?? []) {
            if (mod is null || string.IsNullOrEmpty(mod.ClientTag)) {
                continue;
            }

            var here = string.IsNullOrEmpty(mod.ZoneName) || string.IsNullOrEmpty(ZonePath)
                || ZonePath.StartsWith(mod.ZoneName, StringComparison.OrdinalIgnoreCase)
                || mod.ZoneName.StartsWith(ZonePath, StringComparison.OrdinalIgnoreCase);
            if (!here) {
                continue;
            }

            var tag = ClientBuiltObjects.Plain(mod.ClientTag);
            removed.Remove(tag);
            stateless.Remove(tag);
            if (string.IsNullOrEmpty(mod.ModState)) {
                removed.Add(tag);
            }
            else if (string.Equals(mod.ModState, "Off", StringComparison.OrdinalIgnoreCase)) {
                stateless.Add(tag);
            }
        }

        return (removed, stateless);
    }

}
