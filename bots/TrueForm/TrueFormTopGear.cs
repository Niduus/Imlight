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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// The top level gear in the game's own files, for True Form (Niduus, 2026-11-19: "bot is still repping 180 gear ... curious
/// as to why not 190 gear"). The gear the server's other wizards pick from (BotGear.Catalog) is what a shop sells, a boss
/// drops or a quest gives in the drop and shop lists the server has, and those lists have nothing from Ahmarra yet, so the
/// level 190 gear was not in it and his best was level 180. This reads every item in the game's files once, in the
/// background, and keeps the gear for the top levels (level 180 and up); True Form picks from both. Kept in
/// logs/trueform/top-gear.txt so the next start does not read it all again (read again when the game's files change).
/// </summary>
internal static class TrueFormTopGear {

    private const string FILE = "top-gear.txt";
    private const int FROM_LEVEL = 180;

    private static volatile List<BotGear.Piece> s_pieces;
    private static volatile List<uint> s_jewels;
    private static volatile List<SetPiece> s_setPieces;
    private static volatile List<uint> s_mounts;

    /// <summary>A pet or mount that is part of a set (its set bonus template), from the game's files.</summary>
    internal sealed record SetPiece(uint Id, uint Set, bool Mount);
    private static int s_started;

    /// <summary>The top level gear, or null while it is still being read (the first ask starts it).</summary>
    internal static IReadOnlyList<BotGear.Piece> Pieces {
        get {
            if (s_pieces is { } ready) {
                return ready;
            }

            if (Interlocked.Exchange(ref s_started, 1) == 0) {
                // On a low priority thread, so the first read (every item in the game's files) does not slow the server's
                // launch (Niduus, 2026-11-19: the first start after v0.10.926 "took longer than usual"). Later starts read the
                // kept list in logs/trueform/top-gear.txt and are quick.
                new Thread(Read) { IsBackground = true, Priority = ThreadPriority.Lowest, Name = "True Form top gear" }.Start();
            }

            return null;
        }
    }

    /// <summary>
    /// Every jewel and pin in the game's files (Niduus, 2026-11-19: "max HP for tear is 155"; the +155 tear is not in the
    /// server's drop and shop lists), or null while they are still being read. BotJewels takes them in.
    /// </summary>
    internal static IReadOnlyList<uint> JewelIds {
        get {
            _ = Pieces;
            return s_jewels;
        }
    }

    /// <summary>
    /// Every pet and mount in the game's files that is part of a set, or null while they are still being read (Niduus,
    /// 2026-11-19: "the fossil pet and mount for the + damage for having 2 equipped").
    /// </summary>
    internal static IReadOnlyList<SetPiece> SetPieces {
        get {
            _ = Pieces;
            return s_setPieces;
        }
    }

    /// <summary>
    /// Every mount in the game's files, set or not, or null while they are still being read (Niduus, 2026-11-19: "Now TF is
    /// missing a mount": with rentals and weak set mounts off he had none left).
    /// </summary>
    internal static IReadOnlyList<uint> MountIds {
        get {
            _ = Pieces;
            return s_mounts;
        }
    }

    private static string CachePath => Path.Combine(UltimusSeat.TrueForm.Folder, FILE);

