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
 *
 * ========================================================================
 * SECOND CHANCE CHESTS
 * ========================================================================
 *
 * PURPOSE:
 * A boss that cheats (BossCheats.cs) leaves a Second Chance Chest where it was beaten (Niduus, 2026-10-27). The
 * wizards of that battle can open it with crowns for another roll of the boss's own drops, in the game's own paid loot
 * roll window (WizardMessages.xml MSG_PAID_LOOT_ROLL_PROMPT / _RESPONSE / _RESULT, the window the game's MonsterChest
 * objects open through their PaidLootRollService).
 *
 * NOTE:
 * Imlight.ini [SecondChanceChest] (Enabled, Free, Cost, Uses, Seconds, ChestTemplate), read each time, so a change
 * applies from the next chest or the next open. The member benefit Second Chance Chest makes each wizard's first open
 * of a chest free, as the game's "Member Bonus - One Free Second Chance Chest". The game's chests name loot tables
 * that live on the real servers, so the chest rolls the boss's own drop tables (the ones its battle rolled) instead.
 * Offers are runtime state and are lost with the server.
 *
 * Created by: Niduus
 * Version: KALI 1.0
 * Last Updated: 10/27/2026
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.MessageLayer.Generated;
using Imcodec.Math;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.DropTables;

internal static class SecondChanceChests {

    // Marks a Second Chance Chest (as its zone tag), so its component knows it is one of ours.
    internal const string CHEST_TAG = "SecondChanceChest";
    internal const string MEMBER_BENEFIT = "SecondChanceChestBonus";

    private const string DEFAULT_CHEST = "WC_MonsterChest_GeneralAkilles";
    private const int ROLL_TRIES = 5;

    internal static bool Enabled => ConfigurationManager.GetValue("SecondChanceChest.Enabled", true);
    internal static bool Free => ConfigurationManager.GetValue("SecondChanceChest.Free", false);
    internal static int Cost => Math.Clamp(ConfigurationManager.GetValue("SecondChanceChest.Cost", 150), 0, 1_000_000);
    internal static int Uses => Math.Clamp(ConfigurationManager.GetValue("SecondChanceChest.Uses", 1), 1, 100);
    internal static TimeSpan Lifetime
        => TimeSpan.FromSeconds(Math.Clamp(ConfigurationManager.GetValue("SecondChanceChest.Seconds", 300), 10, 3600));

    private static string ChestName => ConfigurationManager.GetValue("SecondChanceChest.ChestTemplate", DEFAULT_CHEST);

    /// <summary>One chest: the bosses whose drops it rolls, who may open it and how often each one did.</summary>
    internal sealed class Offer {
        internal required ulong[] BossKeys;
        internal required HashSet<ulong> CharIds;
        internal readonly Dictionary<ulong, int> Opened = [];
        internal readonly DateTime Made = DateTime.UtcNow;
    }

    private static readonly ConcurrentDictionary<ulong, Offer> s_offers = new();
    private static readonly ConcurrentDictionary<string, uint> s_templates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The bosses of a won battle that cheat: those whose cheat list has one that is on.</summary>
    internal static ulong[] CheatingBosses(IEnumerable<ulong> lootKeys)
        => [.. (lootKeys ?? []).Distinct().Where(key => BossCheats.For(key).Any(c => c is { Off: false }))];

