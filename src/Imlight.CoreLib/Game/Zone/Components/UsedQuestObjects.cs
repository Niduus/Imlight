using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.RegularExpressions;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.States;

namespace Imlight.CoreLib.Game.Zone.Components;

/// <summary>
/// What a quest object looks like once a wizard has used it for their goal (Niduus, 2026-10-31: the Cave Escape bubbles
/// and the Cyclops tower's Crystal Ball stayed after "Goal Complete"). Many quest objects only carry a plain interact
/// option with a goal tag and a quest event, and the game changes them for the wizard through the object's state set: its
/// start state has a single way out, played through its transition (Bubble_On_IDLE to Bubble_Off_END by Bubble_Xplod_PASS,
/// Active_IDLE to Off_END by Xplod_PASS). That way out is the used look. Objects whose state sets are shared by creatures
/// and plain toggles (levers, chests, braziers) keep their look: their way out leads back, unless its name says the
/// object is gone.
/// </summary>
internal static class UsedQuestObjects {

    // Shared state sets of creatures, wisps and the generic Interacting toggle: never a used look.
    private static readonly string[] SharedSets = ["NPCMobileStates", "InteractiveState", "WispStates", "Player"];

    private static readonly Regex GoneName = new("gone|destroy|looted|invisible|escaped|off_end", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly ConcurrentDictionary<uint, string> s_byTemplate = new();

    /// <summary>The state the object takes for the wizard who used it, or null when it keeps its look.</summary>
    internal static string UsedState(GameObjectTemplate template) {
        if (template is null) {
            return null;
        }

        return s_byTemplate.GetOrAdd(template.m_templateID, _ => Find(template));
    }

    /// <summary>The look the object starts in (before it was used), or null when it has none to go back to.</summary>
    internal static string StartState(GameObjectTemplate template) {
        var setName = template?.m_behaviors?.OfType<ObjectStateBehaviorTemplate>().Select(b => (string) b.m_stateSetName)
            .FirstOrDefault(n => !string.IsNullOrEmpty(n));
        if (setName is null) {
            return null;
        }

        var category = StateFactory.GetStateSet(setName)?.m_categories?
            .FirstOrDefault(c => c is not null && !string.Equals(c.m_categoryName, "Interacting", StringComparison.OrdinalIgnoreCase));
        string start = category?.m_startState;

        return string.IsNullOrEmpty(start) ? null : start;
    }

    private static string Find(GameObjectTemplate template) {
        var behaviors = template.m_behaviors ?? [];
        // Only objects whose goal option is a plain one: a state option already names the state it enters.
        var hasPlainGoalOption = behaviors.OfType<InteractableBehaviorTemplate>()
            .SelectMany(b => b.m_interactOptions ?? [])
            .Any(o => o is not null && o.GetType() == typeof(InteractOptionTemplate) && o.m_goalTags is { Count: > 0 });
        var setName = behaviors.OfType<ObjectStateBehaviorTemplate>().Select(b => (string) b.m_stateSetName).FirstOrDefault(n => !string.IsNullOrEmpty(n));
        if (!hasPlainGoalOption || setName is null || SharedSets.Contains(setName, StringComparer.OrdinalIgnoreCase)) {
            return null;
        }

        var category = StateFactory.GetStateSet(setName)?.m_categories?
            .FirstOrDefault(c => c is not null && !string.Equals(c.m_categoryName, "Interacting", StringComparison.OrdinalIgnoreCase));
        var states = category?.m_states?.Where(s => s is not null).ToList();
        var start = states?.FirstOrDefault(s => s.m_stateName == category.m_startState);
        if (start?.m_transitions is not { Count: 1 } ways) {
            return null;
        }

        string target = ways[0].m_targetState;
        var end = states.FirstOrDefault(s => s.m_stateName == target);
        if (end is null || target == start.m_stateName) {
            return null;
        }

        var lasts = end.m_transitions is not { Count: > 0 };

        return lasts || GoneName.IsMatch(target) ? target : null;
    }

}
