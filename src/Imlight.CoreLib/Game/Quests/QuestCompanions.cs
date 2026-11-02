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
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Common;
using Imlight.CoreLib.Game.Requirements;
using Imlight.CoreLib.Game.Requirements.Contexts;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Quests;

/// <summary>Asks the session to work out again which quest companions walk with the wizard (EquipmentService).</summary>
internal sealed class RefreshQuestCompanions : IServerMessage {
    public byte MessageOrder { get; } = 8;
    public byte ServiceID { get; } = 105;
}

/// <summary>
/// Quest companions: the NPCs that walk beside the wizard during part of a story (Zarozinia and Dyvim in Khrysalis,
/// Greta and Maulwurf in Karamelle, Pork, Lars and Kahn in Empyrea, Hargrove in Wizard City and so on; Niduus
/// 2026-11-02 "I need it to be playable through the main game"). The game gives each one its own object template,
/// "*-Accompany-*" in ObjectData (166 of them), which already carries a LeashBehavior (it follows its owner) and an
/// AccompanyNPCBehavior: the quests it goes along for and the areas it may go into. A quest turns one on with
/// ResToggleAccompanyNPC (the template id) or ResToggleQuestEffect (a GameEffectData/QuestEffectTemplates.xml effect
/// whose m_accompanyID is the template).
///
/// What walks with the wizard is worked out from the quests they have now, so it needs no saving and is right again
/// after a relog, a Fast Forward or a quest finished by hand: a companion goes along while a running quest started it
/// (its start results add it and don't take it off) or while a running quest is on the companion's own quest list,
/// and only in the areas the companion may go into. A quest whose start results take it off keeps it away.
/// </summary>
internal static class QuestCompanions {

    private const string QUEST_EFFECTS_FILE = "GameEffectData/QuestEffectTemplates.xml";

    private static readonly object s_lock = new();
    private static Dictionary<string, QuestEffectTemplate> s_effects;
    private static readonly Dictionary<ulong, Companion> s_companions = [];

    /// <summary>One companion template: its leash and where it may go.</summary>
    internal sealed record Companion(ulong TemplateId, GameObjectTemplate Template, LeashBehaviorTemplate Leash,
                                     int LeashSlot, AccompanyNPCBehaviorTemplate Accompany);

    /// <summary>The companion template with this id, or null when it is no companion.</summary>
    internal static Companion Get(ulong templateId) {
        if (templateId == 0) {
            return null;
        }

        lock (s_lock) {
            if (s_companions.TryGetValue(templateId, out var known)) {
                return known;
            }
        }

        Companion companion = null;
        try {
            if (CoreObjectFactory.GetCoreTemplate(templateId) is GameObjectTemplate template && template.m_behaviors is { } behaviors) {
                var slot = behaviors.FindIndex(b => b is LeashBehaviorTemplate);
                var accompany = behaviors.OfType<AccompanyNPCBehaviorTemplate>().FirstOrDefault();
                if (slot >= 0) {
                    companion = new Companion(templateId, template, (LeashBehaviorTemplate) behaviors[slot], slot, accompany);
                }
            }
        }
        catch (Exception ex) {
            Logger.Warning("Quest companion {0} could not be read: {1}", Logger.Args(templateId, ex.Message));
        }

        lock (s_lock) {
            s_companions[templateId] = companion;
        }

        if (companion is null) {
            Logger.Warning("Template {0} is no quest companion (it has no leash), so it can't walk with the wizard.",
                Logger.Args(templateId));
        }

        return companion;
    }

    /// <summary>
    /// Whether entities of this template may walk on a leash (SummonedPetComponent takes them away with their owner).
    /// Only a leash the server tied has an owner, so an object of the zone made from such a template is left alone.
    /// Some companions (Pork) keep their quest list in a behavior the game renamed, so the leash alone decides.
    /// </summary>
    internal static bool IsCompanionTemplate(CoreTemplate template)
        => template is GameObjectTemplate { m_behaviors: { } behaviors } && behaviors.Any(b => b is LeashBehaviorTemplate);

    /// <summary>The leash a companion of this template walks on, behind this owner.</summary>
    internal static LeashBehavior MakeLeash(Companion companion, GID ownerGid) => new() {
        m_behaviorTemplateNameID = StringHash.Compute($"{companion.Leash.m_behaviorName}" is { Length: > 0 } name ? name : "LeashBehavior"),
        m_ownerGid = ownerGid,
        m_radius = companion.Leash.m_radius > 0 ? companion.Leash.m_radius : 75f,
        m_angle = companion.Leash.m_angle,
        m_leashType = companion.Leash.m_leashType,
        m_alwaysDisplay = companion.Leash.m_alwaysDisplay,
    };

