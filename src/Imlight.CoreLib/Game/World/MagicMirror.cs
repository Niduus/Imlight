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
 *
 * ========================================================================
 * MAGIC MIRROR
 * ========================================================================
 *
 * PURPOSE:
 * The Magic Mirror shop (Wizard City Magic Mirror Shop, NPC WC-ShopArea-MagicMirror 1439031): the shop's look options,
 * turning what the game asks for (MSG_MAGICMIRRORREQUESTCHANGE) into the look numbers it shows
 * (MSG_MAGICMIRRORCHANGEPLAYER), and each wizard's saved mirror look (Wizard.MirrorLook).
 *
 * USAGE EXAMPLE:
 * var look = MagicMirror.FromRequest(request, wizard.MirrorLook, out var notes);
 * var message = MagicMirror.ChangeMessage(wizard.GameObjectID, look);
 *
 * NOTE:
 * - The mirror NPC's MagicMirrorShopBehaviorTemplate lists its options (m_shopOptions). Each option is an item template
 *   (ObjectData/MagicMirrorShopOptions/...) with a VisualAttributeBehaviorTemplate saying what part it changes.
 * - Not checked in the game yet: whether the request carries those option template ids (the message names them "ID")
 *   or look numbers already. An option id becomes its number: its place in the avatar part lists of Root.wad when its
 *   file is found there, otherwise its place among the mirror's options of the same part and gender. Anything else is
 *   used as it is. Every request is logged with both, and magic-mirror-data.txt (next to the server) lists the
 *   options and the avatar parts, so the numbers can be fixed from real data.
 * - The new look is not written into WizardCharacterBehavior (how its newer packed fields hold eyes, nose and mouth is
 *   not known), so it is sent again each time the wizard enters a zone. Character select shows the old look.
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/24/2026
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.World;

/// <summary>One look option of the Magic Mirror shop.</summary>
/// <param name="Id">The option's item template.</param>
/// <param name="Name">Its object name, such as P_G_Hair_Bigpony.</param>
/// <param name="Type">The part it changes.</param>
/// <param name="Gender">"Girl", "Boy" or "" from its name or folder.</param>
/// <param name="Index">The look number it becomes.</param>
/// <param name="IndexFrom">How the number was found: "avatar part" or "mirror order".</param>
internal sealed record MagicMirrorOption(uint Id, string Name, VisualAttributeType Type, string Gender, string Source,
    string Reference, int Index, string IndexFrom);

internal static class MagicMirror {

    /// <summary>The Wizard City Magic Mirror Shop.</summary>
    internal const string SHOP_ZONE = "WizardCity/Interiors/WC_Shop_Makeup";

    /// <summary>What the server learned about the mirror, written the first time a mirror opens (and by .mirror data).</summary>
    internal const string DATA_FILE = "magic-mirror-data.txt";

    /// <summary>The look parts, in the order of MSG_MAGICMIRRORCHANGEPLAYER and of Wizard.MirrorLook.</summary>
    internal static readonly string[] Parts = [
        "hair style", "hair color", "skin color", "face", "face paint", "eye color", "nose", "mouth", "accessory",
        "eyebrow color", "lip color"
    ];

    /// <summary>A part that is not changed.</summary>
    internal const int UNCHANGED = -1;

    // The parts of each look slot, by VisualAttributeType (old and new kinds of the same part share a slot).
    private static readonly VisualAttributeType[][] s_slotTypes = [
        [VisualAttributeType.VAT_HairStyle],
        [VisualAttributeType.VAT_HairColor],
        [VisualAttributeType.VAT_SkinColor],
        [VisualAttributeType.VAT_FaceStyle, VisualAttributeType.VAT_NewFaceStyle],
        [VisualAttributeType.VAT_FacePaint],
        [VisualAttributeType.VAT_EyeColor, VisualAttributeType.VAT_NewEyeColor],
        [VisualAttributeType.VAT_NoseStyle],
        [VisualAttributeType.VAT_MouthStyle],
        [VisualAttributeType.VAT_AccessoryStyle],
        [VisualAttributeType.VAT_EyebrowStyle],
        [VisualAttributeType.VAT_LipColorStyle],
    ];

