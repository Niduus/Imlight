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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Crafting;
using Imlight.CoreLib.Game.Madlibs;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Ultimus answers "How do I train Tower Shield?" and "Where do I get a Tower Shield treasure card / this hat / this
/// jewel?" from the real game's data, the same lists Ahziael's Vendors &amp; Trainers, Items and Furniture searches show
/// (Niduus, 2026-11-16: "He will answer what he finds in the game's VANILLA files. Never mentioning custom trainers").
/// <list type="bullet">
/// <item>Train: the real game's trainers (VanillaTrainers) and the quests that teach it, with where each stands, the level
/// and spells it needs, and whether the asker can (school, level): when not, he still says how, and why they can't.</item>
/// <item>Get: the vanilla vendors that sell it and where they stand, the bosses that drop it, the quests that give it, the
/// recipes that make it (and who sells those), and the Bazaar last.</item>
/// <item>"Get" a spell is unclear: he asks whether they mean to train it or get a treasure card of it.</item>
/// <item>Nothing found: he says so, never a guess. Every answer says it is the real game's data, which this server may
/// change.</item>
/// </list>
/// Read only: it gives nothing and changes nothing.
/// </summary>
internal static class UltimusLookup {

    private const string NOTE = " (That's from the real game's data; this server may differ.)";
    private const string BAZAAR = "The Bazaar in Wizard City's Shopping District is the last option, when someone has sold one there.";
    private const int MOST_PLACES = 3;
    private static readonly TimeSpan s_askWindow = TimeSpan.FromMinutes(3);