    private static void Read() {
        try {
            var templates = CoreObjectFactory.TemplateManifest?.m_serializedTemplates;
            if (templates is null) {
                s_jewels = [];
                s_mounts = [];
                s_setPieces = [];
                s_pieces = [];
                return;
            }

            var stamp = $"templates {templates.Count} with jewels, sets and mounts";
            List<uint> ids = null;
            List<uint> jewels = [];
            List<SetPiece> sets = [];
            List<uint> mounts = [];
            try {
                if (File.Exists(CachePath) && File.ReadAllLines(CachePath) is { Length: > 0 } lines && lines[0] == stamp) {
                    var kept = lines.Skip(1).Select(l => l.Split(' ')).Where(p => !(p.Length > 1 && p[1] is "set" or "mount")).Select(p => (Id: uint.TryParse(p[0], out var id) ? id : 0, Jewel: p.Length > 1 && p[1] == "jewel"))
                        .Where(x => x.Id != 0).ToList();
                    ids = kept.Where(x => !x.Jewel).Select(x => x.Id).ToList();
                    jewels = kept.Where(x => x.Jewel).Select(x => x.Id).ToList();
                    sets = lines.Skip(1).Select(l => l.Split(' '))
                        .Where(p => p.Length >= 4 && p[1] == "set" && uint.TryParse(p[0], out _) && uint.TryParse(p[2], out _))
                        .Select(p => new SetPiece(uint.Parse(p[0]), uint.Parse(p[2]), p[3] == "mount")).ToList();
                    mounts = lines.Skip(1).Select(l => l.Split(' ')).Where(p => p.Length == 2 && p[1] == "mount" && uint.TryParse(p[0], out _))
                        .Select(p => uint.Parse(p[0])).ToList();
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                ids = null;
            }

            var started = DateTime.UtcNow;
            if (ids is null) {
                ids = [];
                foreach (var location in templates) {
                    if (location is null || location.m_id > uint.MaxValue) {
                        continue;
                    }

                    try {
                        // Read without keeping it: only the few top level pieces are kept (BotGear.Read below).
                        if (CoreObjectFactory.LoadTemplateFile(location.m_filename) is WizItemTemplate template) {
                            var kind = SetKind((uint) location.m_id, template);
                            if (template.m_itemSetBonusTemplateID != 0 && kind is { } mount) {
                                sets.Add(new SetPiece((uint) location.m_id, (uint) template.m_itemSetBonusTemplateID, mount));
                            }

                            if (kind == true) {
                                mounts.Add((uint) location.m_id);
                            }

                            if (JewelSockets.IsJewel(template)) {
                                jewels.Add((uint) location.m_id);
                            }
                            else if (BotGear.LevelNeeded(template) >= FROM_LEVEL) {
                                ids.Add((uint) location.m_id);
                            }
                        }
                    }
                    catch (Exception) {
                        // Not an item, or a file the server cannot read.
                    }
                }

                try {
                    Directory.CreateDirectory(UltimusSeat.TrueForm.Folder);
                    File.WriteAllLines(CachePath, [stamp, .. ids.Select(id => id.ToString()), .. jewels.Select(id => $"{id} jewel"),
                        .. sets.Select(p => $"{p.Id} set {p.Set} {(p.Mount ? "mount" : "pet")}"), .. mounts.Select(id => $"{id} mount")]);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                    Logger.Debug("True Form: could not keep the top gear list: {0}", Logger.Args(e.Message));
                }
            }

            var pieces = ids.Select(BotGear.Read).Where(p => p is not null && !p.CrownsOnly && !p.Retired).ToList();
            s_jewels = jewels;
            s_setPieces = sets;
            s_mounts = mounts;
            s_pieces = pieces;
            Logger.Information("True Form: {0} pieces of level {1}+ gear in the game's files ({2}), read in {3:0.0} s.",
                Logger.Args(pieces.Count, FROM_LEVEL, string.Join(", ", pieces.GroupBy(p => p.Level).OrderBy(g => g.Key).Select(g => $"level {g.Key}: {g.Count()}")),
                    (DateTime.UtcNow - started).TotalSeconds));
        }
        catch (Exception e) {
            Logger.Warning("True Form: the game's top gear could not be read: {0}", Logger.Args(e.Message));
            s_jewels ??= [];
            s_setPieces ??= [];
            s_mounts ??= [];
            s_pieces = [];
        }
    }

    // Whether a set piece is a mount (true) or a pet (false); null for anything else.
    private static bool? SetKind(uint id, WizItemTemplate template) {
        if (Pet.PetFactory.GetPetItemBehaviorTemplate(id) is not null) {
            return false;
        }

        try {
            return ItemHelper.GetItemSlot(template)?.SlotType == WizardData.Models.Player.EquipmentSlotType.Mount ? true : null;
        }
        catch (InvalidOperationException) {
            return null;
        }
    }

}
