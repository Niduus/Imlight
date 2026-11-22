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
 * KIOSK HOUSE PETS (pet.kiosk)
 * ========================================================================
 *
 * PURPOSE:
 * Fills the Pet Hatching Kiosk with the kiosk's own pets, so it is never empty (Niduus, 2026-11-21: "all pets in the
 * game with every known build ... random builds like looking like players just gunned a pet to mega, not caring about
 * talents, and full optimized stats, like all damage for a singular school or duo/trio school"). Every pet kind of
 * the game files gets a few: pets raised any old way from their own talent pool, and pets built for one job (damage
 * for one, two or three schools, critical, accuracy, pierce, resist, healing, pips, selfish stats). Every talent a
 * player can search for is shown by some of them, so Search Pet Talents always finds pets.
 *
 * USAGE EXAMPLE:
 * var all = KioskHousePets.Listings();
 *
 * NOTE:
 * Made from this PC's own game files the first time the kiosk needs them, then kept in pet.kiosk next to the server
 * (a JSON list of kiosk listings, the same as hatchmaking-listings.json). A new pet.kiosk is made when it is missing,
 * when it was made by an older version, or when [HatchKiosk] MinAge changed. The pets belong to nobody (owner 0):
 * hatching with one never runs out, and no owner is told. [HatchKiosk] HousePets = false hides them.
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 11/21/2026
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Imcodec.Cryptography;
using Imcodec.IO;
using Imcodec.Types;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.Shared.Utilities;

namespace Imlight.CoreLib.Game.Pet;

internal sealed class KioskHouseFile {
    public int Version { get; set; }
    public int MinAge { get; set; }
    public long MadeAt { get; set; }
    public List<KioskHousePet> Pets { get; set; } = [];
}

internal sealed class KioskHousePet {

    /// <summary>What the pet was built for, for the log (gunned, raised, young, Fire damage, ...).</summary>
    public string Build { get; set; }

    public HatchmakingListing Listing { get; set; }

}

internal static class KioskHousePets {

    internal const string FILE = "pet.kiosk";

    // Raise when the builds change, so every server makes a new pet.kiosk.
    private const int VERSION = 4;

    // A talent search that finds fewer pets than this makes more on the spot (Niduus, 2026-11-22: "there will always be
    // at least 3+ pets in the kiosk that have what you're looking for").
    internal const int FEWEST_FOUND = 3;

    // How many pets a search with too few finds is topped up to.
    private const int TOPPED_UP_TO = 5;

    private const int MEGA = 6;
    private const int ADULT = 3;
    private const int GENES = 10;

    // How many kiosk pets show each searchable talent at least.
    private const int PETS_PER_TALENT = 10;

    // How many built-for-a-job pets each pet kind gets.
    private const int BUILDS_PER_KIND = 4;

    // How many pets of each kind carry a random mix of popular talents (damage, defense and selfish stats).
    private const int MIXES_PER_KIND = 6;

    private static readonly string[] s_schools = ["Fire", "Ice", "Storm", "Myth", "Life", "Death", "Balance"];

    private static readonly Lock s_lock = new();
    private static List<HatchmakingListing> s_listings;
    private static Dictionary<ulong, HatchmakingListing> s_byId = [];
    private static KioskHouseFile s_file;
    private static List<(uint Id, PetItemBehaviorTemplate Template)> s_kinds;
    private static bool s_saving;
    private static bool s_saveAgain;
    private static int s_madeForAge = -1;
    private static bool s_making;

    /// <summary>
    /// [HatchKiosk] HousePets: the kiosk's own pets (pet.kiosk) are at the kiosk. True by default.
    /// </summary>
    internal static bool Enabled => ConfigurationManager.GetValue("HatchKiosk.HousePets", true);

    /// <summary>
    /// [HatchKiosk] MinAge: the youngest age a pet can be at the kiosk (Baby, Teen, Adult, ...). Adult by default, the
    /// game's own rule.
    /// </summary>
    internal static int MinAge
        => PetProgress.ParseLevel(ConfigurationManager.GetValue("HatchKiosk.MinAge", "Adult") ?? "Adult") is { } level ? level : ADULT;

    /// <summary>
    /// Why a player's pet is too young for the kiosk ([HatchKiosk] MinAge), or null.
    /// </summary>
    internal static string AgeProblem(PetParent pet)
        => pet is not null && pet.Level < MinAge
            ? $"{pet.Label} has to be at least {PetProgress.LevelName(MinAge)} to go on the Pet Hatching Kiosk."
            : null;