    /// <summary>
    /// Leaves a Second Chance Chest at <paramref name="location"/> when one of the beaten creatures is a boss that
    /// cheats. Only the wizards of <paramref name="charIds"/> can open it (anyone in the zone when that list is empty).
    /// </summary>
    internal static void Spawn(IActorRef zoneRef, Vector3 location, IEnumerable<ulong> lootKeys, IEnumerable<ulong> charIds) {
        try {
            if (!Enabled || zoneRef is null) {
                return;
            }

            var bosses = CheatingBosses(lootKeys);
            if (bosses.Length == 0) {
                return;
            }

            var templateId = ChestTemplate();
            if (templateId == 0 || CoreObjectFactory.GetCoreTemplate(templateId) is not GameObjectTemplate template) {
                return;
            }

            var info = new CoreObjectInfo {
                m_templateID = templateId,
                m_fScale = 1.0f,
                m_zoneTag = CHEST_TAG,
                m_location = location,
            };

            var chest = CoreObjectFactory.FinalizeCoreObject(info, template);
            if (chest is null) {
                return;
            }

            chest = CoreObjectFactory.InitializeCoreObjectBehaviors(chest, template);
            Forget();
            s_offers[chest.m_globalID.Full] = new Offer { BossKeys = bosses, CharIds = [.. charIds ?? []] };
            zoneRef.Tell(new ZONE_102_PROTOCOL.MSG_SPAWNENTITY { CoreObject = chest, Template = template });

            Logger.Information("Second Chance Chest {0} left for {1}.",
                Logger.Args(chest.m_globalID.Full, string.Join(", ", bosses.Select(BossCheats.NameOf))));
        }
        catch (Exception ex) {
            Logger.Error("A Second Chance Chest could not be left: {0}", Logger.Args(ex));
        }
    }

    internal static bool IsChest(ulong chestId) => s_offers.ContainsKey(chestId);

    internal static void Remove(ulong chestId) => s_offers.TryRemove(chestId, out _);

    /// <summary>How many more times <paramref name="charId"/> can open the chest (0 when it is not theirs).</summary>
    internal static int UsesLeft(ulong chestId, ulong charId) {
        if (!s_offers.TryGetValue(chestId, out var offer)) {
            return 0;
        }

        lock (offer) {
            if (offer.CharIds.Count > 0 && !offer.CharIds.Contains(charId)) {
                return 0;
            }

            return Math.Max(0, Uses - offer.Opened.GetValueOrDefault(charId));
        }
    }

    /// <summary>
    /// What the next open costs <paramref name="charId"/>: nothing when [SecondChanceChest] Free is on, or for the
    /// first open while the member benefit Second Chance Chest is on.
    /// </summary>
    internal static int CostFor(ulong chestId, ulong charId) {
        if (Free) {
            return 0;
        }

        var opened = 0;
        if (s_offers.TryGetValue(chestId, out var offer)) {
            lock (offer) {
                opened = offer.Opened.GetValueOrDefault(charId);
            }
        }

        return opened == 0 && MemberBenefits.IsOn(MEMBER_BENEFIT) ? 0 : Cost;
    }

    /// <summary>Opens the game's paid loot roll window for the chest.</summary>
    internal static void Prompt(IActorRef playerActor, Wizard wizard, ulong chestId) {
        var uses = UsesLeft(chestId, wizard.CharId);
        if (uses <= 0) {
            return;
        }

        playerActor.Tell(new WIZARD_12_PROTOCOL.MSG_PAID_LOOT_CROWNS_BALANCE { Balance = wizard.Account?.Crowns ?? 0 });
        playerActor.Tell(new WIZARD_12_PROTOCOL.MSG_PAID_LOOT_ROLL_PROMPT {
            Id = chestId,
            Cost = CostFor(chestId, wizard.CharId),
            Uses = uses,
        });
    }

