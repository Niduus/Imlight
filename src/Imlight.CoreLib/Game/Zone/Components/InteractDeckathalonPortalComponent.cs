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
 *
 * ========================================================================
 * INTERACT DECKATHALON PORTAL COMPONENT
 * ========================================================================
 *
 * PURPOSE:
 * The live game's way into the Deckathalon (Niduus, 2026-10-25: "There is a portal that is suppose to spawn in the
 * Library to take you to that part of the arena for Deckathalon. Once there, THAT portal takes your in"):
 *   - the Library's WC-Teleport-LibToArena portal (shown only while a Deckathalon is on) goes to the tower portals of
 *     the Duel Arena;
 *   - the arena's HO-TreasureTower_01_<School> portal (shown only while that school's Deckathalon is on) starts the
 *     climb of that school's tower (Game/Events/Deckathalon.cs).
 *
 * NOTE:
 * Neither portal carries a destination in the game files (the Library one's ResTeleport names none, the arena ones use
 * an interact option the server does not know), so press X on them is handled here.
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/25/2026
 */

using System;
using System.Collections.Generic;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Events;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class InteractDeckathalonPortalComponent(ZoneEntity entity)
    : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    // The Library's portal to the arena, and the arena's tower portals (HO-TreasureTower_01_Fire, ...).
    private const string LIBRARY_PORTAL = "WC-Teleport-LibToArena";
    private const string TOWER_PORTAL_PREFIX = "HO-TreasureTower_01_";

    public string ServiceName => "Interact";
    // Both portals have a blank name in the game's text and the Library one has no picture, so the "Press X" box
    // was empty: the Library one says "To The Arena", a tower one "<School> Deckathalon Event", and the Library one
    // borrows the tower portals' picture.
    private const string PORTAL_ICON = "GUI/QuestButtons/Use_Portal_ClassProjects.dds";
    private const string LIBRARY_PORTAL_NAME_KEY = "WizardGameObjects_00000955";
    private static readonly Dictionary<string, string> s_towerNameKeys = new(StringComparer.OrdinalIgnoreCase) {
        ["Fire"] = "ClassProjects_00000023",
        ["Ice"] = "ClassProjects_00000025",
        ["Death"] = "ClassProjects_00000031",
        ["Balance"] = "ClassProjects_00000055",
        ["Life"] = "ClassProjects_00000056",
        ["Myth"] = "ClassProjects_00000057",
        ["Storm"] = "ClassProjects_00000058",
    };

    public string NpcIcon => (Entity.Template as GameObjectTemplate)?.m_sIcon.ToString() is { Length: > 0 } icon ? icon : PORTAL_ICON;
    public string NpcNameKey => TowerSchool is { } school
        ? s_towerNameKeys.GetValueOrDefault(school, LIBRARY_PORTAL_NAME_KEY)
        : LIBRARY_PORTAL_NAME_KEY;
    public string NpcTextKey => "GUI_EnterDoor";
    public WizBangs WizBang => WizBangs.None;
    public string StateName => null;
    public string InteractWizBang => null;
    public string DisplayKey => null;

    private string ObjectName => (Entity.Template as GameObjectTemplate)?.m_objectName.ToString() ?? "";

    // The school of an arena tower portal (HO-TreasureTower_01_Fire: Fire), or null.
    private string TowerSchool => ObjectName.StartsWith(TOWER_PORTAL_PREFIX, StringComparison.OrdinalIgnoreCase)
        ? Deckathalon.SchoolName(ObjectName[TOWER_PORTAL_PREFIX.Length..])
        : null;

    public static bool ShouldAttachToEntity(CoreTemplate template) {
        if (template is not GameObjectTemplate go || go.m_objectName.IsEmpty) {
            return false;
        }

        var name = go.m_objectName.ToString();

        return string.Equals(name, LIBRARY_PORTAL, StringComparison.OrdinalIgnoreCase)
            || (name.StartsWith(TOWER_PORTAL_PREFIX, StringComparison.OrdinalIgnoreCase)
                && Deckathalon.SchoolName(name[TOWER_PORTAL_PREFIX.Length..]) is not null);
    }

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard playerCharacter)
        => Deckathalon.Enabled ? [new InteractableOption { m_serviceName = ServiceName }] : [];

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        try {
            if (TowerSchool is { } school) {
                Deckathalon.TryStart(playerActor, playerCharacter, school, false, out var reply);
                Logger.Information("Deckathalon | {0} used the {1} tower portal: {2}", Logger.Args(playerCharacter.CharId, school, reply));
                playerActor.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = reply, Modal = 0 });

                return;
            }

            if (Deckathalon.CurrentEvent is null) {
                playerActor.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE {
                    Message = "No Deckathalon is on right now. The Events card of the Imlight app starts one.",
                    Modal = 0,
                });

                return;
            }

            playerActor.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
                DestinationZone = Deckathalon.ARENA_ZONE,
                DestinationLocation = Deckathalon.ARENA_LOCATION,
                SendToClient = true,
                OwnerCharId = playerCharacter.CharId,
            });
        }
        catch (Exception e) {
            Logger.Error("Deckathalon | the portal {0} failed: {1}", Logger.Args(ObjectName, e));
        }
    }

}
