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
 *
 * ========================================================================
 * PET HATCHING
 * ========================================================================
 *
 * PURPOSE:
 * Breeds two pets into an egg: the species of one parent, talents and derby talents from both
 * parents' genes, maximum stats near the parents' average. Nothing here saves or sends; PetService
 * adds the egg to the backpack, starts its timer and tells the game (Niduus, 2026-10-20).
 *
 * USAGE EXAMPLE:
 * var egg = PetHatching.MakeEgg(wizard.CharId, PetHatching.Snapshot(petA, id), PetHatching.Snapshot(petB, id), Random.Shared, out var seconds, out var problem);
 *
 * NOTE:
 * A parent is a snapshot, so the other player's pet can be read in that player's own session and
 * sent here. Both parents stay as they are. Settings are in the [Hatching] section of Imlight.ini.
 *
 * TODO:
 * - The live gene rules are not known: each parent's shown talents are preferred, then their other genes.
 * - The new species' guaranteed talents (May Casts and the like) go first, as when a pet is bought.
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/20/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.Cryptography;
using Imcodec.IO;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Pet;

/// <summary>
/// Where a hatch was asked for, which decides what the game is told afterwards.
/// </summary>
internal enum HatchSource : byte {
    Command,
    Kiosk,
    InPerson,
    // The hatch window in the pet morph zone, reached from a hatchery sigil.
    Sigil,
}

/// <summary>
/// The status codes of MSG_PETHATCHRESULT. A guess: copied from the trade result codes (TradeError), since the
/// two windows are built the same way. Check the client's message for each number in Niduus's log.
/// </summary>
internal enum HatchStatus : uint {
    Success = 0,
    TargetNotInZone = 4,
    TargetNotFound = 5,
    Failed = 8,
    NotJoined = 10,
    NotReady = 11,
    AlreadyHatching = 12,
    CannotHatchNow = 14,
    NotAccepting = 15,
}

/// <summary>
/// A stat of a parent pet.
/// </summary>
internal sealed class PetParentStat {
    public string Name { get; set; }
    public int Value { get; set; }
}

/// <summary>
/// What breeding needs from a parent pet, copied from its item.
/// </summary>
internal sealed class PetParent {
    public ulong ItemId { get; set; }
    public ulong OwnerCharId { get; set; }
    public uint TemplateId { get; set; }
    public byte Level { get; set; }
    public string Label { get; set; }
    public List<uint> Talents { get; set; } = [];
    public List<uint> DerbyTalents { get; set; } = [];
    public List<uint> ExpressedTalents { get; set; } = [];
    public List<uint> ExpressedDerbyTalents { get; set; } = [];
    public List<PetParentStat> MaxStats { get; set; } = [];
    public int PrimaryColor { get; set; }
    public int SecondaryColor { get; set; }
    public int Pattern { get; set; }
}

internal static class PetHatching {

    private const int STAT_SPREAD = 5;
    private const int DEFAULT_POOL_SIZE = 10;

    /// <summary>
    /// [Hatching] HatchMinutes: how long an egg takes. -1 (or any negative) uses the pet's own hatch time from the
    /// game files, which is hours for most pets.
    /// </summary>
    internal static int HatchMinutes => ConfigurationManager.GetValue("Hatching.HatchMinutes", -1);

    /// <summary>
    /// [Hatching] GoldCost: gold taken from whoever gets the egg. 0 is free.
    /// </summary>
    internal static int GoldCost => Math.Max(0, ConfigurationManager.GetValue("Hatching.GoldCost", 0));

    /// <summary>
    /// [Hatching] MinParentLevel: the age a parent needs, 1 (Baby) means any hatched pet.
    /// </summary>
    internal static int MinParentLevel => Math.Max(1, ConfigurationManager.GetValue("Hatching.MinParentLevel", 1));

    /// <summary>
    /// [Hatching] ReadyToHatch: the ready value both players must send in the in-person window before the eggs are
    /// made. A guess: 1 when the window has one Hatch button, 2 if it has a confirm step like trading.
    /// </summary>
    internal static int ReadyToHatch => Math.Max(1, ConfigurationManager.GetValue("Hatching.ReadyToHatch", 1));

