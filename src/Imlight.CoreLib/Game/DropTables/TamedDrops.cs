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
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Game.Requirements;
using Imlight.CoreLib.Game.Requirements.Contexts;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.DropTables;

/// <summary>
/// Tamed Drops ([TamedDrops] Enabled): a boss's loot depends on how many rounds the battle took. Every tier rolls the
/// boss's own drop tables; the longer the battle, the rarer the junk (reagents, treasure cards, furniture, snacks) and
/// the likelier the rest. Each tier also has an account-wide pity meter per boss.
/// </summary>
/// <remarks>
/// Every number and choice below comes from TamedDropsSettings (tamed-drops.json, the Manager's Tamed Drops card), read
/// again whenever the file changes. The defaults: tier by rounds 1 = tier 5, 2-3 = tier 4, 4-5 = tier 3, 6-8 = tier 2,
/// 9 or more = tier 1 (the rounds come from CombatDuelComponent.CountRoundForTamedDrops: a round everyone chose to pass
/// stops counting from the third in a row). Tier 1 always adds a piece of gear of the wizard's school from the boss's drops.
/// Pity: a run fills its own tier's meter by 1 and the other tiers' by a quarter. A full meter (tier 5: 1 run, 4: 3,
/// 3: 6, 2: 9, 1: 12) pays out and starts over: tier 5 a drop that is not junk, tier 4 doubled gear odds that run,
/// tier 3 gear not of the wizard's school, tier 2 gear of any school, tier 1 gear of only the wizard's school.
/// A meter whose prize the boss's drops do not hold waits full until it can pay. Repeats are allowed.
/// </remarks>
internal static class TamedDrops {

    internal static bool Enabled => ConfigurationManager.GetValue("TamedDrops.Enabled", true);

    private static readonly EquipmentSlotType[] s_gearSlots = [
        EquipmentSlotType.Hat, EquipmentSlotType.Robe, EquipmentSlotType.Shoes, EquipmentSlotType.Weapon,
        EquipmentSlotType.Athame, EquipmentSlotType.Amulet, EquipmentSlotType.Ring, EquipmentSlotType.Deck,
    ];

    private enum Kind { Junk, Wanted, Gear }

    /// <summary>The tier a battle of this many counted rounds earns: 5 for the fastest down to 1 for the slowest.</summary>
    internal static int TierFor(int rounds) => TamedDropsSettings.Current.TierFor(rounds);

    /// <summary>Whether Tamed Drops decides this creature's loot: a boss with drop tables (or any monster, if set), while it is on.</summary>
    internal static bool AppliesTo(ulong templateId, bool hasOwnDrops)
        => Enabled && (hasOwnDrops || TamedDropsSettings.Current.AllMonsters || IsBoss(templateId));