    /// <summary>
    /// The kiosk's own pets. Empty while they are being made the first time (that runs on its own thread), or when
    /// [HatchKiosk] HousePets is false.
    /// </summary>
    internal static List<HatchmakingListing> Listings() {
        if (!Enabled) {
            return [];
        }

        lock (s_lock) {
            if (s_listings is not null && s_madeForAge == MinAge) {
                return s_listings;
            }

            if (!s_making) {
                s_making = true;
                var age = MinAge;
                new Thread(() => LoadOrMake(age)) { IsBackground = true, Name = "pet.kiosk" }.Start();
            }

            return s_listings ?? [];
        }
    }

    /// <summary>
    /// One of the kiosk's own pets by its item id, or null.
    /// </summary>
    internal static HatchmakingListing Find(ulong itemId) {
        if (!Enabled) {
            return null;
        }

        Listings();
        lock (s_lock) {
            return s_byId.GetValueOrDefault(itemId);
        }
    }

    /// <summary>
    /// True when the pet is one of the kiosk's own.
    /// </summary>
    internal static bool IsHousePet(ulong itemId) => Find(itemId) is not null;

    private static void LoadOrMake(int age) {
        try {
            var file = Read(age) ?? Make(age);
            var listings = file.Pets.Select(p => p.Listing).Where(l => l?.Parent is not null && l.ItemData is not null).ToList();
            lock (s_lock) {
                s_listings = listings;
                s_byId = listings.GroupBy(l => l.Parent.ItemId).ToDictionary(g => g.Key, g => g.First());
                s_madeForAge = age;
                s_file = file;
            }

            Logger.Information("Kiosk: {0} of the kiosk's own pets are up ({1} kinds of pet).",
                Logger.Args(listings.Count, listings.Select(l => l.Parent.TemplateId).Distinct().Count()));
        }
        catch (Exception ex) {
            Logger.Error("Kiosk: could not load or make {0}. {1}", Logger.Args(FILE, ex));
            lock (s_lock) {
                s_listings ??= [];
                s_madeForAge = age;
            }
        }
        finally {
            lock (s_lock) {
                s_making = false;
            }
        }
    }

    private static KioskHouseFile Read(int age) {
        try {
            if (!File.Exists(FILE)) {
                return null;
            }

            var file = JsonSerializer.Deserialize<KioskHouseFile>(File.ReadAllText(FILE));
            if (file is null || file.Version != VERSION || file.MinAge != age || file.Pets is not { Count: > 0 }) {
                Logger.Information("Kiosk: {0} is from an older version or another youngest age, so a new one is made.", Logger.Args(FILE));

                return null;
            }

            return file;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) {
            Logger.Warning("Kiosk: could not read {0}, so a new one is made. {1}", Logger.Args(FILE, ex.Message));

            return null;
        }
    }

