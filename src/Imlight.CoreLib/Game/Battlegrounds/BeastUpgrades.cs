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
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Battlegrounds;

/*
 * The Beastmoon Upgrades NPC (a BattlegroundPolymorphImprovementBehavior): the game's PolymorphImprovement window. Its
 * words (Locale Battlegrounds.lang) tell the two things it raises apart:
 * - Level ("Upgrade Level", from 1 to 5): costs the form's PolymorphBattlegroundsBehaviorTemplate m_levelUpIngredients
 *   (Hunter's Moonstones) and m_levelUpCost Lunari (the cost window shows a reagent and totalLunari). A form's level
 *   sets how many battle cards it carries (m_ratingInfo).
 * - Tier, which the game calls rating ("Upgrade Tier"; at 0 "Unlock Beast", which the game describes as a permanent unlock for all
 *   future Beastmoon events). The event's own beasts (the match's m_unlockedPolymorphTags) are open at tier 1
 *   without it. A tier costs what BattlegroundPolymorphs.xml says (m_ratingBadgeInfo: Lunari and reagents "Found on
 *   Scroll of Fortune, Beastmoon Progress Bars, or Crafted on the Anvil"); [Beastmoon] TierMoonstones and TierLunari
 *   price a tier the table has no entry for.
 * The window: MSG_BATTLEGROUNDPOLYMORPHIMPROVEMENT (RatingData a PolymorphImprovementRatingInfoList: each form's tier-1
 * template, the wizard's own tier and the event's) opens it; MSG_BATTLEGROUNDPOLYMORPHIMPROVEREQ asks for a Level or a
 * Rating (Tag the form), answered by MSG_BATTLEGROUNDPOLYMORPHIMPROVERESP. What the request's Level, Rating and Tag hold
 * exactly is read from the first ones the game sends (they are logged).
 */
internal static class BeastUpgrades {

    private static int TierMoonstones => Math.Max(0, ConfigurationManager.GetValue("Beastmoon.TierMoonstones", 10));
    private static int TierLunari => Math.Max(0, ConfigurationManager.GetValue("Beastmoon.TierLunari", 250));

    private static readonly ObjectSerializer s_serializer = new(Versionable: false, Behaviors: SerializerFlags.None);

    /// <summary>The tier-1 file of every form (BG-Polymorph-Cyclops-Life-01, ...).</summary>
    private static List<string> FirstTiers()
        => [.. Battlegrounds.FormNames().Where(n => Battlegrounds.FormTier(n) == 1)];

    /// <summary>The window's opening message for the wizard.</summary>
    internal static IMessage Window(Wizard wizard, ulong npcId) {
        var (eventTemplate, _, _) = Battlegrounds.PvEEvent(DateTime.UtcNow);
        var match = eventTemplate is null ? null : Battlegrounds.Template(eventTemplate.m_extendedTemplateName.ToString());
        var list = new PolymorphImprovementRatingInfoList { m_list = [] };
        // One entry per beast and school, under the first template of its BattlegroundPolymorphs.xml entry (the window
        // looks it up by that ID). Ratings count from 0 (tier 1) and -1 is not owned (PolymorphImprovement).
        foreach (var entry in BeastTable.Entries) {
            if (entry?.m_polymorphList is not { Count: > 0 } ids || Battlegrounds.FormById(ids[0], out var name) is null) {
                continue;
            }

            var allowed = EventAllows(match, name);
            list.m_list.Add(new PolymorphImprovementRatingInfo {
                m_polymorphTemplateID = ids[0],
                // The event's own beasts count as owned at tier 1 here, so they can be levelled.
                m_personalRating = BeastmoonProgress.TierOf(wizard.CharId, name, allowed) - 1,
                m_eventRating = allowed ? 0 : -1,
            });
        }

        if (!s_serializer.Serialize(list, 5, out var data)) {
            return null;
        }

        return new WIZARD2_53_PROTOCOL.MSG_BATTLEGROUNDPOLYMORPHIMPROVEMENT {
            GlobalID = npcId,
            RatingData = data,
            BGClassProjectID = eventTemplate is null ? 0 : Imcodec.Cryptography.StringHash.Compute(eventTemplate.m_projectName.ToString()),
        };
    }