    /// <summary>
    /// Opens the chest once for the wizard: takes the crowns, rolls the bosses' drops and grants them. Returns why it
    /// did not, or null.
    /// </summary>
    internal static string Open(IActorRef playerActor, CoreObject playerObject, Wizard wizard, ulong chestId) {
        if (!s_offers.TryGetValue(chestId, out var offer)) {
            return "This Second Chance Chest is gone.";
        }

        var account = wizard.Account;
        int cost;
        lock (offer) {
            if (UsesLeft(chestId, wizard.CharId) <= 0) {
                return "You have already opened this Second Chance Chest.";
            }

            cost = CostFor(chestId, wizard.CharId);
            if (cost > 0 && (account is null || account.Crowns < cost)) {
                return $"You need {cost} Crowns to open the Second Chance Chest.";
            }

            offer.Opened[wizard.CharId] = offer.Opened.GetValueOrDefault(wizard.CharId) + 1;
        }

        if (cost > 0 && !account.SetCrowns(account.Crowns - cost)) {
            lock (offer) {
                offer.Opened[wizard.CharId]--;
            }

            return "Your Crowns could not be taken, so the chest stayed shut. Please try again.";
        }

        var loot = Roll(offer.BossKeys, playerActor, playerObject, wizard);
        LootGranter.GrantAndDisplay(playerActor, wizard, loot);

        if (cost > 0) {
            playerActor.Tell(new WIZARD_12_PROTOCOL.MSG_CROWNBALANCE {
                Failure = 0,
                TotalCrowns = account.Crowns,
                CharacterID = wizard.CharId,
                CacheBalanceForCSSegmentation = 1,
            });
        }

        LootGranter.SendPaidLootRollResult(playerActor, chestId, cost, account?.Crowns ?? 0,
            UsesLeft(chestId, wizard.CharId), loot);
        Logger.Information("Second Chance Chest {0} opened by {1} for {2} Crowns: {3} item(s), {4} gold.", Logger.Args(
            chestId, wizard.PlayerNameBehavior?.GetWizardName() ?? wizard.CharId.ToString(), cost, loot.Items.Count, loot.GoldAmount));

        return null;
    }

    // The bosses' own drop tables, rolled until something other than gold comes out (a few tries).
    private static DropTableResult Roll(ulong[] bossKeys, IActorRef playerActor, CoreObject playerObject, Wizard wizard) {
        var tables = bossKeys.SelectMany(DropTablesOf).Distinct().ToArray();
        DropTableResult loot = null;
        for (var i = 0; i < ROLL_TRIES && tables.Length > 0; i++) {
            loot = DropTableRoller.Roll(tables, playerActor, playerObject, wizard);
            if (loot.Items.Count > 0) {
                break;
            }
        }

        loot ??= new DropTableResult();
        if (!loot.HasRewards) {
            loot.GoldAmount = 50 + Random.Shared.Next(0, 101);
        }

        return loot;
    }

    /// <summary>The drop tables a beaten creature rolls: a custom's or a Bosses tab boss's own Drops, else its creature's.</summary>
    internal static string[] DropTablesOf(ulong key) {
        var customDrops = BossOverrides.For(key)?.Drops;

        return customDrops is { Count: > 0 } ? [.. customDrops]
            : NpcDropTableCollection.TryGetDropTable(Customs.BaseOf(key), out var npcDropTable) ? [.. npcDropTable.DropTableNames]
            : [];
    }

    // Chests whose time ran out but whose object never said so (the zone closed first).
    private static void Forget() {
        var oldest = DateTime.UtcNow - TimeSpan.FromHours(2);
        foreach (var entry in s_offers.Where(e => e.Value.Made < oldest)) {
            s_offers.TryRemove(entry);
        }
    }

    // [SecondChanceChest] ChestTemplate: a template id, or the file name of a chest template (WC_MonsterChest_GeneralAkilles).
    private static uint ChestTemplate() {
        var name = (ChestName ?? "").Trim();
        if (name.Length == 0) {
            name = DEFAULT_CHEST;
        }

        if (uint.TryParse(name, out var id)) {
            return id;
        }

        return s_templates.GetOrAdd(name, FindTemplate);
    }

    private static uint FindTemplate(string name) {
        var wanted = name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? name : name + ".xml";
        var found = CoreObjectFactory.TemplateManifest?.m_serializedTemplates?
            .Where(t => t?.m_filename is not null)
            .FirstOrDefault(t => ((string) t.m_filename).Replace('\\', '/').Split('/').Last()
                .Equals(wanted, StringComparison.OrdinalIgnoreCase));
        if (found is not null && CoreObjectFactory.GetCoreTemplate(found.m_id) is GameObjectTemplate) {
            Logger.Information("Second Chance Chest template: {0} ({1}).", Logger.Args(name, found.m_id));

            return found.m_id;
        }

        Logger.Warning("No Second Chance Chest template {0} was found, so cheating bosses leave no chest.", Logger.Args(name));

        return 0;
    }

}
