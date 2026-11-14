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
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Packs;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Battlegrounds;

/*
 * What a Beastmoon match gives. The game files hold no loot for it (the PvE BattlegroundTemplates name no
 * m_lootWinner, and the live loot tables are not in the client), so the amounts are [Beastmoon] settings, set after
 * a recorded PvE match: chat lines "You received: 17 Battle Coins", Lunari and Hunter's Moonstones from the monsters.
 * - Each monster beaten in an arena: Battle Coins, Lunari and a chance at a Hunter's Moonstone, by its rank (the PvE
 *   creatures are BG_x_MobNNN_Normal / _Elite / _Boss in ObjectData/Battlegrounds/BG_PVE).
 * - The end of a match: more for a win than a loss.
 * The Hunter's Moonstone is the reagent the forms ask for to level up (PolymorphBattlegroundsBehaviorTemplate
 * m_levelUpIngredients). The game shows all of it in its loot popup (MSG_LOOT: EventCurrency2LootInfo is Battle Coins,
 * EventCurrency1LootInfo Lunari).
 */
internal static class BeastRewards {

    internal enum Rank : byte { Normal, Elite, Boss }

    private static int Setting(string key, int fallback) => Math.Max(0, ConfigurationManager.GetValue("Beastmoon." + key, fallback));

    private static (int Coins, int Lunari, int MoonstonePercent) PerRank(Rank rank) => rank switch {
        Rank.Boss => (Setting("BossCoins", 17), Setting("BossLunari", 10), Setting("BossMoonstonePercent", 100)),
        Rank.Elite => (Setting("EliteCoins", 8), Setting("EliteLunari", 4), Setting("EliteMoonstonePercent", 35)),
        _ => (Setting("NormalCoins", 3), Setting("NormalLunari", 1), Setting("NormalMoonstonePercent", 10)),
    };

    internal static Rank RankOf(string objectName)
        => objectName is null ? Rank.Normal
            : objectName.Contains("_Boss", StringComparison.OrdinalIgnoreCase) ? Rank.Boss
            : objectName.Contains("_Elite", StringComparison.OrdinalIgnoreCase) ? Rank.Elite
            : Rank.Normal;

    private static uint s_moonstoneId;

    /// <summary>The Hunter's Moonstone: the reagent the beast forms level up with, or 0.</summary>
    internal static uint MoonstoneId {
        get {
            if (s_moonstoneId != 0) {
                return s_moonstoneId;
            }

            foreach (var name in Battlegrounds.FormNames()) {
                var ingredient = Battlegrounds.Form(name, out _)?.m_behaviors?.OfType<PolymorphBattlegroundsBehaviorTemplate>()
                    .SelectMany(b => b.m_levelUpIngredients ?? [])
                    .FirstOrDefault(i => i is not null && i.m_ingredientType == INGREDIENT_TYPE.INGREDIENT_REAGENT);
                if (ingredient is not null) {
                    s_moonstoneId = (uint) ingredient.m_itemID.Full;
                    Logger.Information("Beastmoon | the forms level up with reagent {0}.", Logger.Args(s_moonstoneId));

                    break;
                }
            }

            return s_moonstoneId;
        }
    }

    /// <summary>A battle won in an arena: what each monster beaten gives.</summary>
    /// <param name="wizardsInBattle">How many wizards won it: each one's session reports the win, so the team's points
    /// for the monsters are shared out among them.</param>
    internal static void BattleWon(IActorRef session, Wizard wizard, IEnumerable<ulong> mobTemplateIds, IActorRef zone = null,
                                   int wizardsInBattle = 1) {
        if (wizard is null || !Battlegrounds.Enabled || !Battlegrounds.IsBattlegroundZone(wizard.Zone) || mobTemplateIds is null) {
            return;
        }

        int coins = 0, lunari = 0, moonstones = 0, points = 0;
        foreach (var id in mobTemplateIds) {
            var name = (CoreObjectFactory.GetCoreTemplate(id) as GameObjectTemplate)?.m_objectName.ToString();
            points += BattlegroundScore.MonsterPoints(RankOf(name));
            var (c, l, m) = PerRank(RankOf(name));
            coins += c;
            lunari += l;
            if (Random.Shared.Next(100) < m) {
                moonstones++;
            }
        }

        Give(session, wizard, coins, lunari, moonstones, "a battle");
        // The match's score: defeating opponents earns points (BattlegroundScore).
        BattlegroundScore.Give(zone, Battlegrounds.Of(wizard.CharId)?.Team ?? Battlegrounds.Team.Red,
            (int) Math.Ceiling(points / (double) Math.Max(1, wizardsInBattle)));
    }

