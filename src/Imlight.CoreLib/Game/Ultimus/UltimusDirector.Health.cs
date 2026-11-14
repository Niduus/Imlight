/*
 * Imlight
 * Copyright (C) 2025 Revive101
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
using System.Linq;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Healing before anything else (Niduus, 2026-11-13: "Ultimus is rejoining battles when it has no health or low health.
/// Have it prioritize healing before questing or let alone battling"). The game gives health back no other way than a
/// player gets it: a potion; a health or mana wisp; or a potion bought at a potion shop (any world's PotionShopkeeper),
/// walking to the one in the area or travelling to the nearest area of the world that has one. Hurt and with nothing to
/// heal from, it still talks and travels (walking round creatures), but it does not start a fight; after a long wait with
/// no way at all to heal it is written up pink and fights on, as it would otherwise never move again.
/// </summary>
internal sealed partial class UltimusDirectorActor {

    private static readonly TimeSpan s_noHealGiveUp = TimeSpan.FromMinutes(10);
    private const float WISP_REACH = 5000f;
    private const float WISP_SAFE_FROM_MOBS = 500f;
    private const float SHOP_REACH = 220f;

    // Each world's nearest area with a potion shop, found in the background from the areas' files ("" = none).
    private static readonly ConcurrentDictionary<string, string> s_potionShopZones = new(StringComparer.OrdinalIgnoreCase);

    private DateTime _hurtSince = DateTime.MaxValue;
    private bool _toldNoHeal;

    // Standing still on purpose until it can heal: not "stuck".
    private bool _healWaiting;

    private static bool Hurt(UltimusMember.Status member, out bool lowHealth, out bool lowMana, bool tough = false) {
        lowHealth = lowMana = false;
        if (member.Wizard?.GameStats is not { } stats) {
            return false;
        }

        // A creature that beat it before, or that takes most of its health, is met at full health (UltimusFoes).
        // Bold (a winning streak), he rests a little less before a step (UltimusMood).
        var ready = tough ? UltimusPlaybook.Value("Quest.ToughFoeHealth")
            : UltimusPlaybook.Value("Quest.ReadyHealth") - (UltimusMood.Now == UltimusMood.Level.Bold ? UltimusPlaybook.Value("Mood.BoldHealShift") * 2 : 0);
        lowHealth = stats.m_currentHitpoints < Math.Max(1, stats.m_baseHitpoints + stats.m_bonusHitpoints) * ready;
        lowMana = stats.m_currentMana < Math.Max(1, stats.m_baseMana + stats.m_bonusMana) * UltimusPlaybook.Value(tough ? "Quest.ToughFoeMana" : "Quest.ReadyMana");

        return lowHealth || lowMana;
    }

    /// <summary>
    /// True when healing comes first this moment (the leader walks to a wisp or a potion shop, drinks, or waits instead of
    /// starting a fight); false when it may go on with <paramref name="target"/>.
    /// </summary>
    private bool HealFirst(List<UltimusMember.Status> group, UltimusPlanner.Target target) {
        var leader = group[0];
        _healWaiting = false;
        // Before walking into a dungeon he wants more (UltimusDirector.Sense.cs: a dungeon is done in one go).
        var hurt = Hurt(leader, out var lowHealth, out var lowMana, ToughFoeNow(target));
        if (!hurt && DungeonPrep(leader, target, out var dungeonHealth, out var dungeonMana)) {
            (hurt, lowHealth, lowMana) = (true, dungeonHealth, dungeonMana);
        }

        if (CombatDuelComponent.IsInBattle(leader.GlobalId) || !hurt) {
            _hurtSince = DateTime.MaxValue;
            _toldNoHeal = false;

            return false;
        }

        if (_hurtSince == DateTime.MaxValue) {
            _hurtSince = DateTime.UtcNow;
            var stats = leader.Wizard.GameStats;
            Note("Ultimus", $"{leader.Name} is hurt ({stats.m_currentHitpoints} of {stats.m_baseHitpoints + stats.m_bonusHitpoints} health, " +
                            $"{stats.m_currentMana} of {stats.m_baseMana + stats.m_bonusMana} mana): heals before going on.");
        }

        _lastProgress = DateTime.UtcNow;

        // 1. A potion.
        if (leader.Wizard.GameStats.m_potionCharge >= 1) {
            if (CanClick("potion|heal")) {
                Clicked("potion|heal");
                Note("Ultimus", $"{leader.Name} drinks a potion to heal.");
                _actors[0].Tell(new UltimusMember.UsePotion());
            }

            _doing = "drinking a potion";

            return true;
        }

        // 2. A wisp in the area, one not among creatures.
        if (Wisp(leader, lowHealth, lowMana) is { } wisp) {
            _doing = $"picking up {wisp.What}";
            WalkTo(0, wisp);

            return true;
        }

        // 3. Potions from a potion shop, here or in the nearest area of the world that has one.
        if (PotionShopTarget(leader) is { } shop) {
            _doing = shop.What;
            if (shop.Kind == UltimusPlanner.SHOP && UltimusPlanner.Far(leader.X, leader.Y, shop.X, shop.Y) < SHOP_REACH) {
                StopWalking(0);
                if (CanClick("potion|buy")) {
                    Clicked("potion|buy");
                    Note("Ultimus", $"{leader.Name} fills its potions at the potion shop.");
                    _actors[0].Tell(new UltimusMember.Interact(shop.GlobalId, "PotionShopService", 0, "the potion shop"));
                    _actors[0].Tell(new UltimusMember.BuyPotions());
                }
            }
            else {
                WalkTo(0, shop);
            }

            return true;
        }

        // 4. Nothing to heal from: talking and travelling go on, a fight waits.
        if (target?.Kind != UltimusPlanner.FIGHT && !IntoDungeon(leader, target)) {
            return false;
        }

        if (DateTime.UtcNow - _hurtSince > s_noHealGiveUp) {
            if (!_toldNoHeal) {
                _toldNoHeal = true;
                Issue("team", target.Quest, $"{leader.Name} could not heal for {(int) s_noHealGiveUp.TotalMinutes} minutes: no potions, no wisps in " +
                                            $"{UltimusText.Zone(leader.Zone)} and no potion shop it can reach or afford. It fights on hurt.",
                    key: "noheal|" + leader.Zone, broken: false, level: UltimusLink.ABNORMAL);
            }

            return false;
        }

        _doing = "waiting to heal before the fight (no potions, wisps or potion shop)";
        _healWaiting = true;
        if (SafeSpot(leader) is { } safe) {
            WalkTo(0, safe);
        } else {
            StopWalking(0);
        }

        return true;
    }

    // The nearest health (or mana) wisp in the area that no creature stands by.
    private static UltimusPlanner.Target Wisp(UltimusMember.Status leader, bool health, bool mana) {
        if (leader.ZoneActor is not { } zone) {
            return null;
        }

        var all = MinimapIndex.In(zone);
        var mobs = all.Where(m => m.Kind is MinimapIndex.MOB or MinimapIndex.BOSS && !CombatDuelComponent.IsInBattle(m.Object.m_globalID.Full))
            .Select(m => (m.Object.m_location.X, m.Object.m_location.Y)).ToList();
        var best = all.Where(m => (health && m.Kind == MinimapIndex.WISP_HEALTH) || (mana && m.Kind == MinimapIndex.WISP_MANA))
            .Where(m => m.Entity?.GetComponentOfType<WispHealthComponent>() is not { Collected: true }
                        && m.Entity?.GetComponentOfType<WispManaComponent>() is not { Collected: true })
            .Select(m => (Mark: m, Far: UltimusPlanner.Far(leader.X, leader.Y, m.Object.m_location.X, m.Object.m_location.Y)))
            .Where(m => m.Far < WISP_REACH && mobs.All(c => UltimusPlanner.Far(c.X, c.Y, m.Mark.Object.m_location.X, m.Mark.Object.m_location.Y) > WISP_SAFE_FROM_MOBS))
            .OrderBy(m => m.Far)
            .Select(m => m.Mark)
            .FirstOrDefault();

        return best is null ? null
            : new UltimusPlanner.Target(UltimusPlanner.SPOT, best.Object.m_location.X, best.Object.m_location.Y,
                best.Kind == MinimapIndex.WISP_HEALTH ? "a health wisp" : "a mana wisp", GlobalId: best.Object.m_globalID.Full,
                Key: "wisp|" + best.Object.m_globalID.Full);
    }

    // The potion shop: the shopkeeper here, or the way to the nearest area of this world with one. Null when its potions
    // can't be filled (no bottles, or not the gold for one).
    private UltimusPlanner.Target PotionShopTarget(UltimusMember.Status leader) {
        var stats = leader.Wizard.GameStats;
        if (stats.m_potionMax < 1 || stats.m_currentGold < PotionService.PotionCostPerBottle(Math.Max(1, stats.Level))) {
            return null;
        }

        var keeper = MinimapIndex.In(leader.ZoneActor).Where(m => m.Entity?.GetComponentOfType<InteractPotionShopComponent>() is not null)
            .OrderBy(m => UltimusPlanner.Far(leader.X, leader.Y, m.Object.m_location.X, m.Object.m_location.Y)).FirstOrDefault();
        if (keeper is not null) {
            return new UltimusPlanner.Target(UltimusPlanner.SHOP, keeper.Object.m_location.X, keeper.Object.m_location.Y,
                "filling potions at the potion shop", GlobalId: keeper.Object.m_globalID.Full, Key: "potionshop|" + keeper.Object.m_globalID.Full);
        }

        var world = (leader.Zone ?? "").Split('/')[0];
        if (!s_potionShopZones.TryGetValue(world, out var shopZone)) {
            s_potionShopZones.TryAdd(world, null);
            var from = leader.Zone;
            Task.Run(() => s_potionShopZones[world] = NearestPotionShopZone(from));

            return null;
        }

        if (string.IsNullOrEmpty(shopZone) || OverlayQuestTracker.SameZone(shopZone, leader.Zone)) {
            return null;
        }

        return UltimusPlanner.Travel(leader, shopZone, "to fill potions at the potion shop", "potionshop|" + shopZone);
    }

    // The area of the world with a potion shopkeeper fewest areas away (read from the areas' files once).
    private static string NearestPotionShopZone(string from) {
        try {
            var world = from.Split('/')[0];
            var candidates = SpiralDB.GetAllZoneData().Select(z => z?.ZoneName)
                .Where(z => !string.IsNullOrEmpty(z) && z.StartsWith(world + "/", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var best = (Zone: "", Hops: int.MaxValue);
            foreach (var zone in candidates) {
                var objects = QuestStepSpots.Zone(zone).Objects;
                if (!objects.Any(o => CoreObjectFactory.GetCoreTemplate(o.Template) is GameObjectTemplate t && !t.m_objectName.IsEmpty
                                      && t.m_objectName.ToString().Contains("PotionShopkeeper", StringComparison.Ordinal))) {
                    continue;
                }

                var hops = OverlayQuestRoute.Way(from, zone, false).Count;
                if (hops > 0 && hops < best.Hops) {
                    best = (zone, hops);
                }
            }

            return best.Zone;
        }
        catch (Exception) {
            return "";
        }
    }

}
