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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Groups;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// The Calling of 7 (TCO7) as a team (Niduus, 2026-11-16): seven wizards, one per school, each played by Ultimus's whole
/// brain (UltimusSeat). What only they do on top of him is here: they talk to each other when they meet (what each is on,
/// a friendly hello), they team up when two of them are on the same story quest and go on alone otherwise ("I want them to
/// quest either alone or with someone"), and they read everything Ultimus has done (his quest times, his lessons, his
/// stuck spots and how he got out) to do better than him ("Have them able to link to Ultimus, meaning read EVERYTHING he's
/// done thus far and try to preform better").
/// </summary>
internal static class UltimusTco7 {

    /// <summary>[TCO7] Talk: they talk with each other when they meet. On unless turned off; live.</summary>
    internal static bool Talk => ConfigurationManager.GetValue("TCO7.Talk", true);

    /// <summary>[TCO7] TeamUp: two of them on the same story quest may team up. On unless turned off; live.</summary>
    internal static bool TeamUp => ConfigurationManager.GetValue("TCO7.TeamUp", true);

    /// <summary>What one member says to another: the words said in the game, and what they mean (so neither guesses).</summary>
    internal sealed record Line(UltimusSeat From, ulong FromCharId, string FromName, UltimusSeat To, string Kind, string Text, string Quest, DateTime At);

    internal const string HELLO = "hello", ASK_QUEST = "ask-quest", TELL_QUEST = "tell-quest", TEAM_ASK = "team-ask", TEAM_YES = "team-yes",
        TEAM_NO = "team-no", BYE = "bye";

    private static readonly SeatLocal<ConcurrentQueue<Line>> s_heard = new(() => new ConcurrentQueue<Line>());

    /// <summary>A member said something to another (it is said in the game too, where players see it).</summary>
    internal static void Say(Line line) {
        if (line.To is null || line.To == line.From) {
            return;
        }

        var queue = s_heard.Of(line.To);
        if (queue.Count < 20) {
            queue.Enqueue(line);
        }
    }

    internal static bool TryTake(out Line line) => s_heard.Value.TryDequeue(out line);

    // What each member is on now, and whether it is free to talk or team up, for the others to see.
    private static readonly SeatLocal<string> s_quest = new(() => "");
    private static readonly SeatLocal<bool> s_free = new();

    internal static void Note(string quest, bool free) {
        s_quest.Value = quest ?? "";
        s_free.Value = free;
    }

    internal static string QuestOf(UltimusSeat seat) => s_quest.Of(seat);

    internal static bool FreeOf(UltimusSeat seat) => s_free.Of(seat);

    // ---- The squad (Niduus, 2026-11-19): up to four of them quest together for good ----

    // Set by ".ultimus squad" for this server run; null = [TCO7] Squad from Imlight.ini.
    private static string s_squadOverride;

    internal static void SetSquadOverride(string names) => s_squadOverride = names;

    /// <summary>
    /// [TCO7] Squad: up to four member names, with commas ("Tulkar, Cyndr, Kurrent, Mythis"); empty = no squad. The squad
    /// groups up and stays grouped: the one furthest behind in the story leads and the others come along and help, so they
    /// catch up to the same quest, then quest on together without splitting (also at a quest that branches, like the Three
    /// Streets). The rest quest at their own pace, and Ultimus always alone. Applies at once.
    /// </summary>
    internal static List<UltimusSeat> Squad {
        get {
            var names = s_squadOverride ?? ConfigurationManager.GetValue("TCO7.Squad", "");
            if (string.IsNullOrWhiteSpace(names) || names.Trim().Equals("off", StringComparison.OrdinalIgnoreCase)) {
                return [];
            }

            return [.. names.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
                .Select(n => UltimusSeat.Tco7.FirstOrDefault(s => s.Name.Equals(n.Trim(), StringComparison.OrdinalIgnoreCase)))
                .Where(s => s is not null).Distinct().Take(4)];
        }
    }

    internal static bool InSquad(UltimusSeat seat) => seat is { IsTco7: true } && Squad.Contains(seat);

