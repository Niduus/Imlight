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
            // Myth, or the school his group asked for (TrueForm.School; back to Myth when the group ends).
            var school = TrueForm.School;
            var style = TrueForm.StyleNow(school);
            if (wizard.MagicSchoolBehavior.MagicSchool != school) {
                wizard.SetSchool(school);
                Logger.Information("True Form: now a {0} wizard ({1}).", Logger.Args(school, style));
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
            // His universal setup scores every piece, jewel and pet on what helps every school ("" = every school only).
            var statSchool = TrueForm.Universal ? "" : school.ToString();
            Dress(wizard, top, school.ToString(), style, statSchool);
            Stitch(wizard);
            GivePet(wizard, statSchool, style);
            TrueFormJewels.Fill(wizard, statSchool, style);
            TrueFormSpells.Give(wizard, top, school.ToString(), style);
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

    // His pet's talents as players pick them (BotPetTalents; Niduus, 2026-11-19: "People usually like to go Dealer"): Myth-Dealer,
    // Pain-Giver, Spell-Proof, Spell-Defying and Myth-Giver; the rest, best first, are for his pet jewel (TrueFormJewels).
    internal static string[] PetTalents(Wizard wizard, string school, BotStyle style) {
        // Accuracy or pips already too high from his gear: universal accuracy talents instead (BotPetTalents).
        var need = BotJewelPicker.For(wizard, school, style);
        return BotPetTalents.For(school, style, need.AccuracyFull, need.PipsFull);
    }

    private const int PET_TALENTS = 5;

    // A Myth pet (the one with the highest stats a player can get, not Crowns-only or retired), Ultra, its stats and
    // happiness at their maximum, with his role's talents. Put on when he has none, or when his is not at Ultra yet.
    private static void GivePet(Wizard wizard, string school, BotStyle style) {
        try {
            var talents = PetTalents(wizard, school, style);
            // The pet body (and a mount of its set) that fits how he plays (Niduus, 2026-11-19: "Have the pets that TF
            // uses be based on the style of play he will need in terms of body").
            var pick = BestPetBody(school, style);
            if (pick?.Mount is { } mount) {
                GiveMount(wizard, mount);
            }

            bool Fits(WizClientObjectItem i) => pick is not null ? (uint) i.m_templateID == pick.Pet : IsSchool((uint) i.m_templateID, school);
            if (wizard.EquipmentBehavior?.GetItemInSlot(EquipmentSlotType.Pet) is { } worn && PetProgress.GetBehavior(worn) is { } had
                && had.m_level >= PetProgress.MAX_LEVEL && Fits(worn)) {
                ShapeTalents(wizard, worn, talents);
                return;
            }

            // That pet from his backpack (each loadout's pet is kept there), else a new one.
            var kept = wizard.InventoryBehavior?.Items?.FirstOrDefault(i => PetProgress.GetBehavior(i) is { } b && b.m_level >= PetProgress.MAX_LEVEL
                                                                             && Fits(i));
            if (kept is not null) {
                ShapeTalents(wizard, kept, talents);
                if (wizard.InventoryToEquipmentTransfer(kept.m_globalID.Full, out _, out _)) {
                    Logger.Information("True Form: wears his {0} pet again.", Logger.Args(school));
                }

                return;
            }

            if ((pick?.Pet ?? BestPet(school.Length == 0 ? TrueForm.School.ToString() : school)) is not { } template) {
                Logger.Warning("True Form: no {0} pet was found to give him.", Logger.Args(school));
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
            var names = ShapeTalents(wizard, pet, talents);
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

    // A real pet has five talents, and its jewel is the sixth (Niduus, 2026-11-19: "pets naturally have 5 stats, + jewel as
    // 6th"): the talents a pet is hatched with (his showed Maycast Disarm and Maycast Mythspear on top of his five) are taken
    // off, so he has exactly his role's five, the first of PetTalents the game has. His jewel adds the sixth (TrueFormJewels).
    private static System.Collections.Generic.List<string> ShapeTalents(Wizard wizard, WizClientObjectItem pet, string[] petTalents) {
        var names = new System.Collections.Generic.List<string>();
        var behavior = PetProgress.GetBehavior(pet);
        if (behavior is null) {
            return names;
        }

        var wanted = new System.Collections.Generic.List<PetTalentBaseTemplate>();
        foreach (var name in petTalents) {
            if (wanted.Count >= PET_TALENTS) {
                break;
            }

            if (TalentNamed(name) is { } talent) {
                wanted.Add(talent);
                names.Add($"{name} [{(string) talent.m_talentName}]");
            }
        }

        var wantedIds = wanted.Select(t => PetData.TalentId(t.m_talentName)).ToHashSet();
        var had = behavior.m_expressedTalents.ToList();
        if (had.Count == wantedIds.Count && had.All(wantedIds.Contains)) {
            if (SetStats(pet)) {
                PetCare.Finish(wizard, pet, null, _ => { });
            }

            return names;
        }

        foreach (var id in had.Where(id => !wantedIds.Contains(id))) {
            if (PetData.GetTalent(id) is { } extra && PetProgress.RemoveTalent(pet, extra)) {
                Logger.Information("True Form: his pet's talent {0} is taken off (five talents, the jewel is the sixth).",
                    Logger.Args(Locale.GetEnglishName(extra.m_displayName)));
            }
            else {
                behavior.m_expressedTalents.Remove(id);
            }
        }

        foreach (var talent in wanted) {
            PetProgress.AddTalent(pet, talent);
        }

        PetProgress.MaxStats(pet);
        SetStats(pet);
        PetCare.Finish(wizard, pet, null, _ => { });
        return names;
    }

    // Every stat at 260 (Niduus, 2026-11-19: "Have the pet stats for the pet be 260 across the board"); true when one changed.
    private static bool SetStats(WizClientObjectItem pet) {
        var behavior = PetProgress.GetBehavior(pet);
        var changed = false;
        foreach (var name in BotPetTalents.Stats) {
            var now = behavior?.m_currentStats?.FirstOrDefault(s => string.Equals((string) s.m_name, name, StringComparison.OrdinalIgnoreCase))?.m_value;
            if (now == BotPetTalents.PET_STAT) {
                continue;
            }

            if (PetProgress.SetStat(pet, name, BotPetTalents.PET_STAT.ToString(), out var shown) is { } why) {
                Logger.Debug("True Form: his pet's {0} could not be set: {1}", Logger.Args(name, why));
                continue;
            }

            changed = true;
            Logger.Information("True Form: his pet's {0}.", Logger.Args(shown));
        }

        return changed;
    }

    // A talent by the name the game shows. A school's talent ("Ice-Dealer") must be that school's own: one whose game name
    // holds the school (Niduus, 2026-11-19: in Ice gear his pet still showed Myth damage talents), found by its shown name,
    // else by the shown name's end ("-Dealer") among that school's talents.
    private static PetTalentBaseTemplate TalentNamed(string name) {
        var talents = PetData.AllTalents.Where(t => t is not PetDerbyTalentTemplate).ToList();
        string Shown(PetTalentBaseTemplate t) => Locale.GetEnglishName(t.m_displayName) ?? "";
        var dash = name.IndexOf('-');
        var school = dash > 0 ? name[..dash] : "";
        if (school.Length > 0 && Enum.TryParse<MagicSchool>(school, true, out _)) {
            bool Own(PetTalentBaseTemplate t) => ((string) t.m_talentName ?? "").Contains(school, StringComparison.OrdinalIgnoreCase);
            return talents.FirstOrDefault(t => Own(t) && string.Equals(Shown(t), name, StringComparison.OrdinalIgnoreCase))
                   ?? talents.FirstOrDefault(t => Own(t) && Shown(t).EndsWith(name[dash..], StringComparison.OrdinalIgnoreCase)
                                                   && !Shown(t).Contains(' '))
                   ?? talents.FirstOrDefault(t => string.Equals(Shown(t), name, StringComparison.OrdinalIgnoreCase)
                                                  && !Enum.GetNames<MagicSchool>().Any(o => !o.Equals(school, StringComparison.OrdinalIgnoreCase) && o != "None"
                                                                                          && ((string) t.m_talentName ?? "").Contains(o, StringComparison.OrdinalIgnoreCase)));
        }

        return talents.FirstOrDefault(t => string.Equals(Shown(t), name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSchool(uint template, string school)
        => string.Equals((string) (CoreObjectFactory.GetCoreTemplate(template) as WizItemTemplate)?.m_school, school, StringComparison.OrdinalIgnoreCase);

    internal sealed record PetPick(uint Pet, uint? Mount, double Score, string Why);

    // Every pet body worth having, scored for his school and play style: the body's own stats, plus, when a
    // mount of the same set exists, the set bonus tiers that pet and mount reach together (the fossil pet and mount's
    // damage; TrueFormSetBonus puts them on). Ties: his own school's pet, one players can get, the highest stats.
    internal static PetPick BestPetBody(string school, BotStyle style) {
        try {
            var obtainable = ItemSources.ObtainableIds();
            var mounts = (TrueFormTopGear.SetPieces ?? []).Where(p => p.Mount && Allowed(p.Id, out var name)
                                                                       && !name.Contains("Book", StringComparison.OrdinalIgnoreCase))
                .GroupBy(p => p.Set).ToDictionary(g => g.Key, g => g.Select(p => p.Id).ToList());
            var best = PetFactory.AllPetTemplates.Keys
                .Select(id => (Id: id, Item: CoreObjectFactory.GetCoreTemplate(id) as WizItemTemplate, Pet: PetFactory.GetPetItemBehaviorTemplate(id)))
                .Where(p => p.Item is not null && p.Pet is not null && Allowed(p.Id, out _))
                .Select(p => {
                    // (A pet's cards do not count: his hand comes from his own spells, TrueFormPool.)
                    var body = BotGear.ScoreStats(BotGear.StatsOf(p.Item), school, style);
                    uint? mount = null;
                    var set = 0.0;
                    var why = "";
                    if (p.Item.m_itemSetBonusTemplateID != 0 && mounts.TryGetValue((uint) p.Item.m_itemSetBonusTemplateID, out var ids)) {
                        foreach (var m in ids) {
                            var mountItem = CoreObjectFactory.GetCoreTemplate(m) as WizItemTemplate;
                            var together = TrueFormSetBonus.Reached([p.Item, mountItem]).Sum(r => BotGear.ScoreStats(BotGear.StatsOfEffects(r.Tier.m_equipEffectsGranted), school, style))
                                           + BotGear.ScoreStats(BotGear.StatsOf(mountItem), school, style);
                            if (together > set) {
                                set = together;
                                mount = m;
                            }
                        }

                        if (mount is not null) {
                            why = $" with its set mount {Locale.GetEnglishName((string) (CoreObjectFactory.GetCoreTemplate(mount.Value) as WizItemTemplate)?.m_displayName ?? "")}";
                        }
                    }

                    return new {
                        p.Id, Mount = mount, Score = body + set, Why = why,
                        Own = IsSchool(p.Id, school), Got = obtainable.Contains(p.Id),
                        Stats = (p.Pet.m_maxStats ?? []).Sum(s => (long) s.m_value),
                    };
                })
                .OrderByDescending(p => Math.Round(p.Score, 1)).ThenByDescending(p => p.Own).ThenByDescending(p => p.Got)
                .ThenByDescending(p => p.Stats).ThenBy(p => p.Id)
                .FirstOrDefault();
            if (best is null) {
                return null;
            }

            var shown = Locale.GetEnglishName((string) (CoreObjectFactory.GetCoreTemplate(best.Id) as WizItemTemplate)?.m_displayName ?? "");
            Logger.Information("True Form: pet body for {0} {1}: {2}{3} (worth {4:0.#}).", Logger.Args(school, style, shown, best.Why, best.Score));
            return new PetPick(best.Id, best.Mount, best.Score, best.Why);
        }
        catch (Exception e) {
            Logger.Warning("True Form: could not pick his pet body: {0}", Logger.Args(e.Message));
            return null;
        }
    }

    // A pet or mount he may have: not retired, not a dev item, not Crowns-only, with a shown name.
    private static bool Allowed(uint id, out string name) {
        var item = CoreObjectFactory.GetCoreTemplate(id) as WizItemTemplate;
        name = Locale.GetEnglishName((string) item?.m_displayName ?? "") ?? "";
        return item is not null && !string.IsNullOrWhiteSpace(name)
               && !(item.m_adjectiveList ?? []).Any(a => (string) a is "FLAG_CrownsOnly" or "FLAG_Retired" or "FLAG_DevItem");
    }

    // The set mount for his pet: worn already, from his backpack, or made.
    private static void GiveMount(Wizard wizard, uint mount) {
        try {
            if (wizard.EquipmentBehavior?.GetItemInSlot(EquipmentSlotType.Mount) is { } worn && (uint) worn.m_templateID == mount) {
                return;
            }

            var kept = wizard.InventoryBehavior?.Items?.FirstOrDefault(i => (uint) i.m_templateID == mount);
            if (kept is null && (!wizard.TryCreateItem(mount, out kept) || !wizard.AddInitializedItemToInventory(kept, ignoreLimit: true))) {
                Logger.Warning("True Form: could not make his set mount ({0}).", Logger.Args(mount));
                return;
            }

            if (wizard.InventoryToEquipmentTransfer(kept.m_globalID.Full, out _, out _)) {
                Logger.Information("True Form: rides his set mount {0}.",
                    Logger.Args(Locale.GetEnglishName((string) (CoreObjectFactory.GetCoreTemplate(mount) as WizItemTemplate)?.m_displayName ?? "")));
            }
        }
        catch (Exception e) {
            Logger.Warning("True Form: could not give him his set mount: {0}", Logger.Args(e.Message));
        }
    }

    private static uint? BestPet(string school) {
        var obtainable = ItemSources.ObtainableIds();
        var myth = PetFactory.AllPetTemplates.Keys
            .Select(id => (Id: id, Item: CoreObjectFactory.GetCoreTemplate(id) as WizItemTemplate, Pet: PetFactory.GetPetItemBehaviorTemplate(id)))
            .Where(p => p.Item is not null && p.Pet is not null && IsSchool(p.Id, school)
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
    private static void Dress(Wizard wizard, int level, string school, BotStyle style, string statSchool) {
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
        // The server's drop and shop lists have no Ahmarra gear yet, so the level 190 pieces come from the game's own files
        // (TrueFormTopGear; Niduus, 2026-11-19: "curious as to why not 190 gear"). Read once, up to a few minutes the first time.
        var top = TrueFormTopGear.Pieces;
        for (var i = 0; i < 480 && top is null; i++) {
            System.Threading.Thread.Sleep(500);
            top = TrueFormTopGear.Pieces;
        }

        var pool = BotGear.Catalog.Concat(top ?? []).DistinctBy(p => p.Id).ToList();
        var topOf = pool.Where(p => Allowed(p) && BotGear.CanWear(p, school, level) && BotGear.Score(p, statSchool, style) > 0)
            .GroupBy(p => p.Slot).ToDictionary(g => g.Key, g => g.Max(p => p.Level));
        var best = BotGear.BestFor(school, level, style, p => Allowed(p) && (!topOf.TryGetValue(p.Slot, out var least) || p.Level >= least),
            extra: top, scoreSchool: statSchool);
        var outfit = string.Join(",", best.OrderBy(b => b.Key).Select(b => b.Value.Id));
        var marker = Path.Combine(UltimusSeat.TrueForm.Folder, DRESSED_FILE);
        outfit = $"{school}{(statSchool.Length == 0 ? " universal" : "")} {style}: {outfit}";
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

                // The piece from his backpack when he has it (another school's loadout, already stitched and jeweled), else a new one.
                var item = wizard.InventoryBehavior?.Items?.Where(i => (uint) Math.Min((ulong) i.m_templateID, uint.MaxValue) == piece.Id)
                    .OrderByDescending(i => i.m_displayID != 0).FirstOrDefault();
                if (item is null && (!wizard.TryCreateItem(piece.Id, out item) || !wizard.AddInitializedItemToInventory(item, ignoreLimit: true))) {
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

        Logger.Information("True Form: dressed in the best level {0} {1} gear for a {2} ({3} pieces put on).", Logger.Args(level, school, style, put));
    }

}