    private static readonly object s_lock = new();
    private static Dictionary<uint, MagicMirrorOption> s_options;
    private static uint s_optionsFrom;
    private static List<string> s_avatarReport = [];
    private static bool s_dataWritten;

    /// <summary>Whether a template is a Magic Mirror shop NPC.</summary>
    internal static bool IsMirror(CoreTemplate template)
        => template is GameObjectTemplate goTemplate
        && goTemplate.m_behaviors is { } behaviors
        && behaviors.Any(b => b is MagicMirrorShopBehaviorTemplate);

    /// <summary>
    /// Reads the options of a mirror NPC once (every mirror in the game shares them) and writes magic-mirror-data.txt
    /// the first time.
    /// </summary>
    internal static void Load(CoreTemplate mirror) {
        lock (s_lock) {
            if (s_options is not null) {
                return;
            }

            s_options = [];
            s_optionsFrom = (mirror as GameObjectTemplate)?.m_templateID ?? 0;
            try {
                var behavior = (mirror as GameObjectTemplate)?.m_behaviors?.OfType<MagicMirrorShopBehaviorTemplate>().FirstOrDefault();
                var avatarParts = ReadAvatarParts();
                var order = new Dictionary<string, int>();
                foreach (var id in behavior?.m_shopOptions ?? []) {
                    if (CustomNpcs.TemplateOf(id) is not GameObjectTemplate option) {
                        continue;
                    }

                    var visual = option.m_behaviors?.OfType<VisualAttributeBehaviorTemplate>().FirstOrDefault();
                    if (visual is null) {
                        continue;
                    }

                    var name = option.m_objectName.ToString() ?? "";
                    var gender = GenderOf(name + " " + (CustomNpcs.FileOf(id) ?? ""));
                    var source = visual.m_sourceFile.ToString() ?? "";
                    var reference = visual.m_referenceFile.ToString() ?? "";
                    var slot = SlotOf(visual.m_visualAttributeType);
                    var key = $"{slot}|{gender}";
                    order.TryGetValue(key, out var place);
                    order[key] = place + 1;

                    var fromAvatar = FindInAvatarParts(avatarParts, gender, source, reference);
                    s_options[id] = new MagicMirrorOption(id, name, visual.m_visualAttributeType, gender, source, reference,
                        fromAvatar ?? place, fromAvatar is null ? "mirror order" : "avatar part");
                }

                Logger.Information("Magic Mirror | {0} look options read from mirror {1} ({2} placed by the avatar parts).",
                    Logger.Args(s_options.Count, s_optionsFrom, s_options.Values.Count(o => o.IndexFrom == "avatar part")));
            }
            catch (Exception ex) {
                Logger.Error("Magic Mirror | Reading the mirror's options failed. {0}", Logger.Args(ex));
            }
        }

        if (!s_dataWritten) {
            WriteData();
        }
    }

    /// <summary>The option of a template id, or null.</summary>
    internal static MagicMirrorOption OptionOf(uint id) {
        lock (s_lock) {
            return s_options is not null && s_options.TryGetValue(id, out var option) ? option : null;
        }
    }

    /// <summary>
    /// The look the game asked for, on top of the wizard's saved look. <paramref name="notes"/> says, per part, what came
    /// in and what it became (for the log).
    /// </summary>
    internal static int[] FromRequest(WIZARD2_53_PROTOCOL.MSG_MAGICMIRRORREQUESTCHANGE request, int[] saved, out string notes) {
        uint[] asked = [
            request.HairStyleID, request.HairColorID, request.SkinColorID, request.FaceStyleID, unchecked((uint) request.FacePaintID),
            request.EyeColorID, request.NoseStyleID, request.MouthStyleID, request.AccessoryStyleID, request.EyeBrowColorID,
            request.LipColorID
        ];

        var look = Normalize(saved);
        var text = new StringBuilder();
        for (var i = 0; i < Parts.Length; i++) {
            var value = asked[i];
            string how;
            if (value == uint.MaxValue) {
                how = "unchanged";
            }
            else if (OptionOf(value) is { } option) {
                look[i] = option.Index;
                how = $"option {option.Name} -> {option.Index} ({option.IndexFrom})";
            }
            else {
                look[i] = (int) value;
                how = $"{value} as it is";
            }

            text.Append(i == 0 ? "" : "; ").Append(Parts[i]).Append(' ').Append(how);
        }

        notes = text.ToString();

        return look;
    }

