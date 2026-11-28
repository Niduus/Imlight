using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.RegularExpressions;
using Akka.Actor;
using Imcodec.Math;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal static class QuestSpotGoals {
    private const float Reach = 450f;
    private const long CheckEveryTicks = 6000000L;

    private static readonly ConcurrentDictionary<(string Quest, string Goal, string Zone), (float X, float Y)[]> s_spots = new();
    private static readonly ConcurrentDictionary<IActorRef, long> s_lastCheck = new();

    internal static bool MatchesZone(WaypointGoalTemplate goal, string zone) {
        if (goal is null || string.IsNullOrEmpty(zone)) return false;
        if (string.Equals(goal.m_zoneTag, zone, StringComparison.OrdinalIgnoreCase)) return true;
        var location = goal.m_locationName;
        return !string.IsNullOrWhiteSpace(location)
            && string.Equals(NormalizeLocation(location), NormalizeZoneName(zone), StringComparison.Ordinal);
    }

    private static string NormalizeLocation(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string NormalizeZoneName(string zone) {
        var text = NormalizeLocation(zone.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "");
        var match = Regex.Match(text, "^avz\\d+(.*)$");
        return match.Success ? match.Groups[1].Value : text;
    }

    internal static (float X, float Y)[] SpotsFor(QuestTemplate quest, GoalTemplate goal, string zone) {
        if (quest is null || goal is not WaypointGoalTemplate { m_zoneEntry: true } waypoint
            || !string.IsNullOrEmpty(waypoint.m_proximityTag)
            || !MatchesZone(waypoint, zone)) {
            return [];
        }

        return s_spots.GetOrAdd((quest.m_questName, goal.m_goalName, zone), _ =>
            QuestArrowGoals.GetSpots(quest, goal)
                .Where(s => string.Equals(s.Zone, zone, StringComparison.OrdinalIgnoreCase))
                .Select(s => (X: s.Spot.X, Y: s.Spot.Y))
                .ToArray());
    }

    internal static void OnPlayerLeft(IActorRef player) => s_lastCheck.TryRemove(player, out _);

    internal static void OnPlayerMoved(string zone, ZONE_102_PROTOCOL.MSG_PLAYERMOVE message) {
        var wizard = message.PlayerWizard;
        if (wizard?.QuestBehavior is null || string.IsNullOrEmpty(zone)) return;

        var ticks = DateTime.UtcNow.Ticks;
        if (s_lastCheck.TryGetValue(message.PlayerActor, out var last) && ticks - last < CheckEveryTicks) return;
        s_lastCheck[message.PlayerActor] = ticks;

        var at = message.PlayerObject.m_location;
        foreach (var quest in wizard.QuestBehavior.CurrentQuestInstances.ToList()) {
            QuestTemplate questTemplate = null;
            foreach (var progress in quest.GoalProgress.ToList()) {
                if (!quest.IsGoalActive(progress.GoalName)) continue;
                questTemplate ??= QuestTemplateCollection.GetQuestByName(quest.QuestName);
                var goal = questTemplate?.m_goals?.FirstOrDefault(g => g.m_goalName == progress.GoalName);
                var spots = SpotsFor(questTemplate, goal, zone);
                if (spots.Any(s => (s.X - at.X) * (s.X - at.X) + (s.Y - at.Y) * (s.Y - at.Y) <= Reach * Reach)) {
                    message.PlayerActor.Tell(new ZONE_102_PROTOCOL.MSG_COMPLETEPROXIMITYGOAL {
                        QuestID = quest.ID,
                        GoalID = progress.ID,
                    });
                }
            }
        }
    }
}