    /// <summary>
    /// Makes the kiosk's own pets from the game files and saves them in pet.kiosk.
    /// </summary>
    internal static KioskHouseFile Make(int age) {
        var started = DateTime.UtcNow;
        var file = new KioskHouseFile { Version = VERSION, MinAge = age, MadeAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
        var random = new Random(20261121);
        var builds = Builds();
        var mixes = new Mixes();
        var kinds = Kinds();

        foreach (var (id, template) in kinds) {
            var own = OwnPool(template);
            var top = MaxLevel(template);

            // Raised any old way: its own talents in the order it rolled them, leveled to Mega, stats half trained.
            Add(file, id, template, "gunned to Mega", Shuffled(own, random), Math.Min(MEGA, top), random, statShare: 0.3 + random.NextDouble() * 0.5);
            // Raised a bit more: its own talents, any age from Adult up, stats mostly trained.
            Add(file, id, template, "raised", Shuffled(own, random), random.Next(ADULT, top + 1), random, statShare: 0.6 + random.NextDouble() * 0.4);
            // A young pet, when the kiosk shows them ([HatchKiosk] MinAge under Adult).
            if (age < ADULT) {
                Add(file, id, template, "young", Shuffled(own, random), random.Next(Math.Max(1, age), ADULT), random, statShare: random.NextDouble() * 0.4);
            }

            // Another one raised any old way, so every kind has a few players' pets.
            Add(file, id, template, "raised", Shuffled(own, random), random.Next(ADULT, top + 1), random, statShare: 0.4 + random.NextDouble() * 0.6);

            // Built for a job: the job's talents first, so they are the ones shown, then its own pool.
            foreach (var (name, talents) in builds.OrderBy(_ => random.Next()).Take(BUILDS_PER_KIND)) {
                Add(file, id, template, name, [.. talents, .. Shuffled(own, random)], top, random, statShare: 1);
            }

            // A mix of popular talents, the way players hatch for them (Spell-Proof, Spell-Defy, Mighty, Pain-Giver and the
            // like), so searches for common combinations find many pets (Niduus, 2026-11-21: "more pets, more combos").
            for (var i = 0; i < MIXES_PER_KIND; i++) {
                var (name, talents) = mixes.Mix(random);
                Add(file, id, template, name, [.. talents, .. Shuffled(own, random)], Math.Max(MEGA, Math.Min(top, MEGA + random.Next(2))),
                    random, statShare: 0.8 + random.NextDouble() * 0.2);
            }
        }

        // Every talent a player can search for is shown by a few pets, of kinds that can have it when there are any.
        foreach (var talent in Searchable()) {
            var have = file.Pets.Count(p => p.Listing.Parent.ExpressedTalents.Contains(talent.Id));
            if (have >= PETS_PER_TALENT || kinds.Count == 0) {
                continue;
            }

            var fitting = kinds.Where(k => (k.Template.m_talents ?? []).Any(n => n.ToString() == talent.Name)).ToList();
            var pool = fitting.Count > 0 ? fitting : kinds;
            for (var i = have; i < PETS_PER_TALENT; i++) {
                var (id, template) = pool[random.Next(pool.Count)];
                Add(file, id, template, "shows " + talent.Name, [talent.Id, .. Shuffled(OwnPool(template), random)],
                    random.Next(Math.Max(ADULT, 2), MaxLevel(template) + 1), random, statShare: 0.5 + random.NextDouble() * 0.5);
            }
        }

        try {
            File.WriteAllText(FILE + ".tmp", JsonSerializer.Serialize(file));
            File.Move(FILE + ".tmp", FILE, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Warning("Kiosk: could not save {0}: {1}", Logger.Args(FILE, ex.Message));
        }

        Logger.Information("Kiosk: made {0} with {1} pets of {2} kinds in {3:0.0} s.",
            Logger.Args(FILE, file.Pets.Count, kinds.Count, (DateTime.UtcNow - started).TotalSeconds));

        return file;
    }

    // Every pet kind the kiosk's own pets can be: named, not a developer's, with talents, and able to grow to Adult.
    private static List<(uint Id, PetItemBehaviorTemplate Template)> Kinds() {
        _ = PetFactory.Instance;
        _ = PetTalentRegistry.Instance;
        _ = CoreObjectFactory.Instance;
        _ = WizardNameBank.Instance;
        _ = Locale.Instance;

        return s_kinds ??= [.. PetFactory.AllPetTemplates.Keys.OrderBy(id => id)
            .Select(id => (Id: id, Template: PetFactory.GetPetItemBehaviorTemplate(id)))
            .Where(k => k.Template is { m_talents.Count: > 0 } && MaxLevel(k.Template) >= ADULT && !IsDevPet(k.Id) && HasName(k.Id))];
    }

    private static void Add(KioskHouseFile file, uint templateId, PetItemBehaviorTemplate template, string build,
                            List<uint> genes, int level, Random random, double statShare, List<uint> derbyGenes = null) {
        if (MakeHousePet(templateId, template, build, genes, level, random, statShare, derbyGenes, file.MadeAt,
                $"pet.kiosk|{templateId}|{file.Pets.Count}|{build}") is { } pet) {
            file.Pets.Add(pet);
        }
    }

    private static KioskHousePet MakeHousePet(uint templateId, PetItemBehaviorTemplate template, string build, List<uint> genes,
                                              int level, Random random, double statShare, List<uint> derbyGenes, long listedAt, string idSeed) {
        var item = MakePet(templateId, template, genes, level, random, statShare, RandomGen.GenerateHash(idSeed), derbyGenes);
        if (item is null) {
            return null;
        }

        var parent = PetHatching.Snapshot(item, 0);
        if (parent is null || !ItemSync.TrySerializeForKiosk(item, out var data)) {
            return null;
        }

        // Only pets the kiosk can send back to the game as it reads them (a few kinds' items cannot be read back).
        var itemData = Convert.ToBase64String(data);
        if (ItemSync.KioskPetBytes(itemData) is null) {
            return null;
        }

        return new KioskHousePet {
            Build = build,
            Listing = new HatchmakingListing {
                Parent = parent,
                NameKeys = CoreObjectFactory.FindBehaviorInstance<ClientPetNameBehavior>(item, out var name) ? name.m_nameKeys : 0,
                ListedAt = listedAt,
                ItemData = itemData,
            },
        };
    }

    /// <summary>
    /// Makes kiosk pets that show the searched talents, for a talent search that found too few (Niduus, 2026-11-22:
    /// the "no pets match" message should basically never show). A pet shows at most five talents, so with more picked
    /// each new pet shows the first five of them. The new pets join the kiosk's own and are kept in pet.kiosk.
    /// </summary>
    /// <param name="wanted">The talents searched for, in the order the player picked them.</param>
    /// <param name="have">How many pets the search found already.</param>
    /// <returns>The new pets' listings; empty when the kiosk's own pets are off or not up yet.</returns>
    internal static List<HatchmakingListing> MakeFor(List<uint> wanted, int have) {
        if (!Enabled || wanted is not { Count: > 0 } || have >= FEWEST_FOUND) {
            return [];
        }

        KioskHouseFile file;
        lock (s_lock) {
            if (s_listings is null || s_file is null || s_madeForAge != MinAge) {
                return [];
            }

            file = s_file;
        }

        var talents = wanted.Where(id => id != 0 && PetTalentRegistry.TryGetById(id, out _) && !IsDevTalent(id)).Distinct().ToList();
        var combat = talents.Where(id => PetTalentRegistry.TryGetById(id, out var t) && t.Kind != PetTalentKind.Derby)
            .Take(PetHatching.MOST_REVEALED).ToList();
        var derby = talents.Where(id => PetTalentRegistry.TryGetById(id, out var t) && t.Kind == PetTalentKind.Derby)
            .Take(PetHatching.MOST_REVEALED).ToList();
        if (combat.Count == 0 && derby.Count == 0) {
            return [];
        }

        // A pet shows one more talent each age after Baby, so showing five takes a Mega pet.
        var needed = Math.Max(combat.Count, derby.Count) + 1;
        var names = new HashSet<string>(talents.Select(id => PetTalentRegistry.TryGetById(id, out var t) ? t.Name : ""));
        var random = new Random(unchecked((int) (DateTime.UtcNow.Ticks ^ talents.Aggregate(17u, (h, id) => h * 31 + id))));
        // Kinds that grow old enough, those whose own talents hold the most of the wanted ones first.
        var kinds = Kinds().Where(k => MaxLevel(k.Template) >= needed)
            .Select(k => (k.Id, k.Template, Fits: (k.Template.m_talents ?? []).Concat(k.Template.m_derbyTalents ?? [])
                .Select(n => n.ToString()).Distinct().Count(names.Contains)))
            .ToList();
        if (kinds.Count == 0) {
            return [];
        }

        var best = kinds.Max(k => k.Fits);
        var pool = kinds.Where(k => k.Fits == best).OrderBy(_ => random.Next()).ToList();
        var made = new List<KioskHousePet>();
        var madeAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        for (var i = 0; made.Count < TOPPED_UP_TO - have && i < (TOPPED_UP_TO - have) * 3; i++) {
            var (id, template, _) = pool[i % pool.Count];
            var pet = MakeHousePet(id, template, "made for a search", [.. combat, .. Shuffled(OwnPool(template), random)],
                random.Next(needed, MaxLevel(template) + 1), random, 0.7 + random.NextDouble() * 0.3,
                derby.Count > 0 ? derby : null, madeAt, $"pet.kiosk|search|{id}|{madeAt}|{i}|{random.Next()}");
            if (pet is not null) {
                made.Add(pet);
            }
        }

        if (made.Count == 0) {
            return [];
        }

        lock (s_lock) {
            if (s_file != file) {
                return [];
            }

            file.Pets.AddRange(made);
            // A new list, so a search or Browse Pets reading the old one is not disturbed.
            s_listings = [.. s_listings, .. made.Select(p => p.Listing)];
            foreach (var pet in made) {
                s_byId[pet.Listing.Parent.ItemId] = pet.Listing;
            }
        }

        Save();
        Logger.Information("Kiosk: made {0} pets showing {1} for a search.",
            Logger.Args(made.Count, string.Join(", ", combat.Concat(derby).Select(id => PetTalentRegistry.TryGetById(id, out var t) ? t.Name : id.ToString()))));

        return [.. made.Select(p => p.Listing)];
    }

    // Writes pet.kiosk again on its own thread; saves asked for while one runs are done once it ends.
    private static void Save() {
        lock (s_lock) {
            if (s_saving) {
                s_saveAgain = true;

                return;
            }

            s_saving = true;
        }

        new Thread(() => {
            while (true) {
                KioskHouseFile copy;
                lock (s_lock) {
                    s_saveAgain = false;
                    copy = s_file is null ? null
                        : new KioskHouseFile { Version = s_file.Version, MinAge = s_file.MinAge, MadeAt = s_file.MadeAt, Pets = [.. s_file.Pets] };
                }

                try {
                    if (copy is not null) {
                        File.WriteAllText(FILE + ".tmp", JsonSerializer.Serialize(copy));
                        File.Move(FILE + ".tmp", FILE, overwrite: true);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                    Logger.Warning("Kiosk: could not save {0}: {1}", Logger.Args(FILE, ex.Message));
                }

                lock (s_lock) {
                    if (!s_saveAgain) {
                        s_saving = false;

                        return;
                    }
                }
            }
        }) { IsBackground = true, Name = "pet.kiosk save" }.Start();
    }

    /// <summary>
    /// A hatched pet item of one kind with these genes, age and stats. Nothing is saved for it anywhere.
    /// </summary>
    private static WizClientObjectItem MakePet(uint templateId, PetItemBehaviorTemplate template, List<uint> genes, int level,
                                               Random random, double statShare, ulong itemId, List<uint> derbyGenes = null) {
        var behavior = new ClientPetItemBehavior {
            m_level = (byte) Math.Clamp(level, 1, Math.Max(1, MaxLevel(template))),
            m_XP = 0,
            m_hatchedTimeSecs = 0,
        };
        var pet = new WizClientObjectItem {
            m_characterId = (GID) 0UL,
            m_globalID = new GID(itemId),
            m_templateID = templateId,
            m_inactiveBehaviors = [
                behavior,
                new ClientPetNameBehavior { m_eRace = template.m_eRace, m_eGender = template.m_eGender },
            ],
        };
        PetFactory.AlignBehaviorsToTemplate(pet);

        behavior.m_allTalents = [.. genes.Where(id => id != 0 && PetTalentRegistry.TryGetById(id, out _) && !IsDevTalent(id)).Distinct().Take(GENES)];
        behavior.m_allDerbyTalents = [.. (derbyGenes ?? []).Concat(PetProgression.RollGenes(template.m_guaranteedDerbyTalents, template.m_derbyTalents, random))
            .Where(id => id != 0 && !IsDevTalent(id)).Distinct().Take(GENES)];
        var revealed = Math.Min(behavior.m_level - 1, PetHatching.MOST_REVEALED);
        behavior.m_expressedTalents = [.. behavior.m_allTalents.Take(revealed)];
        behavior.m_expressedDerbyTalents = [.. behavior.m_allDerbyTalents.Take(revealed)];
        behavior.m_maxStats = [.. (template.m_maxStats ?? []).Where(s => s is not null).Select(s => Stat(s.m_name, CappedMax(s)))];
        behavior.m_currentStats = [.. behavior.m_maxStats.Select(s => Stat(s.m_name,
            Math.Max(1, (int) Math.Round(s.m_value * Math.Clamp(statShare, 0, 1)))))];
        PetProgression.RefreshRatings(behavior);
        PetFactory.TrySetPetName(pet, WizardNameBank.RandomPetName(random));

        return pet;
    }

    private static PetStat Stat(ByteString name, int value)
        => new() { m_name = name, m_statID = StringHash.Compute(name), m_value = value };

    private static int CappedMax(PetStat stat) {
        var most = PetRules.MaxStat(stat.m_name.ToString());

        return most > 0 ? Math.Min(stat.m_value, most) : stat.m_value;
    }

    private static int MaxLevel(PetItemBehaviorTemplate template)
        => template?.m_Levels is { Count: > 0 } ? template.m_Levels.Max(info => info.m_level) : 0;

    private static List<uint> OwnPool(PetItemBehaviorTemplate template)
        => [.. PetProgression.RollGenes(template.m_guaranteedTalents, template.m_talents, Random.Shared).Where(id => !IsDevTalent(id))];

    // Words that mark a developer's pet or talent (TestPet_FastHatch, Talent-PetPower-Test1), as a whole word so
    // Tazmanian Devil and Devotion stay (Niduus, 2026-11-21: "so long as Dev pets/talents aren't in the pool").
    private static readonly System.Text.RegularExpressions.Regex s_devWord = new(
        @"(^|[^A-Za-z])(Test\w*|QA|Dev|Debug|GM|Admin|Cheat)([^a-z]|$)|^Test", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static bool IsDevWord(string name) => !string.IsNullOrEmpty(name) && s_devWord.IsMatch(name);

    /// <summary>
    /// A developer's pet kind: test, QA, dev or debug in its file name or English name.
    /// </summary>
    internal static bool IsDevPet(uint templateId)
        => CoreObjectFactory.GetCoreTemplate(templateId) is GameObjectTemplate template
            && (IsDevWord(template.m_objectName.ToString()) || IsDevWord(PetHatching.KindName(templateId)));

    /// <summary>
    /// A pet kind the game has an English name for. Kinds without one showed as blank rows at the top of Browse Pets
    /// (Niduus, 2026-11-21); the game never gives them to players.
    /// </summary>
    private static bool HasName(uint templateId)
        => CoreObjectFactory.GetCoreTemplate(templateId) is GameObjectTemplate template
            && template.m_displayName.ToString() is { Length: > 0 } key
            && !string.IsNullOrWhiteSpace(Locale.GetEnglishName(key));

    /// <summary>
    /// A developer's talent (Talent-PetPower-Test1 and the like).
    /// </summary>
    internal static bool IsDevTalent(uint id) => PetTalentRegistry.TryGetById(id, out var talent) && IsDevWord(talent.Name);

    private static List<uint> Shuffled(List<uint> ids, Random random) => [.. ids.OrderBy(_ => random.Next())];

    /// <summary>
    /// Combat talents a player can pick in Search Pet Talents: not retired, not unsearchable, not locked or test ones.
    /// </summary>
    private static List<PetTalentInfo> Searchable()
        => [.. PetTalentRegistry.All
            .Where(t => t.Kind != PetTalentKind.Derby && !t.Template.m_bRetired && !t.Template.m_unsearchable
                && !t.Name.EndsWith("-Locked", StringComparison.OrdinalIgnoreCase)
                && !IsDevWord(t.Name))
            .OrderBy(t => t.Name, StringComparer.Ordinal)];

    /// <summary>
    /// The jobs a pet can be built for, each with the talents it shows, best first: damage for one, two or three
    /// schools, critical, accuracy, pierce, resist, healing, pips and selfish stats.
    /// </summary>
    private static List<(string Name, List<uint> Talents)> Builds() {
        var builds = new List<(string, List<uint>)>();
        var dmgAll = Best("Damage-All");
        var resAll = Best("Resist-All");
        var health = Best("Health");

        void Build(string name, params IEnumerable<uint>[] parts) {
            var talents = parts.SelectMany(p => p).Where(id => id != 0).Distinct().ToList();
            if (talents.Count >= 3) {
                builds.Add((name, talents));
            }
        }

        foreach (var school in s_schools) {
            Build($"{school} damage", Best("Damage-" + school, 2), dmgAll, resAll.Take(1), health.Take(1));
            Build($"{school} critical", Best("CriticalHit-" + school, 1), Best("CriticalHit-All", 1), Best("Damage-" + school, 1), dmgAll.Take(1), resAll.Take(1));
            Build($"{school} accuracy", Best("Accuracy-" + school, 1), Best("Accuracy-All", 1), Best("Damage-" + school, 1), dmgAll.Take(1), resAll.Take(1));
        }

        for (var a = 0; a < s_schools.Length; a++) {
            for (var b = a + 1; b < s_schools.Length; b++) {
                Build($"{s_schools[a]} and {s_schools[b]} damage", Best("Damage-" + s_schools[a], 1), Best("Damage-" + s_schools[b], 1),
                    dmgAll, resAll.Take(1));
                for (var c = b + 1; c < s_schools.Length; c++) {
                    Build($"{s_schools[a]}, {s_schools[b]} and {s_schools[c]} damage", Best("Damage-" + s_schools[a], 1),
                        Best("Damage-" + s_schools[b], 1), Best("Damage-" + s_schools[c], 1), dmgAll.Take(1), resAll.Take(1));
                }
            }
        }

        Build("all-school damage", dmgAll, Best("CriticalHit-All", 1), Best("ArmorPiercing-All", 1), resAll.Take(1));
        Build("resist", resAll, health, Best("Block-All", 1), Best("StunResistance", 1));
        Build("healer", Best("LifeHealing", 1), Best("IncHealing", 1), resAll.Take(1), health, dmgAll.Take(1));
        Build("pierce", Best("ArmorPiercing-All", 2), dmgAll, resAll.Take(1));
        Build("pips", Best("Pips", 1), Best("PipConversion-All", 1), Best("Archmastery-All", 1), dmgAll.Take(1), resAll.Take(1));
        Build("selfish stats", Best("Stat01-Str", 1), Best("Stat01-Int", 1), Best("Stat01-Agi", 1), Best("Stat01-Will", 1), Best("Stat01-Pwr", 1));

        return builds;
    }

    /// <summary>
    /// Popular talents by what they do, for random mixes: damage (one school or every school, critical, pierce, accuracy,
    /// pips), defense (resist, health, block, stun resist, healing) and selfish stats.
    /// </summary>
    private sealed class Mixes {

        private readonly Dictionary<string, List<uint>> _damageBySchool = [];
        private readonly List<uint> _damageAll;
        private readonly List<uint> _defense;
        private readonly List<uint> _selfish;

        internal Mixes() {
            _damageAll = [.. Best("Damage-All"), .. Best("CriticalHit-All", 1), .. Best("ArmorPiercing-All"), .. Best("Accuracy-All", 1),
                .. Best("Pips", 1), .. Best("PipConversion-All", 1)];
            foreach (var school in s_schools) {
                _damageBySchool[school] = [.. Best("Damage-" + school), .. Best("CriticalHit-" + school, 1), .. Best("Accuracy-" + school, 1)];
            }

            _defense = [.. Best("Resist-All"), .. Best("Resist-All"), .. Best("Health"), .. Best("Block-All", 1), .. Best("StunResistance", 1),
                .. Best("LifeHealing", 1), .. Best("IncHealing", 1)];
            _selfish = [.. Best("Stat01-Str"), .. Best("Stat01-Int"), .. Best("Stat01-Agi"), .. Best("Stat01-Will"), .. Best("Stat01-Pwr"),
                .. Searchable().Where(t => t.Name.StartsWith("Talent-Stat02-", StringComparison.Ordinal)).OrderByDescending(t => t.Rank).Take(10).Select(t => t.Id)];
        }

        internal (string Name, List<uint> Talents) Mix(Random random) {
            var school = s_schools[random.Next(s_schools.Length)];
            var damage = _damageBySchool[school].Concat(_damageAll).ToList();
            var picks = new List<uint>();

            void Take(List<uint> from, int count) {
                for (var i = 0; i < count && from.Count > 0; i++) {
                    var id = from[random.Next(from.Count)];
                    if (!picks.Contains(id)) {
                        picks.Add(id);
                    }
                }
            }

            var hits = 1 + random.Next(3);
            var guards = 1 + random.Next(3);
            Take(damage, hits);
            Take(_defense, guards);
            Take(_selfish, Math.Max(1, PetHatching.MOST_REVEALED - picks.Count));
            Take(_defense, PetHatching.MOST_REVEALED - picks.Count);

            return ($"{school} mix", [.. picks.OrderBy(_ => random.Next())]);
        }

    }

    /// <summary>
    /// The best usable talents whose name starts with Talent-{stem} and goes on with a number: highest rank first, then
    /// highest number.
    /// </summary>
    private static List<uint> Best(string stem, int count = 2) {
        var prefix = "Talent-" + stem;

        return [.. Searchable()
            .Where(t => t.Name == prefix || t.Name.StartsWith(prefix, StringComparison.Ordinal)
                && t.Name.Length > prefix.Length
                && (char.IsDigit(t.Name[prefix.Length]) || (t.Name[prefix.Length] == '-' && t.Name.Length > prefix.Length + 1 && char.IsDigit(t.Name[prefix.Length + 1]))))
            .OrderByDescending(t => t.Rank).ThenByDescending(t => t.Name, StringComparer.Ordinal)
            .Take(count)
            .Select(t => t.Id)];
    }

}
