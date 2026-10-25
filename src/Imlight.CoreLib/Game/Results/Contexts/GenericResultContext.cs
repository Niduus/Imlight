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

using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.Game.Results.Contexts;

/// <summary>
/// Generic result context for any result execution scenario
/// </summary>
public class GenericResultContext(ResultList results,
                           IActorRef playerRef,
                           CoreObject playerObj,
                           IActorRef replyTo = null,
                           IActorRef zoneActor = null,
                           string questName = null,
                           string goalName = null,
                           string triggerName = null) : IResultContext {

    /// <summary>
    /// Multiplies the experience of drop tables among the results.
    /// </summary>
    public float XpScale { get; init; } = 1f;

    /// <summary>
    /// True while the results run for a player who spawned inside the volume that raised the event, until the
    /// results first wait.
    /// </summary>
    public bool PlayerSpawned { get; set; }

    /// <summary>
    /// The zone entity whose interact option runs the results, if any.
    /// </summary>
    public IActorRef SourceRef { get; init; }

    /// <summary>
    /// The template id of that entity.
    /// </summary>
    public ulong SourceTemplateId { get; init; }

    /// <summary>
    /// Whether that entity lives in an instance.
    /// </summary>
    public bool SourceIsInstance { get; init; }

    public IActorRef PlayerRef { get; } = playerRef;
    public CoreObject PlayerObj { get; } = playerObj;
    public IActorRef ReplyTo { get; } = replyTo;
    public IActorRef ZoneActor { get; } = zoneActor;
    public string QuestName { get; } = questName;
    public string GoalName { get; } = goalName;
    public string TriggerName { get; } = triggerName;

    /// <summary>
    /// The state of the zone instance the results run in (its counters and tokens), for the requirements of results
    /// that choose among others (ResIfThenElse, ResRandomResults).
    /// </summary>
    public Zone.Core.ZoneScriptState ScriptState { get; init; }

    /// <summary>
    /// The path of the zone the results run in, if any.
    /// </summary>
    public string ZonePath { get; init; }

    private readonly ResultList _results = results;
    // The results a ResIfThenElse or ResRandomResults chose, so their handlers are found like the others'.
    private readonly List<Result> _chosen = [];

    public IEnumerable<Result> GetResults()
        => _chosen.Count == 0 ? _results?.m_results ?? [] : (_results?.m_results ?? []).Concat(_chosen);

    /// <summary>
    /// Adds the results a branching result chose to run.
    /// </summary>
    internal void AddChosen(IEnumerable<Result> results)
        => _chosen.AddRange(results.Where(r => r is not null));

    public IActorRef GetZoneActor() 
        => ZoneActor;

    public IActorRef GetPlayerRef() 
        => PlayerRef;

    public CoreObject GetPlayerObj() 
        => PlayerObj;

    public IActorRef GetReplyTo() 
        => ReplyTo;

    public float GetXpScale()
        => XpScale;

    public bool IsPlayerSpawn()
        => PlayerSpawned;

    public void EndPlayerSpawn()
        => PlayerSpawned = false;

    public string GetZonePath()
        => ZonePath;

    public IActorRef GetSourceRef()
        => SourceRef;

    public ulong GetSourceTemplateId()
        => SourceTemplateId;

    public bool IsSourceInstance()
        => SourceIsInstance;

    public bool IsQuestContext => !string.IsNullOrEmpty(QuestName);
    public bool IsGoalContext => !string.IsNullOrEmpty(GoalName);
    public bool IsTriggerContext => !string.IsNullOrEmpty(TriggerName);

}