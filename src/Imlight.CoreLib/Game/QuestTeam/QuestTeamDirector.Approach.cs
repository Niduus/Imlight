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
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.Ultimus;
using Imlight.CoreLib.Game.Zone.Components;

namespace Imlight.CoreLib.Game.QuestTeam;

/// <summary>
/// What Ultimus learned about being stuck, for the Quest Team too (Niduus, 2026-11-15: "Are they caught up on everything
/// Ultimus can do?"): a new approach after ten alarms at one spot on one step (the creatures around are fought first, else
/// the team takes a short break), thinking a stuck spot through with the local AI ([Ultimus] StuckThinking, the same switch),
/// and the overnight watchdog that pauses the team after two hours with nothing done. Only moves players make: nothing
/// finishes or skips a step for them.
/// </summary>
internal sealed partial class QuestTeamDirectorActor {

    private const int ALARMS_TO_CHANGE = 10;
    private const int THINK_FROM_ALARM = 2;
    private static readonly TimeSpan THINK_AGAIN = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan BREAK_FOR = TimeSpan.FromMinutes(2);
    private const string WATCHDOG_STOP_FILE = "watchdog-stop.txt";

    private static bool StuckThinkingOn => ConfigurationManager.GetValue("Ultimus.StuckThinking", true);

    // Alarms per spot and step this session ("zone|cell|step").
    private readonly Dictionary<string, int> _alarms = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _askedFor = new(StringComparer.OrdinalIgnoreCase);
    private (string Zone, float X, float Y, DateTime Until)? _clearAround;
    private DateTime _breakUntil = DateTime.MinValue;
    private bool _asking;

    // Real progress (a step, a level, a new area or quest), for the watchdog; _lastProgress is also reset by the stuck alarm.
    private DateTime _progressAt = DateTime.UtcNow;
    private string _progressSignature = "";

    /// <summary>The local AI's idea about a stuck spot, back from the background.</summary>
    private sealed record TeamStuckIdea(string Quest, string Step, string Zone, float X, float Y, string Where, string Answer);

    private static string AlarmKey(QuestTeamMember.Status member, string step)
        => $"{member.Zone}|{(int) (member.X / 500)}|{(int) (member.Y / 500)}|{step}";

    /// <summary>
    /// The leader's alarm at a spot on a step is counted; every tenth one changes the approach: the free creatures within
    /// reach are fought first (true), or, with none, the team sits a short break before trying again (true).
    /// </summary>
    private bool TenAlarms(QuestTeamMember.Status leader, string step, string where) {
        var key = AlarmKey(leader, step);
        var count = _alarms[key] = _alarms.GetValueOrDefault(key) + 1;
        if (count % ALARMS_TO_CHANGE != 0) {
            return false;
        }

        SetStuckLine($"stuck {count} times in {where} on \"{_target?.What ?? _doing}\": tries a new approach.");

        var foes = BotUseGuards.Near(leader.ZoneActor, leader.X, leader.Y)
            .Where(m => !CombatDuelComponent.IsInBattle(m.Object.m_globalID.Full)).ToList();
        if (foes.Count > 0) {
            _clearAround = (leader.Zone ?? "", leader.X, leader.Y, DateTime.UtcNow.AddMinutes(10));
            Note(leader.Role, $"NEW APPROACH: stuck {count} times in {where} on \"{_target?.What ?? _doing}\": the same moves are no use, so the team " +
                              $"fights the {foes.Count} creature(s) around first ({string.Join(", ", foes.Select(f => f.Name).Distinct().Take(3))}), then tries again.",
                QuestTeamLink.NOTABLE);

            return true;
        }

        _breakUntil = DateTime.UtcNow + BREAK_FOR;
        Note(leader.Role, $"NEW APPROACH: stuck {count} times in {where} on \"{_target?.What ?? _doing}\" with no creatures around: the team takes a " +
                          "short break and then finds its way fresh.", QuestTeamLink.NOTABLE);
        Issue("team", _target?.Quest ?? _currentQuest, $"The team was stuck {count} times in {where} on \"{_target?.What ?? _doing}\" and nothing " +
                                                       "freed it; it takes a short break and tries again.", key: "tenalarms|" + key, skipStep: false, level: QuestTeamLink.ABNORMAL);
        for (var i = 0; i < _team.Count; i++) {
            StopWalking(i);
        }

        return true;
    }

