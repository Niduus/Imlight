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
using System.Globalization;
using System.Linq;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.MonsterMagic;

/// <summary>
/// The Monstrodome (HOUSE_SP_MonsterMagicArena and its Autumn, Summer and Krampus copies): a house object whose "Set
/// Monsters" window takes up to four Summon Monster treasure cards, and whose arena (a zone of its own, the object's
/// ExtraHousingZoneBehavior m_lotWorldZone) has those monsters waiting to be fought.
/// <para>
/// Read from the game (WizardGraphicalClient.exe, 2026-11-12): MonsterArenaBehavior::OnPostLoad (0x140f83400) loads its
/// m_monsterData with SerializerBinary(data, true, 0) into itself with property mask 0x10000, which only m_mobList and
/// m_zoneGID carry. The Item blob entry's custom data is that m_monsterData. The window (MonsterArenaWindow.gui, the
/// "Set Monsters" button of house edit mode) sends MSG_MONSTERMAGICADDMONSTER with the Summon card and
/// MSG_MONSTERMAGICERASEMONSTER with the monster; ClientBlobPatcher::MSG_PatchMonsterArena (0x140f1dfa0) takes the new
/// data like the Castle Magic patch does (the entry must already be a custom one, at the blob's version + 1).
/// The arena zone has a "Start" location, one duel circle and a HousingReturn object (template 1397386) to go home by.
/// </para>
/// </summary>
internal static class Monstrodome {

    internal const int MAX_MONSTERS = 4;
    internal const string START_LOCATION = "Start";

    /// <summary>The HousingReturn object of an arena: the way back to the house.</summary>
    internal const uint HOUSING_RETURN_TEMPLATE = 1397386;

    private const uint BEHAVIOR_MASK = 0x10000;
    private const string VARIANT_PREFIX = "Monstrodome:";
    private const string SUMMON_ADJECTIVE = "MonsterMagicSummon";

    // How far from the duel circle, toward where the player comes in, the monsters stand, and how far apart.
    private const float MONSTER_DISTANCE = 300f;
    private const float MONSTER_SPACING = 160f;

    /// <summary>[Monstrology] MonstrodomeEnabled: whether a placed Monstrodome takes monsters and opens its arena.</summary>
    internal static bool Enabled => ConfigurationManager.GetValue("Monstrology.MonstrodomeEnabled", true);

    private static readonly ConcurrentDictionary<uint, string> s_arenaZones = new();
    private static readonly ConcurrentDictionary<string, byte> s_knownZones = new(StringComparer.OrdinalIgnoreCase) {
        ["Housing_MonsterMagicArena/Interior"] = 0,
        ["Housing_SP_TomeFallArena/Interior"] = 0,
        ["Housing_SP_TomeSummerArena/Interior"] = 0,
        ["Housing_KrampusMagicArena/Interior"] = 0,
    };

    // Where each player in an arena goes back to: the house they came from.
    private static readonly ConcurrentDictionary<ulong, (ulong OwnerCharId, uint HouseTemplateId)> s_returns = new();

    /// <summary>
    /// Whether an item template is a Monstrodome, and the arena zone it opens.
    /// </summary>
    internal static bool IsArena(uint templateId, out string zone) {
        zone = s_arenaZones.GetOrAdd(templateId, id => {
            if (CoreObjectFactory.GetCoreTemplate(id) is not WizItemTemplate template
                || !(template.m_behaviors ?? []).Any(b => b is MonsterArenaBehaviorTemplate)) {
                return "";
            }

            string lot = (template.m_behaviors ?? []).OfType<ExtraHousingZoneBehaviorTemplate>().FirstOrDefault()?.m_lotWorldZone;

            return lot ?? "";
        });
        if (string.IsNullOrEmpty(zone)) {
            return false;
        }

        s_knownZones.TryAdd(zone, 0);

        return true;
    }

    /// <summary>Whether a zone is a Monstrodome's arena.</summary>
    internal static bool IsArenaZone(string zone) => !string.IsNullOrEmpty(zone) && s_knownZones.ContainsKey(zone);

    /// <summary>The data of a Monstrodome with no monsters.</summary>
    internal static byte[] Empty => Encode([]);

    /// <summary>
    /// The custom data of a Monstrodome holding these monsters, as the client's MonsterArenaBehavior loads it.
    /// </summary>
    internal static byte[] Encode(IReadOnlyList<uint> monsters) {
        var behavior = new MonsterArenaBehavior { m_mobList = [.. (monsters ?? []).Take(MAX_MONSTERS)] };
        if (!NewSerializer().Serialize(behavior, BEHAVIOR_MASK, out var data)) {
            Logger.Warning("Monstrodome: the monster list could not be written.");

            return [];
        }

        byte[] bytes = data;

        return bytes ?? [];
    }

    /// <summary>
    /// The monsters a Monstrodome's custom data holds; none for no data or data that cannot be read.
    /// </summary>
    internal static List<uint> Decode(byte[] data) {
        if (data is not { Length: > 0 }) {
            return [];
        }

        try {
            if (NewSerializer().Deserialize<PropertyClass>(data, BEHAVIOR_MASK, out var read) && read is MonsterArenaBehaviorBase arena) {
                return [.. (arena.m_mobList ?? []).Where(id => id != 0).Take(MAX_MONSTERS)];
            }
        }
        catch (Exception e) {
            Logger.Warning("Monstrodome: the monster list could not be read: {0}", Logger.Args(e.Message));
        }

        return [];
    }

