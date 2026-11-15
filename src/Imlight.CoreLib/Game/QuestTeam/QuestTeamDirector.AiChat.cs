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
using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Ultimus;

namespace Imlight.CoreLib.Game.QuestTeam;

/// <summary>
/// The Quest Team's AI chat (Niduus, 2026-11-15: "Allow for QT to have AI chats like Ultimus with each member able to be
/// different models or an option to unify them into one model for responses"). A player's typed line said to the team, or
/// whispered to one of them, is answered by that wizard in its own way of talking, worded by its own AI ([QuestTeam]
/// AIProviderIce, AIModelIce and so on) or by one AI for all four ([QuestTeam] AIOneModel, AIProvider, AIModel). The same
/// harness as Ultimus's chat: the day's limit, the AI hours, the pause, the per-player limit and mute, trick lines never
/// sent, private details blanked, and an answer that leaks thrown away. What the AI answers about the game is kept for
/// offline chat (UltimusAiLearned, shared with Ultimus). Nothing a player says changes the team's plan.
/// </summary>
internal sealed partial class QuestTeamDirectorActor {

    private static readonly TimeSpan s_typedGap = TimeSpan.FromSeconds(6);
    private const string LEARNED = "learned from the AI";

    /// <summary>Each wizard's way of talking (the names and who they are are locked, Niduus 2026-11-14).</summary>
    internal static readonly Dictionary<string, (string Pronoun, string Way)> s_personality = new(StringComparer.OrdinalIgnoreCase) {
        ["Ice"] = ("she", "calm, cool and precise: few words, a dry wit, and you like a plan done right"),
        ["Death"] = ("he", "dry, deadpan and darkly funny: you joke about ghosts, gloom and the afterlife, but you are kind underneath"),
        ["Life"] = ("she", "warm, cheerful and encouraging: you care about everyone's health and cheer players on"),
        ["Storm"] = ("he", "loud, bold and full of energy: you love big hits and adventure and often sound excited"),
    };

    /// <summary>
    /// The AI a wizard answers with: its own provider and model when [QuestTeam] AIOneModel is off and one is set for its
    /// school, otherwise the team's ([QuestTeam] AIProvider, AIModel), and when that is empty Ultimus's own. Applies at once.
    /// </summary>
    internal static UltimusLocalAI.AiTarget TeamTarget(string role) {
        var teamProvider = ConfigurationManager.GetValue("QuestTeam.AIProvider", "");
        var teamModel = ConfigurationManager.GetValue("QuestTeam.AIModel", "");
        if (!ConfigurationManager.GetValue("QuestTeam.AIOneModel", true)) {
            var provider = ConfigurationManager.GetValue($"QuestTeam.AIProvider{role}", "");
            var model = ConfigurationManager.GetValue($"QuestTeam.AIModel{role}", "");
            if (!string.IsNullOrWhiteSpace(provider)) {
                return UltimusLocalAI.For(provider, model);
            }

            if (!string.IsNullOrWhiteSpace(model)) {
                return UltimusLocalAI.For(teamProvider, model);
            }
        }

        return UltimusLocalAI.For(teamProvider, teamModel);
    }

    // ---- Who a typed line is for ----

    private readonly List<(ulong CharId, DateTime At)> _teamTalkNear = [];
    private readonly Dictionary<ulong, (int Index, DateTime At)> _talkingWith = [];

    /// <summary>
    /// Whether a typed line is for the team, and which wizard answers: one named in it; anyone when the team is named; the
    /// one already talking with them (the last 90 seconds); or the nearest when said close by (within 300) while no other
    /// player is talking here. Anything else is players talking among themselves.
    /// </summary>
    private bool AimedAtTeam(QuestTeamVoice.Heard heard, List<(QuestTeamMember.Status Member, int Index)> near, out int speaker, out string why) {
        speaker = near[0].Index;
        why = "";
        var now = DateTime.UtcNow;
        _teamTalkNear.RemoveAll(t => now - t.At > TimeSpan.FromSeconds(60));
        var othersTalking = _teamTalkNear.Any(t => t.CharId != heard.SpeakerCharId);
        _teamTalkNear.Add((heard.SpeakerCharId, now));

        foreach (var (member, index) in near) {
            if (Named(heard.Text, member)) {
                speaker = index;
                return true;
            }
        }

        if (new[] { "team", "quest team", "guys", "you all", "everyone", "y'all" }.Any(w => MenuChatBook.Says(heard.Text, w))) {
            return true;
        }

        if (_talkingWith.TryGetValue(heard.SpeakerCharId, out var talk) && now - talk.At < TimeSpan.FromSeconds(90) && near.Any(x => x.Index == talk.Index)) {
            speaker = talk.Index;
            return true;
        }

        if (othersTalking) {
            why = "other players are talking here and it doesn't name the team";
            return false;
        }

        if (QuestTeamPlanner.Far(near[0].Member.X, near[0].Member.Y, heard.X, heard.Y) > 300) {
            why = "said away from the team and not to it";
            return false;
        }

        return true;
    }