    /// <summary>True while the team takes its short break (nothing else is planned).</summary>
    private bool OnBreak() {
        if (DateTime.UtcNow >= _breakUntil) {
            return false;
        }

        if (_team.Any(m => CombatDuelComponent.IsInBattle(m.GlobalId))) {
            _breakUntil = DateTime.MinValue;

            return false;
        }

        _doing = "a short break before trying the step again";
        _lastProgress = DateTime.UtcNow;
        for (var i = 0; i < _team.Count; i++) {
            StopWalking(i);
        }

        return true;
    }

    // While clearing round a stuck spot: the nearest free creature there comes first.
    private QuestTeamPlanner.Target ClearAroundFight(QuestTeamMember.Status leader) {
        if (_clearAround is not { } clear) {
            return null;
        }

        if (DateTime.UtcNow > clear.Until || !string.Equals(clear.Zone, leader.Zone ?? "", StringComparison.OrdinalIgnoreCase)) {
            _clearAround = null;

            return null;
        }

        var foe = BotUseGuards.Near(leader.ZoneActor, clear.X, clear.Y)
            .Where(m => !CombatDuelComponent.IsInBattle(m.Object.m_globalID.Full))
            .OrderBy(m => QuestTeamPlanner.Far(leader.X, leader.Y, m.Object.m_location.X, m.Object.m_location.Y)).FirstOrDefault();
        if (foe is null) {
            _clearAround = null;
            Note(leader.Role, "The team cleared the creatures round the spot where it was stuck: tries its step again.", QuestTeamLink.NOTABLE);

            return null;
        }

        return new QuestTeamPlanner.Target(QuestTeamPlanner.FIGHT, foe.Object.m_location.X, foe.Object.m_location.Y,
            $"{foe.Name}, by the spot where the team was stuck (clears it, then tries again)", _currentQuest ?? "", Template: foe.Template,
            GlobalId: foe.Object.m_globalID.Full, Key: "clear|" + foe.Object.m_globalID.Full, Z: foe.Object.m_location.Z);
    }

    // ---------------------------------------------------------------------
    // Thinking a stuck spot through with the local AI (as Ultimus does, UltimusDirector.Reason.cs)
    // ---------------------------------------------------------------------

    private const string ThinkRules =
        "You help the Quest Team, four Wizard101 wizard bots in a private server who quest the main story together like human players. " +
        "Their leader is stuck on a quest step. Read the facts, guess the most likely cause in one short plain sentence, and pick exactly ONE move " +
        "from the list of allowed moves. Rules: they play like humans. They never skip or finish a step by command and only use the allowed moves. " +
        "If creatures stand by the step and the step or the game's message hints at a fight or a guard, prefer fight_nearby. If the game offers " +
        "something to click next to the leader, prefer click_offered. If they are walled in or the way finder fails, prefer other_route or " +
        "walk_away_and_back. If nothing fits, pick set_step_aside (a short break). Answer with JSON only, no other text: " +
        "{\"cause\": \"...\", \"move\": \"<one move id>\", \"why\": \"...\"}. Keep cause and why under 160 characters each, plain words.";

    private static readonly string[] s_teamMoves = [
        UltimusReasonMoves.ASIDE, UltimusReasonMoves.AWAY, UltimusReasonMoves.OTHER_WAY, UltimusReasonMoves.FIGHT, UltimusReasonMoves.CLICK,
        UltimusReasonMoves.PARK,
    ];

    // The moves that can be made where the leader stands (no Commons button: the team never uses it).
    private static List<string> TeamMoves(QuestTeamMember.Status leader) {
        var moves = new List<string> { UltimusReasonMoves.ASIDE, UltimusReasonMoves.AWAY, UltimusReasonMoves.OTHER_WAY };
        if (BotUseGuards.Near(leader.ZoneActor, leader.X, leader.Y).Any()) {
            moves.Add(UltimusReasonMoves.FIGHT);
        }

        if (OfferedHere(leader).Count > 0) {
            moves.Add(UltimusReasonMoves.CLICK);
        }

        moves.Add(UltimusReasonMoves.PARK);

        return moves;
    }

