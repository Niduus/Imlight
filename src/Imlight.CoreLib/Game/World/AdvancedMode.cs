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
 * ADVANCED MODE (ADVANCED COMBAT)
 * ========================================================================
 *
 * PURPOSE:
 * The game's Advanced Mode (its newer menus call it Challenge Mode): a dungeon sigil with an Advanced version shows a
 * button that turns the pad red, and the team then goes into the harder copy of the dungeon instead. Each copy is its
 * own zone in the game files, named like the normal one with _Hard, _Adv or Adv on the end (Aquila's dungeons,
 * Castle Strigoi, the Celestial Pyramid and so on), with stronger Adv_ creatures that have their own stats, decks and
 * cheats in the game files.
 *
 * HOW IT WORKS HERE:
 * - [Instance] AdvancedMode (live, default on): pads of dungeons with an Advanced copy offer the game's Advanced Mode
 *   button (MinigameSigilOption.m_advancedModeFields). A press with the button on (RequestedSigilMode 2), a Team Up
 *   in Advanced Mode, or a wizard who typed `.advanced on` goes into the Advanced copy. The pad glows red (its "Red"
 *   state) for a wizard with `.advanced on`.
 * - Inside a copy, its doors and exits are the normal dungeon's (SpiralDB has none for the copies), each leading to
 *   the Advanced copy of where it goes when there is one, so the team stays in Advanced Mode until they leave.
 * - The copies' battle circles are a newer kind of sigil object (class 490043331, a Battleground sigil) the server
 *   could not read, so no fight started there: AdvancedCombatSigilObjectInfo reads them as ordinary combat circles.
 * - An Adv_ creature without drops, cheats or a deck of its own on this server uses its normal creature's.
 * - Custom fights (Niduus, 2026-10-29: "allow for custom fights to have the advanced mode"): a room of the Customs tab
 *   (Customs/Rooms.json, a zone with @variant for an added sigil's private copy) can have an Advanced version, the
 *   room's name with ~Advanced on the end, naming the customs its creatures are in Advanced Mode. A sigil leading to
 *   such a room offers the button, and Advanced goes into the same zone with the variant plus ~Advanced: a copy of
 *   its own, with the room's ZoneObjects files, whose spawners spawn the Advanced room's customs first and the room's
 *   for the creatures it does not name. This comes before the game's Advanced copy of the zone.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.World;

internal static partial class AdvancedMode {

    /// <summary>The game's RequestedSigilMode for a press with the Advanced Mode button on.</summary>
    internal const uint ADVANCED_SIGIL_MODE = 2;

    /// <summary>The game's RequestedSigilMode for a press with the Advanced Mode button off.</summary>
    internal const uint STANDARD_SIGIL_MODE = 1;

    /// <summary>[Instance] AdvancedMode: dungeon sigils offer Advanced Mode (live).</summary>
    internal static bool Enabled => ConfigurationManager.GetValue("Instance.AdvancedMode", true);

    // Every dungeon zone of the game with an Advanced copy, and the copy (Wizard404's list of the live game's zones,
    // spiraldb-extra/Zones/zone-details.json; the copies were checked in Niduus's game files on 2026-10-29, Ahmarra's are
    // not installed there yet).
    private static readonly (string Normal, string Advanced, string Name)[] s_zones = [
        ("Ahmarra/Interiors/AH_Z05I02_RuinedHermitage", "Ahmarra/Interiors/AH_Z05I02_RuinedHermitage_Hard", "Ruined Hermitage"),
        ("Ahmarra/Interiors/AH_Z08I06_ThunderCage", "Ahmarra/Interiors/AH_Z08I06_ThunderCage_Hard", "Thunder Cage"),
        ("Ahmarra/Interiors/AH_Z10I07_SkallofJudgement", "Ahmarra/Interiors/AH_Z10I07_SkallofJudgement_Hard", "Skall of Judgement"),
        ("Ahmarra/Interiors/AH_Z10I13_LegionOfTheDeep", "Ahmarra/Interiors/AH_Z10I13_LegionOfTheDeep_Hard", "Legion of the Deep"),
        ("Aquila/AQ_Z01_MountOlympus", "Aquila/AQ_Z01_MountOlympus_Hard", "Mount Olympus"),
        ("Aquila/AQ_Z02_Atlantea02", "Aquila/AQ_Z02_Atlantea02_Hard", "Atlantea"),
        ("Aquila/AQ_Z02_Atlantea", "Aquila/AQ_Z02_Atlantea_Hard", "Atlantea"),
        ("Aquila/AQ_Z03_Tartarus", "Aquila/AQ_Z03_Tartarus_Hard", "Tartarus"),
        ("Aquila/Interiors/AQ_Z01_Apollo_Room", "Aquila/Interiors/AQ_Z01_Apollo_Room_Hard", "Apollo's Room"),
        ("Aquila/Interiors/AQ_Z01_ArtemusRoom", "Aquila/Interiors/AQ_Z01_ArtemusRoom_Hard", "Artemis's Room"),
        ("Aquila/Interiors/AQ_Z01_HallOfBattle", "Aquila/Interiors/AQ_Z01_HallOfBattle_Hard", "Hall of Battle"),
        ("Aquila/Interiors/AQ_Z01_HallOfWatchfulEye", "Aquila/Interiors/AQ_Z01_HallOfWatchfulEye_Hard", "Hall of the Watchful Eye"),
        ("Aquila/Interiors/AQ_Z01_HephaestusForge", "Aquila/Interiors/AQ_Z01_HephaestusForge_Hard", "Hephaestus's Forge"),
        ("Aquila/Interiors/AQ_Z01_PitOfNoxii", "Aquila/Interiors/AQ_Z01_PitOfNoxii_Hard", "Pit of Noxii"),
        ("Aquila/Interiors/AQ_Z01_TempleOfSisyphus", "Aquila/Interiors/AQ_Z01_TempleOfSisyphus_Hard", "Temple of Sisyphus"),
        ("Aquila/Interiors/AQ_Z01_Throne_Room", "Aquila/Interiors/AQ_Z01_Throne_Room_Hard", "Zeus's Throne Room"),
        ("Aquila/Interiors/AQ_Z02_HallOfContriver", "Aquila/Interiors/AQ_Z02_HallOfContriver_Hard", "Hall of the Contriver"),
        ("Aquila/Interiors/AQ_Z02_HouseOfFury", "Aquila/Interiors/AQ_Z02_HouseOfFury_Hard", "House of Fury"),
        ("Aquila/Interiors/AQ_Z02_HouseOfLostSaliors", "Aquila/Interiors/AQ_Z02_HouseOfLostSaliors_Hard", "House of Lost Sailors"),
        ("Aquila/Interiors/AQ_Z02_PirateShip", "Aquila/Interiors/AQ_Z02_PirateShip_Hard", "Pirate Ship"),
        ("Aquila/Interiors/AQ_Z02_SirenPuzzleRoom", "Aquila/Interiors/AQ_Z02_SirenPuzzleRoom_Hard", "Sirens' Room"),
        ("Aquila/Interiors/AQ_Z02_ThroneRoom", "Aquila/Interiors/AQ_Z02_ThroneRoom_Hard", "Poseidon's Throne Room"),
        ("Aquila/Interiors/AQ_Z03_CavOfNyx", "Aquila/Interiors/AQ_Z03_CavOfNyx_Hard", "Cave of Nyx"),
        ("Aquila/Interiors/AQ_Z03_HallOfHades", "Aquila/Interiors/AQ_Z03_HallOfHades_Hard", "Hall of Hades"),
        ("Aquila/Interiors/AQ_Z03_HouseOfFaun", "Aquila/Interiors/AQ_Z03_HouseOfFaun_Hard", "House of the Faun"),
        ("Aquila/Interiors/AQ_Z03_TempleOfLonging", "Aquila/Interiors/AQ_Z03_TempleOfLonging_Hard", "Temple of Longing"),
        ("Darkmoor/Interiors/DM_Z01I04_GargoyleWatchtower", "Darkmoor/Interiors/DM_Z01I04_GargoyleWatchtower_Hard", "Gargoyle Watchhouse"),
        ("Darkmoor/Interiors/DM_Z01I13_SecretLab", "Darkmoor/Interiors/DM_Z01I13_SecretLab_Hard", "Secret Lab"),
        ("Darkmoor/Interiors/DM_Z01I17_SandersonHouse", "Darkmoor/Interiors/DM_Z01I17_SandersonHouse_Hard", "Sanderson House"),
        ("Darkmoor/Interiors/DM_Z02_Castle_A", "Darkmoor/Interiors/DM_Z02_Castle_A_Hard", "Castle Strigoi"),
        ("Darkmoor/Interiors/DM_Z03_OvergrownEstate", "Darkmoor/Interiors/DM_Z03_OvergrownEstate_Adv", "Overgrown Estate"),
        ("Darkmoor/Interiors/DM_Z04I08_BogOfVengeance", "Darkmoor/Interiors/DM_Z04I08_BogOfVengeanceAdv", "Bog of Vengeance"),
        ("Darkmoor/Interiors/DM_Z05_HereticalAcademy", "Darkmoor/Interiors/DM_Z05_HereticalAcademy_Adv", "Heretical Academy"),
        ("Empyrea/Interiors/EM_Z05_Brain", "Empyrea/Interiors/EM_Z05_Brain_Hard", "Headquarters"),
        ("Empyrea/Interiors/EM_Z16_I04_StormTitan", "Empyrea/Interiors/EM_Z16_I04_StormTitan_Hard", "Storm Titan Maelstrom"),
        ("Karamelle/Interiors/KM_Z00_TheCavity", "Karamelle/Interiors/KM_Z00_TheCavity_Adv", "Cavity Chamber"),
        ("Karamelle/Interiors/KM_Z08_RosinasHouse", "Karamelle/Interiors/KM_Z08_RosinasHouse_Adv", "Rosina's House"),
        ("Karamelle/Interiors/KM_Z10_ThroneRoom_B", "Karamelle/Interiors/KM_Z10_ThroneRoom_B_Adv", "Castle Gobbsmack"),
        ("Krokotopia/KT_Selenopolis/Interiors/KT_Z04_Pyramid", "Krokotopia/KT_Selenopolis/Interiors/KT_Z04_Pyramid_Adv", "Celestial Pyramid"),
        ("Krokotopia/KT_Selenopolis/Interiors/KT_Z04_Sanctum", "Krokotopia/KT_Selenopolis/Interiors/KT_Z04_Sanctum_Adv", "Sanctum"),
        ("Krokotopia/KT_Selenopolis/Interiors/KT_Z06I12_CryptOfRa_Int", "Krokotopia/KT_Selenopolis/Interiors/KT_Z06I12_CryptOfRa_Int_Hard", "Crypt of Ra"),
        ("Krokotopia/KT_Selenopolis/Interiors/KT_Z07I03_DarkAbode", "Krokotopia/KT_Selenopolis/Interiors/KT_Z07I03_DarkAbode_Hard", "Dark Abode"),
        ("Lemuria/Interiors/LM_Z00_I08_HallofHeroesInner", "Lemuria/Interiors/LM_Z00_I08_HallofHeroesInner_Hard", "Hall of Heroes"),
        ("Novus/Interiors/NV_Z07_I01_EngineeringWing", "Novus/Interiors/NV_Z07_I01_EngineeringWing_Hard", "Engineering Wing"),
        ("Novus/Interiors/NV_Z07_I04_NovusSkies", "Novus/Interiors/NV_Z07_I04_NovusSkies_Hard", "Battle the Invading Fleet"),
        ("Wallaru/Interiors/WL_Z04_I05_FreddiesDomain", "Wallaru/Interiors/WL_Z04_I05_FreddiesDomain_Hard", "Freddie's Domain"),
        ("Wallaru/Interiors/WL_Z05_BanditCootCave", "Wallaru/Interiors/WL_Z05_BanditCootCave_Hard", "Crash's Cave"),
    ];

    private static readonly Dictionary<string, string> s_advancedOf
        = s_zones.ToDictionary(z => z.Normal, z => z.Advanced, StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> s_normalOf
        = s_zones.ToDictionary(z => z.Advanced, z => z.Normal, StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> s_nameOf
        = s_zones.ToDictionary(z => z.Normal, z => z.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The dungeons with an Advanced copy, for `.advanced list`: (normal zone, copy, name).</summary>
    internal static IReadOnlyList<(string Normal, string Advanced, string Name)> Zones => s_zones;

    /// <summary>The Advanced copy of a zone, when the game has one.</summary>
    internal static bool TryGetAdvanced(string zone, out string advanced) {
        advanced = null;

        return !string.IsNullOrEmpty(zone) && s_advancedOf.TryGetValue(zone, out advanced);
    }

    /// <summary>The normal zone of an Advanced copy.</summary>
    internal static bool TryGetNormal(string zone, out string normal) {
        normal = null;

        return !string.IsNullOrEmpty(zone) && s_normalOf.TryGetValue(zone, out normal);
    }

    internal static bool IsAdvanced(string zone)
        => !string.IsNullOrEmpty(zone) && s_normalOf.ContainsKey(zone);

    /// <summary>The name of a dungeon with an Advanced copy (by its normal zone or the copy), or the zone.</summary>
    internal static string NameOf(string zone)
        => TryGetNormal(zone, out var normal) ? NameOf(normal)
            : s_nameOf.TryGetValue(zone ?? "", out var name) ? name : zone;

    // ---------------------------------------------------------------------
    // Who goes in Advanced
    // ---------------------------------------------------------------------

    // Wizards who typed `.advanced on`: their sigil presses go into the Advanced copies (until the server restarts).
    private static readonly ConcurrentDictionary<ulong, bool> s_wanted = new();

    internal static bool Wants(ulong charId)
        => Enabled && s_wanted.ContainsKey(charId);

    internal static void SetWanted(ulong charId, bool on) {
        if (on) {
            s_wanted[charId] = true;
        }
        else {
            s_wanted.TryRemove(charId, out _);
        }
    }

    /// <summary>The end of a variant (and of a room's name in Customs/Rooms.json) for the Advanced version of a custom fight.</summary>
    internal const string ADVANCED_SUFFIX = "~Advanced";

    /// <summary>A variant without its Advanced end, the one its ZoneObjects files are named by.</summary>
    internal static string BaseVariant(string variant)
        => IsAdvancedVariant(variant) ? variant[..^ADVANCED_SUFFIX.Length] : variant ?? "";

    internal static bool IsAdvancedVariant(string variant)
        => variant?.EndsWith(ADVANCED_SUFFIX, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Whether the Customs tab gives the room a sigil leads to an Advanced version.</summary>
    internal static bool HasCustomAdvanced(string zone, string variant)
        => !string.IsNullOrEmpty(zone) && Imlight.CoreLib.Game.Combat.Customs.HasAdvancedRoom(zone, BaseVariant(variant));

    /// <summary>Whether a sigil to this zone (and variant) offers Advanced Mode: a custom fight's or the game's copy.</summary>
    internal static bool Offers(string zone, string variant)
        => Enabled && !IsAdvancedVariant(variant)
           && (HasCustomAdvanced(zone, variant) || (string.IsNullOrEmpty(variant) && TryGetAdvanced(zone, out _)));

    /// <summary>
    /// Where a sigil press goes, zone and variant: the Advanced version for a press with the game's Advanced Mode
    /// button on, or from a wizard with `.advanced on`. A custom fight's Advanced version is the same zone with the
    /// Advanced variant; otherwise the game's Advanced copy (only for a sigil with no variant, whose files are the
    /// zone's). Anything else goes where the sigil says.
    /// </summary>
    internal static (string Zone, string Variant) TripFor(string zone, string variant, uint requestedMode, ulong charId) {
        variant ??= "";
        if (!Offers(zone, variant) || (requestedMode != ADVANCED_SIGIL_MODE && !Wants(charId))) {
            return (zone, variant);
        }

        if (HasCustomAdvanced(zone, variant)) {
            return (zone, variant + ADVANCED_SUFFIX);
        }

        return TryGetAdvanced(zone, out var advanced) ? (advanced, variant) : (zone, variant);
    }

    /// <summary>Whether a trip goes in Advanced Mode (a game copy or a custom fight's Advanced version).</summary>
    internal static bool IsAdvancedTrip(string zone, string variant) => IsAdvanced(zone) || IsAdvancedVariant(variant);

    // ---------------------------------------------------------------------
    // Doors and exits inside a copy
    // ---------------------------------------------------------------------

    private static readonly ConcurrentDictionary<string, WizardZoneData> s_mergedZoneData = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The zone data of an Advanced copy: its own teleports (none from SpiralDB so far) and the normal dungeon's, each
    /// leading to the Advanced copy of where it goes when the game has one. Null for any other zone.
    /// </summary>
    internal static WizardZoneData ZoneDataOf(string zone, Func<string, WizardZoneData> source) {
        if (!TryGetNormal(zone, out var normal)) {
            return null;
        }

        return s_mergedZoneData.GetOrAdd(zone, _ => {
            var own = source(zone);
            var basis = source(normal);
            var merged = new WizardZoneData { ZoneName = zone, Teleports = [.. own?.Teleports ?? []] };
            foreach (var teleport in basis?.Teleports ?? []) {
                if (teleport?.Teleport is null || string.IsNullOrEmpty(teleport.TriggerName)
                    || merged.Teleports.Any(t => t.TriggerName == teleport.TriggerName)) {
                    continue;
                }

                var destination = teleport.Teleport.m_destinationZone?.ToString();
                merged.Teleports.Add(new WizardTeleportData {
                    TriggerName = teleport.TriggerName,
                    Teleport = TryGetAdvanced(destination, out var advanced)
                        ? teleport.Teleport with { m_destinationZone = advanced }
                        : teleport.Teleport,
                });
            }

            Logger.Information("Advanced Mode: {0} uses {1} teleports of {2}.",
                Logger.Args(zone, merged.Teleports.Count - (own?.Teleports?.Count ?? 0), normal));

            return merged;
        });
    }

    // ---------------------------------------------------------------------
    // Adv_ creatures
    // ---------------------------------------------------------------------

    private static readonly ConcurrentDictionary<ulong, ulong> s_normalTemplates = new();

    [GeneratedRegex(@"^Adv[_-]|[_-]Adv$", RegexOptions.IgnoreCase)]
    private static partial Regex AdvancedNameRegex();

    /// <summary>
    /// The normal creature of an Advanced one (Adv_WL_Toad_EvilClaw_A_01 is WL_Toad_EvilClaw_A_01), or 0 when the
    /// creature is no Advanced one or the game has no normal one.
    /// </summary>
    internal static ulong NormalCreatureOf(ulong templateId)
        => templateId == 0 ? 0 : s_normalTemplates.GetOrAdd(templateId, id => {
            var name = CoreObjectFactory.FileNameOf(id);
            if (string.IsNullOrEmpty(name) || !AdvancedNameRegex().IsMatch(name)) {
                return 0;
            }

            var normal = CoreObjectFactory.IdOfFileName(AdvancedNameRegex().Replace(name, ""));

            return normal != id ? normal : 0;
        });

    /// <summary>The normal deck of an Advanced creature's deck (Mdeck-S-WL-Adv-Dog-NavyCaptain-A-01 is Mdeck-S-WL-Dog-NavyCaptain-A-01).</summary>
    internal static string NormalDeckOf(string deckName) {
        if (string.IsNullOrEmpty(deckName) || !deckName.Contains("Adv", StringComparison.OrdinalIgnoreCase)) {
            return null;
        }

        var normal = Regex.Replace(deckName, @"(?<=-)Adv-|-Adv(?=-|$)", "", RegexOptions.IgnoreCase);

        return normal != deckName ? normal : null;
    }

}
