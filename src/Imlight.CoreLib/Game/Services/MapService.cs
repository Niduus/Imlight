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
 * MAP SERVICE
 * ========================================================================
 *
 * PURPOSE:
 * Answers the two questions the game's map page (the spellbook's map tab) asks the server.
 *
 * NOTE:
 * - Each time the map opens, the game sends MSG_REQUESTACTIVEMAPQUESTS and draws the map's pictures (NPC
 *   portraits, quest places, "Hedge Maze" signs) only once the answer comes. The server never answered, so the
 *   map showed its background and the player arrow only (Niduus, 2026-10-27). The answer is an
 *   ActiveMapQuestsCommon: two lists of StringHash values of picture paths. The game hashes every picture of the
 *   map (DoodleData m_image, '\' turned into '/') and marks a picture whose hash is in m_newQuestList or
 *   m_completedQuestList (WizardGraphicalClient 1.610, MapInfoManager 0x1409c2c50). Here the first list holds the
 *   portraits of NPCs with a quest to give the player, the second the portraits of the steps of quests ready to
 *   hand in. Pictures with tags (m_onTags) show from the tags of the player's quest steps (MSG_SENDGOAL ClientTags)
 *   and dynamods, as before.
 * - The zone ID is sent as 0 so the game never keeps the answer and asks again each time the map opens.
 * - MSG_MAPREQUIREMENTS carries the requirements of map pictures that only show when they are met
 *   (MapInfoRequirementsList); the answer lists the IDs whose requirements this wizard meets.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Common;
using Imlight.CoreLib.Game.Madlibs;
using Imlight.CoreLib.Game.Quests;
using Imlight.CoreLib.Game.Requirements;
using Imlight.CoreLib.Game.Requirements.Contexts;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal class MapService(SessionActor sessionActor) : MessageService(sessionActor) {

    // Map pictures of NPCs from the shared art package carry this prefix; quest step pictures and NPC icons do not.
    private const string SHARED_ART_PREFIX = "|_Shared|WorldData|";

    private static readonly ObjectSerializer s_serializer = new(Versionable: false, Behaviors: SerializerFlags.None);

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new MapService(parentActor));

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_REQUESTACTIVEMAPQUESTS))]
    private void ReceiveRequestActiveMapQuests(WIZARD_12_PROTOCOL.MSG_REQUESTACTIVEMAPQUESTS message) {
        var wizard = GetActiveWizard();
        var answer = new ActiveMapQuestsCommon {
            m_zoneID = new GID(0),
            m_newQuestList = [],
            m_completedQuestList = [],
        };

        try {
            if (wizard is not null) {
                answer.m_newQuestList = OfferPictureHashes(wizard);
                answer.m_completedQuestList = TurnInPictureHashes(wizard);
            }
        }
        catch (Exception ex) {
            // An empty answer still lets the map draw its pictures.
            Logger.Error("Map: working out the quest marks of {0} failed: {1}", Logger.Args(wizard?.CharId, ex));
        }

        if (!s_serializer.Serialize(answer, 1, out var data)) {
            Logger.Error("Map: failed to serialize the active map quests of {0}.", Logger.Args(wizard?.CharId));

            return;
        }

        SendToSocket(new WIZARD_12_PROTOCOL.MSG_REQUESTACTIVEMAPQUESTS { Data = data });
    }

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_MAPREQUIREMENTS))]
    private void ReceiveMapRequirements(WIZARD2_53_PROTOCOL.MSG_MAPREQUIREMENTS message) {
        var wizard = GetActiveWizard();
        var passed = new MapInfoRequirementsPassedList { m_idList = [] };

        if (TryDeserialize<MapInfoRequirementsList>(message.Data, out var asked)) {
            foreach (var request in asked.m_mapInfoRequirementsList ?? []) {
                if (request is not null && MeetsRequirements(wizard, request)) {
                    passed.m_idList.Add(request.m_id);
                }
            }
        } else {
            Logger.Warning("Map: could not read the map requirements {0} sent.", Logger.Args(wizard?.CharId));
        }

        if (!s_serializer.Serialize(passed, 1, out var data)) {
            Logger.Error("Map: failed to serialize the passed map requirements of {0}.", Logger.Args(wizard?.CharId));

            return;
        }

        SendToSocket(new WIZARD2_53_PROTOCOL.MSG_MAPREQUIREMENTS { Data = data });
    }

    private static bool MeetsRequirements(Wizard wizard, MapInfoRequirementsRequest request) {
        byte[] serialized = request.m_serializedRequirements;
        if (serialized is null || serialized.Length == 0) {
            return true;
        }

        // A picture whose requirements cannot be read shows, as a picture without requirements does.
        if (wizard is null || !TryDeserialize<RequirementList>(serialized, out var requirements)) {
            Logger.Warning("Map: could not read the requirements of map picture {0}; it shows.", Logger.Args(request.m_id));

            return true;
        }

        try {
            return RequirementDispatcher.EvaluateRequirements(requirements,
                new QuestRequirementContext(requirements, null, null, wizard));
        }
        catch (Exception ex) {
            Logger.Error("Map: checking the requirements of map picture {0} failed: {1}", Logger.Args(request.m_id, ex));

            return true;
        }
    }

    // The game writes these blobs without type versions; a versioned read is the fallback.
    private static bool TryDeserialize<T>(byte[] data, out T result) where T : PropertyClass {
        result = null;
        if (data is null || data.Length == 0) {
            return false;
        }

        foreach (var serializer in new[] { s_serializer, new ObjectSerializer(Versionable: true, Behaviors: SerializerFlags.None) }) {
            try {
                if (serializer.Deserialize<T>(data, 4, out var read) && read is not null) {
                    result = read;

                    return true;
                }
            }
            catch {
                // Try the next way of reading it.
            }
        }

        return false;
    }

    /// <summary>The portraits of the NPCs who have a quest to give this wizard now.</summary>
    private static List<uint> OfferPictureHashes(Wizard wizard) {
        var hashes = new HashSet<uint>();
        foreach (var (quest, picture) in GiverPictures()) {
            if (hashes.Contains(StringHash.Compute(picture)) || !IsOfferedTo(quest, wizard)) {
                continue;
            }

            AddPicture(hashes, picture);
        }

        return [.. hashes];
    }

    /// <summary>The pictures of the open steps of this wizard's quests that are ready to hand in.</summary>
    private static List<uint> TurnInPictureHashes(Wizard wizard) {
        var hashes = new HashSet<uint>();
        foreach (var qInstance in wizard.QuestBehavior.CurrentQuestInstances.ToList()) {
            if (qInstance is null || !qInstance.IsReadyForTurnIn()) {
                continue;
            }

            var qTemplate = QuestTemplateCollection.GetQuestByName(qInstance.QuestName);
            foreach (var goal in qTemplate?.m_goals ?? []) {
                if (goal is not null && qInstance.IsGoalActive(goal.m_goalName)) {
                    AddPicture(hashes, goal.m_displayImage1);
                }
            }

            // The quest's giver takes it back when no open step names anyone.
            if (CoreObjectFactory.GetCoreTemplate(QuestMadlibs.GiverTemplateOf(qTemplate)) is GameObjectTemplate giver) {
                AddPicture(hashes, giver.m_sIcon);
            }
        }

        return [.. hashes];
    }

    // The same as the quest offer service's check (InteractQuestOfferComponent.GetOrUpdatePlayerState).
    private static bool IsOfferedTo(QuestTemplate quest, Wizard wizard) {
        if (DungeonQuestIndex.IsDungeonQuest(quest.m_questName) || QuestOptions.IsOff(quest.m_questName)) {
            return false;
        }

        if (wizard.HasQuest(quest.m_questName)
            || (wizard.HasCompletedQuest(quest.m_questName) && !QuestOptions.IsRepeatable(quest.m_questName))) {
            return false;
        }

        return quest.m_requirements == null || RequirementDispatcher.EvaluateRequirements(
            requirements: quest.m_requirements,
            context: new QuestRequirementContext(quest.m_requirements, null, null, wizard, quest.m_questName));
    }

    private static void AddPicture(HashSet<uint> hashes, string picture) {
        if (string.IsNullOrEmpty(picture)) {
            return;
        }

        string path = picture.Replace('\\', '/');
        if (path.StartsWith(SHARED_ART_PREFIX, StringComparison.Ordinal)) {
            path = path[SHARED_ART_PREFIX.Length..];
        }

        hashes.Add(StringHash.Compute(path));
        hashes.Add(StringHash.Compute(SHARED_ART_PREFIX + path));
    }

    // A class, not a value tuple: sessions read it on threads of their own, so it must never be half written.
    private static Tuple<int, List<(QuestTemplate Quest, string Picture)>> s_giverPictures
        = Tuple.Create(-1, new List<(QuestTemplate, string)>());
    private static readonly Lock s_giverPicturesLock = new();

    // Every quest an NPC gives with that NPC's portrait, worked out once for each set of quest changes.
    private static List<(QuestTemplate Quest, string Picture)> GiverPictures() {
        var known = s_giverPictures;
        if (known.Item1 == QuestOptions.Version) {
            return known.Item2;
        }

        lock (s_giverPicturesLock) {
            known = s_giverPictures;
            var version = QuestOptions.Version;
            if (known.Item1 == version) {
                return known.Item2;
            }

            var list = new List<(QuestTemplate, string)>();
            foreach (var quest in QuestTemplateCollection.GetAllQuests()) {
                if (quest?.m_dialogList is not ActorDialogList && QuestOptions.GiverOf(quest?.m_questName) == 0) {
                    continue;
                }

                if (CoreObjectFactory.GetCoreTemplate(QuestMadlibs.GiverTemplateOf(quest)) is GameObjectTemplate giver
                    && !string.IsNullOrEmpty(giver.m_sIcon)) {
                    list.Add((quest, giver.m_sIcon));
                }
            }

            s_giverPictures = Tuple.Create(version, list);

            return list;
        }
    }

}