    // How far each member is in the story (world number x 1000 + story quests done there), -1 unknown.
    private static readonly SeatLocal<int> s_score = new(() => -1);
    private static readonly object s_captainLock = new();
    private static UltimusSeat s_captain;

    internal static void NoteScore(int score) => s_score.Value = score;

    internal static int ScoreOf(UltimusSeat seat) => s_score.Of(seat);

    /// <summary>
    /// The squad's leader: the member furthest behind in the story among those playing, so the others help it catch up.
    /// It changes only when another member is two or more story quests further behind, so the group doesn't keep re-forming.
    /// Null when fewer than two squad members are playing.
    /// </summary>
    internal static UltimusSeat Captain() {
        var playing = Squad.Where(s => UltimusDirector.MembersOf(s) is { Count: > 0 } m && m[0].Wizard is not null && ScoreOf(s) >= 0).ToList();
        lock (s_captainLock) {
            if (playing.Count < 2) {
                s_captain = null;
                return null;
            }

            var lowest = playing.OrderBy(ScoreOf).ThenBy(s => s.Index).First();
            if (s_captain is null || !playing.Contains(s_captain) || ScoreOf(s_captain) - ScoreOf(lowest) >= 2) {
                s_captain = lowest;
            }

            return s_captain;
        }
    }

    // A member that just said yes takes the asker's invitation for a minute (UltimusMember, MSG_PARTYREQUESTJOIN).
    private static readonly SeatLocal<(ulong CharId, DateTime Until)> s_accept = new();

    internal static void ExpectInvite(ulong fromCharId) => s_accept.Value = (fromCharId, DateTime.UtcNow.AddMinutes(1));

    /// <summary>Whether the seat working now said yes to teaming up with this inviter a moment ago.</summary>
    internal static bool MayAcceptInvite(ulong inviterCharId) {
        // The squad's leader's invitation is always taken by a squad member.
        if (InSquad(UltimusSeat.Current) && UltimusSeat.OfCharacter(inviterCharId) is { IsTco7: true } from && InSquad(from) && Captain() == from) {
            return true;
        }

        var (charId, until) = s_accept.Value;
        return charId != 0 && charId == inviterCharId && DateTime.UtcNow < until && UltimusSeat.Current.IsTco7
               && UltimusSeat.OfCharacter(inviterCharId) is { IsTco7: true };
    }

}

internal sealed partial class UltimusDirectorActor {

    // When each other member was last talked with (a hello, a question), and last asked to team up.
    private readonly Dictionary<string, DateTime> _tco7TalkedAt = [];
    private readonly Dictionary<string, DateTime> _tco7AskedAt = [];
    private DateTime _tco7NextLook = DateTime.MinValue;
    private DateTime _tco7SplitSince = DateTime.MaxValue;
    private ulong _tco7LedMate;

    private static readonly TimeSpan s_tco7HelloAgain = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan s_tco7AskAgain = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan s_tco7SplitAfter = TimeSpan.FromMinutes(2);