    /// <summary>
    /// [Hatching] Enabled: false turns off every way to hatch.
    /// </summary>
    internal static bool Enabled => ConfigurationManager.GetValue("Hatching.Enabled", true);

    /// <summary>
    /// [Hatching] SigilWindow: false stops the hatchery sigils from sending players to the pet morph zone
    /// (ThePhantomZoneWorld/PetGameMorph) and its hatch window, leaving the chat list and `.hatch` (and, on a duo
    /// sigil with two players, the in-person hatch request).
    /// </summary>
    internal static bool SigilWindow => ConfigurationManager.GetValue("Hatching.SigilWindow", true);

    /// <summary>
    /// [Hatching] LendsPerDay: "Lends Available Today" on the hatch window's Hatch or Lend screen. The game holds it in
    /// one byte, so it shows 255 at most; 0 turns Lend Pet off. Lends are not counted down.
    /// </summary>
    internal static int LendsPerDay => ConfigurationManager.GetValue("Hatching.LendsPerDay", 999);

    /// <summary>
    /// The wizard's hatched pets: the one worn first, then the backpack's.
    /// </summary>
    internal static List<WizClientObjectItem> OwnPets(Wizard wizard) {
        if (wizard is null) {
            return [];
        }

        var worn = PetCare.GetWornPet(wizard);
        var items = (worn is null ? [] : new List<WizClientObjectItem> { worn })
            .Concat(wizard.InventoryBehavior?.Items ?? []);

        return [.. items.Where(i => PetProgress.GetBehavior(i) is { m_level: > 0 }).Distinct()];
    }

    /// <summary>
    /// Copies what breeding needs from a pet item.
    /// </summary>
    /// <returns>Null if the item is not a pet.</returns>
    internal static PetParent Snapshot(WizClientObjectItem pet, ulong ownerCharId) {
        var behavior = PetProgress.GetBehavior(pet);
        if (behavior is null) {
            return null;
        }

        return new PetParent {
            ItemId = pet.m_globalID.Full,
            OwnerCharId = ownerCharId,
            TemplateId = (uint) pet.m_templateID,
            Level = behavior.m_level,
            Label = Label(pet),
            Talents = [.. behavior.m_allTalents ?? []],
            DerbyTalents = [.. behavior.m_allDerbyTalents ?? []],
            ExpressedTalents = [.. behavior.m_expressedTalents ?? []],
            ExpressedDerbyTalents = [.. behavior.m_expressedDerbyTalents ?? []],
            MaxStats = [.. (behavior.m_maxStats ?? []).Where(s => s is not null)
                .Select(s => new PetParentStat { Name = s.m_name.ToString(), Value = s.m_value })],
            PrimaryColor = pet.m_primaryColor,
            SecondaryColor = pet.m_secondaryColor,
            Pattern = pet.m_pattern,
        };
    }

    /// <summary>
    /// The name a player knows the pet by: its own name if it was given one, else its kind (Fire Cat).
    /// </summary>
    internal static string Label(WizClientObjectItem pet) {
        if (pet is null) {
            return "";
        }

        if (CoreObjectFactory.FindBehaviorInstance<ClientPetNameBehavior>(pet, out var name)
            && name.m_wsNameOverride.ToString() is { Length: > 0 } custom) {
            return custom;
        }

        return KindName((uint) pet.m_templateID) ?? pet.m_debugName.ToString();
    }

    /// <summary>
    /// The English name of a pet kind (Fire Cat), or null.
    /// </summary>
    internal static string KindName(uint templateId) {
        if (CoreObjectFactory.GetCoreTemplate(templateId) is not GameObjectTemplate template) {
            return null;
        }

        var key = template.m_displayName.ToString();
        var english = string.IsNullOrEmpty(key) ? "" : Locale.GetEnglishName(key);
        if (!string.IsNullOrEmpty(english)) {
            return english;
        }

        var objectName = template.m_objectName.ToString();

        return string.IsNullOrEmpty(objectName) ? null : objectName;
    }

