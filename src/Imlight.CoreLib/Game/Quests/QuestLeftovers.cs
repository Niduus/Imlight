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

internal static class QuestLeftovers {
    private const float RADIUS = 900f;
    private static readonly Regex s_blocker = new(
        "(^|[^a-z])(ice|icy|frozen|freeze|frost|collision|barrier|blocker|block|boulder|rubble|stone|cage|web|thorn|bramble|brambles|lock|wall|shell|crystal|snow|sealed|gate)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex s_rescueTitle = new(
        "^\\s*(save|free|rescue|release|untie|unchain|cure|revive|wake|heal|cut|chop|clear|slash|smash|destroy)\\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly bool s_off =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("IMLIGHT_NO_AUTO_HIDE"));
    private static readonly object s_lock = new();
    private static readonly Dictionary<string, bool> s_rescueKeys = new(StringComparer.OrdinalIgnoreCase);
    private static Tuple<int, Dictionary<string, List<string>>> s_index =
        Tuple.Create(-1, new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase));
    private static Tuple<int, Dictionary<string, List<(string Quest, string Goal)>>> s_usage =
        Tuple.Create(-1, new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase));

    public static ResultList RescueHides(GoalTemplate goal) {
        if (s_off || goal is null || goal.m_goalType != GOAL_TYPE.GOAL_TYPE_USAGE) return null;
        var tags = goal.m_clientTags;
        if (tags is null || tags.Count == 0) return null;
        if ((goal.m_completeResults?.m_results ?? []).Any(r => r is ResAddDynaMod or ResRemoveDynaMod)) return null;

        var title = (string?)goal.m_goalTitle;
        if (string.IsNullOrWhiteSpace(title)) return null;
        var input = title.Contains(' ') ? title : (Locale.FindInAnyTable(title) ?? title);
        if (!s_rescueTitle.IsMatch(input)) return null;

        var results = new ResultList { m_results = [] };
        foreach (ByteString tag in tags) {
            var name = (string)tag;
            if (string.IsNullOrEmpty(name)) continue;
            results.m_results.Add(new ResAddDynaMod {
                m_dynaModClientTag = name.EndsWith(" instance", StringComparison.OrdinalIgnoreCase) ? name : name + " instance",
                m_dynaModState = "",
                m_zoneName = "",
                m_bShowsObject = false,
                m_dynaModRemove = true,
                m_persist = true,
            });
        }
        return results.m_results.Count > 0 ? results : null;
    }

    public static bool LooksLikeBlocker(string zoneTag)
        => !s_off && !string.IsNullOrEmpty(zoneTag) && s_blocker.IsMatch(zoneTag);

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

    public static bool ShouldHide(Imlight.CoreLib.Game.Zone.Core.Zone zone, string zoneTag, Wizard wizard) {
        if (s_off || wizard is null || string.IsNullOrEmpty(zoneTag)) return false;
        var objects = zone?.ZoneData?.m_objectList;
        if (objects is null) return false;
        var target = objects.FirstOrDefault(o => string.Equals(o.m_zoneTag, zoneTag, StringComparison.OrdinalIgnoreCase));
        if (target is null) return false;
        foreach (var goalObject in objects) {
            var goalTag = (string?)goalObject?.m_zoneTag;
            if (string.IsNullOrEmpty(goalTag) || !UsageGoals().TryGetValue(Norm(goalTag), out var sources)) continue;
            var dx = goalObject.m_location.X - target.m_location.X;
            var dy = goalObject.m_location.Y - target.m_location.Y;
            var dz = goalObject.m_location.Z - target.m_location.Z;
            if (dx * dx + dy * dy + dz * dz <= RADIUS * RADIUS
                && sources.Any(g => GoalDone(wizard, g.Quest, g.Goal))) return true;
        }
        return false;
    }

    public static List<string> FinishedUsageBlockerTags(string zonePath, GoalTemplate goal) {
        var result = new List<string>();
        if (string.IsNullOrEmpty(zonePath) || goal is null || goal.m_goalType != GOAL_TYPE.GOAL_TYPE_USAGE) return result;
        if (!ResourceManager.TryLoadArchive(zonePath, out var wad)) return result;
        var data = ResourceManager.OpenZoneData(wad)?.ToArray();
        if (data is null || !new BindSerializer().Deserialize<WizZoneData>(data, 1u, out var zone) || zone?.m_objectList is null) return result;

        var usageTags = new HashSet<string>(goal.m_clientTags?.Select(t => Norm((string)t)) ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var usage in zone.m_objectList) {
            if (!usageTags.Contains(Norm((string?)usage?.m_zoneTag))) continue;
            var blocker = zone.m_objectList
                .Where(o => !string.IsNullOrEmpty((string?)o?.m_zoneTag) && LooksLikeBlocker((string)o.m_zoneTag))
                .Select(o => (Object: o, Distance: DistanceSquared(o.m_location, usage.m_location)))
                .Where(x => x.Distance <= 400000000d)
                .OrderBy(x => x.Distance)
                .FirstOrDefault().Object;
            if (blocker is null) continue;
            var name = (string)blocker.m_zoneTag;
            result.Add(name);
            result.Add(name.EndsWith(" instance", StringComparison.OrdinalIgnoreCase) ? name : name + " instance");
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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

    private const double WAY_REACH = 1500d;
    private const double WAY_WALL_REACH = 600d;
    private static readonly Regex s_opens = new("open", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static Tuple<int, HashSet<string>> s_questTouched;

    /// <summary>
    /// The way a finished step opens where it was finished: a gate or door near the spot (an object whose state set has
    /// one way out of its start state, ending in an "open" state it never leaves) that has a wall right by it (a blocker
    /// by name, or a bare collision). The gate goes to its open state and the wall goes, for this wizard. The Basstille's
    /// PL_Bastille_Gate and PL_BasstilleGate_Collision stayed shut after "Use the Key" (PL-WALR-MAIN-010, 2026-11-27):
    /// the rebuilt quest has the step but not the results that opened them. Objects a quest's own results or steps name
    /// are left to that quest.
    /// </summary>
    public static List<(string Tag, string State, bool Remove)> OpenedWays(string zonePath, Imcodec.Math.Vector3 at) {
        var result = new List<(string Tag, string State, bool Remove)>();
        if (s_off || string.IsNullOrEmpty(zonePath) || !ResourceManager.TryLoadArchive(zonePath, out var wad)) return result;
        var data = ResourceManager.OpenZoneData(wad)?.ToArray();
        if (data is null || !new BindSerializer().Deserialize<WizZoneData>(data, 1u, out var zone) || zone?.m_objectList is null) return result;

        var touched = QuestTouchedTags();
        foreach (var gate in zone.m_objectList) {
            var tag = (string?)gate?.m_zoneTag;
            if (string.IsNullOrEmpty(tag) || touched.Contains(Norm(tag))
                || DistanceSquared(gate.m_location, at) > WAY_REACH * WAY_REACH
                || OpenState(CoreObjectFactory.GetCoreTemplate(gate.m_templateID) as GameObjectTemplate) is not { } open) {
                continue;
            }

            var walls = zone.m_objectList
                .Where(o => o is not null && !ReferenceEquals(o, gate) && !string.IsNullOrEmpty((string?)o.m_zoneTag)
                    && !touched.Contains(Norm((string)o.m_zoneTag))
                    && DistanceSquared(o.m_location, gate.m_location) <= WAY_WALL_REACH * WAY_WALL_REACH
                    && (LooksLikeBlocker((string)o.m_zoneTag)
                        || Imlight.CoreLib.Game.Zone.Components.UsedQuestObjects.IsBareBlocker(
                            CoreObjectFactory.GetCoreTemplate(o.m_templateID) as GameObjectTemplate)))
                .Select(o => (string)o.m_zoneTag)
                .ToList();
            if (walls.Count == 0) continue;

            result.Add((tag, open, false));
            result.AddRange(walls.Select(w => (w, "", true)));
        }

        return result.Distinct().ToList();
    }

    // Words that say what kind of thing an object is, not which one: a lever named for the bridge it works is
    // "PL_Bridge_Control_Lever", and only "Bridge" ties it to PL_TrollBridge and PL-BridgeBlago_Collision.
    private static readonly HashSet<string> s_kindWords = new(StringComparer.OrdinalIgnoreCase) {
        "control", "controls", "lever", "levers", "switch", "button", "crank", "valve", "wheel", "handle", "panel", "console",
        "collision", "collide", "blocker", "block", "blocking", "instance", "object", "quest", "item", "use", "the", "and",
        "of", "int", "ext", "anim", "fx", "state", "states", "trigger", "volume", "npc", "mob", "new", "old", "big", "small",
    };
    private static readonly Regex s_wallWord = new("collision|blocker|barrier", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex s_words =new("[A-Z]+(?![a-z])|[A-Z]?[a-z]+|\\d+", RegexOptions.Compiled);

    private static HashSet<string> NameWords(string name) {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parts = Norm(name ?? "").Split(['_', '-', ' ', '.'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++) {
            // The world's own prefix (PL, KR, WC) and zone numbers (Z02) say where, not what.
            if (i == 0 && parts[i].Length <= 3 && parts[i].All(char.IsUpper)) continue;
            if (Regex.IsMatch(parts[i], "^[Zz]\\d+")) continue;
            foreach (Match m in s_words.Matches(parts[i])) {
                if (m.Value.Length >= 4 && !s_kindWords.Contains(m.Value) && !m.Value.All(char.IsDigit)) words.Add(m.Value);
            }
        }
        return words;
    }

    /// <summary>
    /// The way a finished "use" step opens somewhere else on the quest's way: in the zones the quest's steps go to, the
    /// object that shares a rare name word with the used object and has one way out of its start state into a state it
    /// never leaves (a bridge that lowers, a door that opens), and the wall (named collision, blocker or barrier, or a bare
    /// collision) that shares that word with it. The thing goes to that state and the wall goes, for this wizard. New Blago's bridge stayed up after the
    /// control room's lever (PL-FORL-MAIN-006, 2026-11-28): the lever only tells the quest it was used, and the rebuilt
    /// quest lost the step's results that lowered PL_TrollBridge and took PL-BridgeBlago_Collision away in Forlorn Taiga.
    /// Objects a quest's own results or steps name are left to that quest.
    /// </summary>
    public static List<(string Zone, string Tag, string State, bool Remove)> LinkedWays(QuestTemplate quest, GoalTemplate used) {
        if (s_off || quest is null || used is null || used.m_goalType != GOAL_TYPE.GOAL_TYPE_USAGE) return [];
        var version = QuestOptions.Version;
        var key = ((string)quest.m_questName ?? "") + "\n" + ((string)used.m_goalName ?? "");
        if (s_linked.TryGetValue(key, out var known) && known.Version == version) return known.Ways;
        var ways = FindLinkedWays(quest, used);
        s_linked[key] = (version, ways);
        return ways;
    }

    // Worked out once a step (and again after quest changes): each zone change asks again for the steps already done.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Version, List<(string Zone, string Tag, string State, bool Remove)> Ways)> s_linked = new();

    private static List<(string Zone, string Tag, string State, bool Remove)> FindLinkedWays(QuestTemplate quest, GoalTemplate used) {
        var result = new List<(string Zone, string Tag, string State, bool Remove)>();
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ByteString t in used.m_clientTags ?? []) words.UnionWith(NameWords((string)t));
        if (words.Count == 0) return result;

        var zones = new[] { (string)used.m_destinationZone }
            .Concat((quest.m_goals ?? []).Where(g => g is not null).Select(g => (string)g.m_destinationZone))
            .Where(z => !string.IsNullOrEmpty(z)).Distinct(StringComparer.OrdinalIgnoreCase);
        var touched = QuestTouchedTags();
        foreach (var zonePath in zones) {
            if (!ResourceManager.TryLoadArchive(zonePath, out var wad)) continue;
            var data = ResourceManager.OpenZoneData(wad)?.ToArray();
            if (data is null || !new BindSerializer().Deserialize<WizZoneData>(data, 1u, out var zone) || zone?.m_objectList is null) continue;

            // The word that ties them is a name, not a kind of wall ("Crystal", "Stone") and not the place: a word on more
            // than three of the zone's objects, or in its path ("Cloud", "Rachias"), would tie the lever to half the zone.
            var count = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in zone.m_objectList) {
                foreach (var w in NameWords((string)o?.m_zoneTag)) count[w] = count.GetValueOrDefault(w) + 1;
            }
            var ties = words.Where(w => !s_blocker.IsMatch(w) && !s_wallWord.IsMatch(w) && count.GetValueOrDefault(w) <= 3
                && !zonePath.Contains(w, StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (ties.Count == 0) continue;

            bool Free(string tag) => !string.IsNullOrEmpty(tag) && !touched.Contains(Norm(tag));
            var walls = zone.m_objectList
                .Where(o => o is not null && Free((string)o.m_zoneTag) && NameWords((string)o.m_zoneTag).Overlaps(ties)
                    && (s_wallWord.IsMatch((string)o.m_zoneTag)
                        || Imlight.CoreLib.Game.Zone.Components.UsedQuestObjects.IsBareBlocker(
                            CoreObjectFactory.GetCoreTemplate(o.m_templateID) as GameObjectTemplate)))
                .Select(o => (string)o.m_zoneTag)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (walls.Count == 0) continue;

            // The thing the used object works: one way out of its start state, into a state it stays in.
            var things = new List<(string Tag, string End)>();
            foreach (var thing in zone.m_objectList) {
                var tag = (string)thing?.m_zoneTag;
                if (!Free(tag) || walls.Contains(tag, StringComparer.OrdinalIgnoreCase) || !NameWords(tag).Overlaps(ties)
                    || EndState(CoreObjectFactory.GetCoreTemplate(thing.m_templateID) as GameObjectTemplate, requireOpen: false) is not { } end) {
                    continue;
                }
                things.Add((tag, end));
            }
            if (things.Count == 0) continue;

            // Each wall goes only with a thing it shares the tying word with.
            var thingWords = things.SelectMany(t => NameWords(t.Tag)).Where(ties.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
            walls = walls.Where(w => NameWords(w).Overlaps(thingWords)).ToList();
            if (walls.Count == 0) continue;

            result.AddRange(things.Select(t => (zonePath, t.Tag, t.End, false)));
            result.AddRange(walls.Select(w => (zonePath, w, "", true)));
        }

        return result.Distinct().ToList();
    }

    /// <summary>The open state a gate or door ends in, or null when the object is not one.</summary>
    private static string OpenState(GameObjectTemplate template) => EndState(template, requireOpen: true);

    /// <summary>
    /// The state an object ends in after its one way out of its start state, when it never leaves that state; with
    /// <paramref name="requireOpen"/>, only when that state or the way there is called "open". Null otherwise.
    /// </summary>
    private static string EndState(GameObjectTemplate template, bool requireOpen) {
        var setName = template?.m_behaviors?.OfType<ObjectStateBehaviorTemplate>().Select(b => (string)b.m_stateSetName)
            .FirstOrDefault(n => !string.IsNullOrEmpty(n));
        if (setName is null || setName is "NPCMobileStates" or "InteractiveState" or "WispStates" or "Player") return null;
        var category = Imlight.CoreLib.Game.States.StateFactory.GetStateSet(setName)?.m_categories?
            .FirstOrDefault(c => c is not null && !string.Equals(c.m_categoryName, "Interacting", StringComparison.OrdinalIgnoreCase));
        var states = category?.m_states?.Where(s => s is not null).ToList();
        var start = states?.FirstOrDefault(s => s.m_stateName == category.m_startState);
        if (start?.m_transitions is not { Count: 1 } ways) return null;
        string target = ways[0].m_targetState;
        string through = ways[0].m_transitionState;
        var end = states.FirstOrDefault(s => s.m_stateName == target);
        if (end is null || target == start.m_stateName || end.m_transitions is { Count: > 0 }) return null;
        return !requireOpen || s_opens.IsMatch(target) || s_opens.IsMatch(through ?? "") ? target : null;
    }

    /// <summary>Tags any quest names in its results or steps: those objects are the quest's to change.</summary>
    private static HashSet<string> QuestTouchedTags() {
        var version = QuestOptions.Version;
        var cached = s_questTouched;
        if (cached is not null && cached.Item1 == version) return cached.Item2;
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Results(ResultList list) {
            foreach (var r in list?.m_results ?? []) {
                switch (r) {
                    case ResAddDynaMod add: tags.Add(Norm((string)add.m_dynaModClientTag)); break;
                    case ResRemoveDynaMod remove: tags.Add(Norm((string)remove.m_dynaModClientTag)); break;
                }
            }
        }
        foreach (var quest in QuestTemplateCollection.GetAllQuests()) {
            if (quest is null) continue;
            Results(quest.m_startResults);
            Results(quest.m_endResults);
            foreach (var goal in quest.m_goals ?? []) {
                if (goal is null) continue;
                Results(goal.m_completeResults);
                foreach (ByteString t in goal.m_clientTags ?? []) tags.Add(Norm((string)t));
            }
        }
        tags.Remove(null);
        s_questTouched = Tuple.Create(version, tags);
        return tags;
    }

    public static ResultList FinishedUsageHides(string zonePath, GoalTemplate goal) {
        if (s_off || string.IsNullOrEmpty(zonePath) || goal is null || goal.m_goalType != GOAL_TYPE.GOAL_TYPE_USAGE) return null;
        if (!ResourceManager.TryLoadArchive(zonePath, out var wad)) return null;
        var data = ResourceManager.OpenZoneData(wad)?.ToArray();
        if (data is null || !new BindSerializer().Deserialize<WizZoneData>(data, 1u, out var zone) || zone?.m_objectList is null) return null;

        var tags = new HashSet<string>(goal.m_clientTags?.Select(t => Norm((string)t)) ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var usage in zone.m_objectList) {
            if (!tags.Contains(Norm((string?)usage?.m_zoneTag))) continue;
            var blocker = zone.m_objectList
                .Where(o => !string.IsNullOrEmpty((string?)o?.m_zoneTag) && LooksLikeBlocker((string)o.m_zoneTag))
                .Select(o => (Object: o, Distance: DistanceSquared(o.m_location, usage.m_location)))
                .Where(x => x.Distance <= 25000000d)
                .OrderBy(x => x.Distance)
                .FirstOrDefault().Object;
            if (blocker is not null) tags.Add(Norm((string)blocker.m_zoneTag));
        }

        var results = new ResultList { m_results = [] };
        foreach (var tag in tags) {
            results.m_results.Add(new ResAddDynaMod {
                m_dynaModClientTag = tag + " instance",
                m_dynaModState = "",
                m_zoneName = "",
                m_bShowsObject = false,
                m_dynaModRemove = true,
                m_persist = true,
            });
        }
        return results.m_results.Count > 0 ? results : null;
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
