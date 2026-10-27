/*
 * "Defeat" steps that name the talking NPC instead of the creature you fight (quest checker, 2026-10-27).
 *
 * Many rebuilt quests (spiraldb-extra QuestTemplates, from Wizard404) mark a step's foe with the adjective of the NPC
 * who stands in for it: "KM-ESTATE-NPC01.AdjRef" is Rosina the talking NPC, while the battle in her house is against
 * Cabal Soldiers with adjectives of their own. No battle could count for such a step. A creature with the same English
 * name as one of those NPCs counts for the step too ("Cabal Soldier", "Nana", "Inspector Clooso").
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Quests;

internal static class BountyStandIns {
    private const string ADJECTIVE_SUFFIX = ".AdjRef";

    // An adjective's NPC name ("" when it is no talking NPC's own adjective), and a creature's name, worked out once.
    private static readonly ConcurrentDictionary<string, string> s_npcNames = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<ulong, string> s_foeNames = new();

    /// <summary>The English names of the talking NPCs a step's adjectives belong to.</summary>
    internal static HashSet<string> NamesOf(IEnumerable<string> adjectives) {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var adjective in adjectives ?? []) {
            if (!string.IsNullOrEmpty(adjective) && s_npcNames.GetOrAdd(adjective, NpcNameOf) is { Length: > 0 } name) {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>True when the creature has the name of one of the talking NPCs a step's adjectives belong to.</summary>
    internal static bool Counts(IEnumerable<string> adjectives, ulong creatureTemplateId) {
        if (creatureTemplateId == 0) {
            return false;
        }

        var name = FoeNameOf(creatureTemplateId);
        return name.Length > 0 && NamesOf(adjectives).Contains(name);
    }

    /// <summary>The English name of a creature you can fight, or "".</summary>
    internal static string FoeNameOf(ulong templateId) => s_foeNames.GetOrAdd(templateId, id =>
        Template(id) is { } template && IsFoe(template) ? EnglishName(template) : "");

    internal static bool IsFoe(GameObjectTemplate template)
        => template.m_behaviors?.Any(b => b is DuelistBehaviorTemplate) == true;

    private static string NpcNameOf(string adjective) {
        if (!adjective.EndsWith(ADJECTIVE_SUFFIX, StringComparison.OrdinalIgnoreCase)) {
            return "";
        }

        var id = CoreObjectFactory.IdOfFileName(adjective[..^ADJECTIVE_SUFFIX.Length]);
        return id != 0 && Template(id) is { } template && !IsFoe(template) ? EnglishName(template) : "";
    }

    private static string EnglishName(GameObjectTemplate template) {
        try {
            return ((string) template.m_displayName) is { Length: > 0 } key ? (Locale.GetEnglishName(key) ?? "").Trim() : "";
        }
        catch (Exception) {
            return "";
        }
    }

    private static GameObjectTemplate Template(ulong id) {
        try {
            return CoreObjectFactory.GetCoreTemplate(id) as GameObjectTemplate;
        }
        catch (Exception) {
            return null;
        }
    }
}
