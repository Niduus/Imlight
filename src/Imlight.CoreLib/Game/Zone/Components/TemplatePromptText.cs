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
using System.Text;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Zone.Components;

/// <summary>
/// The picture and title an object's own game file gives its press-X prompt. The game's interact behavior carries
/// both (picture: property 0x973CC4F2, title: property 0x7139D4B7), but the Imcodec templates the server reads them
/// with have no such fields, so they were lost: Milaka Jewelbender's crystal in the Crystal Grove ("Help me! Get me
/// out of this crystal!") offered "Press X to Open" with no name and no picture, and a Crystal Sample and a Flax
/// node had no picture either (player reports, 2026-11-28). The game file says "Milaka Jewelbender" with her
/// imprisoned portrait, "Use_crystal_sample.dds" and "Resource_Flax_Harvest.dds". They are read from the template's
/// raw bytes once per template.
/// </summary>
internal static class TemplatePromptText {

    private const uint ICON_HASH = 0x973CC4F2;
    private const uint TITLE_HASH = 0x7139D4B7;

    private static readonly ConcurrentDictionary<ulong, (string Icon, string Title)> s_byTemplate = new();

    /// <summary>The prompt picture the template's file names, or empty.</summary>
    internal static string Icon(ulong templateId) => Read(templateId).Icon;

    /// <summary>The prompt title (a text key) the template's file names, or empty.</summary>
    internal static string Title(ulong templateId) => Read(templateId).Title;

    // An empty ResTeleport in the file's results: class hash 0x0DA3207D, then an object size of 32 bits (no properties).
    private static readonly byte[] s_bareTeleport = [0x7D, 0x20, 0xA3, 0x0D, 0x20, 0x00, 0x00, 0x00];
    private static readonly ConcurrentDictionary<ulong, bool> s_bareTeleports = new();

    /// <summary>
    /// True when the template's own options teleport without saying where (a ResTeleport with nothing in it). The
    /// game's server knows where; the file says only "teleport". The Imcodec templates have no results on these
    /// options at all, so only the raw bytes show it.
    /// </summary>
    internal static bool HasBareTeleport(ulong templateId)
        => templateId != 0 && s_bareTeleports.GetOrAdd(templateId, id => {
            try {
                return RootArchiveLoader.GetRawFile(CoreObjectFactory.FilePathOf(id)) is { } bytes
                       && bytes.AsSpan().IndexOf(s_bareTeleport) >= 0;
            }
            catch (Exception) {
                return false;
            }
        });

    private static (string Icon, string Title) Read(ulong templateId)
        => templateId == 0 ? ("", "") : s_byTemplate.GetOrAdd(templateId, ReadFile);

    private static (string Icon, string Title) ReadFile(ulong templateId) {
        try {
            var bytes = RootArchiveLoader.GetRawFile(CoreObjectFactory.FilePathOf(templateId));
            if (bytes is null || bytes.Length < 12) {
                return ("", "");
            }

            var title = StringAfter(bytes, TITLE_HASH);
            // A title that is no game text names nothing.
            if (title.Length > 0 && string.IsNullOrEmpty(Locale.GetEnglishName(title))) {
                title = "";
            }

            return (StringAfter(bytes, ICON_HASH), title);
        }
        catch (Exception e) {
            Logger.Debug("Prompt text of template {0} could not be read: {1}", Logger.Args(templateId, e.Message));

            return ("", "");
        }
    }

    // The first non-empty string property with this hash. A property is: its size in bits (u32, the size and hash
    // included), its hash (u32), then the string (one length byte, the length doubled, and the characters). The size
    // must agree with the string's, so stray bytes that happen to look like the hash are skipped.
    private static string StringAfter(byte[] bytes, uint hash) {
        for (var at = 4; at + 5 <= bytes.Length; at++) {
            if (BitConverter.ToUInt32(bytes, at) != hash) {
                continue;
            }

            var lengthByte = bytes[at + 4];
            if ((lengthByte & 1) != 0) {
                continue;
            }

            var length = lengthByte >> 1;
            var bits = BitConverter.ToUInt32(bytes, at - 4);
            if (length == 0 || at + 5 + length > bytes.Length || bits != (uint) (8 + 1 + length) * 8) {
                continue;
            }

            var text = Encoding.ASCII.GetString(bytes, at + 5, length);
            if (text.AsSpan().IndexOfAnyExceptInRange(' ', '~') < 0) {
                return text;
            }
        }

        return "";
    }

}
