/*
 * Imlight
 * Copyright (C) 2026 Revive101
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
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Zoom's seat (Niduus, 2026-11-22: "Zoom's purpose is to teleport complete tasks in the Story Main quests ... He is permitted
/// to TP to his quests and to complete them as fast as possible"; "no commands ... He is to use the Hive Mind and Ultimus'
/// brain with a mix of TF, BUT, his goal is to mark down broken quests and/or broken steps in quests"). He plays Ultimus's
/// questing brain on the main story, reading everything Ultimus and the bots know, with what True Form has:
/// - He teleports to his step: when the step happens in another area, or far across this one, he is sent right to where the
///   game's quest arrow points (QuestStepSpots, as Ahziael's step buttons do). Never into a dungeon (its copy holds the
///   quest): to the area its way in is in, and he walks in from there. Three teleports per step at most, so a step he can't
///   finish where it says is no teleport loop.
/// - A step that does not finish in [Zoom] StepMinutes (10 unless set) is a broken step, and no next main quest that long
///   after one he finished is a broken quest: each is marked down in his own mind file (ZoomBook, logs/zoom/zoom-mind.json)
///   and as an issue. He never finishes anything by command: he keeps at it with Ultimus's own ways (and his teleports again)
///   until the quest is fixed.
/// </summary>
internal sealed partial class UltimusDirectorActor {

    // A teleport sent: he waits this long for it to land before anything else.
    private static readonly TimeSpan s_zoomLand = TimeSpan.FromSeconds(10);
    // Between two teleports for the same step.
    private static readonly TimeSpan s_zoomHopGap = TimeSpan.FromSeconds(45);
    private const int ZOOM_HOPS_PER_STEP = 3;
    // Closer than this to the step's spot in its own area, he walks.
    private const float ZOOM_WALK_REACH = 1500f;

    private string _zoomKey;
    private DateTime _zoomSince = DateTime.MinValue;
    private DateTime _zoomHopAt = DateTime.MinValue;
    private DateTime _zoomPushedAt = DateTime.MinValue;
    private int _zoomHops;
    private string _zoomQuest;
    private DateTime _zoomQuestSince = DateTime.MinValue;
    private int _zoomQuestHops;
    private int _zoomQuestBroken;

    private static TimeSpan ZoomStepLimit => TimeSpan.FromMinutes(Math.Clamp(ConfigurationManager.GetValue(UltimusSeat.Current.Section + ".StepMinutes", 10), 2, 120));

    private DateTime _zoomNoQuestSince = DateTime.MaxValue;
    private string _zoomLastDone;

    /// <summary>
    /// Zoom's part of each tick, out of battle and dialog: teleports to his step, and writes up and pushes past a broken
    /// one. True when he did something this tick (the rest of the brain waits for the next).
    /// </summary>
    private bool _zoomSpellsDone;
    private DateTime _zoomSpellsTry = DateTime.MinValue;

    private bool ZoomTick(UltimusMember.Status me) {
        // His spellbook (every Storm spell and enchantment) and his Tempest/Glowbug Squall + Epic deck, once the game's spell
        // list is read: checked every half minute until done (ZoomSpells); his deck is then made again from it.
        if (!_zoomSpellsDone && DateTime.UtcNow >= _zoomSpellsTry && me.Wizard is { IsInDuel: false } spellWizard) {
            _zoomSpellsTry = DateTime.UtcNow.AddSeconds(30);
            if (ZoomSpells.Ensure(spellWizard)) {
                _zoomSpellsDone = true;
                _deckFor.Remove(me.Role);
            }
        }

        if (me.Wizard?.QuestBehavior?.CurrentQuestInstances is not { } instances || me.Session is null) {
            return false;
        }

        // Savitar: parked once Zoom has caught up with him (SavitarStart).
        if (_seat.IsSavitar && SavitarHandedOff(me, instances)) {
            return true;
        }

        var now = DateTime.UtcNow;
        if (now - _zoomHopAt < s_zoomLand) {
            _doing = "teleporting to his quest step";
            _lastProgress = now;
            return true;
        }

        // His quest: the one the brain is on, else his first main story quest with a step to do.
        var instance = instances.FirstOrDefault(q => q is not null && q.QuestName == _currentQuest)
                       ?? instances.FirstOrDefault(q => q is not null && (q.QuestName ?? "").Contains("-MAIN-", StringComparison.OrdinalIgnoreCase));
        if (instance is null || QuestTemplateCollection.GetQuestByName(instance.QuestName) is not { m_goals: { Count: > 0 } goals } template) {
            ZoomQuestChanged(me, null);
            ZoomNoQuest(me, now);
            return false;
        }

        _zoomNoQuestSince = DateTime.MaxValue;

        var index = goals.FindIndex(g => g is not null && instance.IsGoalActive(g.m_goalName));
        if (index < 0) {
            return false;
        }

        var quest = instance.QuestName;
        var goal = goals[index];
        string goalName = goal.m_goalName;
        ZoomQuestChanged(me, quest);
        var key = quest + "|" + goalName;
        if (key != _zoomKey) {
            if (_zoomKey is not null && _zoomSince != DateTime.MinValue) {
                Note("Ultimus", $"{_seat.Name}: step done in {(now - _zoomSince).TotalSeconds:0} s; next \"{QuestGuide.StepText(goal)}\" of {QuestGuide.QuestTitle(template)}.");
            }

            _zoomKey = key;
            _zoomSince = now;
            _zoomHops = 0;
        }

        // A step that never finishes: a broken step, marked down in his mind file. He keeps at it (and may teleport again).
        if (now - _zoomSince > ZoomStepLimit && now - _zoomPushedAt > ZoomStepLimit) {
            ZoomBroken(me, template, index + 1, goal, now - _zoomSince);
        }

        if (_zoomHops >= ZOOM_HOPS_PER_STEP || now - _zoomHopAt < s_zoomHopGap) {
            return false;
        }

        var spot = QuestStepSpots.Find(template, index + 1);
        if (spot is null || string.IsNullOrEmpty(spot.Zone)) {
            return false;
        }

        var zone = spot.Zone;
        var location = spot.Location;
        var here = me.Zone ?? "";
        if (OverlayQuestRoute.IsDungeon(zone) && !OverlayQuestTracker.SameZone(zone, here)) {
            // Never into a dungeon: its copy holds the quest. To the area its way in is in, and he walks in from there.
            if (OverlayQuestRoute.IsDungeon(here)) {
                return false;
            }

            var way = OverlayQuestRoute.Way(here, zone, spiralDoorHere: false);
            var outside = way.Select(w => w.Zone).LastOrDefault(z => !string.IsNullOrEmpty(z) && !OverlayQuestRoute.IsDungeon(z));
            if (outside is null || OverlayQuestTracker.SameZone(outside, here)) {
                return false;
            }

            zone = outside;
            location = "Start";
        }

        if (OverlayQuestTracker.SameZone(zone, here)) {
            if (location == "Start") {
                return false;
            }

            var at = Util.GetVectorFromCompactString(location);
            if (UltimusPlanner.Far(me.X, me.Y, at.X, at.Y) < ZOOM_WALK_REACH) {
                return false;
            }
        }

        if (TeleportRooms.Refusal(zone, here) is not null) {
            return false;
        }

        StopWalking(0);
        _zoomHops++;
        _zoomQuestHops++;
        _zoomHopAt = now;
        // A move of his own, never a wrong landing (NoteLeaderZone).
        _recallSentAt = now;
        _buttonFromZone = here;
        _doing = "teleporting to his quest step";
        _lastProgress = now;
        Note("Ultimus", $"{_seat.Name} teleports to \"{QuestGuide.StepText(goal)}\" of {QuestGuide.QuestTitle(template)}: {UltimusText.Zone(zone)}" +
                        $"{(location == "Start" ? "" : $" ({spot.How})")} (teleport {_zoomHops} of {ZOOM_HOPS_PER_STEP} for this step).");
        me.Session.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
            DestinationZone = zone,
            DestinationLocation = location,
            SendToClient = true,
            // Through a real zone transfer, also within his own area, so his game loads where he lands.
            ForceReload = true,
            OwnerCharId = me.CharId,
        }, Self);
        return true;
    }

    // His quest changed: the one before is written down as scouted when it is done (no longer in his book).
    private void ZoomQuestChanged(UltimusMember.Status me, string quest) {
        if (quest == _zoomQuest) {
            return;
        }

        if (_zoomQuest is not null && QuestTemplateCollection.GetQuestByName(_zoomQuest) is { } before
            && me.Wizard?.QuestBehavior?.CurrentQuestInstances?.Any(q => q?.QuestName == _zoomQuest) != true) {
            ZoomBook.Done(_zoomQuest, QuestGuide.QuestTitle(before), DateTime.UtcNow - _zoomQuestSince, _zoomQuestHops, _zoomQuestBroken);
            _zoomLastDone = _zoomQuest;
        }

        _zoomQuest = quest;
        _zoomQuestSince = DateTime.UtcNow;
        _zoomQuestHops = 0;
        _zoomQuestBroken = 0;
    }

    private void ZoomBroken(UltimusMember.Status me, Imcodec.ObjectProperty.TypeCache.QuestTemplate template, int step,
        Imcodec.ObjectProperty.TypeCache.GoalTemplate goal, TimeSpan stuckFor) {
        string quest = template.m_questName;
        string goalName = goal.m_goalName;
        var title = QuestGuide.QuestTitle(template);
        var stepText = QuestGuide.StepText(goal);
        _zoomPushedAt = DateTime.UtcNow;
        var hops = _zoomHops;
        // Another round of teleports for it: the next try starts from its spot again.
        _zoomHops = 0;
        if (ZoomBook.Has(ZoomBook.STEP, quest, goalName)) {
            return;
        }

        var why = $"\"{stepText}\" did not finish in {(int) stuckFor.TotalMinutes} minutes, after {hops} teleport{(hops == 1 ? "" : "s")} to it" +
                  $" and Ultimus's own ways of doing it, at level {me.Wizard?.MagicSchoolBehavior?.Level}, winning every fight in one hit.";
        ZoomBook.Add(new ZoomBook.Broken(DateTime.UtcNow, ZoomBook.STEP, quest, title, step, stepText, UltimusText.Zone(me.Zone),
            why, string.IsNullOrEmpty(_doing) ? "?" : _doing));
        _zoomQuestBroken++;
        Issue("quest", quest, $"{_seat.Name} (quest scout) marked a broken step in {title}, step {step}: {why}", key: _seat.Key.ToLowerInvariant() + "|" + quest + "|" + goalName);
        Note("Ultimus", $"{_seat.Name}: \"{stepText}\" of {title} looks broken; marked down in his mind file. He keeps trying it.", UltimusLink.NOTABLE);
    }

    // No main quest in his book: after a quest he finished, the next one never coming is a broken quest.
    private void ZoomNoQuest(UltimusMember.Status me, DateTime now) {
        if (_zoomNoQuestSince == DateTime.MaxValue) {
            _zoomNoQuestSince = now;
            return;
        }

        if (now - _zoomNoQuestSince < ZoomStepLimit || _zoomLastDone is null
            || ZoomBook.Has(ZoomBook.QUEST, _zoomLastDone, "next")) {
            return;
        }

        var title = QuestTemplateCollection.GetQuestByName(_zoomLastDone) is { } done ? QuestGuide.QuestTitle(done) : _zoomLastDone;
        var why = $"No main quest came for {(int) (now - _zoomNoQuestSince).TotalMinutes} minutes after he finished {title}, " +
                  "while Ultimus's brain looked for its giver.";
        ZoomBook.Add(new ZoomBook.Broken(DateTime.UtcNow, ZoomBook.QUEST, _zoomLastDone, title, 0, "next", UltimusText.Zone(me.Zone),
            why, string.IsNullOrEmpty(_doing) ? "?" : _doing));
        Issue("quest", _zoomLastDone, $"{_seat.Name} (quest scout) marked a broken quest: {why}", key: _seat.Key.ToLowerInvariant() + "|next|" + _zoomLastDone);
        Note("Ultimus", $"{_seat.Name}: the next main quest after {title} never came; marked down in his mind file.", UltimusLink.NOTABLE);
    }

    private DateTime _savitarCheckAt = DateTime.MinValue;
    private bool _savitarParked;

    // Savitar (Niduus, 2026-11-22: "quest ahead of Ultimus to do exactly what zoom does until Zoom catches up"): once a minute,
    // whether Zoom holds or has finished the main quest Savitar is on. Then Savitar stops where he is, for good (his
    // handoff.txt says when and where; deleting it starts him again).
    private bool SavitarHandedOff(UltimusMember.Status me, List<QuestInstance> instances) {
        if (_savitarParked) {
            _doing = "parked: Zoom caught up with him";
            _lastProgress = DateTime.UtcNow;
            return true;
        }

        if (DateTime.UtcNow < _savitarCheckAt) {
            return false;
        }

        _savitarCheckAt = DateTime.UtcNow.AddMinutes(1);
        var main = instances.FirstOrDefault(q => q is not null && q.QuestName == _currentQuest && q.QuestName.Contains("-MAIN-", StringComparison.OrdinalIgnoreCase))
                   ?? instances.FirstOrDefault(q => q is not null && (q.QuestName ?? "").Contains("-MAIN-", StringComparison.OrdinalIgnoreCase));
        var why = SavitarStart.CaughtUp(main?.QuestName);
        if (why is null) {
            return false;
        }

        _savitarParked = true;
        StopWalking(0);
        _doing = "parked: Zoom caught up with him";
        _lastProgress = DateTime.UtcNow;
        Note("Ultimus", $"Savitar: {why}. Zoom has caught up, so Savitar stops here (logs/savitar/handoff.txt).", UltimusLink.NOTABLE);
        return true;
    }

}
