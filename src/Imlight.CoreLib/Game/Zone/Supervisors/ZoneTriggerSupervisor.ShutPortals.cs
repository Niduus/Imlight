/*
 * WALK-IN WAYS THROUGH A PORTAL THAT IS STILL SHUT
 *
 * Some portals open by being switched on: the Tree of Life's spirit portal (MS_SpiritWorldPortal) is Off until the
 * wizard beats Nomoonaga and uses the tree, and its own "enter" option needs it On (ReqTriggerObjectState). Pressing X
 * on it already waited for that (InteractDoorTeleportComponent.OwnOptionAllows), but the same portal also has a walk-in
 * volume ("Activator Volume (1)", trigger Death3TeleportToT4) that goes to the same room and names no needs of its own.
 * A player walked into it before Nomoonaga and was taken straight to Kagemoosha (Mitsu, 2026-11-24 09:42 CT: "that
 * boss' door still takes you to the final boss fight").
 *
 * A teleport trigger that leads where a shut portal of the same zone leads now waits for that portal to open, also with
 * OpenAllDoors (the portal's state is the world's, not a quest door). The game files are not changed.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Requirements;
using Imlight.CoreLib.Game.Requirements.Contexts;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.Zone.Supervisors;

internal sealed partial class ZoneTriggerSupervisor {

    // A portal of this zone that opens by being switched on: where it leads and what its "enter" option needs.
    private sealed record ShutPortal(string Tag, string Destination, RequirementList Needs);

    private List<ShutPortal> _shutPortals;
    private readonly HashSet<string> _shutPortalLogged = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether this teleport trigger leads where a portal of the zone leads that is still shut for this wizard.
    /// </summary>
    private bool LeadsThroughShutPortal(Trigger trigger, ZONE_102_PROTOCOL.MSG_POSTEVENT message) {
        if (Imlight.CoreLib.Game.World.WorldAccess.AllOpen || message.PlayerActor is null || !HasTeleportResult(trigger)) {
            return false;
        }

        var portals = _shutPortals ??= FindShutPortals();
        if (portals.Count == 0) {
            return false;
        }

        var destinations = trigger.m_results.m_results.OfType<ResTeleport>()
            .Select(t => t.m_destinationZone?.ToString()).Where(z => !string.IsNullOrEmpty(z)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var portal in portals.Where(p => destinations.Contains(p.Destination))) {
            // The portal's own trigger name is never held back by itself.
            if (string.Equals(trigger.m_triggerName, portal.Tag, StringComparison.Ordinal)) {
                continue;
            }

            bool open;
            try {
                open = ZoneTrigger.HasUndecodableRequirement(portal.Needs)
                       || RequirementDispatcher.EvaluateRequirements(portal.Needs,
                           new ZoneRequirementContext(portal.Needs, message.PlayerActor, message.PlayerGameObject, ZoneTrigger.QueryWizard(message.PlayerActor), ZoneRef,
                               portal.Tag) {
                               ScriptState = Zone.ScriptState,
                           });
            }
            catch (Exception ex) {
                Logger.Debug("Zone {0}: portal {1} needs could not be read: {2}", Logger.Args(Zone.ZonePath, portal.Tag, ex.Message));
                open = true;
            }

            if (!open) {
                if (_shutPortalLogged.Add(trigger.m_triggerName)) {
                    Logger.Information("Zone {0}: teleport trigger {1} waits for portal {2} to open (state '{3}').",
                        Logger.Args(Zone.ZonePath, trigger.m_triggerName, portal.Tag, Zone.ScriptState.GetObjectState(portal.Tag)));
                }

                return true;
            }
        }

        return false;
    }

    // The zone's placed portals whose teleport option is opened only by an object's state (as OwnOptionAllows reads them),
    // with where their stored teleport goes.
    private List<ShutPortal> FindShutPortals() {
        var found = new List<ShutPortal>();
        try {
            var teleports = ZoneDataCollection.GetZoneData(Zone.ZonePath)?.Teleports;
            foreach (var info in Zone.ZoneData?.m_objectList ?? []) {
                if (info is null || CoreObjectFactory.GetCoreTemplate(info.m_templateID) is not GameObjectTemplate template) {
                    continue;
                }

                var options = template.m_behaviors?.OfType<InteractableBehaviorTemplate>()
                    .SelectMany(b => b.m_interactOptions ?? []).OfType<InteractStateOptionTemplate>()
                    .Where(o => o.m_results?.m_results?.Any(r => r is ResTeleport) == true).ToList() ?? [];
                if (options.Count != 1 || options[0].m_requirements?.m_requirements is not { Count: > 0 } needs
                    || needs.Any(r => r is not ReqTriggerObjectState)) {
                    continue;
                }

                var name = template.m_objectName.ToString();
                var destination = teleports?.FirstOrDefault(t => string.Equals(t?.TriggerName, name, StringComparison.Ordinal))
                    ?.Teleport?.m_destinationZone?.ToString();
                if (string.IsNullOrEmpty(destination)) {
                    continue;
                }

                var tag = (string) info.m_zoneTag;
                found.Add(new ShutPortal(string.IsNullOrEmpty(tag) ? name : tag, destination, options[0].m_requirements));
            }
        }
        catch (Exception ex) {
            Logger.Debug("Zone {0}: portals that open by state could not be listed: {1}", Logger.Args(Zone.ZonePath, ex.Message));
        }

        return found;
    }

}
