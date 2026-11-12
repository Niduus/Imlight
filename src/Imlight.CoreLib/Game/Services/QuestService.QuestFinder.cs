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
using System.Threading;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Common;
using Imlight.CoreLib.Game.Madlibs;
using Imlight.CoreLib.Game.Quests;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

/// <summary>
/// The spellbook's Quest Finder: the "Quest Finder - Find an available quest nearby" entry at the end of the quest book
/// that points the quest arrow at the nearest NPC with a quest to give (Niduus, 2026-11-04: "no quest finder in book").
/// <list type="bullet">
/// <item>The game asks for it with an empty MSG_ADDQUESTFINDER, but only while the stats say it is on
/// (WizGameStats.m_questFinderEnabled, the Options window's Quest Finder box). The server always sent it off and never
/// answered, so the entry never showed.</item>
/// <item>The answer, MSG_ADDQUESTFINDER, names a quest ID and goal ID for the entry (the game makes the quest
/// "QuestFinder" and its "Talk to" goal itself) and NPCList: an NPCDataList of the NPCs with a quest for the wizard
/// (zone, spot, first and last name keys, zone name key) and whether they are in the wizard's zone (CurrentZone), in
/// other zones (OtherZons) or nowhere in this world (None: "There are no available quests in this world.").</item>
/// <item>Picking the entry in the quest book sends MSG_REQUESTNEXTCLOSESTQUEST; the answer is the same message with a
/// fresh NPCList. The game picks the closest NPC of the list itself (ClientQuestFinder, WizardGraphicalClient 1.610).</item>
/// <item>MSG_QUESTFINDEROPTION is the Options box: it is kept on the character (m_questFinderOff).</item>
/// </list>
/// </summary>
internal partial class QuestService {

    // The IDs of the Quest Finder entry. Real quests and goals get random IDs; these only need to differ from them.
    private static readonly GID QUEST_FINDER_QUEST_ID = new(0x0A51_F17D_0000_0001UL);
    private static readonly GID QUEST_FINDER_GOAL_ID = new(0x0A51_F17D_0000_0002UL);

    // The most zones of other areas read from the game's files for one answer; the rest are read on later answers.
    private const int QUEST_FINDER_ZONE_READS = 12;

    private static readonly ObjectSerializer s_questFinderSerializer = new(Versionable: false, Behaviors: SerializerFlags.None);