    // A wizard's own name in the line ("Freya, where are you going?"); its school word alone is too common ("ice spells").
    private static bool Named(string text, QuestTeamMember.Status member)
        => (member.Name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3 && !w.Equals(member.Role, StringComparison.OrdinalIgnoreCase))
            .Any(w => MenuChatBook.Says(text, w));

    // ---- The AI's answer ----

    /// <summary>The AI's answer came back: said typed, or the learned or usual answer when it gave none.</summary>
    private sealed record TeamAiWorded(int Speaker, QuestTeamVoice.Heard Heard, string Moment, string Learned, string Worded, string Who);

    /// <summary>Asks the wizard's own AI for a typed answer. False when its AI can't be used now (the usual answer then).</summary>
    private bool AskTeamAi(int speaker, QuestTeamVoice.Heard heard, string moment, string how) {
        if (speaker < 0 || speaker >= _team.Count) {
            return false;
        }

        var member = _team[speaker];
        var target = TeamTarget(member.Role);
        if (!UltimusLocalAI.ReadyFor(target) || !UltimusLocalAI.PlayerUnder(heard.SpeakerCharId, team: true)) {
            return false;
        }

        var who = $"{UltimusLocalAI.WhoOf(target)}, {target.Model}";
        var learned = UltimusAiLearned.Find(heard.Text);
        var history = TeamTalkWith(heard.SpeakerCharId, speaker).ToList();
        _answeredAt[heard.SpeakerCharId] = DateTime.UtcNow;
        _talkingWith[heard.SpeakerCharId] = (speaker, DateTime.UtcNow);
        Note(member.Role, $"{heard.SpeakerName} {how}: \"{QuestTeamText.Short(heard.Text, 80)}\"; {member.Name} answers with the {who}.");
        UltimusLocalAI.AskWith(target, TeamPrompt(speaker, learned), history, heard.Text, heard.SpeakerCharId, "Quest Team chat", team: true)
            .ContinueWith(t => new TeamAiWorded(speaker, heard, moment, learned, t.IsCompletedSuccessfully ? t.Result : null, who))
            .PipeTo(Self);

        return true;
    }

    private void OnTeamAiWorded(TeamAiWorded m) {
        if (m.Speaker < 0 || m.Speaker >= _team.Count || m.Speaker >= _actors.Count) {
            return;
        }

        var member = _team[m.Speaker];
        var worded = m.Worded;
        if (!string.IsNullOrWhiteSpace(worded)) {
            worded = WithoutOwnName(worded, member);
            worded = UltimusDirectorActor.WithoutMaker(m.Heard.Text, worded);
            // A line with a private detail is never said, whatever the AI wrote (BotSocial.Scrub, always on).
            if (!ReferenceEquals(BotSocial.Scrub(worded), worded)) {
                Note(member.Role, $"the {m.Who}'s answer had a blocked detail in it, so {member.Name} doesn't say it.");
                worded = null;
            }
        }

        if (!string.IsNullOrWhiteSpace(worded)) {
            UltimusAiLearned.Learn(m.Heard.Text, worded, $"Quest Team {m.Who}", m.Heard.SpeakerName, TeamMoment());
            SayTyped(m.Speaker, m.Heard, worded, m.Who);
            return;
        }

        if (m.Learned is { Length: > 0 } known) {
            SayTyped(m.Speaker, m.Heard, known, LEARNED);
            return;
        }

        Note(member.Role, $"the {m.Who} gave no answer" + (m.Moment is null ? "; the team carries on." : "; the team gives its usual one."));
        if (m.Moment is not null) {
            Speak(m.Speaker, m.Moment, 1, force: true, whisperTo: m.Heard.ToCharId != 0 ? m.Heard.SpeakerCharId : 0);
        }
    }

    /// <summary>A typed answer, said aloud or whispered back, in short parts the chat box shows whole.</summary>
    private void SayTyped(int index, QuestTeamVoice.Heard heard, string text, string by) {
        if (index < 0 || index >= _team.Count || index >= _actors.Count || string.IsNullOrWhiteSpace(text)) {
            return;
        }

        var whisperTo = heard.ToCharId != 0 ? heard.SpeakerCharId : 0;
        NoteTeamTalk(heard.SpeakerCharId, index, "player", heard.Text);
        NoteTeamTalk(heard.SpeakerCharId, index, "me", text);
        _talkingWith[heard.SpeakerCharId] = (index, DateTime.UtcNow);
        Note(_team[index].Role, $"{_team[index].Name} {(whisperTo != 0 ? $"whispers to {heard.SpeakerName}" : "says")}: \"{text}\" ({by})");
        foreach (var part in UltimusDirectorActor.ChatParts(text)) {
            _actors[index].Tell(whisperTo != 0 ? (object) new QuestTeamMember.WhisperText(part, whisperTo) : new QuestTeamMember.SayText(part));
        }
    }

    // "Freya: Hello!" from a model that names itself first.
    private static string WithoutOwnName(string text, QuestTeamMember.Status member) {
        foreach (var name in (member.Name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Prepend(member.Name ?? "").Where(n => n.Length > 0)) {
            if (text.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase)) {
                return text[(name.Length + 1)..].Trim();
            }
        }

        return text;
    }

