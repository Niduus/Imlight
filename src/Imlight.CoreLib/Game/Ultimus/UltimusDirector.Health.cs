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

    // From each area: the areas of its world with a potion shop, or with health or mana wisps, nearest first, found in the
    // background from the areas' files (null while still being read, empty when there are none).
    private static readonly ConcurrentDictionary<string, List<(string Zone, int Hops, bool ViaHub)>> s_healZones = new(StringComparer.OrdinalIgnoreCase);

    // Areas whose wisps were all taken when he got there: not tried again for a while (wisps come back).
    private static readonly TimeSpan s_wispsBackIn = TimeSpan.FromMinutes(5);
    private readonly Dictionary<string, DateTime> _wispsTaken = new(StringComparer.OrdinalIgnoreCase);

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
    // A player asked him to get potions (and he said yes): he goes to fill them until this time (UltimusDirector.Voice.cs).
    private DateTime _potionRunUntil = DateTime.MinValue;

    private bool HealFirst(List<UltimusMember.Status> group, UltimusPlanner.Target target) {
        var leader = group[0];
        _healWaiting = false;
        if (PotionRun(leader)) {
            return true;
        }
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

        // Only the extra care before a tough fight or a dungeon asks for more; by his normal rule he is ready.
        var onlyPrep = !Hurt(leader, out _, out _);
        var stats = leader.Wizard.GameStats;
        var maxHealth = Math.Max(1, stats.m_baseHitpoints + stats.m_bonusHitpoints);
        if (_hurtSince == DateTime.MaxValue) {
            _hurtSince = DateTime.UtcNow;
            Note("Ultimus", $"{leader.Name} {(onlyPrep ? "wants more before a hard fight" : "is hurt")} ({stats.m_currentHitpoints} of {maxHealth} health, " +
                            $"{stats.m_currentMana} of {stats.m_baseMana + stats.m_bonusMana} mana): heals before going on.");
        }

        _lastProgress = DateTime.UtcNow;

        // 1. A potion.
        if (stats.m_potionCharge >= 1) {
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

        // Niduus, 2026-11-14: "Ultimus should only wait for wisps if he's below 25% HP." Above it, with no potion and no wisp
        // right here, he goes on: no trips for wisps or potions and no waiting. The exception is a fight he lost from going
        // in hurt (his "full" remedy, UltimusDirector.Losses.cs).
        var share = stats.m_currentHitpoints / (double) maxHealth;
        var huntBelow = UltimusPlaybook.Value("Quest.HuntHealBelow");
        // A boss is never started at low health (Niduus, 2026-11-14: at 34% right after a battle he set off for Krokenkahmen):
        // under half health, he heals first before a boss, whatever the 25% hunt rule says for ordinary steps.
        var bossNext = BossNext(leader, target) && share < 0.5;
        if (bossNext) {
            Think($"I'm at {share:P0} health and my next fight is a boss. I don't start a boss under half health, so I heal first.",
                "boss-heal|" + (target?.Key ?? ""));
        }

        if (!bossNext && !onlyPrep && !HuntsAt(share) && RemedyFor(target?.Key) != "full") {
            Think($"I'm at {share:P0} health with no potion or wisp right here. I only go looking for wisps or potions under {huntBelow:P0} " +
                  $"({UltimusPlaybook.Cite("Quest.HuntHealBelow")}), so I go on and heal as I go.", "hunt-no|" + (int) (share * 10));
            _hurtSince = DateTime.MaxValue;

            return false;
        }

        // Niduus, 2026-11-14 ("he's not low??" at 875 of 1099): with nothing right here, the extra care is not worth a
        // trip or a wait. He is over his normal ready health, so he goes on.
        if (onlyPrep && RemedyFor(target?.Key) != "full") {
            Think($"I'd like full health for this, but I have {stats.m_currentHitpoints * 100 / maxHealth}% and nothing to heal from right here, " +
                  "which is enough by my normal rule. I go on instead of travelling or waiting.", "prepgo|" + leader.Zone);
            _hurtSince = DateTime.MaxValue;

            return false;
        }

        // 3. Wisps in a nearby area or potions from a potion shop, whichever is fewer areas away (wisps win a tie: free).
        // No wisp left here: this area is not a wisp area to come back to for a while (no going back and forth).
        if (!string.IsNullOrEmpty(leader.Zone)) {
            _wispsTaken[leader.Zone] = DateTime.UtcNow + s_wispsBackIn;
        }

        var shop = PotionShopTarget(leader, out var shopHops);
        var wispArea = WispAreaTarget(leader, lowHealth, lowMana, out var wispHops);
        var pick = HealChoice(false, false, false, shop is null ? -1 : shopHops, wispArea is null ? -1 : wispHops) switch {
            "wisp-area" => wispArea,
            "shop" => shop,
            _ => null,
        };
        // Nothing in this world: the potion shop of Wizard City, however far (Niduus, 2026-11-14: "he should hunt for them
        // and if he can't find any safely then he just goes to a potion vendor"). He marks his spot and Recalls back after.
        pick ??= FarPotionShop(leader);
        if (pick is not null) {
            // The trip is not cut short by the Recall back to his marked spot until he is healed.
            HoldRecall(20);
            _doing = pick.What;
            if (pick.Key?.StartsWith(HUB_KEY, StringComparison.Ordinal) == true) {
                if (!PressHubButton(leader, pick.What)) {
                    // The button can't be pressed here: walk the normal way.
                    WalkTo(0, UltimusPlanner.Travel(leader, pick.Key[HUB_KEY.Length..], pick.What, "heal|" + pick.Key) ?? pick);
                }
            }
            else if (pick.Kind == UltimusPlanner.SHOP && UltimusPlanner.Far(leader.X, leader.Y, pick.X, pick.Y) < SHOP_REACH) {
                StopWalking(0);
                if (CanClick("potion|buy")) {
                    Clicked("potion|buy");
                    Note("Ultimus", $"{leader.Name} fills its potions at the potion shop.");
                    _actors[0].Tell(new UltimusMember.Interact(pick.GlobalId, "PotionShopService", 0, "the potion shop"));
                    _actors[0].Tell(new UltimusMember.BuyPotions());
                    HoldRecall(6);
                }
            }
            else {
                if (pick == wispArea && shop is not null) {
                    Think($"I need to heal: wisps in {UltimusText.Zone(pick.Key.Split('|')[1])} are {wispHops} area(s) away and the potion shop is " +
                          $"{shopHops}, so I go for the wisps (they're free).", "healpick|" + pick.Key);
                }
                else if (pick == shop && wispArea is not null) {
                    Think($"I need to heal: the potion shop is {shopHops} area(s) away and the nearest wisps are {wispHops}, so I buy potions.",
                        "healpick|" + pick.Key);
                }

                WalkTo(0, pick);
            }

            return true;
        }

        // 4. Nothing to heal from (no potion bottles, or no gold for potions, and no wisps): talking and travelling go on, a fight waits.
        if (target?.Kind != UltimusPlanner.FIGHT && !IntoDungeon(leader, target)) {
            return false;
        }

        if (DateTime.UtcNow - _hurtSince > s_noHealGiveUp) {
            if (!_toldNoHeal) {
                _toldNoHeal = true;
                Issue("team", target.Quest, $"{leader.Name} could not heal for {(int) s_noHealGiveUp.TotalMinutes} minutes: no potions, no wisps in " +
                                            $"{UltimusText.Zone(leader.Zone)} or nearby areas and no potion shop it can reach or afford. It fights on hurt.",
                    key: "noheal|" + leader.Zone, broken: false, level: UltimusLink.ABNORMAL);
            }

            return false;
        }

        _doing = "waiting for wisps to come back before the fight (no potions, wisps or potion shop near)";
        _healWaiting = true;
        if (SafeSpot(leader) is { } safe) {
            WalkTo(0, safe);
        } else {
            StopWalking(0);
        }

        return true;
    }

    /// <summary>
    /// What healing comes to, from what there is (-1 = none of it): "potion", "wisp-here", "go-on" (only the extra care
    /// before a hard fight wants more, and nothing is right here), "wisp-area" or "shop" (fewer areas away wins; wisps win
    /// a tie, being free) or "wait". Kept apart so the replay checks can test it.
    /// </summary>
    internal static string HealChoice(bool onlyPrep, bool potion, bool wispHere, int shopHops, int wispHops) {
        if (potion) {
            return "potion";
        }

        if (wispHere) {
            return "wisp-here";
        }

        if (onlyPrep) {
            return "go-on";
        }

        if (wispHops >= 0 && (shopHops < 0 || wispHops <= shopHops)) {
            return "wisp-area";
        }

        return shopHops >= 0 ? "shop" : "wait";
    }

    // The nearest health (or mana) wisp in the area that no creature stands by.
    private static UltimusPlanner.Target Wisp(UltimusMember.Status leader, bool health, bool mana, float safeFromMobs = WISP_SAFE_FROM_MOBS) {
        if (leader.ZoneActor is not { } zone) {
            return null;
        }

        var all = MinimapIndex.In(zone);
        var mobs = all.Where(m => m.Kind is MinimapIndex.MOB or MinimapIndex.BOSS && !CombatDuelComponent.IsInBattle(m.Object.m_globalID.Full))
            .Select(m => (m.Object.m_location.X, m.Object.m_location.Y)).ToList();
        var best = all.Where(m => (health && m.Kind == MinimapIndex.WISP_HEALTH) || (mana && m.Kind == MinimapIndex.WISP_MANA))
            // A battle's wisp that is another player's (BattleWispsPerPlayer) is not his to take.
            .Where(m => Combat.BattleDrops.OwnerOf(m.Object.m_globalID.Full) is var owner && (owner == 0 || owner == leader.GlobalId))
            .Where(m => m.Entity?.GetComponentOfType<WispHealthComponent>() is not { Collected: true }
                        && m.Entity?.GetComponentOfType<WispManaComponent>() is not { Collected: true })
            .Select(m => (Mark: m, Far: UltimusPlanner.Far(leader.X, leader.Y, m.Object.m_location.X, m.Object.m_location.Y)))
            .Where(m => m.Far < WISP_REACH && mobs.All(c => UltimusPlanner.Far(c.X, c.Y, m.Mark.Object.m_location.X, m.Mark.Object.m_location.Y) > safeFromMobs))
            .OrderBy(m => m.Far)
            .Select(m => m.Mark)
            .FirstOrDefault();

        return best is null ? null
            : new UltimusPlanner.Target(UltimusPlanner.SPOT, best.Object.m_location.X, best.Object.m_location.Y,
                best.Kind == MinimapIndex.WISP_HEALTH ? "a health wisp" : "a mana wisp", GlobalId: best.Object.m_globalID.Full,
                Key: "wisp|" + best.Object.m_globalID.Full);
    }

    // The potion shop: the shopkeeper here, or the way to the nearest area of this world with one. Null when its potions
    // can't be filled (no bottles, or not the gold for one). Hops: areas away (0 here).
    private UltimusPlanner.Target PotionShopTarget(UltimusMember.Status leader, out int hops) {
        hops = int.MaxValue;
        var stats = leader.Wizard.GameStats;
        if (stats.m_potionMax < 1 || stats.m_currentGold < PotionService.PotionCostPerBottle(Math.Max(1, stats.Level))) {
            return null;
        }

        var keeper = MinimapIndex.In(leader.ZoneActor).Where(m => m.Entity?.GetComponentOfType<InteractPotionShopComponent>() is not null)
            .OrderBy(m => UltimusPlanner.Far(leader.X, leader.Y, m.Object.m_location.X, m.Object.m_location.Y)).FirstOrDefault();
        if (keeper is not null) {
            hops = 0;

            return new UltimusPlanner.Target(UltimusPlanner.SHOP, keeper.Object.m_location.X, keeper.Object.m_location.Y,
                "filling potions at the potion shop", GlobalId: keeper.Object.m_globalID.Full, Key: "potionshop|" + keeper.Object.m_globalID.Full);
        }

        var areas = HealZones("shop|" + leader.Zone, leader.Zone, name => name.Contains("PotionShopkeeper", StringComparison.Ordinal));
        if (areas?.FirstOrDefault(a => a.Hops > 0) is not { Zone: not null } best) {
            return null;
        }

        hops = best.Hops;

        return best.ViaHub ? HubButton(leader, best.Zone, "to fill potions at the potion shop")
            : UltimusPlanner.Travel(leader, best.Zone, "to fill potions at the potion shop", "potionshop|" + best.Zone);
    }

    /// <summary>Whether at this share of health he goes looking for wisps or potions (Quest.HuntHealBelow, 25%).</summary>
    // The step's fight is against a boss (its creature is marked a boss on the area's map).
    private static bool BossNext(UltimusMember.Status leader, UltimusPlanner.Target target) {
        if (target?.Kind != UltimusPlanner.FIGHT || leader.ZoneActor is not { } zone) {
            return false;
        }

        try {
            return MinimapIndex.In(zone).Any(m => m.Kind == MinimapIndex.BOSS
                && (target.GlobalId != 0 ? m.Object.m_globalID.Full == target.GlobalId : target.Template != 0 && m.Template == target.Template));
        }
        catch {
            return false;
        }
    }

    internal static bool HuntsAt(double share) => share < UltimusPlaybook.Value("Quest.HuntHealBelow");

    /// <summary>A player's "go get potions" he agreed to: off to the nearest potion shop until his potions are full.</summary>
    private bool PotionRun(UltimusMember.Status leader) {
        if (DateTime.UtcNow >= _potionRunUntil || leader.Wizard?.GameStats is not { } stats || CombatDuelComponent.IsInBattle(leader.GlobalId)) {
            return false;
        }

        if (stats.m_potionCharge >= stats.m_potionMax) {
            _potionRunUntil = DateTime.MinValue;
            Note("Ultimus", $"{leader.Name} has full potions again, as asked.");
            return false;
        }

        if (PotionShopTarget(leader, out _) is not { } shop) {
            return false;
        }

        _doing = "getting potions, as a player asked";
        if (shop.Key?.StartsWith(HUB_KEY, StringComparison.Ordinal) == true) {
            PressHubButton(leader, shop.What);
        }
        else if (shop.Kind == UltimusPlanner.SHOP && UltimusPlanner.Far(leader.X, leader.Y, shop.X, shop.Y) < SHOP_REACH) {
            StopWalking(0);
            if (CanClick("potion|buy")) {
                Clicked("potion|buy");
                _actors[0].Tell(new UltimusMember.Interact(shop.GlobalId, "PotionShopService", 0, "the potion shop"));
                _actors[0].Tell(new UltimusMember.BuyPotions());
                HoldRecall(6);
            }
        }
        else {
            WalkTo(0, shop);
        }

        return true;
    }

    /// <summary>
    /// A player asked him to get potions: what he says back, and whether he goes (true) — only when he can really do it.
    /// </summary>
    private (string Line, bool Doing) AskedForPotions(UltimusMember.Status me) {
        if (me.Wizard?.GameStats is not { } stats) {
            return ("Can't right now, sorry!", false);
        }

        if (stats.m_potionMax < 1) {
            return ("I don't have any potion bottles yet, so I can't.", false);
        }

        if (stats.m_potionCharge >= stats.m_potionMax) {
            return ($"My potions are already full ({stats.m_potionCharge} of {stats.m_potionMax})!", false);
        }

        if (stats.m_currentGold < PotionService.PotionCostPerBottle(Math.Max(1, stats.Level))) {
            return ("I don't have the gold for potions right now.", false);
        }

        if (PotionShopTarget(me, out var hops) is null) {
            return ("I don't know a potion shop I can reach from here yet, sorry.", false);
        }

        _potionRunUntil = DateTime.UtcNow.AddMinutes(10);
        Think("A player asked me to get potions. I can, so I go now: the nearest potion shop" + (hops > 0 ? $", {hops} area(s) away." : ", right here."), "potionrun");

        return (hops > 0 ? $"Good idea, heading to the potion shop now ({hops} area{(hops == 1 ? "" : "s")} away)!" : "Good idea, getting potions right here!", true);
    }

    private static string s_farPotionZone;
    private static bool s_farPotionLooked;

    /// <summary>
    /// The potion shop in Wizard City, for when the world he is in has none he can reach and no wisps can be found: he
    /// marks his spot, goes there (the Commons button to this world's hub, then the Spiral Door), fills his potions and
    /// Recalls back. Null when he has no bottles or gold for potions, or no way is known.
    /// </summary>
    private UltimusPlanner.Target FarPotionShop(UltimusMember.Status leader) {
        var stats = leader.Wizard?.GameStats;
        if (stats is null || stats.m_potionMax < 1 || stats.m_currentGold < PotionService.PotionCostPerBottle(Math.Max(1, stats.Level))) {
            return null;
        }

        if (!s_farPotionLooked) {
            s_farPotionLooked = true;
            Task.Run(() => s_farPotionZone = NearestZonesWith("WizardCity/WC_Hub", n => n.Contains("PotionShopkeeper", StringComparison.Ordinal))
                .Select(z => z.Zone).FirstOrDefault());
        }

        if (s_farPotionZone is not { } zone || leader.Zone?.StartsWith("WizardCity/", StringComparison.OrdinalIgnoreCase) == true) {
            return null;
        }

        var why = "to fill potions at the potion shop in Wizard City";
        if (_markedZone is null && leader.Wizard?.ZoneIsInstance != true && (stats.m_currentMana >= 10)) {
            _markedZone = leader.Zone;
            _markedAt = DateTime.UtcNow;
            _actors[0].Tell(new UltimusMember.MarkSpot());
            Note("Ultimus", $"{leader.Name} found no wisps and no potion shop in this world: marks his spot and goes to Wizard City's potion shop.");
            Think("I'm too hurt to fight, there are no wisps I can get safely and this world has no potion shop I can reach. So I mark " +
                  "my spot, go to the potion shop in Wizard City, fill my potions and Recall back. Waiting gets me nowhere.", "farpotion|" + leader.Zone);
        }

        return UltimusPlanner.Travel(leader, zone, why, "potionshop|" + zone) ?? HubButton(leader, zone, why);
    }

    private const string HUB_KEY = "hubbutton|";
    private DateTime _hubPressedAt = DateTime.MinValue;

    // The Commons button press toward <paramref name="zone"/> (its hub first): a target at his own feet, pressed in HealFirst.
    private static UltimusPlanner.Target HubButton(UltimusMember.Status leader, string zone, string why) =>
        new(UltimusPlanner.SPOT, leader.X, leader.Y, $"pressing the Commons button, then on {why} in {UltimusText.Zone(zone)}", Key: HUB_KEY + zone);

    /// <summary>
    /// Presses the game's Commons button (to this world's hub) after marking his spot, so Recall brings him back after
    /// the trip, as a player does. Only out of instances, out of battle, and from level 2 (the game locks it before).
    /// False when he can't press it now.
    /// </summary>
    private bool PressHubButton(UltimusMember.Status leader, string why) {
        if (leader.Wizard is not { } wizard || wizard.ZoneIsInstance || CombatDuelComponent.IsInBattle(leader.GlobalId)
            || (wizard.MagicSchoolBehavior?.Level ?? 1) < 2) {
            return false;
        }

        if (DateTime.UtcNow - _hubPressedAt < TimeSpan.FromSeconds(20)) {
            return true;
        }

        StopWalking(0);
        if (_markedZone is null && (wizard.GameStats?.m_currentMana ?? 0) >= 10) {
            _markedZone = leader.Zone;
            _markedAt = DateTime.UtcNow;
            _actors[0].Tell(new UltimusMember.MarkSpot());
        }

        _hubPressedAt = DateTime.UtcNow;
        _buttonFromZone = leader.Zone;
        Note("Ultimus", $"{leader.Name} presses the Commons button ({why}); his spot is marked to Recall back after.");
        Think($"The Commons button is quicker than walking: I mark my spot, press it, go {why}, then Recall back.", "hub|" + leader.Zone);
        Context.System.Scheduler.ScheduleTellOnce(TimeSpan.FromSeconds(1.5), _actors[0], new UltimusMember.PressCommons(), Self);

        return true;
    }

    private static string WispKey(string zone, bool health, bool mana) => $"wisp|{(health ? "h" : "")}{(mana ? "m" : "")}|{zone}";

    // The nearest other area of this world (on the story's ways) whose file places the wisps it needs, skipping areas
    // whose wisps were all taken a little while ago. Hops: areas away.
    private UltimusPlanner.Target WispAreaTarget(UltimusMember.Status leader, bool health, bool mana, out int hops) {
        hops = int.MaxValue;
        var areas = HealZones(WispKey(leader.Zone, health, mana), leader.Zone, name => name.Contains("Wisp", StringComparison.OrdinalIgnoreCase)
            && (health && name.Contains("Health", StringComparison.OrdinalIgnoreCase) || mana && name.Contains("Mana", StringComparison.OrdinalIgnoreCase)));
        if (areas is null) {
            return null;
        }

        foreach (var (zone, away, viaHub) in areas) {
            if (away == 0 || _wispsTaken.TryGetValue(zone, out var until) && DateTime.UtcNow < until) {
                continue;
            }

            hops = away;
            var why = $"to pick up {(health ? "health" : "mana")} wisps";

            return viaHub ? HubButton(leader, zone, why) : UltimusPlanner.Travel(leader, zone, why, "wispzone|" + zone);
        }

        return null;
    }

    // The areas of this world that hold an object named like <paramref name="has"/>, fewest areas away first (0 = this
    // area). Read once per area in the background; null until then.
    private static List<(string Zone, int Hops, bool ViaHub)> HealZones(string key, string from, Func<string, bool> has) {
        if (string.IsNullOrEmpty(from)) {
            return null;
        }

        if (s_healZones.TryGetValue(key, out var found)) {
            return found;
        }

        if (s_healZones.TryAdd(key, null)) {
            Task.Run(() => s_healZones[key] = NearestZonesWith(from, has));
        }

        return null;
    }

    private static List<(string Zone, int Hops, bool ViaHub)> NearestZonesWith(string from, Func<string, bool> has) {
        var list = new List<(string Zone, int Hops, bool ViaHub)>();
        try {
            var world = from.Split('/')[0];
            // The Commons button (allowed, Niduus 2026-11-14: "he is allowed to use the Commons, Home and mark buttons if
            // convenient in timing and travel"): one press to this world's hub, then on from there.
            ZoneService.ResolveWorldHub(from, out var hub, out _);
            var hubHere = string.IsNullOrEmpty(hub) || OverlayQuestTracker.SameZone(hub, from);
            var candidates = SpiralDB.GetAllZoneData().Select(z => z?.ZoneName)
                .Where(z => !string.IsNullOrEmpty(z) && z.StartsWith(world + "/", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var zone in candidates) {
                // Placed objects, and what the area's spawners put on its paths: Krokotopia's wisps all come on paths, so
                // without these he found no wisps in the whole world and stood waiting at 18% health (Niduus's log, 2026-11-14).
                var info = QuestStepSpots.Zone(zone);
                bool Has(ulong template) => CoreObjectFactory.GetCoreTemplate(template) is GameObjectTemplate t && !t.m_objectName.IsEmpty
                                            && has(t.m_objectName.ToString());
                if (!info.Objects.Any(o => Has(o.Template)) && !info.Spawned.Any(Has)) {
                    continue;
                }

                if (OverlayQuestTracker.SameZone(zone, from)) {
                    list.Add((zone, 0, false));
                    continue;
                }

                // The story's own ways only (stays in bounds: no side areas just for a heal).
                var hops = OverlayQuestRoute.Way(from, zone, false, storyOnly: true).Count;
                var viaHub = hubHere ? 0
                    : OverlayQuestTracker.SameZone(hub, zone) ? 1
                    : OverlayQuestRoute.Way(hub, zone, false, storyOnly: true).Count is > 0 and var fromHub ? 1 + fromHub : 0;
                // The button only when it saves at least two areas of walking.
                if (viaHub > 0 && (hops <= 0 || viaHub + 2 <= hops)) {
                    list.Add((zone, viaHub, true));
                }
                else if (hops > 0) {
                    list.Add((zone, hops, false));
                }
            }
        }
        catch (Exception) {
            // Nothing found is fine: he falls back to waiting.
        }

        return list.OrderBy(a => a.Hops).ToList();
    }

}
