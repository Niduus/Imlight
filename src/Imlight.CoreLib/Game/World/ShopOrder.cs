using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.World;

/// <summary>
/// Shop lists in A to Z order by the names players see, with numbers in number order
/// (Flawless Health Opal +65 before +66, and +9 before +10).
/// </summary>
internal static partial class ShopOrder {

    private static readonly ConcurrentDictionary<uint, string> s_keys = new();

    internal static List<GID> Alphabetical(IEnumerable<GID> items)
        => items is null ? [] : [.. items.OrderBy(item => SortKey(item.MParts.TemplateId), StringComparer.OrdinalIgnoreCase)];

    private static string SortKey(uint templateId)
        => s_keys.GetOrAdd(templateId, id => Digits().Replace(ShownName(id), digits => digits.Value.PadLeft(12, '0')));

    private static string ShownName(uint templateId) {
        try {
            if (CoreObjectFactory.GetCoreTemplate(templateId) is ItemTemplate template) {
                string key = template.m_displayName;
                var shown = string.IsNullOrEmpty(key) ? "" : Locale.GetEnglishName(key);

                return string.IsNullOrWhiteSpace(shown) ? (string) template.m_objectName ?? "" : shown;
            }
        }
        catch (Exception) {
            // A template that will not load sorts last.
        }

        return "￿";
    }

    [GeneratedRegex("[0-9]+")]
    private static partial Regex Digits();
}