    internal static bool EventAllows(BattlegroundTemplate match, string formFileName)
        => match?.m_unlockedPolymorphTags?.Any(t => string.Equals(t.ToString(), Battlegrounds.FormTag(formFileName), StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>Handles a Level or Tier request. Returns the answer for the game.</summary>
    internal static WIZARD2_53_PROTOCOL.MSG_BATTLEGROUNDPOLYMORPHIMPROVERESP Improve(IActorRef session, Wizard wizard,
                                                                                  WIZARD2_53_PROTOCOL.MSG_BATTLEGROUNDPOLYMORPHIMPROVEREQ request) {
        var tag = request.Tag.ToString();
        Logger.Information("Beastmoon | {0} asks to improve '{1}': level {2}, rating {3}.",
            Logger.Args(wizard.CharId, tag, request.Level, request.Rating));

        var form = FormOf(tag);
        var answer = new WIZARD2_53_PROTOCOL.MSG_BATTLEGROUNDPOLYMORPHIMPROVERESP { Failure = 1, Tag = request.Tag };
        if (form is null) {
            Logger.Warning("Beastmoon | no form matches the improvement tag '{0}'.", Logger.Args(tag));

            return answer;
        }

        var level = BeastmoonProgress.LevelOf(wizard.CharId, form);
        var (current, _, _) = Battlegrounds.PvEEvent(DateTime.UtcNow);
        var currentMatch = current is null ? null : Battlegrounds.Template(current.m_extendedTemplateName.ToString());
        var tier = BeastmoonProgress.TierOf(wizard.CharId, form, EventAllows(currentMatch, form));
        answer.Level = level + 1;
        answer.Rating = tier - 1;

        // The request names what it is for: a tier (rating) above the wizard's, else the next level.
        var wantsTier = request.Rating + 1 > tier;
        string why;
        if (wantsTier) {
            var target = Math.Min(tier + 1, BeastmoonProgress.MAX_TIER);
            // The game's price for the tier (BattlegroundPolymorphs.xml m_ratingBadgeInfo), else the settings.
            var cost = BeastTable.TierCost(form, target);
            why = target <= tier ? "That beast is at its highest tier."
                : cost is not null ? Pay(session, wizard, 0, cost.m_cost, cost.m_ingredients ?? [])
                : Pay(session, wizard, TierMoonstones * target, TierLunari * target, []);
            if (why is null) {
                BeastmoonProgress.Change(wizard.CharId, h => h.Tiers[Battlegrounds.FormKey(form)] = target);
                answer.Rating = target - 1;
            }
        }
        else {
            var behavior = Battlegrounds.Form(form, out _)?.m_behaviors?.OfType<PolymorphBattlegroundsBehaviorTemplate>().FirstOrDefault();
            why = level >= BeastmoonProgress.MAX_LEVEL ? "That beast is at its highest level."
                : Pay(session, wizard, 0, behavior?.m_levelUpCost ?? 0, behavior?.m_levelUpIngredients ?? []);
            if (why is null) {
                BeastmoonProgress.Change(wizard.CharId, h => h.Levels[Battlegrounds.FormKey(form)] = level + 1);
                answer.Level = level + 2;
                // The Beastmoon event counts a form levelled (CPT_LevelPolymorph, 10 points in Beastmoon_PVE_Event_*).
                Events.ClassProjects.Record(session, wizard, ClassProjectActivityType.CPT_LevelPolymorph, 1, ["BGMobClassProjectPoints"]);
                // The game keeps the level in the event's progress (BeastTable).
                var (eventTemplate, _, _) = Battlegrounds.PvEEvent(DateTime.UtcNow);
                if (eventTemplate is not null && BeastTable.IndexOf(form) is var index and >= 0) {
                    session.Tell(new WIZARD2_53_PROTOCOL.MSG_SetBGPolymorphLevel {
                        ProjectID = Imcodec.Cryptography.StringHash.Compute(eventTemplate.m_projectName.ToString()),
                        Index = index,
                        Level = level + 1,
                    });
                }
            }
        }

        if (why is not null) {
            session.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = why, Modal = 0 });

            return answer;
        }

        answer.Failure = 0;
        Logger.Information("Beastmoon | {0} improved {1}: level {2}, tier {3}.", Logger.Args(wizard.CharId, form, answer.Level, answer.Rating + 1));

        return answer;
    }