    /// <summary>
    /// Why a pet cannot be a parent, or null if it can.
    /// </summary>
    internal static string ParentProblem(PetParent parent) {
        if (parent is null) {
            return "That is not a pet.";
        }

        if (!PetFactory.IsPetTemplate(parent.TemplateId)) {
            return $"{parent.Label} is not a pet that can hatch (template {parent.TemplateId}).";
        }

        if (parent.Level < MinParentLevel) {
            return parent.Level == 0
                ? $"{parent.Label} is still an egg."
                : $"{parent.Label} has to be at least {PetProgress.LevelName(MinParentLevel)} to hatch.";
        }

        return null;
    }

    /// <summary>
    /// Why two pets cannot hatch together, or null if they can.
    /// </summary>
    internal static string PairProblem(PetParent a, PetParent b) {
        if (!Enabled) {
            return "Hatching is turned off on this server ([Hatching] Enabled).";
        }

        var problem = ParentProblem(a) ?? ParentProblem(b);
        if (problem is not null) {
            return problem;
        }

        return a.ItemId == b.ItemId ? "Pick two different pets to hatch." : null;
    }

    /// <summary>
    /// Builds the egg two parents make, for the owner. The egg is a level 0 pet item; it is stored nowhere yet.
    /// </summary>
    /// <param name="ownerCharId">Who gets the egg.</param>
    /// <param name="a">The owner's parent.</param>
    /// <param name="b">The other parent, which may be another player's pet.</param>
    /// <param name="random">The random source.</param>
    /// <param name="hatchSeconds">Seconds from now until the egg hatches.</param>
    /// <param name="problem">Why no egg was made.</param>
    /// <returns>The egg, or null.</returns>
    internal static WizClientObjectItem MakeEgg(ulong ownerCharId, PetParent a, PetParent b, Random random,
                                                out uint hatchSeconds, out string problem) {
        hatchSeconds = 0;
        problem = PairProblem(a, b);
        if (problem is not null) {
            return null;
        }

        // The egg is the kind of one of the parents, either one.
        var species = random.Next(2) == 0 ? a : b;
        var egg = PetFactory.CreatePet(ownerCharId, species.TemplateId, preHatch: false);
        var behavior = PetProgress.GetBehavior(egg);
        var template = PetProgress.GetTemplate(egg);
        if (egg is null || behavior is null || template is null) {
            problem = $"Could not make an egg of {species.Label} (template {species.TemplateId}).";

            return null;
        }

        behavior.m_allTalents = InheritGenes(template.m_guaranteedTalents, template.m_talents,
            a.ExpressedTalents, b.ExpressedTalents, a.Talents, b.Talents, random);
        behavior.m_allDerbyTalents = InheritGenes(template.m_guaranteedDerbyTalents, template.m_derbyTalents,
            a.ExpressedDerbyTalents, b.ExpressedDerbyTalents, a.DerbyTalents, b.DerbyTalents, random);
        behavior.m_expressedTalents = [];
        behavior.m_expressedDerbyTalents = [];
        behavior.m_maxStats = AverageStats(template, a, b, random);

        // The egg's own maximum, which the hatched pet keeps; before 2026-10-22 hatching replaced it with 260 for every
        // stat (Niduus, 2026-10-22).
        PetProgress.RememberOwnMaxStats(egg, behavior.m_maxStats);

        // A new pet gets a name of its own; with none, every pet showed the first name in the tables (Niduus, 2026-10-20).
        PetFactory.TrySetPetName(egg, WizardNameBank.RandomPetName(random));

        // The species parent's colors are colors that kind can have.
        egg.m_primaryColor = species.PrimaryColor;
        egg.m_secondaryColor = species.SecondaryColor;
        egg.m_pattern = species.Pattern;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var minutes = HatchMinutes;
        hatchSeconds = minutes >= 0
            ? (uint) Math.Min(uint.MaxValue, (long) minutes * 60)
            : (uint) Math.Max(0, behavior.m_hatchedTimeSecs - now);
        behavior.m_hatchedTimeSecs = (uint) (now + hatchSeconds);

        return egg;
    }

    /// <summary>
    /// One line about a parent for the log: kind, level, genes and maximum stats.
    /// </summary>
    internal static string Describe(PetParent parent)
        => parent is null ? "none" : $"{parent.Label} (item {parent.ItemId}, template {parent.TemplateId}, owner {parent.OwnerCharId}, " +
            $"level {parent.Level}, talents {Talents(parent.Talents)}, shown {Talents(parent.ExpressedTalents)}, " +
            $"derby {Talents(parent.DerbyTalents)}, max stats {string.Join("/", parent.MaxStats.Select(s => $"{s.Name} {s.Value}"))})";

