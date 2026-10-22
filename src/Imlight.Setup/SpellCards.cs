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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Wad;

namespace Imlight.Setup;

/// <summary>One number a spell's description can show: an effect's amount, school and rounds.</summary>
internal sealed record SpellEffectInfo(int Amount, string School, int Rounds, string Kind = "", string Target = "");

/// <summary>
/// One spell as the game's card shows it, read from the game's own spell file.
/// </summary>
internal sealed class SpellCard {

    internal required uint Id { get; init; }

    /// <summary>The name shown on the card.</summary>
    internal required string Name { get; init; }

    /// <summary>The game's own name for the file, which tells same-named spells apart (such as "Cleanse Ward - Amulet").</summary>
    internal required string Code { get; init; }

    internal required string School { get; init; }

    internal required string Type { get; init; }

    internal required int Pips { get; init; }

    internal required bool XPips { get; init; }

    internal required int ShadowPips { get; init; }

    /// <summary>School pips the spell also costs, such as 2 Fire pips.</summary>
    internal required IReadOnlyList<(string School, int Count)> SchoolPips { get; init; }

    internal required int Accuracy { get; init; }

    internal required bool Treasure { get; init; }

    internal required string ImageName { get; init; }

    internal required int ImageIndex { get; init; }

    /// <summary>The description in the game's own markup, such as "#1:$eA$ #2:$dT_image$#3:$Damage_image$".</summary>
    internal required string Description { get; init; }

    internal required IReadOnlyList<SpellEffectInfo> Effects { get; init; }

    internal required int Level { get; init; }

    internal required string File { get; init; }

    /// <summary>Where the card comes from in the game: Caster, Pet, ShadowCreature, Weapon or Equipment (an item card).</summary>
    internal string Source { get; init; } = "Caster";

    /// <summary>A card frame the spell asks for by name, when it has one of its own.</summary>
    internal string CardFront { get; init; } = "";

    internal bool NoPvEEnchant { get; init; }

    internal bool NoPvPEnchant { get; init; }

    internal bool NoDiscard { get; init; }

    internal bool PvP { get; init; } = true;

    internal bool PvE { get; init; } = true;

    internal bool Cloaked { get; init; }

    internal int MaxCopies { get; init; }

    internal string Category { get; init; } = "";

    /// <summary>Summons a minion (a SummonCreature effect), whatever its type says.</summary>
    internal bool Minion { get; init; }

    /// <summary>
    /// A spell players never get: a monster's or boss's own copy, a cheat, a test or a fight's special version.
    /// </summary>
    internal bool Hidden { get; set; }

    /// <summary>An item card: one a wand, hat, robe, amulet or other piece of gear gives.</summary>
    internal bool Item => Source is "Equipment" or "Weapon";

    /// <summary>Cards that come as counted copies, which the game marks "x1" in a deck: treasure and item cards.</summary>
    internal bool Counted => Treasure || Item;

    /// <summary>The description as plain words, filled in by <see cref="SpellBook"/>.</summary>
    internal string Short { get; set; } = "";

    internal string Haystack { get; set; } = "";

    /// <summary>Whether Imlight's server does the spell (<see cref="SpellSupport"/>).</summary>
    internal SpellSupportResult Support { get; set; } = SpellSupportResult.Unknown;

    internal bool Broken => Support.Status == SpellStatus.Broken;

    internal bool Partly => Support.Status == SpellStatus.Partly;

    /// <summary>What the spell does, written by Imlight from its effects (<see cref="SpellSupport.Describe"/>).</summary>
    internal string Explained { get; set; } = "";

    /// <summary>The game's card text is blank, or only a placeholder or an unread text key.</summary>
    internal bool Blank { get; set; }

    /// <summary>What the card does: the game's own words, then Imlight's corrected words in braces (<see cref="Shown"/>).</summary>
    internal string Does => Shown;