    /// <summary>The TCO7's own half second: what the others said to him, and a hello or a team-up when one is near.</summary>
    private void Tco7Tick(UltimusMember.Status me) {
        if (!_seat.IsTco7 || me?.Wizard is null) {
            return;
        }

        var inBattle = CombatDuelComponent.IsInBattle(me.GlobalId);
        var grouped = GroupRegistry.TryGetGroup(me.CharId, out _);
        UltimusTco7.Note(_currentQuest ?? _target?.Quest, !inBattle && !_paused && !grouped && _partyMate == 0);
        LinkToUltimus(me);

        while (UltimusTco7.TryTake(out var line)) {
            Tco7Heard(me, line, inBattle, grouped);
        }

        // The squad: grouped for good, the one furthest behind leading (UltimusDirector.Tco7.cs, SquadTick).
        var squad = SquadTick(me, inBattle, grouped);

        if (DateTime.UtcNow < _tco7NextLook || inBattle || _paused || !UltimusTco7.Talk) {
            return;
        }

        _tco7NextLook = DateTime.UtcNow.AddSeconds(5);
        foreach (var other in UltimusSeat.Tco7.Where(s => s != _seat)) {
            if (UltimusDirector.MembersOf(other) is not { Count: > 0 } them || them[0] is not { Wizard: not null } mate
                || mate.ZoneActor is null || !mate.ZoneActor.Equals(me.ZoneActor)
                || UltimusPlanner.Far(me.X, me.Y, mate.X, mate.Y) > UltimusPlaybook.Value("Chat.HearRadius")
                || CombatDuelComponent.IsInBattle(mate.GlobalId)) {
                continue;
            }

            var myQuest = _currentQuest ?? _target?.Quest ?? "";
            // Two on the same story quest: the first of the two (by the TCO7's order) asks the other to team up.
            if (UltimusTco7.TeamUp && !squad && !UltimusTco7.InSquad(other) && !grouped && _partyMate == 0 && _seat.Index < other.Index && myQuest.Length > 0
                && UltimusTco7.FreeOf(other) && mate.Wizard.QuestBehavior?.HasQuest(myQuest) == true
                && (!_tco7AskedAt.TryGetValue(other.Key, out var asked) || DateTime.UtcNow - asked > s_tco7AskAgain)) {
                _tco7AskedAt[other.Key] = DateTime.UtcNow;
                _tco7TalkedAt[other.Key] = DateTime.UtcNow;
                Tco7Say(me, other, UltimusTco7.TEAM_ASK, $"{other.Name}, we're both on {UltimusText.Title(myQuest)}. Want to team up?", myQuest);
                Think($"{other.Name} is right here and on {UltimusText.Title(myQuest)} too. Two of us finish it faster, so I ask to team up.",
                    "tco7-ask|" + other.Key + "|" + myQuest);
                return;
            }

            // Otherwise a hello and what they are on, now and then (the first of the two starts).
            if (_seat.Index < other.Index && (!_tco7TalkedAt.TryGetValue(other.Key, out var talked) || DateTime.UtcNow - talked > s_tco7HelloAgain)) {
                _tco7TalkedAt[other.Key] = DateTime.UtcNow;
                var mine = myQuest.Length > 0 ? $" I'm on {UltimusText.Title(myQuest)}." : "";
                Tco7Say(me, other, UltimusTco7.ASK_QUEST, $"Hey {other.Name}!{mine} What are you working on?", myQuest);
                return;
            }
        }
    }

    /// <summary>A line to another member: said in the game (players see it) and given to that member as what it means.</summary>
    private void Tco7Say(UltimusMember.Status me, UltimusSeat to, string kind, string text, string quest = "") {
        var line = BotShared.BotSocial.Scrub(text);
        if (string.IsNullOrWhiteSpace(line) || _actors.Count == 0) {
            return;
        }

        _actors[0].Tell(new UltimusMember.SayText(line));
        Note(_seat.Name, $"TCO7 TALK to {to.Name}: \"{line}\"");
        UltimusTco7.Say(new UltimusTco7.Line(_seat, me.CharId, _seat.Name, to, kind, line, quest ?? "", DateTime.UtcNow));
    }

