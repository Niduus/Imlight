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

    /// <summary>
    /// A small wobble in a walk's middle points (Pace.Wobble, at most a few steps; the ends stay), so he does not walk
    /// like a ruler. Kept small: the way stays on the legal walk (Niduus's routes rule).
    /// </summary>
    private List<(float X, float Y)> Wobble(List<(float X, float Y)> points) {
        var size = (float) UltimusPlaybook.Value("Pace.Wobble");
        if (size <= 0 || points.Count < 3) {
            return points;
        }

        var wobbled = new List<(float X, float Y)>(points.Count) { points[0] };
        for (var i = 1; i < points.Count - 1; i++) {
            wobbled.Add((points[i].X + (float) ((_paceRandom.NextDouble() * 2) - 1) * size,
                         points[i].Y + (float) ((_paceRandom.NextDouble() * 2) - 1) * size));
        }

        wobbled.Add(points[^1]);

        return wobbled;
    }

    // ---------------------------------------------------------------------
    // Backpack sense
    // ---------------------------------------------------------------------

    private DateTime _nextSellLook = DateTime.MinValue;

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
            return null;
        }

        var shop = MinimapIndex.In(leader.ZoneActor)
            .Where(m => m.Kind == MinimapIndex.NPC && m.Entity?.GetComponentOfType<InteractVendorComponent>() is not null)
            .OrderBy(m => UltimusPlanner.Far(leader.X, leader.Y, m.Object.m_location.X, m.Object.m_location.Y))
            .FirstOrDefault();
        if (shop is null) {
            Think($"My backpack has only {wizard.BackpackFreeSlots} free spaces. I sell what I will never wear at the next shop I pass " +
                  $"({UltimusPlaybook.Cite("Backpack.Room")}).", "sell-later");
            return null;
        }

        return new UltimusPlanner.Target(UltimusPlanner.SHOP, shop.Object.m_location.X, shop.Object.m_location.Y,
            $"{leader.Name} sells gear he won't wear to {shop.Name}", Template: shop.Template, GlobalId: shop.Object.m_globalID.Full,
            Key: $"sell|{leader.Role}");
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

    /// <summary>Each new world: he looks his gear over (shops of the world at once, the Bazaar when it is worth the trip).</summary>
    private void NewWorldGearCheck(UltimusMember.Status leader, string world) {
        _nextShopping = DateTime.UtcNow;
        _nextBazaarLook = DateTime.UtcNow;
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

        Note("Ultimus", $"remembers {player.Name}: first met {player.First.ToLocalTime():d MMMM}, talked {player.Times} times. He greets them again.");
        Speak("hello", 1, force: true, whisperTo: whisper ? charId : 0);

        return true;
    }

}