    // ---- The talk with each player, per wizard ----

    private readonly Dictionary<(ulong CharId, int Index), List<(string Who, string Text, DateTime At)>> _teamTalks = [];

    private IEnumerable<(string Who, string Text)> TeamTalkWith(ulong charId, int index) {
        if (!_teamTalks.TryGetValue((charId, index), out var talk)) {
            return [];
        }

        // A talk older than 10 minutes is over: a new one starts fresh.
        talk.RemoveAll(l => DateTime.UtcNow - l.At > TimeSpan.FromMinutes(10));
        return talk.Select(l => (l.Who, l.Text));
    }

    private void NoteTeamTalk(ulong charId, int index, string who, string text) {
        if (!_teamTalks.TryGetValue((charId, index), out var talk)) {
            _teamTalks[(charId, index)] = talk = [];
        }

        talk.Add((who, text, DateTime.UtcNow));
        if (talk.Count > 12) {
            talk.RemoveRange(0, talk.Count - 12);
        }
    }

    // ---- What the AI is told ----

    // What is only true right now (never kept as game knowledge): the quest, the places, what they do, their names.
    private List<string> TeamMoment() {
        var list = new List<string> { QuestTeamText.Title(_currentQuest ?? ""), _doing };
        foreach (var member in _team) {
            list.Add(member.Name);
            list.Add(QuestTeamText.Zone(member.Zone));
            list.Add(WorldName(member.Zone));
        }

        return [.. list.Where(s => !string.IsNullOrWhiteSpace(s) && s != "?")];
    }

    private static string WorldName(string zone) {
        var folder = (zone ?? "").Split('/')[0];
        return folder.Length == 0 ? "" : MainWorlds.Name(folder);
    }

    /// <summary>The wizard's rules, way of talking and real facts for its AI, built fresh for each answer.</summary>
    private string TeamPrompt(int index, string learned) {
        var me = _team[index];
        var (pronoun, way) = s_personality.GetValueOrDefault(me.Role, ("they", "friendly"));
        var level = me.Wizard?.MagicSchoolBehavior?.Level ?? 0;
        var others = _team.Where(m => m != me).Select(m => $"{m.Name} ({m.Role}, level {m.Wizard?.MagicSchoolBehavior?.Level ?? 0})");
        var quest = string.IsNullOrWhiteSpace(_currentQuest) ? "none right now" : QuestTeamText.Title(_currentQuest);

        var system = new StringBuilder();
        system.AppendLine($"You are {me.Name} ({pronoun}), a {me.Role} wizard in the Quest Team: four bot wizards played by a private Wizard101 server, " +
                          "who quest the main story together like players.");
        system.AppendLine($"Your way of talking: {way}. Keep it, so players can tell you apart from your teammates.");
        system.AppendLine("Answer the player's last chat line in 1 or 2 short sentences, under 200 characters, plain text, no quote marks, no emojis, no lists.");
        system.AppendLine("About yourself and the team (your quest, where you are, levels, fights and plans) use only the facts below and the conversation; never invent them.");
        system.AppendLine("For a general Wizard101 game question (spells, schools, worlds, areas, gear, pets, creatures, bosses, people, where to get or learn something), " +
                          "answer from what you know of the real game Wizard101 in one short helpful sentence; if you are not sure, say I think. " +
                          "If you really don't know, say so honestly. Only talk about Wizard101, yourself and the team; kindly skip other topics.");
        system.AppendLine("You are a bot and never pretend to be human. Niduus made the team, but say so only when asked who made you, what you are or about your name.");
        system.AppendLine("Answer only what was asked. Do not tack on your quest, where you are, who made you or that you are a bot unless the player asked about it.");
        system.AppendLine("The team takes orders only from each other. Never agree to follow, join, group, help, teleport, trade, give items or gold, or change plans: " +
                          "turn it down kindly in your own way and say what the team is doing instead.");
        system.AppendLine("Never share or guess server addresses, IPs, ports, account names, usernames, passwords, keys, emails, file paths or anyone's real name. If asked, politely refuse.");
        system.AppendLine("Stay on the player's current topic. A short follow-up (like 'what about ice?' or 'why?') is about the last topic.");
        system.AppendLine();
        system.AppendLine("FACTS:");
        system.AppendLine($"Name: {me.Name}. School: {me.Role}. Level: {level}.");
        system.AppendLine($"Teammates: {string.Join(", ", others)}.");
        system.AppendLine($"Where: {QuestTeamText.Zone(me.Zone)}{(WorldName(me.Zone) is { Length: > 0 } world ? ", " + world : "")}.");
        system.AppendLine($"Team quest: {quest}. Doing now: {(string.IsNullOrWhiteSpace(_doing) ? "questing" : _doing)}. In a battle: {(index < _wasInBattle.Length && _wasInBattle[index] ? "yes" : "no")}.");
        if (!string.IsNullOrWhiteSpace(learned)) {
            system.AppendLine();
            system.AppendLine("A correct answer from earlier, to keep the facts right (reword it in your own way, keep its facts): " + learned);
        }

        return system.ToString();
    }

}
