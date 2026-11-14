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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Effects;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>How a server-played wizard plays, which decides the gear and cards it wants.</summary>
internal enum BotStyle {
    // Ice of the Quest Team: shields and takes the hits.
    Tank,
    // Life: heals.
    Healer,
    // Death: hits second and supports.
    Backup,
    // Storm, Fire, Myth: only hits.
    Hitter,
    // Ultimus alone: hits, keeps itself alive, and its hits land as other schools' damage (Balance's Hydra, Spectral
    // Blast, Nested Fury as Fire, Ice or Storm; Chimera as Life, Death or Myth), so gear for those counts too.
    Solo,
}

/// <summary>
/// Gear for the server's own wizards, the Quest Team and Bot Players (Niduus, 2026-11-12: "Implement gear, they focus on
/// their class over universal and will always look for the recommended gear that's obtainable for them", corrected "to be
/// best for their school and level"). Every hat, robe, boots, wand, athame, amulet, ring and deck the game lets a wizard
/// get (a shop sells it, a quest gives it or a boss drops it; never Crowns-only or retired), scored for the wizard's school
/// and job: stats for its own school count fully, stats for every school a little less, other schools' not at all. A
/// wizard only wears what its level and school allow. Works on a wizard that is never saved and has no game.
/// </summary>
internal static class BotGear {

    internal sealed record Stat(string Kind, string School, float Value);

    internal sealed record Piece(uint Id, string Name, string Slot, int Level, string SchoolNeeded, string School, int Gold,
                                 bool CrownsOnly, bool Retired, IReadOnlyList<Stat> Stats, int DeckSpells, int DeckSchoolCopies,
                                 string DeckSchool);

    // The slots it dresses (pets and mounts are their own matter).
    internal static readonly EquipmentSlotType[] Slots = [
        EquipmentSlotType.Hat, EquipmentSlotType.Robe, EquipmentSlotType.Shoes, EquipmentSlotType.Weapon, EquipmentSlotType.Athame,
        EquipmentSlotType.Amulet, EquipmentSlotType.Ring, EquipmentSlotType.Deck,
    ];

    private static readonly ConcurrentDictionary<uint, Piece> s_pieces = new();
    private static volatile List<Piece> s_catalog;
    private static int s_building;

    /// <summary>The style that fits a school, for a wizard with no job of its own (a Bot Player).</summary>
    internal static BotStyle StyleOf(string school) => school switch {
        "Ice" => BotStyle.Tank,
        "Life" => BotStyle.Healer,
        "Death" or "Balance" => BotStyle.Backup,
        _ => BotStyle.Hitter,
    };

    /// <summary>A Quest Team job's style (Ice tank, Life healer, Death backup, Storm hitter).</summary>
    internal static BotStyle StyleOfRole(string role) => StyleOf(role);

    internal static string SchoolOf(Wizard wizard) => wizard?.MagicSchoolBehavior?.MagicSchool.ToString() ?? "";

    internal static int LevelOf(Wizard wizard) => wizard?.MagicSchoolBehavior?.Level ?? 1;

    // ---------------------------------------------------------------------
    // The catalog
    // ---------------------------------------------------------------------

    /// <summary>Every piece a wizard can get, or empty while it is still being read (it starts reading on the first ask).</summary>
    internal static IReadOnlyList<Piece> Catalog {
        get {
            if (s_catalog is { } ready) {
                return ready;
            }

            if (Interlocked.Exchange(ref s_building, 1) == 0) {
                _ = Task.Run(() => {
                    try {
                        var started = DateTime.UtcNow;
                        var list = ItemSources.ObtainableIds().Select(Read)
                            .Where(p => p is not null && !p.CrownsOnly && !p.Retired).ToList();
                        s_catalog = list;
                        Logger.Information("Bot gear: {0} pieces the server's wizards can get, read in {1:0.0} s.",
                            Logger.Args(list.Count, (DateTime.UtcNow - started).TotalSeconds));
                    }
                    catch (Exception e) {
                        Logger.Warning("Bot gear: the gear could not be read: {0}", Logger.Args(e.Message));
                        Interlocked.Exchange(ref s_building, 0);
                    }
                });
            }

            return [];
        }
    }

    /// <summary>Whether the catalog is being read right now (the first ask starts it; it takes a little while).</summary>
    internal static bool Reading => s_catalog is null && Volatile.Read(ref s_building) == 1;