    // What another member said to him: answered with his real data, never with a question back (so two of them never talk
    // in circles).
    private void Tco7Heard(UltimusMember.Status me, UltimusTco7.Line line, bool inBattle, bool grouped) {
        if (DateTime.UtcNow - line.At > TimeSpan.FromMinutes(1)) {
            return;
        }

        Note(_seat.Name, $"TCO7 TALK from {line.FromName}: \"{line.Text}\"");
        var myQuest = _currentQuest ?? _target?.Quest ?? "";
        switch (line.Kind) {
            case UltimusTco7.ASK_QUEST: {
                var social = SocialSelf(me);
                var mine = myQuest.Length == 0 ? "Just getting my bearings." :
                    $"I'm on {UltimusText.Title(myQuest)}{(string.IsNullOrWhiteSpace(social.Step) ? "" : ": " + UltimusText.Short(social.Step, 80))}.";
                var same = myQuest.Length > 0 && string.Equals(myQuest, line.Quest, StringComparison.OrdinalIgnoreCase) ? " Same as you!" : "";
                _tco7TalkedAt[line.From.Key] = DateTime.UtcNow;
                SayAfter(me, line.From, UltimusTco7.TELL_QUEST, $"Hi {line.FromName}! {mine}{same}", myQuest, 2.5);
                break;
            }

            case UltimusTco7.TEAM_ASK: {
                string no = null;
                if (UltimusTco7.InSquad(_seat)) {
                    no = "I'm questing with my squad";
                }
                else if (!UltimusTco7.TeamUp) {
                    no = "teaming up is switched off for us right now";
                }
                else if (inBattle) {
                    no = "I'm in a fight";
                }
                else if (grouped || _partyMate != 0) {
                    no = "I'm already in a group";
                }
                else if (_paused) {
                    no = "I'm paused for now";
                }
                else if (me.Wizard?.QuestBehavior?.HasQuest(line.Quest) != true) {
                    no = "I'm not on that quest";
                }

                if (no is null) {
                    UltimusTco7.ExpectInvite(line.FromCharId);
                    Think($"{line.FromName} asked to team up on {UltimusText.Title(line.Quest)}. I'm on it too and free, so yes: I go along with them.",
                        "tco7-yes|" + line.From.Key + "|" + line.Quest);
                    SayAfter(me, line.From, UltimusTco7.TEAM_YES, $"Sure, {line.FromName}! Send me the invite.", line.Quest, 2);
                }
                else {
                    Think($"{line.FromName} asked to team up, but {no}, so I go on alone.", "tco7-no|" + line.From.Key + "|" + line.Quest);
                    SayAfter(me, line.From, UltimusTco7.TEAM_NO, $"Thanks, {line.FromName}, but {no}. Good luck!", line.Quest, 2);
                }

                break;
            }

            case UltimusTco7.TEAM_YES:
                // The one who asked sends the invitation, as a player does from the friends list.
                if (!inBattle && _partyMate == 0 && UltimusDirector.MembersOf(line.From) is { Count: > 0 } them && _actors.Count > 0) {
                    Note(_seat.Name, $"TCO7: invites {line.FromName} into his group for {UltimusText.Title(line.Quest)}.");
                    UltimusJournal.Write(UltimusJournal.PARTY, $"Teamed up with {line.FromName} on {UltimusText.Title(line.Quest)}.",
                        WorldOf(me.Zone), UltimusText.Zone(me.Zone), line.FromName, 1);
                    _actors[0].Tell(new UltimusMember.Invite(them[0].CharId, them[0].GlobalId));
                }

                break;
            default:
                // A told quest, a no, a goodbye: nothing more to say.
                break;
        }
    }

    private void SayAfter(UltimusMember.Status me, UltimusSeat to, string kind, string text, string quest, double seconds)
        => Context.System.Scheduler.ScheduleTellOnce(TimeSpan.FromSeconds(seconds), Self, new Tco7Later(to, kind, text, quest), Self);

    internal sealed record Tco7Later(UltimusSeat To, string Kind, string Text, string Quest);

    private void OnTco7Later(Tco7Later later) {
        if (_team.Count > 0) {
            Tco7Say(_team[0], later.To, later.Kind, later.Text, later.Quest);
        }
    }

