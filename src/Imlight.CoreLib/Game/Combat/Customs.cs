/*
 * Custom creatures (Niduus, 2026-10-22: "mobs and anything that's considered CUSTOM are their own separate entities.
 * So like my 100k Mali, if I make a custom using his ID, it will use THAT id when I do a summon for him"), made in the
 * app's Customs tab and read from spiraldb-extra/Customs/<custom id>.json. The game files are never changed.
 *
 * A custom is a game creature (BaseTemplateID, such as Malistaire the Undying 1336035) with changes of its own, in the
 * Bosses tab's format (BossOverrides.cs: Health, the school grid, Deck, Cheats and the rest), and:
 *   CustomID        its number (1, 2, 3...), the file's name; .joinme Malistaire the Undying 1 brings it in.
 *   Name            the name it shows in battle (empty: the creature's own).
 *   Drops           drop table names (DropTables) it drops; left out or empty: the creature's own drops.
 *   Forms           a creature it turns into (a polymorph such as Haunted Malistaire 1336037) to the custom that form
 *                   is then: { "1336037": 2 }. A form not named is the game's creature.
 * It starts from the game's creature, not from the creature's own Bosses tab changes, and its changes count only for
 * a creature spawned as it: .joinme with its number, or a zone spawner a ZoneObjects file (ReplaceSpawns) or the
 * Customs tab's rooms (Customs/Rooms.json) swaps for it. Its cheats are the creature's BossCheats file with the
 * custom's own Cheats changes laid over it.
 *
 * A creature spawned as a custom is marked by its global id (Mark); everything that reads the changes of a creature
 * uses KeyOf, which is the template id for any other creature, and a key of the custom's own (Key) for one, so the
 * files of the two never mix. A saved file counts from the next battle or spawn, without a restart.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.WizardData;

namespace Imlight.CoreLib.Game.Combat;

internal static class Customs {

    internal const string FOLDER = "Customs";

    /// <summary>The Customs tab's rooms: a zone copy's creature templates spawned as customs.</summary>
    internal const string ROOMS_FILE = "Rooms.json";

    // A custom's key is above every template id (template ids are 32-bit), so it never names a game creature.
    private const ulong KEY_MARK = 1UL << 48;

    // The custom each spawned creature is, by its global id.
    private static readonly ConcurrentDictionary<ulong, int> s_marked = new();

    private static readonly JsonSerializerOptions s_options = new() {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>The key a custom's changes are kept under (BossOverrides, BossCheats).</summary>
    internal static ulong Key(int customId) => KEY_MARK | (uint) customId;

    internal static bool IsCustom(ulong key) => (key & KEY_MARK) != 0;

    internal static int IdOf(ulong key) => IsCustom(key) ? (int) (key & uint.MaxValue) : 0;

    internal static string FileOf(int customId) => Path.Combine(SpiralDB.ExtraPath, FOLDER, $"{customId}.json");

    /// <summary>A custom's file as last read, or null when there is no such custom.</summary>
    internal static BossOverrides.BossOverride Get(int customId)
        => customId > 0 ? BossOverrides.For(Key(customId)) : null;

    /// <summary>Marks a creature as a custom before its actor is made, so its name and changes follow.</summary>
    internal static void Mark(ulong globalId, int customId) {
        if (customId > 0) {
            s_marked[globalId] = customId;
        }
    }

    /// <summary>The custom a creature was spawned as, or 0.</summary>
    internal static int Of(ulong globalId) => s_marked.TryGetValue(globalId, out var id) ? id : 0;

    /// <summary>Forgets a creature's mark when it is gone.</summary>
    internal static void Forget(ulong globalId) => s_marked.TryRemove(globalId, out _);

    /// <summary>The key of a creature's changes: its custom's key, or its template id.</summary>
    internal static ulong KeyOf(CoreObject creature)
        => creature is null ? 0 : Of(creature.m_globalID.Full) is var id and > 0 ? Key(id) : creature.m_templateID.Full;

    /// <summary>The game creature a key is: the custom's base template, or the key itself.</summary>
    internal static ulong BaseOf(ulong key) => IsCustom(key) ? Get(IdOf(key))?.BaseTemplateID ?? 0 : key;

    /// <summary>The name a custom shows, or null for the creature's own.</summary>
    internal static string NameOf(ulong globalId)
        => Get(Of(globalId)) is { Name: { Length: > 0 } name } ? name : null;

    /// <summary>
    /// The key of the creature a polymorph turns a creature into: the custom its custom's Forms names for it, or the
    /// form's template.
    /// </summary>
    internal static ulong FormKey(CoreObject creature, ulong formTemplateId) {
        if (formTemplateId == 0 || Get(Of(creature?.m_globalID.Full ?? 0)) is not { Forms: { } forms }) {
            return formTemplateId;
        }

        return forms.TryGetValue(formTemplateId.ToString(), out var form) && Get(form) is { } custom
               && custom.BaseTemplateID == formTemplateId
            ? Key(form)
            : formTemplateId;
    }

    /// <summary>Every custom, by number.</summary>
    internal static List<BossOverrides.BossOverride> All() {
        var dir = Path.Combine(SpiralDB.ExtraPath, FOLDER);
        if (!Directory.Exists(dir)) {
            return [];
        }

        return [.. Directory.GetFiles(dir, "*.json")
            .Select(f => int.TryParse(Path.GetFileNameWithoutExtension(f), out var id) ? Get(id) : null)
            .Where(c => c is not null)
            .OrderBy(c => c.CustomID)];
    }

    // The rooms file as last read, kept until it changes.
    private static (DateTime Written, Dictionary<string, Dictionary<string, int>> Rooms) s_rooms;

    /// <summary>
    /// The custom a zone copy spawns instead of a creature template (the Customs tab's rooms), -1 for the game's
    /// creature whatever its ZoneObjects file says, or 0 when the rooms do not name it. The room is the zone path with
    /// @variant for a variant, as the ZoneObjects file names it.
    /// </summary>
    internal static int RoomCustomFor(string zonePath, string variant, ulong templateId) {
        var file = Path.Combine(SpiralDB.ExtraPath, FOLDER, ROOMS_FILE);
        if (!File.Exists(file)) {
            return 0;
        }

        var written = File.GetLastWriteTimeUtc(file);
        if (s_rooms.Rooms is null || s_rooms.Written != written) {
            try {
                var rooms = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(File.ReadAllText(file), s_options) ?? [];
                s_rooms = (written, new Dictionary<string, Dictionary<string, int>>(rooms, StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception e) {
                Logger.Warning("Customs {0} could not be read: {1}", Logger.Args(ROOMS_FILE, e.Message));
                s_rooms = (written, []);
            }
        }

        var room = string.IsNullOrEmpty(variant) ? zonePath : $"{zonePath}@{variant}";

        return s_rooms.Rooms.TryGetValue(room, out var swaps) && swaps.TryGetValue(templateId.ToString(), out var id) ? id : 0;
    }

}
