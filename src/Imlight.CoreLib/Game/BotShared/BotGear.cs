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

        s_pieces[templateId] = piece;

        return piece;
    }

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
        var (level, schoolNeeded) = Requirements(template.m_equipRequirements);
        var stats = new List<Stat>();
        foreach (var effect in template.m_equipEffects ?? []) {
            switch (effect) {
                case StatisticEffectInfo info when ReadStat(info) is { } stat:
                    stats.Add(stat);
                    break;
                case StartingPipEffectInfo pips:
                    stats.Add(new Stat("StartingPips", "", pips.m_pipsGiven + (pips.m_powerPipsGiven * 2)));
                    break;
            }
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

    /// <summary>How good a piece is for a wizard of this school and style; 0 for nothing it uses.</summary>
    internal static double Score(Piece piece, string school, BotStyle style) {
        if (piece is null) {
            return 0;
        }

        var score = 0.0;
        foreach (var stat in piece.Stats) {
            var own = stat.School.Length > 0 && stat.School.Equals(school, StringComparison.OrdinalIgnoreCase);
            var all = stat.School.Length == 0;
            // Its own school's stats count fully; every school's a little less (class over universal); other schools' a
            // resist still helps a bit, the rest not at all.
            var share = own ? 1.0 : all ? 0.8 : stat.Kind.Contains("ReduceDamage", StringComparison.Ordinal) ? 0.15 : 0;
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

    private static double Weight(string kind, BotStyle style) => kind switch {
        "Damage" => style switch { BotStyle.Hitter => 4, BotStyle.Backup => 3.5, BotStyle.Tank => 1.5, _ => 1 },
        "FlatDamage" => style is BotStyle.Hitter or BotStyle.Backup ? 1.2 : 0.4,
        "ReduceDamage" => style switch { BotStyle.Tank => 4, BotStyle.Healer => 3, BotStyle.Backup => 2.5, _ => 2 },
        "FlatReduceDamage" => style == BotStyle.Tank ? 1.5 : 1,
        "MaxHealth" => style switch { BotStyle.Tank => 0.1, BotStyle.Healer => 0.07, _ => 0.06 },
        "Accuracy" => style == BotStyle.Hitter ? 1.5 : 1,
        "ArmorPiercing" => style switch { BotStyle.Hitter => 2.5, BotStyle.Backup => 2, _ => 1 },
        "CriticalHit" => style is BotStyle.Hitter or BotStyle.Backup ? 0.25 : 0.12,
        "Block" => style == BotStyle.Tank ? 0.2 : 0.1,
        "Healing" => style == BotStyle.Healer ? 3 : 0.5,
        "IncHealing" => style == BotStyle.Tank ? 1.5 : 1,
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
            var (_, wearing) = Wearing(wizard, slot, style);
            var best = items.Select(i => (Item: i, Piece: Read((uint) Math.Min((ulong) i.m_templateID, uint.MaxValue))))
                .Where(x => x.Piece is not null && x.Piece.Slot == slot.ToString() && CanWear(x.Piece, school, level))
                .Select(x => (x.Item, x.Piece, Score: Score(x.Piece, school, style)))
                .OrderByDescending(x => x.Score).FirstOrDefault();
            if (best.Item is not null && best.Score > wearing + 0.5) {
                found.Add((best.Item, slot.ToString(), best.Piece, best.Score - wearing));
            }
        }

        return found;
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