    /// <summary>The quest effect of this name (ResToggleQuestEffect), or null.</summary>
    internal static QuestEffectTemplate GetEffect(string effectName) {
        if (string.IsNullOrEmpty(effectName)) {
            return null;
        }

        lock (s_lock) {
            if (s_effects is null) {
                s_effects = new Dictionary<string, QuestEffectTemplate>(StringComparer.OrdinalIgnoreCase);
                try {
                    var list = RootArchiveLoader.GetFile<GameEffectTemplateList>(QUEST_EFFECTS_FILE);
                    foreach (var effect in list?.m_effectTemplates?.OfType<QuestEffectTemplate>() ?? []) {
                        var name = $"{effect.m_effectName}";
                        if (name.Length > 0) {
                            s_effects[name] = effect;
                        }
                    }

                    Logger.Information("Read {0} quest effects ({1} with a companion).",
                        Logger.Args(s_effects.Count, s_effects.Values.Count(e => e.m_accompanyID != 0)));
                }
                catch (Exception ex) {
                    Logger.Warning("The quest effects could not be read: {0}", Logger.Args(ex.Message));
                }
            }

            return s_effects.GetValueOrDefault(effectName);
        }
    }

    /// <summary>
    /// The companion templates that walk with the wizard in the area they are in now, from the quests they have.
    /// </summary>
    internal static List<ulong> Wanted(Wizard wizard) {
        var wanted = new List<ulong>();
        var quests = wizard?.QuestBehavior?.CurrentQuestInstances?.Where(q => q is not null).Select(q => q.QuestName)
            .Where(n => !string.IsNullOrEmpty(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (quests is not { Count: > 0 } || string.IsNullOrEmpty(wizard.Zone)) {
            return wanted;
        }

        // Started by a running quest, or kept away by one.
        var started = new Dictionary<ulong, string>();
        var keptAway = new HashSet<ulong>();
        foreach (var questName in quests) {
            var results = SpiralDB.GetQuestByName(questName)?.m_startResults?.m_results;
            foreach (var result in results ?? []) {
                switch (result) {
                    case ResToggleAccompanyNPC toggle when toggle.m_templateID != 0:
                        Note(toggle.m_templateID, toggle.m_add, questName);
                        break;
                    case ResToggleQuestEffect effect when GetEffect($"{effect.m_effectName}") is { m_accompanyID: not 0 } template:
                        Note(template.m_accompanyID, effect.m_addEffect, questName);
                        break;
                }
            }
        }

        foreach (var (templateId, questName) in started) {
            if (!keptAway.Contains(templateId) && MayGo(wizard, Get(templateId), questName)) {
                wanted.Add(templateId);
            }
        }

        // A running quest on a companion's own list brings it along too (the steps in between its start and end).
        foreach (var companion in Known().Where(c => !started.ContainsKey(c.TemplateId) && !keptAway.Contains(c.TemplateId))) {
            var quest = companion.Accompany?.m_allowedQuests?.Select(q => $"{q}").FirstOrDefault(quests.Contains);
            if (quest is not null && MayGo(wizard, companion, quest)) {
                wanted.Add(companion.TemplateId);
            }
        }

        return wanted;

        void Note(ulong templateId, bool add, string questName) {
            if (add) {
                started.TryAdd(templateId, questName);
            }
            else {
                keptAway.Add(templateId);
            }
        }
    }

    // The companions the quests of the game turn on, read once (their quest lists say which quests they go along for).
    // Read again when the quests are (an update changes them while the server runs).
    private static List<Companion> s_known;
    private static IReadOnlyList<QuestTemplate> s_knownFrom;

    private static List<Companion> Known() {
        var templates = SpiralDB.QuestTemplates;
        lock (s_lock) {
            if (s_known is not null && ReferenceEquals(s_knownFrom, templates)) {
                return s_known;
            }
        }

        var ids = new HashSet<ulong>();
        foreach (var quest in templates.Where(q => q?.m_startResults?.m_results is not null)) {
            foreach (var result in quest.m_startResults.m_results) {
                if (result is ResToggleAccompanyNPC { m_add: true, m_templateID: not 0 } toggle) {
                    ids.Add(toggle.m_templateID);
                }
                else if (result is ResToggleQuestEffect { m_addEffect: true } effect
                         && GetEffect($"{effect.m_effectName}") is { m_accompanyID: not 0 } template) {
                    ids.Add(template.m_accompanyID);
                }
            }
        }

        var known = ids.Select(Get).Where(c => c is not null).ToList();
        lock (s_lock) {
            s_known = known;
            s_knownFrom = templates;
        }

        Logger.Information("Quest companions: {0} the quests turn on.", Logger.Args(known.Count));

        return known;
    }

    // Whether the companion may come into the wizard's area: its own area list when it keeps to one, and the start
    // requirements of its quest effect (where the game lets it go).
    private static bool MayGo(Wizard wizard, Companion companion, string questName) {
        if (companion is null) {
            return false;
        }

        var accompany = companion.Accompany;
        if (accompany is { m_allowedInZones: true, m_zoneList.Count: > 0 }
            && !accompany.m_zoneList.Any(z => string.Equals($"{z}", wizard.Zone, StringComparison.OrdinalIgnoreCase))) {
            return false;
        }

        var requirements = accompany?.m_questEffectTemplate?.m_startReqs;
        if (requirements?.m_requirements is not { Count: > 0 }) {
            return true;
        }

        try {
            return RequirementDispatcher.EvaluateRequirements(requirements,
                new QuestRequirementContext(requirements, null, null, wizard, questName));
        }
        catch (Exception ex) {
            Logger.Warning("Quest companion {0}: its area check failed ({1}); it comes along.",
                Logger.Args(companion.TemplateId, ex.Message));

            return true;
        }
    }

}
