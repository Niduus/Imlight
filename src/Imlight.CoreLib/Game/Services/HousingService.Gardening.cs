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
 * GARDENING
 * ========================================================================
 *
 * PURPOSE:
 * The garden spells, planting and harvests of a house's garden, and the plants growing while players are there.
 *
 * NOTE:
 * - The client sends MSG_GARDENINGCOMMAND for every spell and seed (Command: 1 soil, 2 plow or zap, 3 plant, 4 growing,
 *   6 insects, 7 protection, 8 revive, 9 inspect, 10 stasis, 12 Plant All from a treasure card, 13 Hasty Harvest), and
 *   MSG_GARDENINGHARVESTPLANT to harvest. Its gardening window stays locked until MSG_GARDENINGCOMMANDRESPONSE comes,
 *   so every one of them gets it, whatever happened.
 * - Plow All and Plant All send one command per tile; their energy is taken once per cast.
 * - The garden is its own blob, sub type "Garden": the client patches only a cached Housing/Garden blob whose one entry
 *   is the gardening proxy (0x140f20430), so the server offers it beside the item blob when a player enters.
 * - The garden grows when a player enters the house and every minute while anyone is there. The times are stored, so
 *   a garden left alone catches up the next time someone comes.
 * - Only the owner plows, plants and harvests; a visitor may cast growing, insect and protection spells.
 * - The garden trainers' spells are taught when a wizard reaches their gardening rank (GardenLessons).
 *
 * TODO:
 * - Garden trainers selling the spells for gold, instead of the spells coming with each rank.
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/24/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.Cryptography;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Game.Gardening;
using Imlight.CoreLib.Game.Housing;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Character;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal partial class HousingService {

    private const string GardenTimer = "garden-grow";
    private static readonly TimeSpan GardenInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AllCastWindow = TimeSpan.FromSeconds(8);

    // The garden's blob: the one MSG_PATCHGARDENING patches.
    private const string GardenSubType = "Garden";

    private bool _gardenLessonsChecked;
    private uint _allCastSpell;
    private DateTime _allCastAt;

    internal sealed record GardenGrow;

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_GARDENINGCOMMAND))]
    private void ReceiveGardeningCommand(WIZARDHOUSING_50_PROTOCOL.MSG_GARDENINGCOMMAND message) {
        try {
            HandleGardeningCommand(message);
        } catch (Exception exception) {
            Logger.Error("Gardening: command {Command} with {TemplateId} at ({X}, {Y}) failed: {Reason}",
                Logger.Args(message.Command, message.TemplateID, message.PositionX, message.PositionY, exception));
        } finally {
            SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_GARDENINGCOMMANDRESPONSE());
        }
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_GARDENINGHARVESTPLANT))]
    private void ReceiveGardeningHarvestPlant(WIZARDHOUSING_50_PROTOCOL.MSG_GARDENINGHARVESTPLANT message) {
        try {
            HarvestPlant(message.PositionX, message.PositionY, message.PlantTemplateID);
        } catch (Exception exception) {
            Logger.Error("Gardening: harvesting at ({X}, {Y}) failed: {Reason}", Logger.Args(message.PositionX, message.PositionY, exception));
        } finally {
            SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_GARDENINGCOMMANDRESPONSE());
        }
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_GARDENINGHARVESTPLANTSECONDSPRING))]
    private void ReceiveGardeningHarvestSecondSpring(WIZARDHOUSING_50_PROTOCOL.MSG_GARDENINGHARVESTPLANTSECONDSPRING message) {
        try {
            HarvestPlant(message.PositionX, message.PositionY, 0);
        } catch (Exception exception) {
            Logger.Error("Gardening: harvesting at ({X}, {Y}) failed: {Reason}", Logger.Args(message.PositionX, message.PositionY, exception));
        } finally {
            SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_GARDENINGCOMMANDRESPONSE());
        }
    }

    // The caster's client shows the spell; the others see it from this.
    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_GARDENINGCASTSPELL))]
    private void ReceiveGardeningCastSpell(WIZARDHOUSING_50_PROTOCOL.MSG_GARDENINGCASTSPELL message) {
        if (_house is null) {
            return;
        }

        ZoneBroadcast(new WIZARDHOUSING_50_PROTOCOL.MSG_GARDENINGCASTSPELL {
            SpellTemplateID = message.SpellTemplateID,
            PlayerGID = GetActiveWizard().GameObjectID,
            PositionX = message.PositionX,
            PositionY = message.PositionY,
            PositionZ = message.PositionZ,
        }, isSelfless: true, ZoneBroadcastTarget.Players);
    }

    [InternalMessageHandler(typeof(GardenGrow))]
    private void ReceiveGardenGrow(GardenGrow message) {
        var house = _house;
        if (house is null) {
            Timers.Cancel(GardenTimer);

            return;
        }

        try {
            EditGarden(house, growth => growth.Tick());
        } catch (Exception exception) {
            Logger.Error("Gardening: the garden of lot {LotGid} could not grow: {Reason}", Logger.Args(house.LotGid, exception.Message));
        }
    }

    /// <summary>
    /// Grows the garden of the house a player entered, before the blob is offered, and keeps it growing while they stay.
    /// </summary>
    private void EnterGarden(HouseState house, Wizard wizard) {
        try {
            EditGarden(house, growth => growth.Tick());
            Timers.StartPeriodicTimer(GardenTimer, new GardenGrow(), GardenInterval, GardenInterval);
            if (house.OwnerCharId == wizard.CharId) {
                CheckGardenLessons(wizard, SendGardenXp(wizard));
            }
        } catch (Exception exception) {
            Logger.Error("Gardening: the garden of lot {LotGid} could not start: {Reason}", Logger.Args(house.LotGid, exception));
        }
    }

    // The garden blob's line in the blob request the server sends when a player enters the house.
    private static BlobRequestObject GardenRequestEntry(HouseState house) {
        var garden = house.GardenSnapshot();

        return new BlobRequestObject {
            m_subType = GardenSubType,
            m_versionNumber = garden.Revision,
            m_packageNumber = 0,
            m_userData = garden.GidBase,
            m_objectCount = 1,
        };
    }

    private void SendGardenBlob(HouseState house) {
        var garden = house.GardenSnapshot();
        try {
            SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_SEND_BLOB {
                Data = HousingBlobs.Serialize(new Blob {
                    m_type = BlobType,
                    m_subType = GardenSubType,
                    m_versionNumber = garden.Revision,
                    m_packageNumber = 0,
                    m_associatedGID = house.LotGid,
                    m_epochDays = HousingBlobs.EpochDaysNow(),
                    m_data = new HousingBlob {
                        m_housingBlobObjectList = [
                            new HousingBlobCustomObject {
                                m_gameObjectTemplateID = GardenTemplates.ProxyTemplateId,
                                m_customData = GardenCodec.Write(garden.Garden),
                            },
                        ],
                    },
                }),
                UserData = garden.GidBase,
            });
            Logger.Debug("Gardening: sent the garden of lot {LotGid} (version {Revision}, {Count} soil and plants).",
                Logger.Args(house.LotGid, garden.Revision, garden.Garden.Count));
        } catch (InvalidOperationException exception) {
            Logger.Error("Could not build the garden blob of lot {LotGid}: {Reason}", Logger.Args(house.LotGid, exception.Message));
        }
    }

    private void LeaveGarden()
        => Timers.Cancel(GardenTimer);

    private void HandleGardeningCommand(WIZARDHOUSING_50_PROTOCOL.MSG_GARDENINGCOMMAND message) {
        var house = _house;
        var wizard = GetActiveWizard();
        if (house is null) {
            HandleForgedRequest("garden outside a house");

            return;
        }

        var owner = house.OwnerCharId == wizard.CharId;
        var x = GardenCodec.Round(message.PositionX);
        var y = GardenCodec.Round(message.PositionY);
        var z = message.PositionZ;
        Logger.Debug("Gardening: {CharId} sends command {Command} with {TemplateId} (object {ObjectGid}) at ({X}, {Y}, {Z}).",
            Logger.Args(wizard.CharId, message.Command, message.TemplateID, message.ObjectGID, x, y, z));

        if (message.Command == 3) {
            if (owner) {
                PlantSeed(house, wizard, message.TemplateID, message.ObjectGID, x, y, z);
            }
            else {
                InformGameClient("Only the owner of the house can plant here.");
            }

            return;
        }

        var spell = GardenTemplates.Spell(message.TemplateID);
        if (spell is null) {
            Logger.Warning("Gardening: {CharId} cast {TemplateId}, which is not a garden spell.", Logger.Args(wizard.CharId, message.TemplateID));
            Fizzle(GardenFizzle.Fizzle);

            return;
        }

        var visitorSpell = message.Command is 4 or 6 or 7 or 9;
        if (!owner && !visitorSpell) {
            InformGameClient("Only the owner of the house can do that here.");

            return;
        }

        if (GardenLevelOf(wizard) < spell.GardeningLevel) {
            InformGameClient($"{spell.Name} needs gardening rank {spell.GardeningLevel}.");
            Fizzle(GardenFizzle.Fizzle);

            return;
        }

        if (message.Command == 9) {
            InspectPlant(house, x, y);

            return;
        }

        var fromCard = message.ObjectGID != 0 && spell.IsTreasureCard;
        if (fromCard && wizard.SpellbookBehavior.TreasureCardCount(spell.TemplateId) <= 0) {
            Logger.Warning("Gardening: {CharId} cast treasure card {Spell} without having one.", Logger.Args(wizard.CharId, spell.Name));
            Fizzle(GardenFizzle.NotEquipped);

            return;
        }

        if (!fromCard && !wizard.SpellbookBehavior.HasSpell(spell.TemplateId)) {
            Logger.Information("Gardening: {CharId} cast {Spell} ({TemplateId}), which is not in their spellbook; it is allowed.",
                Logger.Args(wizard.CharId, spell.Name, spell.TemplateId));
        }

        // Plow All and Plant All come as one command per tile, and are paid once.
        var allCast = spell.Utility is UtilitySpellType.US_PlowAll or UtilitySpellType.US_PlantAll;
        var alreadyPaid = allCast && _allCastSpell == spell.TemplateId && DateTime.UtcNow - _allCastAt < AllCastWindow;
        var cost = alreadyPaid || fromCard ? 0 : spell.EnergyCost;
        if (wizard.PetOwnerBehavior.Energy < cost) {
            Fizzle(GardenFizzle.NoEnergy);

            return;
        }

        var tooStrong = false;
        var reached = 0;
        var change = message.Command switch {
            1 => EditGarden(house, growth => reached = growth.AddSoil(x, y, z, spell.SoilTemplateId) ? 1 : 0),
            2 => EditGarden(house, growth => reached = growth.Remove(x, y) ? 1 : 0),
            4 => EditGarden(house, growth => { growth.Tick(); reached = growth.Provide(spell, message.PositionX, message.PositionY, z); }),
            6 => EditGarden(house, growth => { growth.Tick(); reached = growth.FightPests(spell, message.PositionX, message.PositionY, z, out tooStrong); }),
            7 => EditGarden(house, growth => { growth.Tick(); reached = growth.Protect(spell, message.PositionX, message.PositionY, z); }),
            8 => EditGarden(house, growth => { growth.Tick(); reached = growth.Revive(spell, message.PositionX, message.PositionY, z); }),
            10 => EditGarden(house, growth => reached = growth.Stasis(x, y) ? 1 : 0),
            12 => new GardenChange(HouseEdit.Done),
            13 => EditGarden(house, growth => { growth.Tick(); reached = growth.HarvestNow(x, y) ? 1 : 0; }),
            _ => new GardenChange(HouseEdit.NotFound),
        };

        if (message.Command == 12) {
            reached = 1;
        }

        if (!change.Done) {
            Logger.Warning("Gardening: command {Command} of {CharId} was not made: {Result}.", Logger.Args(message.Command, wizard.CharId, change.Result));
            if (change.Result == HouseEdit.LimitReached) {
                InformGameClient("Your garden is full.");
            }

            Fizzle(GardenFizzle.Fizzle);

            return;
        }

        if (reached == 0) {
            Fizzle(tooStrong ? GardenFizzle.InsectsTooStrong : GardenFizzle.Fizzle);

            return;
        }

        if (allCast && !alreadyPaid) {
            _allCastSpell = spell.TemplateId;
            _allCastAt = DateTime.UtcNow;
        }

        if (cost > 0) {
            UseGardenEnergy(wizard, cost);
        }

        if (fromCard && !alreadyPaid) {
            UseGardenCard(wizard, spell);
        }
    }

    private void PlantSeed(HouseState house, Wizard wizard, uint seedTemplateId, ulong seedGid, int x, int y, float z) {
        var seed = GardenTemplates.Plant(seedTemplateId);
        if (seed is not { IsSeedItem: true }) {
            Logger.Warning("Gardening: {CharId} planted {TemplateId}, which is not a seed.", Logger.Args(wizard.CharId, seedTemplateId));
            Fizzle(GardenFizzle.Fizzle);

            return;
        }

        if (GardenLevelOf(wizard) < seed.GardeningLevel) {
            InformGameClient($"That seed needs gardening rank {seed.GardeningLevel}.");
            Fizzle(GardenFizzle.Fizzle);

            return;
        }

        // The seed the client named, or another of the same kind when Plant All names one already used.
        var item = wizard.InventoryBehavior.GetItem(seedGid);
        if (item is null || item.m_templateID != seedTemplateId) {
            item = wizard.InventoryBehavior.Items.FirstOrDefault(other => other.m_templateID == seedTemplateId);
        }

        if (item is null) {
            InformGameClient("You have no more of those seeds.");
            Fizzle(GardenFizzle.Fizzle);

            return;
        }

        var planted = false;
        var change = EditGarden(house, growth => planted = growth.Plant(x, y, z, seed));
        if (!change.Done || !planted) {
            if (change.Result == HouseEdit.LimitReached) {
                InformGameClient("Your garden is full.");
            }

            Fizzle(GardenFizzle.Fizzle);

            return;
        }

        if (wizard.RemoveItemFromInventory(item.m_globalID)) {
            ItemSync.SendRemoved(SessionActor.ActorRef, wizard, item.m_globalID);
        }

        Logger.Information("Gardening: {CharId} planted {Seed} at ({X}, {Y}).", Logger.Args(wizard.CharId, seed.Name, x, y));
    }

    private void HarvestPlant(float positionX, float positionY, uint plantTemplateId) {
        if (!TryGetHouse("harvest a plant", out var house)) {
            return;
        }

        var wizard = GetActiveWizard();
        var x = GardenCodec.Round(positionX);
        var y = GardenCodec.Round(positionY);
        PlantInfo harvested = null;
        uint seedTemplateId = 0;
        var done = false;
        var change = EditGarden(house, growth => {
            growth.Tick();
            done = growth.Harvest(x, y, out harvested, out seedTemplateId);
        });
        if (!change.Done || !done || harvested is null) {
            Logger.Information("Gardening: {CharId} could not harvest at ({X}, {Y}) (template {TemplateId}).",
                Logger.Args(wizard.CharId, x, y, plantTemplateId));
            InformGameClient("That plant is not ready to harvest.");

            return;
        }

        var loot = GardenHarvests.Roll(harvested, seedTemplateId, elder: harvested.Stage == PlantStage.PS_Angel);
        LootGranter.GrantAndDisplay(SessionActor.ActorRef, wizard, loot);
        var levelBefore = GardenLevelOf(wizard);
        var level = GiveGardenXp(wizard, Math.Max(1, harvested.HarvestXp));
        if (level > levelBefore) {
            CheckGardenLessons(wizard, level, force: true);
        }

        Logger.Information("Gardening: {CharId} harvested {Plant} at ({X}, {Y}): {Gold} gold, {Items}.",
            Logger.Args(wizard.CharId, harvested.Name, x, y, loot.GoldAmount,
                string.Join(", ", loot.Items.Select(item => $"{item.Quantity}x {item.ItemId}"))));
    }

    private void InspectPlant(HouseState house, int x, int y) {
        var spot = house.GardenSnapshot().Garden.FirstOrDefault(s => !s.IsSoil() && s.X == x && s.Y == y);
        if (spot is null || GardenTemplates.Plant(spot.TemplateId) is not { } info) {
            return;
        }

        var needs = new List<string>();
        string[] names = ["water", "sun", "pollination", "magic", "music"];
        for (var need = GardenNeed.Water; need <= GardenNeed.Music; need++) {
            if ((spot.Flags & GardenFlags.Need(need)) != 0) {
                needs.Add(names[(int) need]);
            }
        }

        if ((spot.Flags & GardenFlags.Pest) != 0) {
            needs.Add("pests to be taken away");
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var next = spot.NextStage > now ? $", next stage in {Span(spot.NextStage - now)}" : "";
        var ready = (spot.Flags & GardenFlags.Ready) != 0 ? ", ready to harvest"
            : spot.HarvestAt > now && info.Stage is PlantStage.PS_Mature or PlantStage.PS_Angel ? $", harvest in {Span(spot.HarvestAt - now)}" : "";
        InformGameClient($"{info.Name}: {StageName(info.Stage)}{next}{ready}. Needs: {(needs.Count == 0 ? "nothing" : string.Join(", ", needs))}.");
    }

    private static string StageName(PlantStage stage) => stage switch {
        PlantStage.PS_Seedling => "seedling",
        PlantStage.PS_Young => "young",
        PlantStage.PS_Mature => "mature",
        PlantStage.PS_Angel => "elder",
        PlantStage.PS_Wilting => "wilting",
        PlantStage.PS_Dead => "dead",
        _ => stage.ToString(),
    };

    private static string Span(long seconds) {
        var span = TimeSpan.FromSeconds(seconds);

        return span.TotalDays >= 1 ? $"{(int) span.TotalDays}d {span.Hours}h"
            : span.TotalHours >= 1 ? $"{(int) span.TotalHours}h {span.Minutes}m"
            : $"{Math.Max(1, (int) span.TotalMinutes)}m";
    }

    /// <summary>
    /// Changes a garden and sends the patches to everyone in the house.
    /// </summary>
    private GardenChange EditGarden(HouseState house, Action<GardenGrowth> edit) {
        var change = house.TryEditGarden(edit);
        if (change.Done && change.Growth is { Patches.Count: > 0 }) {
            BroadcastGardenPatches(change);
        }

        return change;
    }

    private void BroadcastGardenPatches(GardenChange change) {
        var revision = change.FirstRevision;
        foreach (var patch in change.Growth.Patches) {
            ZoneBroadcast(new WIZARDHOUSING_50_PROTOCOL.MSG_PATCHGARDENING {
                Command = patch.Command,
                PositionX = patch.X,
                PositionY = patch.Y,
                PositionZ = patch.Z,
                PackageNumber = 0,
                VersionNumber = revision++,
                GIDID = change.GidId,
                TemplateID = patch.TemplateId,
                Time = patch.Time,
            }, isSelfless: false, ZoneBroadcastTarget.Players);
        }
    }

    private void Fizzle(GardenFizzle type)
        => SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_GARDENINGPELLFIZZLE { FizzleType = (byte) type });

    private static int GardenLevelOf(Wizard wizard)
        => Math.Max(1, (int) (wizard.GameStats?.m_gardeningLevel ?? 1));

    /// <summary>Adds gardening XP and tells the client, with a level up when there is one.</summary>
    /// <returns>The gardening level after.</returns>
    private int GiveGardenXp(Wizard wizard, int xp) {
        var stats = wizard.GameStats;
        var before = GardenLevelOf(wizard);
        stats.m_gardeningXP = (int) Math.Min((long) stats.m_gardeningXP + Math.Max(0, xp), int.MaxValue);
        var after = Math.Max(before, GardenLevels.LevelAt(stats.m_gardeningXP));
        stats.m_gardeningLevel = (byte) Math.Clamp(after, 1, GardenLevels.MaxLevel);
        WizardCollection.UpdateCharacterGameStats(wizard);
        SendGardenXp(wizard);
        if (after > before) {
            Logger.Information("Gardening: {CharId} is gardening rank {Level} now.", Logger.Args(wizard.CharId, after));
            ZoneBroadcast(new WIZARDHOUSING_50_PROTOCOL.MSG_GARDENLEVELUP { GlobalID = wizard.GameObjectID, NewLevel = after },
                isSelfless: false, ZoneBroadcastTarget.Players);
        }

        return after;
    }

    /// <summary>Sets a gardening level, for testing: the XP becomes the level's start.</summary>
    internal int SetGardenLevel(Wizard wizard, int level) {
        level = Math.Clamp(level, 1, GardenLevels.MaxLevel);
        wizard.GameStats.m_gardeningLevel = (byte) level;
        wizard.GameStats.m_gardeningXP = GardenLevels.XpOf(level);
        WizardCollection.UpdateCharacterGameStats(wizard);
        SendGardenXp(wizard);
        CheckGardenLessons(wizard, level, force: true);

        return level;
    }

    private int SendGardenXp(Wizard wizard) {
        var stats = wizard.GameStats;
        if (stats.m_gardeningLevel == 0) {
            stats.m_gardeningLevel = 1;
            WizardCollection.UpdateCharacterGameStats(wizard);
        }

        SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_UPDATEGARDENINGXP {
            GlobalID = wizard.GameObjectID,
            XP = stats.m_gardeningXP,
            Level = stats.m_gardeningLevel,
            IsPet = 0,
        });

        return stats.m_gardeningLevel;
    }

    /// <summary>Teaches the garden spells of a wizard's rank that they do not know yet.</summary>
    /// <returns>The names of the spells taught.</returns>
    internal List<string> CheckGardenLessons(Wizard wizard, int level, bool force = false) {
        var taught = new List<string>();
        if (_gardenLessonsChecked && !force) {
            return taught;
        }

        _gardenLessonsChecked = true;
        foreach (var lesson in GardenLessons.UpTo(level)) {
            if (wizard.SpellbookBehavior.HasSpell(lesson.SpellId)) {
                continue;
            }

            var spell = SpellFactory.GetSpell(lesson.SpellId);
            if (spell is null || !wizard.LearnSpell(spell)) {
                Logger.Warning("Gardening: could not teach {Spell} ({SpellId}) to {CharId}.", Logger.Args(lesson.Name, lesson.SpellId, wizard.CharId));

                continue;
            }

            SendToSocket(new WIZARD_12_PROTOCOL.MSG_ADDSPELLTOBOOK { SpellID = (int) lesson.SpellId });
            taught.Add(lesson.Name);
        }

        if (taught.Count > 0) {
            Logger.Information("Gardening: taught {CharId} the rank {Level} garden spells: {Spells}.",
                Logger.Args(wizard.CharId, level, string.Join(", ", taught)));
            InformGameClient($"New garden spells: {string.Join(", ", taught)}.");
        }

        return taught;
    }

    private void UseGardenEnergy(Wizard wizard, int cost) {
        var energy = Math.Max(0, wizard.PetOwnerBehavior.Energy - cost);
        wizard.UpdateEnergy(energy);

        // The client adds the gear's energy to the max itself, so the level's own max is sent (as CantripService does).
        var baseStats = MagicLevelsConfig.GetPlayerLevelInfo(wizard.MagicSchoolBehavior.MagicSchool, wizard.MagicSchoolBehavior.Level);
        SendToSocket(new PET_9_PROTOCOL.MSG_PETENERGYTICK {
            GlobalID = wizard.GameObject.m_globalID,
            Energy = energy,
            MaxEnergy = baseStats.m_petEnergy,
            TickTime = (int) wizard.PetOwnerBehavior.LastEnergyTickEpoch,
        });
    }

    private void UseGardenCard(Wizard wizard, GardenSpellInfo spell) {
        if (!wizard.SpellbookBehavior.RemoveTreasureCard(spell.TemplateId)) {
            return;
        }

        WizardCollection.RemoveTreasureCard(wizard, spell.TemplateId);
        if (CoreObjectFactory.GetCoreTemplate(spell.TemplateId) is SpellTemplate template) {
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_REMOVETREASURESPELLFROMBOOK {
                SpellID = unchecked((int) StringHash.Compute(template.m_name)),
                EnchantmentID = 0,
            });
        }
    }

    [MessageHandler(typeof(CHARACTER_103_PROTOCOL.MSG_GARDENCOMMAND))]
    private void ReceiveGardenCommand(CHARACTER_103_PROTOCOL.MSG_GARDENCOMMAND message) {
        try {
            var wizard = GetActiveWizard();
            switch (message.Action) {
                case "grow":
                case "ready": {
                    var changed = HurryGarden(message.Action == "ready");
                    InformGameClient(changed < 0
                        ? "Go to your own house first."
                        : message.Action == "ready"
                            ? $"Garden: {changed} plant(s) watered, cleaned and made ready to harvest where they can be."
                            : $"Garden: {changed} plant(s) grew one stage, with every need met.");
                    break;
                }
                case "rank": {
                    var level = SetGardenLevel(wizard, message.Value);
                    InformGameClient($"Gardening rank {level} ({wizard.GameStats.m_gardeningXP} XP).");
                    break;
                }
                case "seeds": {
                    GiveTestSeeds(wizard);
                    break;
                }
                case "spells": {
                    var taught = CheckGardenLessons(wizard, GardenLevelOf(wizard), force: true);
                    if (taught.Count == 0) {
                        InformGameClient($"You know every garden spell of rank {GardenLevelOf(wizard)} already.");
                    }

                    break;
                }
                default: {
                    var spots = GardenSpots();
                    if (spots is null) {
                        InformGameClient($"Gardening rank {GardenLevelOf(wizard)} ({wizard.GameStats.m_gardeningXP} XP). Go to a house to see its garden.");
                        break;
                    }

                    var plants = spots.Where(spot => !spot.IsSoil()).ToList();
                    var stages = plants
                        .GroupBy(spot => GardenTemplates.Plant(spot.TemplateId) is { } info ? StageName(info.Stage) : "unknown")
                        .Select(group => $"{group.Count()} {group.Key}");
                    var ready = plants.Count(spot => (spot.Flags & GardenFlags.Ready) != 0);
                    var needy = plants.Count(spot => (spot.Flags & (GardenFlags.NeedMask | GardenFlags.Pest)) != 0);
                    InformGameClient($"Gardening rank {GardenLevelOf(wizard)} ({wizard.GameStats.m_gardeningXP} XP). This garden: "
                        + $"{spots.Count - plants.Count} soil, {plants.Count} plant(s){(plants.Count > 0 ? $" ({string.Join(", ", stages)})" : "")}, "
                        + $"{ready} ready to harvest, {needy} needing something.");
                    break;
                }
            }
        } catch (Exception exception) {
            Logger.Error("Gardening: the garden command {Action} failed: {Reason}", Logger.Args(message.Action, exception));
            InformGameClient("That did not work.");
        }
    }

    // Farley's seeds a wizard's rank may plant, three of each, for testing.
    private void GiveTestSeeds(Wizard wizard) {
        const ulong Farley = 346179;
        var rank = GardenLevelOf(wizard);
        var seeds = (WizardData.SpiralDB.NpcInventories.TryGetValue(Farley, out var shop) ? shop.Inventory : [])
            .Select(gid => (uint) (ulong) gid)
            .Where(id => GardenTemplates.Plant(id) is { IsSeedItem: true } seed && seed.GardeningLevel <= rank)
            .Distinct()
            .Take(6)
            .ToList();
        if (seeds.Count == 0) {
            seeds = [.. GardenTemplates.SeedItems.Where(id => GardenTemplates.Plant(id).GardeningLevel <= rank).Take(6)];
        }

        if (seeds.Count == 0) {
            InformGameClient("No seeds were found in the game files.");

            return;
        }

        var loot = new WizardData.Models.World.DropTableResult { DropTableId = "Gardening test seeds" };
        loot.Items.AddRange(seeds.Select(id => new WizardData.Models.World.DropItemResult { ItemId = id.ToString(), Quantity = 3 }));
        LootGranter.GrantAndDisplay(SessionActor.ActorRef, wizard, loot);
        InformGameClient($"Gave you 3 each of: {string.Join(", ", seeds.Select(id => GardenTemplates.Plant(id).Name))}.");
    }

    /// <summary>For testing: grows every plant one stage, or only makes them ready.</summary>
    /// <returns>The plants changed, or -1 outside a house of one's own.</returns>
    internal int HurryGarden(bool readyOnly) {
        var house = _house;
        if (house is null || house.OwnerCharId != GetActiveWizard().CharId) {
            return -1;
        }

        var changed = 0;
        EditGarden(house, growth => changed = growth.Hurry(readyOnly));

        return changed;
    }

    /// <summary>For testing: the soil and plants of the garden one is in.</summary>
    internal IReadOnlyList<GardenSpot> GardenSpots()
        => _house?.GardenSnapshot().Garden;

}
