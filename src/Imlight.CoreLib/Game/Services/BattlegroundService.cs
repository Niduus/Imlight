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
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.Game.Battlegrounds;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;
using BG = Imlight.CoreLib.Game.Battlegrounds.Battlegrounds;

namespace Imlight.CoreLib.Game.Services;

/*
 * ========================================================================
 * BATTLEGROUND SERVICE
 * ========================================================================
 *
 * PURPOSE:
 * The Beastmoon windows a wizard uses in an arena's base.
 *
 * NOTE:
 * - Beast Form Select (BGPolymorphSelect.gui, opened by a statue: Zone/Components/BeastFormStatueComponent.cs). For
 *   each form it shows, the window asks MSG_BGISPOLYMORPHUNLOCKEDREQUEST and draws the answer: whether the wizard may
 *   take it, and its rating, the form's level (BeastmoonProgress). SELECT sends MSG_BGSELECTPOLYMORPHREQUEST; the
 *   wizard takes the form (BeastForm.Start), and MSG_BGSELECTPOLYMORPHRESPONSE names the beast taken (the window
 *   closes when it is its own, PolymorphCategoryAvailable 1) and the one let go (ReleasedPolymorphCategory, which
 *   frees that beast's statue for the team in PvP). Read from WizardGraphicalClient (BGPolymorphSelectWindow).
 * - A form may be taken when the match allows its beast and school (BattlegroundTemplate m_unlockedPolymorphTags, "Life
 *   Cyclops") and the wizard has its tier.
 */