    /// <summary>
    /// Rolls a boss's tables for the tier its battle earned, moves the account's pity meters and adds what they pay.
    /// </summary>
    /// <param name="tables">The boss's drop tables.</param>
    /// <param name="bossKey">What the boss drops by (its template id or custom key); its pity meters are kept under it.</param>
    /// <param name="rounds">The battle's counted rounds.</param>
    /// <param name="note">One line for the wizard: the tier, the rounds and any pity paid; null when the settings hide it.</param>
    internal static DropTableResult Roll(string[] tables, ulong bossKey, int rounds, IActorRef playerRef, CoreObject playerObj,
        Wizard wizard, out string note) {
        var settings = TamedDropsSettings.Current;
        var tier = settings.TierFor(Math.Max(1, rounds));
        var pool = Pool(tables, playerRef, playerObj, wizard);
        var school = wizard.MagicSchoolBehavior?.MagicSchool.ToString() ?? "";

        // The prizes the boss's drops can pay, by tier (a tier with no prize or no runs has no meter).
        var canPay = new bool[6];
        var hasMeter = new bool[6];
        for (var t = 1; t <= 5; t++) {
            var of = settings.Of(t);
            hasMeter[t] = of.PityPrize != TamedPrize.None && of.PityRuns > 0;
            canPay[t] = hasMeter[t] && CanPay(of.PityPrize, pool, school, settings);
        }

        var paid = new List<int>();
        var meters = new double[6];
        if (wizard.AccountId != 0) {
            AccountCollection.UpdateTamedPity(wizard.AccountId, stored => {
                for (var t = 1; t <= 5; t++) {
                    if (!hasMeter[t]) {
                        continue;
                    }

                    var key = $"{bossKey}:{t}";
                    var runs = settings.Of(t).PityRuns;
                    var meter = stored.GetValueOrDefault(key) + (t == tier ? settings.OwnTierFill : settings.OtherTierFill);
                    if (meter >= runs && canPay[t]) {
                        paid.Add(t);
                        meter -= runs;
                    }

                    stored[key] = meter;
                    meters[t] = meter;
                }
            });
        }

        // What this run adds: the tier's own every-run extra, then the prizes the meters paid.
        var prizes = new List<TamedPrize>();
        if (settings.Of(tier).EveryRun != TamedPrize.None) {
            prizes.Add(settings.Of(tier).EveryRun);
        }

        prizes.AddRange(paid.Select(t => settings.Of(t).PityPrize));

        var odds = settings.Of(tier);
        var gearBoost = Math.Pow(settings.PrizeGearOdds, prizes.Count(p => p == TamedPrize.DoubleGearOdds));
        var loot = DropTableRoller.Roll(tables, playerRef, playerObj, wizard, item => KindOf(item, settings) switch {
            Kind.Junk => odds.JunkOdds,
            Kind.Gear => odds.OtherOdds * gearBoost,
            _ => odds.OtherOdds,
        });

        foreach (var prize in prizes.Where(p => p != TamedPrize.DoubleGearOdds)) {
            AddPick(loot, Candidates(prize, pool, school, settings));
        }

        var meterText = string.Join(", ", Enumerable.Range(1, 5).Reverse().Where(t => hasMeter[t])
            .Select(t => $"T{t} {Math.Floor(meters[t] * 100) / 100:0.##}/{settings.Of(t).PityRuns:0.##}"));
        note = !settings.ShowNote ? null
            : $"Tamed Drops: tier {tier} ({rounds} round{(rounds == 1 ? "" : "s")})."
              + (paid.Count > 0 ? $" Pity paid: {string.Join(", ", paid.Select(t => PrizeName(t, settings.Of(t).PityPrize, settings)))}." : "")
              + (wizard.AccountId != 0 && meterText.Length > 0 ? $" Pity: {meterText}." : "");
        Logger.Information("Tamed Drops | {0} beat {1} in {2} rounds: tier {3}, pity paid [{4}], meters {5}.", Logger.Args(
            wizard.PlayerNameBehavior?.GetWizardName() ?? wizard.CharId.ToString(), bossKey, rounds, tier,
            string.Join(",", paid), meterText));

        return loot;
    }

    private static string PrizeName(int tier, TamedPrize prize, TamedDropsSettings settings) => $"tier {tier} (" + prize switch {
        TamedPrize.NotJunk => "a drop that is not junk",
        TamedPrize.DoubleGearOdds => $"gear odds x{settings.PrizeGearOdds:0.##}",
        TamedPrize.OtherSchoolGear => "gear of another school",
        TamedPrize.AnyGear => "gear of any school",
        TamedPrize.OwnSchoolGear => "gear of your school",
        _ => "nothing",
    } + ")";

    private static bool CanPay(TamedPrize prize, List<DropItem> pool, string school, TamedDropsSettings settings) => prize switch {
        TamedPrize.DoubleGearOdds => pool.Any(i => KindOf(i, settings) == Kind.Gear),
        TamedPrize.None => false,
        _ => Candidates(prize, pool, school, settings).Any(),
    };

    // The drops a prize picks one of.
    private static IEnumerable<DropItem> Candidates(TamedPrize prize, List<DropItem> pool, string school, TamedDropsSettings settings)
        => prize switch {
            TamedPrize.NotJunk => pool.Where(i => KindOf(i, settings) != Kind.Junk),
            TamedPrize.OtherSchoolGear => pool.Where(i => KindOf(i, settings) == Kind.Gear && GearSchool(i) is { } s
                && !s.Equals(school, StringComparison.OrdinalIgnoreCase)),
            TamedPrize.AnyGear => pool.Where(i => KindOf(i, settings) == Kind.Gear),
            TamedPrize.OwnSchoolGear => pool.Where(i => IsOwnSchoolGear(i, school, settings)),
            _ => [],
        };

