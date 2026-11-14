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
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Game.Bazaar;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.Zone.Components;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Ultimus lives like a player (Niduus, 2026-11-14, "go for it", with: "He is not allowed to pet train or craft. He can
/// buy gear from the Bazaar though"):
/// <list type="bullet">
/// <item>Pacing: a short pause after each step, dialogs read at a reading pace, and a small wobble in his walk.</item>
/// <item>Backpack sense: with little room left (Backpack.Room) he sells the gear he will never wear at a shop, as a player
/// does; quest items and anything worn are kept. Nothing is removed for him.</item>
/// <item>Gold planning: no gold is given to him. He keeps a reserve for the next world's gear (Gold.ReservePerLevel) and
/// says what he saves for.</item>
/// <item>Gear check at each new world, from shops and the Bazaar (in Olde Town); never crafting.</item>
/// <item>A spell training plan for each world, with the reason for each spell (training-plan.txt).</item>
/// <item>Learning from a lost battle: why it was lost and what he changes next time.</item>
/// <item>Menu Chat personality: he remembers the players he meets (players.json) and greets them again.</item>
/// </list>
/// </summary>
internal sealed partial class UltimusDirectorActor {

    // ---------------------------------------------------------------------
    // Pacing
    // ---------------------------------------------------------------------

    private DateTime _pauseUntil = DateTime.MinValue;
    private readonly Random _paceRandom = new();

    /// <summary>A step done: he takes a moment, as a player reads the quest book's next line.</summary>
    private void PauseAfterStep() {
        var most = UltimusPlaybook.Value("Pace.StepPause");
        if (most <= 0) {
            return;
        }

        _pauseUntil = DateTime.UtcNow + TimeSpan.FromSeconds(most * (0.4 + (_paceRandom.NextDouble() * 0.6)));
    }

    /// <summary>True while he takes his moment (he stands still).</summary>
    private bool Pausing() {
        if (DateTime.UtcNow >= _pauseUntil) {
            return false;
        }

        _doing = "takes a moment, as a player reads the next step";
        _lastProgress = DateTime.UtcNow;

        return true;
    }

    // Corners are rounded off by at most this much (approved 2026-11-14, "smoother movement: curves").
    private const float CURVE_MOST = 45f;

    /// <summary>
    /// Rounded corners: each turn of the walk is cut a little (a quarter of each side, at most CURVE_MOST), as a player
    /// curves round a corner instead of stopping on it and turning. A cut that would leave the floor keeps the corner
    /// (Niduus's routes rule: legal ground only).
    /// </summary>
    private static List<(float X, float Y)> Smooth(string zone, List<(float X, float Y)> points) {
        if (points.Count < 3 || string.IsNullOrEmpty(zone)) {
            return points;
        }

        var smooth = new List<(float X, float Y)>(points.Count * 2) { points[0] };
        for (var i = 1; i < points.Count - 1; i++) {
            var (a, b, c) = (points[i - 1], points[i], points[i + 1]);
            var inLen = UltimusPlanner.Far(a.X, a.Y, b.X, b.Y);
            var outLen = UltimusPlanner.Far(b.X, b.Y, c.X, c.Y);
            var cutIn = Math.Min(CURVE_MOST, inLen * 0.25f);
            var cutOut = Math.Min(CURVE_MOST, outLen * 0.25f);
            if (inLen < 1 || outLen < 1) {
                smooth.Add(b);
                continue;
            }

            var p = (X: b.X + ((a.X - b.X) / inLen * cutIn), Y: b.Y + ((a.Y - b.Y) / inLen * cutIn));
            var q = (X: b.X + ((c.X - b.X) / outLen * cutOut), Y: b.Y + ((c.Y - b.Y) / outLen * cutOut));
            var mid = ((p.X + q.X) / 2, (p.Y + q.Y) / 2);
            if (OverlayQuestPath.OnFloor(zone, p.X, p.Y) && OverlayQuestPath.OnFloor(zone, q.X, q.Y) && OverlayQuestPath.OnFloor(zone, mid.Item1, mid.Item2)) {
                smooth.Add(p);
                smooth.Add(q);
            }
            else {
                smooth.Add(b);
            }
        }

        smooth.Add(points[^1]);

        return smooth;
    }

    /// <summary>
    /// A clean stop at a person (a talk, a shop, a trainer): the walk ends a little before them, facing them, instead of
    /// walking into them (approved 2026-11-14, "clean stops at NPCs").
    /// </summary>
    private static List<(float X, float Y)> StopShort(List<(float X, float Y)> points, UltimusPlanner.Target target) {
        if (target?.Kind is not (UltimusPlanner.TALK or UltimusPlanner.OFFER or UltimusPlanner.SHOP or UltimusPlanner.TRAIN) || points.Count < 2) {
            return points;
        }

        var end = points[^1];
        var before = points[^2];
        var len = UltimusPlanner.Far(before.X, before.Y, end.X, end.Y);
        if (UltimusPlanner.Far(end.X, end.Y, target.X, target.Y) > 80 || len < 1) {
            return points;
        }

        const float SHORT = 60f;
        var trimmed = new List<(float X, float Y)>(points);
        trimmed[^1] = len > SHORT + 10 ? (end.X + ((before.X - end.X) / len * SHORT), end.Y + ((before.Y - end.Y) / len * SHORT)) : before;

        return trimmed;
    }

    /// <summary>
    /// A small wobble in a walk's middle points (Pace.Wobble, at most a few steps; the ends stay), so he does not walk
    /// like a ruler. Kept small: the way stays on the legal walk (Niduus's routes rule).
    /// </summary>
    private List<(float X, float Y)> Wobble(List<(float X, float Y)> points) {
        var size = (float) UltimusPlaybook.Value("Pace.Wobble");
        if (size <= 0 || points.Count < 3) {
            return points;
        }

        var zone = _team.Count > 0 ? _team[0].Zone : null;
        var wobbled = new List<(float X, float Y)>(points.Count) { points[0] };
        for (var i = 1; i < points.Count - 1; i++) {
            var moved = (X: points[i].X + (float) ((_paceRandom.NextDouble() * 2) - 1) * size,
                         Y: points[i].Y + (float) ((_paceRandom.NextDouble() * 2) - 1) * size);
            // Never off the floor for a wobble.
            wobbled.Add(zone is null || OverlayQuestPath.OnFloor(zone, moved.X, moved.Y) ? moved : points[i]);
        }

        wobbled.Add(points[^1]);

        return wobbled;
    }

    // ---------------------------------------------------------------------
    // Backpack sense
    // ---------------------------------------------------------------------

    private DateTime _nextSellLook = DateTime.MinValue;
    private int _freeBeforeSell = -1;

    // A shop further than this is not worth a detour: he sells at the next shop he passes (Niduus, 2026-11-14 "HUH???": he
    // walked off to sell to Charley at half health in the middle of "Payback").
    private const float SELL_DETOUR = 2500f;

    /// <summary>
    /// Little room left in the backpack and gear he will never wear: the nearest shopkeeper in this area, to sell it. Null
    /// when there is room, nothing to sell or no shop here (he sells at the next town).
    /// </summary>
    private UltimusPlanner.Target SellTarget(List<UltimusMember.Status> group) {
        if (DateTime.UtcNow < _nextSellLook) {
            return _target?.Key.StartsWith("sell|", StringComparison.Ordinal) == true ? _target : null;
        }

        _nextSellLook = DateTime.UtcNow.AddSeconds(20);
        var leader = group[0];
        if (leader.Wizard is not { } wizard || wizard.ZoneIsInstance || wizard.BackpackFreeSlots >= UltimusPlaybook.Value("Backpack.Room")
            || BotGear.Junk(wizard, StyleOf(leader)).Count == 0) {
            _freeBeforeSell = -1;
            return null;
        }

        // He sold here and the backpack got no roomier: selling did not work, so not again for a while (no loop).
        if (_freeBeforeSell >= 0 && wizard.BackpackFreeSlots <= _freeBeforeSell) {
            _freeBeforeSell = -1;
            _nextSellLook = DateTime.UtcNow.AddMinutes(15);
            Note("Ultimus", $"{leader.Name} tried to sell but the backpack still has only {wizard.BackpackFreeSlots} free spaces: no selling for 15 minutes.");
            return null;
        }

        // Hurt: healing comes first, selling waits.
        if (Hurt(leader, out _, out _)) {
            return null;
        }

        var shop = MinimapIndex.In(leader.ZoneActor)
            .Where(m => m.Kind == MinimapIndex.NPC && m.Entity?.GetComponentOfType<InteractVendorComponent>() is not null)
            .OrderBy(m => UltimusPlanner.Far(leader.X, leader.Y, m.Object.m_location.X, m.Object.m_location.Y))
            .FirstOrDefault();
        if (shop is null || UltimusPlanner.Far(leader.X, leader.Y, shop.Object.m_location.X, shop.Object.m_location.Y) > SELL_DETOUR) {
            Think($"My backpack has only {wizard.BackpackFreeSlots} free spaces. I sell what I will never wear at the next shop I pass " +
                  $"({UltimusPlaybook.Cite("Backpack.Room")}).", "sell-later");
            return null;
        }

        return new UltimusPlanner.Target(UltimusPlanner.SHOP, shop.Object.m_location.X, shop.Object.m_location.Y,
            $"{leader.Name} sells gear he won't wear to {shop.Name}", Template: shop.Template, GlobalId: shop.Object.m_globalID.Full,
            Key: $"sell|{leader.Role}");
    }

    // ---------------------------------------------------------------------
    // Selling at the Bazaar
    // ---------------------------------------------------------------------

    private DateTime _nextBazaarSellLook = DateTime.MinValue;

    /// <summary>
    /// What the Bazaar pays for his junk, past what a shop pays (Niduus, 2026-11-14: "Allow for Ultimus to not only quick
    /// sell but to also use the Bazaar to sell (If disabled, he will not attempt to go to the Bazaar)"). The Bazaar pays half
    /// an item's value, a shop a twentieth. Pieces the Bazaar won't take (No Auction) are left for the shop.
    /// </summary>
    private static (List<ulong> Items, int Bazaar, int Shop) BazaarJunk(Imlight.CoreLib.WizardData.Models.Player.Wizard wizard, BotStyle style) {
        var items = new List<ulong>();
        var bazaar = 0;
        var shop = 0;
        foreach (var (item, _) in BotGear.Junk(wizard, style)) {
            if (Imlight.CoreLib.Shared.Resources.CoreObjectFactory.GetCoreTemplate(item.m_templateID) is not Imcodec.ObjectProperty.TypeCache.WizItemTemplate template
                || template.m_adjectiveList.Any(a => a == "FLAG_NoAuction")) {
                continue;
            }

            items.Add(item.m_globalID.Full);
            bazaar += (int) Math.Ceiling(template.m_baseCost * 0.5f);
            shop += (int) Math.Ceiling(template.m_baseCost * 0.05f);
        }

        return (items, bazaar, shop);
    }

    /// <summary>
    /// Little room in the backpack, and the Bazaar pays clearly more than a shop for what he won't wear: the way to the Bazaar
    /// (only while he is in Wizard City anyway, where it is; never when it is turned off), or the sale at its counter.
    /// </summary>
    private UltimusPlanner.Target BazaarSellTarget(List<UltimusMember.Status> group) {
        var leader = group[0];
        var atBazaar = OverlayQuestTracker.SameZone(leader.Zone ?? "", BAZAAR_ZONE);
        if (DateTime.UtcNow < _nextBazaarSellLook || leader.Wizard is not { } wizard || wizard.ZoneIsInstance) {
            return _target?.Key.StartsWith("bazaarsell|", StringComparison.Ordinal) == true ? _target : null;
        }

        _nextBazaarSellLook = DateTime.UtcNow.AddSeconds(30);
        if (!BazaarLink.Enabled || BazaarLink.StateNow == BazaarState.Disabled) {
            return null;
        }

        // At the Bazaar for a buy: what he won't wear goes too, whatever room is left. Elsewhere only when the backpack is
        // nearly full, he is not hurt, and he is in Wizard City (no trip across worlds to sell).
        var nearlyFull = wizard.BackpackFreeSlots < UltimusPlaybook.Value("Backpack.Room");
        if (!atBazaar && (!nearlyFull || Hurt(leader, out _, out _)
            || !string.Equals((leader.Zone ?? "").Split('/')[0], BAZAAR_ZONE.Split('/')[0], StringComparison.OrdinalIgnoreCase))) {
            return null;
        }

        var (items, pays, shopPays) = BazaarJunk(wizard, StyleOf(leader));
        if (items.Count == 0 || pays - shopPays < UltimusPlaybook.Value("Gold.BazaarSellGain")) {
            return null;
        }

        if (atBazaar) {
            var keeper = MinimapIndex.In(leader.ZoneActor).FirstOrDefault(m => m.Entity?.GetComponentOfType<InteractAuctionHouseComponent>() is not null);
            return keeper is null ? null
                : new UltimusPlanner.Target(UltimusPlanner.SHOP, keeper.Object.m_location.X, keeper.Object.m_location.Y,
                    $"{leader.Name} sells {items.Count} pieces he won't wear at the Bazaar (about {pays:N0} gold)", Template: keeper.Template,
                    GlobalId: keeper.Object.m_globalID.Full, Key: "bazaarsell|" + leader.Role);
        }

        Think($"My backpack has only {wizard.BackpackFreeSlots} free spaces. The Bazaar pays about {pays:N0} gold for the {items.Count} pieces I " +
              $"won't wear, a shop only {shopPays:N0}, and I am in Wizard City anyway: I sell them at the Bazaar ({UltimusPlaybook.Cite("Gold.BazaarSellGain")}).",
            "bazaar-sell");

        return UltimusPlanner.Travel(leader, BAZAAR_ZONE, "to the Bazaar, to sell what I won't wear", "bazaarsell-way") is { Problem.Length: 0 } way ? way : null;
    }

    private void BazaarSellHere(UltimusPlanner.Target target) {
        var member = _team[0];
        if (UltimusPlanner.Far(member.X, member.Y, target.X, target.Y) > 200) {
            WalkTo(0, target);
            return;
        }

        if (!CanClick(target.Key)) {
            return;
        }

        Clicked(target.Key);
        if (BazaarLink.Visit() != BazaarState.Open || member.Wizard is not { } wizard) {
            // Waking up, or turned off meanwhile: the shop's quick sell is still there.
            _nextBazaarSellLook = DateTime.UtcNow.AddMinutes(BazaarLink.StateNow == BazaarState.Opening ? 0.5 : 30);
            return;
        }

        var (items, pays, _) = BazaarJunk(wizard, StyleOf(member));
        if (items.Count == 0) {
            return;
        }

        if (member.Options.TryGetValue(target.GlobalId, out var options) && options.FirstOrDefault(o => o.Service == "AuctionHouseService") is { Service: not null } open) {
            _actors[0].Tell(new UltimusMember.Interact(target.GlobalId, open.Service, open.Index, "the Bazaar"));
        }

        HoldRecall(8);
        Context.System.Scheduler.ScheduleTellOnce(TimeSpan.FromSeconds(1.5), _actors[0],
            new UltimusMember.SellAtBazaar(target.GlobalId, items, $"{items.Count} pieces, about {pays:N0} gold"), Self);
        Count("things sold at the Bazaar", items.Count);
        Think($"At the Bazaar I sell {items.Count} pieces I will never wear, for about {pays:N0} gold. Quest items and what I wear stay.", "bazaar-sold");
        _nextBazaarSellLook = DateTime.UtcNow.AddMinutes(10);
        _target = null;
    }

    private void SellHere(UltimusPlanner.Target target) {
        var member = _team[0];
        if (UltimusPlanner.Far(member.X, member.Y, target.X, target.Y) > 200) {
            // To the auctioneer himself: the way finder stops on the floor in front of his counter, never behind it.
            WalkTo(0, target);
            return;
        }

        if (!CanClick(target.Key)) {
            return;
        }

        Clicked(target.Key);
        var junk = member.Wizard is { } wizard ? BotGear.Junk(wizard, StyleOf(member)) : [];
        _freeBeforeSell = member.Wizard?.BackpackFreeSlots ?? -1;
        if (member.Options.TryGetValue(target.GlobalId, out var options) && options.FirstOrDefault(o => o.Service == "WizShoppingService") is { Service: not null } open) {
            _actors[0].Tell(new UltimusMember.Interact(target.GlobalId, open.Service, open.Index, "selling"));
            HoldRecall(6);
        }

        if (junk.Count > 0) {
            Context.System.Scheduler.ScheduleTellOnce(TimeSpan.FromSeconds(1.5), _actors[0],
                new UltimusMember.Sell(target.GlobalId, [.. junk.Select(j => j.Item.m_globalID.Full)], string.Join(", ", junk.Select(j => j.Piece.Name).Take(8))), Self);
            Count("things sold", junk.Count);
            Think($"My backpack is nearly full, so I sell {junk.Count} pieces I will never wear. Quest items and what I wear stay.", "sell");
        }

        _nextSellLook = DateTime.UtcNow.AddSeconds(30);
        _target = null;
    }

    // ---------------------------------------------------------------------
    // Gold planning
    // ---------------------------------------------------------------------

    private string _goldGoalWorld = "";

    /// <summary>Gold he keeps for the next world's gear: never spent on a small upgrade.</summary>
    private static int GoldReserve(UltimusMember.Status member)
        => (int) ((member.Wizard?.MagicSchoolBehavior?.Level ?? 1) * UltimusPlaybook.Value("Gold.ReservePerLevel"));

    /// <summary>In a new world: what the next world's shop gear will cost, and what he has, said once.</summary>
    private void PlanGold(UltimusMember.Status leader, string world) {
        if (leader.Wizard is not { } wizard || string.Equals(world, _goldGoalWorld, StringComparison.OrdinalIgnoreCase)) {
            return;
        }

        _goldGoalWorld = world;
        var level = (wizard.MagicSchoolBehavior?.Level ?? 1) + (int) UltimusPlaybook.Value("Gold.LookAheadLevels");
        var cost = BotGear.BestFor(BotGear.SchoolOf(wizard), level, StyleOf(leader), p => p.Gold > 0 && Imlight.CoreLib.Game.World.ItemSources.VendorsOf(p.Id).Count > 0)
            .Values.Sum(p => (long) p.Gold);
        var have = wizard.GameStats?.m_currentGold ?? 0;
        Think($"Gold plan for {world}: I have {have:N0} gold. Shop gear for level {level} costs about {cost:N0} in all, so I keep " +
              $"{GoldReserve(leader):N0} back ({UltimusPlaybook.Cite("Gold.ReservePerLevel")} a level) and only spend past it on a clear upgrade.",
            "gold|" + world);
    }

    // ---------------------------------------------------------------------
    // Gear check at each new world: shops and the Bazaar
    // ---------------------------------------------------------------------

    internal const string BAZAAR_ZONE = "WizardCity/WC_Streets/Interiors/WC_OldeTown_AuctionHouse";
    private const ulong BAZAAR_ITEM = 9UL << 40;
    private readonly HashSet<uint> _bazaarSkipped = [];
    private DateTime _nextBazaarLook = DateTime.MinValue;
    private UltimusPlanner.Target _bazaarTrip;
    // He may wake a sleeping Bazaar bot once per world (it reads the game files for a minute when it wakes); later looks
    // in the same world only ask while it is awake anyway.
    private bool _bazaarWakeAllowed = true;

    /// <summary>Each new world: he looks his gear over (shops of the world at once, the Bazaar when it is worth the trip).</summary>
    private void NewWorldGearCheck(UltimusMember.Status leader, string world) {
        _nextShopping = DateTime.UtcNow;
        _nextBazaarLook = DateTime.UtcNow;
        _bazaarWakeAllowed = true;
        _shopSkipped.Clear();
        _bazaarSkipped.Clear();
        PlanGold(leader, world);
        Think($"New world, {world}: I look my gear over. Its shops first; the Bazaar in Olde Town if it has a clear upgrade I can pay for. I never craft.",
            "gearcheck|" + world);
        WriteTrainingPlan(leader, world);
    }

    /// <summary>A clear upgrade the Bazaar has in stock that he can pay for, past his reserve: the way there, or the buy.</summary>
    private UltimusPlanner.Target BazaarTarget(List<UltimusMember.Status> group) {
        if (DateTime.UtcNow < _nextBazaarLook) {
            return _bazaarTrip;
        }

        _nextBazaarLook = DateTime.UtcNow.AddMinutes(10);
        _bazaarTrip = null;
        var leader = group[0];
        if (leader.Wizard is not { } wizard || wizard.ZoneIsInstance) {
            return null;
        }

        var spend = (wizard.GameStats?.m_currentGold ?? 0) - GoldReserve(leader);
        if (spend <= 0) {
            return null;
        }

        // The Bazaar can be turned off, sleeps while nobody is there, and in Actual mode has only what players sold
        // (Niduus, 2026-11-14). Asking wakes it; he looks again once it is up, and skips it while it is off.
        if (!_bazaarWakeAllowed && BazaarLink.StateNow == BazaarState.Opening) {
            return null;
        }

        switch (BazaarLink.Visit()) {
            case BazaarState.Disabled:
                Think("The Bazaar is turned off on this server, so I keep to the shops.", "bazaar|off");
                return null;
            case BazaarState.Opening:
                _nextBazaarLook = DateTime.UtcNow.AddSeconds(75);
                return null;
            case BazaarState.Closed:
                return null;
        }

        _bazaarWakeAllowed = false;
        var actual = BazaarBook.Mode == BazaarMode.Actual;
        var style = StyleOf(leader);
        var school = BotGear.SchoolOf(wizard);
        var gainNeeded = UltimusPlaybook.Value("Gear.BazaarGain");
        foreach (var (slot, piece) in BotGear.BestFor(school, BotGear.LevelOf(wizard), style, p => !p.CrownsOnly && !BotGear.Forbidden(p.Id))) {
            if (_bazaarSkipped.Contains(piece.Id)) {
                continue;
            }

            var (_, wearing) = BotGear.Wearing(wizard, slot, style);
            var gain = BotGear.Score(piece, school, style) - wearing;
            if (gain < gainNeeded) {
                continue;
            }

            BazaarQuote quote;
            try {
                quote = BazaarLink.Find(piece.Id);
            }
            catch (Exception e) {
                Logger.Debug("Ultimus: could not ask the Bazaar: {0}", Logger.Args(e.Message));
                return null;
            }

            if (quote is not { Stock: > 0 } || quote.BuyPrice > spend) {
                // Not for sale (in the Actual Bazaar, nobody sold one yet): no asking about it again until the next world.
                if (actual && quote is not { Stock: > 0 }) {
                    _bazaarSkipped.Add(piece.Id);
                }

                continue;
            }

            Think($"The Bazaar has {piece.Name} for {quote.BuyPrice:N0} gold, a clear upgrade (+{gain:0}). I can pay for it and keep my reserve.",
                "bazaar|" + piece.Id);
            var live = MinimapIndex.In(leader.ZoneActor);
            var keeper = live.FirstOrDefault(m => m.Entity?.GetComponentOfType<InteractAuctionHouseComponent>() is not null);
            _bazaarTrip = keeper is not null
                ? new UltimusPlanner.Target(UltimusPlanner.SHOP, keeper.Object.m_location.X, keeper.Object.m_location.Y,
                    $"{leader.Name} buys {piece.Name} at the Bazaar", Template: keeper.Template, GlobalId: keeper.Object.m_globalID.Full,
                    Key: $"bazaar|{leader.Role}|{piece.Id}")
                : UltimusPlanner.Travel(leader, BAZAAR_ZONE, $"the Bazaar, for {piece.Name}", "bazaar");
            if (_bazaarTrip is null) {
                _bazaarSkipped.Add(piece.Id);
                continue;
            }

            _nextBazaarLook = DateTime.UtcNow.AddSeconds(10);
            return _bazaarTrip;
        }

        return null;
    }

    private void BazaarHere(UltimusPlanner.Target target) {
        var parts = target.Key.Split('|');
        if (parts.Length < 3 || !uint.TryParse(parts[2], out var itemId)) {
            return;
        }

        var member = _team[0];
        if (UltimusPlanner.Far(member.X, member.Y, target.X, target.Y) > 200) {
            // To the auctioneer himself: the way finder stops on the floor in front of his counter, never behind it.
            WalkTo(0, target);
            return;
        }

        if (!CanClick(target.Key)) {
            if (TimesClicked(target.Key) >= 2) {
                Note("Ultimus", $"could not buy {BotGear.Read(itemId)?.Name ?? itemId.ToString()} at the Bazaar.");
                _bazaarSkipped.Add(itemId);
                _bazaarTrip = null;
            }

            return;
        }

        // At the counter: the Bazaar must be open, and in Actual mode still have one (another player may have bought it).
        var state = BazaarLink.Visit();
        if (state == BazaarState.Opening) {
            // It woke up when he walked in; it is ready in about a minute. The side-trip budget still applies.
            return;
        }

        if (state != BazaarState.Open || BazaarLink.Find(itemId) is not { Stock: > 0 }) {
            Note("Ultimus", $"{BotGear.Read(itemId)?.Name ?? itemId.ToString()} is no longer for sale at the Bazaar ({BazaarLink.Describe()}).");
            _bazaarSkipped.Add(itemId);
            _bazaarTrip = null;
            return;
        }

        Clicked(target.Key);
        if (member.Options.TryGetValue(target.GlobalId, out var options) && options.FirstOrDefault(o => o.Service == "AuctionHouseService") is { Service: not null } open) {
            _actors[0].Tell(new UltimusMember.Interact(target.GlobalId, open.Service, open.Index, "the Bazaar"));
        }

        // The buy goes 1.5 seconds from now: no recall before it is done.
        HoldRecall(6);
        Context.System.Scheduler.ScheduleTellOnce(TimeSpan.FromSeconds(1.5), _actors[0],
            new UltimusMember.BuyAtBazaar(target.GlobalId, itemId | BAZAAR_ITEM, BotGear.Read(itemId)?.Name ?? itemId.ToString()), Self);
        Count("bought at the Bazaar");
        _bazaarSkipped.Add(itemId);
        _bazaarTrip = null;
        _nextBazaarLook = DateTime.UtcNow.AddSeconds(15);
        _nextGearLook = DateTime.UtcNow.AddSeconds(3);
    }

    // ---------------------------------------------------------------------
    // Spell training plan
    // ---------------------------------------------------------------------

    // Why a spell is worth a training point for a Balance wizard alone (the reason he gives).
    private static readonly Dictionary<string, string> s_spellWhy = new(StringComparer.OrdinalIgnoreCase) {
        ["Fairy"] = "a heal of my own: alone, nobody heals me",
        ["Sprite"] = "a heal over time, cheap, for between hits",
        ["Unicorn"] = "a bigger heal",
        ["Tower Shield"] = "halves the next hit, whatever its school",
        ["Feint"] = "a big trap for my next hit (it costs me some resist, so not when I am low)",
        ["Scarab"] = "my first Balance hit",
        ["Scorpion"] = "a stronger Balance hit",
        ["Locust Swarm"] = "damage over time",
        ["Sandstorm"] = "hits every creature at once",
        ["Hydra"] = "Fire, Ice and Storm at once: hits through Balance resist",
        ["Spectral Blast"] = "hits every creature with the school it is weakest to",
        ["Weakness"] = "cuts a creature's next hit",
        ["Balanceblade"] = "a blade for my next Balance hit",
        ["Elemental Blade"] = "a blade for Hydra and my split hits",
        ["Spirit Blade"] = "a blade for spirit hits",
        ["Reshuffle"] = "my used cards back when a fight runs long",
    };

    private void WriteTrainingPlan(UltimusMember.Status leader, string world) {
        var lessons = UltimusTraining.Learnable(leader);
        var points = leader.Wizard?.MagicSchoolBehavior?.TrainingPoints ?? 0;
        var text = new StringBuilder();
        text.AppendLine($"ULTIMUS TRAINING PLAN for {world} ({DateTime.Now:yyyy-MM-dd HH:mm}), level {leader.Wizard?.MagicSchoolBehavior?.Level ?? 0}, {points} training points");
        text.AppendLine("He trains at trainers he can reach, as the game's trainer rules allow. Own school first, then what a wizard alone needs.");
        text.AppendLine();
        foreach (var lesson in lessons) {
            var why = s_spellWhy.GetValueOrDefault(lesson.Spell) ?? "a Balance spell my level opens";
            text.AppendLine($"- {lesson.Spell} in {UltimusText.Zone(lesson.Zone)}: {why}.");
        }

        if (lessons.Count == 0) {
            text.AppendLine("- Nothing to learn right now: the next spells open at higher levels.");
        }

        try {
            File.WriteAllText(Path.Combine(UltimusWire.Folder, "training-plan.txt"), text.ToString());
        }
        catch (Exception e) {
            Logger.Debug("Ultimus: could not write the training plan: {0}", Logger.Args(e.Message));
        }

        if (lessons.Count > 0) {
            Think($"My training plan for {world}: {string.Join("; ", lessons.Take(4).Select(l => $"{l.Spell} ({s_spellWhy.GetValueOrDefault(l.Spell) ?? "my school"})"))}.",
                "trainplan|" + world);
        }
    }

    // ---------------------------------------------------------------------
    // Learning from a lost battle
    // ---------------------------------------------------------------------

    private readonly Dictionary<uint, int> _lostTo = [];

    /// <summary>Why he lost, from what he knows, and what he changes for the next try (a different plan each time).</summary>
    private void LearnFromLoss(UltimusMember.Status me, uint template, string foe, double wentInAt, double seconds) {
        var times = _lostTo[template] = _lostTo.GetValueOrDefault(template) + 1;
        var why = new List<string>();
        if (wentInAt < 0.9) {
            why.Add($"I went in at {wentInAt:P0} health");
        }

        var habit = BotKnowledge.Of(template);
        if (habit is { BiggestHitShare: >= 0.5 }) {
            why.Add($"its biggest hit took {habit.BiggestHitShare:P0} of my health");
        }

        if (habit is { Casts: >= 5 } && habit.Chance(BotKnowledge.HEAL) >= 0.25) {
            why.Add($"it heals itself often ({habit.Chance(BotKnowledge.HEAL):P0} of its casts)");
        }

        if (seconds > 300) {
            why.Add($"the fight ran {seconds / 60:0} minutes, so I ran out of good cards");
        }

        if (why.Count == 0) {
            why.Add("it simply hit harder than I could heal");
        }

        // A new plan each time: careful deck and full health; then level first; then better gear.
        var change = times switch {
            1 => "full health and mana first, a deck with more shields and heals for it, and its healers hit first",
            2 => "fight other creatures for XP first, up to the quest's level",
            _ => "look for better gear in this world's shops and the Bazaar, then try again",
        };
        if (times == 1) {
            PrepareDecks(UltimusScout.SituationOf(template));
        }

        if (times >= 3) {
            _nextShopping = DateTime.UtcNow;
            _nextBazaarLook = DateTime.UtcNow;
        }

        var text = $"Lost to {foe} ({times} time(s)): {string.Join(", ", why)}. Next time: {change}.";
        Think($"Why I lost to {foe}: {string.Join(", ", why)}. What I change: {change}.", "loss|" + template + "|" + times);
        BotKnowledge.Learn("Ultimus", "lost fight", text);
        RememberStep(_target?.Quest, _target?.Goal, text);
    }

    // ---------------------------------------------------------------------
    // Menu Chat personality: players he remembers
    // ---------------------------------------------------------------------

    private const string PLAYERS_FILE = "players.json";

    internal sealed class Met {
        public string Name { get; set; } = "";
        public int Times { get; set; }
        public DateTime First { get; set; }
        public DateTime Last { get; set; }
    }

    private Dictionary<ulong, Met> _met;

    private Dictionary<ulong, Met> MetPlayers() {
        if (_met is not null) {
            return _met;
        }

        try {
            var path = Path.Combine(UltimusWire.Folder, PLAYERS_FILE);
            _met = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<ulong, Met>>(File.ReadAllText(path)) : null;
        }
        catch (Exception e) {
            Logger.Debug("Ultimus: could not read {0}: {1}", Logger.Args(PLAYERS_FILE, e.Message));
        }

        return _met ??= [];
    }

    /// <summary>
    /// A player spoke to him: remembered. One he met before, on another day or an hour ago or more, is greeted again,
    /// and he says so. Returns whether he greeted them.
    /// </summary>
    private bool Remember(ulong charId, string name, bool whisper) {
        var met = MetPlayers();
        var known = met.TryGetValue(charId, out var player);
        if (!known) {
            met[charId] = player = new Met { Name = name ?? "", First = DateTime.UtcNow };
        }

        var away = DateTime.UtcNow - player.Last;
        player.Times++;
        player.Last = DateTime.UtcNow;
        player.Name = name ?? player.Name;
        try {
            File.WriteAllText(Path.Combine(UltimusWire.Folder, PLAYERS_FILE), JsonSerializer.Serialize(met, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) {
            Logger.Debug("Ultimus: could not write {0}: {1}", Logger.Args(PLAYERS_FILE, e.Message));
        }

        if (!known || away < TimeSpan.FromHours(1)) {
            return false;
        }

        // The welcome back is part of his one answer (BotSocial), never a second line before it (audit C1: "Hello?" then
        // "Ha, true!" to "Can I fish next to you?").
        Note("Ultimus", $"remembers {player.Name}: first met {player.First.ToLocalTime():d MMMM}, talked {player.Times} times.");

        return true;
    }

}