    private static List<(ulong GlobalId, string Service, uint Index, string Display)> OfferedHere(QuestTeamMember.Status member)
        => [.. member.Options.SelectMany(kv => kv.Value.Where(o => (o.Service ?? "").StartsWith("Quest", StringComparison.OrdinalIgnoreCase))
            .Select(o => (kv.Key, o.Service, o.Index, o.Display ?? "")))];

    /// <summary>Stuck on the same step again (from Unstick): the local AI is asked in the background; the usual fix runs meanwhile.</summary>
    private void AskAboutStuck(QuestTeamMember.Status leader, string quest, string step, string where) {
        if (!StuckThinkingOn || !UltimusLocalAI.CanThink || _asking) {
            return;
        }

        var key = AlarmKey(leader, step);
        if (_alarms.GetValueOrDefault(key) < THINK_FROM_ALARM || _askedFor.TryGetValue(key, out var asked) && DateTime.UtcNow - asked < THINK_AGAIN) {
            return;
        }

        _askedFor[key] = DateTime.UtcNow;
        _asking = true;
        var q = new StringBuilder();
        q.AppendLine($"Quest: {(string.IsNullOrEmpty(quest) ? "none known" : QuestTeamText.Title(quest))}.");
        q.AppendLine($"Step: {_target?.What ?? _doing ?? "unknown"}.");
        if (!string.IsNullOrEmpty(_target?.Problem)) {
            q.AppendLine($"Planner note on the step: {_target.Problem}");
        }

        q.AppendLine($"Doing: {_doing}. Where: {where}. Levels: {string.Join(", ", _team.Select(m => $"{m.Role} {m.Wizard?.MagicSchoolBehavior?.Level ?? 0}"))}.");
        if (!string.IsNullOrWhiteSpace(leader.LastServerText)) {
            q.AppendLine($"Last thing the game told the leader: {QuestTeamText.Short(leader.LastServerText, 200)}");
        }

        var offered = OfferedHere(leader).Select(o => o.Display.Length > 0 ? o.Display : o.Service).Distinct().Take(6).ToList();
        q.AppendLine(offered.Count > 0 ? $"Offered to click next to the leader: {string.Join(", ", offered)}." : "Nothing offered to click next to the leader.");
        var foes = BotUseGuards.Near(leader.ZoneActor, leader.X, leader.Y).Select(f => f.Name).Distinct().Take(5).ToList();
        q.AppendLine(foes.Count > 0 ? $"Creatures near: {string.Join(", ", foes)}." : "No creatures near.");
        foreach (var known in UltimusSolutions.Known(quest, step, leader.Zone, leader.X, leader.Y)) {
            q.AppendLine($"Tried for this step before: {known.Move} ({known.Worked} worked, {known.Failed} failed){(known.Cause.Length > 0 ? $", thought: {known.Cause}" : "")}.");
        }

        foreach (var tip in UltimusTips.For(quest)) {
            q.AppendLine("Player tip: " + QuestTeamText.Short(tip, 200));
        }

        q.AppendLine();
        q.AppendLine("Allowed moves (id: what it does):");
        foreach (var move in TeamMoves(leader)) {
            q.AppendLine($"{move}: {(move == UltimusReasonMoves.PARK ? "take a short break and try again fresh" : UltimusReasonMoves.Words(move))}");
        }

        Note(leader.Role, $"{leader.Name} is stuck on the same step again at {where}: the team thinks it through with the {UltimusLocalAI.Who} while it tries its usual fix.",
            QuestTeamLink.NOTABLE);
        var (iQuest, iStep, iZone, iX, iY) = (quest ?? "", step ?? "", leader.Zone ?? "", leader.X, leader.Y);
        UltimusLocalAI.Think(ThinkRules, q.ToString())
            .ContinueWith(t => new TeamStuckIdea(iQuest, iStep, iZone, iX, iY, where, t.IsCompletedSuccessfully ? t.Result : null))
            .PipeTo(Self);
    }