    private static readonly Regex s_train = new(
        @"\b(?:how (?:do|can|would|could|should) (?:i|you|we|one|someone) |how to |where (?:do|can|could) (?:i|you|we) |where to |who (?:can )?)(?:train|learn|teach)(?:es|s)? (?:the spell |the card |a |an |the )?(?<x>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex s_get = new(
        @"\b(?:how (?:do|can|would|could|should) (?:i|you|we|one|someone) |how to |where (?:do|can|could) (?:i|you|we) |where to |who )(?<verb>get|buy|find|obtain|sell)s? (?:a |an |the |some )?(?<x>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex s_sold = new(@"\bwhere (?:is|are) (?:a |an |the )?(?<x>.+?) sold$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A "train or get?" he asked, by asker: the spell's name and when.
    private static readonly ConcurrentDictionary<ulong, (string Spell, DateTime At)> s_asked = new();

    // ---------------------------------------------------------------------
    // The books, read once
    // ---------------------------------------------------------------------

    private sealed record Place(string Name, IReadOnlyList<string[]> Zones);
    private sealed record Teacher(Place Npc, VanillaTrainers.Spell Spell);
    private sealed record QuestTeach(string Title, Place Giver, string School, int Level);
    private sealed record Recipe(string Name, IReadOnlyList<Place> SoldBy);

    private sealed class Books {
        internal readonly Dictionary<string, (string Name, uint Id, List<Teacher> Trainers, List<QuestTeach> Quests)> Spells = new();
        internal readonly Dictionary<string, (string Name, uint Id, List<Place> Vendors, List<Recipe> Recipes)> Cards = new();
        internal readonly Dictionary<uint, int> CardPrices = new();
        internal readonly Dictionary<uint, List<Place>> ItemVendors = new();
        internal readonly Dictionary<uint, List<Recipe>> ItemRecipes = new();
        internal readonly Dictionary<uint, Place> Npcs = new();
    }

    private static volatile Books s_books;
    private static int s_reading;

    /// <summary>The items a sold recipe crafts (from the real game's data), or null while the books are still being read.</summary>
    internal static IReadOnlySet<uint> MadeItems => s_books is { } books ? books.ItemRecipes.Keys.ToHashSet() : null;

    /// <summary>The items a real game shop sells, or null while the books are still being read.</summary>
    internal static IReadOnlySet<uint> SoldItems => s_books is { } books ? books.ItemVendors.Keys.ToHashSet() : null;

    /// <summary>Starts reading the books in the background, if nobody has yet.</summary>
    internal static void Warm() {
        if (s_books is not null || Interlocked.Exchange(ref s_reading, 1) == 1) {
            return;
        }

        _ = Task.Run(() => {
            try {
                s_books = Read();
            }
            catch (Exception e) {
                Logger.Warning("Ultimus lookup: the game's books could not be read: {0}", Logger.Args(e.Message));
                Interlocked.Exchange(ref s_reading, 0);
            }
        });
    }

    private static Books Read() {
        var books = new Books();
        var (zoneNames, zonesOf) = VendorGuide.LoadZones();
        Place PlaceOf(uint id) {
            if (books.Npcs.TryGetValue(id, out var known)) {
                return known;
            }

            var name = "";
            if (CoreObjectFactory.GetCoreTemplate(id) is GameObjectTemplate template) {
                string key = template.m_displayName;
                name = string.IsNullOrEmpty(key) ? "" : Locale.GetEnglishName(key);
                if (string.IsNullOrEmpty(name)) {
                    name = (string) template.m_objectName ?? "";
                }
            }

            var zones = zonesOf.TryGetValue(id, out var at)
                ? at.Where(i => i >= 0 && i < zoneNames.Count).Select(i => zoneNames[i]).ToList() : [];

            return books.Npcs[id] = new Place(name, zones);
        }

        // The real game's trainers.
        foreach (var trainer in VanillaTrainers.All) {
            var place = PlaceOf(trainer.TemplateId) is { Name.Length: > 0 } p ? p : new Place(trainer.Name, []);
            foreach (var spell in trainer.Spells ?? []) {
                SpellEntry(books, spell.Name, spell.Id).Trainers.Add(new Teacher(place, spell));
            }
        }

        // The quests that teach a spell (the files as they are, none of the Manager's quest changes).
        foreach (var quest in SpiralDB.OriginalQuestTemplates) {
            if (quest is null || quest.m_outdated) {
                continue;
            }

            var results = (quest.m_endResults?.m_results ?? []).Concat((quest.m_goals ?? []).Where(g => g is not null)
                .SelectMany(g => g.m_completeResults?.m_results ?? [])).OfType<ResLearnSpell>().ToList();
            if (results.Count == 0) {
                continue;
            }

            string titleKey = quest.m_questTitle;
            var title = string.IsNullOrEmpty(titleKey) ? "" : Locale.GetEnglishName(titleKey);
            var giverId = (uint) QuestMadlibs.GiverTemplateOf(quest);
            var giver = giverId == 0 ? null : PlaceOf(giverId);
            var questSchool = SchoolIn(quest.m_requirements);
            var level = LevelIn(quest.m_requirements);
            foreach (var learn in results) {
                var id = (uint) learn.m_templateID;
                if (id == 0) {
                    continue;
                }

                var name = UltimusText.SpellName(id);
                var entry = SpellEntry(books, name, id);
                var school = SchoolIn(learn.m_requirements) ?? questSchool;
                if (!entry.Quests.Any(q => q.Title == title && q.School == school)) {
                    entry.Quests.Add(new QuestTeach(string.IsNullOrEmpty(title) ? (string) quest.m_questName : title, giver, school, level));
                }
            }
        }

        // The vendors, as Ahziael's Vendors & Trainers search shows them: never our own lists, never a test trainer.
        var recipeSellers = new Dictionary<string, List<Place>>(StringComparer.OrdinalIgnoreCase);
        foreach (var npc in VendorGuide.All()) {
            if (npc.Test.Length > 0) {
                continue;
            }

            var place = new Place(npc.Name, npc.Zones);
            books.Npcs.TryAdd(npc.Id, place);
            foreach (var shop in npc.Shops.Where(s => s.From != "Our own list")) {
                foreach (var item in shop.Items ?? []) {
                    AddTo(books.ItemVendors, item, place);
                }

                foreach (var recipe in shop.Recipes ?? []) {
                    (recipeSellers.TryGetValue(recipe, out var list) ? list : recipeSellers[recipe] = []).Add(place);
                }
            }

            foreach (var code in npc.TreasureCards ?? []) {
                var id = TieredSpells.TemplateIdOf(code);
                if (id != 0) {
                    var card = CardEntry(books, UltimusText.SpellName(id), id);
                    if (SpiralDB.TreasureCardInventories.TryGetValue(npc.Id, out var stock)
                        && stock.TreasureCards?.FirstOrDefault(c => c.SpellName == code) is { Price: > 0 } sold) {
                        books.CardPrices[id] = books.CardPrices.TryGetValue(id, out var price) ? Math.Min(price, sold.Price) : sold.Price;
                    }

                    if (!card.Vendors.Contains(place)) {
                        card.Vendors.Add(place);
                    }
                }
            }
        }

        // The recipes the vendors sell, and what each makes.
        foreach (var (code, sellers) in recipeSellers) {
            if (!Crafting.Crafting.TryFind(code, out var recipeId, out var recipe) || recipe is null) {
                continue;
            }

            var made = new Recipe(Crafting.Crafting.ShownNameOf(recipeId, recipe), sellers);
            string spellCode = recipe.m_spellName;
            if (!string.IsNullOrEmpty(spellCode)) {
                var id = TieredSpells.TemplateIdOf(spellCode);
                if (id != 0) {
                    CardEntry(books, UltimusText.SpellName(id), id).Recipes.Add(made);
                }

                continue;
            }

            var item = (uint) recipe.m_itemID.MParts.TemplateId;
            if (item != 0) {
                (books.ItemRecipes.TryGetValue(item, out var list) ? list : books.ItemRecipes[item] = []).Add(made);
            }
        }

        Logger.Information("Ultimus lookup: read {0} spells to train, {1} treasure cards, {2} items sold and {3} made, from the real game's data.",
            Logger.Args(books.Spells.Count, books.Cards.Count, books.ItemVendors.Count, books.ItemRecipes.Count));

        return books;
    }

    private static (string Name, uint Id, List<Teacher> Trainers, List<QuestTeach> Quests) SpellEntry(Books books, string name, uint id) {
        var key = Norm(name);
        if (!books.Spells.TryGetValue(key, out var entry)) {
            books.Spells[key] = entry = (name, id, [], []);
        }

        return entry;
    }

    private static (string Name, uint Id, List<Place> Vendors, List<Recipe> Recipes) CardEntry(Books books, string name, uint id) {
        var key = Norm(name);
        if (!books.Cards.TryGetValue(key, out var entry)) {
            books.Cards[key] = entry = (name, id, [], []);
        }

        return entry;
    }

    private static void AddTo(Dictionary<uint, List<Place>> map, uint key, Place place) {
        var list = map.TryGetValue(key, out var known) ? known : map[key] = [];
        if (!list.Contains(place)) {
            list.Add(place);
        }
    }

    private static string SchoolIn(RequirementList list) {
        foreach (var requirement in list?.m_requirements ?? []) {
            switch (requirement) {
                case ReqSchoolOfFocus { m_applyNOT: false } focus when !string.IsNullOrEmpty(focus.m_magicSchool):
                    return focus.m_magicSchool;
                case RequirementList nested when SchoolIn(nested) is { } inner:
                    return inner;
            }
        }

        return null;
    }

    private static int LevelIn(RequirementList list) {
        var level = 0;
        foreach (var requirement in list?.m_requirements ?? []) {
            switch (requirement) {
                case ReqMagicLevel { m_applyNOT: false } magic:
                    level = Math.Max(level, (int) magic.m_numericValue);
                    break;
                case RequirementList nested:
                    level = Math.Max(level, LevelIn(nested));
                    break;
            }
        }

        return level;
    }

    // ---------------------------------------------------------------------
    // Answers
    // ---------------------------------------------------------------------

    /// <summary>
    /// His answer to a "how do I train" or "where do I get" question, or null when the line asks something else.
    /// </summary>
    internal static string Answer(ulong askerId, string text) {
        var t = Regex.Replace((text ?? "").Trim(), @"[?!.]+$", "").Trim();
        if (t.Length == 0) {
            return null;
        }

        // The answer to his own "train or get?".
        if (s_asked.TryGetValue(askerId, out var asked) && DateTime.UtcNow - asked.At < s_askWindow) {
            var lower = t.ToLowerInvariant();
            var train = Regex.IsMatch(lower, @"\b(train|learn|spell)\b");
            var card = Regex.IsMatch(lower, @"\b(treasure|tc|card|buy)\b");
            if (train != card) {
                s_asked.TryRemove(askerId, out _);

                return Ready(out var books) ? (train ? Train(books, askerId, asked.Spell) : Card(books, asked.Spell)) : NotReady();
            }
        }

        // "How do I get jewels?", "how do I socket a jewel?": jewels in general (a jewel by its name is an item, below).
        if (JewelAsk(t) is { } jewelAsk) {
            return Ready(out var jewelBooks) ? Jewels(jewelBooks, jewelAsk.Get, jewelAsk.Use) : NotReady();
        }

        string subject;
        bool wantsTrain;
        var find = false;
        if (s_train.Match(t) is { Success: true } trainMatch) {
            subject = trainMatch.Groups["x"].Value;
            wantsTrain = true;
        }
        else if (s_get.Match(t) is { Success: true } getMatch) {
            subject = getMatch.Groups["x"].Value;
            wantsTrain = false;
            find = getMatch.Groups["verb"].Value.Equals("find", StringComparison.OrdinalIgnoreCase);
        }
        else if (s_sold.Match(t) is { Success: true } soldMatch) {
            subject = soldMatch.Groups["x"].Value;
            wantsTrain = false;
        }
        else {
            return null;
        }

        // "How do I get to Olde Town?", "how do I get there": a way, not a thing (BotSocial's tips answer those).
        if (!wantsTrain && Regex.IsMatch(subject, @"^(to|there|here|back|out|in|into|up|down|past|through|around|unstuck|better|stronger|more|money|gold|xp|exp|training points?)\b", RegexOptions.IgnoreCase)) {
            return null;
        }

        var tc = Regex.IsMatch(subject, @"\b(treasure cards?|tcs?)\b", RegexOptions.IgnoreCase);
        subject = Regex.Replace(subject, @"\b(treasure cards?|tcs?|spell|card|please|pls|plz)\b", "", RegexOptions.IgnoreCase);
        subject = Regex.Replace(subject, @"\b(from|in|at) (the )?(game|wizard101|here)$|\b(of|for) it$", "", RegexOptions.IgnoreCase);
        subject = Regex.Replace(subject, @"\s+", " ").Trim(' ', '"', '\'', ',');
        if (subject.Length < 3) {
            return null;
        }

        if (!Ready(out var known)) {
            return NotReady();
        }

        if (wantsTrain) {
            return Train(known, askerId, subject);
        }

        var key = Norm(subject);
        var isSpell = known.Spells.ContainsKey(key);
        var isCard = known.Cards.ContainsKey(key);
        if (tc) {
            return Card(known, subject);
        }

        // "Get" a spell: train it, or a treasure card of it? Asked when both are possible (Niduus, 2026-11-16).
        if (isSpell && isCard) {
            s_asked[askerId] = (subject, DateTime.UtcNow);
            var name = known.Spells[key].Name;

            return $"Do you mean train the spell {name}, or get a {name} treasure card? Say \"train\" or \"card\" and I'll tell you how.";
        }

        if (isSpell) {
            return Train(known, askerId, subject);
        }

        if (isCard) {
            return Card(known, subject);
        }

        // "Where can I find Malorn?": a person or place, not a thing, is left to his normal chat ("where is").
        if (find && (known.Npcs.Values.Any(n => Norm(n.Name) == key) || ZoneNames.Find(subject) is not null)) {
            return null;
        }

        return Item(known, askerId, subject);
    }

    // ---------------------------------------------------------------------
    // Jewels in general (Niduus, 2026-11-19: "Give knowledge of how to access and obtain jewels, then how to use them")
    // ---------------------------------------------------------------------

    private static readonly Regex s_jewelWord = new(@"\b(jewels?|pins?|sockets?|socketing|socketed)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex s_question = new(@"^(how|where|what|who|can|could|do|does|is|are|any|tell|explain)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Every word a general jewel question is made of; anything left over names a jewel (an item ask) or something else.
    private static readonly Regex s_jewelFiller = new(
        @"\b(how|where|what|who|why|when|which|can|could|would|should|do|does|did|i|me|you|we|one|someone|to|get|gets|getting|buy|buying|find|"
        + @"obtain|farm|farming|craft|crafting|crafted|make|making|use|using|used|socket|sockets|socketing|socketed|put|putting|add|adding|"
        + @"equip|equipping|unlock|unlocking|work|works|are|is|a|an|the|my|your|some|any|in|into|on|onto|of|for|from|with|jewels?|pins?|"
        + @"gear|items?|pets?|and|or|them|it|they|there|out|take|taking|remove|removing|place|best|good|tell|about|explain|please|pls|"
        + @"plz|again|more|new|its|their|where's|what's|how's|come|comes|drop|drops|sold|sell|sells|need|go|i'd|like|know)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex s_useWords = new(@"\b(use|using|socket|socketing|socketed|put|putting|add|adding|equip|equipping|unlock|"
                                                    + @"unlocking|work|works|remove|removing|take|taking|place|out)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex s_getWords = new(@"\b(get|gets|getting|buy|buying|find|obtain|farm|farming|craft|crafting|crafted|make|"
                                                    + @"making|where|where's|drop|drops|sold|sell|sells|come|comes|from)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A question about jewels in general: whether it asks how to get them, how to use them (both when neither).</summary>
    private static (bool Get, bool Use)? JewelAsk(string text) {
        if (!s_jewelWord.IsMatch(text) || !s_question.IsMatch(text)) {
            return null;
        }

        var left = Regex.Replace(s_jewelFiller.Replace(text.ToLowerInvariant(), " "), @"[^a-z0-9]+", " ").Trim();
        if (left.Length > 2) {
            return null;
        }

        var use = s_useWords.IsMatch(text);
        var get = s_getWords.IsMatch(text);
        return use || get ? (get, use) : (true, true);
    }

    private static string Jewels(Books books, bool get, bool use) {
        var jewels = BotJewels.All;
        if (jewels.Count == 0) {
            return NotReady();
        }

        var parts = new List<string>();
        if (get) {
            var sold = jewels.Where(j => j.Sold).ToList();
            var dropped = jewels.Count(j => j.Dropped);
            var crafted = jewels.Where(j => j.Crafted).ToList();
            parts.Add($"There are {jewels.Count:N0} jewels and pins in the real game that you can get.");
            if (sold.Count > 0) {
                var shops = sold.SelectMany(j => books.ItemVendors.GetValueOrDefault(j.Id) ?? []).Where(p => p.Name.Length > 0)
                    .GroupBy(p => p.Name).OrderByDescending(g => g.Count()).Take(MOST_PLACES).Select(g => g.First()).ToList();
                parts.Add($"{sold.Count:N0} are sold in shops{(shops.Count > 0 ? $", most of them by {List(shops)}" : "")}.");
            }

            if (dropped > 0) {
                parts.Add($"{dropped:N0} drop from bosses and creatures; ask me about one by name and I'll say who drops it.");
            }

            if (crafted.Count > 0) {
                var sellers = crafted.SelectMany(j => books.ItemRecipes.GetValueOrDefault(j.Id) ?? []).SelectMany(r => r.SoldBy)
                    .Where(p => p.Name.Length > 0).GroupBy(p => p.Name).OrderByDescending(g => g.Count()).Take(2).Select(g => g.First()).ToList();
                parts.Add($"{crafted.Count:N0} are crafted at a {CraftingStation.TypeNames["CraftJewel"]} from recipes"
                          + (sellers.Count > 0 ? $" sold by {List(sellers)}." : "."));
            }

            parts.Add(BAZAAR);
        }

        if (use) {
            var shapes = BotJewels.Shapes.Select(s => (Shape: s, Gives: BotJewels.Gives(s))).Where(x => x.Gives.Length > 0)
                .Select(x => $"{JewelSockets.ShapeName(x.Shape)} gives {x.Gives}").ToList();
            parts.Add("Gear with sockets shows its empty sockets by shape on the item, and a pet has one pet socket."
                      + (shapes.Count > 0 ? $" Mostly, {string.Join("; ", shapes)}." : ""));
            parts.Add("To use one, open your backpack, drag the jewel onto the item, and drop it in an empty socket of the same shape. Some jewels need a level, and pet jewels a pet level.");
            parts.Add("Locked sockets unlock for free on this server.");
            var keep = ConfigurationManager.GetValue("Jewels.KeepShatteredJewels", false);
            parts.Add(keep
                ? "Taking a jewel out gives it back to your backpack here."
                : "Taking a jewel out breaks it, except pins and jewels marked No Shatter, which come back to your backpack.");
        }

        return string.Join(" ", parts) + NOTE;
    }

    private static bool Ready(out Books books) {
        books = s_books;
        if (books is null) {
            Warm();
        }

        return books is not null;
    }

    private static string NotReady()
        => "Give me a minute, I'm still reading the game's books. Ask me again in a moment!";

    private static string Train(Books books, ulong askerId, string subject) {
        if (!books.Spells.TryGetValue(Norm(subject), out var spell)) {
            return books.Cards.TryGetValue(Norm(subject), out var card)
                ? $"No trainer or quest in the real game teaches {card.Name}. It only comes as a treasure card. {Card(books, subject)}"
                : $"I couldn't find a spell called \"{subject}\" that a trainer or quest teaches in the original game's data, so I won't guess. If it's something made just for this server, it isn't in the original game.";
        }

        var (school, level) = Asker(askerId);
        var parts = new List<string>();
        var first = spell.Trainers.FirstOrDefault()?.Spell;
        var shownSchool = first?.School is { Length: > 0 } s ? s : "";
        if (spell.Trainers.Count > 0) {
            var kind = first.Kind is "School" or "" ? "" : $" ({first.Kind} rank {first.Rank})";
            var places = spell.Trainers.Select(tr => tr.Npc).DistinctBy(p => p.Name).ToList();
            parts.Add($"To train {spell.Name}{(shownSchool.Length > 0 ? $" ({shownSchool}" + (first.Kind is "School" or "" && first.Rank > 0 ? $", level {first.Rank}" : "") + ")" : "")}{kind}, "
                      + $"go to {List(places)}.");
            parts.Add(WayThere(places));
            var needs = Needs(first.Needs);
            if (needs.Count > 0) {
                parts.Add($"You need to know {string.Join(" and ", needs)} first.");
            }

            if (first.Kind is "School" or "") {
                if (level > 0 && first.Rank > level) {
                    parts.Add($"You're level {level}, so you can't train it until level {first.Rank}.");
                }

                if (school.Length > 0 && shownSchool.Length > 0 && !Same(school, shownSchool)) {
                    parts.Add($"It's not your school ({school}), so it costs {Math.Max(1, first.Cost)} training point{(first.Cost > 1 ? "s" : "")}.");
                }
            }
        }

        foreach (var quest in spell.Quests.Take(MOST_PLACES)) {
            var from = quest.Giver is { Name.Length: > 0 } giver ? $" from {Where(giver)}" : "";
            var line = $"{(spell.Trainers.Count > 0 ? "It's also" : $"{spell.Name} is")} a reward from the quest \"{quest.Title}\"{from}";
            var limits = new List<string>();
            if (!string.IsNullOrEmpty(quest.School)) {
                limits.Add($"only {quest.School} wizards get it");
            }

            if (quest.Level > 0) {
                limits.Add($"it needs level {quest.Level}");
            }

            line += limits.Count > 0 ? $" ({string.Join(", ", limits)})." : ".";
            if (!string.IsNullOrEmpty(quest.School) && school.Length > 0 && !Same(school, quest.School)) {
                line += $" You're {school}, so you can't get it that way.";
            }
            else if (quest.Level > 0 && level > 0 && level < quest.Level) {
                line += $" You're level {level}, so not yet.";
            }

            parts.Add(line);
        }

        if (BrokenCards.Why(spell.Id) is { } broken) {
            parts.Add($"Heads up: on this server that card is marked broken ({broken.TrimEnd('.')}).");
        }

        return string.Join(" ", parts.Where(p => p.Length > 0)) + NOTE;
    }

    private static string Card(Books books, string subject) {
        if (!books.Cards.TryGetValue(Norm(subject), out var card)) {
            return books.Spells.TryGetValue(Norm(subject), out var spell)
                ? $"No vendor in the real game sells a {spell.Name} treasure card that I know of. {BAZAAR}{NOTE}"
                : $"I couldn't find a treasure card called \"{subject}\" in the game's own data, so I won't guess.";
        }

        var parts = new List<string>();
        if (card.Vendors.Count > 0) {
            parts.Add($"You can buy a {card.Name} treasure card{(books.CardPrices.TryGetValue(card.Id, out var price) ? $" for {price:N0} gold" : "")} from {List(card.Vendors)}.");
            parts.Add(WayThere(card.Vendors));
        }

        foreach (var recipe in card.Recipes.Take(2)) {
            parts.Add($"You can also craft it with the recipe {recipe.Name}{(recipe.SoldBy.Count > 0 ? $", sold by {List(recipe.SoldBy)}" : "")}.");
        }

        parts.Add(BAZAAR);
        if (BrokenCards.Why(card.Id) is { } broken) {
            parts.Add($"Heads up: on this server that card is marked broken ({broken.TrimEnd('.')}).");
        }

        return string.Join(" ", parts.Where(p => p.Length > 0)) + NOTE;
    }

    private static string Item(Books books, ulong askerId, string subject) {
        if (!IdCatalog.IsReady) {
            return NotReady();
        }

        var found = IdCatalog.Search(IdCatalogKind.Item, subject, 60);
        var exact = found.Where(e => Norm(e.Name) == Norm(subject)).ToList();
        if (exact.Count == 0) {
            var names = found.Select(e => e.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (names.Count == 1) {
                exact = found.Where(e => string.Equals(e.Name, names[0], StringComparison.OrdinalIgnoreCase)).ToList();
            }
            else if (names.Count > 1) {
                return $"I found a few things like that: {string.Join(", ", names.Take(4))}. Which one do you mean?";
            }
        }

        if (exact.Count == 0) {
            return $"I couldn't find \"{subject}\" in the original game's data, so I won't guess. If it's something made just for this server, it isn't in the original game.";
        }

        var name = exact[0].Name;
        var (school, level) = Asker(askerId);
        var vendors = new List<Place>();
        var bosses = new List<Place>();
        var quests = new List<string>();
        var recipes = new List<Recipe>();
        foreach (var entry in exact) {
            var id = (uint) entry.Id;
            vendors.AddRange(books.ItemVendors.GetValueOrDefault(id) ?? []);
            recipes.AddRange(books.ItemRecipes.GetValueOrDefault(id) ?? []);
            foreach (var boss in ItemSources.BossesDropping(id)) {
                if (books.Npcs.TryGetValue(boss, out var place) || PlaceOf(books, boss) is { } p && (place = p) is not null) {
                    bosses.Add(place);
                }
            }

            foreach (var quest in ItemSources.QuestsGiving(id)) {
                string key = quest.m_questTitle;
                var title = string.IsNullOrEmpty(key) ? "" : Locale.GetEnglishName(key);
                quests.Add(string.IsNullOrEmpty(title) ? (string) quest.m_questName : title);
            }
        }

        vendors = [.. vendors.Distinct()];
        bosses = [.. bosses.Where(b => b.Name.Length > 0).DistinctBy(b => b.Name)];
        quests = [.. quests.Distinct(StringComparer.OrdinalIgnoreCase)];
        recipes = [.. recipes.DistinctBy(r => r.Name)];
        var parts = new List<string>();
        if (vendors.Count > 0) {
            var gold = exact.Select(e => CoreObjectFactory.GetCoreTemplate((uint) e.Id) as WizItemTemplate).Where(x => x is not null)
                .Select(x => (int) x.m_baseCost).Where(c => c > 0).DefaultIfEmpty(0).Min();
            parts.Add($"{name} is sold{(gold > 0 ? $" for about {gold:N0} gold" : "")} by {List(vendors)}.");
        }

        if (bosses.Count > 0) {
            parts.Add($"{(parts.Count > 0 ? "It's also" : $"{name} is")} dropped by {List(bosses, where: false)}.");
        }

        if (quests.Count > 0) {
            parts.Add($"The quest{(quests.Count > 1 ? "s" : "")} {string.Join(", ", quests.Take(MOST_PLACES).Select(q => $"\"{q}\""))} give{(quests.Count > 1 ? "" : "s")} it.");
        }

        foreach (var recipe in recipes.Take(2)) {
            parts.Add($"You can craft it with the recipe {recipe.Name}{(recipe.SoldBy.Count > 0 ? $", sold by {List(recipe.SoldBy)}" : "")}.");
        }

        if (parts.Count == 0) {
            parts.Add($"I found {name} in the game's data, but no vendor, boss, quest or recipe I know of gives it.");
        }

        var adjectives = exact.Select(e => CoreObjectFactory.GetCoreTemplate((uint) e.Id) as WizItemTemplate)
            .SelectMany(x => (x?.m_adjectiveList ?? []).Select(a => (string) a ?? "")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (adjectives.Contains("FLAG_CrownsOnly")) {
            parts.Add("It's a Crowns item.");
        }

        if (adjectives.Contains("FLAG_NoTrade")) {
            parts.Add("It's no-trade, so nobody can trade it to you.");
        }

        if (adjectives.Contains("FLAG_NoAuction") || adjectives.Contains("FLAG_NoTrade") || adjectives.Contains("FLAG_CrownsOnly")) {
            parts.Add("It's no-auction, so the Bazaar won't have it.");
        }
        else {
            parts.Add(BAZAAR);
        }

        if (vendors.Count > 0) {
            parts.Add(WayThere(vendors));
        }

        // Who can use it: the school and level the item asks for.
        var schools = exact.Select(e => e.School).Where(s => !string.IsNullOrWhiteSpace(s) && !s.Equals("Any", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (schools.Count == 1 && school.Length > 0 && !Same(school, schools[0])) {
            parts.Add($"Only {schools[0]} wizards can use it, and you're {school}.");
        }

        var needLevel = exact.Max(e => e.Level);
        if (needLevel > 0 && level > 0 && level < needLevel) {
            parts.Add($"It needs level {needLevel}; you're level {level}.");
        }

        return string.Join(" ", parts.Where(p => p.Length > 0)) + NOTE;
    }

    // An offer of directions to the first place named (BotSocial's "where is" answers it).
    private static string WayThere(IReadOnlyList<Place> places)
        => places.FirstOrDefault(p => p.Name.Length > 0) is { } first ? $"Ask me \"where is {first.Name}\" and I'll point the way." : "";

    private static Place PlaceOf(Books books, uint id) {
        if (CoreObjectFactory.GetCoreTemplate(id) is not GameObjectTemplate template) {
            return null;
        }

        string key = template.m_displayName;
        var name = string.IsNullOrEmpty(key) ? "" : Locale.GetEnglishName(key);

        return new Place(string.IsNullOrEmpty(name) ? (string) template.m_objectName ?? "" : name, []);
    }

    // The spells and other things a trainer's list says are needed first, in plain words.
    private static List<string> Needs(VanillaTrainers.Need need) {
        var found = new List<string>();
        void Walk(VanillaTrainers.Need n) {
            if (n is null || n.Not) {
                return;
            }

            if (n.Items is { Count: > 0 }) {
                foreach (var item in n.Items) {
                    Walk(item);
                }

                return;
            }

            if (!string.IsNullOrEmpty(n.Spell)) {
                found.Add(n.Spell);
            }
            else if (!string.IsNullOrEmpty(n.Shown)) {
                found.Add(n.Shown);
            }
            else if (!string.IsNullOrEmpty(n.Quest)) {
                found.Add($"the quest \"{n.Quest}\" done");
            }
            else if (!string.IsNullOrEmpty(n.Badge)) {
                found.Add($"the badge {n.Badge}");
            }
        }

        Walk(need);

        return [.. found.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    // "Lydia Greyrose in Ravenwood (Wizard City) or Tarrak Hadfield in ...", at most three, then how many more.
    private static string List(IReadOnlyList<Place> places, bool where = true) {
        var named = places.Where(p => p.Name.Length > 0).DistinctBy(p => p.Name).ToList();
        var shown = named.Take(MOST_PLACES).Select(p => where ? Where(p) : p.Name).ToList();
        var more = named.Count - shown.Count;
        var text = shown.Count switch {
            0 => "a vendor",
            1 => shown[0],
            _ => string.Join(", ", shown.Take(shown.Count - 1)) + " or " + shown[^1],
        };

        return more > 0 ? $"{text} (and {more} more)" : text;
    }

    private static string Where(Place place) {
        if (place.Zones.Count == 0) {
            return place.Name;
        }

        var zone = place.Zones[0];
        var world = MainWorlds.Name((zone[0] ?? "").Split('/')[0]);
        var area = zone.Length > 1 && zone[1].Length > 0 ? zone[1] : ZoneNames.Shown(zone[0]);

        return world.Length > 0 && !string.Equals(world, area, StringComparison.OrdinalIgnoreCase) ? $"{place.Name} in {area} ({world})" : $"{place.Name} in {area}";
    }

    private static (string School, int Level) Asker(ulong askerId) {
        try {
            var online = OnlinePlayerCollection.GetOnlinePlayers().FirstOrDefault(p => p.CharacterId == askerId);

            return (online?.School ?? "", online?.Level ?? 0);
        }
        catch (Exception) {
            return ("", 0);
        }
    }

    private static bool Same(string a, string b) => Norm(a) == Norm(b);

    private static string Norm(string text)
        => Regex.Replace(Regex.Replace((text ?? "").ToLowerInvariant(), @"[^a-z0-9 ]", ""), @"\s+", " ").Trim();

}
