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

    /// <summary>Whether a game event template (TreasureTower_01_Fire, 2026FallSOF, ...) is on now.</summary>
    internal static bool IsActive(string projectName)
        => !string.IsNullOrEmpty(projectName)
           && (string.Equals(ProjectName(GameEvents.Current), projectName, StringComparison.OrdinalIgnoreCase)
               || ClassProjects.IsActive(projectName));

    /// <summary>
    /// The game registry value of an event entry: the game's zones show an event's objects while the entry named after
    /// its template is 1 (the Duel Arena's tower portals ask for TreasureTower_01_&lt;School&gt; = 1). Null for any other entry.
    /// </summary>
    internal static float? RegistryValue(string entry)
        => entry is not null && Deckathalon.Towers.Any(t => string.Equals(entry, $"TreasureTower_01_{t.School}", StringComparison.OrdinalIgnoreCase))
            ? IsActive(entry) ? 1 : 0
            : ClassProjects.HolidayValue(entry);

    /// <summary>The school of a Deckathalon event id, or null.</summary>
    internal static string SchoolOf(uint projectId)
        => Deckathalon.Towers.Select(t => t.School).FirstOrDefault(s => StringHash.Compute($"TreasureTower_01_{s}") == projectId);

    /// <summary>The ids of every event on: the Deckathalon, the Scroll of Fortune and the other queued event.</summary>
    internal static List<uint> ActiveIds() {
        var ids = new List<uint>();
        if (ProjectId(GameEvents.Current) is var id and not 0) {
            ids.Add(id);
        }

        ids.AddRange(ClassProjects.Active().Select(r => r.Id).Where(i => !ids.Contains(i)));

        return ids;
    }

    /// <summary>The button message: the events that are on, or none (which hides the button).</summary>
    internal static WIZARD2_53_PROTOCOL.MSG_UPDATECLASSPROJECTBUTTON ButtonMessage()
        => new() {
            Buffer = Serialize(new ClassProjectUpdateList { m_activeProjectList = ActiveIds() })
        };

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
            var player = new ClassProjectPlayer {
                m_playerGID = new GID(wizard.GameObjectID),
                // The game's tower counts a point a floor and a point a treasure card; ours are ten times that.
                m_totalProgress = (ushort) Math.Clamp((score?.Points ?? 0) / 10, 0, ushort.MaxValue),
                m_level = (byte) Math.Clamp(score?.BestFloor ?? 0, 0, byte.MaxValue),
                m_extendedData = [],
            };

            // Today's count of each of the event's activities, the "0/20" of its window: the template's first activity is
            // beating a floor (CPT_DefeatMob), the second using a treasure card (CPT_CastTreasureCard, 20 a day).
            if (score is not null && score.Day == DateTime.UtcNow.ToString("yyyy-MM-dd")) {
                SetGoalProgress(player, 1, score.FloorsToday);
                SetGoalProgress(player, 2, score.CardsToday);
            }

            progress.Add(new ClassProjectPlayerProgress { m_projectID = id, m_classProjectPlayer = player });
        }

        // The game's other events on (ClassProjects.cs). With two or more, the button opens the game's list of events.
        foreach (var other in ClassProjects.Active()) {
            active.Add(new ActiveClassProject {
                m_projectID = other.Id,
                m_goalCount = 0,
                m_endTime = (uint) Math.Max(0, new DateTimeOffset(other.End).ToUnixTimeSeconds()),
                m_currentDay = (uint) Math.Max(1, (int) Math.Floor((DateTime.UtcNow - other.Start).TotalDays) + 1),
                m_multiplier = 1,
                m_displayMultiplier = 1,
                m_isEnding = false,
                m_parameter = "",
            });
            progress.Add(new ClassProjectPlayerProgress { m_projectID = other.Id, m_classProjectPlayer = PlayerOf(wizard, other) });
        }

        return new WIZARD2_53_PROTOCOL.MSG_REQUESTCLASSPROJECTINFO {
            Buffer = Serialize(new ClassProjectActiveList { m_activeProjectList = active }),
            TokenBuffer = Serialize(new ClassProjectPlayerProgressList { m_playerProgressList = progress }),
            DisplayInfo = displayInfo,
        };
    }

    // A wizard in one of the other events: points, today's count of each activity, and the rewards claimed.
    private static ClassProjectPlayer PlayerOf(Wizard wizard, ClassProjects.Running running) {
        var mine = ClassProjects.Find(running, wizard.CharId);
        var player = new ClassProjectPlayer {
            m_playerGID = new GID(wizard.GameObjectID),
            m_totalProgress = (ushort) Math.Clamp(mine?.Points ?? 0, 0, ushort.MaxValue),
            m_level = 0,
            m_extendedData = [],
        };

        var today = ClassProjects.TodayOf(running, mine);
        for (var i = 0; i < Math.Min(5, today.Count); i++) {
            SetGoalProgress(player, i + 1, today[i]);
        }

        var claimed = ClassProjects.ClaimedBits(mine, out var flags);
        player.m_flags = flags;
        if (running.IsScroll && claimed.Count > 0) {
            player.m_extendedData = claimed;
            SetByName(player, "m_usesExtraData", true);
        }

        return player;
    }

    private static void SetByName(PropertyClass value, string name, object to) {
        var property = value.GetType().GetProperty(name);
        if (property?.CanWrite != true) {
            return;
        }

        try {
            property.SetValue(value, Convert.ChangeType(to, property.PropertyType));
        }
        catch (Exception e) {
            Logger.Warning("Event button: could not set {0}: {1}", Logger.Args(name, e.Message));
        }
    }

    /// <summary>
    /// The tower portal's window (the client's ClassProjectPortalWindow) for a wizard with a saved checkpoint: its Use
    /// button sends MSG_CLASSPROJECTUSETOKEN with the checkpoint picked, and its checkpoint button opens the game's floor
    /// picker (ClasProjectsFloorSelectWindow), one button for each checkpoint up to Level. The client shows the Use
    /// button only with a token, and the picker only when TeleporterCheckPoint is 0; entry is free here, so one token
    /// is always shown, and ClaimedDailyCredits hides the game's credit button.
    /// </summary>
    internal static WIZARD2_53_PROTOCOL.MSG_OPENCLASSPROJECTPORTAL PortalMessage(Wizard wizard, int checkpoint)
        => new() {
            ProjectID = ProjectId(GameEvents.Current),
            Tokens = 1,
            Level = (uint) Math.Max(0, checkpoint),
            ClaimedDailyCredits = 1,
            TeleporterCheckPoint = 0,
            GlobalID = wizard.GameObjectID,
            WebFailure = 0,
            Credits = 0,
        };

    /// <summary>The event window's leaderboard: the best wizards of the event that is on.</summary>
    internal static WIZARD2_53_PROTOCOL.MSG_CLASSPROJECTREQUESTLEADERBOARD LeaderboardMessage(uint projectId) {
        var running = GameEvents.Current;
        var points = running is not null && ProjectId(running) == projectId
            ? Deckathalon.Top(running.Key, 100).Select(s => Math.Max(0, s.Points / 10)).ToList()
            : ClassProjects.ActiveOf(projectId) is { } other ? ClassProjects.Top(other, 100).Select(p => p.Points).ToList() : [];
        var board = Serialize(new ClassProjectLeaderboard {
            m_leaderboardSize = points.Count,
            m_topEntry = 0,
            m_leaderboardList = [.. points.Select(p => new ClassProjectLeaderboardPlayer { m_points = (uint) p })],
        });

        return new WIZARD2_53_PROTOCOL.MSG_CLASSPROJECTREQUESTLEADERBOARD { ProjectID = projectId, TopBuffer = board, Buffer = board };
    }

    /// <summary>
    /// Sets ClassProjectPlayer.m_goalProgress1..5: the client keeps today's count of an event activity in the low byte
    /// (ClientClassProjectComponent's window shows it as "count/limit"). Set by name, since only a type dump from a
    /// recent game client has these fields; with an older one the window shows 0.
    /// </summary>
    private static void SetGoalProgress(ClassProjectPlayer player, int activity, int count) {
        var property = typeof(ClassProjectPlayer).GetProperty($"m_goalProgress{activity}");
        if (property?.CanWrite != true) {
            return;
        }

        try {
            property.SetValue(player, Convert.ChangeType(Math.Clamp(count, 0, byte.MaxValue), property.PropertyType));
        }
        catch (Exception e) {
            Logger.Warning("Event button: could not set {0}: {1}", Logger.Args(property.Name, e.Message));
        }
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