    /// <summary>The match is over: the wizard's share for winning or losing it.</summary>
    internal static void MatchOver(IActorRef session, Wizard wizard, bool won) {
        if (wizard is null) {
            return;
        }

        var coins = won ? Setting("WinCoins", 20) : Setting("LossCoins", 5);
        var lunari = won ? Setting("WinLunari", 25) : Setting("LossLunari", 5);
        var moonstones = won ? Setting("WinMoonstones", 2) : Setting("LossMoonstones", 0);
        Give(session, wizard, coins, lunari, moonstones, won ? "a won match" : "a match");
    }

    /// <summary>A Coin Clam opened in an arena (Results/Handlers/ResBattlegroundHandlers.cs ResLoot).</summary>
    internal static void CoinClam(IActorRef session, Wizard wizard) {
        if (wizard is null || !Battlegrounds.IsBattlegroundZone(wizard.Zone)) {
            return;
        }

        Give(session, wizard, Setting("CoinClamCoins", 15), Setting("CoinClamLunari", 0), 0, "a Coin Clam");
    }

    private static void Give(IActorRef session, Wizard wizard, int coins, int lunari, int moonstones, string why) {
        var loot = new List<LootInfo>();
        if (coins > 0 && BeastmoonProgress.AddBattleCoins(wizard, session, coins) is var added and > 0) {
            loot.Add(new EventCurrency2LootInfo { m_lootType = LOOT_TYPE.LOOT_TYPE_EVENT_CURRENCY_2, m_eventCurrency2Amount = added });
        }

        if (lunari > 0) {
            wizard.AddLunari(lunari);
            session.Tell(new WIZARD2_53_PROTOCOL.MSG_UPDATEEVENTCURRENCY1 {
                EventCurrency1 = wizard.GameStats.m_currentEventCurrency1,
                MaxEventCurrency1 = wizard.GameStats.m_baseEventCurrency1Pouch,
            });
            loot.Add(new EventCurrency1LootInfo { m_lootType = LOOT_TYPE.LOOT_TYPE_EVENT_CURRENCY_1, m_eventCurrency1Amount = lunari });
        }

        var moonstoneId = moonstones > 0 ? MoonstoneId : 0;
        if (moonstoneId != 0 && CoreObjectFactory.GetCoreTemplate(moonstoneId) is { } moonstone) {
            for (var i = 0; i < moonstones; i++) {
                PackManager.GrantDroppedItem(session, wizard, moonstoneId, moonstone);
            }

            loot.Add(new ReagentLootInfo { m_lootType = LOOT_TYPE.LOOT_TYPE_REAGENT, m_itemID = moonstoneId, m_numItems = moonstones });
        }

        if (loot.Count == 0) {
            return;
        }

        var list = new LootInfoList { m_loot = loot };
        if (new ObjectSerializer(Versionable: false).Serialize(list, 4, out var data)) {
            session.Tell(new WIZARD_12_PROTOCOL.MSG_LOOT { GlobalID = wizard.GameObjectID, LootList = data });
        }

        Logger.Information("Beastmoon | {0} got {1} Battle Coins, {2} Lunari and {3} Hunter's Moonstones for {4}.",
            Logger.Args(wizard.CharId, coins, lunari, moonstoneId == 0 ? 0 : moonstones, why));
    }

}
