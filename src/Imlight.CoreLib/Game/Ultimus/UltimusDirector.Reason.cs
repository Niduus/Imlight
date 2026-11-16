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
using System.Text;
using System.Text.Json.Nodes;
using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.Zone.Components;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Thinking a stuck spot through (Niduus, 2026-11-14: "are you able to give Ultimus a little of autonomy that you have in
/// context to the project?", approved the local version): when he is stuck on the same step again, he first looks in his
/// memory for a move that worked there before (UltimusSolutions, shared with the Quest Team); with none, his local AI (the
/// one his chat can use, on Niduus's PC, never the internet) reads the quest step, what the game told him, what is around
/// him, the diagnosis, his tries, tips and thoughts, guesses the cause and picks ONE of his unstuck moves. He makes it,
/// checks whether it got him going, and writes the result down. Only unstuck moves a player makes: nothing finishes or
/// skips a step for him (the Ultimus human rule). [Ultimus] StuckThinking turns it off; it applies at once.
/// </summary>
internal sealed partial class UltimusDirectorActor {

    private static bool StuckThinkingOn => UltimusSeat.Current.Setting("StuckThinking", true);

    // Thinking is asked from the second alarm at one spot on one step, and at most once every few minutes there.
    private const int THINK_FROM_ALARM = 2;
    private static readonly TimeSpan THINK_AGAIN = TimeSpan.FromMinutes(3);

    // Got going: well away for this long, or no new alarm at the spot for this long.
    private static readonly TimeSpan GOING_AWAY_FOR = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan GOING_QUIET_FOR = TimeSpan.FromMinutes(5);
    private const float SAME_SPOT = 800f;

    private sealed record StuckIdea(string Quest, string Step, string GoalKey, string What, string Where, string Zone, float X, float Y, string Answer);

    private bool _asking;
    private readonly Dictionary<string, DateTime> _askedFor = new(StringComparer.OrdinalIgnoreCase);

    // The move he is trying now, from memory or from thinking, until it is known whether it worked.
    private sealed class ReasonedTry {
        public UltimusSolutions.Solution Solution;
        public BotShared.BotLessons.Situation Situation;
        public string Zone;
        public float X, Y;
        public DateTime At;
        public DateTime? AwaySince;
    }

    private ReasonedTry _reasonedTry;
    private string _lastFailedMove = "";

    /// <summary>
    /// Stuck again (from Unstick): a move from memory is made now (true), or his local AI is asked in the background and
    /// his normal fix runs meanwhile (false). <paramref name="skip"/>: the normal fixes that can't be made here.
    /// </summary>
    private bool ReasonAboutStuck(UltimusMember.Status member, UltimusPlanner.Target goal, string step, string quest, string where, string why,
                                  ICollection<string> skip) {
        // Stuck again by the spot of the move he tried: it did not work.
        if (_reasonedTry is { } tried && string.Equals(tried.Zone, member.Zone ?? "", StringComparison.OrdinalIgnoreCase)
                                      && UltimusPlanner.Far(tried.X, tried.Y, member.X, member.Y) <= SAME_SPOT) {
            UltimusSolutions.Result(tried.Solution, false);
            BotShared.BotLessons.Learn(tried.Situation, tried.Solution.Move, false, null);
            _lastFailedMove = tried.Solution.Move;
            _reasonedTry = null;
            Think($"\"{UltimusReasonMoves.Words(tried.Solution.Move)}\" did not get me going here. I write that down, so I don't count on it for this step.",
                $"reason-failed|{tried.Solution.Move}|{(int) member.X / 500}|{(int) member.Y / 500}");
        }

        var allowed = AllowedMoves(member, skip);
        var memoryAllowed = allowed.Where(m => m != UltimusReasonMoves.PARK && m != _lastFailedMove).ToList();

        // What worked here before, first.
        if (UltimusSolutions.Best(quest, step, member.Zone, member.X, member.Y, memoryAllowed) is { } best) {
            Think($"I was stuck on this step here before{(best.Cause.Length > 0 ? $" (I thought: {best.Cause})" : "")}, and " +
                  $"\"{UltimusReasonMoves.Words(best.Move)}\" got me going {best.Worked} time(s). I do that again.",
                $"reason-memory|{best.Move}|{(int) member.X / 500}|{(int) member.Y / 500}");
            Note("Ultimus", $"{member.Name} remembers what worked for this step at {where}: {UltimusReasonMoves.Words(best.Move)}.", UltimusLink.NOTABLE);
            MakeMove(member, best.Move, quest, goal?.Key, goal?.What ?? _doing, where);
            StartTry(UltimusSolutions.Tried(quest, step, member.Zone, member.X, member.Y, best.Cause, best.Move, best.Why, "memory"), member);

            return true;
        }

        if (!StuckThinkingOn || !UltimusLocalAI.CanThink || _asking) {
            return false;
        }

        var alarmKey = $"{member.Zone}|{(int) (member.X / 500)}|{(int) (member.Y / 500)}|{step}";
        if (_alarms.GetValueOrDefault(alarmKey) < THINK_FROM_ALARM
            || _askedFor.TryGetValue(alarmKey, out var asked) && DateTime.UtcNow - asked < THINK_AGAIN) {
            return false;
        }

        _askedFor[alarmKey] = DateTime.UtcNow;
        _asking = true;
        var question = StuckQuestion(member, goal, quest, where, why, allowed);
        Note("Ultimus", $"{member.Name} is stuck on the same step again at {where}: he thinks it through with his {UltimusLocalAI.Who} while he tries his usual fix.",
            UltimusLink.NOTABLE);
        Think("I am stuck here again. While I try my usual fix, I think it through: what the step asks, what the game told me, what is around me and what I already tried.",
            "reason-ask|" + alarmKey);
        var (iQuest, iStep, iKey, iWhat, iZone, iX, iY) = (quest ?? "", step ?? "", goal?.Key, goal?.What ?? _doing ?? "his step", member.Zone ?? "", member.X, member.Y);
        UltimusLocalAI.Think(ThinkRules, question)
            .ContinueWith(t => new StuckIdea(iQuest, iStep, iKey, iWhat, where, iZone, iX, iY, t.IsCompletedSuccessfully ? t.Result : null))
            .PipeTo(Self);

        return false;
    }

    private const string ThinkRules =
        "You help Ultimus, a Balance wizard bot in a private Wizard101 server, who quests the main story like a human player. He is stuck on a quest step. " +
        "Read the facts, guess the most likely cause in one short plain sentence, and pick exactly ONE move from the list of allowed moves. " +
        "Rules: he plays like a human. He never skips or finishes a step by command, never trains or crafts pets, and only uses the allowed moves. " +
        "Do not pick a move listed as already failed unless nothing else is left. If creatures stand by the step and the step or the game's message " +
        "hints at a fight or a guard, prefer fight_nearby. If the game offers something to click next to him, prefer click_offered. " +
        "If he is walled in or the way finder fails, prefer other_route or walk_away_and_back. If nothing fits, pick set_step_aside. " +
        "Facts of the game: a full backpack never stops a battle, a talk, a door, a lever or a quest step (quest items need no backpack room), " +
        "so never give the backpack as the cause; in a dungeon, setting the step aside wastes the run, so pick it last. " +
        "Maps: his way finder lays the game's own walk map over its rough one, so narrow ledges, bridges, ring walks and lift tops count as floor; " +
        "when a step is on a tower's top or a platform, the way is usually round the outer ring and over a bridge, not straight across, so prefer other_route. " +
        "Area book lines come from the area's own files: if one says a gate or door near him opens on beating a creature, prefer fight_nearby; " +
        "if it says the step is on another ground and no known teleporter leads there, prefer other_route (he tries teleporters he has not used yet). " +
        "Answer with JSON only, no other text: {\"cause\": \"...\", \"move\": \"<one move id>\", \"why\": \"...\"}. Keep cause and why under 160 characters each, plain words.";

    private string StuckQuestion(UltimusMember.Status member, UltimusPlanner.Target goal, string quest, string where, string why, List<string> allowed) {
        var q = new StringBuilder();
        var level = member.Wizard?.MagicSchoolBehavior?.Level ?? 0;
        q.AppendLine($"Quest: {(string.IsNullOrEmpty(quest) ? "none known" : UltimusText.Title(quest))}.");
        q.AppendLine($"Step: {goal?.What ?? _doing ?? "unknown"}.");
        if (!string.IsNullOrEmpty(goal?.Problem)) {
            q.AppendLine($"Planner note on the step: {goal.Problem}");
        }

        q.AppendLine($"Doing: {_doing}. Where: {where}. What happened: he {why}.");
        q.AppendLine($"Level {level}, health {HealthShare(member):P0}. In a dungeon: {(InDungeon(member) ? "yes" : "no")}.");
        if (!string.IsNullOrWhiteSpace(member.LastServerText)) {
            q.AppendLine($"Last thing the game told him: {UltimusText.Short(member.LastServerText, 200)}");
        }

        var offered = OfferedHere(member).Select(o => o.Display.Length > 0 ? o.Display : o.Service).Distinct().Take(6).ToList();
        q.AppendLine(offered.Count > 0 ? $"Offered to click next to him: {string.Join(", ", offered)}." : "Nothing offered to click next to him.");
        var foes = BotUseGuards.Near(member.ZoneActor, member.X, member.Y).Select(f => f.Name).Distinct().Take(5).ToList();
        q.AppendLine(foes.Count > 0 ? $"Creatures near him: {string.Join(", ", foes)}." : "No creatures near him.");
        foreach (var fact in UltimusGameData.Facts(goal?.Template ?? 0, BotUseGuards.Near(member.ZoneActor, member.X, member.Y).Select(f => (ulong) f.Template))) {
            q.AppendLine(fact);
        }

        // His own effort today, the bad as well as the good (UltimusEffort).
        foreach (var fact in UltimusEffort.Facts()) {
            q.AppendLine(fact);
        }
        if (!string.IsNullOrEmpty(quest) && Diagnose(quest) is { } d) {
            q.AppendLine(d.Summary);
            foreach (var check in d.Checks.Take(4)) {
                q.AppendLine("- check: " + UltimusText.Short(check, 160));
            }
        }

        if (goal is not null) {
            foreach (var fact in UltimusAreaBook.Facts(member.Zone, member.X, member.Y, goal.X, goal.Y)) {
                q.AppendLine(fact);
            }
        }

        if (BotMistakes.Near(BotMistakes.STUCK, member.Zone, member.X, member.Y) is { } lesson) {
            q.AppendLine($"Stuck at this spot {lesson.Times} time(s) before; fixes tried: {string.Join(", ", lesson.Tried.Distinct())}; " +
                         (lesson.Worked.Length > 0 ? $"what freed him once: {lesson.Worked}." : "nothing freed him yet."));
        }

        foreach (var known in UltimusSolutions.Known(quest, StepOf(goal), member.Zone, member.X, member.Y)) {
            q.AppendLine($"Tried for this step before: {known.Move} ({known.Worked} worked, {known.Failed} failed){(known.Cause.Length > 0 ? $", thought: {known.Cause}" : "")}.");
        }

        if (_lastFailedMove.Length > 0) {
            q.AppendLine($"Already failed just now: {_lastFailedMove}.");
        }

        foreach (var tip in UltimusTips.For(quest)) {
            q.AppendLine("Player tip: " + UltimusText.Short(tip, 200));
        }

        foreach (var thought in _recentThoughts.TakeLast(6)) {
            q.AppendLine("His recent thought: " + UltimusText.Short(thought, 200));
        }

        foreach (var entry in UltimusJournal.About(quest ?? "").TakeLast(3)) {
            q.AppendLine("Diary: " + UltimusText.Short(entry.Text, 160));
        }

        q.AppendLine();
        q.AppendLine("Allowed moves (id: what it does):");
        foreach (var move in allowed) {
            q.AppendLine($"{move}: {UltimusReasonMoves.Words(move)}");
        }

        return q.ToString();
    }

    private static string StepOf(UltimusPlanner.Target goal) => goal is not null ? $"{goal.Quest}|{goal.Goal}|{goal.What}" : null;

    /// <summary>His local AI's idea came back (or didn't): checked, then made if it still fits.</summary>
    private void OnStuckIdea(StuckIdea m) {
        _asking = false;
        if (m.Answer is null) {
            Note("Ultimus", $"his {UltimusLocalAI.Who} did not answer in time" + (UltimusLocalAI.LastProblem.Length > 0 ? $" ({UltimusLocalAI.LastProblem})" : "") +
                            "; he carries on with his own fixes.");
            return;
        }

        var (cause, move, why) = ReadIdea(m.Answer);
        if (_team.Count == 0 || _team[0] is not { State: "playing" } member) {
            return;
        }

        cause = Plain(cause);
        why = Plain(why);
        if (!string.Equals(member.Zone ?? "", m.Zone, StringComparison.OrdinalIgnoreCase) || UltimusPlanner.Far(m.X, m.Y, member.X, member.Y) > SAME_SPOT
            || member.Wizard is null || CombatDuelComponent.IsInBattle(member.GlobalId) || DateTime.UtcNow < _breakUntil) {
            Think($"I thought it through ({(cause.Length > 0 ? cause : "no clear cause")}), but I am already somewhere else or busy, so I let it be.",
                "reason-moved|" + m.Zone + "|" + (int) m.X / 500);
            return;
        }

        var allowed = AllowedMoves(member, null);
        if (move is null || !allowed.Contains(move)) {
            Think($"I thought it through{(cause.Length > 0 ? $" ({cause})" : "")}, but the move I came up with can't be made here, so I keep to my usual fixes.",
                "reason-bad|" + m.Zone + "|" + (int) m.X / 500);
            Note("Ultimus", $"his {UltimusLocalAI.Who}'s idea could not be used (it answered: \"{UltimusText.Short(m.Answer.Replace('\n', ' '), 160)}\").");
            return;
        }

        Think($"I thought it through. {(cause.Length > 0 ? $"My guess at the cause: {cause} " : "")}So I {UltimusReasonMoves.Words(move)}." +
              (why.Length > 0 ? $" Why: {why}" : ""), $"reason-idea|{move}|{m.Zone}|{(int) m.X / 500}|{(int) m.Y / 500}");
        Note("Ultimus", $"THOUGHT IT THROUGH at {m.Where}: cause \"{(cause.Length > 0 ? cause : "not clear")}\"; he will {UltimusReasonMoves.Words(move)}.",
            UltimusLink.NOTABLE);
        MakeMove(member, move, m.Quest, m.GoalKey, m.What, m.Where);
        if (move != UltimusReasonMoves.PARK) {
            StartTry(UltimusSolutions.Tried(m.Quest, m.Step, member.Zone, member.X, member.Y, cause, move, why, "local AI"), member);
        }
    }

    // "cause", "move", "why" from the model's answer; small models wrap JSON in words, so the first {...} is read, and a
    // move id named anywhere is taken when no JSON is found.
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
                // Not JSON after all: the move is looked for in the words below.
            }
        }

        var named = AllMoves.FirstOrDefault(mv => answer.Contains(mv, StringComparison.OrdinalIgnoreCase));

        return ("", named, "");
    }

    private static readonly string[] AllMoves = [
        UltimusReasonMoves.ASIDE, UltimusReasonMoves.AWAY, UltimusReasonMoves.OTHER_WAY, UltimusReasonMoves.COMMONS,
        UltimusReasonMoves.FIGHT, UltimusReasonMoves.CLICK, UltimusReasonMoves.PARK,
    ];

    // One line, no private details, not too long.
    private static string Plain(string text) {
        var t = BotSocial.Scrub((text ?? "").Replace('\r', ' ').Replace('\n', ' ').Replace("\"", "").Trim());

        return UltimusText.Short(t ?? "", 200);
    }

    /// <summary>The unstuck moves that can be made where he stands now (a player's moves only).</summary>
    private List<string> AllowedMoves(UltimusMember.Status member, ICollection<string> skip) {
        var moves = new List<string>();
        bool Skipped(string fix) => skip?.Contains(fix) == true;
        if (!Skipped(UltimusFixes.ASIDE) && !BoxedIn(member)) {
            moves.Add(UltimusReasonMoves.ASIDE);
        }

        if (!Skipped(UltimusFixes.AWAY)) {
            moves.Add(UltimusReasonMoves.AWAY);
        }

        if (!Skipped(UltimusFixes.OTHER_WAY)) {
            moves.Add(UltimusReasonMoves.OTHER_WAY);
        }

        if (!Skipped(UltimusFixes.START) && !InDungeon(member)) {
            moves.Add(UltimusReasonMoves.COMMONS);
        }

        if (BotUseGuards.Near(member.ZoneActor, member.X, member.Y).Any(f => !CombatDuelComponent.IsInBattle(f.Object.m_globalID.Full))) {
            moves.Add(UltimusReasonMoves.FIGHT);
        }

        if (OfferedHere(member).Count > 0) {
            moves.Add(UltimusReasonMoves.CLICK);
        }

        moves.Add(UltimusReasonMoves.PARK);

        return moves;
    }

    // What the people and things next to him offer for quests (a player clicks those).
    private static List<(ulong GlobalId, string Service, uint Index, string Display)> OfferedHere(UltimusMember.Status member)
        => [.. member.Options.SelectMany(kv => kv.Value.Where(o => (o.Service ?? "").StartsWith("Quest", StringComparison.OrdinalIgnoreCase))
            .Select(o => (kv.Key, o.Service, o.Index, o.Display ?? "")))];

    /// <summary>Makes one unstuck move (never anything that finishes or skips a step).</summary>
    private void MakeMove(UltimusMember.Status member, string move, string quest, string goalKey, string what, string where) {
        const int index = 0;
        if (move != UltimusReasonMoves.PARK && move != UltimusReasonMoves.CLICK) {
            _sent.Remove(index);
            OverlayQuestPath.Forget(member.CharId);
            _target = null;
        }

        switch (move) {
            case UltimusReasonMoves.ASIDE:
                Aside(index, member, 150f, 600f, "steps aside (his own idea)");
                return;
            case UltimusReasonMoves.AWAY:
                Aside(index, member, 900f, 2500f, "walks well away and comes back another way (his own idea)");
                return;
            case UltimusReasonMoves.OTHER_WAY:
                if (TryAnotherWay(index)) {
                    _unstickUntil[index] = DateTime.UtcNow + UNSTICK_FOR;
                    return;
                }

                Aside(index, member, 600f, 1600f, "takes a new route round the spot (his own idea)");
                return;
            case UltimusReasonMoves.COMMONS:
                Recall(index, member);
                return;
            case UltimusReasonMoves.FIGHT:
                _clearAround = (member.Zone ?? "", member.X, member.Y, DateTime.UtcNow.AddMinutes(10));
                return;
            case UltimusReasonMoves.CLICK:
                // The option named for his quest when there are several (BotOptions.Pick), else the first.
                var offers = OfferedHere(member);
                var picked = BotOptions.Pick([.. offers.Select(o => (o.Service, o.Index, o.Display))], quest, 0);
                var offer = offers.FirstOrDefault(o => o.Service == picked.Service && o.Index == picked.Index && o.Display == picked.Display);
                if (offer.Service is null) {
                    Aside(index, member, 150f, 600f, "steps aside (nothing to click after all)");
                    return;
                }

                Note("Ultimus", $"{member.Name} clicks what is offered next to him ({(offer.Display.Length > 0 ? offer.Display : offer.Service)}).");
                _actors[index].Tell(new UltimusMember.Interact(offer.GlobalId, offer.Service, offer.Index, what ?? "what is offered"));
                _unstickUntil[index] = DateTime.UtcNow + UNSTICK_FOR;
                return;
            default:
                ParkStep(member, quest, goalKey, what ?? "his step", where);
                return;
        }
    }

    private void StartTry(UltimusSolutions.Solution solution, UltimusMember.Status member) {
        _reasonedTry = new ReasonedTry { Solution = solution, Situation = _lastSituation, Zone = member.Zone ?? "", X = member.X, Y = member.Y, At = DateTime.UtcNow };
    }

    /// <summary>Each look (LookForStuck): the move he tried got him going (well away for a while, or no new alarm there).</summary>
    private void CheckReasonedTry(UltimusMember.Status member) {
        if (_reasonedTry is not { } t || string.IsNullOrEmpty(member.Zone) || member.State != "playing") {
            return;
        }

        var away = !string.Equals(t.Zone, member.Zone, StringComparison.OrdinalIgnoreCase) || UltimusPlanner.Far(t.X, t.Y, member.X, member.Y) > SAME_SPOT;
        if (away) {
            t.AwaySince ??= DateTime.UtcNow;
        }
        else {
            t.AwaySince = null;
        }

        var going = t.AwaySince is { } since && DateTime.UtcNow - since >= GOING_AWAY_FOR || DateTime.UtcNow - t.At >= GOING_QUIET_FOR;
        if (!going) {
            return;
        }

        _reasonedTry = null;
        _lastFailedMove = "";
        UltimusSolutions.Result(t.Solution, true);
        // Also for the kind of place it was, in every world (BotLessons).
        BotShared.BotLessons.Learn(t.Situation, t.Solution.Move, true, UltimusText.Zone(t.Zone));
        var (known, working) = UltimusSolutions.Count();
        Think($"\"{UltimusReasonMoves.Words(t.Solution.Move)}\" got me going. I remember it for this step, and the Quest Team can read it too.",
            $"reason-worked|{t.Solution.Move}|{t.Zone}|{(int) t.X / 500}|{(int) t.Y / 500}");
        Note("Ultimus", $"{member.Name}'s move \"{UltimusReasonMoves.Words(t.Solution.Move)}\" worked; written down ({known} stuck solutions known, {working} that work).");
    }

}