    internal static string Talents(IEnumerable<uint> ids)
        => string.Join(", ", (ids ?? []).Select(id => PetTalentRegistry.TryGetById(id, out var talent) ? talent.Name : id.ToString()));

    /// <summary>
    /// The new pet's genes: its kind's guaranteed talents, then the parents' shown talents taken in turns, then
    /// the rest of their genes in random order, then the kind's own pool if the parents had too few. As many as
    /// the kind's pool has, without duplicates.
    /// </summary>
    private static List<uint> InheritGenes(List<ByteString> guaranteed, List<ByteString> pool,
                                           List<uint> shownA, List<uint> shownB, List<uint> genesA, List<uint> genesB, Random random) {
        var own = PetProgression.RollGenes(guaranteed, pool, random);
        var size = own.Count > 0 ? own.Count : DEFAULT_POOL_SIZE;
        var genes = new List<uint>(size);

        void Add(uint id) {
            if (genes.Count < size && id != 0 && !genes.Contains(id)) {
                genes.Add(id);
            }
        }

        foreach (var name in guaranteed ?? []) {
            if (PetTalentRegistry.TryGetTemplateId(name.ToString(), out var id)) {
                Add(id);
            }
        }

        var a = Shuffled(shownA, random);
        var b = Shuffled(shownB, random);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++) {
            var first = random.Next(2) == 0;
            if (first && i < a.Length) Add(a[i]);
            if (i < b.Length) Add(b[i]);
            if (!first && i < a.Length) Add(a[i]);
        }

        foreach (var id in Shuffled([.. (genesA ?? []).Concat(genesB ?? [])], random)) {
            Add(id);
        }

        foreach (var id in own) {
            Add(id);
        }

        return genes;
    }

    private static uint[] Shuffled(List<uint> ids, Random random) {
        var copy = (ids ?? []).ToArray();
        random.Shuffle(copy);

        return copy;
    }

    /// <summary>
    /// The maximum of each stat of the new pet's kind: the parents' average give or take a few points, but no more
    /// than the server's maximum. A stat only one parent has counts that parent's; one neither has keeps the kind's own.
    /// </summary>
    private static List<PetStat> AverageStats(PetItemBehaviorTemplate template, PetParent a, PetParent b, Random random) {
        var names = (template.m_maxStats ?? []).Where(s => s is not null).Select(s => s.m_name.ToString())
            .Concat(a.MaxStats.Select(s => s.Name)).Concat(b.MaxStats.Select(s => s.Name))
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var stats = new List<PetStat>(names.Count);
        foreach (var name in names) {
            var values = a.MaxStats.Concat(b.MaxStats)
                .Where(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.Value)
                .ToList();
            var most = BaseMaxStat(template, name);
            var value = values.Count > 0
                ? (int) Math.Round(values.Average()) + random.Next(-STAT_SPREAD, STAT_SPREAD + 1)
                : OwnMaxStat(template, name);

            // Never above the server's maximum: only selfish talents raise that (Niduus, 2026-10-20).
            stats.Add(new PetStat { m_name = name, m_statID = StringHash.Compute(name), m_value = Math.Clamp(value, 1, Math.Max(1, most)) });
        }

        return stats;
    }

    // The ceiling on a hatched stat: [Pets] MaxStrength and the others, or the kind's own when that is 0.
    private static int BaseMaxStat(PetItemBehaviorTemplate template, string name) {
        var configured = PetRules.MaxStat(name);

        return configured > 0 ? configured : PetProgress.GetStat(template.m_maxStats, name);
    }

    // The kind's own maximum of a stat neither parent has, under the ceiling.
    private static int OwnMaxStat(PetItemBehaviorTemplate template, string name) {
        var own = PetProgress.GetStat(template.m_maxStats, name);
        var configured = PetRules.MaxStat(name);

        return own <= 0 ? configured : configured > 0 ? Math.Min(own, configured) : own;
    }

}
