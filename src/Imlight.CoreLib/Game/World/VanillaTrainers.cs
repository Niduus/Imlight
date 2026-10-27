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
 *
 * ========================================================================
 * VANILLA TRAINERS
 * ========================================================================
 *
 * PURPOSE:
 * [Trainers] VanillaTrainers: every spell trainer of the game teaches what it teaches in the real game and nothing
 * else, by the real game's rules: the wizard level (or fishing, gardening or monster magic rank) each spell needs, the
 * spells, school of focus, quests, badges, secondary school and shadow magic it needs first, its training point cost
 * (none for the wizard's own school) and the gold a fishing or garden spell costs.
 *
 * NOTE:
 * The lists come from spiraldb-extra/VanillaTrainers/Trainers.json (Wizard404's copy of each NPC's training list,
 * made by tools/vanilla-trainers/make_vanilla_trainers.py). Custom trainers (Customs tab) and the trainer hall's
 * tomes are not game trainers and keep teaching their own lists; the Customs tab's changes to a game trainer still
 * apply on top of its real list.
 *
 * The server has no Magic Weaving Tome, so a wizard's secondary school is the school of the first spell that needed
 * one which they trained here (kept in their registry); weaving spells also need the game's weaving rank badges.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.World;

internal static class VanillaTrainers {

    /// <summary>Whether game trainers behave as in the real game ([Trainers] VanillaTrainers, live).</summary>
    internal static bool On => ConfigurationManager.GetValue("Trainers.VanillaTrainers", false);

    // The registry entry that keeps a wizard's secondary school (its index in s_schools, from 1).
    private const string SECONDARY_SCHOOL_KEY = "Imlight_SecondarySchool";
    private static readonly string[] s_schools = ["Fire", "Ice", "Storm", "Myth", "Life", "Death", "Balance"];

    internal sealed class Trainer {
        public uint TemplateId { get; set; }
        public string Name { get; set; } = "";
        public string Object { get; set; } = "";
        public List<Spell> Spells { get; set; } = [];
    }

    internal sealed class Spell {
        public uint Id { get; set; }
        public string Name { get; set; } = "";
        public string School { get; set; } = "";
        /// <summary>School (wizard level), Fishing, Gardening or MonsterMagic (that rank).</summary>
        public string Kind { get; set; } = "School";
        public int Rank { get; set; }
        /// <summary>Training points for a wizard of another school.</summary>
        public int Cost { get; set; } = 1;
        public Need Needs { get; set; }
    }

    /// <summary>
    /// One of the game's requirements: a list (Items) or a single check. Items fold left to right, each joining the
    /// next with its own Op, as the game's RequirementList does.
    /// </summary>
    internal sealed class Need {
        public string Class { get; set; } = "";
        public string Op { get; set; } = "AND";
        public bool Not { get; set; }
        public List<Need> Items { get; set; }
        public string Spell { get; set; }
        public uint? Id { get; set; }
        public string School { get; set; }
        public string Entry { get; set; }
        public string Quest { get; set; }
        public string QuestKey { get; set; }
        public string Badge { get; set; }
        public string Shown { get; set; }

        private Requirement _game;

        /// <summary>The game's own requirement object, for the training window.</summary>
        internal Requirement Game => _game ??= ToGame(this);

        internal bool IsOr => string.Equals(Op, "OR", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What a wizard may do about one spell of a vanilla trainer.</summary>
    internal readonly record struct Verdict(bool CanTrain, int Cost, int Gold, int RequiredLevel, Requirement Failed, string Why);

    private static readonly Lazy<IReadOnlyDictionary<uint, Trainer>> s_trainers = new(Load);

    /// <summary>The real game's training list of this NPC, or null when it teaches nothing in the real game.</summary>
    internal static Trainer For(uint templateId)
        => s_trainers.Value.TryGetValue(templateId, out var trainer) ? trainer : null;

    private static IReadOnlyDictionary<uint, Trainer> Load() {
        var path = Path.Combine(SpiralDB.ExtraPath, "VanillaTrainers", "Trainers.json");
        var result = new Dictionary<uint, Trainer>();
        try {
            if (!File.Exists(path)) {
                Logger.Warning("Vanilla trainers: {0} is missing; with VanillaTrainers on, no game trainer teaches.", Logger.Args(path));

                return result;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            foreach (var entry in document.RootElement.GetProperty("Trainers").EnumerateArray()) {
                var trainer = entry.Deserialize<Trainer>(options);
                if (trainer is not null) {
                    result[trainer.TemplateId] = trainer;
                }
            }

            Logger.Information("Vanilla trainers: {0} trainers of the real game, {1} spells.",
                Logger.Args(result.Count, result.Values.Sum(t => t.Spells.Count)));
        }
        catch (Exception e) {
            Logger.Error("Vanilla trainers: could not read {0}: {1}", Logger.Args(path, e.Message));
        }

        return result;
    }

    /// <summary>
    /// Judges one spell for a wizard by the real game's rules. <paramref name="spell"/> is null for a spell the
    /// Customs tab added to a game trainer: it needs nothing and costs one training point (none for the wizard's
    /// school), or <paramref name="priceOverride"/>.
    /// </summary>
    internal static Verdict Judge(Wizard wizard, Spell spell, SpellTemplate template, int? priceOverride = null) {
        var kind = spell?.Kind ?? "School";
        var rank = spell?.Rank ?? 0;
        string school = template?.m_sMagicSchoolName;
        var ownSchool = string.Equals(school, wizard.MagicSchoolBehavior.MagicSchool.ToString(), StringComparison.OrdinalIgnoreCase);

        var cost = priceOverride ?? (kind != "School" || ownSchool ? 0 : spell?.Cost ?? 1);
        var gold = kind switch {
            "Gardening" => Imlight.CoreLib.Game.Gardening.GardenLessons.PriceOf(template),
            "Fishing" => Math.Max(0, template?.m_baseCost ?? 0),
            _ => 0,
        };

        var (has, needed) = kind switch {
            "Fishing" => ((int) wizard.GameStats.m_fishingLevel, rank),
            "Gardening" => (Imlight.CoreLib.Game.Gardening.GardenLessons.RankOf(wizard), rank),
            "MonsterMagic" => ((int) wizard.GameStats.m_monsterMagicLevel, rank),
            _ => ((int) wizard.MagicSchoolBehavior.Level, rank),
        };
        var requiredLevel = kind == "School" ? Math.Max(rank, 1) : 0;
        if (has < needed) {
            return new(false, cost, gold, requiredLevel, null, $"needs {(kind == "School" ? "level" : kind + " rank")} {needed} (has {has})");
        }

        if (spell?.Needs is { } needs) {
            var knownNames = KnownNames(wizard);
            if (!Meets(needs, wizard, knownNames, out var failed)) {
                return new(false, cost, gold, requiredLevel, failed?.Game, $"needs {Describe(failed)}");
            }
        }

        if (wizard.MagicSchoolBehavior.TrainingPoints < cost) {
            return new(false, cost, gold, requiredLevel, null, $"needs {cost} training points (has {wizard.MagicSchoolBehavior.TrainingPoints})");
        }

        if (wizard.GameStats.m_currentGold < gold) {
            return new(false, cost, gold, requiredLevel, null, $"needs {gold} gold (has {wizard.GameStats.m_currentGold})");
        }

        return new(true, cost, gold, requiredLevel, null, null);
    }

    /// <summary>
    /// After a wizard trains a spell that needs a secondary school, that school becomes theirs (when they had none).
    /// </summary>
    internal static void Trained(Wizard wizard, Spell spell) {
        if (spell?.Needs is null || SecondarySchoolOf(wizard) is not null || FindSecondary(spell.Needs) is not { } school) {
            return;
        }

        var index = Array.FindIndex(s_schools, s => string.Equals(s, school, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) {
            wizard.SetRegistryValue(SECONDARY_SCHOOL_KEY, (ulong) index + 1);
            Logger.Information("Vanilla trainers: {0}'s secondary school is now {1}.", Logger.Args(wizard.PlayerNameBehavior.GetWizardName(), s_schools[index]));
        }
    }

    /// <summary>The wizard's secondary school, or null before they have one.</summary>
    internal static string SecondarySchoolOf(Wizard wizard) {
        var value = wizard.HasRegistryValue(SECONDARY_SCHOOL_KEY) ? wizard.GetRegistryValue(SECONDARY_SCHOOL_KEY) : 0;

        return value >= 1 && value <= (ulong) s_schools.Length ? s_schools[value - 1] : null;
    }

    private static string FindSecondary(Need need)
        => need.Class == "SecondarySchool" && !need.Not ? need.School
         : need.Items?.Select(FindSecondary).FirstOrDefault(s => s is not null);

    private static HashSet<string> KnownNames(Wizard wizard) {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in wizard.SpellbookBehavior.LearnedSpellTemplateIds ?? []) {
            if (CoreObjectFactory.GetCoreTemplate(id) is SpellTemplate known && (string) known.m_name is { Length: > 0 } name) {
                names.Add(name);
            }
        }

        return names;
    }

    // Left to right, as RequirementDispatcher and the client do: an item joins the running answer with the operator
    // of the item before it. The first check that fails while it still matters is kept for the training window.
    private static bool Meets(Need list, Wizard wizard, HashSet<string> knownNames, out Need failed) {
        failed = null;
        if (list.Items is null || list.Items.Count == 0) {
            return true;
        }

        var result = true;
        var joinsWithOr = false;
        foreach (var item in list.Items) {
            var canChange = joinsWithOr ? !result : result;
            if (canChange) {
                Need itemFailed = null;
                var met = item.Class == "List"
                    ? Meets(item, wizard, knownNames, out itemFailed)
                    : MeetsOne(item, wizard, knownNames);
                result = met != item.Not;
                if (!result && failed is null) {
                    failed = item.Class == "List" ? itemFailed ?? item : item;
                }
                else if (result && joinsWithOr) {
                    failed = null;
                }
            }

            joinsWithOr = item.IsOr;
        }

        if (result) {
            failed = null;
        }

        return result;
    }

    private static bool MeetsOne(Need need, Wizard wizard, HashSet<string> knownNames) {
        switch (need.Class) {
            case "HasSpell":
                return need.Id is { } id && wizard.SpellbookBehavior.HasSpell(id) || (need.Spell is { } spell && knownNames.Contains(spell));
            case "SchoolOfFocus":
                return string.Equals(need.School, wizard.MagicSchoolBehavior.MagicSchool.ToString(), StringComparison.OrdinalIgnoreCase);
            case "HasEntry": {
                if (string.IsNullOrEmpty(need.Entry)) {
                    return false;
                }

                const string COMPLETE = "_Complete";

                return wizard.HasRegistryValue(need.Entry)
                    // A quest the Manager's Quest Editor skips counts as done.
                    || (need.Entry.EndsWith(COMPLETE, StringComparison.OrdinalIgnoreCase)
                        && Imlight.CoreLib.Game.Quests.QuestOptions.IsSkipped(need.Entry[..^COMPLETE.Length]));
            }
            case "HasBadge":
                return !string.IsNullOrEmpty(need.Badge)
                    && Imlight.CoreLib.Game.Badges.BadgeCatalog.Current.TryGetByName(need.Badge, out var badge)
                    && Imlight.CoreLib.Game.Badges.BadgeView.IsComplete(wizard.BadgeBehavior, badge);
            case "SecondarySchool": {
                // A wizard without one yet may take this school as theirs by training the spell (Trained).
                var secondary = SecondarySchoolOf(wizard);

                return secondary is null || string.Equals(secondary, need.School, StringComparison.OrdinalIgnoreCase);
            }
            case "ShadowMagic":
                return wizard.GameStats.m_shadowMagicUnlocked || Imlight.CoreLib.Game.Combat.CombatDuelSubCircle.ShadowPipsOn;
            default:
                return false;
        }
    }

    private static string Describe(Need need)
        => need?.Class switch {
            null => "something",
            "HasSpell" => $"the spell {need.Spell}",
            "SchoolOfFocus" => $"{(need.Not ? "a school other than " : "")}{need.School} as school",
            "HasEntry" => $"the quest {(string.IsNullOrEmpty(need.Quest) ? need.Entry : need.Quest)} done",
            "HasBadge" => $"the badge {(string.IsNullOrEmpty(need.Shown) ? need.Badge : need.Shown)}",
            "SecondarySchool" => $"{need.School} as secondary school",
            "ShadowMagic" => "shadow magic",
            _ => need.Class,
        };

    private static Operator OperatorOf(Need need) => need.IsOr ? Operator.ROP_OR : Operator.ROP_AND;

    private static Requirement ToGame(Need need)
        => need.Class switch {
            "List" => new RequirementList {
                m_applyNOT = need.Not, m_operator = OperatorOf(need),
                m_requirements = [.. (need.Items ?? []).Select(item => item.Game)],
            },
            "HasSpell" => new ReqHasSpell { m_applyNOT = need.Not, m_operator = OperatorOf(need), m_spellName = need.Spell ?? "" },
            "SchoolOfFocus" => new ReqSchoolOfFocus { m_applyNOT = need.Not, m_operator = OperatorOf(need), m_magicSchool = need.School ?? "" },
            "HasEntry" => new ReqHasEntry {
                m_applyNOT = need.Not, m_operator = OperatorOf(need), m_entryName = need.Entry ?? "", m_displayName = need.QuestKey ?? "",
            },
            "HasBadge" => new ReqHasBadge { m_applyNOT = need.Not, m_operator = OperatorOf(need), m_badgeName = need.Badge ?? "" },
            "SecondarySchool" => new ReqHasSecondarySchool { m_applyNOT = need.Not, m_operator = OperatorOf(need), m_secondarySchool = need.School ?? "" },
            "ShadowMagic" => new ReqHasShadowMagic { m_applyNOT = need.Not, m_operator = OperatorOf(need) },
            _ => new RequirementList { m_applyNOT = need.Not, m_operator = OperatorOf(need), m_requirements = [] },
        };

    /// <summary>The spell's requirements as the game's own list, for the training window, or null.</summary>
    internal static RequirementList ShownRequirements(Spell spell)
        => spell?.Needs?.Game as RequirementList;

}