    /// <summary>
    /// What the code does, in Imlight's words written from the spell's effects, shown in braces after the game's text on
    /// every card (Niduus, 2026-10-22: "ALL cards ... should have their written text and {code text}"), or "".
    /// </summary>
    internal string Corrected => Explained;

    /// <summary>
    /// The game's text with what the code does after it in braces, such as "710 Death Damage and ... {Deals 710 Death
    /// damage ...}"; the braced words are searchable too.
    /// </summary>
    internal string Shown => Corrected.Length == 0 ? Short : $"{Short} {{{Corrected.TrimEnd('.')}}}".Trim();

    internal string Command => $".sb learn {Id}";

    /// <summary>What it costs, such as "3 pips", "X pips" or "4 pips + 2 shadow".</summary>
    internal string Cost {
        get {
            var parts = new List<string> { XPips ? "X pips" : Pips == 1 ? "1 pip" : $"{Pips} pips" };
            parts.AddRange(SchoolPips.Select(p => $"{p.Count} {p.School}"));
            if (ShadowPips > 0) {
                parts.Add($"{ShadowPips} shadow");
            }

            return string.Join(" + ", parts);
        }
    }

    internal string Kind => Treasure ? "Treasure card" : Item ? "Item card" : Source == "Pet" ? "Pet card" : "Spell";

    /// <summary>The pip cost as a number to sort and filter by; X spells count as 0.</summary>
    internal int PipSort => XPips ? -1 : Pips;

}

/// <summary>
/// One piece of a card's description, ready to draw: words, a small picture, or a line break.
/// </summary>
internal sealed record DescriptionPart(string Text, uint Color = 0xFF000000, string? Icon = null, bool Wide = false, bool Break = false);

/// <summary>
/// Every spell in the game with what its card needs: names, costs, descriptions, art, the card frames and the game's
/// card fonts, read straight from the game's .wad files (Root.wad and the rest). The files are only ever read.
/// </summary>
internal sealed partial class SpellBook {

    private static readonly string[] s_imageKinds = [".dds", ".tga"];

