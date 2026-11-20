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
        TrueFormBuildPickTick(me);

        // A deck order: his deck built again, out of battle.
        if (TrueForm.DeckRebuild && me.Wizard is { } deckWizard && !deckWizard.IsInDuel) {
            TrueForm.DeckRebuild = false;
            TrueFormSpells.Give(deckWizard, BotGear.LevelOf(deckWizard), TrueForm.School.ToString(), TrueForm.StyleNow(TrueForm.School));
        }

        // His set bonuses (True Form only), put on again whenever his stats were worked out anew.
        if (me.Wizard is { } setWizard) {
            TrueFormSetBonus.Ensure(setWizard);
        }

        // Banned gear (developers', testers', creature stand-ins) comes off at once, whatever he is doing out of battle
        // (Niduus, 2026-11-20: "HE'S NOT ALLOWED TO HAVE DEV GEAR. NONE OF THE BOTS ARE ALLOWED"): the gear pick skipped
        // it, but a piece already worn stayed on until his next change.
        if (me.Wizard is { IsInDuel: false } stripWizard && DateTime.UtcNow >= _tfNextStrip) {
            _tfNextStrip = DateTime.UtcNow.AddSeconds(5);
            foreach (var banned in BotGear.ForbiddenWorn(stripWizard)) {
                Imlight.Common.Logger.Information("True Form: takes off banned gear {0} ({1}).", Imlight.Common.Logger.Args(banned.Name, banned.Slot));
                Note("Ultimus", $"True Form takes off {banned.Name} ({banned.Slot}): developers' and creature gear is not for bots.");
                _actors[0].Tell(new UltimusMember.Unequip(banned.Item.m_globalID, banned.Slot, banned.Name), Self);
                TrueForm.CardPush = true;
            }
        }

        // After a change, every player's game gets his new character card at once (TrueFormCardPush).
        if (TrueForm.CardPush && me.Wizard is { GameObjectID: not 0 } cardWizard) {
            TrueForm.CardPush = false;
            TrueFormCardPush(cardWizard);
        }

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
                if (TrueForm.School != Shared.Behaviors.MagicSchool.Myth || TrueForm.Special) {
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

        TrueForm.Become(swap.School, swap.Role, swap.HeadId, swap.HeadName, swap.Kind);
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

    private static bool IsQuestion(string order)
        => System.Text.RegularExpressions.Regex.IsMatch(order, @"^(do|does|did|are|is|am|was|were|should|shall|what|whats|what's|how|why|who|when|where|which)\b");

    // His answers about himself, from what he really has. True when it was one.
    private bool TrueFormQuestion(UltimusVoice.Heard heard, UltimusMember.Status me, string order) {
        bool Has(params string[] words) => words.Any(w => System.Text.RegularExpressions.Regex.IsMatch(order, $@"(^|\s){System.Text.RegularExpressions.Regex.Escape(w)}($|\s)"));
        var asking = IsQuestion(order) || Has("show", "tell me", "list", "look at", "check");
        var wizard = me.Wizard;

        // Treasure cards: "what's in your treasure card deck", "what tc's do you have", "look at your treasure cards".
        if (asking && Has("treasure card", "treasure cards", "tc", "tcs", "tc's", "treasure")) {
            var deck = wizard?.EquipmentBehavior?.GetItemInSlot(WizardData.Models.Player.EquipmentSlotType.Deck) is { } d
                       && Shared.Resources.CoreObjectFactory.FindBehaviorInstance<Imcodec.ObjectProperty.TypeCache.DeckBehavior>(d, out var b) ? b.m_spellList ?? [] : [];
            bool Tc(uint id) => Shared.Resources.CoreObjectFactory.GetCoreTemplate(id) is Imcodec.ObjectProperty.TypeCache.SpellTemplate t && t.m_Treasure;
            var inDeck = deck.Where(s => s is not null && Tc(s.m_templateID)).Select(s => TrueFormPool.Of(s.m_templateID)?.Shown ?? UltimusText.SpellName(s.m_templateID)).ToList();
            var book = wizard?.SpellbookBehavior?.TreasureCardTemplateIds?.Count ?? 0;
            SendReply(heard, inDeck.Count == 0 ? $"I have {book} treasure cards in my book, one of each card that only comes as a treasure card. I pick the ones a fight needs each round."
                : $"In my deck's treasure card slots: {string.Join(", ", inDeck.Take(8))}{(inDeck.Count > 8 ? $" and {inDeck.Count - 8} more" : "")}. " +
                  $"{book} more in my book, and each round I pick the cards the fight needs.", "true form");
            return true;
        }

        // His deck or spells: "what is in your deck", "what spells do you have in your combat deck".
        if (asking && Has("deck", "spells", "spell", "cards", "combat deck") && !Has("hand")) {
            var count = TrueFormPool.Ready ? TrueFormBattle.SpellCount() : 0;
            var deck = wizard?.EquipmentBehavior?.GetItemInSlot(WizardData.Models.Player.EquipmentSlotType.Deck) is { } d
                       && Shared.Resources.CoreObjectFactory.FindBehaviorInstance<Imcodec.ObjectProperty.TypeCache.DeckBehavior>(d, out var b) ? b.m_spellList ?? [] : [];
            var names = deck.Where(s => s is not null).Select(s => TrueFormPool.Of(s.m_templateID)?.Shown ?? UltimusText.SpellName(s.m_templateID))
                .Where(n => !string.IsNullOrWhiteSpace(n)).Take(8).ToList();
            SendReply(heard, $"I have every spell in the game, one of each{(count > 0 ? $" ({count} at level {TrueForm.SpellLevel()})" : "")}, and each round I pick the seven the fight needs. " +
                             (names.Count > 0 ? $"My {TrueForm.School} deck starts with {string.Join(", ", names)}. " : "") +
                             "Ask \"what's in your hand\" in a battle, or tell me \"use Dragonblade\" to keep a card in my hand.", "true form");
            return true;
        }

        // His school: "what is your school", "which school are you".
        if (asking && Has("school", "class")) {
            var now = TrueForm.School;
            SendReply(heard, now == Shared.Behaviors.MagicSchool.Myth
                ? "Myth is my own school. In a group you can switch me: \"go ice for me\", or \"go ice and tank for me\"."
                : $"I'm {now} for this group. Myth is my own school, and I go back to it when the group ends.", "true form");
            if (TrueFormBuild.Weave is { } weaving || TrueFormBuild.Pushes.Count > 0) {
                SendReply(heard, (TrueFormBuild.Weave is { } w ? $"I'm weaving {w} too, and my school pips go to {w}. " : "")
                                 + (TrueFormBuild.Pushes.Count > 0 ? $"You asked for more {string.Join(" and ", TrueFormBuild.Pushes.Select(p => p.Words))}." : ""), "true form");
            }

            return true;
        }

        // What he can do: "what all options do you have", "what can you do", "commands".
        if (Has("options", "commands", "what can you do", "what do you do", "how do i use you", "how does this work", "what are you for", "abilities")) {
            SendReply(heard, "Group with me (\"join me\" or an invite) and I follow your lead at your level. Tell me \"attack\", \"defend\", \"support\" or \"auto\" for how I play, " +
                             "\"go ice for me\" or \"go ice and tank for me\" to switch my school, \"go ice weave fire\" to play two schools, \"get more ice accuracy\" to push a stat, \"last build\" for your build from last time, \"use Dragonblade\" to keep a card in my hand, " +
                             "\"blade me\" or \"shield us\" in a battle, and \"leave\" when you're done.", "true form");
            return true;
        }

        // "Do you need healing?", "are you okay?": his health, no mode change.
        if (IsQuestion(order) && Has("heal", "healing", "health", "hurt", "okay", "ok", "alright", "low")) {
            var stats = wizard?.GameStats;
            var max = stats is null ? 1 : Math.Max(1, stats.m_baseHitpoints + stats.m_bonusHitpoints);
            var share = stats is null ? 1.0 : stats.m_currentHitpoints / (double) max;
            SendReply(heard, share >= 0.6 ? $"I'm fine, at {share:P0} health. Thanks!" : $"I'm at {share:P0} health, a heal would help!", "true form");
            return true;
        }

        return false;
    }

    // Who called him, his leaving when told, and the lead handed to whoever he joined.
    private void TrueFormCalls(UltimusMember.Status me) {
        if (TrueForm.TakeLeave() && _partyMate != 0) {
            LeaveParty("he was told to leave", "Okay, I'm heading back to my spot in The Commons. Call me any time!");
        }

        GroupRegistry.TryGetGroup(me.CharId, out var group);
        // A school asked for before grouping: once that player is in his group, he switches.
        if (TrueForm.SwapAfterJoin is { } later) {
            if (group is not null && group.Members.Any(m => m.CharId == later.HeadId)) {
                TrueForm.SwapAfterJoin = null;
                TrueForm.AskSwap(later.School, later.Role, later.HeadId, later.HeadName, later.Kind);
                if (later.Role is { } role) {
                    TrueForm.SetMode(role);
                }

                SayLater(later.HeadId, true, $"Switching to {(later.Kind != TrueForm.Setup.Own ? TrueForm.SetupWords(later.Kind) : later.School.ToString())} now. I'll pop out for a moment to change and be right back.", 0.5);
            }
            else if (DateTime.UtcNow - TrueForm.SwapAfterJoinAt > TimeSpan.FromMinutes(3)) {
                TrueForm.SwapAfterJoin = null;
            }
        }

        // A commander he has had before: offer their last build (Niduus, 2026-11-20: "TF will have memories of these builds for
        // the next time they encounter that player as a commander and ask if they would like the last build").
        if (group is not null && group.LeaderCharId != me.CharId && TrueForm.IsPlayer(group.LeaderCharId) && !me.Wizard?.IsInDuel == true
            && TrueForm.Rejoining is null && TrueFormBuild.LastFor(group.LeaderCharId) is { } last && !TrueFormBuild.IsNow(last)
            && (!_tfOfferedAt.TryGetValue(group.LeaderCharId, out var offered) || DateTime.UtcNow - offered > TimeSpan.FromHours(2))) {
            _tfOfferedAt[group.LeaderCharId] = DateTime.UtcNow;
            TrueFormBuild.Offered = Tuple.Create(group.LeaderCharId, last);
            TrueFormBuild.OfferedAt = DateTime.UtcNow;
            Note("Ultimus", $"True Form offers {group.LeaderCharId} their last build: {last.Words}.");
            SayLater(group.LeaderCharId, true, $"Welcome back! Last time you had me as {last.Words}. Want that build again? Say \"yes\" or \"last build\", or tell me something new.", 2);
        }

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

        // Questions about him, grouped or not: his deck, treasure cards, school, what he can do, whether he needs a heal
        // (Niduus, 2026-11-19: "TF interactions need to be looked at and fixed"; "What is in your deck?" got his fishing
        // cantrips, "What all options do you have?" got Ultimus's questing answer, "Do you need healing?" set him to support).
        if (TrueFormQuestion(heard, me, order)) {
            return true;
        }

        // Weave school, stat asks and the commander's last build (TrueFormBuild). Said with a school or setup ("go ice weave
        // fire"), the rest of the order is read below with the weave taken out.
        if (TrueFormBuildOrder(heard, me, grouped, ref order)) {
            return true;
        }

        // A deck order ("setup your deck to focus on healing. No damage spells", "heals only"): his deck is built again
        // around it, and kept; he plays the matching mode too.
        if (!IsQuestion(order) && TrueForm.DeckFocusFrom(order) is { } focus) {
            TrueForm.SetFocus(focus);
            var focusMode = focus switch {
                TrueForm.DeckFocus.Heals => TrueForm.Mode.Support,
                TrueForm.DeckFocus.Hits => TrueForm.Mode.Attack,
                TrueForm.DeckFocus.Shields => TrueForm.Mode.Defend,
                _ => (TrueForm.Mode?) null,
            };
            if (focusMode is { } m) {
                TrueForm.SetMode(m);
            }
            else {
                TrueForm.ResetMode();
            }

            var what = focus switch {
                TrueForm.DeckFocus.Heals => "heals only: no damage spells, just heals, shields and cleanses",
                TrueForm.DeckFocus.Hits => "damage: mostly hits",
                TrueForm.DeckFocus.Shields => "shields: shields and heals first",
                _ => "my normal mix",
            };
            Note("Ultimus", $"True Form: {heard.SpeakerName} asks for a {focus} deck (\"{UltimusText.Short(heard.Text, 60)}\").");
            SendReply(heard, $"Rebuilding my deck for {what}{(me.Wizard?.IsInDuel == true ? ", right after this battle" : "")}. I'll keep it that way until you say otherwise.", "true form");
            return true;
        }

        // Astral schools are not a school a wizard is ("become moon").
        if (System.Text.RegularExpressions.Regex.IsMatch(order, @"\b(go|become|be|switch to|change to|turn into)\s+(an?\s+)?(moon|sun|star|shadow|astral)\b")) {
            SendReply(heard, "I can't be an astral school, but I can be Fire, Ice, Storm, Myth, Life, Death or Balance. Try \"go ice for me\".", "true form");
            return true;
        }

        // His setups beside his school's own (Niduus, 2026-11-19): universal damage ("go universal for me"), full outgoing
        // healing and full resist ("make a full healing set ... and full tank set ... giving the commander options of class to
        // pick from, citing the more optimal of their desires first"). A full setup with no school named gets the schools
        // offered, best first; the asker's pick ("life") then switches him.
        // Full glass ("if he goes full glass, he is to go exactly that"): his school's damage, pierce and accuracy only.
        // The build for a school just asked for ("go myth", then "full damage" / "defensive" / "balanced").
        if (TrueForm.BuildPick is { } building && building.HeadId == heard.SpeakerCharId && DateTime.UtcNow - TrueForm.BuildPickAt < TrueForm.BuildPickWait
            && TrueForm.BuildFrom(order) is { } build) {
            TrueForm.BuildPick = null;
            return build == TrueForm.Setup.Own ? TrueFormSchoolSwap(heard, me, building.School, null) : TrueFormSetup(heard, me, grouped, build, building.School);
        }

        var setup = TrueForm.FullGlassFrom(order) ? TrueForm.Setup.FullGlass
            : TrueForm.UniversalFrom(order) ? TrueForm.Setup.Universal
            : TrueForm.FullHealFrom(order) ? TrueForm.Setup.FullHeal
            : TrueForm.ImmunityFrom(order) ? TrueForm.Setup.Immunity
            : TrueForm.FullTankFrom(order) ? TrueForm.Setup.FullTank
            : TrueForm.DefensiveFrom(order) ? TrueForm.Setup.Defensive
            : (TrueForm.Setup?) null;
        var pending = TrueForm.PendingPick is { } waiting && waiting.HeadId == heard.SpeakerCharId
                      && DateTime.UtcNow - TrueForm.PendingPickAt < TimeSpan.FromMinutes(3) ? waiting : null;
        var named = SchoolNamed(order);
        if (setup is null && pending is not null && named is { } picked) {
            setup = pending.Kind;
            TrueForm.PendingPick = null;
            return TrueFormSetup(heard, me, grouped, setup.Value, picked);
        }

        if (setup is { } kind) {
            if (kind is TrueForm.Setup.Universal or TrueForm.Setup.FullGlass or TrueForm.Setup.Defensive) {
                return TrueFormSetup(heard, me, grouped, kind, named ?? TrueForm.School);
            }

            if (named is { } namedSchool) {
                return TrueFormSetup(heard, me, grouped, kind, namedSchool);
            }

            // Already in that setup: no school question again (Niduus, 2026-11-20: "go full healer" twice, then "life", got
            // "I'm already in my full healing setup as Life!" each time).
            if (TrueForm.Kind == kind && !TrueFormBuild.HasPending) {
                var setupWords = kind == TrueForm.Setup.FullHeal ? "full healing" : kind == TrueForm.Setup.Immunity ? "Universal Immunity" : "full tank";
                SendReply(heard, $"I'm already in my {setupWords} setup as {TrueForm.School}! Say another school, like \"go full {(kind == TrueForm.Setup.FullHeal ? "healer" : "tank")} as ice\", to change it.", "true form");
                return true;
            }

            var best = TrueForm.BestSchoolsFor(kind);
            TrueForm.PendingPick = new TrueForm.SwapAsk(best[0], null, heard.SpeakerCharId, heard.SpeakerName, kind);
            TrueForm.PendingPickAt = DateTime.UtcNow;
            var what = kind == TrueForm.Setup.FullHeal ? "full healing" : kind == TrueForm.Setup.Immunity ? "Universal Immunity" : "full tank";
            SendReply(heard, $"Which school for my {what} setup? Best first: {string.Join(", ", best)}. " +
                             $"Just say the school, like \"{best[0].ToString().ToLowerInvariant()}\".", "true form");
            return true;
        }

        if (!grouped) {
            // "Become fire please" before grouping: he comes, and switches once the group is made.
            if (TrueForm.SchoolFrom(order) is { } wanted) {
                var rest = System.Text.RegularExpressions.Regex.Replace(order, @"\b(fire|ice|storm|myth|life|death|balance)\b", " ");
                var role = TrueForm.ModeFrom(rest) is { } r && r is not (TrueForm.Mode.Auto or TrueForm.Mode.Evil) ? r : (TrueForm.Mode?) null;
                TrueForm.SwapAfterJoin = new TrueForm.SwapAsk(wanted, role, heard.SpeakerCharId, heard.SpeakerName);
                TrueForm.SwapAfterJoinAt = DateTime.UtcNow;
                TrueForm.Call(heard.SpeakerCharId, heard.SpeakerName);
                Note("Ultimus", $"True Form: {heard.SpeakerName} asks for {wanted} before grouping: he comes, then switches.");
                SendReply(heard, $"Sure! Let's group first, then I'll switch to {wanted}." +
                                 (TrueForm.Misfit(wanted, role) is { } misfit ? " Heads up: " + misfit : ""), "true form");
                return true;
            }

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

        // An order for this battle ("heal me", "blade Mitsu", "Legion Shield us", "kill them") is his group leader's order,
        // read in LeaderOrders, not a new mode (Niduus, 2026-11-19: "Legion Shield us" set him to defend and nothing was cast).
        if (IsBattleOrder(heard, me)) {
            return false;
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
            if (role is null) {
                // Which build: the commander picks within 30 seconds, else balanced (TrueFormBuildPickTick).
                TrueForm.BuildPick = new TrueForm.SwapAsk(school, null, heard.SpeakerCharId, heard.SpeakerName);
                TrueForm.BuildPickAt = DateTime.UtcNow;
                Note("Ultimus", $"True Form: {heard.SpeakerName} asks for {school}; asks which build (30 s, then balanced).");
                SendReply(heard, $"Going {school}! Which build: balanced, full damage (glass cannon), defensive, full healer, or Universal Immunity? " +
                                 "Say it within 30 seconds, or I'll go balanced.", "true form");
                return true;
            }

            return TrueFormSchoolSwap(heard, me, school, role);
        }

        // A mode, however it is said: "defend", "go attack", "play support", "be evil", "tank for us", "atack". A question
        // ("do you need healing", "are you attacking") is no order.
        if (!IsQuestion(order) && TrueForm.ModeFrom(order) is { } mode) {
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

    private readonly System.Collections.Generic.Dictionary<ulong, DateTime> _tfOfferedAt = [];

    // The game keeps another wizard's character card (stats, gear, pet and its talents) from the last time it asked, and does
    // not ask again when he logs back in changed: his pet card showed the old talents (Niduus, 2026-11-20, "ugh...": the
    // server last sent his card at 23:01:50, he changed to full healer at 23:02:31, and the game never asked again). So after
    // each change his whole card is sent to every player online, as the game does for a wishlist change.
    private DateTime _tfNextStrip;

    private void TrueFormCardPush(Wizard wizard) {
        try {
            var sent = 0;
            foreach (var player in WizardData.Collections.OnlinePlayerCollection.GetOnlinePlayers()
                         .Where(p => p.CharacterId != 0 && p.CharacterId != wizard.CharId && p.ActorPath is not null && TrueForm.IsPlayer(p.CharacterId))) {
                var session = Context.System.ActorSelection(player.ActorPath);
                foreach (var id in new[] { wizard.GameObjectID, wizard.CharId }) {
                    var card = Shared.Character.BuddyStatsBuilder.Build(wizard, new Imcodec.MessageLayer.Generated.GAME_5_PROTOCOL.MSG_BUDDYSTATS { BuddyID = id }, 0);
                    session.Tell(card);
                }

                sent++;
            }

            Note("Ultimus", $"True Form: his new character card (pet talents, gear, stats) sent to {sent} player(s).");
        }
        catch (Exception e) {
            Imlight.Common.Logger.Warning("True Form: his character card could not be sent: {0}", Imlight.Common.Logger.Args(e.Message));
        }
    }

    // "Go ice weave fire", "weave storm", "stop weaving", "get more ice accuracy", "reset stats", "last build", and the yes to
    // an offered last build. True when the order was all that; a weave or stat ask said with a school or setup is kept for
    // that switch, and the order left for it.
    private bool TrueFormBuildOrder(UltimusVoice.Heard heard, UltimusMember.Status me, bool grouped, ref string order) {
        var commander = heard.SpeakerCharId;
        var said0 = order;
        bool Is(string pattern) => System.Text.RegularExpressions.Regex.IsMatch(said0, pattern);

        // The yes (or no) to his offer of their last build.
        if (TrueFormBuild.Offered is { } offer && offer.Item1 == commander && DateTime.UtcNow - TrueFormBuild.OfferedAt < TimeSpan.FromMinutes(3)) {
            if (Is(@"^(yes|yeah|yep|yup|sure|ok|okay|please|do it|go ahead|sounds good|y)\b") || Is(@"\b(last|same|previous|old)\s+(build|setup|one|time)\b")) {
                TrueFormBuild.Offered = null;
                return TrueFormUseBuild(heard, me, grouped, offer.Item2);
            }

            if (Is(@"^(no|nah|nope|no thanks|new build|something else|not now|n)\b")) {
                TrueFormBuild.Offered = null;
                SendReply(heard, "Okay, a fresh build then. Tell me a school (\"go ice\"), a weave (\"go ice weave fire\") or a stat (\"get more ice accuracy\").", "true form");
                return true;
            }
        }

        if (Is(@"\b(last|same|previous|old)\s+(build|setup)\b|\b(like|as)\s+last time\b")) {
            if (TrueFormBuild.LastFor(commander) is { } last) {
                return TrueFormUseBuild(heard, me, grouped, last);
            }

            SendReply(heard, "I don't have a build of yours yet. Tell me a school (\"go ice\"), a weave (\"go ice weave fire\") or a stat (\"get more ice accuracy\").", "true form");
            return true;
        }

        if (TrueFormBuild.NoWeaveFrom(order)) {
            TrueFormBuild.AskWeave(null);
            return TrueFormRedress(heard, me, grouped, TrueFormBuild.Weave is { } had ? $"No more {had} weave: back to {TrueForm.School} only." : $"I'm not weaving now, just {TrueForm.School}.");
        }

        if (TrueFormBuild.NoPushesFrom(order)) {
            TrueFormBuild.AskPushes([]);
            return TrueFormRedress(heard, me, grouped, "Stat asks cleared: back to my build's own picks.");
        }

        var weave = TrueFormBuild.WeaveFrom(order, out var rest);
        // Stat asks, not in a deck order ("focus on healing" is about his deck) or a battle order.
        var pushVerb = System.Text.RegularExpressions.Regex.Match(rest, @"\b(more|extra|higher|boost|increase|raise|prioriti[sz]e|stack|maximi[sz]e)\b");
        var pushes = pushVerb.Success && !System.Text.RegularExpressions.Regex.IsMatch(rest, @"\b(deck|spells?|cards?)\b")
            ? TrueFormBuild.PushesFrom(rest[pushVerb.Index..]) : [];
        if (weave is null && pushes.Count == 0) {
            return false;
        }

        if (pushes.Count > 0) {
            rest = rest[..pushVerb.Index].Trim();
            rest = System.Text.RegularExpressions.Regex.Replace(rest, @"(\s*\b(and|also|plus|with|get|then|to|please)\b)+$", "").Trim();
        }

        if (weave is { } w) {
            if (w == (SchoolNamed(rest) ?? TrueForm.School)) {
                SendReply(heard, $"I can't weave {w} with {w} as my main school. Pick another, like \"go {w.ToString().ToLowerInvariant()} weave {(w == Shared.Behaviors.MagicSchool.Fire ? "ice" : "fire")}\".", "true form");
                return true;
            }

            TrueFormBuild.AskWeave(w);
        }

        if (pushes.Count > 0) {
            TrueFormBuild.AskPushes(TrueFormBuild.Pushes.Where(p => !pushes.Contains(p)).Concat(pushes));
        }

        var said = (weave is { } ws ? $"Weaving {ws}: my gear, jewels, pet and deck cover both schools, and my school pips go to {ws}." : "")
                   + (pushes.Count > 0 ? $"{(weave is null ? "" : " ")}More {string.Join(" and ", pushes.Select(p => p.Words))}: I'll push that in my gear, jewels and pet while keeping my build." : "");
        // Said with a school or setup: that switch takes them; the rest of the order is read as usual.
        var swap = SchoolNamed(rest) is not null || TrueForm.FullGlassFrom(rest) || TrueForm.UniversalFrom(rest) || TrueForm.FullHealFrom(rest)
                   || TrueForm.FullTankFrom(rest) || TrueForm.ImmunityFrom(rest) || TrueForm.DefensiveFrom(rest);
        if (swap) {
            Note("Ultimus", $"True Form: {heard.SpeakerName} asks for {(weave is null ? "" : $"a {weave} weave ")}{string.Join(", ", pushes.Select(p => p.Words))} with \"{rest}\".");
            SendReply(heard, said, "true form");
            order = rest;
            return false;
        }

        return TrueFormRedress(heard, me, grouped, said);
    }

    // His school and setup as they are, made again with the weave or stat asks just given.
    private bool TrueFormRedress(UltimusVoice.Heard heard, UltimusMember.Status me, bool grouped, string said) {
        var school = TrueForm.School;
        if (!grouped) {
            TrueForm.SwapAfterJoin = new TrueForm.SwapAsk(school, TrueForm.Role, heard.SpeakerCharId, heard.SpeakerName, TrueForm.Kind);
            TrueForm.SwapAfterJoinAt = DateTime.UtcNow;
            TrueForm.Call(heard.SpeakerCharId, heard.SpeakerName);
            SendReply(heard, $"{said} Let's group first, then I'll change.", "true form");
            return true;
        }

        TrueForm.AskSwap(school, TrueForm.Role, heard.SpeakerCharId, heard.SpeakerName, TrueForm.Kind);
        Note("Ultimus", $"True Form: {heard.SpeakerName}: {said}");
        SendReply(heard, $"{said} I'll pop out {(me.Wizard?.IsInDuel == true ? "right after this battle " : "")}for a moment to change and be right back.", "true form");
        return true;
    }

    // A commander's last build put back.
    private bool TrueFormUseBuild(UltimusVoice.Heard heard, UltimusMember.Status me, bool grouped, TrueFormBuild.Saved build) {
        TrueFormBuild.AskWeave(build.Weave);
        TrueFormBuild.AskPushes(build.Pushes);
        if (!grouped) {
            TrueForm.SwapAfterJoin = new TrueForm.SwapAsk(build.School, build.Role, heard.SpeakerCharId, heard.SpeakerName, build.Kind);
            TrueForm.SwapAfterJoinAt = DateTime.UtcNow;
            TrueForm.Call(heard.SpeakerCharId, heard.SpeakerName);
            SendReply(heard, $"Sure! Let's group first, then I'll go {build.Words} like last time.", "true form");
            return true;
        }

        TrueForm.AskSwap(build.School, build.Role, heard.SpeakerCharId, heard.SpeakerName, build.Kind);
        if (build.Role is { } role) {
            TrueForm.SetMode(role);
        }
        else {
            TrueForm.ResetMode();
        }

        Note("Ultimus", $"True Form: {heard.SpeakerName} takes their last build: {build.Words}.");
        SendReply(heard, $"Your last build it is: {build.Words}. I'll pop out for a moment to change and be right back.", "true form");
        return true;
    }

    // A school switch with its build picked: balanced (no role) or leaning to the role said with it ("go ice and tank").
    private bool TrueFormSchoolSwap(UltimusVoice.Heard heard, UltimusMember.Status me, Shared.Behaviors.MagicSchool school, TrueForm.Mode? role) {
        if (school == TrueForm.School && role == TrueForm.Role && !TrueForm.Special && !TrueFormBuild.HasPending) {
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

    // No build picked in 30 seconds after "go <school>": balanced.
    private void TrueFormBuildPickTick(UltimusMember.Status me) {
        if (TrueForm.BuildPick is not { } building || DateTime.UtcNow - TrueForm.BuildPickAt < TrueForm.BuildPickWait) {
            return;
        }

        TrueForm.BuildPick = null;
        if (building.School == TrueForm.School && TrueForm.Role is null && !TrueForm.Special && !TrueFormBuild.HasPending) {
            Announce(SAY_PARTY, $"No build picked, so I'm staying in my balanced {building.School} setup.", urgent: true);
            return;
        }

        TrueForm.AskSwap(building.School, null, building.HeadId, building.HeadName);
        TrueForm.ResetMode();
        Note("Ultimus", $"True Form: no build picked for {building.School} in 30 s: balanced.");
        Announce(SAY_PARTY, $"No build picked, so I'm going balanced {building.School}. I'll pop out for a moment to change and be right back.", urgent: true);
    }

    // A line that names a card he can be dealt, or an order with someone to do it on ("heal me", "shield Mitsu", "kill them").
    private bool IsBattleOrder(UltimusVoice.Heard heard, UltimusMember.Status me) {
        var orders = UltimusOrders.Parse(WithoutMyName(heard.Text ?? "", me), part => CardSaid(me, part));
        return orders.Any(o => o.Kind is UltimusOrders.Kind.Cast or UltimusOrders.Kind.Focus or UltimusOrders.Kind.Discard or UltimusOrders.Kind.GoTo
                               // "Heal us" / "shield the team" stays a mode (support, defend); "heal me", "blade Mitsu" is this battle.
                               || (o.Kind is UltimusOrders.Kind.Heal or UltimusOrders.Kind.Shield or UltimusOrders.Kind.Blade or UltimusOrders.Kind.Trap
                                   or UltimusOrders.Kind.Attack && o.Target.Length > 0
                                   && o.Target is not ("us" or "team" or "the team" or "everyone" or "all of us" or "everybody")));
    }

    // A school named in an order ("life", "go death", "be an ice wizard"), or null.
    private static Shared.Behaviors.MagicSchool? SchoolNamed(string order) {
        if (TrueForm.SchoolFrom(order) is { } said) {
            return said;
        }

        var m = System.Text.RegularExpressions.Regex.Match(order ?? "", @"\b(fire|ice|storm|myth|life|death|balance)\b");
        return m.Success && Enum.TryParse<Shared.Behaviors.MagicSchool>(m.Value, true, out var school) ? school : null;
    }

    // Switches him to a setup (with the school for it), now or once grouped.
    private bool TrueFormSetup(UltimusVoice.Heard heard, UltimusMember.Status me, bool grouped, TrueForm.Setup kind, Shared.Behaviors.MagicSchool school) {
        var role = kind switch {
            TrueForm.Setup.FullHeal => TrueForm.Mode.Support,
            TrueForm.Setup.FullTank or TrueForm.Setup.Immunity => TrueForm.Mode.Defend,
            TrueForm.Setup.Defensive => TrueForm.Mode.Auto,
            _ => TrueForm.Mode.Attack,
        };
        var words = TrueForm.SetupWords(kind) + (kind == TrueForm.Setup.Universal ? "" : $" as {school}");
        if (!grouped) {
            TrueForm.SwapAfterJoin = new TrueForm.SwapAsk(school, role, heard.SpeakerCharId, heard.SpeakerName, kind);
            TrueForm.SwapAfterJoinAt = DateTime.UtcNow;
            TrueForm.Call(heard.SpeakerCharId, heard.SpeakerName);
            SendReply(heard, $"Sure! Let's group first, then I'll switch to {words}.", "true form");
            return true;
        }

        if (TrueForm.Kind == kind && TrueForm.School == school && !TrueFormBuild.HasPending) {
            SendReply(heard, $"I'm already in {words}!", "true form");
            return true;
        }

        if (me.Wizard?.IsInDuel == true) {
            SendReply(heard, $"I'll switch to {words} right after this battle.", "true form");
        }

        TrueForm.AskSwap(school, role, heard.SpeakerCharId, heard.SpeakerName, kind);
        TrueForm.SetMode(role);
        Note("Ultimus", $"True Form: {heard.SpeakerName} asks for {words} (\"{UltimusText.Short(heard.Text, 60)}\").");
        var what = kind switch {
            TrueForm.Setup.FullHeal => "every piece, jewel and pet talent on outgoing healing, and I'll play support",
            TrueForm.Setup.FullTank => "every piece, jewel and pet talent on resist, and I'll play defend",
            TrueForm.Setup.Immunity => "every piece, set, jewel and pet talent on resist to every school, aiming for 100% (Universal Immunity), and I'll play defend",
            TrueForm.Setup.FullGlass => $"every piece, jewel and pet talent on {school} damage, pierce and accuracy, nothing on resist or health, and I'll play attack",
            TrueForm.Setup.Defensive => "resist and health first with some damage kept, and I pick attack, defend or support each round",
            _ => "every piece, jewel and pet talent on damage to all schools, and I'll play attack",
        };
        var warn = kind is TrueForm.Setup.FullHeal or TrueForm.Setup.FullTank or TrueForm.Setup.Immunity && TrueForm.BestSchoolsFor(kind).Take(2).All(b => b != school)
            ? $" Heads up: {school} is not the best school for this; {string.Join(" or ", TrueForm.BestSchoolsFor(kind).Take(2))} would do it better."
            : "";
        SendReply(heard, $"Switching to {words}: {what}. I'll pop out for a moment to change and be right back. " +
                         "If I'm not back in the group in a minute, send me an invite." + warn, "true form");
        return true;
    }

}
