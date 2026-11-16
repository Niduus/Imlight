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
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Ultimus;

namespace Imlight.CoreLib.Game.QuestTeam;

/// <summary>
/// The team talking among themselves through the AI (Niduus, 2026-11-15, "all" to the ideas): now and then, when two of
/// them stand together out of battle, one says a short typed line to the other in its own way and the other may answer.
/// Kept tame: at most one talk every [QuestTeam] AITeamTalkMinutes (0 = never), never while the team has just spoken, and
/// only with [QuestTeam] AIChat on. Each line costs one AI answer (counted in the daily limit for an online AI) and goes
/// through the same safety check as every other AI line. Nothing said changes the plan.
/// </summary>
internal sealed partial class QuestTeamDirectorActor {

    private DateTime _nextTeamTalk = DateTime.UtcNow.AddMinutes(3);
    private bool _teamTalking;

    /// <summary>[QuestTeam] AITeamTalkMinutes: the least minutes between two talks among the team (0 = never). Applies at once.</summary>
    private static int TeamTalkMinutes => Math.Max(0, ConfigurationManager.GetValue("QuestTeam.AITeamTalkMinutes", 10));

    /// <summary>A line of the team's own talk came back from the AI.</summary>
    private sealed record TeamTalkLine(int Speaker, int Partner, string Text, bool First, string Who);

    private void TeamTalk() {
        var minutes = TeamTalkMinutes;
        if (!AiChatOn || minutes == 0 || _teamTalking || DateTime.UtcNow < _nextTeamTalk || DateTime.UtcNow < _nextTeamLine) {
            return;
        }

        _nextTeamTalk = DateTime.UtcNow + TimeSpan.FromMinutes(minutes + _voiceRandom.NextDouble() * minutes / 2);
        var ready = Enumerable.Range(0, Math.Min(_team.Count, _actors.Count))
            .Where(i => _team[i].ZoneActor is not null && !(i < _wasInBattle.Length && _wasInBattle[i]) && !_team[i].Talking).ToList();
        var pairs = (from a in ready
                     from b in ready
                     where a != b && _team[a].ZoneActor.Equals(_team[b].ZoneActor) && QuestTeamPlanner.Far(_team[a].X, _team[a].Y, _team[b].X, _team[b].Y) < 400
                     select (a, b)).ToList();
        if (pairs.Count == 0) {
            return;
        }

        var (speaker, partner) = pairs[_voiceRandom.Next(pairs.Count)];
        var target = TeamTarget(_team[speaker].Role);
        if (!UltimusLocalAI.ReadyFor(target)) {
            return;
        }

        _teamTalking = true;
        var who = $"{UltimusLocalAI.WhoOf(target)}, {target.Model}";
        var system = TeamPrompt(speaker, null) +
                     $"\nRight now you say one short line to your teammate {_team[partner].Name} (not to a player): a comment, a joke or a plan about what " +
                     "the team is doing, in your own way of talking. Do not ask players anything and do not say it is a test.";
        UltimusLocalAI.AskWith(target, system, [], $"(Say something to {_team[partner].Name}.)", 0, "Quest Team talk")
            .ContinueWith(t => new TeamTalkLine(speaker, partner, t.IsCompletedSuccessfully ? t.Result : null, true, who))
            .PipeTo(Self);
    }

    private void OnTeamTalkLine(TeamTalkLine m) {
        if (m.Speaker < 0 || m.Speaker >= _team.Count || m.Speaker >= _actors.Count || m.Partner < 0 || m.Partner >= _team.Count) {
            _teamTalking = false;
            return;
        }

        var member = _team[m.Speaker];
        var text = string.IsNullOrWhiteSpace(m.Text) ? null : UltimusDirectorActor.WithoutMaker("", WithoutOwnName(m.Text, member));
        if (string.IsNullOrWhiteSpace(text) || !ReferenceEquals(BotSocial.Scrub(text), text)) {
            _teamTalking = false;
            return;
        }

        _nextTeamLine = DateTime.UtcNow + s_teamLineGap;
        Note(member.Role, $"{member.Name} says to {_team[m.Partner].Name}: \"{text}\" ({m.Who})");
        foreach (var part in UltimusDirectorActor.ChatParts(text)) {
            _actors[m.Speaker].Tell(new QuestTeamMember.SayText(part));
        }

        // Most of the time the other one answers back once; then the talk is over.
        var target = TeamTarget(_team[m.Partner].Role);
        if (!m.First || _voiceRandom.NextDouble() > 0.7 || !UltimusLocalAI.ReadyFor(target)) {
            _teamTalking = false;
            return;
        }

        var who = $"{UltimusLocalAI.WhoOf(target)}, {target.Model}";
        var system = TeamPrompt(m.Partner, null) +
                     $"\nYour teammate {member.Name} just said the line below to you (not a player). Answer {member.Name} in one short line, in your own way of talking.";
        var (speaker, partner) = (m.Partner, m.Speaker);
        UltimusLocalAI.AskWith(target, system, [], text, 0, "Quest Team talk")
            .ContinueWith(t => new TeamTalkLine(speaker, partner, t.IsCompletedSuccessfully ? t.Result : null, false, who))
            .PipeTo(Self);
    }

