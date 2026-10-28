/*
 * Quest rewards that went wrong, given back (Niduus, 2026-10-28: Grim Tales gave the Sprite pet instead of the Sprite
 * treasure card, and no quest gave its extra potion bottle). At every login:
 *   - Potion bottles: a wizard has at least [Character] StartingPotions plus one bottle for each finished quest whose
 *     reward gives one (Grim Tales, A Whole New World, Missing Souls). Never lowered.
 *   - Missed items: a wizard who finished a quest below before its fix gets the reward once. A wizard who finishes it
 *     now gets the right reward from the quest, so finishing it marks the repair as done.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.Quests;

internal static class QuestRewardRepair {

    private const string COMPLETE_SUFFIX = "_Complete";

    private sealed record Repair(string Id, string Quest, string QuestTitle, ulong Item, string ItemName);

    // Grim Tales' reward named the Sprite treasure card but was the Sprite pet (100420).
    private static readonly Repair[] s_repairs = [
        new("grim-tales-sprite-tc", "WC-UNICORN-MAIN-008", "Grim Tales", 1889148781, "Sprite treasure card"),
    ];

    /// <summary>
    /// How many potion bottles the wizard's finished quests give.
    /// </summary>
    internal static int PotionSlotsFromQuests(Wizard wizard) {
        var count = 0;
        foreach (var (key, value) in wizard.QuestBehavior?.Registry.ToArray() ?? Array.Empty<KeyValuePair<string, ulong>>()) {
            if (value == 0 || !key.EndsWith(COMPLETE_SUFFIX, StringComparison.Ordinal)) {
                continue;
            }

            var quest = QuestTemplateCollection.GetQuestByName(key[..^COMPLETE_SUFFIX.Length]);
            var tables = quest?.m_endResults?.m_results?.OfType<ResDropTable>() ?? Enumerable.Empty<ResDropTable>();
            if (tables.Any(t => DropTableFiles.Get(t.m_tableName) is { GrantsPotionSlot: true })) {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Marks the repairs of a quest as done: the wizard just got its reward from the fixed quest.
    /// </summary>
    internal static void MarkDone(Wizard wizard, string questName) {
        wizard.RewardRepairs ??= [];
        var changed = false;
        foreach (var repair in s_repairs) {
            if (string.Equals(repair.Quest, questName, StringComparison.OrdinalIgnoreCase)
                    && !wizard.RewardRepairs.Contains(repair.Id)) {
                wizard.RewardRepairs.Add(repair.Id);
                changed = true;
            }
        }

        if (changed) {
            WizardCollection.UpdateCharacterRewardRepairs(wizard);
        }
    }

    /// <summary>
    /// Gives the wizard each missed reward of a quest finished before its fix.
    /// </summary>
    /// <returns>A chat line for each reward given.</returns>
    internal static List<string> GiveMissedRewards(IActorRef playerActor, Wizard wizard) {
        var lines = new List<string>();
        wizard.RewardRepairs ??= [];
        foreach (var repair in s_repairs) {
            if (wizard.RewardRepairs.Contains(repair.Id) || !wizard.HasCompletedQuest(repair.Quest)) {
                continue;
            }

            if (!Give(playerActor, wizard, repair.Item)) {
                Logger.Warning("Quest reward repair {0}: could not give {1} to wizard {2}; tried again at the next login.",
                    Logger.Args(repair.Id, repair.Item, wizard.CharId));

                continue;
            }

            wizard.RewardRepairs.Add(repair.Id);
            WizardCollection.UpdateCharacterRewardRepairs(wizard);
            Logger.Information("Quest reward repair {0}: gave {1} to wizard {2}.",
                Logger.Args(repair.Id, repair.Item, wizard.CharId));
            lines.Add($"You got the {repair.ItemName} that \"{repair.QuestTitle}\" should have given you.");
        }

        return lines;
    }

    private static bool Give(IActorRef playerActor, Wizard wizard, ulong templateId) {
        switch (CoreObjectFactory.GetCoreTemplate(templateId)) {
            case SpellTemplate { m_Treasure: true } card:
                return LootGranter.GrantTreasureCards(playerActor, wizard, card, (uint) templateId, 1) > 0;
            case ItemTemplate:
                return LootGranter.GrantItem(playerActor, wizard, new DropItemResult { ItemId = templateId.ToString() }, templateId);
            default:
                return false;
        }
    }

}