    /// <summary>
    /// He leads a group of TCO7 members (he asked them): his own story goes on and they come along. When none of them is on
    /// his quest any more for a while, the group has done its job: he thanks them and leaves it.
    /// </summary>
    private UltimusPlanner.Target Tco7Leading(UltimusMember.Status leader, List<GroupMemberSnapshot> mates) {
        var quest = _currentQuest ?? _target?.Quest ?? "";
        var mate = mates.FirstOrDefault(m => m.IsOnline) ?? mates[0];
        var name = PlayerCheats.LiveName(mate.CharId) is { Length: > 0 } n ? n : "my teammate";
        if (_tco7LedMate != mate.CharId) {
            _tco7LedMate = mate.CharId;
            _tco7SplitSince = DateTime.MaxValue;
            Note(_seat.Name, $"TCO7: leads a group with {name}: plays his own story, they come along.");
            Think($"{name} and I are a team now. I lead: my story goes on and they come along with me.", "tco7-lead|" + mate.CharId);
        }

        // A squad never splits: its mates come along on every quest, the leader's story is theirs too (they catch up on it).
        var squadGroup = UltimusTco7.InSquad(_seat) && mates.All(m => UltimusSeat.OfCharacter(m.CharId) is { } s && UltimusTco7.InSquad(s));
        var shared = quest.Length > 0 && mates.Any(m => PlayerCheats.LiveWizard(m.CharId)?.QuestBehavior?.HasQuest(quest) == true);
        if (shared || quest.Length == 0 || squadGroup) {
            _tco7SplitSince = DateTime.MaxValue;
            return null;
        }

        if (_tco7SplitSince == DateTime.MaxValue) {
            _tco7SplitSince = DateTime.UtcNow;
        }

        if (DateTime.UtcNow - _tco7SplitSince > s_tco7SplitAfter || !UltimusTco7.TeamUp) {
            _tco7SplitSince = DateTime.MaxValue;
            _tco7LedMate = 0;
            Note(_seat.Name, $"TCO7: {name} is no longer on {UltimusText.Title(quest)}: our quests split, so he leaves the group.");
            Think($"{name} and I aren't on the same quest any more, so the team has done its job. I thank them and go on alone.", "tco7-split|" + mate.CharId);
            if (_actors.Count > 0) {
                _actors[0].Tell(new UltimusMember.SayText(BotShared.BotSocial.Scrub($"Our quests split here. Thanks for the help, {name}!")));
                _actors[0].Tell(new UltimusMember.LeaveGroup());
            }
        }

        return null;
    }

    // ---------------------------------------------------------------------
    // The squad (Niduus, 2026-11-19: "an option for TCO7 for them to group on and work together exclusively... they will help
    // each other catch up to the point that they're all on the same furthest progressed quest and then work through quests
    // together"; before, a team-up split when their quests split, as at the Three Streets)
    // ---------------------------------------------------------------------

    private DateTime _squadNextLook = DateTime.MinValue;
    private readonly Dictionary<string, DateTime> _squadInvitedAt = [];
    private string _squadSaid = "";

