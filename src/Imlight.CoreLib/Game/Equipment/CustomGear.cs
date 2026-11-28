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
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Imcodec.Cryptography;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Effects;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Equipment;

/// <summary>
/// Gear made in Blood Moon (Niduus, 2026-11-23: "custom gear stats and choice of what the gear looks like. Using the QA
/// gear as default look if not assigned"). Each piece lives in spiraldb-extra/Customs/CustomGear.json (read again when
/// the file changes, no restart). A piece is a real game item of its slot, the "carrier": the developers' QA item for
/// that slot, so it can be worn by anyone at any level. Its look is the QA item's own unless the maker picked another
/// item of the same slot, which is shown the way the Seamstress shows a stitched look (m_displayID). While worn, the
/// carrier's own stats are taken off and the piece's own stats are put on, the same way pet talents and jewels are
/// (see <see cref="EquipmentExtras"/>), so the character's stats page shows the custom numbers. The game's hover box
/// still shows the carrier's name and its own numbers, because the game reads those from its own files.
/// Which items are custom pieces is kept in custom-gear-items.json next to the server.
/// </summary>
internal static class CustomGear {

    internal const string FILE = "CustomGear.json";
    internal const string ITEMS_FILE = "custom-gear-items.json";

    /// <summary>One stat line: a kind (Damage, Accuracy, Resist, MaxHealth, ...), a school or "All", and the number.</summary>
    internal sealed record Stat(string Kind, string School, float Value);

    /// <summary>One custom piece as the file holds it.</summary>
    internal sealed record Piece(int Id, string Name, EquipmentSlotType Slot, uint Look, List<Stat> Stats, List<int> Cards,
                                 List<string> Spells);

    // The QA item that carries each slot (Root/ObjectData/QA_Gear). Names first, so a game update that renumbers still finds them.
    private static readonly Dictionary<EquipmentSlotType, (string Name, uint Id)> s_carriers = new() {
        [EquipmentSlotType.Hat] = ("ResistAll_Hat_05", 1317127),
        [EquipmentSlotType.Robe] = ("ResistAll_Robe_05", 1317129),
        [EquipmentSlotType.Shoes] = ("QA_SpeedBoots_100", 1317234),
        [EquipmentSlotType.Weapon] = ("QA_Wand-NotPlaceable", 1383146),
        [EquipmentSlotType.Athame] = ("QA_Socket_Athame", 1363075),
        [EquipmentSlotType.Amulet] = ("QA_Accuracy_Amulet_100", 1359457),
        [EquipmentSlotType.Ring] = ("QA_One_Ring", 1363076),
    };

    private static readonly object s_lock = new();
    private static DateTime s_checked = DateTime.MinValue;
    private static DateTime s_fileTime = DateTime.MinValue;
    private static Dictionary<int, Piece> s_pieces = [];
    private static ConcurrentDictionary<ulong, int> s_items;

    private static string FilePath => Path.Combine(SpiralDB.ExtraPath, "Customs", FILE);
    private static string ItemsPath => Path.Combine(Path.GetDirectoryName(SpiralDB.ExtraPath) ?? ".", ITEMS_FILE);

    /// <summary>Every piece in the file now.</summary>
    internal static IReadOnlyCollection<Piece> All {
        get {
            Reload();
            lock (s_lock) {
                return [.. s_pieces.Values.OrderBy(p => p.Id)];
            }
        }
    }

