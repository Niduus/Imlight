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
using Imcodec.Cryptography;
using Imcodec.IO;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Common;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Events;

/// <summary>
/// The game's event button (Niduus, 2026-10-25: "event button is missing from UI"): the "Now" button at the top of the
/// HUD, which the game calls the Class Project button (ClassEventButton in HUDWindow.gui). Read from the client
/// (WizardGraphicalClient r806919):
///   MSG_UPDATECLASSPROJECTBUTTON  Buffer is a ClassProjectUpdateList: the ids of the events that are on. The HUD shows
///                                 the button when the list is not empty and the wizard is level 10 or higher.
///   MSG_REQUESTCLASSPROJECTINFO   the button sends it; the answer's Buffer is a ClassProjectActiveList and its
///                                 TokenBuffer a ClassProjectPlayerProgressList, and DisplayInfo opens the window.
/// An event's id is the string hash of its template's m_projectName (ClassProjectsCommon::Initialize reads every
/// ClassProjects/*.xml and keys them so); the Deckathalon of a school is the game's TreasureTower_01_&lt;School&gt;.
/// The blobs are written like the client reads them: no versioning, property mask 4 (as PvPBlobs).
/// </summary>
internal static class EventButton {

    private const uint CLIENT_PROPERTY_MASK = 4;

    /// <summary>The game's event template of a running event, or null when the game has none for it.</summary>
    internal static string ProjectName(GameEvents.RunningEvent running)
        => running is { IsDeckathalon: true } && Deckathalon.SchoolName(running.School) is { } school
            ? $"TreasureTower_01_{school}"
            : null;

    internal static uint ProjectId(GameEvents.RunningEvent running)
        => ProjectName(running) is { } name ? StringHash.Compute(name) : 0;

    /// <summary>The school of a Deckathalon event id, or null.</summary>
    internal static string SchoolOf(uint projectId)
        => Deckathalon.Towers.Select(t => t.School).FirstOrDefault(s => StringHash.Compute($"TreasureTower_01_{s}") == projectId);

    /// <summary>The button message: the event that is on, or none (which hides the button).</summary>
    internal static WIZARD2_53_PROTOCOL.MSG_UPDATECLASSPROJECTBUTTON ButtonMessage() {
        var id = ProjectId(GameEvents.Current);

        return new WIZARD2_53_PROTOCOL.MSG_UPDATECLASSPROJECTBUTTON {
            Buffer = Serialize(new ClassProjectUpdateList { m_activeProjectList = id == 0 ? [] : [id] })
        };
    }

    /// <summary>The answer to the button: the event that is on and how far the wizard got in it.</summary>
    internal static WIZARD2_53_PROTOCOL.MSG_REQUESTCLASSPROJECTINFO InfoMessage(Wizard wizard, byte displayInfo) {
        var running = GameEvents.Current;
        var id = ProjectId(running);
        var active = new List<ActiveClassProject>();
        var progress = new List<ClassProjectPlayerProgress>();
        if (id != 0) {
            active.Add(new ActiveClassProject {
                m_projectID = id,
                m_goalCount = 0,
                m_endTime = (uint) Math.Max(0, new DateTimeOffset(running.End).ToUnixTimeSeconds()),
                m_currentDay = (uint) Math.Max(1, (int) Math.Floor((DateTime.UtcNow - running.Start).TotalDays) + 1),
                m_multiplier = 1,
                m_displayMultiplier = 1,
                m_isEnding = false,
                m_parameter = "",
            });

            var score = Deckathalon.PlaceOf(running.Key, wizard.CharId)?.Score;
            progress.Add(new ClassProjectPlayerProgress {
                m_projectID = id,
                m_classProjectPlayer = new ClassProjectPlayer {
                    m_playerGID = new GID(wizard.GameObjectID),
                    // The game's tower counts a point a floor and a point a treasure card; ours are ten times that.
                    m_totalProgress = (ushort) Math.Clamp((score?.Points ?? 0) / 10, 0, ushort.MaxValue),
                    m_level = (byte) Math.Clamp(score?.BestFloor ?? 0, 0, byte.MaxValue),
                    m_extendedData = [],
                },
            });
        }

        return new WIZARD2_53_PROTOCOL.MSG_REQUESTCLASSPROJECTINFO {
            Buffer = Serialize(new ClassProjectActiveList { m_activeProjectList = active }),
            TokenBuffer = Serialize(new ClassProjectPlayerProgressList { m_playerProgressList = progress }),
            DisplayInfo = displayInfo,
        };
    }

    /// <summary>The event window's leaderboard: the best wizards of the event that is on.</summary>
    internal static WIZARD2_53_PROTOCOL.MSG_CLASSPROJECTREQUESTLEADERBOARD LeaderboardMessage(uint projectId) {
        var running = GameEvents.Current;
        var top = running is not null && ProjectId(running) == projectId ? Deckathalon.Top(running.Key, 100) : [];
        var board = Serialize(new ClassProjectLeaderboard {
            m_leaderboardSize = top.Count,
            m_topEntry = 0,
            m_leaderboardList = [.. top.Select(s => new ClassProjectLeaderboardPlayer { m_points = (uint) Math.Max(0, s.Points / 10) })],
        });

        return new WIZARD2_53_PROTOCOL.MSG_CLASSPROJECTREQUESTLEADERBOARD { ProjectID = projectId, TopBuffer = board, Buffer = board };
    }

    private static ByteString Serialize(PropertyClass value) {
        var serializer = new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.None);
        if (!serializer.Serialize(value, CLIENT_PROPERTY_MASK, out var data)) {
            Logger.Error("Event button: could not write {0}.", Logger.Args(value.GetType().Name));

            return default;
        }

        return data;
    }

}