    /// <summary>A piece of gear by template, or null when it is not one.</summary>
    internal static Piece Read(uint templateId) {
        if (templateId == 0) {
            return null;
        }

        if (s_pieces.TryGetValue(templateId, out var known)) {
            return known;
        }

        Piece piece = null;
        try {
            if (CoreObjectFactory.GetCoreTemplate(templateId) is WizItemTemplate template && template is not ItemBundleTemplate) {
                piece = Parse(templateId, template);
            }
        }
        catch (Exception e) {
            Logger.Debug("Bot gear: item {0} could not be read: {1}", Logger.Args(templateId, e.Message));
        }

        // A piece whose stats could not all be read yet (the stat tables still loading when it was first asked about) is
        // read again next time, not kept as a piece worth nothing (Niduus, 2026-11-13: "Why does Ultimus not use the Adept
        // robe": it read as nothing, so a dropped Cloak of the Watchful took its place).
        if (piece is null || !s_partial.Contains(templateId)) {
            s_pieces[templateId] = piece;
        }

        return piece;
    }

    private static readonly ConcurrentDictionary<uint, byte> s_partialSet = new();
    private static readonly ICollection<uint> s_partial = s_partialSet.Keys;

    /// <summary>Whether a piece has stats that could not be read (so its score is not to be trusted).</summary>
    internal static bool PartlyRead(uint templateId) => s_partialSet.ContainsKey(templateId);

    private static Piece Parse(uint id, WizItemTemplate template) {
        EquipmentSlot slot;
        try {
            slot = ItemHelper.GetItemSlot(template);
        }
        catch (InvalidOperationException) {
            return null;
        }

        if (slot is null || !Slots.Contains(slot.SlotType)) {
            return null;
        }

        var adjectives = (template.m_adjectiveList ?? []).Select(a => (string) a ?? "").ToList();
        if (Forbidden(id)) {
            return null;
        }

        var (level, schoolNeeded) = Requirements(template.m_equipRequirements);
        var stats = new List<Stat>();
        var unread = 0;
        foreach (var effect in template.m_equipEffects ?? []) {
            switch (effect) {
                case StatisticEffectInfo info when ReadStat(info) is { } stat:
                    stats.Add(stat);
                    break;
                case StatisticEffectInfo:
                    unread++;
                    break;
                case StartingPipEffectInfo pips:
                    stats.Add(new Stat("StartingPips", "", pips.m_pipsGiven + (pips.m_powerPipsGiven * 2)));
                    break;
            }
        }

        if (unread > 0) {
            s_partialSet[id] = 0;
        }
        else {
            s_partialSet.TryRemove(id, out _);
        }

        var deck = (template.m_behaviors ?? []).OfType<DeckBehaviorTemplate>().FirstOrDefault();
        string key = template.m_displayName;
        var name = string.IsNullOrEmpty(key) ? "" : Locale.GetEnglishName(key);

        return new Piece(id, string.IsNullOrWhiteSpace(name) ? (string) template.m_objectName ?? id.ToString() : name.Trim(),
            slot.SlotType.ToString(), level, schoolNeeded, (string) template.m_school ?? "", (int) Math.Max(0, template.m_baseCost),
            adjectives.Any(a => a.Equals("FLAG_CrownsOnly", StringComparison.OrdinalIgnoreCase)),
            adjectives.Any(a => a.Equals("FLAG_Retired", StringComparison.OrdinalIgnoreCase)),
            stats, deck?.m_maxSpells ?? 0, deck?.m_schoolMaxInstances ?? 0, (string) deck?.m_primarySchoolName ?? "");
    }

    // ---------------------------------------------------------------------
    // Gear the server's wizards never wear (Niduus, 2026-11-12: "noticed the bots putting on dev gear like the QA robe.
    // Prevent that. Not just QA gear but any Dev gear and spells" and "Remove Deckathalon decks from all bots. They are
    // not allowed to equip them")
    // ---------------------------------------------------------------------

    private static readonly ConcurrentDictionary<uint, bool> s_forbidden = new();
    private static readonly Lazy<Dictionary<uint, string>> s_files = new(() => {
        var files = new Dictionary<uint, string>();
        foreach (var location in CoreObjectFactory.TemplateManifest?.m_serializedTemplates ?? []) {
            files.TryAdd(location.m_id, ((string) location.m_filename ?? "").Replace('\\', '/'));
        }

        return files;
    });

    // Words that mark the developers' and testers' own items (as the Dev trainer's spells are found).
    private static readonly System.Text.RegularExpressions.Regex s_devWord
        = new(@"(^|[^A-Za-z])((Dev|Debug|GM|Admin|QA|Test|Testing|Tester)([^a-z]|$)|(DEV|DEBUG|ADMIN|TEST|TESTING)([^A-Za-z]|$))");

