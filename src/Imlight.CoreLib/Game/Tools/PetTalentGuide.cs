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
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Tools;

/// <summary>
/// Every pet talent and pet derby talent for the Manager's Database (Niduus, 2026-10-27: "add to database pet talents and
/// derby"), written to pet-talents.json beside ids.csv: each talent's names, rarity, what it does in plain words (with the
/// wizard stat it gives on a pet with the usual top stats), the card it casts or gives, and which pets can have it.
/// </summary>
internal static class PetTalentGuide {

    internal sealed record PetRow(uint Id, string Name, string Object);

    internal sealed record Talent(string Name, string Shown, string Description, string Kind, int Rank, bool Derby,
        bool Retired, bool Hidden, string BreedsAs, List<string> Effects, List<string> Stats, string Card, uint CardId,
        string Buff, float Cooldown, List<uint> Pets, List<uint> Sure);

    internal sealed record Guide(List<PetRow> Pets, List<Talent> Talents);

    private const string FILE = "pet-talents.json";

    // The usual top stats of a fully trained pet, to show what a stat talent gives.
    private static readonly Dictionary<string, int> s_topStats = new(StringComparer.OrdinalIgnoreCase) {
        ["Strength"] = 255, ["Intellect"] = 250, ["Agility"] = 260, ["Will"] = 260, ["Power"] = 250,
    };

