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
 * INTERACT DOOR TELEPORT COMPONENT
 * ========================================================================
 * 
 * PURPOSE:
 * Makes an "Enter door" object (an InteractableBehavior template such as the Krokotopia School
 * doors) a press-X interactable that sends the player to a fixed zone and location. The client
 * ships no destination for these doors, so it comes from the zone's teleport table.
 * 
 * USAGE EXAMPLE:
 * Add a ZoneTransfer teleport to the placing zone's file whose TriggerName is the door template's
 * object name (GameObjectTemplate.m_objectName); the door is then usable in that zone.
 * 
 * NOTE:
 * The option is offered through the shared service memento (MSG_SENDNPCOPTIONS), which the client
 * handles exactly like live's MSG_SENDINTERACTOPTIONS. A door whose name has no teleport in the
 * current zone stays inert. The transfer is the same MSG_ZONETRANSFER a trigger ResTeleport sends.
 * 
 * TODO:
 * 
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 09/29/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Requirements;
using Imlight.CoreLib.Game.Requirements.Contexts;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class InteractDoorTeleportComponent(ZoneEntity entity)
    : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    private const string DOOR_TEXT_KEY = "GUI_EnterDoor";

    // Every teleport name the zone data knows, built on first use (the zone data is loaded before any zone).
    private static HashSet<string> s_teleportNames;

    public string ServiceName => "Interact";
    // Ladders and zip-lines offer Press-X exactly as a door does (2026-11-25): the transport-only icon, blank name,
    // own text key, forced option and wizbang kept the game from ever showing their Press-X, while doors worked.
    public string NpcIcon => (Entity.Template as GameObjectTemplate)?.m_sIcon;
    // The zone file's name for this placed door wins (the Necropolis portal home reads "The Basilica").
    public string NpcNameKey => Imlight.CoreLib.Shared.Resources.CoreObjectFactory.OverrideNameKey(Entity.Info)
                                ?? (Entity.Template is GameObjectTemplate template ? (string) template.m_displayName : null);
    // A ladder or zip-line shows its own text (m_interactTextOverride) when its template has one; doors stay on GUI_EnterDoor.
    public string NpcTextKey => IsTransportMesh() && OwnInteractText() is { } ownText ? ownText : DOOR_TEXT_KEY;
    public WizBangs WizBang => WizBangs.None;
    public string StateName => null;
    public string InteractWizBang => null;
    public string DisplayKey => null;
    // Transport Press-X comes from the trigger volume, not from standing near the mesh.
    public float DEFAULT_INTERACTION_RADIUS => Entity.Template is GameObjectTemplate transport
        && HasTransportName(transport.m_objectName.ToString())
            ? 0f
            : 300f;

    private string _boundTrigger;

    public static bool ShouldAttachToEntity(CoreTemplate template) {
        // A spiraldb-extra/ZoneObjects exit door (the Mausoleum's warp portal in the Ravenwood Malistaire copy).
        if (template is GameObjectTemplate exitDoor && ZoneObjects.IsExitDoor(exitDoor.m_templateID)) {
            return true;
        }

        if (template is not GameObjectTemplate go || go.m_objectName.IsEmpty) {
            return false;
        }

        var name = go.m_objectName.ToString();
        if (GetTeleportNames().Contains(name)
            || HasTransportName(name)
            || IsBareTeleporter(go)) {
            return true;
        }

        return go.m_behaviors?.Any(behavior =>
            HasTransportName(behavior?.m_behaviorName)
            || HasTransportName(behavior?.GetType().Name)) == true;
    }

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard playerCharacter) {
        // A ladder's own option can be shut by an object-state check the zone never sets. The
        // trigger volume is the gate. Once the player is in it, Press-X has to be offered.
        if (!string.IsNullOrEmpty(_boundTrigger)) {
            return [new InteractableOption { m_serviceName = ServiceName }];
        }

        if (!TryResolveTeleport(out _) && !TryQuestStepTeleport(playerCharacter, out _) && !TryReturnTeleport(playerCharacter, out _)) {
            return [];
        }
        if (!OwnOptionAllows(playerCharacter)) {
            return [];
        }

        return [
            new InteractableOption { m_serviceName = ServiceName }
        ];
    }

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        // The door's own "enter" option names what opens it (the Tree of Life's spirit portal: only once the tree turned
        // it On). A door that is still shut takes nobody anywhere.
        if (string.IsNullOrEmpty(_boundTrigger) && !OwnOptionAllows(playerCharacter)) {
            Logger.Debug("Zone {0}: door {1} refused {2}: its own option's requirements are not met (state '{3}').",
                Logger.Args(Entity.Zone?.ZonePath, (Entity.Template as GameObjectTemplate)?.m_objectName, playerCharacter?.CharId,
                    Entity.Zone?.ScriptState.GetObjectState(PlacedName ?? "")));

            return;
        }

        // Press-X fires the trigger whose volume the player is standing in.
        if (TryResolveTrigger(out var trigger)) {
            if (!TriggerAllows(playerActor, playerCharacter, playerObject)) {
                Logger.Debug("Zone {0}: transport object {1} refused {2}: trigger {3} requirements are not met.",
                    Logger.Args(Entity.Zone?.ZonePath, (Entity.Template as GameObjectTemplate)?.m_objectName,
                        playerCharacter?.CharId, trigger.m_triggerName));
                return;
            }

            var results = ResolveTriggerResults(trigger);
            if (results?.m_results is { Count: > 0 }) {
                Logger.Information("Transport interaction: {0} in {1} fires trigger {2}.",
                    Logger.Args((Entity.Template as GameObjectTemplate)?.m_objectName,
                        Entity.Zone?.ZonePath, trigger.m_triggerName));
                Entity.ExecuteResults(results, playerActor, playerObject, trigger.m_triggerName);
                return;
            }
        }

        if (IsTransportMesh()
            && Entity.Zone?.TryPairedTransportEnd(PlacedName, out var pairedEnd) == true) {
            Logger.Information("Transport interaction: {0} in {1} goes to paired ladder end {2}.",
                Logger.Args((Entity.Template as GameObjectTemplate)?.m_objectName, Entity.Zone?.ZonePath, pairedEnd));
            playerActor.Tell(new CHARACTER_103_PROTOCOL.MSG_DOTELEPORTEFFECTS(), ActorRefs.NoSender);
            playerActor.Tell(new CHARACTER_103_PROTOCOL.MSG_MOVEINZONE { Location = pairedEnd }, ActorRefs.NoSender);
            return;
        }

        // Ends not named as a pair: the other end is the nearest ladder end over or under the one the wizard stands at.
        if (IsTransportMesh() && playerObject is not null
            && Entity.Zone?.TryNearestTransportEnd(new System.Numerics.Vector3(playerObject.m_location.X,
                playerObject.m_location.Y, playerObject.m_location.Z), out var nearestEnd) == true) {
            playerActor.Tell(new CHARACTER_103_PROTOCOL.MSG_DOTELEPORTEFFECTS(), ActorRefs.NoSender);
            playerActor.Tell(new CHARACTER_103_PROTOCOL.MSG_MOVEINZONE { Location = nearestEnd }, ActorRefs.NoSender);
            return;
        }

        if (!TryResolveTeleport(out var teleport) && !TryQuestStepTeleport(playerCharacter, out teleport)
            && !TryReturnTeleport(playerCharacter, out teleport)) {
            return;
        }

        // The teleporter's trigger in the game's zone files says which quest opens it (Niduus 2026-10-27: "when
        // teleport access is given via quests"); walking into a teleport volume already checks this.
        if (!TriggerAllows(playerActor, playerCharacter, playerObject)) {
            Logger.Debug("Zone {0}: teleporter {1} refused {2}: its quest requirements are not met.",
                Logger.Args(Entity.Zone?.ZonePath, (Entity.Template as GameObjectTemplate)?.m_objectName, playerCharacter?.CharId));

            return;
        }

        // The same request a trigger's ResTeleport sends: the session readies the client and transfers.
        playerActor.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
            DestinationZone = teleport.m_destinationZone,
            DestinationLocation = teleport.m_destinationLoc,
            SendToClient = true,
            ByZoneDoor = true,
            OwnerCharId = playerCharacter.CharId
        });
    }

    /// <summary>The zone this door leads to, or empty when it can't be told (for the overlay's minimap).</summary>
    internal string DestinationZone() {
        try {
            return TryResolveTeleport(out var teleport) ? teleport.m_destinationZone ?? "" : "";
        }
        catch (Exception) {
            return "";
        }
    }

    /// <summary>Where this door lands (a named spot or "x,y,z,yaw"), or empty when it can't be told (for the way finders).</summary>
    internal string DestinationLoc() {
        try {
            return TryResolveTeleport(out var teleport) ? teleport.m_destinationLoc ?? "" : "";
        }
        catch (Exception) {
            return "";
        }
    }

    private bool TryResolveTeleport(out ResTeleport teleport) {
        teleport = null;
        var zonePath = Entity.Zone?.ZonePath;
        if (string.IsNullOrEmpty(zonePath)) {
            return false;
        }

        // A spiraldb-extra/ZoneObjects exit door of this copy goes to the exit, whatever the zone data says.
        if (Entity.Template is GameObjectTemplate doorTemplate
            && Entity.Zone.Extras.TryGetDoorExit(doorTemplate.m_templateID, out var doorZone, out var doorLoc)) {
            teleport = new ResTeleport { m_destinationZone = doorZone, m_destinationLoc = doorLoc };

            return true;
        }

        var templateName = (Entity.Template as GameObjectTemplate)?.m_objectName.ToString();
        var placedName = (string?)Entity.Info?.m_zoneTag;
        var candidates = new[] { placedName, templateName }
            .Where(name => !string.IsNullOrEmpty(name))
            .SelectMany(name => new[] {
                name,
                name.EndsWith(" instance", StringComparison.OrdinalIgnoreCase)
                    ? name[..^" instance".Length]
                    : name
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidates.Count == 0) {
            return false;
        }

        var teleports = ZoneDataCollection.GetZoneData(zonePath)?.Teleports;
        teleport = teleports?
            .Where(t => t is not null)
            .FirstOrDefault(t => candidates.Any(name =>
                string.Equals(t.TriggerName, name, StringComparison.OrdinalIgnoreCase)))?.Teleport;

        // A ladder, zip-line or lift is normally a trigger-owned object. The zone transfer is on
        // Trigger.m_results and its displayed/static object is m_triggerObjInfo; it is not necessarily
        // a WizardZoneData.TriggerName. Match the exact placed tag first, then the trigger name,
        // and never match a ladder by a broad letter or position.
        if (string.IsNullOrEmpty(teleport?.m_destinationZone)
            && Entity.Zone?.TriggersByName is { } triggers) {
            var trigger = triggers.Values.FirstOrDefault(t =>
                t?.m_triggerObjInfo is { } info
                && candidates.Any(name =>
                    string.Equals((string?)info.m_zoneTag, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(t.m_triggerName, name, StringComparison.OrdinalIgnoreCase)));

            trigger ??= triggers.Values.FirstOrDefault(t =>
                candidates.Any(name => string.Equals(t?.m_triggerName, name, StringComparison.OrdinalIgnoreCase)));

            if (trigger is not null) {
                teleport = trigger.m_results?.m_results?.OfType<ResTeleport>()
                    .FirstOrDefault(t => !string.IsNullOrEmpty(t.m_destinationZone));

                teleport ??= teleports?
                    .FirstOrDefault(t => string.Equals(t?.TriggerName, trigger.m_triggerName,
                        StringComparison.OrdinalIgnoreCase))?.Teleport;
            }

        }

        // Some transport objects carry the destination on their own interact option while the
        // zone table only knows the trigger or has no entry for a ladder/zip-line copy.
        if (string.IsNullOrEmpty(teleport?.m_destinationZone)
            && Entity.Template is GameObjectTemplate transportTemplate) {
            teleport = transportTemplate.m_behaviors?
                .OfType<InteractableBehaviorTemplate>()
                .SelectMany(behavior => behavior.m_interactOptions ?? [])
                .OfType<InteractStateOptionTemplate>()
                .Select(option => option.m_results?.m_results?.OfType<ResTeleport>().FirstOrDefault())
                .FirstOrDefault(result => result is not null
                    && !string.IsNullOrEmpty(result.m_destinationZone));
        }

        // A ride's skip sign (KT_TeleportToOasis on the Krokotopia ships) that SpiralDB has no capture of in this ride
        // goes where the trip ends (RideZones).
        if (string.IsNullOrEmpty(teleport?.m_destinationZone)
            && Imlight.CoreLib.Game.World.RideZones.TryGetTripEnd(zonePath, out var tripZone, out var tripLoc)) {
            teleport = new ResTeleport { m_destinationZone = tripZone, m_destinationLoc = tripLoc };
        }

        // A teleporter of a pair with no destination anywhere in the server's data goes to its partner: "...TeleportTo<X>"
        // to "...TeleportFrom<X>" and back, wherever in the world that one stands. Ruined Alcazar's island teleporters
        // (KR-Z07-TeleportToSerpentIsle) offered Press-X and did nothing, as the zone's teleport table is not in SpiralDB
        // (2026-11-26).
        if (string.IsNullOrEmpty(teleport?.m_destinationZone)) {
            foreach (var name in candidates) {
                string partner = null;
                if (name.Contains("TeleportTo", StringComparison.OrdinalIgnoreCase)) {
                    partner = System.Text.RegularExpressions.Regex.Replace(name, "TeleportTo", "TeleportFrom",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                }
                else if (name.Contains("PortalTo", StringComparison.OrdinalIgnoreCase)) {
                    partner = System.Text.RegularExpressions.Regex.Replace(name, "PortalTo", "PortalFrom",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                }
                else if (name.Contains("PortalFrom", StringComparison.OrdinalIgnoreCase)) {
                    partner = System.Text.RegularExpressions.Regex.Replace(name, "PortalFrom", "PortalTo",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                }
                else if (name.Contains("TeleportFrom", StringComparison.OrdinalIgnoreCase)) {
                    partner = System.Text.RegularExpressions.Regex.Replace(name, "TeleportFrom", "TeleportTo",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                }

                var world = zonePath.Split('/')[0];
                if (partner is null
                    || Imlight.CoreLib.Game.Zone.Core.ZoneObjectTags.FindInWorld(world, zonePath, partner) is not { } found
                    || string.Equals(found.Zone, zonePath, StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }

                teleport = new ResTeleport {
                    m_destinationZone = found.Zone,
                    m_destinationLoc = string.Join(",", new[] { found.Spot.X, found.Spot.Y, found.Spot.Z, 0f }
                        .Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture))),
                };
                Logger.Information("Teleporter {0} in {1}: no destination in the data; goes to its partner {2} in {3}.",
                    Logger.Args(name, zonePath, partner, found.Zone));
                break;
            }
        }

        // A bare teleporter named for where it goes ("PL-TeleportToAuroracleUP" in the Sunless Shrine) with no destination
        // and no partner anywhere: the one zone of its world whose name holds that place (PL_Z03I05_AuroracleChamber).
        // The way up to the Auroracle never worked on the server (PL-FROZ-MAIN-010, 2026-11-28).
        if (string.IsNullOrEmpty(teleport?.m_destinationZone) && Entity.Template is GameObjectTemplate bare && IsBareTeleporter(bare)) {
            foreach (var name in candidates) {
                if (NamedPlace(zonePath, name) is { } placeZone) {
                    teleport = new ResTeleport { m_destinationZone = placeZone, m_destinationLoc = "Start" };
                    Logger.Information("Teleporter {0} in {1}: no destination in the data; goes to {2}, the zone named for it.",
                        Logger.Args(name, zonePath, placeZone));
                    break;
                }
            }
        }

        // A copy of the zone with a spiraldb-extra/ZoneObjects exit (an added sigil's instance) leaves there instead.
        if (teleport is not null) {
            foreach (var name in candidates) {
                if (Entity.Zone.Extras.TryGetExit(name, teleport.m_destinationZone, out var exitZone, out var exitLoc)) {
                    teleport = teleport with { m_destinationZone = exitZone, m_destinationLoc = exitLoc };
                    break;
                }
            }
        }

        return !string.IsNullOrEmpty(teleport?.m_destinationZone);
    }

    /// <summary>
    /// A bare teleporter nothing else gives a place to (no table entry, no partner, no zone named for it, no quest that
    /// shows it) is the way back out: it takes the wizard to the zone they came from. Urville's PL_Z04_Cave01 has only
    /// PL-SolitaryTeleportUp, so a wizard who came down by TelepotTo_PL_Z04_Cave01 could not leave (2026-11-28).
    /// </summary>
    private bool TryReturnTeleport(Wizard wizard, out ResTeleport teleport) {
        teleport = null;
        var here = Entity.Zone?.ZonePath;
        if (string.IsNullOrEmpty(here) || Entity.Template is not GameObjectTemplate template || !IsBareTeleporter(template)
            || TryResolveTeleport(out _)) {
            return false;
        }

        // The way in: a teleporter of this world named for this zone ("TelepotTo_PL_Z04_Cave01" in Urville Station). The
        // wizard comes out beside it.
        var leaf = here[(here.LastIndexOf('/') + 1)..];
        var world = here.Split('/')[0];
        foreach (var tag in new[] { $"TelepotTo_{leaf}", $"TeleportTo_{leaf}", $"TeleportTo{leaf}", $"Teleport_{leaf}", $"Teleport to {leaf}" }) {
            foreach (var name in new[] { tag, tag + " instance" }) {
                if (Imlight.CoreLib.Game.Zone.Core.ZoneObjectTags.FindInWorld(world, here, name) is { } wayIn
                    && !string.Equals(wayIn.Zone, here, StringComparison.OrdinalIgnoreCase)) {
                    teleport = new ResTeleport {
                        m_destinationZone = wayIn.Zone,
                        m_destinationLoc = string.Join(",", new[] { wayIn.Spot.X, wayIn.Spot.Y, wayIn.Spot.Z, 0f }
                            .Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture))),
                    };
                    return true;
                }
            }
        }

        // Else the zone the wizard came from.
        var back = wizard?.PreviousZone;
        if (!string.IsNullOrEmpty(back) && !string.Equals(back, here, StringComparison.OrdinalIgnoreCase)) {
            teleport = new ResTeleport { m_destinationZone = back, m_destinationLoc = "Start" };
            return true;
        }

        if (wizard is not null && s_noWayBackLoggedMap.TryAdd((here, wizard.CharId), 0)) {
            Logger.Information("Teleporter {0} in {1}: no way back for {2} (no teleporter into this zone found, previous zone '{3}').",
                Logger.Args(PlacedName, here, wizard.CharId, back ?? ""));
        }

        return false;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, ulong), byte> s_noWayBackLoggedMap = new();

    private static string[] s_zonePaths;

    /// <summary>
    /// The zone a "...TeleportTo&lt;Place&gt;" teleporter is named for: the only other zone of the same world whose path
    /// holds the place's name (at least five letters; "Up", "Down" and copy numbers after it are dropped). Null when no zone
    /// or more than one fits.
    /// </summary>
    private static string NamedPlace(string zonePath, string name) {
        var m = System.Text.RegularExpressions.Regex.Match(StripInstance(name) ?? "", "TeleportTo_?([A-Za-z]+?)(?:_?(?:UP|Up|DOWN|Down|In|Out))?(?:[-_ ]?\\d+)?$");
        if (!m.Success || m.Groups[1].Value.Length < 5) {
            return null;
        }

        var place = m.Groups[1].Value;
        s_zonePaths ??= LoadZonePaths();
        var world = zonePath.Split('/')[0];
        var fits = s_zonePaths.Where(z => z.StartsWith(world + "/", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(z, zonePath, StringComparison.OrdinalIgnoreCase)
                && z[(z.LastIndexOf('/') + 1)..].Contains(place, StringComparison.OrdinalIgnoreCase))
            .Take(2).ToList();
        return fits.Count == 1 ? fits[0] : null;
    }

    private static string[] LoadZonePaths() {
        try {
            var file = System.IO.Path.Combine(Imlight.CoreLib.Shared.Resources.IdCatalog.OutputPath, "zones.txt");
            return System.IO.File.Exists(file)
                ? System.IO.File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray()
                : [];
        }
        catch (Exception) {
            return [];
        }
    }

    /// <summary>
    /// A teleporter whose use names nothing (no event, step, state or result) and is not a quest step's object: the Solar
    /// Arc's KR_TeleportToKondha_01 ("ToKondhaGate"), which Diego the Duel Master's quest shows. Nothing in the server's
    /// data says where it goes, so it never offered Press-X (2026-11-27).
    /// </summary>
    private static bool IsBareTeleporter(GameObjectTemplate template) {
        var name = template.m_objectName.ToString();
        // "Portal" too: the Sand Sea's AR-PortalFromSandSea (AR-PreMR-MAIN-002) names no destination either (2026-11-28).
        if (!name.Contains("Teleport", StringComparison.OrdinalIgnoreCase) && !name.Contains("Portal", StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        var options = template.m_behaviors?.OfType<InteractableBehaviorTemplate>()
            .SelectMany(behavior => behavior.m_interactOptions ?? []).Where(option => option is not null).ToList() ?? [];

        return options.Count > 0
            && options.All(option => string.IsNullOrEmpty(option.m_questEvent) && option.m_goalTags is not { Count: > 0 }
                && option is not InteractStateOptionTemplate { m_results.m_results.Count: > 0 }
                && string.IsNullOrEmpty((option as InteractStateOptionTemplate)?.m_enterState.ToString()))
            && !InteractQuestSelectComponent.UsageGoalsOf(template).Any();
    }

    /// <summary>
    /// Where a teleporter with no destination in the data takes the wizard: a quest that shows it when it starts (its
    /// start results bring the object out) goes on somewhere else, so the teleporter takes the wizard to the zone of that
    /// quest's step now under way, at the zone's own arrival spot. Diego the Duel Master's quest shows the Solar Arc's
    /// KR_TeleportToKondha_01 ("ToKondhaGate") and asks the wizard to talk to Diego in Kondha Desert.
    /// </summary>
    private bool TryQuestStepTeleport(Wizard wizard, out ResTeleport teleport) {
        teleport = null;
        if (wizard?.QuestBehavior?.CurrentQuestInstances is not { } quests
            || Entity.Template is not GameObjectTemplate template || !IsBareTeleporter(template)
            || PlacedName is not { Length: > 0 } placed) {
            return false;
        }

        var tag = StripInstance(placed);
        foreach (var quest in quests.ToList()) {
            var questTemplate = Imlight.CoreLib.WizardData.Collections.QuestTemplateCollection.GetQuestByName(quest.QuestName);
            var shows = questTemplate?.m_startResults?.m_results?.OfType<ResAddDynaMod>().Any(mod =>
                string.Equals(StripInstance((string) mod.m_dynaModClientTag), tag, StringComparison.OrdinalIgnoreCase)
                && (mod.m_bShowsObject || !string.IsNullOrEmpty((string) mod.m_dynaModState))) == true;
            if (!shows) {
                continue;
            }

            var goals = questTemplate.m_goals ?? [];
            for (var index = 0; index < goals.Count; index++) {
                if (goals[index]?.m_goalName is not { } goalName || !quest.IsGoalActive(goalName)) {
                    continue;
                }

                var spot = Imlight.CoreLib.Game.Tools.QuestStepSpots.Find(questTemplate, index + 1);
                if (spot is null || string.Equals(spot.Zone, Entity.Zone?.ZonePath, StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }

                // The zone's own arrival spot, not the step's spot: the teleporter goes to that place, it does not walk the
                // step for the wizard.
                teleport = new ResTeleport { m_destinationZone = spot.Zone, m_destinationLoc = "" };
                Logger.Information("Teleporter {0} in {1}: no destination in the data; {2} shows it, so it goes to the zone of that quest's step {3}, {4} ({5}).",
                    Logger.Args(placed, Entity.Zone?.ZonePath, quest.QuestName, goalName, spot.Zone, spot.How));

                return true;
            }
        }

        return false;
    }

    private bool TriggerAllows(IActorRef playerActor, Wizard wizard, CoreObject playerObject) {
        if (Imlight.CoreLib.Game.World.WorldAccess.AllOpen) {
            return true;
        }

        if (!TryResolveTrigger(out var trigger)
            || trigger.m_requirements?.m_requirements is not { Count: > 0 }
            // A requirement Imcodec cannot read is no reason to keep the door shut.
            || ZoneTrigger.HasUndecodableRequirement(trigger.m_requirements)) {
            return true;
        }

        return RequirementDispatcher.EvaluateRequirements(
            requirements: trigger.m_requirements,
            context: new ZoneRequirementContext(trigger.m_requirements, playerActor, playerObject, wizard,
                Entity.ZoneRef, trigger.m_triggerName) {
                ScriptState = Entity.Zone.ScriptState,
            });
    }

    internal void OnVolumePrompt(TransportVolumePrompt message) {
        if (message?.PlayerActor is null || string.IsNullOrEmpty(message.TriggerName)) {
            return;
        }

        var memento = Entity.GetComponentOfType<InteractServiceMementoComponent>();
        if (!message.Show) {
            if (string.Equals(_boundTrigger, message.TriggerName, StringComparison.OrdinalIgnoreCase)) {
                _boundTrigger = null;
            }
            memento?.ReleasePrompt(message.PlayerActor);
            SendDirectTransportLeave(message.PlayerActor);
            return;
        }

        if (!string.IsNullOrEmpty(_boundTrigger)
            && !string.Equals(_boundTrigger, message.TriggerName, StringComparison.OrdinalIgnoreCase)) {
            return;
        }

        _boundTrigger = message.TriggerName;
        Logger.Information("Transport interact: {0} entered volume for {1} in {2}.",
            Logger.Args(message.PlayerActor.Path.Name, message.TriggerName, Entity.Zone?.ZonePath));
        // One Press-X, from the memento as for a door. The second, hand-built prompt (SendDirectTransportPrompt) is no
        // longer sent.
        memento?.HoldPrompt(message.PlayerActor);
        Logger.Information("Transport prompt: Ladder {0} offered door-style to global {1}.",
            Logger.Args(PlacedName, Entity.ActiveGameObject?.m_globalID.Full ?? 0));
    }

    private void SendDirectTransportPrompt(IActorRef playerActor) {
        if (playerActor is null || Entity.Template is not GameObjectTemplate ladderTemplate) {
            return;
        }

        var displayName = ladderTemplate.m_displayName.Length > 0
            ? ladderTemplate.m_displayName
            : ladderTemplate.m_objectName;
        var transportText = TransportTextOverride();
        var memento = new ServiceMementoBase {
            m_npcIcon = "Interact",
            m_npcNameKey = "NPCFormats_Name",
            m_npcTextKey = transportText,
            m_serviceOptions = [
                new InteractableOption {
                    m_serviceName = ServiceName,
                    m_displayKey = transportText,
                    m_iconKey = "Interact",
                    m_serviceIndex = 0,
                    m_optionIndex = 0,
                    m_forceInteract = true,
                }
            ],
            m_personaMadlibs = new MadlibBlock {
                m_blockToken = "NPC",
                m_madlibs = [
                    new MadlibArgT_ByteString {
                        m_madlibArgument = displayName,
                        m_madlibToken = "NAME",
                    }
                ],
            },
        };

        var serializer = new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.None);
        if (!serializer.Serialize(memento, 4, out var data)) {
            Logger.Error("Transport prompt: failed to serialize Ladder {0}.",
                Logger.Args(PlacedName));
            return;
        }

        var globalId = Entity.ActiveGameObject?.m_globalID.Full ?? 0;
        if (globalId > 0) {
            playerActor.Tell(new QUEST_MESSAGES_52_PROTOCOL.MSG_SENDNPCOPTIONS {
                MobileID = globalId,
                Options = data,
                Reinteract = 0,
            });
            playerActor.Tell(new GAME_5_PROTOCOL.MSG_WIZBANG {
                WizBangID = (uint) WizBangs.Interactable,
                GameObjectID = globalId,
            });
        }
        Logger.Information("Transport direct prompt: Ladder {0} sent global {1} text {2}.",
            Logger.Args(PlacedName, globalId, transportText));
    }

    private void SendDirectTransportLeave(IActorRef playerActor) {
        if (playerActor is null) {
            return;
        }

        var globalId = Entity.ActiveGameObject?.m_globalID.Full ?? 0;
        if (globalId > 0) {
            playerActor.Tell(new GAME_5_PROTOCOL.MSG_LEAVESERVICERANGE { MobileID = globalId });
        }
    }

    private bool TryResolveTrigger(out Trigger trigger) {
        trigger = null;
        if (Entity.Zone?.TriggersByName is not { } triggers) {
            return false;
        }

        if (!string.IsNullOrEmpty(_boundTrigger)
            && triggers.TryGetValue(_boundTrigger, out trigger)) {
            return true;
        }

        var templateName = (Entity.Template as GameObjectTemplate)?.m_objectName.ToString();
        var placedName = (string?)Entity.Info?.m_zoneTag;
        var candidates = new[] { placedName, templateName }
            .Where(name => !string.IsNullOrEmpty(name))
            .SelectMany(name => new[] {
                name,
                name.EndsWith(" instance", StringComparison.OrdinalIgnoreCase)
                    ? name[..^" instance".Length]
                    : name
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        trigger = triggers.Values.FirstOrDefault(t =>
            t?.m_triggerObjInfo is { } info
            && candidates.Any(name =>
                string.Equals((string?)info.m_zoneTag, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.m_triggerName, name, StringComparison.OrdinalIgnoreCase)));

        trigger ??= triggers.Values.FirstOrDefault(t =>
            candidates.Any(name => string.Equals(t?.m_triggerName, name, StringComparison.OrdinalIgnoreCase)));

        return trigger is not null;
    }

    private ResultList ResolveTriggerResults(Trigger trigger) {
        if (trigger?.m_results?.m_results is not { Count: > 0 } results) {
            return null;
        }

        // Stored zone transfer data replaces an empty ResTeleport exactly as the
        // trigger supervisor does. This keeps paired up/down triggers exact.
        var zoneName = Entity.Zone?.ZonePath;
        var stored = ZoneDataCollection.GetZoneData(zoneName)?.Teleports?
            .FirstOrDefault(t => string.Equals(t?.TriggerName, trigger.m_triggerName,
                StringComparison.OrdinalIgnoreCase))?.Teleport;
        var replaced = results.Select(result => {
            if (result is ResTeleport teleport
                && string.IsNullOrEmpty(teleport.m_destinationZone)
                && !string.IsNullOrEmpty(stored?.m_destinationZone)) {
                return stored;
            }

            return result;
        }).ToList();

        return new ResultList { m_results = replaced };
    }

    /// <summary>
    /// Whether the door's own teleport option lets this wizard in now. The game files put what opens a portal on the option
    /// itself (MS_SpiritWorldPortal: ReqTriggerObjectState "MS_SpiritWorldPortal instance" On, set by using the Tree of
    /// Life). A door with no such option, no requirements, other kinds of requirement, or one the codec cannot read stays
    /// open, as before.
    /// </summary>
    private bool OwnOptionAllows(Wizard wizard) {
        if (Imlight.CoreLib.Game.World.WorldAccess.AllOpen || Entity.Template is not GameObjectTemplate template) {
            return true;
        }

        var options = template.m_behaviors?.OfType<InteractableBehaviorTemplate>()
            .SelectMany(b => b.m_interactOptions ?? []).OfType<InteractStateOptionTemplate>()
            .Where(o => o.m_results?.m_results?.Any(r => r is ResTeleport) == true).ToList() ?? [];
        // Only a door opened by an object's state is held shut here (the portal lights when the tree is used). Other needs
        // (a quest entry such as Death1's portal's QT-MS-DTH1-C01-006) were never checked for doors and the server does not
        // always write those entries, so they stay as they were: open.
        if (options.Count == 0 || options.Any(o => o.m_requirements?.m_requirements is not { Count: > 0 } list
                                                   || list.Any(r => r is not ReqTriggerObjectState))) {
            return true;
        }

        try {
            return options.Any(o => ZoneTrigger.HasUndecodableRequirement(o.m_requirements)
                                    || RequirementDispatcher.EvaluateRequirements(o.m_requirements,
                                        new ZoneRequirementContext(o.m_requirements, null, null, wizard, Entity.ZoneRef,
                                            PlacedName) {
                                            ScriptState = Entity.Zone.ScriptState,
                                        }));
        }
        catch (Exception ex) {
            Logger.Debug("Zone {0}: door {1} requirements could not be read: {2}", Logger.Args(Entity.Zone?.ZonePath, template.m_objectName, ex.Message));

            return true;
        }
    }

    // The name the zone's scripts know this placed door by (its zone tag, else the template's name).
    private string PlacedName => Entity.Info?.m_zoneTag is { Length: > 0 } tag ? tag : (Entity.Template as GameObjectTemplate)?.m_objectName.ToString();

    private bool IsTransportMesh()
        => Entity.Template is GameObjectTemplate template && HasTransportName(template.m_objectName.ToString());

    private string TransportTextOverride() => OwnInteractText() ?? "GUI_ObjectInteract";

    // The template's own interact text (m_interactTextOverride), or null when it has none.
    private string OwnInteractText()
        => (Entity.Template as GameObjectTemplate)?.m_behaviors?
            .OfType<InteractableBehaviorTemplate>()
            .Select(behavior => behavior.m_interactTextOverride?.ToString())
            .FirstOrDefault(text => !string.IsNullOrEmpty(text));

    private static bool HasInteractableBehavior(GameObjectTemplate template)
        => template.m_behaviors?.OfType<InteractableBehaviorTemplate>().Any() == true;

    internal static bool IsTransportTrigger(Trigger trigger) {
        if (trigger is null) {
            return false;
        }

        var info = trigger.m_triggerObjInfo;
        return HasTransportName(trigger.m_triggerName)
            || HasTransportName(info?.m_zoneTag)
            || HasTransportName((string)info?.m_triggerObjName);
    }

    internal static bool VolumeOwnsTransport(Volume volume, Trigger trigger, float volumeX, float volumeY) {
        if (!IsTransportTrigger(trigger) || volume is null) {
            return false;
        }

        var fires = trigger.m_fireEvents;
        if (fires is { Count: > 0 } && volume.m_enterEvents?.Any(enter =>
                fires.Any(fire => string.Equals((string)enter, (string)fire, StringComparison.OrdinalIgnoreCase))) == true) {
            return true;
        }

        string[] names = [
            trigger.m_triggerName,
            (string)trigger.m_triggerObjInfo?.m_zoneTag,
            (string)trigger.m_triggerObjInfo?.m_triggerObjName,
        ];
        string[] volumeNames = [volume.m_volumeName, StripInstance(volume.m_volumeName)];
        if (volumeNames.Any(volumeName => names.Any(name =>
                string.Equals(StripInstance(name), StripInstance(volumeName), StringComparison.OrdinalIgnoreCase)))) {
            return true;
        }

        var info = trigger.m_triggerObjInfo;
        if (info is null || info.m_templateID == 0) {
            return false;
        }

        var dx = volumeX - info.m_locationX;
        var dy = volumeY - info.m_locationY;
        var reach = MathF.Max(volume.m_radius, MathF.Max(volume.m_length, volume.m_width) / 2f);
        if (reach < 80f) {
            reach = 80f;
        }

        return (dx * dx) + (dy * dy) <= reach * reach;
    }

    private static string StripInstance(string name) {
        const string suffix = " instance";
        return !string.IsNullOrEmpty(name) && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? name[..^suffix.Length]
            : name;
    }

    internal static bool HasTransportName(string name)
        => !string.IsNullOrEmpty(name)
           && (name.Contains("Ladder", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Zipline", StringComparison.OrdinalIgnoreCase)
               || name.Contains("ZipLine", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Rope", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Lift", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Elevator", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Climb", StringComparison.OrdinalIgnoreCase)
               || name.Equals("LadderBehavior", StringComparison.OrdinalIgnoreCase));

    private static HashSet<string> GetTeleportNames() {
        if (s_teleportNames is not null) {
            return s_teleportNames;
        }

        var names = SpiralDB.GetAllZoneData()
            .Where(zone => zone?.Teleports is not null)
            .SelectMany(zone => zone.Teleports)
            .Select(t => t.TriggerName)
            .Where(name => !string.IsNullOrEmpty(name))
            .ToHashSet(StringComparer.Ordinal);

        // An empty set means the zone data is not loaded yet: do not cache it.
        if (names.Count > 0) {
            s_teleportNames = names;
        }

        return names;
    }

}

internal sealed class TransportSpot {
    public string TriggerName { get; init; }
    public string ObjectTag { get; init; }
    public string VolumeName { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }
    public float RadiusSq { get; init; }
    public float Length { get; init; }
    public float Width { get; init; }
    public float Yaw { get; init; }
    // A spot around the ladder end itself (no trigger, volume or click box): the ends of one ladder stand nearly over each
    // other, so only a wizard on this end's own level is at it.
    public bool UsesHeight { get; init; }
}

internal sealed class TransportVolumePrompt : IServerMessage {
    public byte MessageOrder { get; } = 250;
    public byte ServiceID { get; } = 102;

    public bool Show { get; init; }
    public IActorRef PlayerActor { get; init; }
    public string TriggerName { get; init; }
}