    /// <summary>
    /// Whether the server's wizards may never wear this item: the developers' and testers' own gear (QA, Dev, Debug, GM,
    /// Admin or Test in its name or file, or in a Test or Dev folder) and every Deckathalon deck.
    /// </summary>
    internal static bool Forbidden(uint templateId) => templateId != 0 && s_forbidden.GetOrAdd(templateId, id => {
        try {
            if (CoreObjectFactory.GetCoreTemplate(id) is not WizItemTemplate template) {
                return false;
            }

            string objectName = (string) template.m_objectName ?? "";
            string key = template.m_displayName;
            var shown = string.IsNullOrEmpty(key) ? "" : Locale.GetEnglishName(key) ?? "";
            var file = s_files.Value.GetValueOrDefault(id) ?? "";
            var folders = file.Contains("/Test/", StringComparison.OrdinalIgnoreCase) || file.StartsWith("Test/", StringComparison.OrdinalIgnoreCase)
                || file.Contains("/Dev/", StringComparison.OrdinalIgnoreCase) || file.StartsWith("Dev/", StringComparison.OrdinalIgnoreCase)
                || file.Contains("/QA/", StringComparison.OrdinalIgnoreCase);
            var dev = folders || s_devWord.IsMatch(objectName) || s_devWord.IsMatch(shown)
                || s_devWord.IsMatch(System.IO.Path.GetFileNameWithoutExtension(file));
            var deckathalon = shown.Contains("Deckathlete", StringComparison.OrdinalIgnoreCase)
                || shown.Contains("Deckathalon", StringComparison.OrdinalIgnoreCase)
                || objectName.Contains("Deckathalon", StringComparison.OrdinalIgnoreCase)
                || objectName.StartsWith("Deck-DU", StringComparison.OrdinalIgnoreCase) || objectName.Contains("-DU-T", StringComparison.OrdinalIgnoreCase)
                || file.Contains("Deckathalon", StringComparison.OrdinalIgnoreCase) || file.Contains("Deck-DU", StringComparison.OrdinalIgnoreCase);

            return dev || deckathalon;
        }
        catch (Exception) {
            return false;
        }
    });

    /// <summary>
    /// Forbidden gear a wizard wears that can come off now (a forbidden deck stays until a fair one replaces it, so the
    /// wizard still has cards): item, slot, name.
    /// </summary>
    internal static List<(WizClientObjectItem Item, string Slot, string Name)> ForbiddenWorn(Wizard wizard) {
        var found = new List<(WizClientObjectItem, string, string)>();
        foreach (var slot in Slots) {
            if (slot == EquipmentSlotType.Deck || wizard?.EquipmentBehavior?.GetItemInSlot(slot) is not { } item) {
                continue;
            }

            var id = (uint) Math.Min((ulong) item.m_templateID, uint.MaxValue);
            if (Forbidden(id)) {
                found.Add((item, slot.ToString(), NameOf(id)));
            }
        }

        return found;
    }

    private static string NameOf(uint id) {
        try {
            if (CoreObjectFactory.GetCoreTemplate(id) is WizItemTemplate template) {
                string key = template.m_displayName;
                var shown = string.IsNullOrEmpty(key) ? "" : Locale.GetEnglishName(key);

                return string.IsNullOrWhiteSpace(shown) ? (string) template.m_objectName ?? id.ToString() : shown.Trim();
            }
        }
        catch (Exception) {
            // The id is enough.
        }

        return id.ToString();
    }

    private static Stat ReadStat(StatisticEffectInfo info) {
        string effectName = info.m_effectName;
        if (string.IsNullOrEmpty(effectName) || CanonicalStatEffects.GetEffectTemplate(effectName) is not WizStatisticEffectTemplate effect) {
            return null;
        }

        var category = (string) effect.m_effectCategory ?? "";
        var school = (string) effect.m_school ?? "";
        var kind = category;
        foreach (var name in s_schools) {
            if (category.Length > name.Length && category.StartsWith(name, StringComparison.Ordinal)) {
                school = school.Length > 0 ? school : name;
                kind = category[name.Length..];
                break;
            }
        }

        if (effectName.Contains("Flat", StringComparison.Ordinal) && !kind.Contains("Flat", StringComparison.Ordinal)) {
            kind = "Flat" + kind;
        }

        if (kind.EndsWith("All", StringComparison.Ordinal) && kind.Length > 3) {
            kind = kind[..^3];
            school = "";
        }

        if (school.Equals("All", StringComparison.OrdinalIgnoreCase) || school.Equals("AllSchools", StringComparison.OrdinalIgnoreCase)) {
            school = "";
        }

        return new Stat(kind, school, CanonicalStatEffects.GetCanonicalStatValue(info));
    }