    // The game asked for the Quest Finder on this session, so it shows it and wants fresh lists as quests change.
    private bool _questFinderShown;

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_ADDQUESTFINDER))]
    private void ReceiveAddQuestFinder(WIZARD_12_PROTOCOL.MSG_ADDQUESTFINDER message) {
        var wizard = GetActiveWizard();
        if (wizard?.GameStats is null || wizard.GameStats.m_questFinderOff) {
            return;
        }

        if (QuestFinderList(wizard, out var list) is not { } data) {
            return;
        }

        _questFinderShown = true;
        Logger.Information("Quest Finder: {0} gets the Quest Finder ({1}).", Logger.Args(wizard.CharId, list));
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_ADDQUESTFINDER {
            QuestID = QUEST_FINDER_QUEST_ID,
            GoalID = QUEST_FINDER_GOAL_ID,
            NPCList = data,
        });
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_REQUESTNEXTCLOSESTQUEST))]
    private void ReceiveRequestNextClosestQuest(WIZARD_12_PROTOCOL.MSG_REQUESTNEXTCLOSESTQUEST message) {
        var wizard = GetActiveWizard();
        if (wizard is null) {
            return;
        }

        _questFinderShown = true;
        SendQuestFinderList(wizard);
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_QUESTFINDEROPTION))]
    private void ReceiveQuestFinderOption(WIZARD_12_PROTOCOL.MSG_QUESTFINDEROPTION message) {
        var wizard = GetActiveWizard();
        if (wizard?.GameStats is null) {
            return;
        }

        var on = message.Enable != 0;
        try {
            wizard.SetQuestFinder(on);
        }
        catch (Exception ex) {
            Logger.Error("Quest Finder: the option of {0} could not be saved: {1}", Logger.Args(wizard.CharId, ex));
        }

        if (!on) {
            _questFinderShown = false;
        }

        Logger.Information("Quest Finder: {0} turned the Quest Finder {1}.", Logger.Args(wizard.CharId, on ? "on" : "off"));
    }

    // A quest was taken, finished or dropped, or a zone loaded: the NPCs with a quest to give may have changed.
    private void RefreshQuestFinder(Wizard wizard) {
        if (_questFinderShown && wizard?.GameStats is { m_questFinderOff: false }) {
            SendQuestFinderList(wizard);
        }
    }

    private void SendQuestFinderList(Wizard wizard) {
        if (QuestFinderList(wizard, out _) is { } data) {
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_REQUESTNEXTCLOSESTQUEST { NPCList = data });
        }
    }

    /// <summary>The NPCs with a quest for this wizard, serialized as the game reads them; null when that failed.</summary>
    private static byte[] QuestFinderList(Wizard wizard, out string described) {
        var list = new NPCDataList { m_npcList = [], m_zoneType = ZONE_TYPE.None };
        described = "no quests in this world";
        try {
            FillQuestFinderList(wizard, list);
            described = list.m_zoneType switch {
                ZONE_TYPE.CurrentZone => $"{list.m_npcList.Count} NPC(s) in this area",
                ZONE_TYPE.OtherZons => $"{list.m_npcList.Count} NPC(s) in other areas of this world",
                _ => described,
            };
        }
        catch (Exception ex) {
            // An empty list still shows the entry, with "There are no available quests in this world."
            Logger.Error("Quest Finder: finding the NPCs with a quest for {0} failed: {1}", Logger.Args(wizard?.CharId, ex));
            list = new NPCDataList { m_npcList = [], m_zoneType = ZONE_TYPE.None };
        }

        if (!s_questFinderSerializer.Serialize(list, 1, out var data)) {
            Logger.Error("Quest Finder: the NPC list of {0} could not be serialized.", Logger.Args(wizard?.CharId));

            return null;
        }

        return data;
    }

    private static void FillQuestFinderList(Wizard wizard, NPCDataList list) {
        string zone = wizard.Zone ?? "";
        if (zone.Length == 0) {
            return;
        }

        var offered = QuestFinderGivers()
            .Where(g => MapService.IsOfferedTo(g.Quest, wizard))
            .ToList();
        if (offered.Count == 0) {
            return;
        }

        // Givers standing in this zone.
        var here = QuestStepSpots.Zone(zone);
        var givers = offered.Select(g => g.Giver).ToHashSet();
        var hereKey = string.IsNullOrEmpty(wizard.ZoneDisplayName) ? here.DisplayKey : wizard.ZoneDisplayName;
        foreach (var (template, spot) in here.Objects.Where(o => givers.Contains(o.Template)).DistinctBy(o => o.Template)) {
            if (QuestFinderNpc(template, zone, spot, hereKey) is { } npc) {
                list.m_npcList.Add(npc);
            }
        }

        if (list.m_npcList.Count > 0) {
            list.m_zoneType = ZONE_TYPE.CurrentZone;

            return;
        }

        // Givers in other zones of this world, where a "talk to" step of some quest finds them. Main quests first.
        var world = WorldOfZone(zone);
        var elsewhere = offered
            .OrderByDescending(g => g.Quest.m_mainline || ((string) g.Quest.m_questName).Contains("MAIN", StringComparison.OrdinalIgnoreCase))
            .Select(g => (g.Giver, Zone: QuestGuide.GiverZone(g.Quest)))
            .Where(g => g.Zone.Length > 0 && !string.Equals(g.Zone, zone, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(WorldOfZone(g.Zone), world, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(g => g.Giver)
            .ToList();

        var reads = 0;
        foreach (var (giver, giverZone) in elsewhere) {
            if (!QuestStepSpots.IsRead(giverZone) && ++reads > QUEST_FINDER_ZONE_READS) {
                continue;
            }

            var info = QuestStepSpots.Zone(giverZone);
            var placed = info.Objects.FirstOrDefault(o => o.Template == giver);
            if (placed.Template != giver) {
                continue;
            }

            if (QuestFinderNpc(giver, giverZone, placed.Spot, info.DisplayKey) is { } npc) {
                list.m_npcList.Add(npc);
            }
        }

        if (list.m_npcList.Count > 0) {
            list.m_zoneType = ZONE_TYPE.OtherZons;
        }
    }

    private static string WorldOfZone(string zone) {
        var slash = zone.IndexOf('/');

        return slash < 0 ? zone : zone[..slash];
    }

    private static QuestFinderNPCData QuestFinderNpc(ulong template, string zone, Imcodec.Math.Vector3 spot, string zoneKey) {
        if (CoreObjectFactory.GetCoreTemplate(template) is not GameObjectTemplate npc) {
            return null;
        }

        string nameKey = npc.m_displayName;
        var (first, last) = Locale.GetEnglishNameKeys(nameKey);
        if (string.IsNullOrEmpty(first) && string.IsNullOrEmpty(last)) {
            (first, last) = (nameKey ?? "", "");
        }

        return new QuestFinderNPCData {
            m_zoneName = zone,
            m_location = spot,
            m_firstName = first ?? "",
            m_lastName = last ?? "",
            m_zoneDisplayName = zoneKey ?? "",
        };
    }

    // A class, not a value tuple: sessions read it on threads of their own, so it must never be half written.
    private static Tuple<int, List<(QuestTemplate Quest, ulong Giver)>> s_questFinderGivers
        = Tuple.Create(-1, new List<(QuestTemplate, ulong)>());
    private static readonly Lock s_questFinderGiversLock = new();

    // Every quest an NPC gives with that NPC, worked out once for each set of quest changes.
    internal static List<(QuestTemplate Quest, ulong Giver)> QuestFinderGivers() {
        var known = s_questFinderGivers;
        if (known.Item1 == QuestOptions.Version) {
            return known.Item2;
        }

        lock (s_questFinderGiversLock) {
            known = s_questFinderGivers;
            var version = QuestOptions.Version;
            if (known.Item1 == version) {
                return known.Item2;
            }

            var list = new List<(QuestTemplate, ulong)>();
            foreach (var quest in QuestTemplateCollection.GetAllQuests()) {
                if (quest?.m_dialogList is not ActorDialogList && QuestOptions.GiverOf(quest?.m_questName) == 0) {
                    continue;
                }

                var giver = QuestMadlibs.GiverTemplateOf(quest);
                if (giver != 0) {
                    list.Add((quest, giver));
                }
            }

            s_questFinderGivers = Tuple.Create(version, list);

            return list;
        }
    }

}
