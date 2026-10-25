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
 * var look = MagicMirror.FromRequest(request, wizard.MirrorLook, wizard.WizardAvatar, out var notes);
 * var message = MagicMirror.ChangeMessage(wizard.GameObjectID, look);
 *
 * NOTE:
 * - The mirror NPC's MagicMirrorShopBehaviorTemplate lists its options (m_shopOptions). Each option is an item template
 *   (ObjectData/MagicMirrorShopOptions/...) with a VisualAttributeBehaviorTemplate saying what part it changes.
 * - The window sends the option template ids it picked and 0 for each part it left alone (seen in Niduus's log,
 *   2026-10-24). An option id becomes its look number from the game's avatar table (HumanTemplate.xml in Root.wad):
 *   its file's place in its part's list (face paint and glasses count from 1, 0 is none; a hair color is its place
 *   in the one list of every style's colors). Eyebrow and lip colors have no file and use their mirror order, which
 *   is the color order. A part left alone keeps what the wizard has now. Every request is logged, and
 *   magic-mirror-data.txt (next to the server) lists the options and their numbers.
 * - The look is written into WizardCharacterBehavior the way the game's own MSG_MAGICMIRRORCHANGEPLAYER handler does
 *   it (read from WizardGraphicalClient.exe, 2026-10-25), so character select and every zone draw it:
 *   hair style m_nHairModel, hair color m_nHairColor (low 7 bits) + m_extendedHairColor (the rest), skin
 *   m_nSkinColor, face paint m_nSkinDecal2, eye color m_newPlayerOptions2 bits 0-4, and in m_newPlayerOptions: nose
 *   bits 0-2, mouth 3-8, face 9-14, accessory 15-21, eyebrow color 22-26, lip color 27-31.
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/25/2026
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.Bit;
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
/// <param name="IndexFrom">How the number was found: "avatar part" (the avatar table) or "mirror order".</param>
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

    /// <summary>Wizard.MirrorLookVersion of looks with the game's own hair color numbers.</summary>
    internal const int LOOK_VERSION = 3;

    /// <summary>
    /// Fixes a saved mirror look from an older version: v0.10.237's numbers were wrong (every part left alone became 0)
    /// and it is dropped; v0.10.256 counted a hair color among its style's colors, so its hair color goes back to the
    /// avatar's. True when the wizard needs saving.
    /// </summary>
    internal static bool UpgradeOldLook(WizardData.Models.Player.Wizard wizard) {
        if (wizard?.MirrorLook is null || wizard.MirrorLookVersion >= LOOK_VERSION) {
            return false;
        }

        if (wizard.MirrorLookVersion < 2) {
            wizard.MirrorLook = null;
        }
        else {
            wizard.MirrorLook = Normalize(wizard.MirrorLook);
            wizard.MirrorLook[1] = UNCHANGED;
            wizard.MirrorLook = Current(wizard.MirrorLook, wizard.WizardAvatar);
            FitHairColor(wizard.MirrorLook, wizard.WizardAvatar);
        }

        wizard.MirrorLookVersion = LOOK_VERSION;

        return true;
    }

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
    private static CharacterRaceTable s_race;
    private static bool s_raceRead;
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
                var race = ReadAvatarTable();
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

                    var fromAvatar = IndexInAvatarTable(race, visual.m_visualAttributeType, gender, name, source);
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
    internal static int[] FromRequest(WIZARD2_53_PROTOCOL.MSG_MAGICMIRRORREQUESTCHANGE request, int[] saved,
        WizardCharacterBehavior avatar, out string notes) {
        uint[] asked = [
            request.HairStyleID, request.HairColorID, request.SkinColorID, request.FaceStyleID, unchecked((uint) request.FacePaintID),
            request.EyeColorID, request.NoseStyleID, request.MouthStyleID, request.AccessoryStyleID, request.EyeBrowColorID,
            request.LipColorID
        ];

        // The window sends 0 for every part that was not changed (Niduus's log, 2026-10-24); those keep the look the
        // wizard has now.
        var look = Current(saved, avatar);
        var text = new StringBuilder();
        for (var i = 0; i < Parts.Length; i++) {
            var value = asked[i];
            string how;
            if (value is 0 or uint.MaxValue) {
                how = $"unchanged ({look[i]})";
            }
            else if (OptionOf(value) is { } option) {
                look[i] = option.Index;
                how = $"option {option.Name} -> {option.Index} ({option.IndexFrom})";
            }
            else {
                how = $"{value} is no mirror option, unchanged ({look[i]})";
            }

            text.Append(i == 0 ? "" : "; ").Append(Parts[i]).Append(' ').Append(how);
        }

        FitHairColor(look, avatar, text);
        notes = text.ToString();

        return look;
    }

    /// <summary>
    /// Keeps the hair color on the hair style: the game draws the hair color number alone, and each style has its own
    /// colors, so after a new style the color moves to the same color of the new style.
    /// </summary>
    private static void FitHairColor(int[] look, WizardCharacterBehavior avatar, StringBuilder notes = null) {
        if (look[0] < 0 || look[1] < 0 || avatar is null) {
            return;
        }

        var colors = TableOf(avatar.m_eGender)?.m_hairTextureList;
        var styles = TableOf(avatar.m_eGender)?.m_hairElementList;
        if (colors is null || styles is null || look[0] >= styles.Count || look[1] >= colors.Count) {
            return;
        }

        var style = styles[look[0]]?.m_id;
        var oldStyle = colors[look[1]]?.m_id;
        if (style is null || oldStyle is null || style == oldStyle) {
            return;
        }

        var place = colors.Take(look[1]).Count(e => e?.m_id == oldStyle);
        var ofStyle = Enumerable.Range(0, colors.Count).Where(i => colors[i]?.m_id == style).ToList();
        if (ofStyle.Count == 0) {
            return;
        }

        var fitted = ofStyle[Math.Min(place, ofStyle.Count - 1)];
        notes?.Append($"; hair color {look[1]} belongs to another style, now {fitted}");
        look[1] = fitted;
    }

    /// <summary>The look a wizard has now: the saved mirror look, and for parts it never changed, what its avatar says.</summary>
    internal static int[] Current(int[] saved, WizardCharacterBehavior avatar) {
        var look = Normalize(saved);
        if (avatar is null) {
            return look;
        }

        var fromAvatar = FromAvatar(avatar);
        for (var i = 0; i < look.Length; i++) {
            if (look[i] == UNCHANGED) {
                look[i] = fromAvatar[i];
            }
        }

        return look;
    }

    /// <summary>The look stored in an avatar (see the note at the top for where each part sits).</summary>
    internal static int[] FromAvatar(WizardCharacterBehavior a) {
        var options = a.m_newPlayerOptions;

        return [
            a.m_nHairModel, a.m_nHairColor + (a.m_extendedHairColor << 7), a.m_nSkinColor, (int) ((options >> 9) & 0x3f),
            a.m_nSkinDecal2, (int) (a.m_newPlayerOptions2 & 0x1f), (int) (options & 7), (int) ((options >> 3) & 0x3f),
            (int) ((options >> 15) & 0x7f), (int) ((options >> 22) & 0x1f), (int) (options >> 27)
        ];
    }

    /// <summary>
    /// Writes a look into an avatar the way the game does when it gets MSG_MAGICMIRRORCHANGEPLAYER (-1 parts stay), so
    /// character select and new zones draw it. True when the avatar changed.
    /// </summary>
    internal static bool WriteToAvatar(WizardCharacterBehavior a, int[] look) {
        if (a is null) {
            return false;
        }

        look = Normalize(look);
        var before = FromAvatar(a);
        if (look[0] >= 0) {
            a.m_nHairModel = (Bui4) (byte) Math.Min(look[0], 15);
        }

        if (look[1] >= 0) {
            a.m_nHairColor = (Bui7) (byte) (look[1] & 0x7f);
            a.m_extendedHairColor = (byte) (look[1] >> 7);
        }

        if (look[2] >= 0) {
            a.m_nSkinColor = (Bui4) (byte) Math.Min(look[2], 15);
        }

        if (look[4] >= 0) {
            a.m_nSkinDecal2 = (ushort) look[4];
        }

        if (look[5] >= 0) {
            a.m_newPlayerOptions2 = (a.m_newPlayerOptions2 & ~0x1fu) | ((uint) look[5] & 0x1f);
        }

        var options = a.m_newPlayerOptions;
        options = Pack(options, look[6], 0, 0x7);
        options = Pack(options, look[7], 3, 0x3f);
        options = Pack(options, look[3], 9, 0x3f);
        options = Pack(options, look[8], 15, 0x7f);
        options = Pack(options, look[9], 22, 0x1f);
        options = Pack(options, look[10], 27, 0x1f);
        a.m_newPlayerOptions = options;

        return !before.SequenceEqual(FromAvatar(a));
    }

    private static uint Pack(uint options, int value, int shift, uint mask)
        => value < 0 ? options : (options & ~(mask << shift)) | (((uint) value & mask) << shift);

    /// <summary>The avatar's look fields, for the log (to learn how the newer parts are packed).</summary>
    internal static string DescribeAvatar(WizardCharacterBehavior a)
        => a is null ? "no avatar" : $"gender {a.m_eGender}, hair {(int) a.m_nHairModel}, hair color {(int) a.m_nHairColor} " +
            $"(extended {a.m_extendedHairColor}), skin {(int) a.m_nSkinColor}, skin decal {(int) a.m_nSkinDecal} (extended " +
            $"{a.m_extendedSkinDecal}), skin decal 2 {a.m_nSkinDecal2}, new options 0x{a.m_newPlayerOptions:X8}, new options 2 " +
            $"0x{a.m_newPlayerOptions2:X8}";

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
                text.AppendLine("Avatar table (HumanTemplate.xml):");
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

    /// <summary>
    /// The game's avatar table (HumanTemplate.xml in Root.wad, a CharacterRaceTable): per gender, the lists the look
    /// numbers point into. Every option's file is looked up in its part's list.
    /// </summary>
    private static CharacterRaceTable ReadAvatarTable() {
        s_avatarReport = [];
        try {
            var race = RootArchiveLoader.GetFile<CharacterRaceTable>("HumanTemplate.xml");
            s_race = race;
            s_raceRead = true;
            foreach (var table in race?.m_characterTables ?? []) {
                s_avatarReport.Add($"{table.m_sGender}: hair {table.m_hairElementList?.Count}, hair colors {table.m_hairTextureList?.Count}, " +
                    $"skin {table.m_skinTextureList?.Count}, face paint {table.m_skinDecalList2?.Count}, faces {table.m_faceElementList?.Count}, " +
                    $"eye colors {table.m_eyeColorTextureList?.Count}, noses {table.m_noseElementList?.Count}, mouths " +
                    $"{table.m_mouthElementList?.Count}, accessories {table.m_accessoryElementList?.Count}");
            }

            if (race is null) {
                s_avatarReport.Add("HumanTemplate.xml could not be read.");
            }

            return race;
        }
        catch (Exception ex) {
            s_avatarReport.Add("Reading HumanTemplate.xml failed: " + ex.Message);

            return null;
        }
    }

    /// <summary>The avatar table of a gender (read once), or null.</summary>
    private static CharacterElementTable TableOf(eGender gender) {
        lock (s_lock) {
            if (!s_raceRead) {
                ReadAvatarTable();
                s_raceRead = true;
            }

            return s_race?.m_characterTables?.FirstOrDefault(t => (t.m_sGender.ToString() ?? "")
                .Equals(gender == eGender.Female ? "Female" : "Male", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>The look number of an option, from the avatar table, or null when its file is not found there.</summary>
    private static int? IndexInAvatarTable(CharacterRaceTable race, VisualAttributeType type, string gender, string name, string source) {
        // Options without a gender (eye colors, glasses) are the same for both; their files sit in the boy folders.
        var table = race?.m_characterTables?.FirstOrDefault(t => (t.m_sGender.ToString() ?? "")
            .Equals(gender == "Girl" ? "Female" : "Male", StringComparison.OrdinalIgnoreCase));
        if (table is null) {
            return null;
        }

        var stem = Stem(source);
        switch (type) {
            case VisualAttributeType.VAT_HairStyle:
                return Find(table.m_hairElementList, stem, byGeometry: false);

            case VisualAttributeType.VAT_HairColor:
                // The hair colors are one list for every style (each color's m_id is its style's); the game draws the
                // color by its place in that list (his boy: hair style 8 = Shredder, hair color 80 = Shredder Black).
                return Find(table.m_hairTextureList, stem, byGeometry: false);

            case VisualAttributeType.VAT_SkinColor:
                return Find(table.m_skinTextureList, stem, byGeometry: false);

            case VisualAttributeType.VAT_FaceStyle:
            case VisualAttributeType.VAT_NewFaceStyle:
                return FaceIndex(table, name) ?? Find(table.m_faceElementList, stem, byGeometry: true);

            case VisualAttributeType.VAT_FacePaint:
                // 0 is no face paint; the paints count from 1 (Niduus, 2026-10-24: number 3 showed Balance, the
                // girl list's third paint).
                return stem.Length == 0 ? 0 : Find(table.m_skinDecalList2, stem, byGeometry: false) + 1;

            case VisualAttributeType.VAT_EyeColor:
            case VisualAttributeType.VAT_NewEyeColor:
                return Find(table.m_eyeColorTextureList, stem, byGeometry: false);

            case VisualAttributeType.VAT_NoseStyle:
                return Find(table.m_noseElementList, stem, byGeometry: true);

            case VisualAttributeType.VAT_MouthStyle:
                return Find(table.m_mouthElementList, stem, byGeometry: false);

            case VisualAttributeType.VAT_AccessoryStyle:
                // Like face paint: 0 is none.
                return stem.Length == 0 ? 0 : Find(table.m_accessoryElementList, stem, byGeometry: false) + 1;

            default:
                // Eyebrow and lip colors have no file; their mirror order is the game's color order.
                return null;
        }
    }

    /// <summary>
    /// A face's number from its name (P_G_Headhands_Droop is Droop): the face textures (m_skinDecalList) sit in a folder
    /// per face and their m_id is face * 100 + color. The face meshes cannot tell, as several faces share a mesh and the
    /// girl list carries the boy geometry names.
    /// </summary>
    private static int? FaceIndex(CharacterElementTable table, string name) {
        var at = name.LastIndexOf("Headhands_", StringComparison.OrdinalIgnoreCase);
        if (at < 0) {
            return null;
        }

        var folder = "/" + name[(at + "Headhands_".Length)..].Trim() + "/";
        var texture = table.m_skinDecalList?.FirstOrDefault(e =>
            (e?.m_assetFilename.ToString() ?? "").Replace('\\', '/').Contains(folder, StringComparison.OrdinalIgnoreCase));

        return texture is null ? null : texture.m_id / 100;
    }

    /// <summary>
    /// The place of the element whose file (or, first when <paramref name="byGeometry"/>, whose geometry name) is the
    /// stem. Faces and noses share files between entries, so they are told apart by geometry.
    /// </summary>
    private static int? Find(List<CharacterElement> list, string stem, bool byGeometry) {
        if (list is null || stem.Length == 0) {
            return null;
        }

        if (byGeometry) {
            var byName = list.FindIndex(e => Stem(e?.m_geometryName.ToString()) == stem);
            if (byName >= 0) {
                return byName;
            }
        }

        var byFile = list.FindIndex(e => Stem(e?.m_assetFilename.ToString()) == stem);

        return byFile >= 0 ? byFile : null;
    }

}