    private static readonly string[] s_schools = ["Fire", "Ice", "Storm", "Myth", "Life", "Death", "Balance", "Shadow", "Sun", "Moon", "Star", "All"];

    private static (int Level, string School) Requirements(Requirement requirement) {
        var level = 0;
        var school = "";
        void Walk(Requirement r, int depth) {
            if (r is null || depth > 6 || r.m_applyNOT) {
                return;
            }

            switch (r) {
                case RequirementList list:
                    foreach (var inner in list.m_requirements ?? []) {
                        Walk(inner, depth + 1);
                    }

                    break;
                case ReqMagicLevel magicLevel:
                    level = Math.Max(level, (int) magicLevel.m_numericValue);
                    break;
                case ReqSchoolOfFocus focus when (string) focus.m_magicSchool is { Length: > 0 } name:
                    school = name;
                    break;
            }
        }

        Walk(requirement, 0);

        return (level, school);
    }

    // ---------------------------------------------------------------------
    // Scoring
    // ---------------------------------------------------------------------

    /// <summary>Whether a wizard of this school and level may wear it.</summary>
    internal static bool CanWear(Piece piece, string school, int level)
        => piece is not null && piece.Level <= level
           && (piece.SchoolNeeded.Length == 0 || piece.SchoolNeeded.Equals(school, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// For the Solo style (Ultimus): the share of its attack damage each damage type takes, the biggest 1 (Niduus,
    /// 2026-11-13: "Given that it's a balance ... Hydra, Spectral Blast and Nested Fury do elemental and some that do
    /// spiritual like Chimera"). The server boosts a hit by the caster's damage for the hit's damage type, so a Balance
    /// wizard whose Hydra lands as Fire, Ice and Storm gains from those schools' damage and pierce. Set from its deck.
    /// </summary>
    internal static volatile IReadOnlyDictionary<string, double> SoloDamageMix = new Dictionary<string, double>();

    /// <summary>
    /// Alone: the share of the recent creatures' hits each school carries (the biggest 1), read from their decks
    /// (UltimusScout.Expect). A resist of those schools counts by that share (never under the usual 0.3), so its gear
    /// leans toward what the area's creatures hit with.
    /// </summary>
    internal static volatile IReadOnlyDictionary<string, double> SoloResistMix = new Dictionary<string, double>();

    private static readonly HashSet<string> s_hitKinds = new(StringComparer.Ordinal) { "Damage", "FlatDamage", "ArmorPiercing" };

    /// <summary>How good a piece is for a wizard of this school and style; 0 for nothing it uses.</summary>
    internal static double Score(Piece piece, string school, BotStyle style) {
        if (piece is null) {
            return 0;
        }

        var score = 0.0;
        foreach (var stat in piece.Stats) {
            var share = Share(stat, school, style);
            if (share > 0) {
                score += share * stat.Value * Weight(stat.Kind, style);
            }
        }

        if (piece.Slot == nameof(EquipmentSlotType.Deck)) {
            var ownDeck = piece.DeckSchool.Equals(school, StringComparison.OrdinalIgnoreCase);
            score += (piece.DeckSpells * 0.6) + (ownDeck ? piece.DeckSchoolCopies * 3 : 0);
        }

        // Its own school's piece wins a tie.
        if (piece.School.Equals(school, StringComparison.OrdinalIgnoreCase) || piece.SchoolNeeded.Equals(school, StringComparison.OrdinalIgnoreCase)) {
            score *= 1.05;
        }

        return score;
    }

    private static double Share(Stat stat, string school, BotStyle style) {
        var own = stat.School.Length > 0 && stat.School.Equals(school, StringComparison.OrdinalIgnoreCase);
        var all = stat.School.Length == 0;
        // Its own school's stats count fully; every school's a little less (class over universal); other schools' a
        // resist still helps a bit, the rest not at all.
        var share = own ? 1.0 : all ? 0.8 : stat.Kind.Contains("ReduceDamage", StringComparison.Ordinal) ? (style == BotStyle.Solo ? 0.3 : 0.15) : 0;
        // Alone, its hits land as the damage types of its spells: those schools' damage and pierce count by how much
        // of its damage they carry (its own school's by that too, but never under half).
        if (style == BotStyle.Solo && !own && stat.School.Length > 0 && stat.Kind.Contains("ReduceDamage", StringComparison.Ordinal)
            && SoloResistMix.TryGetValue(stat.School, out var hitBy)) {
            share = Math.Max(share, Math.Min(0.8, hitBy));
        }

        if (style == BotStyle.Solo && stat.School.Length > 0 && s_hitKinds.Contains(stat.Kind)) {
            var mix = SoloDamageMix.TryGetValue(stat.School, out var part) ? part : 0;
            share = own ? Math.Max(0.5, mix) : Math.Max(share, mix);
        }

        return share;
    }

    private static double Weight(string kind, BotStyle style) => kind switch {
        // Solo: as much hit as a backup hitter, and more health, resist, block and healing taken, as nobody else heals it.
        "Damage" => style switch { BotStyle.Hitter => 4, BotStyle.Backup or BotStyle.Solo => 3.5, BotStyle.Tank => 1.5, _ => 1 },
        "FlatDamage" => style is BotStyle.Hitter or BotStyle.Backup or BotStyle.Solo ? 1.2 : 0.4,
        "ReduceDamage" => style switch { BotStyle.Tank => 4, BotStyle.Healer or BotStyle.Solo => 3, BotStyle.Backup => 2.5, _ => 2 },
        "FlatReduceDamage" => style is BotStyle.Tank or BotStyle.Solo ? 1.5 : 1,
        "MaxHealth" => style switch { BotStyle.Tank => 0.1, BotStyle.Solo => 0.09, BotStyle.Healer => 0.07, _ => 0.06 },
        "Accuracy" => style is BotStyle.Hitter or BotStyle.Solo ? 1.5 : 1,
        "ArmorPiercing" => style switch { BotStyle.Hitter => 2.5, BotStyle.Backup or BotStyle.Solo => 2, _ => 1 },
        "CriticalHit" => style is BotStyle.Hitter or BotStyle.Backup or BotStyle.Solo ? 0.25 : 0.12,
        "Block" => style is BotStyle.Tank or BotStyle.Solo ? 0.2 : 0.1,
        "Healing" => style == BotStyle.Healer ? 3 : style == BotStyle.Solo ? 1 : 0.5,
        "IncHealing" => style is BotStyle.Tank or BotStyle.Solo ? 1.5 : 1,
        "PowerPips" => 2.5,
        "MaxMana" => 0.02,
        "StartingPips" => 15,
        _ => 0,
    };

    // ---------------------------------------------------------------------
    // What a wizard wears and what would be better
    // ---------------------------------------------------------------------

    /// <summary>What a wizard wears in a slot, as a piece, and its score (0 for nothing).</summary>
    internal static (Piece Piece, double Score) Wearing(Wizard wizard, EquipmentSlotType slot, BotStyle style) {
        var item = wizard?.EquipmentBehavior?.GetItemInSlot(slot);
        var piece = item is null ? null : Read((uint) Math.Min((ulong) item.m_templateID, uint.MaxValue));

        return (piece, Score(piece, SchoolOf(wizard), style));
    }

    /// <summary>The best pieces for a school, level and style, one per slot (two rings are one ring slot in the game).</summary>
    internal static Dictionary<EquipmentSlotType, Piece> BestFor(string school, int level, BotStyle style, Func<Piece, bool> allowed = null,
                                                                Random pickNear = null) {
        var best = new Dictionary<EquipmentSlotType, Piece>();
        var catalog = Catalog;
        foreach (var slot in Slots) {
            var name = slot.ToString();
            var ranked = catalog.Where(p => p.Slot == name && CanWear(p, school, level) && (allowed?.Invoke(p) ?? true))
                .Select(p => (Piece: p, Score: Score(p, school, style))).Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score).ThenByDescending(x => x.Piece.Level).Take(3).ToList();
            if (ranked.Count == 0) {
                continue;
            }

            // A Bot Player wears one of the best few, so not every bot of a school looks the same.
            best[slot] = pickNear is null || ranked.Count == 1 || ranked[^1].Score < ranked[0].Score * 0.85
                ? ranked[0].Piece
                : ranked[pickNear.Next(ranked.Count)].Piece;
        }

        return best;
    }

    /// <summary>Items in the backpack better than what the wizard wears: (item, slot name, piece, how much better).</summary>
    internal static List<(WizClientObjectItem Item, string Slot, Piece Piece, double Gain)> BetterInBackpack(Wizard wizard, BotStyle style) {
        var found = new List<(WizClientObjectItem, string, Piece, double)>();
        if (wizard?.InventoryBehavior?.Items is not { } items) {
            return found;
        }

        var school = SchoolOf(wizard);
        var level = LevelOf(wizard);
        foreach (var slot in Slots) {
            var (worn, wearing) = Wearing(wizard, slot, style);
            // A worn piece whose stats can't all be read stays on: unknown is not worthless.
            if (worn is not null && PartlyRead(worn.Id)) {
                continue;
            }

            var best = items.Select(i => (Item: i, Piece: Read((uint) Math.Min((ulong) i.m_templateID, uint.MaxValue))))
                .Where(x => x.Piece is not null && x.Piece.Slot == slot.ToString() && CanWear(x.Piece, school, level) && !PartlyRead(x.Piece.Id))
                .Select(x => (x.Item, x.Piece, Score: Score(x.Piece, school, style)))
                .OrderByDescending(x => x.Score).FirstOrDefault();
            if (best.Item is not null && best.Score > wearing + 0.5) {
                found.Add((best.Item, slot.ToString(), best.Piece, best.Score - wearing));
            }
        }

        return found;
    }

    // ---------------------------------------------------------------------
    // Alone, by goals (Niduus, 2026-11-14: "As a balance player, he would want gear that fits him better overall ... Enough
    // accuracy. Enough resist, Enough HP, attempt to gain 100% power pip chance with Damage being the top priority along
    // side pierce. Granted that he is a solo quester, his priority is to survive while also being able to do damage")
    // ---------------------------------------------------------------------

    /// <summary>What a solo wizard wants from its whole outfit (Ultimus's playbook gives the numbers).</summary>
    internal sealed record Goals(double Accuracy, double Resist, double Health, double PowerPips, double DamageWeight, double PierceWeight,
                                 double FloorWeight, double OverFloorShare);

    /// <summary>The outfit's totals that the goals look at (percent, health points), each school's stat by its share.</summary>
    internal sealed record Totals(double Damage, double Pierce, double Accuracy, double Resist, double Health, double PowerPips, double Other) {
        internal string Describe(Goals goals) =>
            $"damage {Damage:0.#}%, pierce {Pierce:0.#}%, accuracy {Accuracy:0.#}% (want {goals.Accuracy:0.#}), resist {Resist:0.#}% (want {goals.Resist:0.#}), " +
            $"health +{Health:0} (want +{goals.Health:0}), power pip {PowerPips:0.#}% (toward {goals.PowerPips:0})";
    }

    /// <summary>A change of one piece and why, in words.</summary>
    internal sealed record Swap(WizClientObjectItem Item, string Slot, Piece Piece, Piece Worn, double Gain, Totals Before, Totals After, string Why);

    internal static Totals TotalsOf(IEnumerable<Piece> pieces, string school) {
        double damage = 0, pierce = 0, accuracy = 0, resist = 0, health = 0, power = 0, other = 0;
        foreach (var piece in pieces.Where(p => p is not null)) {
            foreach (var stat in piece.Stats) {
                // Every-school stats count fully in the totals (they are the wizard's own as much as its school's).
                var share = stat.School.Length == 0 ? 1.0 : Share(stat, school, BotStyle.Solo);
                if (share <= 0) {
                    continue;
                }

                var value = share * Percent(stat);
                switch (stat.Kind) {
                    case "Damage": damage += value; break;
                    case "ArmorPiercing": pierce += value; break;
                    case "Accuracy": accuracy += value; break;
                    case "ReduceDamage": resist += value; break;
                    case "MaxHealth": health += value; break;
                    case "PowerPips": power += value; break;
                    default: other += value * Weight(stat.Kind, BotStyle.Solo); break;
                }
            }

            if (piece.Slot == nameof(EquipmentSlotType.Deck)) {
                other += (piece.DeckSpells * 0.6) + (piece.DeckSchool.Equals(school, StringComparison.OrdinalIgnoreCase) ? piece.DeckSchoolCopies * 3 : 0);
            }
        }

        return new Totals(damage, pierce, accuracy, resist, health, power, other);
    }

    // The game's stat tables keep most percent stats as shares (0.12 for 12%), and only a few come out of the tables
    // already times 100. The goals are in percent, so a share is turned into percent here. Before, the totals read 12%
    // damage as 0.12%, no outfit ever met a floor and no swap was ever worth making, so Ultimus never put on the Adept
    // robe (Niduus, 2026-11-14: "He still isn't using the adept robe").
    private static readonly HashSet<string> s_percentKinds = new(StringComparer.Ordinal) {
        "Damage", "ArmorPiercing", "Accuracy", "ReduceDamage", "PowerPips",
    };

    private static double Percent(Stat stat) =>
        s_percentKinds.Contains(stat.Kind) && Math.Abs(stat.Value) > 0 && Math.Abs(stat.Value) < 1.5 ? stat.Value * 100 : stat.Value;

    /// <summary>How good an outfit is for the goals: under a floor counts most, damage and pierce lead above them.</summary>
    internal static double Value(Totals t, Goals g) {
        double Floor(double have, double want, double weight) => (weight * Math.Min(have, want)) + (weight * g.OverFloorShare * Math.Max(0, have - want));

        return Floor(t.Resist, g.Resist, g.FloorWeight)
               + Floor(t.Accuracy, g.Accuracy, g.FloorWeight * 0.75)
               // Health: points, not percent; 25 health under the floor is about one point of resist.
               + Floor(t.Health, g.Health, g.FloorWeight / 25)
               + (Math.Min(t.PowerPips, g.PowerPips) * 2.5)
               + (t.Damage * g.DamageWeight) + (t.Pierce * g.PierceWeight) + t.Other;
    }

    /// <summary>The worn outfit, a piece per slot (two rings count as the one ring slot the game has).</summary>
    internal static Dictionary<EquipmentSlotType, Piece> Outfit(Wizard wizard) {
        var outfit = new Dictionary<EquipmentSlotType, Piece>();
        foreach (var slot in Slots) {
            var item = wizard?.EquipmentBehavior?.GetItemInSlot(slot);
            outfit[slot] = item is null ? null : Read((uint) Math.Min((ulong) item.m_templateID, uint.MaxValue));
        }

        return outfit;
    }

    /// <summary>
    /// The one swap from the backpack that best brings the whole outfit toward the goals, with the reason; null when the
    /// outfit is as good as the backpack allows.
    /// </summary>
    internal static Swap BestSwap(Wizard wizard, Goals goals, double least = 0.5) {
        if (wizard?.InventoryBehavior?.Items is not { } items) {
            return null;
        }

        var school = SchoolOf(wizard);
        var level = LevelOf(wizard);
        var outfit = Outfit(wizard);
        var before = TotalsOf(outfit.Values, school);
        var now = Value(before, goals);
        Swap best = null;
        foreach (var slot in Slots) {
            var worn = outfit[slot];
            if (worn is not null && PartlyRead(worn.Id)) {
                continue;
            }

            foreach (var item in items) {
                var piece = item is null ? null : Read((uint) Math.Min((ulong) item.m_templateID, uint.MaxValue));
                if (piece is null || piece.Slot != slot.ToString() || !CanWear(piece, school, level) || PartlyRead(piece.Id)
                    || worn is not null && worn.Id == piece.Id) {
                    continue;
                }

                var trial = new Dictionary<EquipmentSlotType, Piece>(outfit) { [slot] = piece };
                var after = TotalsOf(trial.Values, school);
                var gain = Value(after, goals) - now;
                if (gain > least && (best is null || gain > best.Gain)) {
                    best = new Swap(item, slot.ToString(), piece, worn, gain, before, after, Reason(before, after, goals));
                }
            }
        }

        return best;
    }

    // What a swap changes, in words: the needs it meets first, then damage and pierce.
    private static string Reason(Totals a, Totals b, Goals g) {
        var parts = new List<string>();
        void Need(string what, double from, double to, double want, string unit) {
            if (Math.Abs(to - from) < 0.05) {
                return;
            }

            var under = from < want ? (to >= want ? " and now meets what I want" : $", still short of {want:0.#}{unit}") : "";
            parts.Add($"{what} {from:0.#}{unit} to {to:0.#}{unit}{under}");
        }

        Need("resist", a.Resist, b.Resist, g.Resist, "%");
        Need("accuracy", a.Accuracy, b.Accuracy, g.Accuracy, "%");
        Need("health", a.Health, b.Health, g.Health, "");
        Need("power pip", a.PowerPips, b.PowerPips, g.PowerPips, "%");
        if (Math.Abs(b.Damage - a.Damage) >= 0.05) {
            parts.Add($"damage {a.Damage:0.#}% to {b.Damage:0.#}%");
        }

        if (Math.Abs(b.Pierce - a.Pierce) >= 0.05) {
            parts.Add($"pierce {a.Pierce:0.#}% to {b.Pierce:0.#}%");
        }

        return parts.Count == 0 ? "a better deck or small extras" : string.Join(", ", parts);
    }

    /// <summary>
    /// Gear in the backpack worth nothing to this wizard: not worn, not better than what it wears, and not a Crowns item.
    /// Sold when the backpack is full (Niduus, 2026-11-12). Anything the gear list does not know is left alone.
    /// </summary>
    internal static List<(WizClientObjectItem Item, Piece Piece)> Junk(Wizard wizard, BotStyle style) {
        var junk = new List<(WizClientObjectItem, Piece)>();
        if (wizard?.InventoryBehavior?.Items is not { } items) {
            return junk;
        }

        var worn = Slots.Append(EquipmentSlotType.Mount).Append(EquipmentSlotType.Pet)
            .Select(s => wizard.EquipmentBehavior?.GetItemInSlot(s)?.m_globalID.Full ?? 0).Where(id => id != 0).ToHashSet();
        var school = SchoolOf(wizard);
        foreach (var item in items.ToList()) {
            if (item is null || worn.Contains(item.m_globalID.Full) || ItemHelper.IsLocked(item)) {
                continue;
            }

            var piece = Read((uint) Math.Min((ulong) item.m_templateID, uint.MaxValue));
            if (piece is null || piece.CrownsOnly || !Enum.TryParse<EquipmentSlotType>(piece.Slot, out var slot)) {
                continue;
            }

            var (_, wearing) = Wearing(wizard, slot, style);
            if (Score(piece, school, style) <= wearing) {
                junk.Add((item, piece));
            }
        }

        return junk;
    }

    /// <summary>
    /// What a shop sells that is better than what the wizard wears and that it can pay for, best gain first: the piece, the
    /// shops that sell it (by NPC template) and how much better it is.
    /// </summary>
    internal static List<(Piece Piece, IReadOnlyList<uint> Vendors, double Gain)> ShopUpgrades(Wizard wizard, BotStyle style, int gold) {
        var school = SchoolOf(wizard);
        var level = LevelOf(wizard);
        var found = new List<(Piece, IReadOnlyList<uint>, double)>();
        foreach (var (slot, piece) in BestFor(school, level, style, p => p.Gold > 0 && p.Gold <= gold && ItemSources.VendorsOf(p.Id).Count > 0)) {
            var (_, wearing) = Wearing(wizard, slot, style);
            var gain = Score(piece, school, style) - wearing;
            // Worth a trip: clearly better, not a point or two.
            if (gain > Math.Max(2, wearing * 0.1)) {
                found.Add((piece, ItemSources.VendorsOf(piece.Id), gain));
            }
        }

        return [.. found.OrderByDescending(f => f.Item3)];
    }

    /// <summary>
    /// Dresses a wizard that is never saved (a Bot Player) in gear fit for its school and level: new items in its backpack,
    /// then worn. Returns what it put on.
    /// </summary>
    internal static List<Piece> Dress(Wizard wizard, BotStyle style, Random random = null) {
        var worn = new List<Piece>();
        if (wizard is null) {
            return worn;
        }

        var school = SchoolOf(wizard);
        foreach (var (_, piece) in BestFor(school, LevelOf(wizard), style, pickNear: random)) {
            try {
                if (!wizard.TryCreateItem(piece.Id, out var item) || !wizard.InventoryBehavior.AddItem(item, ignoreLimit: true)) {
                    continue;
                }

                if (wizard.InventoryToEquipmentTransfer(item.m_globalID, out _, out _)) {
                    worn.Add(piece);
                }
            }
            catch (Exception e) {
                Logger.Debug("Bot gear: {0} could not put on {1}: {2}", Logger.Args(wizard.CharId, piece.Name, e.Message));
            }
        }

        return worn;
    }

    /// <summary>What a wizard wears, slot by slot, for the Quest Team page: "Hat: Cap of the Novice".</summary>
    internal static List<string> Describe(Wizard wizard) {
        var lines = new List<string>();
        foreach (var slot in Slots) {
            var item = wizard?.EquipmentBehavior?.GetItemInSlot(slot);
            if (item is null) {
                continue;
            }

            var piece = Read((uint) Math.Min((ulong) item.m_templateID, uint.MaxValue));
            lines.Add($"{(slot == EquipmentSlotType.Weapon ? "Wand" : slot == EquipmentSlotType.Shoes ? "Boots" : slot.ToString())}: " +
                      $"{piece?.Name ?? item.m_templateID.ToString()}{(piece is { Level: > 0 } ? $" (level {piece.Level})" : "")}");
        }

        return lines;
    }

}