    private void OnTeamStuckIdea(TeamStuckIdea m) {
        _asking = false;
        if (m.Answer is null || _team.Count == 0 || _team[0] is not { State: "playing" } leader) {
            if (m.Answer is null) {
                Note("Team", $"the {UltimusLocalAI.Who} did not answer in time; the team carries on with its own fixes.");
            }

            return;
        }

        var (cause, move, why) = ReadIdea(m.Answer);
        if (!string.Equals(leader.Zone ?? "", m.Zone, StringComparison.OrdinalIgnoreCase) || QuestTeamPlanner.Far(m.X, m.Y, leader.X, leader.Y) > 800
            || _team.Any(t => CombatDuelComponent.IsInBattle(t.GlobalId)) || DateTime.UtcNow < _breakUntil) {
            return;
        }

        if (move is null || !TeamMoves(leader).Contains(move)) {
            Note(leader.Role, $"the {UltimusLocalAI.Who}'s idea could not be used (it answered: \"{QuestTeamText.Short(m.Answer.Replace('\n', ' '), 160)}\").");

            return;
        }

        Note(leader.Role, $"THOUGHT IT THROUGH at {m.Where}: cause \"{(cause.Length > 0 ? QuestTeamText.Short(cause, 160) : "not clear")}\"; " +
                          $"the team will {(move == UltimusReasonMoves.PARK ? "take a short break" : UltimusReasonMoves.Words(move))}.", QuestTeamLink.NOTABLE);
        MakeTeamMove(leader, move, m.Quest);
        if (move != UltimusReasonMoves.PARK) {
            _teamTry = new TeamTry {
                Solution = UltimusSolutions.Tried(m.Quest, m.Step, leader.Zone, leader.X, leader.Y, QuestTeamText.Short(cause, 160), move,
                    QuestTeamText.Short(why, 160), "Quest Team local AI"),
                Zone = leader.Zone ?? "", X = leader.X, Y = leader.Y, At = DateTime.UtcNow,
            };
        }
    }

    private void MakeTeamMove(QuestTeamMember.Status leader, string move, string quest) {
        const int index = 0;
        _sent.Remove(index);
        OverlayQuestPath.Forget(leader.CharId);
        switch (move) {
            case UltimusReasonMoves.ASIDE:
            case UltimusReasonMoves.AWAY:
            case UltimusReasonMoves.OTHER_WAY: {
                var (near, far) = move == UltimusReasonMoves.ASIDE ? (150f, 600f) : move == UltimusReasonMoves.AWAY ? (900f, 2500f) : (600f, 1600f);
                RouteMemory.Stuck(leader.CharId, leader.Zone, leader.X, leader.Y);
                var spot = BotStuck.StepAside(leader.Zone, leader.X, leader.Y, leader.Z, near, far)
                           ?? (leader.X + Random.Shared.Next(-400, 400), leader.Y + Random.Shared.Next(-400, 400));
                var walk = OverlayQuestPath.Walk(leader.CharId, leader.Zone, leader.X, leader.Y, spot.X, spot.Y, body: true, fromZ: leader.Z);
                _actors[index].Tell(new QuestTeamMember.WalkTo(walk, "gets unstuck (the team's own idea)"));
                _unstickUntil[index] = DateTime.UtcNow + UNSTICK_FOR;
                return;
            }
            case UltimusReasonMoves.FIGHT:
                _clearAround = (leader.Zone ?? "", leader.X, leader.Y, DateTime.UtcNow.AddMinutes(10));
                return;
            case UltimusReasonMoves.CLICK: {
                var offers = OfferedHere(leader);
                var picked = BotOptions.Pick([.. offers.Select(o => (o.Service, o.Index, o.Display))], quest, 0);
                var offer = offers.FirstOrDefault(o => o.Service == picked.Service && o.Index == picked.Index && o.Display == picked.Display);
                if (offer.Service is not null) {
                    Note(leader.Role, $"{leader.Name} clicks what is offered next to it ({(offer.Display.Length > 0 ? offer.Display : offer.Service)}).");
                    _actors[index].Tell(new QuestTeamMember.Interact(offer.GlobalId, offer.Service, offer.Index, "what is offered"));
                    _unstickUntil[index] = DateTime.UtcNow + UNSTICK_FOR;
                }

                return;
            }
            default:
                _breakUntil = DateTime.UtcNow + BREAK_FOR;
                return;
        }
    }