    /// <summary>
    /// The squad's half second for one member: true when this member is in the squad (then the usual two-member team-up is
    /// not used). The leader (the one furthest behind) invites the others into its group; the others leave any other group
    /// and take only the leader's invitation. Once grouped, party mode does the rest: the others go along, join its
    /// battles and do the steps they share, and the leader never leaves them when quests differ.
    /// </summary>
    private bool SquadTick(UltimusMember.Status me, bool inBattle, bool grouped) {
        if (!UltimusTco7.InSquad(_seat)) {
            return false;
        }

        if (DateTime.UtcNow < _squadNextLook || inBattle || _paused || _actors.Count == 0) {
            return true;
        }

        _squadNextLook = DateTime.UtcNow.AddSeconds(10);
        try {
            if (BotShared.BotProgress.Of(me.Wizard, _currentQuest ?? _target?.Quest) is { } count) {
                UltimusTco7.NoteScore(BotShared.MainWorlds.Number(count.World) * 1000 + count.Done);
            }
        }
        catch (Exception e) {
            Logger.Debug("{0}: squad progress could not be read: {1}", Logger.Args(_seat.LogName, e.Message));
        }

        var captain = UltimusTco7.Captain();
        if (captain is null || UltimusDirector.MembersOf(captain) is not { Count: > 0 } lead) {
            return true;
        }

        GroupRegistry.TryGetGroup(me.CharId, out var group);
        if (captain == _seat) {
            if (_squadSaid != "lead") {
                _squadSaid = "lead";
                Note(_seat.Name, "SQUAD: leads the squad (furthest behind in the story): the others come along and help it catch up.");
                Think("I'm the furthest behind of my squad, so I lead: they come along and help me catch up, then we quest on together.", "squad-lead");
            }

            // In someone else's group: it leaves, to lead its own.
            if (grouped && group is not null && group.LeaderCharId != me.CharId) {
                Note(_seat.Name, "SQUAD: leaves its group to lead the squad.");
                _actors[0].Tell(new UltimusMember.LeaveGroup());
                return true;
            }

            var size = group?.Members.Count ?? 1;
            foreach (var mate in UltimusTco7.Squad.Where(s => s != _seat)) {
                if (size >= 4 || UltimusDirector.MembersOf(mate) is not { Count: > 0 } them || them[0] is not { Wizard: not null } other
                    || GroupRegistry.AreGrouped(me.CharId, other.CharId) || CombatDuelComponent.IsInBattle(other.GlobalId)
                    || _squadInvitedAt.TryGetValue(mate.Key, out var at) && DateTime.UtcNow - at < TimeSpan.FromSeconds(30)) {
                    continue;
                }

                _squadInvitedAt[mate.Key] = DateTime.UtcNow;
                Note(_seat.Name, $"SQUAD: invites {mate.Name} into the squad's group.");
                _actors[0].Tell(new UltimusMember.Invite(other.CharId, other.GlobalId));
                size++;
            }

            return true;
        }

        if (_squadSaid != captain.Key) {
            _squadSaid = captain.Key;
            Note(_seat.Name, $"SQUAD: {captain.Name} leads the squad (furthest behind in the story); goes along with them.");
            Think($"{captain.Name} is the furthest behind of my squad, so I go along and help them catch up. Then we quest on together.",
                "squad-follow|" + captain.Key);
        }

        // In a group without the leader, or leading one the leader is in: it leaves, so the leader's invitation can come.
        if (grouped && group is not null && (!GroupRegistry.AreGrouped(me.CharId, lead[0].CharId) || group.LeaderCharId == me.CharId)) {
            Note(_seat.Name, $"SQUAD: leaves its group to join {captain.Name}'s.");
            _actors[0].Tell(new UltimusMember.LeaveGroup());
        }

        return true;
    }

    // ---------------------------------------------------------------------
    // The link to Ultimus: everything he has done, read to do better
    // ---------------------------------------------------------------------

    private const string LINK_FILE = "ultimus-link.txt";
    private DateTime _linkReadAt = DateTime.MinValue;
    private bool _linkTold;

