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
 * TRIGGER SYSTEM
 * ========================================================================
 * 
 * PURPOSE:
 * Handles event-based scripting within zones, allowing for interactive
 * elements like gates, teleporters, and scripted sequences.
 * 
 * USAGE EXAMPLE:
 * // Created by the Zone system during zone loading
 * // Trigger triggerData = ...
 * var triggerActor = Context.ActorOf(Props.Create(() => 
 *     new ZoneTrigger(zoneRef, zone, triggerData)));
 * 
 * NOTE:
 * Triggers are usually activated by volumes
 * Supports player-specific cooldowns for trigger activation.
 * Dynamically attaches result handlers based on trigger configuration.
 *
 * TODO:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 10/14/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Requirements;
using Imlight.CoreLib.Game.Requirements.Contexts;
using Imlight.CoreLib.Game.Results;
using Imlight.CoreLib.Game.Results.Contexts;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Core;

/// <summary>
/// Represents a trigger (or event) within a <see cref="Zone"/>. Triggers are used to
/// handle events such as a gate opening, zone transfer, or other scripted events.
/// </summary>
/// <param name="zoneRef">The reference to the zone that this trigger is a part of.</param>
/// <param name="zone">The zone that this trigger is a part of.</param>
public sealed class ZoneTrigger(IActorRef zoneRef, Zone zone, Trigger trigger) 
    : ZoneEntity(null, null, null, zoneRef, zone) {

    private const double MinPacedWaitSeconds = 1;
    private const float QueryWizardTimeoutSeconds = 5.0f;

    public Trigger TriggerData { get; init; } = trigger;
    private readonly Dictionary<IActorRef, DateTime> _cooldowns = [];

    // Unsure why this override is required, but it fails without it present.
    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONEOBJECTLOADBEGIN))]
    protected override void ReceiveObjectLoadBegin() 
        => base.ReceiveObjectLoadBegin();

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_POSTEVENT))]
    private void ReceivePostEvent(ZONE_102_PROTOCOL.MSG_POSTEVENT message) {
        // Early-out if the event name doesn't match any configured fire events.
        if (!TriggerData.m_fireEvents.Any(x => x == message.EventName)) {
            return;
        }

        // Early-out for per-player cooldowns.
        if (TriggerData.m_cooldown > 0 && !CooldownCheck(message.PlayerActor)) {
            return;
        }

        // Evaluate requirements when present.
        if (!message.RequirementsChecked
            && TriggerData.m_requirements is not null
            && TriggerData.m_requirements.m_requirements is not null
            && TriggerData.m_requirements.m_requirements.Count > 0) {
            var requirementsMet = RequirementDispatcher.EvaluateRequirements(
                requirements: TriggerData.m_requirements,
                context: new ZoneRequirementContext(
                    TriggerData.m_requirements,
                    message.PlayerActor,
                    message.PlayerGameObject,
                    QueryWizard(message.PlayerActor),
                    ZoneRef,
                    TriggerData.m_triggerName
                ) {
                    ScriptState = Zone.ScriptState,
                    EventAdjectives = message.Adjectives,
                }
            );

            if (!requirementsMet) {
                return;
            }
        }

        // Paired teleporter triggers share an event; the supervisor keeps only the first
        // ResTeleport, so the player is never raced between destinations.
        var results = TriggerData.m_results;
        if (message.SuppressTeleportResults && results?.m_results is { Count: > 0 }) {
            results = new ResultList {
                m_results = results.m_results.Where(result => result is not ResTeleport).ToList()
            };
        }

        results = WithoutNonLoopSelfPosts(results, TriggerData);

        ResultDispatcher.ExecuteResults(Context, results, message.PlayerActor, message.PlayerGameObject,
                                       Sender, ZoneRef, triggerName: TriggerData.m_triggerName,
                                       scriptState: Zone.ScriptState, playerSpawned: message.PlayerSpawned,
                                       zonePath: Zone.ZonePath);
    }

    /// <summary>
    /// True when a requirement class the client data uses failed to decode (it came back null). Such a trigger
    /// must not fire: skipping the requirement would make it pass for every event.
    /// </summary>
    internal static bool HasUndecodableRequirement(RequirementList requirements)
        => requirements?.m_requirements is { } list
        && list.Any(r => r is null || r is RequirementList nested && HasUndecodableRequirement(nested));

    internal static ResultList WithoutNonLoopSelfPosts(ResultList results, Trigger trigger) {
        var fireEvents = trigger?.m_fireEvents;
        if (results?.m_results is not { Count: > 0 } || fireEvents is null) {
            return results;
        }

        // A self-post spins unless a wait comes first (a wait with requirements can be skipped), and it only echoes an
        // event every listener already got unless the trigger changes something or can stop.
        var canStop = trigger.m_triggerMax != uint.MaxValue
            || HasRequirements(trigger.m_requirements)
            || results.m_results.Any(result => HasRequirements(result?.m_requirements));
        var changesSomething = results.m_results.Any(result => result is not (null or ResWait or ResPostEvent or ResPostQuestEvent));
        var isLoop = canStop || changesSomething;

        var paced = false;
        var kept = new List<Result>(results.m_results.Count);
        foreach (var result in results.m_results) {
            if (result is ResWait wait && wait.m_secondsToWait >= MinPacedWaitSeconds && !HasRequirements(wait.m_requirements)) {
                paced = true;
            }

            if (!(paced && isLoop) && result is ResPostEvent post && fireEvents.Any(x => x == post.m_eventName)) {
                continue;
            }

            kept.Add(result);
        }

        return kept.Count == results.m_results.Count ? results : new ResultList { m_results = kept };
    }

    internal static Wizard QueryWizard(IActorRef playerActor) {
        // A loop outlives its player, and an Ask with no timeout never returns for a stopped actor.
        if (playerActor is null || playerActor is IInternalActorRef { IsTerminated: true }) {
            return null;
        }

        try {
            return playerActor
                .Ask<CHARACTER_103_PROTOCOL.MSG_CHARACTER>(new CHARACTER_103_PROTOCOL.MSG_QUERYACTIVEWIZARD(),
                    TimeSpan.FromSeconds(QueryWizardTimeoutSeconds)).Result?.Wizard;
        } catch (Exception ex) {
            Logger.Warning("Could not query the wizard of a trigger event's player: {Error}", Logger.Args(ex.Message));

            return null;
        }
    }

    private static bool HasRequirements(RequirementList requirements)
        => requirements?.m_requirements is { Count: > 0 };

    private bool CooldownCheck(IActorRef playerRef) {
        if (_cooldowns.TryGetValue(playerRef, out var lastTriggered)) {
            if (DateTime.Now - lastTriggered < TimeSpan.FromSeconds(TriggerData.m_cooldown)) {
                return false;
            }
            else {
                _cooldowns[playerRef] = DateTime.Now;
            }
        }
        else {
            _cooldowns.Add(playerRef, DateTime.Now);
        }

        return true;
    }


}