internal class BattlegroundService(SessionActor sessionActor) : MessageService(sessionActor) {

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new BattlegroundService(parentActor));

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_BGISPOLYMORPHUNLOCKEDREQUEST))]
    private void ReceiveIsPolymorphUnlocked(WIZARD2_53_PROTOCOL.MSG_BGISPOLYMORPHUNLOCKEDREQUEST message) {
        var wizard = GetActiveWizard();
        var form = BG.FormById((uint) message.PolymorphTemplateID, out var fileName);
        var unlocked = wizard is not null && form is not null && MayTake(wizard, fileName, out _);
        // The rating is the beast's tier (the game's words: "improve your Tier"), the most the wizard may take now, from 0:
        // the window shows it plus one (BGPolymorphSelectWindow, "TIER 1" for 0).
        var tier = wizard is null || fileName is null ? 0 : Math.Max(0, BeastmoonProgress.TierOf(wizard.CharId, fileName, EventAllows(wizard, fileName)) - 1);

        SendToSocket(new WIZARD2_53_PROTOCOL.MSG_BGISPOLYMORPHUNLOCKEDRESPONSE {
            PolymorphTemplateID = message.PolymorphTemplateID,
            PolymorphRating = tier,
            IsPolymorphUnlocked = (byte) (unlocked ? 1 : 0),
        });
    }

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_BGSELECTPOLYMORPHREQUEST))]
    private void ReceiveSelectPolymorph(WIZARD2_53_PROTOCOL.MSG_BGSELECTPOLYMORPHREQUEST message) {
        var wizard = GetActiveWizard();
        var form = BG.FormById((uint) message.PolymorphTemplateID, out var fileName);
        if (wizard is null || form is null) {
            Logger.Warning("Beastmoon | {0} chose form {1}, which is not a beast form.", Logger.Args(wizard?.CharId, message.PolymorphTemplateID));

            return;
        }

        var category = BG.FormCategory(fileName);
        if (!MayTake(wizard, fileName, out var why)) {
            InformGameClient(why);
            SendToSocket(new WIZARD2_53_PROTOCOL.MSG_BGSELECTPOLYMORPHRESPONSE {
                PolymorphCategory = category,
                PolymorphCategoryAvailable = 0,
                ReleasedPolymorphCategory = "",
            });

            return;
        }

        var before = BG.Of(wizard.CharId)?.FormName;
        var error = BeastForm.Start(wizard, SessionActor.ActorRef, SessionActor.GetZoneActor(), fileName, BeastForm.Look.Both);
        if (error is not null) {
            InformGameClient(error);

            return;
        }

        var released = before is null ? "" : BG.FormCategory(before);
        SendToSocket(new WIZARD2_53_PROTOCOL.MSG_BGSELECTPOLYMORPHRESPONSE {
            PolymorphCategory = category,
            PolymorphCategoryAvailable = 1,
            ReleasedPolymorphCategory = string.Equals(released, category, StringComparison.OrdinalIgnoreCase) ? "" : released,
        });
        Logger.Information("Beastmoon | {0} chose the form {1} at a statue.", Logger.Args(wizard.CharId, fileName));
        BattleCards.SyncBattleBook(wizard, SessionActor.ActorRef);
    }

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_BGSELECTPOLYMORPHDONE))]
    private void ReceiveSelectPolymorphDone(WIZARD2_53_PROTOCOL.MSG_BGSELECTPOLYMORPHDONE message) {
        // The window closed; nothing waits on it.
    }

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_BATTLECARDBUY))]
    private void ReceiveBattleCardBuy(WIZARD2_53_PROTOCOL.MSG_BATTLECARDBUY message) {
        var wizard = GetActiveWizard();
        if (wizard is null) {
            return;
        }

        var why = BattleCards.Buy(wizard, SessionActor.ActorRef, message.Data);
        if (why is not null) {
            InformGameClient(why);
        }

        BattleCards.SyncBattleBook(wizard, SessionActor.ActorRef);
        SendToSocket(new WIZARD2_53_PROTOCOL.MSG_BATTLECARDBUYCONFIRM { Failure = (sbyte) (why is null ? 0 : 1) });
        if (BattleCards.ShopList(wizard, message.npcGlobalID, out _) is { } list) {
            SendToSocket(list);
        }
    }

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_BATTLECARDTRASH))]
    private void ReceiveBattleCardTrash(WIZARD2_53_PROTOCOL.MSG_BATTLECARDTRASH message) {
        var wizard = GetActiveWizard();
        if (wizard is null) {
            return;
        }

        var why = BattleCards.Trash(wizard, message.SpellID);
        if (why is not null) {
            InformGameClient(why);
        }

        BattleCards.SyncBattleBook(wizard, SessionActor.ActorRef);
        SendToSocket(new WIZARD2_53_PROTOCOL.MSG_BATTLECARDTRASHCONFIRM { Failure = (sbyte) (why is null ? 0 : 1) });
        if (BattleCards.ShopList(wizard, message.npcGlobalID, out _) is { } list) {
            SendToSocket(list);
        }
    }

    // The event window's Join button (Battlegrounds_UI_JoinMatch_Solo) for a battleground event that is on
    // (Battlegrounds.PvEEvent): the wizard waits in the match's lobby, then goes in with whoever joined in time
    // (Battlegrounds/BattlegroundQueue.cs). ProjectID 0 is the game's Cancel.
    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_BATTLEGROUNDQUEUEPLAYER))]
    private void ReceiveQueuePlayer(WIZARD2_53_PROTOCOL.MSG_BATTLEGROUNDQUEUEPLAYER message) => Queue(message.ProjectID);

    // Join as a group: the group goes into one lobby, and so one copy of the arena.
    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_BATTLEGROUNDQUEUEGROUP))]
    private void ReceiveQueueGroup(WIZARD2_53_PROTOCOL.MSG_BATTLEGROUNDQUEUEGROUP message) => Queue(message.ProjectID, group: true);

    // The group's members who are online and not in a match, other than the wizard.
    private static List<BattlegroundQueue.Entry> GroupMates(Wizard wizard)
        => !Imlight.CoreLib.Game.Groups.GroupRegistry.TryGetGroup(wizard.CharId, out var group) ? []
            : [.. group.Members.Where(m => m.CharId != wizard.CharId && m.IsOnline && !BG.IsBattlegroundZone(m.ZonePath))
                .Select(m => new BattlegroundQueue.Entry(m.CharId, 0, m.Session))];

    private void Queue(uint projectId, bool group = false) {
        var wizard = GetActiveWizard();
        if (wizard is null || !BG.Enabled) {
            return;
        }

        if (projectId == 0) {
            if (BattlegroundQueue.Leave(wizard.CharId)) {
                InformGameClient("You left the queue.");
            }

            return;
        }

        var project = Imlight.CoreLib.Game.Events.ClassProjects.TemplateOf(projectId);
        var matchName = project?.m_extendedTemplateName.ToString();
        if (project is null || BG.Template(matchName) is null) {
            InformGameClient("That event has no Beastmoon match.");

            return;
        }

        if (BG.IsBattlegroundZone(wizard.Zone)) {
            InformGameClient("You are in a match already.");

            return;
        }

        Logger.Information("Beastmoon | {0} joins {1} ({2}){3}.",
            Logger.Args(wizard.CharId, project.m_projectName.ToString(), matchName, group ? " with their group" : ""));
        BattlegroundQueue.Join(matchName, projectId, new BattlegroundQueue.Entry(wizard.CharId, wizard.GameObjectID, SessionActor.ActorRef),
            group ? GroupMates(wizard) : []);
    }

    // The arena's word that the match is over (Zone.Battleground.cs EndMatch): the wizard's rewards. And the queue's
    // wait being over (Queue): into the match.
    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_BATTLEGROUNDCONTROL))]
    private void ReceiveBattlegroundControl(ZONE_102_PROTOCOL.MSG_BATTLEGROUNDCONTROL message) {
        switch (message.Kind) {
            case BattlegroundControlKind.Reward:
                // Given when the game's end panel asks for it (MSG_REQUESTBATTLEGROUNDSLOOT), or after a while
                // (Points -1: the wait is over).
                if (message.Points == -1) {
                    GivePendingReward();
                    break;
                }

                _pendingReward = message.Points == 1;
                Context.System.Scheduler.ScheduleTellOnce(TimeSpan.FromSeconds(20), Self,
                    new ZONE_102_PROTOCOL.MSG_BATTLEGROUNDCONTROL { Kind = BattlegroundControlKind.Reward, Points = -1 }, Self);
                break;
            case BattlegroundControlKind.CapturedArea:
                Imlight.CoreLib.Game.Events.ClassProjects.Record(SessionActor.ActorRef, GetActiveWizard(),
                    Imcodec.ObjectProperty.TypeCache.ClassProjectActivityType.CPT_ClaimCaptureArea);
                break;
            case BattlegroundControlKind.CoinClam:
                BeastRewards.CoinClam(SessionActor.ActorRef, GetActiveWizard());
                break;
            case BattlegroundControlKind.Start when !string.IsNullOrEmpty(message.Text):
                if (BattlegroundEntry.SendIntoMatch(GetActiveWizard(), SessionActor.ActorRef, message.Text, ownerCharId: message.Player) is { } why) {
                    InformGameClient(why);
                }
                break;
        }
    }

    private bool? _pendingReward;

    // The match's end panel is up and wants the match's loot.
    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_REQUESTBATTLEGROUNDSLOOT))]
    private void ReceiveRequestBattlegroundsLoot(WIZARD2_53_PROTOCOL.MSG_REQUESTBATTLEGROUNDSLOOT message) => GivePendingReward();

    private void GivePendingReward() {
        if (_pendingReward is not { } won) {
            return;
        }

        _pendingReward = null;
        BeastRewards.MatchOver(SessionActor.ActorRef, GetActiveWizard(), won);
    }

    // Whether the wizard may take the form here and now; why not, if not.
    private static bool MayTake(Wizard wizard, string fileName, out string why) {
        why = null;
        if (!BG.Enabled || !BG.IsBattlegroundZone(wizard.Zone)) {
            why = "Beast forms are taken in a Beastmoon arena.";

            return false;
        }

        if (wizard.IsInDuel) {
            why = "You can't change form during a battle.";

            return false;
        }

        var allowed = BeastmoonProgress.TierOf(wizard.CharId, fileName, EventAllows(wizard, fileName));
        if (allowed == 0) {
            why = "This match does not allow that beast, and you have not unlocked it.";

            return false;
        }

        if (BG.FormTier(fileName) > allowed) {
            why = $"You have not unlocked tier {BG.FormTier(fileName)} of this beast yet.";

            return false;
        }

        return true;
    }

    // Whether the wizard's match lets everyone take this beast (m_unlockedPolymorphTags); a match with no list does.
    private static bool EventAllows(Wizard wizard, string fileName) {
        var match = BG.Of(wizard.CharId)?.MatchName is { } name ? BG.Template(name) : null;

        return match?.m_unlockedPolymorphTags is not { Count: > 0 } || BeastUpgrades.EventAllows(match, fileName);
    }

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_BATTLEGROUNDPOLYMORPHIMPROVEREQ))]
    private void ReceiveImproveRequest(WIZARD2_53_PROTOCOL.MSG_BATTLEGROUNDPOLYMORPHIMPROVEREQ message) {
        var wizard = GetActiveWizard();
        if (wizard is null || !BG.Enabled) {
            return;
        }

        SendToSocket(BeastUpgrades.Improve(SessionActor.ActorRef, wizard, message));
        if (BeastUpgrades.Window(wizard, message.npcGlobalID) is { } window) {
            SendToSocket(window);
        }
    }

}
