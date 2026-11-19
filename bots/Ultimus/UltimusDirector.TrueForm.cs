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
using System.IO;
using System.Linq;
using Akka.Actor;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Groups;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// True Form's seat (TrueForm.cs): he never quests. Alone he stands at his spot in The Commons, by the carousel; any player
/// may call him (".trueform", "True Form, join me"), and in a group he goes along with its head as Ultimus does in party
/// mode (UltimusDirector.Party.cs): their battles, their sigils, to them in another area. When the group ends, or he is
/// told to leave, he goes back to his spot (Recall to the mark he keeps there, else the Commons button) and waits.
/// </summary>
internal sealed partial class UltimusDirectorActor {

    private const string TRUE_FORM_KEY = "trueform|";
    private const string MARKED_FILE = "marked.txt";

    private (ulong CharId, string Name, DateTime At) _tfInvited;
    private bool _tfLeaderGiven;
    private DateTime _tfHomeTryAt = DateTime.MinValue;
    private int _tfRecallTries;
    private string _tfLastWith = "";

    // His slim tick: talk, pause, battle, cutscene, sigil, then where he should be. None of the questing parts.
    private void TrueFormTick() {
        var me = _team[0];
        if (TrueFormSwitch(me)) {
            return;
        }

        AnswerPlayers();
        // Calls and "leave" are answered at once, in a battle too (a call waited for the battle's end and often ran out).
        TrueFormCalls(me);
        UltimusNow.Note(_doing, _lastReasoning, null);

        var paused = File.Exists(Path.Combine(UltimusWire.Folder, UltimusWire.PAUSE_FILE));
        if (paused != _paused) {
            _paused = paused;
            Note("Ultimus", paused ? "True Form is paused in Ahziael: he stands still." : "True Form is resumed in Ahziael.");
        }

        if (_paused) {
            _doing = "paused (Ahziael's Pause button)";
            _lastProgress = DateTime.UtcNow;
            StopWalking(0);
            return;
        }

        if (HandleBattle()) {
            return;
        }

        if (WatchingCutscene()) {
            return;
        }

        if (Services.ZoneService.IsCountingDown(me.GlobalId)) {
            _doing = "on the sigil with my group, waiting for the countdown";
            _lastProgress = DateTime.UtcNow;
            return;
        }

        TrueFormLead(me);
    }

    private void TrueFormLead(UltimusMember.Status me) {
        _lastProgress = DateTime.UtcNow;
        if (ButtonMovePending(me)) {
            StopWalking(0);
            return;
        }

        var target = PartyTarget(me) ?? TrueFormHome(me);
        var with = _partyMate != 0 ? _partyMateName : "";
        if (with != _tfLastWith) {
            _tfLastWith = with;
            TrueForm.With = with;
        }

        Idle(target is null || target.Kind == UltimusPlanner.WAIT || target.Key?.StartsWith(PARTY_KEY + "with|", StringComparison.Ordinal) == true
             || target.Key == TRUE_FORM_KEY + "wait");
        if (target is null) {
            StopWalking(0);
            return;
        }

        if (!target.Same(_target)) {
            var sameStep = _target is not null && _target.Kind == target.Kind && _target.Key == target.Key;
            _target = target;
            if (!sameStep) {
                _targetSince = DateTime.UtcNow;
                Note("Ultimus", $"True Form: {target.What}.");
            }
        }

        _doing = target.What;
        if (target.Kind == UltimusPlanner.WAIT) {
            StopWalking(0);
            return;
        }

        var reach = target.Kind == UltimusPlanner.FIGHT ? 40f : 25f;
        if (UltimusPlanner.Far(me.X, me.Y, target.X, target.Y) > reach) {
            WalkTo(0, target);
        }
        else if (target.Kind == UltimusPlanner.FIGHT) {
            WalkTo(0, target with { X = target.X + 1 });
        }
        else {
            StopWalking(0);
        }
    }