    /// <summary>A piece by its number or (part of) its name.</summary>
    internal static Piece Find(string what) {
        var all = All;
        if (int.TryParse(what?.Trim(), out var id)) {
            return all.FirstOrDefault(p => p.Id == id);
        }

        var name = what?.Trim() ?? "";
        return all.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(p => p.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The custom piece an item is, or null for an ordinary item.</summary>
    internal static Piece Of(WizClientObjectItem item) {
        if (item is null || !Items.TryGetValue(item.m_globalID.Full, out var id)) {
            return null;
        }

        Reload();
        lock (s_lock) {
            return s_pieces.GetValueOrDefault(id);
        }
    }

    /// <summary>The QA item template that carries a slot, or null.</summary>
    internal static WizItemTemplate Carrier(EquipmentSlotType slot) {
        if (!s_carriers.TryGetValue(slot, out var carrier)) {
            return null;
        }

        if (CoreObjectFactory.GetCoreTemplate(carrier.Id) is WizItemTemplate byId && (string) byId.m_objectName == carrier.Name) {
            return byId;
        }

        return CoreObjectFactory.TryGetCoreTemplateID(t => ((string) t.m_filename ?? "").Replace('\\', '/').EndsWith("/" + carrier.Name + ".xml", StringComparison.OrdinalIgnoreCase), out uint found)
               && CoreObjectFactory.GetCoreTemplate(found) is WizItemTemplate byName
            ? byName
            : CoreObjectFactory.GetCoreTemplate(carrier.Id) as WizItemTemplate;
    }

    /// <summary>
    /// Makes the piece as an item for a wizard (not placed anywhere yet). Null with the reason when it cannot be made.
    /// </summary>
    internal static WizClientObjectItem Make(Wizard wizard, Piece piece, out string problem) {
        problem = null;
        var carrier = Carrier(piece.Slot);
        if (carrier is null) {
            problem = $"Blood Moon gear for the {SlotWord(piece.Slot)} slot can't be made: the game files have no QA item for it.";
            return null;
        }

        if (!wizard.TryCreateItem((uint) carrier.m_templateID, out var item)) {
            problem = $"Could not make the QA item {(string) carrier.m_objectName}.";
            return null;
        }

        // The look: another item of the same slot, shown like a stitched one; none keeps the QA item's own.
        if (piece.Look != 0 && piece.Look != (uint) carrier.m_templateID) {
            if (CoreObjectFactory.GetCoreTemplate(piece.Look) is WizItemTemplate look && SameSlot(look, piece.Slot)) {
                item.m_displayID = piece.Look;
            }
            else {
                Logger.Warning("Custom gear {0} ({1}): look {2} is not an item of the {3} slot; the QA look is used.",
                    Logger.Args(piece.Id, piece.Name, piece.Look, piece.Slot));
            }
        }

        Items[item.m_globalID.Full] = piece.Id;
        SaveItems();
        Logger.Information("Custom gear {0} ({1}) made as item {2} for {3}: carrier {4}, look {5}, {6} stats.",
            Logger.Args(piece.Id, piece.Name, item.m_globalID.Full, wizard.CharId, (uint) carrier.m_templateID,
                item.m_displayID == 0 ? "QA" : item.m_displayID.ToString(), piece.Stats.Count));

        return item;
    }

    private static bool SameSlot(WizItemTemplate template, EquipmentSlotType slot) {
        try {
            return ItemHelper.GetItemSlot(template)?.SlotType == slot;
        }
        catch (InvalidOperationException) {
            return false;
        }
    }

    internal static string SlotWord(EquipmentSlotType slot) => slot == EquipmentSlotType.Weapon ? "wand" : slot.ToString().ToLowerInvariant();

    // ------------------------------------------------------------------ stats

    /// <summary>
    /// The piece's stats as the game's stat effects (named like the game's, "CanonicalFireDamage"), each with the number
    /// the game shows (20 = 20%).
    /// </summary>
    internal static List<(string Name, WizStatisticEffect Effect)> Effects(Piece piece) {
        var list = new List<(string, WizStatisticEffect)>();
        foreach (var stat in piece.Stats) {
            if (stat.Value == 0 || Effect(stat) is not { } made) {
                continue;
            }

            list.Add(made);
        }

        return list;
    }

    private static readonly string[] s_schools = ["Fire", "Ice", "Storm", "Myth", "Life", "Death", "Balance", "Shadow"];

    private static (string, WizStatisticEffect)? Effect(Stat stat) {
        var school = s_schools.FirstOrDefault(s => s.Equals(stat.School?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "All";
        var v = stat.Value;
        var kind = (stat.Kind ?? "").Replace(" ", "").ToLowerInvariant();
        string name;
        var e = new WizStatisticEffect();
        switch (kind) {
            case "damage": name = $"Canonical{school}Damage"; e.m_damageBonusPercent = v; break;
            case "flatdamage": name = $"Canonical{school}FlatDamage"; e.m_damageBonusFlat = v; break;
            case "resist": case "reducedamage": name = $"Canonical{school}ReduceDamage"; e.m_damageReducePercent = v; break;
            case "flatresist": case "flatreducedamage": name = $"Canonical{school}FlatReduceDamage"; e.m_damageReduceFlat = v; break;
            case "accuracy": name = $"Canonical{school}Accuracy"; e.m_accuracyBonusPercent = v; break;
            case "pierce": case "armorpiercing": name = $"Canonical{school}ArmorPiercing"; e.m_armorPiercingBonusPercent = v; break;
            case "critical": case "criticalhit": name = $"Canonical{school}CriticalHit"; e.m_criticalHitRating = v; break;
            case "block": name = $"Canonical{school}Block"; e.m_blockRating = v; break;
            case "pipconversion": name = $"Canonical{school}PipConversion"; e.m_pipConversionRating = v; break;
            case "health": case "maxhealth": name = "CanonicalMaxHealth"; e.m_hitPointBonus = MathF.Round(v); break;
            case "mana": case "maxmana": name = "CanonicalMaxMana"; e.m_manaBonus = MathF.Round(v); break;
            case "powerpip": name = "CanonicalPowerPip"; e.m_powerPipBonusPercent = v; break;
            case "outgoinghealing": case "healing": name = "CanonicalLifeHealing"; e.m_healBonusPercent = v; break;
            case "incominghealing": case "inchealing": name = "CanonicalIncHealing"; e.m_healIncBonusPercent = v; break;
            case "stunresist": case "stunresistance": name = "CanonicalStunResistance"; e.m_stunResistancePercent = v; break;
            case "shadowpip": case "shadowpiprating": name = "CanonicalShadowPipRating"; e.m_shadowPipRating = v; break;
            case "archmastery": name = "CanonicalArchmastery"; e.m_archmastery = v; break;
            default:
                Logger.Warning("Custom gear: stat kind {0} is not known; left out.", Logger.Args(stat.Kind));
                return null;
        }

        e.m_effectNameID = StringHash.Compute(name);

        return (name, e);
    }

    // ------------------------------------------------------------------ the file

    private static void Reload() {
        lock (s_lock) {
            var now = DateTime.UtcNow;
            if (now - s_checked < TimeSpan.FromSeconds(2)) {
                return;
            }

            s_checked = now;
            try {
                var time = File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;
                if (time == s_fileTime) {
                    return;
                }

                s_fileTime = time;
                s_pieces = time == DateTime.MinValue ? [] : Read(File.ReadAllText(FilePath));
                Logger.Information("Custom gear: {0} pieces read from {1}.", Logger.Args(s_pieces.Count, FILE));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
                // Half written: read again on the next check.
                s_fileTime = DateTime.MinValue;
            }
        }
    }

    internal static Dictionary<int, Piece> Read(string json) {
        var pieces = new Dictionary<int, Piece>();
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        foreach (var node in (root?["Gear"] as JsonArray ?? []).OfType<JsonObject>()) {
            try {
                var id = (int?) node["Id"] ?? 0;
                if (id <= 0 || !Enum.TryParse<EquipmentSlotType>(((string) node["Slot"] ?? "").Replace("Wand", "Weapon").Replace("Boots", "Shoes"), true, out var slot)) {
                    continue;
                }

                var stats = (node["Stats"] as JsonArray ?? []).OfType<JsonObject>()
                    .Select(s => new Stat((string) s["Kind"] ?? "", (string) s["School"] ?? "All", (float?) (double?) s["Value"] ?? 0))
                    .ToList();
                var cards = (node["Cards"] as JsonArray ?? []).Select(c => (int?) c ?? 0).Where(c => c > 0).ToList();
                var spells = (node["Spells"] as JsonArray ?? []).Select(c => (string) c ?? "").Where(c => c.Length > 0).ToList();
                pieces[id] = new Piece(id, (string) node["Name"] ?? $"Custom gear {id}", slot, (uint?) (long?) node["Look"] ?? 0, stats, cards, spells);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException) {
                Logger.Warning("Custom gear: a piece in {0} could not be read: {1}", Logger.Args(FILE, ex.Message));
            }
        }

        return pieces;
    }

    private static ConcurrentDictionary<ulong, int> Items {
        get {
            if (s_items is not null) {
                return s_items;
            }

            var items = new ConcurrentDictionary<ulong, int>();
            try {
                if (File.Exists(ItemsPath) && JsonNode.Parse(File.ReadAllText(ItemsPath)) is JsonObject json) {
                    foreach (var (key, value) in json) {
                        if (ulong.TryParse(key, out var gid) && value is not null) {
                            items[gid] = (int) value;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) {
                Logger.Warning("Custom gear: {0} could not be read: {1}", Logger.Args(ITEMS_FILE, ex.Message));
            }

            return s_items ??= items;
        }
    }

    private static void SaveItems() {
        lock (s_lock) {
            try {
                var json = new JsonObject();
                foreach (var (gid, id) in Items.OrderBy(p => p.Key)) {
                    json[gid.ToString()] = id;
                }

                File.WriteAllText(ItemsPath + ".saving", json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(ItemsPath + ".saving", ItemsPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                Logger.Warning("Custom gear: {0} could not be saved: {1}", Logger.Args(ITEMS_FILE, ex.Message));
            }
        }
    }

    // ------------------------------------------------------------------ worn

    /// <summary>What was changed on a wizard for one worn custom piece, so it can be undone.</summary>
    internal sealed class Worn {
        internal ulong ItemId;
        internal WizItemTemplate Carrier;
        internal uint SlotHash;
        internal List<GameEffectBase> CarrierShown = [];
        internal List<(string Name, WizStatisticEffect Effect)> Added = [];
        internal List<GameEffectBase> Shown = [];
    }

    private static readonly ConditionalWeakTable<Wizard, List<Worn>> s_worn = new();

    internal static List<Worn> WornOn(Wizard wizard) => s_worn.GetValue(wizard, _ => []);

}