    // A form named by the window: its file, "Cyclops-Life", "Life Cyclops", or a template ID.
    private static string FormOf(string tag) {
        if (string.IsNullOrWhiteSpace(tag)) {
            return null;
        }

        if (uint.TryParse(tag, out var id)) {
            return Battlegrounds.FormById(id, out var byId) is null ? null : byId;
        }

        return FirstTiers().FirstOrDefault(n => string.Equals(n, tag, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Battlegrounds.FormKey(n), tag, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Battlegrounds.FormTag(n), tag, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Battlegrounds.FormKey(n), Battlegrounds.FormKey(tag), StringComparison.OrdinalIgnoreCase));
    }

    // Takes the cost: extra Hunter's Moonstones, Lunari and the listed reagents. Returns why not, or null when paid.
    private static string Pay(IActorRef session, Wizard wizard, int moonstones, int lunari, IEnumerable<Ingredient> ingredients) {
        var needs = new Dictionary<uint, int>();
        foreach (var ingredient in ingredients.Where(i => i is not null && i.m_ingredientType == INGREDIENT_TYPE.INGREDIENT_REAGENT)) {
            var id = (uint) ingredient.m_itemID.Full;
            needs[id] = needs.GetValueOrDefault(id) + Math.Max(1, ingredient.m_quantity);
        }

        if (moonstones > 0 && BeastRewards.MoonstoneId is var stone and not 0) {
            needs[stone] = needs.GetValueOrDefault(stone) + moonstones;
        }

        foreach (var (id, count) in needs) {
            if ((wizard.AlchemyBehavior.GetReagent(id)?.m_quantity ?? 0) < count) {
                return $"Not enough resources: {count} {NameOf(id)} needed.";
            }
        }

        if (wizard.GameStats.m_currentEventCurrency1 < lunari) {
            return $"Not enough resources: {lunari} Lunari needed.";
        }

        foreach (var (id, count) in needs) {
            var reagent = wizard.AlchemyBehavior.GetReagent(id);
            if (reagent is not null && wizard.RemoveReagents(reagent.m_globalID, count, out var left)) {
                session.Tell(left.m_quantity > 0
                    ? new WIZARD_12_PROTOCOL.MSG_REAGENTUPDATE { GlobalID = wizard.GameObjectID, ItemID = left.m_globalID, Quantity = left.m_quantity }
                    : new WIZARD_12_PROTOCOL.MSG_REAGENTREMOVE { GlobalID = wizard.GameObjectID, ItemID = left.m_globalID });
            }
        }

        if (lunari > 0) {
            wizard.RemoveLunari(lunari);
            session.Tell(new WIZARD2_53_PROTOCOL.MSG_UPDATEEVENTCURRENCY1 {
                EventCurrency1 = wizard.GameStats.m_currentEventCurrency1,
                MaxEventCurrency1 = wizard.GameStats.m_baseEventCurrency1Pouch,
            });
        }

        return null;
    }

    private static string NameOf(uint id)
        => CoreObjectFactory.GetCoreTemplate(id) is GameObjectTemplate t && Locale.GetEnglishName(t.m_displayName) is { Length: > 0 } name
            ? name : $"reagent {id}";

}