    // "cause", "move", "why" from the model's answer (the first {...}; a move id named anywhere when no JSON is found).
    private static (string Cause, string Move, string Why) ReadIdea(string answer) {
        var start = answer.IndexOf('{');
        var end = answer.LastIndexOf('}');
        if (start >= 0 && end > start) {
            try {
                var json = JsonNode.Parse(answer[start..(end + 1)]);
                var move = ((string) json?["move"] ?? "").Trim().ToLowerInvariant();

                return ((string) json?["cause"] ?? "", move.Length > 0 ? move : null, (string) json?["why"] ?? "");
            }
            catch (Exception) {
                // Not JSON after all.
            }
        }

        return ("", s_teamMoves.FirstOrDefault(mv => answer.Contains(mv, StringComparison.OrdinalIgnoreCase)), "");
    }

    // ---------------------------------------------------------------------
    // The overnight watchdog: two hours with nothing done pauses the team (as it pauses Ultimus)
    // ---------------------------------------------------------------------

    // The same playbook rule as Ultimus's; 0 or less turns the pause off.
    private static double WatchdogPauseMinutes => UltimusPlaybook.Value("Watchdog.PauseMinutes");

    private void TeamWatchdog(string signature) {
        if (signature != _progressSignature) {
            _progressSignature = signature;
            _progressAt = DateTime.UtcNow;

            return;
        }

        var idle = DateTime.UtcNow - _progressAt;
        if (_paused || WatchdogPauseMinutes <= 0 || idle < TimeSpan.FromMinutes(Math.Max(30, WatchdogPauseMinutes))) {
            return;
        }

        _progressAt = DateTime.UtcNow;
        var leader = _team.Count > 0 ? _team[0] : null;
        var where = leader is null ? "" : $"{QuestTeamText.Zone(leader.Zone)} at ({leader.X:0}, {leader.Y:0}, height {leader.Z:0})";
        var text = new StringBuilder();
        text.AppendLine($"Stopped at {DateTime.Now:yyyy-MM-dd HH:mm}: no step done, no level and no new area for {idle.TotalMinutes:0} minutes.");
        text.AppendLine($"Where: {where}.");
        text.AppendLine($"Quest: {(string.IsNullOrEmpty(_currentQuest) ? "none" : QuestTeamText.Title(_currentQuest))}");
        if (_target is { } target) {
            text.AppendLine($"Step: {target.What} at ({target.X:0}, {target.Y:0}).");
        }

        text.AppendLine($"Doing: {_doing}");
        try {
            Directory.CreateDirectory(QuestTeamWire.Folder);
            File.WriteAllText(Path.Combine(QuestTeamWire.Folder, WATCHDOG_STOP_FILE), text.ToString());
            File.WriteAllText(Path.Combine(QuestTeamWire.Folder, QuestTeamWire.PAUSE_FILE), "Watchdog: " + text.ToString().Split('\n')[0]);
        }
        catch (IOException e) {
            Logger.Warning("Quest Team: could not write the watchdog stop: {0}", Logger.Args(e.Message));
        }

        Note("Team", $"WATCHDOG STOP: nothing done for {idle.TotalMinutes:0} minutes ({where}). The team is paused until Niduus resumes it in Ahziael " +
                     $"(what it was doing is in {WATCHDOG_STOP_FILE}).", QuestTeamLink.CRITICAL);
        Issue("team", _currentQuest, $"The watchdog paused the team: nothing done for {idle.TotalMinutes:0} minutes in {where}, while {_doing}.",
            key: "watchdog|" + where, skipStep: false, level: QuestTeamLink.CRITICAL);
    }

}