    // Alone: back to his spot and wait there.
    private UltimusPlanner.Target TrueFormHome(UltimusMember.Status me) {
        var home = TrueForm.Home;
        if (!OverlayQuestTracker.SameZone(me.Zone ?? "", home.Zone)) {
            TrueFormGoHome(me, home);
            return new UltimusPlanner.Target(UltimusPlanner.WAIT, me.X, me.Y, "going back to my spot in The Commons", Key: TRUE_FORM_KEY + "away");
        }

        _tfRecallTries = 0;
        if (UltimusPlanner.Far(me.X, me.Y, home.X, home.Y) > 60) {
            return new UltimusPlanner.Target(UltimusPlanner.SPOT, home.X, home.Y, "walking back to my spot by the carousel", Key: TRUE_FORM_KEY + "home",
                Z: home.Z);
        }

        // On his spot: his mark is kept here, so Recall brings him back from anywhere.
        var spot = $"{home.Zone} {home.X:0} {home.Y:0}";
        var marked = Path.Combine(_seat.Folder, MARKED_FILE);
        try {
            if (!File.Exists(marked) || File.ReadAllText(marked).Trim() != spot) {
                _actors[0].Tell(new UltimusMember.MarkSpot(), Self);
                Directory.CreateDirectory(_seat.Folder);
                File.WriteAllText(marked, spot);
                Note("Ultimus", "True Form marks his spot by the carousel, to Recall back to it after a group.");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            // Only the Recall home is lost; the Commons button still works.
        }

        return new UltimusPlanner.Target(UltimusPlanner.WAIT, me.X, me.Y, "waiting at my spot in The Commons for anyone who calls me",
            Key: TRUE_FORM_KEY + "wait");
    }

    // Out of The Commons with nobody to help: Recall to his mark (twice at most), else the Commons button.
    private void TrueFormGoHome(UltimusMember.Status me, TrueForm.Spot home) {
        if (CombatDuelComponent.IsInBattle(me.GlobalId) || DateTime.UtcNow - _tfHomeTryAt < TimeSpan.FromSeconds(25)) {
            return;
        }

        _tfHomeTryAt = DateTime.UtcNow;
        StopWalking(0);
        var marked = Path.Combine(_seat.Folder, MARKED_FILE);
        if (_tfRecallTries < 2 && File.Exists(marked)) {
            _tfRecallTries++;
            _recallSentAt = DateTime.UtcNow;
            _buttonFromZone = me.Zone;
            Note("Ultimus", $"True Form recalls to his spot in The Commons (from {UltimusText.Zone(me.Zone)}).");
            _actors[0].Tell(new UltimusMember.RecallSpot(), Self);
            return;
        }

        _hubPressedAt = DateTime.UtcNow;
        _buttonFromZone = me.Zone;
        Note("Ultimus", $"True Form presses the Commons button to get back to his spot (from {UltimusText.Zone(me.Zone)}).");
        _actors[0].Tell(new UltimusMember.PressCommons(), Self);
    }

    // ---------------------------------------------------------------------
    // Switching school (Niduus, 2026-11-19: "Go ice for me", "Go ice and tank for me")
    // ---------------------------------------------------------------------

    private DateTime _tfRejoinAskedAt = DateTime.MinValue;

    /// <summary>
    /// A school switch: out of battle, he logs out, is made ready as that school (its best gear, jewels, pet and deck, his
    /// stitched look the same; each school's pieces kept in his backpack for next time), logs back in and goes back to the
    /// group's head. True while he is logging out for it.
    /// </summary>
    private bool TrueFormSwitch(UltimusMember.Status me) {
        if (TrueForm.Rejoining is { } back) {
            GroupRegistry.TryGetGroup(me.CharId, out var group);
            if (back.HeadId == 0 || (group is not null && group.Members.Any(m => m.CharId == back.HeadId))) {
                TrueForm.Rejoining = null;
            }
            else if (DateTime.UtcNow - TrueForm.RejoiningSince > TimeSpan.FromMinutes(3)) {
                // Never let back in: the group is over, so back to Myth.
                TrueForm.Rejoining = null;
                Note("Ultimus", $"True Form: {back.HeadName} did not take him back after his switch to {back.School}; back to Myth.");
                if (TrueForm.School != Shared.Behaviors.MagicSchool.Myth) {
                    TrueForm.ResetMode();
                    TrueForm.ClearRequested();
                    TrueForm.AskSwap(Shared.Behaviors.MagicSchool.Myth, null, 0, "");
                }
            }
            else if (me.Wizard is not null && group is null && DateTime.UtcNow - _tfRejoinAskedAt > TimeSpan.FromSeconds(30)
                     && PlayerCheats.LiveWizard(back.HeadId) is not null) {
                _tfRejoinAskedAt = DateTime.UtcNow;
                TrueForm.Call(back.HeadId, back.HeadName);
            }
        }

        if (me.Wizard is null || me.Wizard.IsInDuel || TrueForm.TakeSwap() is not { } swap) {
            return false;
        }

        TrueForm.Become(swap.School, swap.Role);
        if (swap.HeadId != 0) {
            TrueForm.Rejoining = swap;
            TrueForm.RejoiningSince = DateTime.UtcNow;
            _tfRejoinAskedAt = DateTime.MinValue;
        }

        Note("Ultimus", swap.HeadId != 0
            ? $"True Form switches to {swap.School} for {swap.HeadName}: logs out to change, then back to the group."
            : $"True Form goes back to {swap.School}: logs out to change.");
        _doing = $"switching to {swap.School}";
        foreach (var actor in _actors) {
            actor.Tell(PoisonPill.Instance);
        }

        Timers.Cancel("tick");
        // A moment for the logout to be saved before he is changed and logged in again (StartTeam makes him ready).
        Context.System.Scheduler.ScheduleTellOnce(TimeSpan.FromSeconds(5), Self, new TrueFormRelog(), Self);
        return true;
    }

    private sealed record TrueFormRelog;

    private void OnTrueFormRelog() => StartTeam(restart: false);

    // Who called him, his leaving when told, and the lead handed to whoever he joined.
    private void TrueFormCalls(UltimusMember.Status me) {
        if (TrueForm.TakeLeave() && _partyMate != 0) {
            LeaveParty("he was told to leave", "Okay, I'm heading back to my spot in The Commons. Call me any time!");
        }

        GroupRegistry.TryGetGroup(me.CharId, out var group);
        // He made the group for whoever called him: the lead is theirs, so his spells match them.
        if (group is not null && !_tfLeaderGiven && _tfInvited.CharId != 0 && group.LeaderCharId == me.CharId
            && group.Members.Any(m => m.CharId == _tfInvited.CharId)) {
            _tfLeaderGiven = true;
            GroupDirectory.Instance?.Tell(new GROUP_109_PROTOCOL.MSG_CHANGELEADER { RequesterCharId = me.CharId, NewLeaderCharId = _tfInvited.CharId }, Self);
            Note("Ultimus", $"True Form gives the group's lead to {_tfInvited.Name}, who called him.");
        }

        if (group is null) {
            _tfLeaderGiven = false;
            if (_tfInvited.CharId != 0 && DateTime.UtcNow - _tfInvited.At > TimeSpan.FromMinutes(2)) {
                _tfInvited = default;
            }
        }

        if (TrueForm.TakeCall() is not { } call) {
            return;
        }

        var name = call.Name.Length > 0 ? call.Name : "there";
        if (group is not null && group.Members.Any(m => m.CharId == call.CharId)) {
            SayLater(call.CharId, true, $"I'm already in your group, {name}! Tell me \"attack\", \"defend\", \"support\" or \"auto\" to change how I play.", 0.5);
            return;
        }

        if (group is not null && group.Members.Any(m => m.CharId != me.CharId && TrueForm.IsPlayer(m.CharId))) {
            SayLater(call.CharId, true, $"Sorry {name}, I'm helping {(_partyMateName.Length > 0 ? _partyMateName : "another group")} right now. " +
                                        "There's only one of me! Ask again when they're done.", 0.5);
            return;
        }

        if (PlayerCheats.LiveWizard(call.CharId) is null) {
            return;
        }

        // They are in a group already: he can't be put in it by his own invite, so he asks for theirs.
        if (GroupRegistry.TryGetGroup(call.CharId, out _)) {
            SayLater(call.CharId, true, $"Send me a group invite, {name}, and I'll join you right away.", 0.5);
            return;
        }

        _tfInvited = (call.CharId, name, DateTime.UtcNow);
        _tfLeaderGiven = false;
        Note("Ultimus", $"True Form was called by {name}: sends them a group invite.");
        _actors[0].Tell(new UltimusMember.Invite(call.CharId, Wizard.GetGameObjectId(call.CharId)), Self);
        SayLater(call.CharId, true, $"On my way, {name}! Accept my group invite and I'll come to you. You'll lead, and I match your level.", 0.5);
    }

    // "True Form, join me", "leave", "attack", "use Dragonblade": his own asks, said to him. True when it was one.
    private bool TrueFormAsk(UltimusVoice.Heard heard, UltimusMember.Status me) {
        // His name, "hey", "please", "can you" are taken off first, so "True Form, can you please heal us?" reads "heal us"
        // (Niduus, 2026-11-19: "Bots need better obedience to commands and to better identify them and their meaning").
        var order = TrueForm.Order(heard.Text);
        var grouped = GroupRegistry.AreGrouped(me.CharId, heard.SpeakerCharId);
        bool Has(params string[] words) => words.Any(w => MenuChatBook.Says(order, w));
        bool Starts(params string[] words) => words.Any(w => order == w || order.StartsWith(w + " ", StringComparison.Ordinal));
        if (order.Length == 0) {
            return false;
        }

        if (!grouped) {
            if (Has("join me", "join us", "join", "come with me", "group with me", "group up", "help me", "help us", "party with me",
                    "come here", "i need you", "invite me", "team up", "follow me", "come help")) {
                TrueForm.Call(heard.SpeakerCharId, heard.SpeakerName);
                Note("Ultimus", $"True Form: {heard.SpeakerName} calls him (\"{UltimusText.Short(heard.Text, 60)}\").");
                return true;
            }

            return false;
        }

        // Questions about him: what he plays, which spells he keeps, where his spot is.
        if (Has("what mode", "which mode", "your mode", "how are you playing", "what are you playing", "what do you play", "what's your role",
                "whats your role", "your role", "status")) {
            var kept = TrueForm.Requested.Count == 0 ? "" : " I keep " + string.Join(", ", TrueForm.Requested.Select(id => TrueFormPool.Of(id)?.Shown ?? id.ToString())) + " in my hand.";
            SendReply(heard, $"I play {TrueForm.Words(TrueForm.Now)}, with spells of level {TrueForm.SpellLevel()}.{kept}", "true form");
            return true;
        }

        // Said alone ("leave", "bye") or as a whole send-off ("go home", "you can leave"); "go back to the door" is not one.
        if (order is "leave" or "go back" or "bye" or "goodbye" or "dismissed" or "done" or "leave us"
            || Starts("go home", "you can go", "leave the group", "leave group", "go away", "you can leave", "thats all", "that's all", "we're done", "were done")) {
            TrueForm.AskLeave();
            Note("Ultimus", $"True Form: {heard.SpeakerName} sends him home.");
            SendReply(heard, "Okay, heading back to my spot in The Commons. Call me any time!", "true form");
            return true;
        }

        if (Has("stop using", "forget the spell", "forget spells", "clear spells", "your own cards", "pick your own", "use whatever", "any spell")) {
            TrueForm.ClearRequested();
            SendReply(heard, "Okay, I pick all my cards myself again.", "true form");
            return true;
        }

        // "use Dragonblade", "cast Tower Shield", "keep Feint in your hand", "bring Mythspear".
        var spellAsk = System.Text.RegularExpressions.Regex.Match(order, @"(?:^|\s)(?:use|cast|keep|bring|play|hold|save)\s+(?<x>.+)$");
        if (spellAsk.Success) {
            var asked = System.Text.RegularExpressions.Regex.Replace(spellAsk.Groups["x"].Value,
                @"\b(in (your|ur) hand|in hand|on (me|us|them|him|her|the boss|boss|it)|next( round)?|the spell|spell|card|a|an|the|more|again|first|always)\b", " ");
            asked = System.Text.RegularExpressions.Regex.Replace(asked, @"\s+", " ").Trim();
            // "play support" or "use your heals" is a mode, not a spell.
            if (asked.Length > 0 && TrueFormPool.Find(asked, TrueForm.SpellLevel(), TrueForm.DevSpellsNow) is { } spell) {
                TrueForm.Request(spell.Id);
                Note("Ultimus", $"True Form: {heard.SpeakerName} asks for {spell.Shown}.");
                SendReply(heard, $"Got it, I'll keep {spell.Shown} in my hand from the next round.", "true form");
                return true;
            }

            if (TrueForm.ModeFrom(asked) is null && asked.Length > 0) {
                SendReply(heard, TrueFormPool.Ready ? $"I don't have a spell called \"{UltimusText.Short(asked, 40)}\" at level {TrueForm.SpellLevel()}. Say it the way the card says it, like \"use Dragonblade\"."
                    : "Still reading my spells, give me a moment and ask again.", "true form");
                return true;
            }
        }

        // A school ("go ice for me"), with a role ("go ice and tank for me") or the school's own (Niduus, 2026-11-19). A role
        // that does not suit the school is tried, with a warning.
        if (TrueForm.SchoolFrom(order) is { } school) {
            var rest = System.Text.RegularExpressions.Regex.Replace(order, @"\b(fire|ice|storm|myth|life|death|balance)\b", " ");
            var role = TrueForm.ModeFrom(rest) is { } r && r is not (TrueForm.Mode.Auto or TrueForm.Mode.Evil) ? r : (TrueForm.Mode?) null;
            if (school == TrueForm.School && role == TrueForm.Role) {
                SendReply(heard, $"I'm already {school}{(role is { } same ? $", playing {TrueForm.Words(same).Split(':')[0]}" : "")}!", "true form");
                return true;
            }

            if (me.Wizard?.IsInDuel == true) {
                SendReply(heard, $"I'll switch to {school} right after this battle.", "true form");
            }

            TrueForm.AskSwap(school, role, heard.SpeakerCharId, heard.SpeakerName);
            if (role is { } asked) {
                TrueForm.SetMode(asked);
            }
            else {
                TrueForm.ResetMode();
            }

            var plan = role is { } chosen ? $"and play {TrueForm.Words(chosen).Split(':')[0]}" : $"set up as {TrueForm.OwnRole(school)}";
            var warn = TrueForm.Misfit(school, role) is { } misfit ? " Heads up: " + misfit : "";
            Note("Ultimus", $"True Form: {heard.SpeakerName} asks for {school}{(role is null ? "" : " " + role)} (\"{UltimusText.Short(heard.Text, 60)}\").");
            SendReply(heard, $"Switching to {school} {plan}. I'll pop out for a moment to change and be right back. " +
                             "If I'm not back in the group in a minute, send me an invite." + warn, "true form");
            return true;
        }

        // A mode, however it is said: "defend", "go attack", "play support", "be evil", "heal us", "tank for us", "atack".
        if (TrueForm.ModeFrom(order) is { } mode) {
            var words = order.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if (words <= 6 || Has("mode", "play", "go", "be", "switch", "focus", "role", "become", "turn")) {
                TrueForm.SetMode(mode);
                Note("Ultimus", $"True Form: {heard.SpeakerName} asks for {mode} (\"{UltimusText.Short(heard.Text, 60)}\").");
                SendReply(heard, $"Okay, {TrueForm.Words(mode)}.", "true form");
                return true;
            }
        }

        return false;
    }
}
