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
using System.Linq;
using Imlight.Common;
using Imlight.CoreLib.Game.Crafting;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.craft` opens the crafting window anywhere, for every recipe you know; `.recipes` lists your recipes.
/// </summary>
internal class CommandCraftProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("craft")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void CraftCommand() {
        if (!Crafting.Crafting.Enabled) {
            InformSenderClient("Crafting is turned off in the server settings ([Crafting] Enabled).");

            return;
        }

        var known = Context.Character.AlchemyBehavior?.RecipeIds?.Count ?? 0;
        Crafting.Crafting.OpenStation(Context.SessionActor, Context.Character,
            new CraftingStation(0, ".craft", CraftingStation.AllTypes, [], false, false, "", "", Anything: true));
        InformSenderClient(known == 0
            ? "The crafting window is open, but you know no recipes yet. Buy some at a recipe shop or use .recipe learn <name>."
            : $"The crafting window is open with the {known} recipes you know.");
    }

    [Command("recipes")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void RecipesCommand() {
        var ids = Context.Character.AlchemyBehavior?.RecipeIds ?? [];
        if (ids.Count == 0) {
            InformSenderClient("You know no recipes yet. Buy some at a recipe shop or use .recipe learn <name>.");

            return;
        }

        var names = ids.Take(20).Select(id => Crafting.Crafting.NameOf(id, Crafting.Crafting.RecipeOf(id)));
        InformSenderClient($"You know {ids.Count} recipes: {string.Join(", ", names)}{(ids.Count > 20 ? ", ..." : "")}");
    }

}

/// <summary>
/// `.recipe learn`, `.recipe forget`, `.recipe find` and `.recipe cooldowns`.
/// </summary>
internal class CommandRecipeProtocol : CommandProtocol {

    internal override string Group { get; set; } = "recipe";

    [Command("learn")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void LearnCommand([Remainder] string recipe) {
        if (!Crafting.Crafting.TryFind(recipe, out var id, out var template)) {
            InformSenderClient($"No recipe is called {recipe}. Try .recipe find <part of its name>.");

            return;
        }

        var name = Crafting.Crafting.NameOf(id, template);
        InformSenderClient(Crafting.Crafting.Learn(Context.SessionActor, Context.Character, id)
            ? $"You learned the recipe {name}."
            : $"You already know the recipe {name}.");
    }

    [Command("forget")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void ForgetCommand([Remainder] string recipe) {
        if (!Crafting.Crafting.TryFind(recipe, out var id, out var template)) {
            InformSenderClient($"No recipe is called {recipe}.");

            return;
        }

        var name = Crafting.Crafting.NameOf(id, template);
        InformSenderClient(Crafting.Crafting.Forget(Context.SessionActor, Context.Character, id)
            ? $"You forgot the recipe {name}."
            : $"You don't know the recipe {name}.");
    }

    // The crafting window checks the game files' amounts before it asks the server; this crafts with the amounts the
    // Manager's Recipes tab set (Niduus, 2026-10-25), anywhere.
    [Command("make")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void MakeCommand([Remainder] string recipe) {
        var quantity = 1;
        var words = (recipe ?? "").Trim();
        var last = words.LastIndexOf(' ');
        if (last > 0 && int.TryParse(words[(last + 1)..], out var count) && Crafting.Crafting.TryFind(words[..last], out _, out _)) {
            (words, quantity) = (words[..last], count);
        }

        var result = Crafting.Crafting.Craft(Context.SessionActor, Context.Character, Context.CharacterObject, words, quantity);
        InformSenderClient(result.Error == CraftError.None
            ? $"Crafted {words} x{result.Crafted}: {result.Made} made, in your backpack or bags."
            : result.Message);
    }

    [Command("find")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void FindCommand([Remainder] string text) {
        var names = Crafting.Crafting.NamesContaining(text, 25);
        InformSenderClient(names.Count == 0
            ? $"No recipe name has {text} in it ({Crafting.Crafting.RecipeCount} recipes in the game)."
            : $"Recipes with {text}: {string.Join(", ", names)}{(names.Count == 25 ? ", ..." : "")}");
    }

    [Command("cooldowns")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void CooldownsCommand(string clear = "") {
        var alchemy = Context.Character.AlchemyBehavior;
        var slots = alchemy?.Cooldowns ?? [];
        if (string.Equals(clear, "clear", StringComparison.OrdinalIgnoreCase)) {
            var count = slots.Count;
            foreach (var slot in slots) {
                slot.FinishedAt = 0;
            }

            Crafting.Crafting.ExpireSlots(Context.SessionActor, Context.Character);
            InformSenderClient($"Freed {count} crafting slots.");

            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        InformSenderClient(slots.Count == 0
            ? $"All {Crafting.Crafting.Slots} crafting slots are free."
            : $"{slots.Count} of {Crafting.Crafting.Slots} crafting slots in use: "
              + string.Join(", ", slots.Select(slot => $"{slot.RecipeName} ({Math.Max(0, slot.FinishedAt - now)} s)"))
              + ". .recipe cooldowns clear frees them.");
    }

}