    private static void AddPick(DropTableResult loot, IEnumerable<DropItem> candidates) {
        var list = candidates.ToList();
        if (list.Count == 0) {
            return;
        }

        var pick = list[Random.Shared.Next(list.Count)];
        var min = Math.Max(1, pick.MinCount ?? 1);
        loot.Items.Add(new DropItemResult {
            ItemId = pick.ItemId,
            ItemName = pick.ItemName,
            Quantity = Random.Shared.Next(min, Math.Max(min, pick.MaxCount ?? min) + 1),
        });
    }

    // Every item the boss's tables name that the wizard may get (their own requirements met), once each.
    private static List<DropItem> Pool(string[] tables, IActorRef playerRef, CoreObject playerObj, Wizard wizard)
        => [.. tables.Select(DropTableFiles.Get)
            .Where(table => table?.Items is not null)
            .SelectMany(table => table.Items)
            .Where(item => !string.IsNullOrEmpty(item.ItemId))
            .Where(item => item.Requirements is null || RequirementDispatcher.EvaluateRequirements(item.Requirements,
                new GenericRequirementContext(requirements: item.Requirements, playerRef: playerRef, playerObj: playerObj, wizard: wizard)))
            .GroupBy(item => item.ItemId)
            .Select(group => group.First())];

    private static bool IsOwnSchoolGear(DropItem item, string school, TamedDropsSettings settings)
        => KindOf(item, settings) == Kind.Gear && GearSchool(item) is { } s && s.Equals(school, StringComparison.OrdinalIgnoreCase);

    private static Kind KindOf(DropItem item, TamedDropsSettings settings) {
        // A treasure card may be named by its spell name instead of its number.
        if (!ulong.TryParse(item.ItemId, out var id)) {
            return SpellFactory.Exists(item.ItemId) && SpellFactory.GetSpell(item.ItemId) is { } card
                && CoreObjectFactory.GetCoreTemplate(card.m_templateID) is SpellTemplate { m_Treasure: false }
                ? Kind.Wanted : settings.JunkTreasureCards ? Kind.Junk : Kind.Wanted;
        }

        switch (CoreObjectFactory.GetCoreTemplate(id)) {
            case ReagentItemTemplate:
                return settings.JunkReagents ? Kind.Junk : Kind.Wanted;
            case PetSnackItemTemplate:
                return settings.JunkPetSnacks ? Kind.Junk : Kind.Wanted;
            case SpellTemplate spell:
                return spell.m_Treasure && settings.JunkTreasureCards ? Kind.Junk : Kind.Wanted;
            case WizItemTemplate template:
                var adjectives = template.m_adjectiveList?.Select(a => (string) a).ToList() ?? [];
                if (adjectives.Any(a => a.Equals("Housing", StringComparison.OrdinalIgnoreCase) || a.Equals("Furniture", StringComparison.OrdinalIgnoreCase))) {
                    return settings.JunkFurniture ? Kind.Junk : Kind.Wanted;
                }

                if (template.m_adjectiveList is not null && !PetFactory.IsPetTemplate((uint) Math.Min(id, uint.MaxValue))
                    && ItemHelper.GetItemSlot(template) is { } slot && s_gearSlots.Contains(slot.SlotType)) {
                    return Kind.Gear;
                }

                return Kind.Wanted;
            default:
                return Kind.Wanted;
        }
    }

    // The school a piece of gear is for, or null when any school can wear it.
    private static string GearSchool(DropItem item)
        => ulong.TryParse(item.ItemId, out var id) && CoreObjectFactory.GetCoreTemplate(id) is WizItemTemplate template
            ? (template.m_equipRequirements?.m_requirements ?? [])
                .OfType<ReqSchoolOfFocus>()
                .Where(requirement => !requirement.m_applyNOT && !string.IsNullOrEmpty(requirement.m_magicSchool))
                .Select(requirement => (string) requirement.m_magicSchool)
                .FirstOrDefault()
            : null;

    private static bool IsBoss(ulong templateId)
        => (CoreObjectFactory.GetCoreTemplate(templateId) as GameObjectTemplate)?.m_behaviors?
            .OfType<NPCBehaviorTemplate>().Any(npc => npc.m_bossMob) == true;

}
