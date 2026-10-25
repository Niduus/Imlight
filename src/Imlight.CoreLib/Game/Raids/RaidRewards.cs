using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.Raids;

/*
 * What a raid gives when it is won. The lists are the boss's drops on its saved Central page
 * (central-pages/verify-2026-10-24/The Nullity.html: "All 12 Wizards in the Raid receive the drops, not just the ones
 * within the battle"), matched to the game's items by name with the Wizard404 export; the gear is the raid's
 * "Drop-GR01-..." pieces, not the crafted ones. The page does not say how many of each a wizard gets, so every wizard
 * in the raid gets one gear piece of their own school, one jewel, one treasure card, Fire Blossoms and spellements,
 * and maybe the raid's pet of their school ([Raids] PetChance).
 */
internal static class RaidRewards {

    internal static int PetChance => Math.Clamp(ConfigurationManager.GetValue("Raids.PetChance", 10), 0, 100);
    internal static int Spellements => Math.Clamp(ConfigurationManager.GetValue("Raids.Spellements", 2), 0, 20);
    internal static int Reagents => Math.Clamp(ConfigurationManager.GetValue("Raids.Reagents", 3), 0, 99);

    /// <summary>
    /// A raid's drops: its gear and pet for each school, and what any school can get.
    /// </summary>
    internal sealed record Loot(IReadOnlyDictionary<MagicSchool, uint[]> Gear, IReadOnlyDictionary<MagicSchool, uint> Pets,
                                uint[] Jewels, uint[] TreasureCards, uint[] Reagents, uint[] Spellements);

    // The Nullity's gear: per school the Conical, Robe and Boots of The Nullity and of The Void, and Nullity's and
    // The Void's Blade (Drop-GR01-{Hats,Robe,Shoes,Wands}-L150-<school>-013/010-01).
    private static uint[] VoidGear(uint offset)
        => [1569098 + offset, 1569119 + offset, 1569105 + offset, 1569126 + offset,
            1569112 + offset, 1569133 + offset, 1569196 + offset, 1569203 + offset];

    private static readonly Loot VoraciousVoid = new(
        new Dictionary<MagicSchool, uint[]> {
            [MagicSchool.Balance] = VoidGear(0),
            [MagicSchool.Death] = VoidGear(1),
            [MagicSchool.Fire] = VoidGear(2),
            [MagicSchool.Ice] = VoidGear(3),
            [MagicSchool.Life] = VoidGear(4),
            [MagicSchool.Myth] = VoidGear(5),
            [MagicSchool.Storm] = VoidGear(6),
        },
        // The Null Dragons (PP_Raid_Dragon_*): Steady is the Balance one.
        new Dictionary<MagicSchool, uint> {
            [MagicSchool.Balance] = 1552628,
            [MagicSchool.Death] = 1552616,
            [MagicSchool.Fire] = 1552617,
            [MagicSchool.Ice] = 1552618,
            [MagicSchool.Life] = 1552619,
            [MagicSchool.Myth] = 1552620,
            [MagicSchool.Storm] = 1552621,
        },
        // Amazing Accurate Jade +15%, Amazing Damage Jade/Onyx/Peridot +32, Amazing Mana Opal +58, Buck Gordon Ruby,
        // Dog Tracy Amethyst.
        [1535704, 1535657, 1535659, 1535662, 1535678, 1537693, 1537695],
        // Azoth (GuildReagent01_TC), Dark Sprite TC, Sprite Swarm TC (SpriteSwarm_Trainable_TC).
        [192242813, 658115525, 279110423],
        // Fire Blossom.
        [106968],
        // The page's spellements (Shard-*-01).
        [1507393, 1552022, 1530081, 1552004, 1494696, 1528497, 1530094, 1552065, 1552049, 1494689, 1552005, 1552047,
         1494692, 1528509, 1507373, 1507399, 1552061, 1552053, 1494695, 1528499, 1552056, 1494688, 1494698, 1528498,
         1552059, 1552048, 1507398, 1528504, 1552058, 1507388, 1507378, 1528514, 1494691, 1552054, 1552057, 1492526,
         1530093, 1552006, 1552046, 1552060, 1494697, 1552051, 1552062, 1528004]);

    private static readonly Dictionary<string, Loot> s_loot = new(StringComparer.OrdinalIgnoreCase) {
        ["Raids/Raid_DS_Knowledge"] = VoraciousVoid,
    };

    internal static bool Has(string zonePath) => zonePath is not null && s_loot.ContainsKey(zonePath);

    /// <summary>
    /// Rolls a wizard's drops for winning the raid in <paramref name="zonePath"/>, grants them and shows the loot popup.
    /// </summary>
    internal static void Grant(IActorRef playerActor, Wizard wizard, string zonePath) {
        if (zonePath is null || !s_loot.TryGetValue(zonePath, out var loot)) {
            return;
        }

        var school = wizard.MagicSchoolBehavior.MagicSchool;
        if (!loot.Gear.ContainsKey(school)) {
            school = MagicSchool.Balance;
        }

        var rolled = new DropTableResult { DropTableId = "Raid:" + zonePath };
        void Add(uint id, int quantity = 1) => rolled.Items.Add(new DropItemResult { ItemId = id.ToString(), Quantity = quantity });

        Add(Pick(loot.Gear[school]));
        if (loot.Jewels.Length > 0) {
            Add(Pick(loot.Jewels));
        }

        if (loot.TreasureCards.Length > 0) {
            Add(Pick(loot.TreasureCards));
        }

        if (loot.Reagents.Length > 0 && Reagents > 0) {
            Add(Pick(loot.Reagents), Reagents);
        }

        foreach (var shard in loot.Spellements.OrderBy(_ => Random.Shared.Next()).Take(Spellements)) {
            Add(shard);
        }

        if (loot.Pets.TryGetValue(school, out var pet) && Random.Shared.Next(100) < PetChance) {
            Add(pet);
        }

        Logger.Information("Raids | {0} gets the drops of {1}: {2}.",
            Logger.Args(wizard.CharId, zonePath, string.Join(", ", rolled.Items.Select(i => i.ItemId))));
        LootGranter.GrantAndDisplay(playerActor, wizard, rolled);
    }

    private static uint Pick(uint[] ids) => ids[Random.Shared.Next(ids.Length)];

}
