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
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.DailyAssignments;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.Services;

/// <summary>
/// A wizard's Daily Assignment (DailyAssignmentData.cs): handed out the first time the wizard enters the game on a new
/// day, shown by Aegon Statz's Daily Assignment option or `.daily`, done by winning a battle against the creature or
/// by visiting the place, and rewarded by the day of the wizard's streak.
/// </summary>
internal class DailyAssignmentService(SessionActor sessionActor) : MessageService(sessionActor) {

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new DailyAssignmentService(parentActor));

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE))]
    private void ReceiveAttachComplete(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE message) {
        try {
            var wizard = GetActiveWizard();
            if (wizard is null || !DailyAssignmentData.CanTakeAssignments(wizard, out _)) {
                return;
            }

            var info = EnsureToday(wizard, out var index);
            SendUpdate(wizard, index);
            if (info is null || IsDoneToday(wizard) || info.m_templateID != 0) {
                return;
            }

            // A place to visit: in its zone, the game is told where the place is; a place the zone has no volume for
            // (or the zone itself) counts as visited on entry.
            if (!string.Equals(wizard.Zone, (string) info.m_zonePath, StringComparison.OrdinalIgnoreCase)) {
                return;
            }

            string volumeName = info.m_activatorVolumeName;
            if (DailyAssignmentData.TryGetVolume(wizard.Zone, volumeName, out var volume)) {
                SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_DAILYQUESTEXPLORE {
                    LocX = volume.X, LocY = volume.Y, LocZ = volume.Z, Radius = volume.Radius
                });
            }
            else {
                Complete(wizard, $"entered {wizard.Zone}");
            }
        }
        catch (Exception ex) {
            Logger.Error("Daily Assignments | Sending the assignment on entering the zone failed. {0}", Logger.Args(ex.Message));
        }
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_COMBATWIN))]
    private void ReceiveCombatWin(COMBAT_106_PROTOCOL.MSG_COMBATWIN message) {
        try {
            var wizard = GetActiveWizard();
            if (wizard is null || IsDoneToday(wizard) || !DailyAssignmentData.CanTakeAssignments(wizard, out _)) {
                return;
            }

            var info = EnsureToday(wizard, out _);
            if (info is null || info.m_templateID == 0 || message.MobTemplateIds?.Contains((ulong) info.m_templateID) != true) {
                return;
            }

            if (!DailyAssignmentData.DefeatAnywhere
                && !string.Equals(wizard.Zone, (string) info.m_zonePath, StringComparison.OrdinalIgnoreCase)) {
                return;
            }

            Complete(wizard, $"defeated creature {info.m_templateID}");
        }
        catch (Exception ex) {
            Logger.Error("Daily Assignments | Checking a won battle failed. {0}", Logger.Args(ex.Message));
        }
    }

    [MessageHandler(typeof(DailyAssignmentVolumeEntered))]
    private void ReceiveVolumeEntered(DailyAssignmentVolumeEntered message) {
        try {
            var wizard = GetActiveWizard();
            if (wizard is null || IsDoneToday(wizard) || !DailyAssignmentData.CanTakeAssignments(wizard, out _)) {
                return;
            }

            var info = EnsureToday(wizard, out _);
            if (info is null || info.m_templateID != 0
                || !string.Equals((string) info.m_activatorVolumeName, message.VolumeName, StringComparison.OrdinalIgnoreCase)
                || !string.Equals((string) info.m_zonePath, message.ZonePath, StringComparison.OrdinalIgnoreCase)) {
                return;
            }

            Complete(wizard, $"walked into {message.VolumeName}");
        }
        catch (Exception ex) {
            Logger.Error("Daily Assignments | Checking a visited place failed. {0}", Logger.Args(ex.Message));
        }
    }

    // Aegon Statz's Daily Assignment option.
    [MessageHandler(typeof(DailyAssignmentOpen))]
    private void ReceiveOpen(DailyAssignmentOpen message) {
        try {
            Open(GetActiveWizard());
        }
        catch (Exception ex) {
            Logger.Error("Daily Assignments | Opening the window failed. {0}", Logger.Args(ex.Message));
        }
    }

    // `.daily`.
    [MessageHandler(typeof(DailyAssignmentCommand))]
    private void ReceiveCommand(DailyAssignmentCommand message) {
        try {
            var wizard = GetActiveWizard();
            if (wizard is null) {
                return;
            }

            switch (message.Action) {
                case "new":
                    if (!DailyAssignmentData.CanTakeAssignments(wizard, out var why)) {
                        Reply(why);
                        return;
                    }

                    if (IsDoneToday(wizard)) {
                        Reply("You already did today's assignment. A new one comes tomorrow.");
                        return;
                    }

                    EnsureToday(wizard, out var old);
                    var picked = DailyAssignmentData.Pick(wizard, old);
                    wizard.SetRegistryValue(DailyAssignmentData.KEY_INDEX, (ulong) (picked + 1));
                    Logger.Information("Daily Assignments | {0} asked for another assignment: {1}.", Logger.Args(wizard.CharId, picked));
                    Open(wizard);
                    break;
                case "done":
                    if (IsDoneToday(wizard)) {
                        Reply("Today's assignment is already done.");
                        return;
                    }

                    EnsureToday(wizard, out _);
                    Complete(wizard, ".daily done");
                    break;
                case "reset":
                    foreach (var key in new[] {
                        DailyAssignmentData.KEY_DAY, DailyAssignmentData.KEY_INDEX,
                        DailyAssignmentData.KEY_DONE_DAY, DailyAssignmentData.KEY_STREAK,
                    }) {
                        wizard.SetRegistryValue(key, 0);
                    }

                    Reply("Your Daily Assignment and streak are reset. Type .daily for a new one.");
                    break;
                case "day":
                    // Makes the next completion the given day of the streak, to see each reward.
                    var day = (ulong) Math.Clamp(message.Number, 1, 255);
                    var today = DailyAssignmentData.Today();
                    wizard.SetRegistryValue(DailyAssignmentData.KEY_STREAK, day - 1);
                    wizard.SetRegistryValue(DailyAssignmentData.KEY_DONE_DAY, day > 1 ? today - 1 : 0);
                    Reply($"Your next finished assignment counts as day {day} ({DailyAssignmentData.DescribeReward(DailyAssignmentData.RewardFor(day))}).");
                    SendUpdate(wizard, (int) wizard.GetRegistryValue(DailyAssignmentData.KEY_INDEX) - 1);
                    break;
                default:
                    Open(wizard);
                    break;
            }
        }
        catch (Exception ex) {
            Logger.Error("Daily Assignments | .daily {0} failed. {1}", Logger.Args(message.Action, ex.Message));
        }
    }

    private void Open(Wizard wizard) {
        if (wizard is null) {
            return;
        }

        if (!DailyAssignmentData.CanTakeAssignments(wizard, out var why)) {
            Reply(why);
            return;
        }

        var info = EnsureToday(wizard, out var index);
        SendUpdate(wizard, index);
        SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_DAILYQUESTOPEN());

        var streak = wizard.GetRegistryValue(DailyAssignmentData.KEY_STREAK);
        Reply(IsDoneToday(wizard)
            ? $"Daily Assignment: done for today (streak {streak} days). The next one comes in {Hours()}."
            : $"Daily Assignment: {DailyAssignmentData.Describe(info)}. Reward: {DailyAssignmentData.DescribeReward(DailyAssignmentData.RewardFor(NextStreak(wizard)))}. Type .daily new for another one.");
        Logger.Information("Daily Assignments | {0} opened the window: assignment {1} ({2}), done today {3}.",
            Logger.Args(wizard.CharId, index, DailyAssignmentData.Describe(info), IsDoneToday(wizard)));
    }

    /// <summary>Today's assignment of the wizard, handing out a new one on a new day.</summary>
    private static DailyQuestInfo EnsureToday(Wizard wizard, out int index) {
        var today = DailyAssignmentData.Today();
        index = (int) wizard.GetRegistryValue(DailyAssignmentData.KEY_INDEX) - 1;
        if (wizard.GetRegistryValue(DailyAssignmentData.KEY_DAY) != today || DailyAssignmentData.Get(index) is null) {
            index = DailyAssignmentData.Pick(wizard);
            wizard.SetRegistryValue(DailyAssignmentData.KEY_DAY, today);
            wizard.SetRegistryValue(DailyAssignmentData.KEY_INDEX, (ulong) (index + 1));
            Logger.Information("Daily Assignments | {0} got today's assignment {1}: {2}.",
                Logger.Args(wizard.CharId, index, DailyAssignmentData.Describe(DailyAssignmentData.Get(index))));
        }

        return DailyAssignmentData.Get(index);
    }

    private static bool IsDoneToday(Wizard wizard)
        => wizard.GetRegistryValue(DailyAssignmentData.KEY_DONE_DAY) == DailyAssignmentData.Today();

    // The day of the streak the next finished assignment is: one more when the last one was done yesterday.
    private static ulong NextStreak(Wizard wizard) {
        var lastDone = wizard.GetRegistryValue(DailyAssignmentData.KEY_DONE_DAY);
        var streak = wizard.GetRegistryValue(DailyAssignmentData.KEY_STREAK);

        return lastDone != 0 && lastDone + 1 == DailyAssignmentData.Today() ? streak + 1 : 1;
    }

    private void SendUpdate(Wizard wizard, int index) {
        var info = DailyAssignmentData.Get(index);
        var done = IsDoneToday(wizard);
        var streak = done ? wizard.GetRegistryValue(DailyAssignmentData.KEY_STREAK) : NextStreak(wizard) - 1;
        SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_DailyQuestUpdate {
            QuestIndex = (uint) Math.Max(0, index),
            CurrentCircle = (byte) (info?.m_circleNumber ?? 1),
            ConsecutiveDays = (byte) Math.Min(255UL, streak),
            Flags = done ? DailyAssignmentData.FLAG_DONE_TODAY : (byte) 0,
            TimeLeft = DailyAssignmentData.SecondsLeft(),
        });
    }

    private void Complete(Wizard wizard, string how) {
        var today = DailyAssignmentData.Today();
        var index = (int) wizard.GetRegistryValue(DailyAssignmentData.KEY_INDEX) - 1;
        var info = DailyAssignmentData.Get(index);
        var streak = NextStreak(wizard);
        wizard.SetRegistryValue(DailyAssignmentData.KEY_STREAK, streak);
        wizard.SetRegistryValue(DailyAssignmentData.KEY_DONE_DAY, today);
        wizard.SetRegistryValue(DailyAssignmentData.KEY_TOTAL, wizard.GetRegistryValue(DailyAssignmentData.KEY_TOTAL) + 1);

        var reward = DailyAssignmentData.RewardFor(streak);
        var given = GiveReward(wizard, reward, (int) (info?.m_circleNumber ?? 1));
        Logger.Information("Daily Assignments | {0} finished assignment {1} ({2}), day {3} of the streak: {4}.",
            Logger.Args(wizard.CharId, index, how, streak, given));

        SendUpdate(wizard, index);
        SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_DAILYQUESTCOMPLETED {
            QuestIndex = (uint) Math.Max(0, index),
            CurrentCircle = (byte) (info?.m_circleNumber ?? 1),
            ConsecutiveDays = (byte) Math.Min(255UL, streak),
            Flags = DailyAssignmentData.FLAG_DONE_TODAY,
            ItemTemplateID = 0,
        });
        Reply($"Daily Assignment done! Day {streak} of your streak: {given}.");
        Imlight.CoreLib.Game.Events.ClassProjects.Record(SessionActor.ActorRef, wizard, Imcodec.ObjectProperty.TypeCache.ClassProjectActivityType.CPT_DailyQuest);
    }

    private string GiveReward(Wizard wizard, DailyQuestReward reward, int circle) {
        if (reward is null) {
            return "no reward (the server could not read the reward list)";
        }

        var session = SessionActor.ActorRef;
        switch ((int) reward.m_rewardType) {
            case DailyAssignmentData.RT_GOLD:
                return GiveGold(wizard, reward.m_quantity);
            case DailyAssignmentData.RT_CROWNS: {
                var account = wizard.Account ?? GetActiveAccount();
                if (account is null) {
                    return GiveGold(wizard, reward.m_quantity * 10);
                }

                account.SetCrowns(account.Crowns + reward.m_quantity);
                session.Tell(new WIZARD_12_PROTOCOL.MSG_CROWNBALANCE {
                    Failure = 0,
                    TotalCrowns = account.Crowns,
                    CharacterID = wizard.CharId,
                    CacheBalanceForCSSegmentation = 1
                });

                return $"{reward.m_quantity} Crowns";
            }
            case DailyAssignmentData.RT_CHEST: {
                var names = new[] { (string) reward.m_lootTable }
                    .Where(n => !string.IsNullOrWhiteSpace(n) && DropTableCollection.GetDropTable(n) is not null)
                    .ToArray();
                if (names.Length == 0) {
                    Logger.Information("Daily Assignments | The server has no drop table {0}; giving {1} Gold instead.",
                        Logger.Args((string) reward.m_lootTable, DailyAssignmentData.ChestFallbackGold));

                    return GiveGold(wizard, DailyAssignmentData.ChestFallbackGold) + " (the chest's loot is not on this server)";
                }

                var result = DropTableRoller.Roll(names, session, GetActiveGameObject(), wizard);
                LootGranter.GrantAndDisplay(session, wizard, result);

                return $"a chest ({string.Join(", ", names)})";
            }
            case DailyAssignmentData.RT_QUEST: {
                var quest = DailyAssignmentData.LoreQuestFor(circle);
                if (!string.IsNullOrEmpty(quest) && QuestTemplateCollection.GetQuestByName(quest) is not null
                    && !wizard.HasQuest(quest) && !wizard.HasCompletedQuest(quest)) {
                    session.Tell(new CHARACTER_103_PROTOCOL.MSG_QUESTCOMMAND { Action = "grant", QuestName = quest });

                    return $"the Gravulum Order quest {quest}";
                }

                return GiveGold(wizard, Math.Max(reward.m_quantity, 1));
            }
            default:
                return GiveGold(wizard, Math.Max(reward.m_quantity, 100));
        }
    }

    private string GiveGold(Wizard wizard, int gold) {
        LootGranter.GrantAndDisplay(SessionActor.ActorRef, wizard, new DropTableResult { DropTableId = "DailyAssignment", GoldAmount = gold });

        return $"{gold} Gold";
    }

    private static string Hours() {
        var left = TimeSpan.FromSeconds(DailyAssignmentData.SecondsLeft());

        return left.TotalHours >= 1 ? $"{(int) left.TotalHours} h {left.Minutes} min" : $"{left.Minutes} min";
    }

    private void Reply(string text)
        => SendToSocket(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = text, Modal = 0 });

}

/// <summary>Aegon Statz's Daily Assignment option was picked (InteractDailyAssignmentComponent).</summary>
internal sealed class DailyAssignmentOpen : IServerMessage {

    public byte MessageOrder { get; } = 1;
    public byte ServiceID { get; } = 104;

}

/// <summary>A player walked into a volume some Daily Assignment visits (VolumeComponent).</summary>
internal sealed class DailyAssignmentVolumeEntered : IServerMessage {

    public byte MessageOrder { get; } = 2;
    public byte ServiceID { get; } = 104;

    public string ZonePath { get; init; }
    public string VolumeName { get; init; }

}

/// <summary>`.daily` (CommandDailyProtocol): show, new, done, reset or day N.</summary>
internal sealed class DailyAssignmentCommand : IServerMessage {

    public byte MessageOrder { get; } = 3;
    public byte ServiceID { get; } = 104;

    public string Action { get; init; } = "";
    public int Number { get; init; }

}