    private readonly string _folder;
    private readonly Archive _root;
    private readonly ConcurrentDictionary<string, Dictionary<string, string>> _tables = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Icon, string Text, uint Color, bool ColorOverride, bool Wide)> _tokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(string Wad, string Entry)>> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Archive?> _archives = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, GameImage?> _imageCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, GlyphFont?> _fonts = new(StringComparer.OrdinalIgnoreCase);

    private SpellBook(string folder, Archive root) {
        _folder = folder;
        _root = root;
        _archives[Path.Combine(folder, "Root.wad")] = root;
    }

    internal IReadOnlyList<SpellCard> Spells { get; private set; } = [];

    /// <summary>The game's data folder these were read from.</summary>
    internal string Folder => _folder;

    /// <summary>
    /// Reads every spell from the game's Root.wad, and finds where each picture lives in the other .wad files.
    /// Takes a few seconds, so run it in the background.
    /// </summary>
    internal static SpellBook Load(string folder, IProgress<string>? progress = null) {
        var rootPath = Path.Combine(folder, "Root.wad");
        if (!System.IO.File.Exists(rootPath)) {
            throw new PanelException("The game's Root.wad could not be found, so the spell cards can't be shown. " +
                "Click \"Update Imlight\" on the Tools tab once so the app can find Wizard101.");
        }

        var root = ArchiveParser.Parse(OpenRead(rootPath)) ?? throw new PanelException("The game's Root.wad could not be read.");
        var book = new SpellBook(folder, root);
        progress?.Report("Reading the spell list…");
        book.ReadTokens();
        book.ReadSpells();
        progress?.Report("Finding the card pictures…");
        book.IndexImages();

        return book;
    }

    /// <summary>The wad files are opened so the game can still update or replace them while the app is open.</summary>
    private static FileStream OpenRead(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);

    private static T? Decode<T>(Archive archive, string name) where T : PropertyClass {
        var data = archive.OpenFile(name);
        if (data is null) {
            return null;
        }

        try {
            return new BindSerializer().Deserialize<PropertyClass>(data.Value.ToArray(), out var decoded) ? decoded as T : null;
        }
        catch (Exception) {
            return null;
        }
    }

    private void ReadSpells() {
        var manifest = Decode<TemplateManifest>(_root, "TemplateManifest.xml")
            ?? throw new PanelException("The game's list of spells (TemplateManifest.xml in Root.wad) could not be read.");
        var files = new List<(uint Id, string File, byte[] Data)>();
        foreach (var location in manifest.m_serializedTemplates ?? []) {
            string file = location.m_filename;
            if (file is not null && file.StartsWith("Spells/", StringComparison.OrdinalIgnoreCase) && _root.OpenFile(file) is { } data) {
                files.Add((location.m_id, file, data.ToArray()));
            }
        }

        var spells = new ConcurrentBag<SpellCard>();
        System.Threading.Tasks.Parallel.ForEach(files, entry => {
            if (Read(entry.Id, entry.File, entry.Data) is { } card) {
                spells.Add(card);
            }
        });

        Spells = [.. spells.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.School).ThenBy(s => s.Pips).ThenBy(s => s.Id)];
    }

    private SpellCard? Read(uint id, string file, byte[] data) {
        SpellTemplate? spell;
        try {
            spell = new BindSerializer().Deserialize<PropertyClass>(data, out var decoded) ? decoded as SpellTemplate : null;
        }
        catch (Exception) {
            return null;
        }

        if (spell is null) {
            return null;
        }

        var rank = spell.m_spellRank;
        var schoolPips = new List<(string, int)>();
        if (rank is not null) {
            foreach (var (school, count) in new[] {
                ("Balance", rank.m_balancePips), ("Death", rank.m_deathPips), ("Fire", rank.m_firePips), ("Ice", rank.m_icePips),
                ("Life", rank.m_lifePips), ("Myth", rank.m_mythPips), ("Storm", rank.m_stormPips),
            }) {
                if (count > 0) {
                    schoolPips.Add((school, count));
                }
            }
        }

        string displayName = spell.m_displayName;
        string code = spell.m_name;
        string description = spell.m_description;
        var card = new SpellCard {
            Id = id,
            Name = Text(displayName) is { Length: > 0 } shown ? shown.Trim() : code ?? "",
            Code = code ?? "",
            School = spell.m_sMagicSchoolName.ToString() ?? "",
            Type = spell.m_sTypeName.ToString() ?? "",
            Pips = rank?.m_spellRank ?? 0,
            XPips = rank?.m_xPipSpell ?? false,
            ShadowPips = rank?.m_shadowPips ?? 0,
            SchoolPips = schoolPips,
            Accuracy = spell.m_accuracy,
            Treasure = spell.m_Treasure,
            ImageName = spell.m_imageName.ToString() ?? "",
            ImageIndex = spell.m_imageIndex,
            Description = string.IsNullOrEmpty(description) ? "" : Text(description) is { Length: > 0 } text ? text : "",
            Effects = Flatten(spell.m_effects),
            Level = spell.m_levelRestriction,
            File = file,
            Source = spell.m_spellSourceType.ToString() is { Length: > 1 } source && source[0] == 'k' ? source[1..] : "Caster",
            CardFront = spell.m_cardFront.ToString() ?? "",
            NoPvEEnchant = spell.m_noPvEEnchant,
            NoPvPEnchant = spell.m_noPvPEnchant,
            NoDiscard = spell.m_noDiscard,
            PvP = spell.m_PvP,
            PvE = spell.m_PvE,
            Cloaked = spell.m_cloaked,
            MaxCopies = (int) spell.m_maxCopies,
            Category = spell.m_spellCategory.ToString() ?? "",
            Minion = Summons(spell.m_effects),
        };
        card.Hidden = IsHidden(card, Text(displayName).Length > 0);
        card.Short = Plain(card);
        card.Blank = IsBlank(card.Short);
        try {
            card.Support = SpellSupport.Classify(spell, card.Code, file);
            card.Explained = SpellSupport.Describe(spell, card.School);
        }
        catch (Exception) {
            // A spell the reader can't make sense of keeps "works" and its own text.
        }

        var status = card.Broken ? "broken" : card.Partly ? "partly" : "";
        card.Haystack = ($"{card.Name} {card.Code} {card.Id} {card.School} {card.Type} {card.Shown} " +
                         $"{(card.Treasure ? "treasure tc" : "")} {status}").ToLowerInvariant();

        return card;
    }

    [GeneratedRegex(@"(?i)\b(mob|mobs|boss|bosses|cheat|cheats|test|testing|fortesting|dev|npc|monster|monsters|unused|deprecated|do not use|placeholder|fight specific|raid|gauntlet|tutorial|minion only|archive)\b|mob only|_mob|-mob|\bTEMP\b")]
    private static partial Regex HiddenWords();

    /// <summary>
    /// Whether players never get the spell: it has no name of its own in the game's text, no card text, or its file or
    /// game name says it is a monster's, a boss's, a cheat's, a test's or a fight's special copy.
    /// </summary>
    private static bool IsHidden(SpellCard card, bool named)
        => !named || card.Description.Length == 0 || HiddenWords().IsMatch(card.Code) || HiddenWords().IsMatch(card.File)
           || card.School.Length == 0;

    [GeneratedRegex(@"(?i)^(\?+|-+|\.+|x+|tbd|todo|none|n/?a|placeholder|description|desc|\w+_\d+)$")]
    private static partial Regex PlaceholderText();

    [GeneratedRegex(@"\d+(-\d+)?%|\d+(-\d+)? (\w+ )?(damage|health)(?![-\w])")]
    private static partial Regex AmountWords();

    /// <summary>Whether words give a spell's numbers: a percent, damage or health.</summary>
    internal static bool HasAmounts(string text) => AmountWords().IsMatch(text);

    /// <summary>The card's text is blank, only a placeholder, or a text key the game files had no words for.</summary>
    private static bool IsBlank(string text) => string.IsNullOrWhiteSpace(text) || PlaceholderText().IsMatch(text.Trim());

    private static bool Summons(IEnumerable<SpellEffect>? effects) {
        bool Any(SpellEffect? effect, int depth) {
            if (effect is null || depth > 8) {
                return false;
            }

            if (effect.m_effectType.ToString().Contains("Summon", StringComparison.Ordinal)) {
                return true;
            }

            IEnumerable<SpellEffect?>? inside = effect switch {
                RandomSpellEffect random => random.m_effectList,
                VariableSpellEffect variable => variable.m_effectList,
                EffectListSpellEffect list => list.m_effectList,
                CountBasedSpellEffect count => count.m_effectList,
                ConditionalSpellEffect conditional => conditional.m_elements?.Select(e => e?.m_pEffect),
                TargetCountSpellEffect targets => targets.m_effectLists?.SelectMany(l => l?.m_effectList ?? []),
                _ => null,
            };

            return inside?.Any(e => Any(e, depth + 1)) == true;
        }

        return effects?.Any(e => Any(e, 0)) == true;
    }

    /// <summary>
    /// The effects in the order the description counts them ($eA$ is the first, $eA2$ the second): an effect that
    /// holds others, such as Fire Cat's random 80 to 120, counts as the ones inside it.
    /// </summary>
    private static List<SpellEffectInfo> Flatten(IEnumerable<SpellEffect>? effects) {
        var flat = new List<SpellEffectInfo>();
        void Add(SpellEffect? effect, int depth) {
            if (effect is null || depth > 8) {
                return;
            }

            IEnumerable<SpellEffect?>? inside = effect switch {
                RandomSpellEffect random => random.m_effectList,
                VariableSpellEffect variable => variable.m_effectList,
                EffectListSpellEffect list => list.m_effectList,
                CountBasedSpellEffect count => count.m_effectList,
                ConditionalSpellEffect conditional => conditional.m_elements?.Select(e => e?.m_pEffect),
                TargetCountSpellEffect targets => targets.m_effectLists?.SelectMany(l => l?.m_effectList ?? []),
                _ => null,
            };
            var children = inside?.ToList();
            if (children is { Count: > 0 }) {
                foreach (var child in children) {
                    Add(child, depth + 1);
                }

                return;
            }

            flat.Add(new SpellEffectInfo(effect.m_effectParam, effect.m_sDamageType.ToString() ?? "", effect.m_numRounds,
                Trim(effect.m_effectType.ToString()), Trim(effect.m_effectTarget.ToString())));
        }

        foreach (var effect in effects ?? []) {
            Add(effect, 0);
        }

        return flat;
    }

    /// <summary>"kDamage" as "Damage": the game's enum names without their k.</summary>
    private static string Trim(string? name)
        => string.IsNullOrEmpty(name) ? "" : name.Length > 1 && name[0] == 'k' && char.IsUpper(name[1]) ? name[1..] : name;

    /// <summary>
    /// Spells holding every word of the query, best matches first: the exact name, then names that start with it.
    /// </summary>
    internal List<SpellCard> Search(string query, int max, out int total) => Search(query, max, hideBroken: false, out total, out _);

    /// <summary>
    /// As <see cref="Search(string, int, out int)"/>; with <paramref name="hideBroken"/>, cards the server can't do are
    /// left out (and counted in <paramref name="brokenHidden"/>) unless asked for by their ID.
    /// </summary>
    internal List<SpellCard> Search(string query, int max, bool hideBroken, out int total, out int brokenHidden) {
        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var exact = query.Trim();
        var matches = Spells.Where(s => words.All(s.Haystack.Contains)).ToList();
        brokenHidden = 0;
        if (hideBroken) {
            var before = matches.Count;
            matches = [.. matches.Where(s => !s.Broken || s.Id.ToString(CultureInfo.InvariantCulture) == exact)];
            brokenHidden = before - matches.Count;
        }

        total = matches.Count;

        return [.. matches
            .OrderBy(s => !(s.Name.Equals(exact, StringComparison.OrdinalIgnoreCase) || s.Id.ToString(CultureInfo.InvariantCulture) == exact))
            .ThenBy(s => !s.Name.StartsWith(exact, StringComparison.OrdinalIgnoreCase))
            .ThenBy(s => words.Length == 0 ? 0 : s.Name.Length)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            // The spell players know (its file is named like the card) before creature, pet and item copies.
            .ThenBy(s => s.Hidden)
            .ThenBy(s => s.Code.Equals(s.Name, StringComparison.OrdinalIgnoreCase) ? 0 : s.Code.StartsWith(s.Name + " TC", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(s => s.Description.Length == 0)
            .Take(max)];
    }

    // ---------------------------------------------------------------------
    // Game text
    // ---------------------------------------------------------------------

    /// <summary>
    /// The English text for a key such as "Spells_00000129" (the table, then the line), or "" when there is none.
    /// </summary>
    internal string Text(string? key) {
        if (string.IsNullOrEmpty(key)) {
            return "";
        }

        var split = key.LastIndexOf('_');
        if (split <= 0) {
            return "";
        }

        var table = _tables.GetOrAdd(key[..split], ReadTable);

        return table.TryGetValue(key[(split + 1)..], out var text) ? text : "";
    }

    private Dictionary<string, string> ReadTable(string table) {
        var lines = new Dictionary<string, string>(StringComparer.Ordinal);
        var data = _root.OpenFile($"Locale/en-US/{table}.lang");
        if (data is null) {
            return lines;
        }

        var all = Encoding.Unicode.GetString(data.Value.Span).TrimStart('﻿').Split(["\r\n", "\n"], StringSplitOptions.None);

        // First line "1:Spells", then each line of text as three lines: key, note, text.
        for (var i = 1; i + 2 < all.Length; i += 3) {
            if (all[i].Length > 0) {
                lines[all[i]] = all[i + 2];
            }
        }

        return lines;
    }

    private void ReadTokens() {
        if (Decode<MadlibIconDefinition>(_root, "MadlibIcons.xml") is not { } icons) {
            return;
        }

        foreach (var token in icons.m_madlibIconDataList ?? []) {
            if (token is null || token.m_useCase != MadlibIconUseCase.MIUC_Default) {
                continue;
            }

            string name = token.m_token;
            if (string.IsNullOrEmpty(name) || _tokens.ContainsKey(name)) {
                continue;
            }

            var color = uint.TryParse(token.m_color.ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var c) ? c : 0xFF000000;
            var text = Text(token.m_text.ToString());
            if (text.Length == 0) {
                // A tooltip reads "Shield: Helpful Ward that reduces incoming damage"; the word before the colon is its name.
                text = Text(token.m_tooltip.ToString());
                if (text.IndexOf(':') is var colon and > 0) {
                    text = text[..colon];
                }

                text = text.Trim();
            }

            _tokens[name] = (token.m_icon.ToString() ?? "", text, color, token.m_colorOverride, token.m_wide != 0);
        }
    }

    // ---------------------------------------------------------------------
    // Descriptions
    // ---------------------------------------------------------------------

    [GeneratedRegex(@"#-?\d+:|[{}]|\[\d+\]")]
    private static partial Regex Markers();

    [GeneratedRegex(@"^eA(Rounds|PerPip|Bonus)?(\d*)$")]
    private static partial Regex AmountToken();

    [GeneratedRegex(@"^dT_image(\d*)$")]
    private static partial Regex SchoolToken();

    /// <summary>
    /// The card's description as words and pictures, with the spell's own numbers filled in, as the game draws it.
    /// </summary>
    internal List<DescriptionPart> Describe(SpellCard card) {
        var parts = new List<DescriptionPart>();
        var text = Markers().Replace(card.Description, "")
            .Replace("\\r", "").Replace("\\n", "\n").Replace("\r", "")
            .Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase);
        var colors = new Stack<uint>();
        colors.Push(0xFF000000);
        var words = new StringBuilder();
        void Flush() {
            if (words.Length > 0) {
                parts.Add(new DescriptionPart(words.ToString(), colors.Peek()));
                words.Clear();
            }
        }

        for (var i = 0; i < text.Length; i++) {
            var c = text[i];
            if (c == '\n') {
                Flush();
                parts.Add(new DescriptionPart("", Break: true));
            }
            else if (c == '<' && text.IndexOf('>', i) is var close && close > i) {
                // <color;FF37005B>words</color>; any other tag is left out.
                var tag = text[(i + 1)..close];
                Flush();
                if (tag.StartsWith("color;", StringComparison.OrdinalIgnoreCase)
                        && uint.TryParse(tag[6..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var color)) {
                    colors.Push(color | 0xFF000000);
                }
                else if (tag.Equals("/color", StringComparison.OrdinalIgnoreCase) && colors.Count > 1) {
                    colors.Pop();
                }

                i = close;
            }
            else if (c == '$' && text.IndexOf('$', i + 1) is var end && end > i) {
                Flush();
                parts.AddRange(Token(card, text[(i + 1)..end], colors.Peek()));
                i = end;
            }
            else {
                words.Append(c);
            }
        }

        Flush();

        return parts;
    }

    private IEnumerable<DescriptionPart> Token(SpellCard card, string token, uint color) {
        if (token.Length == 0) {
            yield break;
        }

        if (AmountToken().Match(token) is { Success: true } amount) {
            var effect = EffectAt(card, amount.Groups[2].Value);
            yield return new DescriptionPart(effect is null ? "?" : amount.Groups[1].Value == "Rounds"
                ? effect.Rounds.ToString(CultureInfo.InvariantCulture)
                : effect.Amount.ToString(CultureInfo.InvariantCulture), color);

            yield break;
        }

        if (SchoolToken().Match(token) is { Success: true } school) {
            var name = EffectAt(card, school.Groups[1].Value)?.School ?? card.School;
            if (_tokens.TryGetValue(name + "SchoolIcon", out var icon) && icon.Icon.Length > 0) {
                yield return new DescriptionPart(name, color, icon.Icon);
            }
            else if (name.Length > 0 && !name.Equals("All", StringComparison.OrdinalIgnoreCase)) {
                yield return new DescriptionPart(name, color);
            }

            yield break;
        }

        if (_tokens.TryGetValue(token, out var known)) {
            if (known.Icon.Length > 0) {
                yield return new DescriptionPart(known.Text.Length > 0 ? known.Text : Words(token), color, known.Icon, known.Wide);
            }
            else if (known.Text.Length > 0) {
                yield return new DescriptionPart(known.Text, known.ColorOverride ? known.Color | 0xFF000000 : color);
            }

            yield break;
        }

        yield return new DescriptionPart(Words(token), color);
    }

    private static SpellEffectInfo? EffectAt(SpellCard card, string number) {
        var index = number.Length == 0 ? 0 : int.Parse(number, CultureInfo.InvariantCulture) - 1;

        return index >= 0 && index < card.Effects.Count ? card.Effects[index] : null;
    }

    private static string Words(string token) => token.Replace("_image", "").Replace('_', ' ').Trim();

    /// <summary>
    /// The description as one line of plain words for the list, such as "80-120 Fire Damage".
    /// </summary>
    internal string Plain(SpellCard card) {
        if (card.Description.Length == 0) {
            return "";
        }

        var text = new StringBuilder();
        foreach (var part in Describe(card)) {
            if (part.Break) {
                text.Append(' ');
            }
            else if (part.Icon is not null) {
                text.Append(' ').Append(part.Text).Append(' ');
            }
            else {
                text.Append(part.Text);
            }
        }

        return string.Join(' ', text.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    // ---------------------------------------------------------------------
    // Pictures and fonts
    // ---------------------------------------------------------------------

    /// <summary>Notes where every picture in the game's .wad files is, by its file name, so a card can find its art.</summary>
    private void IndexImages() {
        var wads = Directory.EnumerateFiles(_folder, "*.wad")
            // Root.wad first, so its menus win when another file has a picture of the same name.
            .OrderBy(f => Path.GetFileName(f).Equals("Root.wad", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase);
        foreach (var wad in wads) {
            IEnumerable<string> names;
            if (_archives.TryGetValue(wad, out var open) && open is not null) {
                names = open.Files.Keys;
            }
            else {
                try {
                    using var stream = OpenRead(wad);
                    names = ArchiveParser.Parse(stream)?.Files.Keys.ToList() ?? [];
                }
                catch (Exception) {
                    continue;
                }
            }

            foreach (var name in names) {
                if (!s_imageKinds.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase)) {
                    continue;
                }

                var key = Path.GetFileName(name);
                if (!_images.TryGetValue(key, out var list)) {
                    _images[key] = list = [];
                }

                list.Add((wad, name));
            }
        }
    }

    /// <summary>
    /// A picture by its file name, such as "Spell_Fire_FireCat.dds", from whichever .wad file has it, or null.
    /// The round pictures the game shows over summoned creatures share names with card art, so those come last.
    /// </summary>
    internal GameImage? Image(string? fileName) {
        if (string.IsNullOrWhiteSpace(fileName)) {
            return null;
        }

        return _imageCache.GetOrAdd(Path.GetFileName(fileName.Replace('\\', '/')), name => {
            if (!_images.TryGetValue(name, out var places) && !_images.TryGetValue(name + ".dds", out places)) {
                return null;
            }

            foreach (var (wad, entry) in places.OrderBy(p => p.Entry.Contains("SummonedImages", StringComparison.OrdinalIgnoreCase) ? 1 : 0)) {
                var archive = _archives.GetOrAdd(wad, path => {
                    try {
                        return ArchiveParser.Parse(OpenRead(path));
                    }
                    catch (Exception) {
                        return null;
                    }
                });
                if (archive?.OpenFile(entry) is { } data && GameImage.Decode(data.Span) is { } image) {
                    return image;
                }
            }

            return null;
        });
    }

    /// <summary>
    /// An item's icon by its template file (the Path column of the server's ids.csv), such as a hat's or a reagent's
    /// picture; null when the game has none for it.
    /// </summary>
    internal GameImage? ItemIcon(string templateFile) {
        if (string.IsNullOrWhiteSpace(templateFile)) {
            return null;
        }

        string? icon;
        try {
            icon = Decode<PropertyClass>(_root, templateFile.Replace('\\', '/')) switch {
                ReagentItemTemplate reagent => (string) reagent.m_imageName,
                ItemTemplate item => (string) item.m_sIcon,
                _ => null,
            };
        }
        catch (Exception) {
            return null;
        }

        return Image(icon);
    }

    /// <summary>The templates in a folder of Root.wad (such as ObjectData/), from the game's own list, by ID.</summary>
    internal List<(uint Id, string File)> TemplatesUnder(string folder) {
        var manifest = Decode<TemplateManifest>(_root, "TemplateManifest.xml");

        return [.. (manifest?.m_serializedTemplates ?? [])
            .Where(t => t is not null && ((string) t.m_filename)?.StartsWith(folder, StringComparison.OrdinalIgnoreCase) == true)
            .Select(t => (t.m_id, (string) t.m_filename))];
    }

    /// <summary>Every file in a folder of Root.wad, such as GameEffectRuleData/.</summary>
    internal List<(uint Id, string File)> FilesUnder(string folder)
        => [.. _root.Files.Keys.Where(k => k.StartsWith(folder, StringComparison.OrdinalIgnoreCase)).Select(k => (0u, k))];

    /// <summary>A data file of Root.wad, decoded, or null.</summary>
    internal PropertyClass? DecodeFile(string file) => Decode<PropertyClass>(_root, file);

    /// <summary>The picture file of one of the game's small text icons (MadlibIcons.xml), such as Accuracy_image.</summary>
    internal string? TokenIcon(string token) => _tokens.TryGetValue(token, out var known) && known.Icon.Length > 0 ? known.Icon : null;

    /// <summary>Every picture file name in the game files.</summary>
    internal IEnumerable<string> ImageNames => _images.Keys;

    /// <summary>The picture file names that hold every one of these words (any case), shortest first.</summary>
    internal List<string> FindImages(params string[] words)
        => [.. _images.Keys.Where(k => words.All(w => k.Contains(w, StringComparison.OrdinalIgnoreCase))).OrderBy(k => k.Length)];

    /// <summary>Whether a picture of this name is anywhere in the game files.</summary>
    internal bool HasImage(string? fileName)
        => !string.IsNullOrWhiteSpace(fileName) && (_images.ContainsKey(Path.GetFileName(fileName)) || _images.ContainsKey(Path.GetFileName(fileName) + ".dds"));

    /// <summary>One of the game's card fonts from Root.wad's Fonts folder, such as "WizardFancy.xml".</summary>
    internal GlyphFont? Font(string name) => _fonts.GetOrAdd(name, file => {
        if (_root.OpenFile("Fonts/" + file) is not { } xml) {
            return null;
        }

        return GlyphFont.Read(xml.ToArray(), page => _root.OpenFile("Fonts/" + page) is { } data ? GameImage.Decode(data.Span) : null);
    });

    /// <summary>The picture file for a school's icon, such as Icon_Fire.dds, from the game's own list.</summary>
    internal string? SchoolIcon(string school)
        => _tokens.TryGetValue(school + "SchoolIcon", out var token) && token.Icon.Length > 0 ? token.Icon : null;

    /// <summary>The picture file for a school's pip, such as pip_fire_no_glow.dds.</summary>
    internal string? PipIcon(string school)
        => _tokens.TryGetValue(school + "Pips_image", out var token) && token.Icon.Length > 0 ? token.Icon : null;

}
