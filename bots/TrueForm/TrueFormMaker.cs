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
using System.IO;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.ItemSafety;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Game.Seamstress;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Character;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Makes True Form what Niduus asked for (2026-11-19: "He is to be max level, Myth with the best 190 Myth gear statistically
/// speaking in the game"), before he logs in: the server's top level, Myth, Shadow Magic open (for the shadow spells), the
/// title "True Form", and per slot the best piece of gear a Myth wizard of his level can get in the real game, by its stats
/// (BotGear: every hat, robe, boots, wand, athame, amulet, ring and deck a shop, quest or boss gives; never Crowns-only,
/// retired, dev or test gear). His gear is scored for a wizard who hits, blades and keeps the team up (the Blading set's
/// weights: damage, pierce and resist all count). Done again whenever the top level changes or a better piece appears.
/// </summary>
internal static class TrueFormMaker {

    /// <summary>The title shown under his name.</summary>
    internal const string TITLE = "True Form";

    private const string DRESSED_FILE = "dressed.txt";

    /// <summary>Makes him ready (he must be logged out); writes what it did to the server log.</summary>
    internal static void Prepare(ulong charId) {
        var wizard = WizardCollection.GetCharacter(charId);
        if (wizard is null) {
            Logger.Warning("True Form: his wizard {0} was not found to make ready.", Logger.Args(charId));
            return;
        }

        try {
            if (wizard.MagicSchoolBehavior.MagicSchool != MagicSchool.Myth) {
                wizard.SetSchool(MagicSchool.Myth);
            }

            var top = (byte) Math.Clamp(MagicLevelsConfig.MaxLevel, 1, 255);
            if (wizard.MagicSchoolBehavior.Level != top) {
                wizard.SetLevel(top);
                wizard.FitExperienceToLevel();
                Logger.Information("True Form: made level {0}.", Logger.Args(top));
            }

            if (wizard.GameStats is { } stats) {
                if (!stats.m_shadowMagicUnlocked || stats.m_shadowPipSlots < 3) {
                    wizard.UnlockShadowMagic(3);
                }

                // Full health and mana to start with.
                wizard.UpdateHealth(Math.Max(1, stats.m_baseHitpoints + stats.m_bonusHitpoints));
                wizard.UpdateMana(Math.Max(1, stats.m_baseMana + stats.m_bonusMana));
            }

            if ((string) wizard.PlayerNameBehavior?.BadgeTitle != TITLE) {
                wizard.SetBadgeOverride(TITLE);
            }

            MakeMale(wizard);
            Dress(wizard, top);
            Stitch(wizard);
            GivePet(wizard);
            TrueFormJewels.Fill(wizard);
        }
        catch (Exception e) {
            Logger.Warning("True Form: could not make him ready: {0}", Logger.Args(e));
        }
    }

    private const string LOOK_FILE = "look.txt";
    private const string BASE_LOOK = "male base";

    // Niduus, 2026-11-19 (after his first test): "Base MALE Character". The character maker's first choice of every part
    // (index 0), no face paint or accessory; eyebrow and lip colors as they are. Done once (look.txt), so a look Niduus
    // gives him later at the Magic Mirror stays.
    private static void MakeMale(Wizard wizard) {
        var marker = Path.Combine(UltimusSeat.TrueForm.Folder, LOOK_FILE);
        if (wizard.WizardAvatar is not { } avatar || (File.Exists(marker) && File.ReadAllText(marker).Trim() == BASE_LOOK)) {
            return;
        }

        avatar.m_eGender = eGender.Male;
        MagicMirror.WriteToAvatar(avatar, [0, 0, 0, 0, 0, 0, 0, 0, 0, -1, -1]);
        wizard.MirrorLook = null;
        wizard.MirrorStartLook = null;
        WizardCollection.UpdateCharacterMirrorLook(wizard);
        try {
            Directory.CreateDirectory(UltimusSeat.TrueForm.Folder);
            File.WriteAllText(marker, BASE_LOOK);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            Logger.Debug("True Form: could not note his look: {0}", Logger.Args(e.Message));
        }

        Logger.Information("True Form: made a male wizard with the base look.");
    }

    // His pet's talents (Niduus: "Myth pet stats, all of appropriate stats he would need to complete his role"): he hits,
    // blades and keeps the team up, so damage, Myth damage, resist twice and a team heal. The first five the game has.
    internal static readonly string[] PetTalents = [
        "Pain-Giver", "Myth-Dealer", "Spell-Proof", "Spell-Defying", "Unicorn", "Pain-Bringer", "Myth-Giver", "Spell-Ward", "Fairy",
    ];

    private const int PET_TALENTS = 5;

    // A Myth pet (the one with the highest stats a player can get, not Crowns-only or retired), Ultra, its stats and
    // happiness at their maximum, with his role's talents. Put on when he has none, or when his is not at Ultra yet.
    private static void GivePet(Wizard wizard) {
        try {
            if (wizard.EquipmentBehavior?.GetItemInSlot(EquipmentSlotType.Pet) is { } worn && PetProgress.GetBehavior(worn) is { } had
                && had.m_level >= PetProgress.MAX_LEVEL && IsMyth((uint) worn.m_templateID)) {
                return;
            }

            if (BestMythPet() is not { } template) {
                Logger.Warning("True Form: no Myth pet was found to give him.");
                return;
            }

            if (!wizard.TryCreateItem(template, out var pet) || !wizard.AddInitializedItemToInventory(pet, ignoreLimit: true)) {
                Logger.Warning("True Form: could not make his pet ({0}).", Logger.Args(template));
                return;
            }

            PetProgress.EnsureInitialized(pet);
            PetProgress.SetLevel(pet, PetProgress.MAX_LEVEL);
            PetProgress.MaxStats(pet);
            PetProgress.SetStat(pet, "happiness", "max", out _);
            var given = 0;
            var names = new System.Collections.Generic.List<string>();
            foreach (var name in PetTalents) {
                if (given >= PET_TALENTS) {
                    break;
                }

                var talent = PetData.AllTalents.FirstOrDefault(t => t is not PetDerbyTalentTemplate
                                                                   && string.Equals(Locale.GetEnglishName(t.m_displayName), name, StringComparison.OrdinalIgnoreCase));
                if (talent is not null && PetProgress.AddTalent(pet, talent)) {
                    given++;
                    names.Add(name);
                }
            }

            PetProgress.MaxStats(pet);
            PetCare.Finish(wizard, pet, null, _ => { });
            var shown = Locale.GetEnglishName((string) (CoreObjectFactory.GetCoreTemplate(template) as WizItemTemplate)?.m_displayName ?? "");
            if (wizard.InventoryToEquipmentTransfer(pet.m_globalID.Full, out _, out _)) {
                Logger.Information("True Form: wears his pet {0} (Ultra, talents: {1}).", Logger.Args(shown, names.Count > 0 ? string.Join(", ", names) : "none found"));
            }
            else {
                Logger.Warning("True Form: his pet {0} is in his backpack but could not be put on.", Logger.Args(shown));
            }
        }
        catch (Exception e) {
            Logger.Warning("True Form: could not give him his pet: {0}", Logger.Args(e.Message));
        }
    }

    private static bool IsMyth(uint template)
        => string.Equals((string) (CoreObjectFactory.GetCoreTemplate(template) as WizItemTemplate)?.m_school, "Myth", StringComparison.OrdinalIgnoreCase);

    private static uint? BestMythPet() {
        var obtainable = ItemSources.ObtainableIds();
        var myth = PetFactory.AllPetTemplates.Keys
            .Select(id => (Id: id, Item: CoreObjectFactory.GetCoreTemplate(id) as WizItemTemplate, Pet: PetFactory.GetPetItemBehaviorTemplate(id)))
            .Where(p => p.Item is not null && p.Pet is not null && IsMyth(p.Id)
                        && !(p.Item.m_adjectiveList ?? []).Any(a => (string) a is "FLAG_CrownsOnly" or "FLAG_Retired" or "FLAG_DevItem")
                        && !string.IsNullOrWhiteSpace(Locale.GetEnglishName((string) p.Item.m_displayName ?? "")))
            .Select(p => (p.Id, Got: obtainable.Contains(p.Id), Stats: (p.Pet.m_maxStats ?? []).Sum(s => (long) s.m_value)))
            .OrderByDescending(p => p.Got).ThenByDescending(p => p.Stats).ThenBy(p => p.Id)
            .ToList();
        return myth.Count > 0 ? myth[0].Id : null;
    }

    // Niduus, 2026-11-19: "Character on my account (1234) has a character called "Myth Stitch" that characters Hat, Robe,
    // Boots and Wand for gear stitch". His worn hat, robe, boots and wand take the look (and dyes) of what that character
    // wears, by the Seamstress's own rules, and keep their 190 stats and jewels. That character is only read, never changed.
    // [TrueForm] StitchAccount / StitchCharacter name it (live). Done again whenever he is dressed in a new piece or the
    // look changes.
    private static readonly EquipmentSlotType[] s_stitchSlots = [
        EquipmentSlotType.Hat, EquipmentSlotType.Robe, EquipmentSlotType.Shoes, EquipmentSlotType.Weapon,
    ];

    private static void Stitch(Wizard wizard) {
        try {
            var accountName = ConfigurationManager.GetValue("TrueForm.StitchAccount", "1234");
            var characterName = ConfigurationManager.GetValue("TrueForm.StitchCharacter", "Myth Stitch");
            if (string.IsNullOrWhiteSpace(accountName) || string.IsNullOrWhiteSpace(characterName)) {
                return;
            }

            var account = AccountCollection.GetAccount(accountName);
            var model = account?.CharacterIds.Select(WizardCollection.GetCharacter)
                .FirstOrDefault(c => c is not null && string.Equals(c.PlayerNameBehavior?.GetWizardName()?.Trim(), characterName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (model is null) {
                Logger.Warning("True Form: the stitch character \"{0}\" was not found on account {1}; he keeps his own look.", Logger.Args(characterName, accountName));
                return;
            }

            var done = 0;
            foreach (var slot in s_stitchSlots) {
                var look = model.EquipmentBehavior?.GetItemInSlot(slot);
                var mine = wizard.EquipmentBehavior?.GetItemInSlot(slot);
                if (look is null || mine is null) {
                    continue;
                }

                var lookId = ItemHelper.GetDisplayTemplateId(look);
                if (ItemHelper.GetDisplayTemplateId(mine) == lookId && mine.m_primaryColor == look.m_primaryColor && mine.m_secondaryColor == look.m_secondaryColor) {
                    continue;
                }

                if (!StitchRules.TryPlanStitch(look, mine, out var plan, out var reason)) {
                    Logger.Warning("True Form: his {0} could not take the look of {1}'s: {2}.", Logger.Args(slot, characterName, reason));
                    continue;
                }

                if (!StitchedItemFactory.TryCreateStitched(wizard, plan, out var stitched)) {
                    Logger.Warning("True Form: his stitched {0} could not be made.", Logger.Args(slot));
                    continue;
                }

                StitchedItemFactory.CopySockets(mine, stitched);
                if (!wizard.AddInitializedItemToInventory(stitched, ignoreLimit: true)) {
                    Logger.Warning("True Form: his stitched {0} could not go in his backpack.", Logger.Args(slot));
                    continue;
                }

                var old = mine.m_globalID.Full;
                if (!wizard.InventoryToEquipmentTransfer(stitched.m_globalID.Full, out _, out _)) {
                    wizard.RemoveItemFromInventory(stitched.m_globalID.Full, ItemRecordKind.Removed, "True Form stitch not worn");
                    Logger.Warning("True Form: his stitched {0} could not be put on.", Logger.Args(slot));
                    continue;
                }

                // The plain piece the stitch used up.
                wizard.RemoveItemFromInventory(old, ItemRecordKind.Removed, "used up by True Form's stitch");
                done++;
                Logger.Information("True Form: his {0} now looks like {1}'s ({2}).", Logger.Args(slot, characterName,
                    BotJewels.NameOf(CoreObjectFactory.GetCoreTemplate(lookId) as WizItemTemplate)));
            }

            if (done > 0) {
                Logger.Information("True Form: {0} pieces stitched to {1}'s look.", Logger.Args(done, characterName));
            }
        }
        catch (Exception e) {
            Logger.Warning("True Form: could not stitch his look: {0}", Logger.Args(e.Message));
        }
    }

    // The best gear per slot for a Myth wizard of his level; put on when what he wears differs.
    private static void Dress(Wizard wizard, int level) {
        // The gear list is read on the first ask, in the background: up to a minute for it.
        for (var i = 0; i < 120 && BotGear.Catalog.Count == 0; i++) {
            System.Threading.Thread.Sleep(500);
        }

        if (BotGear.Catalog.Count == 0) {
            Logger.Warning("True Form: the gear list could not be read; he keeps what he wears.");
            return;
        }

        // Only the top level's gear in each slot (Niduus, 2026-11-19: "TF is using non-190 gear"): a lower piece with bigger
        // stats than a level 190 one used to win, as the 190 piece's sockets were empty. Now the 190 piece is worn and its
        // sockets are filled (TrueFormJewels). A slot with no piece at his level wears the highest level there is.
        static bool Allowed(BotGear.Piece p) => !p.CrownsOnly && !p.Retired && !BotGear.Forbidden(p.Id) && !BotGear.PartlyRead(p.Id);
        var topOf = BotGear.Catalog.Where(p => Allowed(p) && BotGear.CanWear(p, "Myth", level) && BotGear.Score(p, "Myth", BotStyle.Backup) > 0)
            .GroupBy(p => p.Slot).ToDictionary(g => g.Key, g => g.Max(p => p.Level));
        var best = BotGear.BestFor("Myth", level, BotStyle.Backup, p => Allowed(p) && (!topOf.TryGetValue(p.Slot, out var top) || p.Level >= top));
        var outfit = string.Join(",", best.OrderBy(b => b.Key).Select(b => b.Value.Id));
        var marker = Path.Combine(UltimusSeat.TrueForm.Folder, DRESSED_FILE);
        var worn = BotGear.Slots.Select(s => (Slot: s, Item: wizard.EquipmentBehavior?.GetItemInSlot(s))).ToList();
        var wearsIt = best.All(b => worn.FirstOrDefault(w => w.Slot == b.Key).Item is { } item && (uint) Math.Min((ulong) item.m_templateID, uint.MaxValue) == b.Value.Id);
        if (wearsIt && File.Exists(marker) && File.ReadAllText(marker).Trim() == outfit) {
            return;
        }

        var put = 0;
        foreach (var (slot, piece) in best) {
            try {
                if (wizard.EquipmentBehavior?.GetItemInSlot(slot) is { } on && (uint) Math.Min((ulong) on.m_templateID, uint.MaxValue) == piece.Id) {
                    continue;
                }

                if (!wizard.TryCreateItem(piece.Id, out var item) || !wizard.AddInitializedItemToInventory(item, ignoreLimit: true)) {
                    Logger.Warning("True Form: could not get {0} ({1}).", Logger.Args(piece.Name, slot));
                    continue;
                }

                if (wizard.InventoryToEquipmentTransfer(item.m_globalID.Full, out _, out _)) {
                    put++;
                    Logger.Information("True Form: wears {0} ({1}, level {2}).", Logger.Args(piece.Name, slot, piece.Level));
                }
            }
            catch (Exception e) {
                Logger.Warning("True Form: could not put on {0}: {1}", Logger.Args(piece.Name, e.Message));
            }
        }

        try {
            Directory.CreateDirectory(UltimusSeat.TrueForm.Folder);
            File.WriteAllText(marker, outfit);
            File.WriteAllLines(Path.Combine(UltimusSeat.TrueForm.Folder, "gear.txt"), BotGear.Describe(wizard));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            Logger.Debug("True Form: could not write his gear list: {0}", Logger.Args(e.Message));
        }

        Logger.Information("True Form: dressed in the best level {0} Myth gear ({1} pieces put on).", Logger.Args(level, put));
    }

}