    /// <summary>The message that shows a look on a wizard.</summary>
    internal static WIZARD2_53_PROTOCOL.MSG_MAGICMIRRORCHANGEPLAYER ChangeMessage(ulong gameObjectId, int[] look) {
        look = Normalize(look);

        return new WIZARD2_53_PROTOCOL.MSG_MAGICMIRRORCHANGEPLAYER {
            GlobalID = gameObjectId,
            HairStyleIndex = look[0],
            HairColorIndex = look[1],
            SkinColorIndex = look[2],
            FaceStyleIndex = look[3],
            FacePaintIndex = look[4],
            EyeColorIndex = look[5],
            NoseStyleIndex = look[6],
            MouthStyleIndex = look[7],
            AccessoryStyleIndex = look[8],
            EyeBrowColorIndex = look[9],
            LipColorIndex = look[10],
        };
    }

    /// <summary>A look with every part (missing parts unchanged).</summary>
    internal static int[] Normalize(int[] look) {
        var full = Enumerable.Repeat(UNCHANGED, Parts.Length).ToArray();
        if (look is not null) {
            Array.Copy(look, full, Math.Min(look.Length, full.Length));
        }

        return full;
    }

    /// <summary>A look for the chat, such as "hair style 3, hair color 1, ...".</summary>
    internal static string Describe(int[] look) {
        look = Normalize(look);
        var parts = Enumerable.Range(0, Parts.Length).Where(i => look[i] != UNCHANGED).Select(i => $"{Parts[i]} {look[i]}").ToList();

        return parts.Count == 0 ? "nothing changed" : string.Join(", ", parts);
    }

    /// <summary>Writes magic-mirror-data.txt next to the server: the options and the avatar parts. Returns its path.</summary>
    internal static string WriteData() {
        try {
            var text = new StringBuilder();
            text.AppendLine($"Magic Mirror data, written by the server {DateTime.Now:yyyy-MM-dd HH:mm}.");
            text.AppendLine("Look parts in order: " + string.Join(", ", Parts));
            text.AppendLine();
            lock (s_lock) {
                text.AppendLine($"Options of mirror {s_optionsFrom} ({s_options?.Count ?? 0}):");
                text.AppendLine("Id\tName\tType\tGender\tNumber\tFrom\tSource file\tReference file");
                foreach (var o in (s_options?.Values ?? Enumerable.Empty<MagicMirrorOption>()).OrderBy(o => o.Type).ThenBy(o => o.Gender).ThenBy(o => o.Index)) {
                    text.AppendLine($"{o.Id}\t{o.Name}\t{o.Type}\t{o.Gender}\t{o.Index}\t{o.IndexFrom}\t{o.Source}\t{o.Reference}");
                }

                text.AppendLine();
                text.AppendLine("Avatar parts found in Root.wad:");
                foreach (var line in s_avatarReport) {
                    text.AppendLine(line);
                }
            }

            File.WriteAllText(DATA_FILE, text.ToString());
            s_dataWritten = true;

            return Path.GetFullPath(DATA_FILE);
        }
        catch (Exception ex) {
            Logger.Error("Magic Mirror | Writing {0} failed. {1}", Logger.Args(DATA_FILE, ex.Message));

            return null;
        }
    }

    private static int SlotOf(VisualAttributeType type) {
        for (var i = 0; i < s_slotTypes.Length; i++) {
            if (s_slotTypes[i].Contains(type)) {
                return i;
            }
        }

        return 100 + (int) type; // Gear colors (BoyColor and the rest) have no look slot.
    }