    /// <summary>
    /// What Ultimus has done so far, read every half hour and written to ultimus-link.txt in his own folder (Ahziael shows
    /// it): Ultimus's quest times (the ones to beat; UltimusBrain), his lessons, his stuck spots and how he got out, his
    /// effort. His maps, teleporters, doors, stuck solutions, quest tree and creatures are one shared book (UltimusWire.SharedFolder),
    /// read and added to by every one of them as they go.
    /// </summary>
    private void LinkToUltimus(UltimusMember.Status me) {
        if (DateTime.UtcNow - _linkReadAt < TimeSpan.FromMinutes(30)) {
            return;
        }

        _linkReadAt = DateTime.UtcNow;
        var his = UltimusWire.SharedFolder;
        var text = new StringBuilder();
        text.AppendLine($"{_seat.Name}'s link to Ultimus ({DateTime.Now:yyyy-MM-dd HH:mm})");
        text.AppendLine("Everything Ultimus has done, read so I can do the same or better.");
        text.AppendLine();

        var quests = 0;
        try {
            var times = Path.Combine(his, UltimusWire.QUEST_TIMES_FILE);
            if (File.Exists(times) && JsonDocument.Parse(File.ReadAllText(times)).RootElement is { ValueKind: JsonValueKind.Object } root) {
                quests = root.EnumerateObject().Count();
                var slowest = root.EnumerateObject()
                    .Select(p => (Title: p.Value.TryGetProperty("Title", out var t) ? t.GetString() : p.Name,
                        Seconds: p.Value.TryGetProperty("Seconds", out var s) && s.TryGetDouble(out var d) ? d : 0))
                    .OrderByDescending(p => p.Seconds).Take(5).ToList();
                text.AppendLine($"Quests Ultimus has finished: {quests}. His time on each is the one I try to beat.");
                foreach (var (title, seconds) in slowest) {
                    text.AppendLine($"  slow for him: {title} ({seconds / 60:0} min)");
                }
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) {
            text.AppendLine($"Could not read his quest times: {e.Message}");
        }

        var lessons = ReadLastLines(Path.Combine(his, UltimusWire.LEARNED_FILE), 10);
        text.AppendLine();
        text.AppendLine($"His latest lessons ({lessons.Count}):");
        foreach (var raw in lessons) {
            try {
                using var doc = JsonDocument.Parse(raw);
                var mistake = doc.RootElement.TryGetProperty("mistake", out var m) ? m.GetString() : raw;
                var lesson = doc.RootElement.TryGetProperty("lesson", out var l) ? l.GetString() : "";
                text.AppendLine($"  - {UltimusText.Short(mistake, 140)}{(string.IsNullOrEmpty(lesson) ? "" : " Next time: " + UltimusText.Short(lesson, 120))}");
            }
            catch (JsonException) {
                text.AppendLine($"  - {UltimusText.Short(raw, 160)}");
            }
        }

        var solutions = CountEntries(Path.Combine(his, "stuck-solutions.json"));
        var exits = ReadLastLines(Path.Combine(his, "stubborn-exits.txt"), 1000).Count;
        var maps = Directory.Exists(Path.Combine(his, "maps")) ? Directory.GetFiles(Path.Combine(his, "maps")).Length : 0;
        text.AppendLine();
        text.AppendLine($"Shared with him as I go (one book for all of us): {solutions} stuck solutions, {exits} stubborn exits, {maps} area maps, " +
                        "his teleporters, doors, quest tree and what he knows of each creature.");
        // The watchdog book (BotWatchdogBook): where any bot lost hours, and what got it going again.
        text.AppendLine($"Watchdog book: {BotShared.BotWatchdogBook.Summary()}.");
        var effort = ReadLastLines(Path.Combine(his, "effort.txt"), 12);
        if (effort.Count > 0) {
            text.AppendLine();
            text.AppendLine("His effort ledger (latest):");
            foreach (var line in effort) {
                text.AppendLine("  " + line);
            }
        }

        try {
            Directory.CreateDirectory(UltimusWire.Folder);
            File.WriteAllText(Path.Combine(UltimusWire.Folder, LINK_FILE), text.ToString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            Logger.Debug("{0}: could not write {1}: {2}", Logger.Args(_seat.LogName, LINK_FILE, e.Message));
        }

        if (!_linkTold) {
            _linkTold = true;
            Think($"I read everything Ultimus has done so far: {quests} quests finished, {lessons.Count} recent lessons, {solutions} stuck solutions. " +
                  "His maps and solutions are mine too, and his time on each quest is the one I try to beat.", "tco7-link");
            UltimusJournal.Write("link", $"Read Ultimus's records: {quests} quests, {solutions} stuck solutions, {maps} area maps.", WorldOf(me.Zone),
                UltimusText.Zone(me.Zone), "Ultimus", 1);
        }
    }

    private static List<string> ReadLastLines(string path, int most) {
        try {
            if (!File.Exists(path)) {
                return [];
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new Queue<string>();
            while (reader.ReadLine() is { } line) {
                if (string.IsNullOrWhiteSpace(line)) {
                    continue;
                }

                lines.Enqueue(line);
                if (lines.Count > most) {
                    lines.Dequeue();
                }
            }

            return [.. lines];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            return [];
        }
    }

    private static int CountEntries(string path) {
        try {
            if (!File.Exists(path)) {
                return 0;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            return doc.RootElement.ValueKind switch {
                JsonValueKind.Array => doc.RootElement.GetArrayLength(),
                JsonValueKind.Object => doc.RootElement.EnumerateObject().Count(),
                _ => 0,
            };
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) {
            return 0;
        }
    }

}