    private static readonly JsonSerializerOptions s_write = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Works the list out and writes it (IdCatalog runs it after the server's start).</summary>
    internal static void Write(string folder) {
        try {
            var spellNames = SpellFactory.AllSpellTemplates()
                .Where(s => s.Template is not null && !string.IsNullOrEmpty(s.Template.m_name))
                .GroupBy(s => (string) s.Template.m_name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => Shown(g.First().Template.m_displayName), StringComparer.OrdinalIgnoreCase);

            var pets = new List<PetRow>();
            var petsOf = new Dictionary<string, List<uint>>(StringComparer.OrdinalIgnoreCase);
            var sureOf = new Dictionary<string, List<uint>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, template) in PetFactory.AllPetTemplates.OrderBy(p => p.Key)) {
                var pet = template?.m_behaviors?.OfType<PetItemBehaviorTemplate>().FirstOrDefault();
                if (pet is null) {
                    continue;
                }

                string objectName = template.m_objectName;
                var name = Shown(template.m_displayName);
                pets.Add(new PetRow(id, name.Length > 0 ? name : objectName ?? "", objectName ?? ""));
                void Add(Dictionary<string, List<uint>> to, IEnumerable<object> names) {
                    foreach (var talent in names.Select(n => n?.ToString()).Where(n => !string.IsNullOrEmpty(n)).Distinct()) {
                        if (!to.TryGetValue(talent, out var list)) {
                            to[talent] = list = [];
                        }

                        list.Add(id);
                    }
                }

                Add(petsOf, Names(pet.m_talents).Concat(Names(pet.m_derbyTalents)).Concat(Names(pet.m_guaranteedTalents)).Concat(Names(pet.m_guaranteedDerbyTalents)));
                Add(sureOf, Names(pet.m_guaranteedTalents).Concat(Names(pet.m_guaranteedDerbyTalents)));
            }

            var talents = new List<Talent>();
            foreach (var talent in PetData.AllTalents.OrderBy(t => (string) t.m_talentName, StringComparer.OrdinalIgnoreCase)) {
                try {
                    talents.Add(Describe(talent, petsOf, sureOf, spellNames));
                }
                catch (Exception e) {
                    Logger.Debug("Pet talents: skipped {0}: {1}", Logger.Args((string) talent.m_talentName, e.Message));
                }
            }

            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, FILE);
            File.WriteAllText(path + ".saving", JsonSerializer.Serialize(new Guide(pets, talents), s_write));
            File.Move(path + ".saving", path, overwrite: true);
            Logger.Information("Pet talents for the Manager: {0} talents, {1} derby talents, {2} pets.",
                Logger.Args(talents.Count(t => !t.Derby), talents.Count(t => t.Derby), pets.Count));
        }
        catch (Exception e) {
            Logger.Warning("Pet talents for the Manager could not be written: {0}", Logger.Args(e.Message));
        }
    }

    private static IEnumerable<object> Names(IEnumerable list) => list?.Cast<object>() ?? [];

    private static Talent Describe(PetTalentBaseTemplate talent, Dictionary<string, List<uint>> petsOf,
            Dictionary<string, List<uint>> sureOf, Dictionary<string, string> spellNames) {
        string name = talent.m_talentName;
        var shown = Shown(talent.m_displayName);
        var description = Shown(talent.m_description);
        var effects = new List<string>();
        var stats = new List<string>();
        var kinds = new List<string>();
        string card = null;
        var buff = "";
        var cooldown = 0f;

        string CardShown(string spell) => spell is null ? null
            : spellNames.TryGetValue(spell, out var s) && s.Length > 0 && !s.Equals(spell, StringComparison.OrdinalIgnoreCase) ? $"{s} ({spell})" : spell;

        if (talent is PetDerbyTalentTemplate derby) {
            kinds.Add("Derby");
            buff = Get(derby, "m_isBuff")?.ToString() ?? "";
            cooldown = Get(derby, "m_fCooldown") is float f ? f : 0;
            foreach (var effect in Names(Get(derby, "m_derbyEffects") as IEnumerable)) {
                effects.Add(DerbyEffect(effect));
            }
        }
        else if (talent is PetTalentTemplate combat) {
            foreach (var stat in combat.m_maxStatList ?? []) {
                if (stat is not null && stat.m_value != 0) {
                    effects.Add($"Raises the pet's top {(string) stat.m_name} by {stat.m_value}.");
                    stats.Add(stat.m_name);
                    kinds.Add("Pet stat");
                }
            }

            foreach (var info in combat.m_effectList ?? []) {
                switch (info) {
                    case StatisticEffectInfo statInfo: {
                        kinds.Add("Wizard stat");
                        var template = PetData.GetTalentEffect(statInfo.m_effectName);
                        if (template is null) {
                            effects.Add($"Gives a wizard stat ({(string) statInfo.m_effectName}).");
                            break;
                        }

                        effects.AddRange(StatEffect(template, stats));
                        break;
                    }
                    case ProvideCombatTriggerInfo:
                        kinds.Add("May Cast");
                        card = PetMayCasts.SpellOf(combat);
                        effects.Add(card is null ? "May cast a spell in battle." : $"May cast {CardShown(card)} in battle.");
                        break;
                    case ProvideSpellEffectInfo spellInfo: {
                        kinds.Add("Gives a card");
                        card = spellInfo.m_spellName;
                        var count = Math.Max(1, spellInfo.m_numSpells);
                        effects.Add($"Puts {(count == 1 ? "a" : count.ToString(CultureInfo.InvariantCulture))} {CardShown(card)} card{(count == 1 ? "" : "s")} in your deck as an item card.");
                        break;
                    }
                    default:
                        effects.Add(OtherEffect(info, kinds, ref card, CardShown));
                        break;
                }
            }
        }

        // A talent whose own words say "will cast" is a Will Cast, whatever kind of effect it has.
        var words = $"{shown} {description} {string.Join(' ', effects)}".Replace(" ", "");
        if (words.Contains("willcast", StringComparison.OrdinalIgnoreCase)) {
            kinds.Remove("May Cast");
            kinds.Insert(0, "Will Cast");
        }

        var kind = kinds.Count > 0 ? kinds.Distinct().First() : "Other";
        var cardId = card is not null && SpellFactory.Exists(card) ? SpellFactory.GetSpell(card)?.m_templateID ?? 0u : 0u;

        return new Talent(name, shown, description, kind, talent.m_rank, talent is PetDerbyTalentTemplate, talent.m_bRetired,
            talent.m_unsearchable, (string) talent.m_breedsAsTalentName ?? "", effects, [.. stats.Distinct(StringComparer.OrdinalIgnoreCase)],
            card is null ? "" : CardShown(card), cardId, buff, cooldown,
            petsOf.TryGetValue(name, out var pets) ? pets : [], sureOf.TryGetValue(name, out var sure) ? sure : []);
    }

    // What a wizard stat talent gives: per stat point, and on a pet with the usual top stats.
    private static IEnumerable<string> StatEffect(PetBoostPlayerStatEffectTemplate template, List<string> stats) {
        string first = template.m_primaryStat1;
        string second = template.m_primaryStat2;
        string extra = template.m_secondaryStat;
        var from = new[] { first, second }.Where(s => !string.IsNullOrEmpty(s)).ToList();
        var parts = new List<string>(from);
        var modifier = template.m_secondaryModifier;
        if (!string.IsNullOrEmpty(extra) && modifier != 0) {
            parts.Add(modifier == 0.5f ? $"half of {extra}" : $"{modifier.ToString("0.##", CultureInfo.InvariantCulture)} x {extra}");
        }

        stats.AddRange(from);
        if (!string.IsNullOrEmpty(extra)) {
            stats.Add(extra);
        }

        float Top(string stat) => string.IsNullOrEmpty(stat) ? 0 : s_topStats.GetValueOrDefault(stat);
        var basis = Top(first) + Top(second) + Top(extra) * modifier;
        string school = template.m_school;
        var schoolWord = template.m_buffAll ? "every school" : string.IsNullOrEmpty(school) ? "" : school;

        foreach (var property in typeof(PetBoostPlayerStatEffectTemplate).GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
            if (!s_statWords.TryGetValue(property.Name, out var what)) {
                continue;
            }

            var per = Convert.ToSingle(property.GetValue(template) ?? 0f, CultureInfo.InvariantCulture);
            if (per == 0) {
                continue;
            }

            var percent = property.Name.Contains("Percent", StringComparison.Ordinal);
            var top = per * basis * (percent ? 100 : 1);
            var shownTop = percent ? $"{top.ToString("0.#", CultureInfo.InvariantCulture)}%" : MathF.Round(top).ToString(CultureInfo.InvariantCulture);
            var sign = top >= 0 ? "+" : "";
            var school2 = what.Contains("{school}") ? what.Replace("{school}", schoolWord.Length > 0 ? schoolWord : "every school") : what;
            yield return parts.Count > 0
                ? $"{sign}{shownTop} {school2} on a pet with top stats (from {string.Join(" + ", parts)})."
                : $"{sign}{shownTop} {school2}.";
        }
    }

    private static readonly Dictionary<string, string> s_statWords = new(StringComparer.Ordinal) {
        ["m_damageBonusPercent"] = "{school} damage",
        ["m_damageBonusFlat"] = "flat {school} damage",
        ["m_accuracyBonusPercent"] = "{school} accuracy",
        ["m_armorPiercingBonusPercent"] = "{school} armor piercing",
        ["m_damageReducePercent"] = "{school} resist",
        ["m_damageReduceFlat"] = "flat {school} resist",
        ["m_accuracyReducePercent"] = "less enemy accuracy",
        ["m_healBonusPercent"] = "outgoing healing",
        ["m_healIncBonusPercent"] = "incoming healing",
        ["m_hitPointBonus"] = "max health",
        ["m_spellChargeBonus"] = "spell charges",
        ["m_powerPipBonusPercent"] = "power pip chance",
        ["m_manaBonus"] = "max mana",
        ["m_manaReducePercent"] = "less mana used",
        ["m_expPercent"] = "XP",
        ["m_goldPercent"] = "gold",
        ["m_energyBonus"] = "max energy",
        ["m_criticalHitRating"] = "{school} critical rating",
        ["m_blockRating"] = "{school} critical block rating",
        ["m_accuracyRating"] = "accuracy rating",
        ["m_powerPipRating"] = "power pip rating",
        ["m_damageResistanceRating"] = "resist rating",
        ["m_archmastery"] = "archmastery",
        ["m_archmasteryBonusPercent"] = "archmastery",
        ["m_stunResistancePercent"] = "stun resist",
        ["m_fishingLuckBonusPercent"] = "fishing luck",
        ["m_shadowPipBonusPercent"] = "shadow pip chance",
        ["m_wispBonusPercent"] = "wisp healing",
        ["m_pipConversionRating"] = "pip conversion rating",
        ["m_shadowPipRating"] = "shadow pip rating",
    };

    // Pet powers, XP talents and the gardening and fishing May Casts, read by their field names.
    private static string OtherEffect(object info, List<string> kinds, ref string card, Func<string, string> cardShown) {
        var type = info?.GetType().Name ?? "";
        switch (type) {
            case "ProvidePetPowerInfo": {
                kinds.Add("Pet power");
                var power = Shown(Get(info, "m_powerDisplayName")?.ToString());
                var words = Shown(Get(info, "m_description")?.ToString());
                var happiness = Number(info, "m_happinessCost");
                var wait = Number(info, "m_cooldown");
                var uses = Number(info, "m_usesPerCombat");
                var bits = new List<string>();
                if (happiness > 0) {
                    bits.Add($"costs {happiness} happiness");
                }

                if (wait > 0) {
                    bits.Add($"then waits {Duration(wait)}");
                }

                if (uses > 0) {
                    bits.Add($"{uses} use{(uses == 1 ? "" : "s")} per battle");
                }

                return $"Pet power{(power.Length > 0 ? $" {power}" : "")}{(words.Length > 0 ? $": {words.TrimEnd('.')}" : "")}" +
                       $"{(bits.Count > 0 ? $" ({string.Join(", ", bits)})" : "")}.";
            }
            case "NonCombatMayCastSpellInfo": {
                kinds.Add("May Cast");
                card = Get(info, "m_spellName")?.ToString();
                var chance = Number(info, "m_percentChance");
                var about = Get(info, "m_spellDescription");
                var spell = Shown(Get(about, "m_displaySpellName")?.ToString());
                var words = Shown(Get(about, "m_description")?.ToString());

                return $"May cast {(spell.Length > 0 ? spell : cardShown(card))} outside battle{(chance > 0 ? $" ({chance}% chance)" : "")}" +
                       $"{(words.Length > 0 ? $": {words.TrimEnd('.')}" : "")}.";
            }
            case "CombatPetXPEffectInfo":
            case "CombatGardeningXPEffectInfo": {
                kinds.Add("Extra XP");
                var words = Shown(Get(info, "m_description")?.ToString());
                var what = type.Contains("Gardening") ? "gardening XP" : "pet XP";

                return words.Length > 0 ? $"{words.TrimEnd('.')}." : $"Gives {what} after battles (loot table {Get(info, "m_lootTableName")}).";
            }
            default:
                return $"{type.Replace("Info", "")}.";
        }
    }

    // One derby effect in plain words, by its kind and fields.
    private static string DerbyEffect(object effect) {
        var type = effect?.GetType().Name ?? "";
        var target = Get(effect, "m_kTarget")?.ToString() switch {
            "kTargetSelf" => "your pet",
            "kTargetAll" => "every other racer",
            "kTargetBehind" => "the racer behind",
            "kTargetInFront" => "the racer in front",
            var other => other?.Replace("kTarget", "").ToLowerInvariant() ?? "a racer",
        };
        var seconds = Number(effect, "m_nDuration");
        var lasting = seconds > 0 ? $" for {seconds} seconds" : "";

        string Signed(int value) => value > 0 ? $"+{value}" : value.ToString(CultureInfo.InvariantCulture);

        return type switch {
            "DerbyModifyRate" => $"Speed {Signed(Number(effect, "m_nRateChange"))}% for {target}{lasting}.",
            "DerbyRemoveAction" => $"{Upper(target)} can't {Get(effect, "m_nActionType")?.ToString().ToLowerInvariant() ?? "act"}{lasting}.",
            "DerbyModifyMorale" => $"Morale {Signed(Number(effect, "m_nMoraleChange"))} for {target}{lasting}.",
            "DerbyLockLane" => $"Locks {target} in their lane{lasting}.",
            "DerbyForceLaneChange" => $"Pushes {target} into another lane.",
            "DerbyAddObstacle" => $"Drops {Math.Max(1, Number(effect, "m_nObstacleAmount"))} obstacle{(Number(effect, "m_nObstacleAmount") > 1 ? "s" : "")} " +
                                  $"({Get(effect, "m_lane")?.ToString() switch { "kLaneMine" => "in your lane", "kLaneAll" => "in every lane", var l => l?.Replace("kLane", "lane ") ?? "" }}).",
            "DerbyModifyPetStat" => $"{string.Join(", ", Names(Get(Get(effect, "m_mods"), "m_modifications") as IEnumerable).Select(m => $"{Get(m, "m_name")} {Signed(Number(m, "m_change"))}"))} for {target}{lasting}.",
            "DerbyModifyCheerCost" => $"Cheering on {Get(Get(effect, "m_costMap"), "m_kTerrainType")} costs {Signed(Number(Get(effect, "m_costMap"), "m_nCostChangeAmt"))} for {target}{lasting}.",
            "DerbyModifyMoraleChance" => $"{Signed(Number(Get(effect, "m_moraleChanceActionMap"), "m_nMoraleChanceAmt"))}% morale chance when {target} " +
                                         $"{Get(Get(effect, "m_moraleChanceActionMap"), "m_kActionType")?.ToString().ToLowerInvariant() ?? "acts"}s{lasting}.",
            "DerbyStealMorale" => $"Steals {Number(effect, "m_nAmountToSteal")} morale from {target}.",
            "DerbyAffectEffects" => $"Removes {Number(effect, "m_nNumBuffs")} buff(s) and {Number(effect, "m_nNumDebuffs")} debuff(s) from {target}.",
            "DerbyImmunityEffect" => $"{Upper(target)} can't be hit by {(Get(effect, "m_bToDebuffs") is true ? "debuffs" : "buffs")}{lasting}.",
            "DerbyTeleport" => $"Teleports {target} to {(Get(effect, "m_kTeleportToTarget")?.ToString() == "kTargetInFront" ? "the racer in front" : "another spot")}.",
            _ => $"{type.Replace("Derby", "")} on {target}{lasting}.",
        };
    }

    private static string Upper(string text) => text.Length > 0 ? char.ToUpperInvariant(text[0]) + text[1..] : text;

    private static string Duration(int seconds) => seconds switch {
        >= 3600 when seconds % 3600 == 0 => $"{seconds / 3600} hour{(seconds == 3600 ? "" : "s")}",
        >= 60 when seconds % 60 == 0 => $"{seconds / 60} minute{(seconds == 60 ? "" : "s")}",
        _ => $"{seconds} seconds",
    };

    private static string Shown(string key) {
        if (string.IsNullOrEmpty(key)) {
            return "";
        }

        var text = Locale.GetEnglishName(key);

        return string.IsNullOrWhiteSpace(text) ? "" : text.Trim();
    }

    // A field of a game type, read by name (the derby and pet power types are only needed here).
    private static object Get(object of, string name) {
        if (of is null) {
            return null;
        }

        var type = of.GetType();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        return type.GetProperty(name, flags)?.GetValue(of) ?? type.GetField(name, flags)?.GetValue(of);
    }

    private static int Number(object of, string name) {
        try {
            return Get(of, name) is { } value and not Enum ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : 0;
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException) {
            return 0;
        }
    }

}