    private static string GenderOf(string text) {
        if (text.Contains("_G_", StringComparison.Ordinal) || text.Contains("Girl", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Female", StringComparison.OrdinalIgnoreCase)) {
            return "Girl";
        }

        if (text.Contains("_B_", StringComparison.Ordinal) || text.Contains("Boy", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Male", StringComparison.OrdinalIgnoreCase)) {
            return "Boy";
        }

        return "";
    }

    private static string Stem(string file) {
        if (string.IsNullOrWhiteSpace(file)) {
            return "";
        }

        var name = file.Replace('|', '/').Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');

        return (dot > 0 ? name[..dot] : name).Trim().ToLowerInvariant();
    }

    // One avatar part list: the file it came from, the part, and the file names of each of its options.
    private sealed record AvatarPart(string File, string Gender, string Part, List<HashSet<string>> Options, List<HashSet<string>> Textures);

    /// <summary>Every AvatarItemInfoList in Root.wad (files with "avatar" in the name), for placing the options.</summary>
    private static List<AvatarPart> ReadAvatarParts() {
        var parts = new List<AvatarPart>();
        s_avatarReport = [];
        try {
            var names = RootArchiveLoader.GetRootWad()?.Files.Keys
                .Where(n => n.Contains("avatar", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .Take(300)
                .ToList() ?? [];
            foreach (var name in names) {
                PropertyClass file;
                try {
                    file = RootArchiveLoader.GetFile<PropertyClass>(name);
                }
                catch {
                    continue;
                }

                if (file is not AvatarItemInfoList list) {
                    s_avatarReport.Add($"{name}: {file?.GetType().Name ?? "not readable"}");

                    continue;
                }

                var gender = GenderOf(name);
                foreach (var info in list.m_itemInfoList?.OfType<AvatarItemInfo>() ?? []) {
                    var options = (info.m_options ?? []).Select(o => new HashSet<string> { Stem(o?.m_mesh.ToString()), Stem(o?.m_geometry.ToString()) }).ToList();
                    var textures = (info.m_textureOptions ?? []).Select(t => new HashSet<string>(
                        (t?.m_textures ?? []).Concat(t?.m_decals ?? []).Concat(t?.m_decals2 ?? []).Select(s => Stem(s.ToString()))
                            .Append(Stem(t?.m_materialName.ToString())))).ToList();
                    var part = new AvatarPart(name, gender, info.m_partName.ToString() ?? "", options, textures);
                    parts.Add(part);
                    s_avatarReport.Add($"{name}\tpart {part.Part}\tslot {info.m_slotName}\t{options.Count} options\t{textures.Count} textures\t" +
                        $"first: {string.Join(",", options.Take(3).Select(o => string.Join("/", o.Where(s => s.Length > 0))))} | " +
                        $"{string.Join(",", textures.Take(3).Select(t => string.Join("/", t.Where(s => s.Length > 0).Take(2))))}");
                }
            }
        }
        catch (Exception ex) {
            s_avatarReport.Add("Reading the avatar parts failed: " + ex.Message);
        }

        return parts;
    }

    /// <summary>An option's place in an avatar part list whose option uses the same file, or null.</summary>
    private static int? FindInAvatarParts(List<AvatarPart> parts, string gender, string source, string reference) {
        var stems = new[] { Stem(source), Stem(reference) }.Where(s => s.Length > 0).ToArray();
        if (stems.Length == 0) {
            return null;
        }

        foreach (var part in parts.OrderBy(p => p.Gender == gender ? 0 : 1)) {
            if (gender.Length > 0 && part.Gender.Length > 0 && part.Gender != gender) {
                continue;
            }

            foreach (var list in new[] { part.Options, part.Textures }) {
                var index = list.FindIndex(o => stems.Any(o.Contains));
                if (index >= 0) {
                    return index;
                }
            }
        }

        return null;
    }

}
