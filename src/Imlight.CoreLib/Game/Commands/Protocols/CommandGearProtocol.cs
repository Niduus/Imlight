/*
 * Gives items by kind and name or ID (Justin, 2026-10-20):
 *   .hat 1 Malistaire Veil    .pet 1 Chromatic Pet    .robe 2 1234567
 * Kinds: hat, robe, boot, wand, athame, amulet, ring, pet, gear (any item), deck, potion.
 * The number is optional (1 when left out). With a name, the best match of that kind is given: an exact name
 * first, then the shortest name holding every word.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Bank;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

internal class CommandGearProtocol : CommandProtocol {

    private const int MAX_COPIES = 100;
    private const int MAX_CANDIDATES = 5000;

    internal override string Group { get; set; } = "";

    [Command("hat")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void HatCommand([Remainder] string text) => Give("hat", text, "Hat");

    [Command("robe")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void RobeCommand([Remainder] string text) => Give("robe", text, "Robe");

    [Command("boot")]
    [Alias("boots", "shoe", "shoes")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void BootCommand([Remainder] string text) => Give("boot", text, "Shoes");

    [Command("wand")]
    [Alias("weapon")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void WandCommand([Remainder] string text) => Give("wand", text, "Weapon", "Wand");

    [Command("athame")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void AthameCommand([Remainder] string text) => Give("athame", text, "Athame");

    [Command("amulet")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void AmuletCommand([Remainder] string text) => Give("amulet", text, "Amulet");

    [Command("ring")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void RingCommand([Remainder] string text) => Give("ring", text, "Ring");

    // `.pet <talent|stats|...>` belongs to the pet group; the dispatcher sends `.pet <number> ...` here.
    [Command("pet")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void PetCommand([Remainder] string text) => Give("pet", text, "Pet");

    [Command("gear")]
    [Alias("item")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void GearCommand([Remainder] string text) => Give("gear", text);

    [Command("deck")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void DeckCommand([Remainder] string text) => Give("deck", text, "Deck");

    [Command("potion")]
    [Alias("elixir")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void PotionCommand([Remainder] string text) => Give("potion", text, "Elixir", "Potion");

    private void Give(string command, string text, params string[] kinds) {
        var words = (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var copies = 1;
        if (words.Count > 1 && int.TryParse(words[0], out var number)) {
            copies = number;
            words.RemoveAt(0);
        }

        var query = string.Join(' ', words);
        if (query.Length == 0 || copies < 1) {
            InformSenderClient($"Type .{command} <how many> <name or ID>, such as .{command} 1 {Example(command)}");

            return;
        }

        copies = Math.Min(copies, MAX_COPIES);
        var template = Find(query, kinds, out var problem);
        if (template is null) {
            InformSenderClient(problem);

            return;
        }

        var templateId = (ulong) template.m_templateID;
        var name = ShownName(template);
        var given = 0;
        var placements = new List<Placement>();
        for (var i = 0; i < copies; i++) {
            if (!Context.Character.TryCreateItem(templateId, out var item)) {
                break;
            }

            var placement = ItemPlacement.Place(Context.Character, item);
            ItemPlacement.Announce(Context.SessionActor, Context.Character, item, placement);
            placements.Add(placement);
            if (placement == Placement.Lost) {
                break;
            }

            given++;
        }

        if (given == 0) {
            InformSenderClient(placements.Contains(Placement.Lost)
                ? $"Could not give {name}: the backpack and the bank are full."
                : $"Could not create {name} ({templateId}).");

            return;
        }

        var where = placements.Contains(Placement.Bank) || placements.Contains(Placement.SharedBank)
            ? " Some went to the bank because the backpack is full."
            : "";
        InformSenderClient($"Gave {given} x {name} ({templateId}).{where}");
    }

    private static WizItemTemplate Find(string query, string[] kinds, out string problem) {
        problem = null;

        // An ID is used as it is, whatever kind it is.
        if (ulong.TryParse(query, out var id)) {
            if (CoreObjectFactory.GetCoreTemplate(id) is WizItemTemplate byId && Usable(byId, id)) {
                return byId;
            }

            problem = $"{id} is not an item that can be given.";

            return null;
        }

        if (!IdCatalog.IsReady) {
            problem = "The ID lookup is still being built, so names do not work yet. Use the ID, or try again shortly.";

            return null;
        }

        foreach (var entry in IdCatalog.Search(IdCatalogKind.Item, query, MAX_CANDIDATES)) {
            if (CoreObjectFactory.GetCoreTemplate(entry.Id) is WizItemTemplate template
                    && (kinds.Length == 0 || HasKind(template, kinds)) && Usable(template, entry.Id)) {
                return template;
            }
        }

        problem = $"No {(kinds.Length == 0 ? "item" : kinds[0].ToLowerInvariant())} named \"{query}\" was found. "
            + "Try fewer words, or .lookup item " + query;

        return null;
    }

    private static bool HasKind(WizItemTemplate template, string[] kinds)
        => (template.m_adjectiveList ?? []).Any(a => kinds.Contains((string) a, StringComparer.OrdinalIgnoreCase));

    // Pets must be in the pet list, or the game cannot load them (same check as .mod additem).
    private static bool Usable(WizItemTemplate template, ulong id)
        => PetFactory.IsPetTemplate((uint) id)
            || template.m_behaviors?.Any(b => (string) b?.m_behaviorName == "PetItemBehavior") != true;

    private static string ShownName(WizItemTemplate template) {
        string key = template.m_displayName;
        var shown = string.IsNullOrEmpty(key) ? "" : Locale.GetEnglishName(key);

        return string.IsNullOrWhiteSpace(shown) ? (string) template.m_objectName ?? "item" : shown;
    }

    private static string Example(string command) => command switch {
        "hat" => "Malistaire Veil",
        "pet" => "Chromatic Pet",
        _ => "<name>",
    };

}
