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
using System.Globalization;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.CoreLib.Game.Fishing;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// Fishing from chat: `.fishing` shows your fishing level, basket and the fish here, `.fishing basket` lists the basket,
/// `.fishing sell` sells it all. QA: `.fishing spells` teaches every fishing spell, `.fishing level &lt;n&gt;` sets the
/// fishing level and `.fishing reset` fills this zone's ponds with new fish.
/// </summary>
internal class CommandFishingProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("fishing")]
    private void FishingCommand([Remainder] string what = "") {
        var me = Context.Character;
        var words = (what ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var verb = words.Length > 0 ? words[0].ToLowerInvariant() : "";
        me.FishingBehavior ??= new ServerFishingBehavior();

        switch (verb) {
            case "":
                Status(me);
                break;
            case "basket":
                Basket(me);
                break;
            case "sell":
                Sell(me);
                break;
            case "spells":
                if (RequireQa()) {
                    TeachAll(me);
                }
                break;
            case "level":
                if (RequireQa()) {
                    SetLevel(me, words.ElementAtOrDefault(1));
                }
                break;
            case "reset":
                if (RequireQa()) {
                    var pond = FishPonds.For(me.Zone, out var ready);
                    if (pond is null) {
                        InformSenderClient(ready ? "There is no fishing in this area." : "This area's ponds are still being read. Try again in a moment.");
                    }
                    else {
                        pond.Reset();
                        InformSenderClient($"New fish swim in this area's ponds now ({pond.Snapshot().Count}).");
                    }
                }
                break;
            default:
                InformSenderClient("Use .fishing, .fishing basket or .fishing sell (QA: .fishing spells, .fishing level 10, .fishing reset).");
                break;
        }
    }

    private bool RequireQa() {
        if (Context.Account.AuthLevel >= AuthLevel.QualityAssurance) {
            return true;
        }

        InformSenderClient("You do not have permission to use this command.");

        return false;
    }

    private void Status(Wizard me) {
        var stats = me.GameStats;
        var level = Math.Max((int) stats.m_fishingLevel, 1);
        var next = FishingData.Levels.FirstOrDefault(l => l.m_level == level + 1);
        var basket = me.FishingBehavior.Basket?.Count ?? 0;
        var text = $"Fishing level {level}, {stats.m_fishingXP} fishing XP"
                   + (next is null ? "" : $" ({next.m_xpToLevel} for level {next.m_level})")
                   + $". Basket: {basket} of {FishingSettings.BasketSize} fish.";

        var pond = FishPonds.For(me.Zone, out var ready);
        if (!ready) {
            text += "<br>This area's ponds are still being read.";
        }
        else if (pond is null) {
            text += "<br>There is no fishing in this area.";
        }
        else {
            var fish = pond.Snapshot();
            var kinds = fish.GroupBy(f => f.TemplateId)
                .Select(g => (Info: FishingData.Fish(g.Key), Count: g.Count()))
                .Where(k => k.Info is not null)
                .OrderBy(k => k.Info.Rank)
                .Select(k => $"{k.Count} {k.Info.Name} (rank {k.Info.Rank} {k.Info.School})");
            text += $"<br>{fish.Count} fish in {pond.Waters.Count} pond(s) here: {string.Join(", ", kinds)}.";
        }

        InformSenderClient(text);
    }

    private void Basket(Wizard me) {
        var basket = me.FishingBehavior.Basket ?? [];
        if (basket.Count == 0) {
            InformSenderClient("Your fish basket is empty.");

            return;
        }

        var lines = basket.GroupBy(f => f.TemplateId)
            .Select(g => (Info: FishingData.Fish(g.Key), Fish: g.ToList()))
            .Select(k => $"{k.Fish.Count} {k.Info?.Name ?? $"fish {k.Fish[0].TemplateId}"}"
                         + $" (biggest {k.Fish.Max(f => k.Info?.LengthOf(f.Size) ?? f.Size).ToString("0.0", CultureInfo.InvariantCulture)} in)");
        InformSenderClient($"Fish basket ({basket.Count}):<br>" + string.Join("<br>", lines));
    }

    private void Sell(Wizard me) {
        var basket = me.FishingBehavior.Basket ?? [];
        if (basket.Count == 0) {
            InformSenderClient("Your fish basket is empty.");

            return;
        }

        var gold = basket.Sum(f => FishingData.Fish(f.TemplateId)?.SalePrice ?? 0);
        var count = basket.Count;
        basket.Clear();
        WizardCollection.UpdateCharacterFishingBehavior(me);
        me.AddGold(gold);
        Context.SessionActor.Tell(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD {
            Gold = me.GameStats.m_currentGold,
            MaxGold = me.GameStats.m_baseGoldPouch,
        });
        InformSenderClient($"Sold {count} fish for {gold} gold. Change area or log out and back in to see your basket empty.");
    }

    private void TeachAll(Wizard me) {
        var taught = 0;
        foreach (var (id, template) in FishingData.Spells.Where(s => FishingData.IsPlayerSpell(s.Template))) {
            if (me.SpellbookBehavior.HasSpell(id)) {
                continue;
            }

            var spell = SpellFactory.GetSpell(id);
            if (spell is null || !me.LearnSpell(spell)) {
                continue;
            }

            Context.SessionActor.Tell(new WIZARD_12_PROTOCOL.MSG_ADDSPELLTOBOOK { SpellID = (int) id });
            taught++;
        }

        InformSenderClient(taught == 0 ? "You already know every fishing spell." : $"You learned {taught} fishing spells. Open your spellbook's fishing page to see them.");
    }

    private void SetLevel(Wizard me, string value) {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var level)) {
            InformSenderClient("Use .fishing level and a number, like .fishing level 10.");

            return;
        }

        level = Math.Clamp(level, 1, FishingData.MaxLevel);
        var stats = me.GameStats;
        stats.m_fishingLevel = (byte) level;
        // The XP the level needs, so the next catch does not take the level back down.
        stats.m_fishingXP = Math.Max(stats.m_fishingXP, FishingData.Levels.FirstOrDefault(l => l.m_level == level)?.m_xpToLevel ?? 0);
        WizardCollection.UpdateCharacterGameStats(me);
        Context.SessionActor.Tell(new WIZARD2_53_PROTOCOL.MSG_UPDATEFISHINGXP {
            GlobalID = me.GameObjectID,
            XP = stats.m_fishingXP,
            Level = level,
        });
        InformSenderClient($"You are fishing level {level} now.");
    }

}
