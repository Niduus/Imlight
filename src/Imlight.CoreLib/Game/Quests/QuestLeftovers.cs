using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Imcodec.IO;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Quests;

/// <summary>
/// What a quest leaves behind for one wizard: hides that would block a step they are on now, and an object used as the
/// way through that is only a wall. Every rule here works from what an object is (its quest steps, its template), never
/// from guessing at its name.
/// </summary>
internal static class QuestLeftovers {
    private static readonly bool s_off =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("IMLIGHT_NO_AUTO_HIDE"));
    private static readonly object s_lock = new();
    private static Tuple<int, Dictionary<string, List<(string Quest, string Goal)>>> s_usage =
        Tuple.Create(-1, new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase));


    private static string Norm(string tag) {
        if (tag is null) return null;
        return tag.EndsWith(" instance", StringComparison.OrdinalIgnoreCase)
            ? tag[..^" instance".Length]
            : tag;
    }

    public static bool ActiveUsageNames(Wizard wizard, string zoneTag) {
        var tag = Norm(zoneTag);
        if (s_off || wizard is null || string.IsNullOrEmpty(tag)) return false;
        foreach (var (usageTag, sources) in UsageGoals()) {
            if (string.Equals(Norm(usageTag), tag, StringComparison.OrdinalIgnoreCase)
                && sources.Any(g => GoalActive(wizard, g.Quest, g.Goal))) return true;
        }
        return false;
    }

    private static bool GoalActive(Wizard wizard, string quest, string goal) =>
        wizard.QuestBehavior?.CurrentQuestInstances?
            .FirstOrDefault(q => q != null && string.Equals(q.QuestName, quest, StringComparison.OrdinalIgnoreCase))
            ?.IsGoalActive(goal) ?? false;

    private static bool GoalDone(Wizard wizard, string quest, string goal) =>
        wizard.QuestBehavior?.CurrentQuestInstances?
            .FirstOrDefault(q => string.Equals(q.QuestName, quest, StringComparison.OrdinalIgnoreCase))
            ?.IsGoalCompleted(goal) ?? false;

    private static Dictionary<string, List<(string Quest, string Goal)>> UsageGoals() {
        if (s_usage.Item1 == QuestOptions.Version) return s_usage.Item2;
        lock (s_lock) {
            var version = QuestOptions.Version;
            if (s_usage.Item1 == version) return s_usage.Item2;
            var index = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var quest in SpiralDB.QuestTemplates) {
                foreach (var goal in quest?.m_goals ?? []) {
                    if (goal is null || goal.m_goalType != GOAL_TYPE.GOAL_TYPE_USAGE) continue;
                    foreach (ByteString tag in goal.m_clientTags ?? []) {
                        var name = (string)tag;
                        if (string.IsNullOrEmpty(name)) continue;
                        if (!index.TryGetValue(name, out var goals)) index[name] = goals = [];
                        goals.Add((quest.m_questName, goal.m_goalName));
                    }
                }
            }
            s_usage = Tuple.Create(version, index);
            return index;
        }
    }


    /// <summary>
    /// The tags of the goal's own objects that are only a wall (solid collision, no model or states): used, each is the
    /// way through and goes for the wizard who used it. Kondha's KR_Kondha_SecretPassage, the "hush-hush way through" of
    /// No Country for Old Mice!, says nothing of a wall in its name, so it stayed and the next step behind it could not be
    /// reached (2026-11-27). Only for that wizard: anyone else on the quest still has it to use.
    /// </summary>
    public static List<string> UsedBareBlockerTags(string zonePath, GoalTemplate goal) {
        var result = new List<string>();
        if (s_off || string.IsNullOrEmpty(zonePath) || goal is null || goal.m_goalType != GOAL_TYPE.GOAL_TYPE_USAGE) return result;
        if (!ResourceManager.TryLoadArchive(zonePath, out var wad)) return result;
        var data = ResourceManager.OpenZoneData(wad)?.ToArray();
        if (data is null || !new BindSerializer().Deserialize<WizZoneData>(data, 1u, out var zone) || zone?.m_objectList is null) return result;

        var usageTags = new HashSet<string>(goal.m_clientTags?.Select(t => Norm((string)t)) ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var usage in zone.m_objectList) {
            if (usage is null || !usageTags.Contains(Norm((string?)usage.m_zoneTag))
                || !Imlight.CoreLib.Game.Zone.Components.UsedQuestObjects.IsBareBlocker(
                    CoreObjectFactory.GetCoreTemplate(usage.m_templateID) as GameObjectTemplate)) {
                continue;
            }

            result.Add((string)usage.m_zoneTag);
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }


    public static List<string> HideTagsBlockingActiveUsage(Wizard wizard) {
        var result = new List<string>();
        if (s_off) return result;
        foreach (var dynamod in (wizard?.DynamodSet?.Dynamods ?? []).ToList()) {
            if (dynamod is not null && !string.IsNullOrEmpty(dynamod.ClientTag)
                && IsUsageHide(dynamod.ModState) && ActiveUsageNames(wizard, dynamod.ClientTag)) {
                result.Add(dynamod.ClientTag);
            }
        }
        return result;
    }

    public static void ReleaseHidesBlockingActiveUsage(Wizard wizard) {
        if (wizard is null) return;
        foreach (var tag in HideTagsBlockingActiveUsage(wizard).ToList()) {
            try { wizard.RemoveDynamod(tag); }
            catch (InvalidOperationException) { }
        }
    }

    public static void ReleaseHidesBlockingActivePersonaGoals(Wizard wizard) {
        if (wizard is null) return;
        var personas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var quest in (wizard.QuestBehavior?.CurrentQuestInstances ?? []).ToList()) {
            QuestTemplate questTemplate;
            try { questTemplate = QuestTemplateCollection.GetQuestByName(quest.QuestName); }
            catch { continue; }
            foreach (var progress in quest.GoalProgress ?? []) {
                if (quest.IsGoalActive(progress.GoalName)
                    && questTemplate?.m_goals?.FirstOrDefault(g => g.m_goalName == progress.GoalName) is PersonaGoalTemplate persona
                    && !string.IsNullOrEmpty(persona.m_personaName)) {
                    personas.Add(NormalizePersona(persona.m_personaName));
                }
            }
        }
        foreach (var dynamod in wizard.DynamodSet?.Dynamods?.ToList() ?? []) {
            if (dynamod is null || string.IsNullOrEmpty(dynamod.ClientTag)) continue;
            if (personas.Contains(NormalizePersona(Norm(dynamod.ClientTag)))) wizard.RemoveDynamod(dynamod.ClientTag);
        }
    }

    private static bool IsUsageHide(string state) =>
        string.IsNullOrEmpty(state) || state.Equals("Off", StringComparison.OrdinalIgnoreCase)
        || state.Equals("Gone", StringComparison.OrdinalIgnoreCase);

    private static string NormalizePersona(string name) {
        if (string.IsNullOrEmpty(name)) return "";
        name = Norm(name);
        return name.EndsWith("_Persona", StringComparison.OrdinalIgnoreCase)
            ? name[..^"_Persona".Length]
            : name;
    }

    private static double DistanceSquared(Imcodec.Math.Vector3 a, Imcodec.Math.Vector3 b) {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        return dx * dx + dy * dy + dz * dz;
    }
}