    /// <summary>
    /// The Summon Monster card the window names (a template id, or the hash of the spell's name) and the monster it
    /// summons (the card's kSummonCreature effect, or the id in its name "Summon&lt;id&gt;").
    /// </summary>
    internal static bool TryReadCard(uint spellId, out uint cardTemplateId, out uint monster) {
        monster = 0;
        cardTemplateId = CoreObjectFactory.GetCoreTemplate(spellId) is SpellTemplate ? spellId : SpellFactory.GetTemplateIdByHash(spellId);
        if (cardTemplateId == 0 || CoreObjectFactory.GetCoreTemplate(cardTemplateId) is not SpellTemplate card) {
            return false;
        }

        var isSummonCard = (card.m_adjectives ?? []).Any(a => string.Equals(a, SUMMON_ADJECTIVE, StringComparison.OrdinalIgnoreCase));
        var effect = CombatEnchantments.Leaves(card.m_effects).FirstOrDefault(e => e.m_effectType == kSpellEffects.kSummonCreature && e.m_effectParam > 0);
        if (effect is not null) {
            monster = (uint) effect.m_effectParam;
        }
        else {
            string name = card.m_name;
            if (name?.StartsWith("Summon", StringComparison.Ordinal) == true
                && uint.TryParse(name["Summon".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var id)) {
                monster = id;
            }
        }

        return isSummonCard && monster != 0 && CoreObjectFactory.GetCoreTemplate(monster) is GameObjectTemplate;
    }

    /// <summary>The zone variant that carries a Monstrodome's monsters into its arena.</summary>
    internal static string VariantFor(IReadOnlyList<uint> monsters)
        => VARIANT_PREFIX + string.Join(",", (monsters ?? []).Take(MAX_MONSTERS));

    /// <summary>Whether a zone variant is a Monstrodome's, and its monsters.</summary>
    internal static bool TryReadVariant(string variant, out List<uint> monsters) {
        monsters = [];
        if (variant?.StartsWith(VARIANT_PREFIX, StringComparison.Ordinal) != true) {
            return false;
        }

        foreach (var part in variant[VARIANT_PREFIX.Length..].Split(',', StringSplitOptions.RemoveEmptyEntries)) {
            if (uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id != 0) {
                monsters.Add(id);
            }
        }

        monsters = [.. monsters.Take(MAX_MONSTERS)];

        return true;
    }

    /// <summary>
    /// Where the arena's monsters stand: in a row beside the duel circle, on the side the player walks in from, facing
    /// the player.
    /// </summary>
    internal static List<(uint TemplateId, float X, float Y, float Z, float Yaw)> Places(IReadOnlyList<uint> monsters,
        (float X, float Y, float Z) circle, (float X, float Y)? start) {
        var dx = start.HasValue ? start.Value.X - circle.X : 1f;
        var dy = start.HasValue ? start.Value.Y - circle.Y : 0f;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 1f) {
            (dx, dy, length) = (1f, 0f, 1f);
        }

        dx /= length;
        dy /= length;
        var yaw = MathF.Atan2(dy, dx);
        var places = new List<(uint, float, float, float, float)>();
        for (var i = 0; i < monsters.Count; i++) {
            var side = (i - (monsters.Count - 1) / 2f) * MONSTER_SPACING;
            places.Add((monsters[i],
                circle.X + dx * MONSTER_DISTANCE - dy * side,
                circle.Y + dy * MONSTER_DISTANCE + dx * side,
                circle.Z,
                yaw));
        }

        return places;
    }

    /// <summary>Remembers the house a player goes into an arena from.</summary>
    internal static void RememberReturn(ulong charId, ulong ownerCharId, uint houseTemplateId)
        => s_returns[charId] = (ownerCharId, houseTemplateId);

    /// <summary>The house a player went into an arena from.</summary>
    internal static bool TryGetReturn(ulong charId, out ulong ownerCharId, out uint houseTemplateId) {
        if (s_returns.TryGetValue(charId, out var back)) {
            (ownerCharId, houseTemplateId) = back;

            return true;
        }

        (ownerCharId, houseTemplateId) = (0, 0);

        return false;
    }

    /// <summary>A name for a monster, for messages.</summary>
    internal static string NameOf(uint monster) {
        if (Monstrology.MonsterOf(monster) is { } known) {
            return known.Name;
        }

        if (CoreObjectFactory.GetCoreTemplate(monster) is GameObjectTemplate template) {
            string key = template.m_displayName;
            var name = string.IsNullOrEmpty(key) ? null : Locale.GetEnglishName(key);

            return string.IsNullOrEmpty(name) ? (string) template.m_objectName ?? monster.ToString() : name;
        }

        return monster.ToString();
    }

    // The client reads the data with object and property sizes and no serializer flags (SerializerBinary(data, true, 0)).
    private static ObjectSerializer NewSerializer() => new(Versionable: true, Behaviors: SerializerFlags.None);

}