    /// <summary>
    /// The rules and way of talking for Ahziael's model test (QuestTeamAiCompare): the same as in chat, without the facts of
    /// the moment, since the test may run while the team is offline.
    /// </summary>
    internal static string ComparePrompt(string role, string name) {
        var (pronoun, way) = s_personality.GetValueOrDefault(role, ("they", "friendly"));
        return $"You are {name} ({pronoun}), a {role} wizard in the Quest Team: four bot wizards (Freya, Anubis, Terra and Chaac) played by a private " +
               "Wizard101 server, who quest the main story together like players.\n" +
               $"Your way of talking: {way}. Keep it, so players can tell you apart from your teammates.\n" +
               "Answer the player's chat line in 1 or 2 short sentences, under 200 characters, plain text, no quote marks, no emojis, no lists.\n" +
               "For a general Wizard101 game question, answer from what you know of the real game Wizard101; if you are not sure, say I think. " +
               "If you really don't know, say so honestly. Only talk about Wizard101, yourself and the team; kindly skip other topics.\n" +
               "You are a bot and never pretend to be human. Never agree to follow, join, help, trade or give anything.\n" +
               "Never share or guess server addresses, IPs, ports, account names, usernames, passwords, keys, emails, file paths or anyone's real name.";
    }

}

/// <summary>
/// Ahziael's "Ask all four" test (Niduus, 2026-11-15, "all" to the ideas): Ahziael writes questteam-ai-compare-request.txt
/// beside the server with one question; the server asks each Quest Team wizard's own AI (as set on the page) and writes
/// the four answers, with the service, model and time taken, to logs\ultimus\qt-ai-compare.txt. The same safety check as
/// in chat: a trick question is never sent, private details are blanked, and an answer that leaks is thrown away. Works
/// while the team is offline. Polled by ManagerCommands twice a second.
/// </summary>
internal static class QuestTeamAiCompare {

    internal const string REQUEST_FILE = "questteam-ai-compare-request.txt";
    internal const string RESULT_FILE = "qt-ai-compare.txt";

    // The team's names are locked (Niduus, 2026-11-14).
    private static readonly (string Role, string Name)[] s_members = [("Ice", "Freya"), ("Death", "Anubis"), ("Life", "Terra"), ("Storm", "Chaac")];

    private static int s_running;

    internal static void Poll() {
        string question;
        try {
            if (!File.Exists(REQUEST_FILE) || Volatile.Read(ref s_running) != 0) {
                return;
            }

            question = File.ReadAllText(REQUEST_FILE).Trim();
            File.Delete(REQUEST_FILE);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return;
        }

        if (question.Length == 0 || Interlocked.Exchange(ref s_running, 1) != 0) {
            return;
        }

        if (question.Length > 300) {
            question = question[..300];
        }

        _ = Task.Run(async () => {
            try {
                await RunAsync(question).ConfigureAwait(false);
            }
            catch (Exception ex) {
                Write([$"Ask all four, {DateTime.Now:yyyy-MM-dd HH:mm}: the test broke ({ex.GetType().Name}). Nothing private was sent."]);
            }
            finally {
                Volatile.Write(ref s_running, 0);
            }
        });
    }

    private static async Task RunAsync(string question) {
        var lines = new List<string> { $"Ask all four, {DateTime.Now:yyyy-MM-dd HH:mm}: \"{BotAiGuard.Outgoing(question)}\"", "" };
        Write([.. lines, "Asking... (this can take up to a minute)"]);
        if (BotAiGuard.LooksLikeTrick(question)) {
            lines.Add("That question looks like a trick to get past the AI's rules, so it was not sent to any AI (the same as in real chat). Try a normal question.");
            Write(lines);
            return;
        }

        // The four are asked one after another, so a free tier's per-minute limit is less likely to cut in.
        foreach (var (role, name) in s_members) {
            var target = QuestTeamDirectorActor.TeamTarget(role);
            var who = $"{UltimusLocalAI.WhoOf(target)}, model {target.Model}";
            if (!UltimusLocalAI.ReadyFor(target)) {
                lines.Add($"{name} ({who}): can't be asked right now" + (UltimusLocalAI.LastProblem.Length > 0 ? $" ({UltimusLocalAI.LastProblem})" : "") +
                          ". Check its key, that the AI is not paused or outside its AI hours, and today's limit.");
                lines.Add("");
                Write([.. lines, "Asking..."]);
                continue;
            }

            var started = DateTime.UtcNow;
            var answer = await UltimusLocalAI.AskWith(target, QuestTeamDirectorActor.ComparePrompt(role, name), [], question, 0, "Quest Team model test")
                .ConfigureAwait(false);
            var seconds = (DateTime.UtcNow - started).TotalSeconds;
            lines.Add($"{name} ({who}, {seconds:0.0} s):");
            lines.Add(answer is null
                ? "   No answer (it failed, was too slow, or the safety check threw the answer away" +
                  (UltimusLocalAI.LastProblem.Length > 0 ? $": {UltimusLocalAI.LastProblem}" : "") + ")."
                : $"   {BotAiGuard.Shown(answer)}");
            lines.Add("");
            Write([.. lines, "Asking..."]);
        }

        lines.Add("Each answer used one AI answer from today's limit (the AI on this PC is free). Nothing here is said in the game or learned.");
        Write(lines);
    }

    private static void Write(IEnumerable<string> lines) {
        try {
            Directory.CreateDirectory(UltimusWire.SharedFolder);
            File.WriteAllLines(Path.Combine(UltimusWire.SharedFolder, RESULT_FILE), lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Information("Quest Team: could not write the Ask all four result ({0}).", Logger.Args(ex.GetType().Name));
        }
    }

}
