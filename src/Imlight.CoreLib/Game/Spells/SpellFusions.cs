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
 *
 * ========================================================================
 * SPELL FUSION (FUSION FORMULAS)
 * ========================================================================
 *
 * PURPOSE:
 * Spell Fusion as the game has it (Selenopolis update): a wizard learns Fusion Formulas (the "FUSE - ..." spells) from
 * trainers, and in battle two cards of a formula in the hand fuse into the fused spell, which is then cast like any
 * other card.
 *
 * USAGE EXAMPLE:
 * var ids = SpellFusions.HandFusionIds(wizard);          // Hand.m_spellFusionIDList
 * var fused = SpellFusions.Fuse(wizard, primary, secondary, requestedId, out var why);
 *
 * NOTE:
 * - Root.wad SpellFusions/*.xml are the 132 recipes (SpellFusionTemplate): m_primaryIngredient and
 *   m_secondaryIngredient each name an adjective ("Fusion_Banshee"); every card with that adjective (all tiers, the
 *   pet and amulet versions) is that ingredient. The fused spells (278, Spells/Fusion Spells) name their recipe in
 *   SpellTemplate.m_spellFusion; a recipe of a tiered spell has one fused spell per tier and path.
 * - Read from the game program (CombatResolver::CastSpellFusion, 2026-10-29): the game sends a fusion as
 *   MSG_COMBATMOVE MoveType 12, SpellSelection = the first card, SpellTarget = the second card (plain index),
 *   SelectedTieredSpellID = the fused spell picked (0 or one not allowed = the first one). It looks the recipes up
 *   from the hand's list of fused spells (Hand.m_spellFusionIDList, fused spell template IDs): a recipe counts when
 *   the adjectives of the two cards match it, in either order. Both cards leave the hand and the fused card takes
 *   the first card's place.
 * - A recipe is the wizard's when the wizard knows one of its fused spells ([SpellFusion] NeedFormula, default
 *   true; off = every recipe works for everyone).
 *
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/29/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.Cryptography;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Spells;

/// <summary>
/// Spell Fusion: two cards of a Fusion Formula fuse into one fused spell in battle.
/// </summary>
internal static class SpellFusions {

    /// <summary>The MSG_COMBATMOVE MoveType the game sends for a fusion.</summary>
    internal const byte FUSE_MOVE_TYPE = 12;

    private const string FOLDER = "SpellFusions/";

    // [SpellFusion]
    internal static bool Enabled => ConfigurationManager.GetValue("SpellFusion.Enabled", true);
    private static bool s_needFormula => ConfigurationManager.GetValue("SpellFusion.NeedFormula", true);

    /// <summary>A recipe: its ID, name, the two ingredient adjectives and its fused spells (template IDs).</summary>
    internal sealed record Recipe(uint Id, string Name, string Primary, string Secondary, List<uint> Outputs);

    private static readonly object s_lock = new();
    private static List<Recipe> s_recipes;
    private static Dictionary<uint, Recipe> s_recipeOfOutput;
    // Ingredient adjective -> every card with it (template ID and the template).
    private static Dictionary<string, List<(uint Id, SpellTemplate Template)>> s_cardsByAdjective;

    /// <summary>Every recipe the game has.</summary>
    internal static IReadOnlyList<Recipe> Recipes {
        get {
            EnsureLoaded();

            return s_recipes;
        }
    }

    /// <summary>The recipe a fused spell is made with, or null.</summary>
    internal static Recipe RecipeOf(uint fusedTemplateId) {
        EnsureLoaded();

        return s_recipeOfOutput.GetValueOrDefault(fusedTemplateId);
    }

    private static void EnsureLoaded() {
        if (s_recipes is not null) {
            return;
        }

        lock (s_lock) {
            if (s_recipes is not null) {
                return;
            }

            var recipes = new List<Recipe>();
            var byId = new Dictionary<uint, Recipe>();
            try {
                foreach (var location in CoreObjectFactory.TemplateManifest?.m_serializedTemplates ?? []) {
                    string file = location?.m_filename;
                    if (file is null || !file.StartsWith(FOLDER, StringComparison.OrdinalIgnoreCase)) {
                        continue;
                    }

                    if (CoreObjectFactory.GetCoreTemplate(location.m_id) is not SpellFusionTemplate template) {
                        continue;
                    }

                    string primary = template.m_primaryIngredient?.m_adjective;
                    string secondary = template.m_secondaryIngredient?.m_adjective;
                    if (string.IsNullOrEmpty(primary) || string.IsNullOrEmpty(secondary)) {
                        continue;
                    }

                    var recipe = new Recipe((uint) location.m_id, (string) template.m_debugName ?? file, primary, secondary, []);
                    recipes.Add(recipe);
                    byId.TryAdd(recipe.Id, recipe);
                }

                var outputs = new Dictionary<uint, Recipe>();
                var adjectives = new HashSet<string>(recipes.SelectMany(r => new[] { r.Primary, r.Secondary }), StringComparer.OrdinalIgnoreCase);
                var cards = new Dictionary<string, List<(uint Id, SpellTemplate Template)>>(StringComparer.OrdinalIgnoreCase);
                foreach (var (_, spell) in SpellFactory.AllSpellTemplates()) {
                    if (spell is null) {
                        continue;
                    }

                    foreach (var adjective in spell.m_adjectives?.Select(a => (string) a).Where(a => a is not null && adjectives.Contains(a)) ?? []) {
                        var cardId = SpellFactory.TemplateIdOf(spell);
                        if (cardId != 0) {
                            (cards.TryGetValue(adjective, out var list) ? list : cards[adjective] = []).Add((cardId, spell));
                        }
                    }

                    if (spell.m_spellFusion == 0 || !byId.TryGetValue(spell.m_spellFusion, out var recipe)) {
                        continue;
                    }

                    var id = SpellFactory.TemplateIdOf(spell);
                    if (id == 0) {
                        continue;
                    }

                    recipe.Outputs.Add(id);
                    outputs.TryAdd(id, recipe);
                }

                s_recipeOfOutput = outputs;
                s_cardsByAdjective = cards;
                Logger.Information("Spell Fusion: {0} recipes, {1} fused spells.", Logger.Args(recipes.Count, outputs.Count));
            }
            catch (Exception ex) {
                Logger.Error("Spell Fusion: could not read the recipes: {0}", Logger.Args(ex));
                s_recipeOfOutput ??= [];
                s_cardsByAdjective ??= [];
            }

            s_recipes = recipes;
        }
    }

    /// <summary>Every card that is this ingredient (template IDs): all tiers, pet and amulet versions.</summary>
    internal static List<uint> CardsOf(string adjective) {
        EnsureLoaded();

        return s_cardsByAdjective.TryGetValue(adjective ?? "", out var list) ? [.. list.Select(c => c.Id)] : [];
    }

    /// <summary>
    /// The card a wizard trains for an ingredient ("Shadow Creature Healer" for Fusion_DarkShepherd): a trained spell
    /// before a boss copy, a treasure card or an upgraded tier.
    /// </summary>
    internal static uint MainCardOf(string adjective) {
        EnsureLoaded();
        if (!s_cardsByAdjective.TryGetValue(adjective ?? "", out var list) || list.Count == 0) {
            return 0;
        }

        // A trainer's spell (the trainers' lists name them), not a boss copy ("Dark Shepherd NA"), a treasure card
        // or an upgraded tier.
        var trained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try {
            trained.UnionWith(VanillaTrainers.All.SelectMany(t => t.Spells).Select(s => s.Name));
        }
        catch (Exception ex) {
            Logger.Warning("Spell Fusion: could not read the trainers' lists: {0}", Logger.Args(ex.Message));
        }

        int Rank(SpellTemplate t) {
            var name = (string) t.m_name ?? "";
            var rank = trained.Contains(name) ? 0 : 4;
            if (t.m_Treasure) {
                rank += 2;
            }

            if (name.Contains(" - T0", StringComparison.Ordinal) && !name.Contains("T01 - Base", StringComparison.Ordinal)) {
                rank++;
            }

            return rank;
        }

        return list.OrderBy(c => Rank(c.Template)).ThenBy(c => ((string) c.Template.m_name ?? "").Length).First().Id;
    }

    /// <summary>
    /// The ingredients of a recipe the wizard knows no card of. The game puts a formula in the deck only when the
    /// wizard knows a card of each ingredient (FusionDeckPage, read 2026-11-01); otherwise the click does nothing.
    /// </summary>
    internal static List<string> MissingIngredients(Wizard wizard, Recipe recipe) {
        var book = wizard?.SpellbookBehavior;
        if (book is null || recipe is null) {
            return [];
        }

        return [.. new[] { recipe.Primary, recipe.Secondary }.Distinct().Where(a => !CardsOf(a).Any(book.HasSpell))];
    }

    /// <summary>Whether a spell template has the adjective.</summary>
    internal static bool HasAdjective(SpellTemplate template, string adjective)
        => template?.m_adjectives?.Any(a => string.Equals((string) a, adjective, StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>The recipes the wizard can use: every one, or those with a fused spell the wizard knows.</summary>
    internal static List<Recipe> KnownRecipes(Wizard wizard) {
        EnsureLoaded();
        if (!s_needFormula) {
            return [.. s_recipes];
        }

        var known = new HashSet<Recipe>();
        foreach (var id in wizard?.SpellbookBehavior?.LearnedSpellTemplateIds?.ToList() ?? []) {
            if (s_recipeOfOutput.TryGetValue(id, out var recipe)) {
                known.Add(recipe);
            }
        }

        return [.. known];
    }

    /// <summary>
    /// The hand's list of fused spells (Hand.m_spellFusionIDList): every fused spell of the recipes the wizard can use.
    /// </summary>
    internal static List<uint> HandFusionIds(Wizard wizard) {
        if (!Enabled || wizard is null) {
            return [];
        }

        try {
            return [.. KnownRecipes(wizard).SelectMany(r => r.Outputs).Distinct()];
        }
        catch (Exception ex) {
            Logger.Error("Spell Fusion: could not list the fusions of {0}: {1}", Logger.Args(wizard.CharId, ex));

            return [];
        }
    }

    /// <summary>The recipes of these two cards, in either order.</summary>
    internal static List<Recipe> RecipesFor(SpellTemplate first, SpellTemplate second, IEnumerable<Recipe> among)
        => [.. among.Where(r => (HasAdjective(first, r.Primary) && HasAdjective(second, r.Secondary))
                                || (HasAdjective(first, r.Secondary) && HasAdjective(second, r.Primary)))];

    /// <summary>
    /// The fused spell two cards make: the one asked for when it is allowed; else the one on the same tier and path as
    /// the tiered card ("Blast Off - T02 - A" makes "... - Shadow - T02 - A"); else the first.
    /// </summary>
    internal static uint PickOutput(List<Recipe> recipes, SpellTemplate first, SpellTemplate second, int requested) {
        var outputs = recipes.SelectMany(r => r.Outputs).Distinct().ToList();
        if (outputs.Count == 0) {
            return 0;
        }

        if (requested > 0) {
            var asked = (uint) requested;
            if (outputs.Contains(asked)) {
                return asked;
            }

            // The game may name the spell by its name hash.
            var byHash = outputs.FirstOrDefault(o => CoreObjectFactory.GetCoreTemplate(o) is SpellTemplate t
                                                   && StringHash.Compute(t.m_name) == asked);
            if (byHash != 0) {
                return byHash;
            }
        }

        foreach (var card in new[] { first, second }) {
            var tier = TierOf(card?.m_name);
            if (tier is null) {
                continue;
            }

            var same = outputs.FirstOrDefault(o => CoreObjectFactory.GetCoreTemplate(o) is SpellTemplate t && TierOf(t.m_name) == tier);
            if (same != 0) {
                return same;
            }
        }

        // The plain fused spell before a "T01 - TEMP" or upgraded one.
        return outputs.OrderBy(o => TierOf(CoreObjectFactory.GetCoreTemplate(o) is SpellTemplate t ? (string) t.m_name : null) is null ? 0 : 1)
            .First();
    }

    // "T02 - A" from "Blast Off - T02 - A"; null for a spell that is not upgraded (or "T01 - Base"/"T01 - TEMP").
    private static string TierOf(string name) {
        if (string.IsNullOrEmpty(name)) {
            return null;
        }

        var at = name.LastIndexOf(" - T", StringComparison.Ordinal);
        if (at < 0) {
            return null;
        }

        var tier = name[(at + 3)..];
        return tier.StartsWith("T01", StringComparison.Ordinal) ? null : tier;
    }

    /// <summary>
    /// Marks the cards of a hand for the game: FS_Valid when another card in the hand fuses with it, FS_Partial when it
    /// is a card of a usable recipe whose other card is not there, FS_Invalid otherwise.
    /// </summary>
    internal static void MarkHand(List<Spell> cards, List<Recipe> recipes) {
        if (cards is null) {
            return;
        }

        try {
            var templates = cards.Select(c => c is null ? null : CoreObjectFactory.GetCoreTemplate(c.m_templateID) as SpellTemplate).ToList();
            for (var i = 0; i < cards.Count; i++) {
                if (cards[i] is null) {
                    continue;
                }

                var state = FusionState.FS_Invalid;
                var mine = templates[i];
                if (mine is not null && recipes.Count > 0) {
                    var partOf = recipes.Where(r => HasAdjective(mine, r.Primary) || HasAdjective(mine, r.Secondary)).ToList();
                    if (partOf.Count > 0) {
                        state = FusionState.FS_Partial;
                        for (var j = 0; j < cards.Count && state != FusionState.FS_Valid; j++) {
                            if (j != i && templates[j] is { } other && RecipesFor(mine, other, partOf).Count > 0) {
                                state = FusionState.FS_Valid;
                            }
                        }
                    }
                }

                cards[i].m_fusionState = state;
            }
        }
        catch (Exception ex) {
            Logger.Error("Spell Fusion: could not mark the hand: {0}", Logger.Args(ex));
        }
    }
}
