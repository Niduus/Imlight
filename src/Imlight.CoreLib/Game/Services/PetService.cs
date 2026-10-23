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
 * PET SERVICE
 * ========================================================================
 * 
 * PURPOSE:
 * Manages pet energy mechanics and periodic energy regeneration 
 * for player characters.
 * 
 * USAGE EXAMPLE:
 * Internal service handling pet energy tick and synchronization 
 * within the game server session.
 * 
 * NOTE:
 * 
 * TODO:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 10/20/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.MessageLayer;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Character;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.Shared.Utilities;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal class PetService(SessionActor sessionActor) : MessageService(sessionActor) {

    private static int EnergyTickIntervalInSeconds => Imlight.CoreLib.Game.Pet.AdvancedPetsConfig.EnergyTickInSeconds;
    private const int PET_ENERGY_TICK_DELAY = 2;

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new PetService(parentActor));

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE))]
    private void ReceivePostAttach(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE message) {
        var wizard = GetActiveWizard();
        // Energy above the maximum (gear that gave energy came off while away) drops to it.
        wizard.CapEnergyAtMax();
        var petOwnerBehavior = wizard.PetOwnerBehavior;
        var clientMaxEnergy = wizard.GameStats.GetClientTypeAlternative().m_energyMax;

        var tickTime = petOwnerBehavior.LastEnergyTickEpoch;
        if (tickTime <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) {
            tickTime = (uint) (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + EnergyTickIntervalInSeconds);
        }

        var tickMsg = new PET_9_PROTOCOL.MSG_PETENERGYTICK() {
            GlobalID = wizard.GameObjectID,
            Energy = petOwnerBehavior.Energy,
            MaxEnergy = clientMaxEnergy,
            TickTime = (int) tickTime
        };

        SendToSocket(tickMsg);

        var tickTimeSeconds = tickTime - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var firstTickDelay = TimeSpan.FromSeconds(tickTimeSeconds + PET_ENERGY_TICK_DELAY);
        var tickInterval = TimeSpan.FromSeconds(EnergyTickIntervalInSeconds + PET_ENERGY_TICK_DELAY);
        Timers.StartPeriodicTimer("petEnergyTick", new CHARACTER_103_PROTOCOL.MSG_DOENERGYTICK(), firstTickDelay, tickInterval);

        InitializeEggs(wizard);
    }

    [MessageHandler(typeof(CHARACTER_103_PROTOCOL.MSG_DOENERGYTICK))]
    private void ReceiveDoEnergyTick(CHARACTER_103_PROTOCOL.MSG_DOENERGYTICK message) {
        var wizard = GetActiveWizard();
        var petOwnerBehavior = wizard.PetOwnerBehavior;
        var maxEnergy = wizard.GameStats.m_energyMax;
        var clientMaxEnergy = wizard.GameStats.GetClientTypeAlternative().m_energyMax;

        var tickTime = (int) (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + EnergyTickIntervalInSeconds);

        // Above the maximum it drops to it; below, it regains one.
        if (petOwnerBehavior.Energy != maxEnergy) {
            wizard.UpdateEnergy(petOwnerBehavior.Energy < maxEnergy ? petOwnerBehavior.Energy + 1 : maxEnergy);
            var tickMsg = new PET_9_PROTOCOL.MSG_PETENERGYTICK() {
                GlobalID = wizard.GameObjectID,
                Energy = petOwnerBehavior.Energy,
                MaxEnergy = clientMaxEnergy,
                TickTime = tickTime
            };

            SendToSocket(tickMsg);
        }
    }

    [MessageHandler(typeof(CHARACTER_103_PROTOCOL.MSG_DOEGGHATCH))]
    private void ReceiveEggHatch(CHARACTER_103_PROTOCOL.MSG_DOEGGHATCH message) {
        var wizard = GetActiveWizard();
        var egg = wizard.PetOwnerBehavior.MorphingSlots
            ?.FirstOrDefault(e => e.m_globalID == message.EggGlobalId);

        if (egg == null) {
            Logger.Debug("MSG_DOEGGHATCH for unknown egg {0}.", Logger.Args(message.EggGlobalId));
            return;
        }

        HatchEgg(wizard, egg);
    }

    [MessageHandler(typeof(PET_9_PROTOCOL.MSG_HATCHEGGNOW))]
    private void ReceiveHatchEggNow(PET_9_PROTOCOL.MSG_HATCHEGGNOW message) {
        var wizard = GetActiveWizard();
        var egg = wizard.PetOwnerBehavior.MorphingSlots
            ?.FirstOrDefault(e => e.m_globalID == message.EggGID);

        if (egg == null) {
            Logger.Debug("MSG_HATCHEGGNOW for unknown egg {0}.", Logger.Args(message.EggGID));
            return;
        }

        // TODO: validate gold cost against message.Gold
        // For now, just hatch immediately.
        HatchEgg(wizard, egg);
    }

    /// <summary>
    /// The Pet Tome (the Pets page of the spellbook links to it) asks for the pets the wizard has owned. The answer's
    /// PetData becomes PetTomeBehavior.m_petData (PetTome), and the game then shows the tome.
    /// </summary>
    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_REQUESTPETTOME))]
    private void ReceiveRequestPetTome(WIZARD2_53_PROTOCOL.MSG_REQUESTPETTOME message) {
        Logger.Information("Pet tome: game sent MSG_REQUESTPETTOME GlobalID={0} PetData={1} bytes Buffer={2} bytes PremiumPetExceptionCode={3}.",
            Logger.Args(message.GlobalID, ((byte[]) message.PetData)?.Length ?? 0, ((byte[]) message.Buffer)?.Length ?? 0, message.PremiumPetExceptionCode));
        try {
            var wizard = GetActiveWizard();
            var tome = PetTome.Collect(wizard, out var added);

            // The game reads the premium pet list from Buffer when the code differs from the one it has; an empty
            // list in the hatchmaking format keeps that read harmless.
            var premium = new Imcodec.ObjectProperty.ObjectSerializer(Versionable: false, Behaviors: Imcodec.ObjectProperty.SerializerFlags.None);
            premium.Serialize(new HatchmakingPremiumPetsList { m_addedPremiumPets = [], m_removedPremiumPets = [] },
                Imcodec.ObjectProperty.PropertyFlags.Prop_Public, out var buffer);

            SendToSocket(new WIZARD2_53_PROTOCOL.MSG_REQUESTPETTOME {
                GlobalID = wizard.GameObjectID,
                PetData = PetTome.Write(tome),
                Buffer = buffer,
                PremiumPetExceptionCode = message.PremiumPetExceptionCode,
            });
            Logger.Information("Pet tome: sent {0} pet kinds to {1} ({2} new: {3}).",
                Logger.Args(tome.Count, wizard.CharId, added.Count, string.Join(",", added)));
        }
        catch (Exception ex) {
            Logger.Error("Pet tome: MSG_REQUESTPETTOME failed. {0}", Logger.Args(ex));
        }
    }

    /// <summary>
    /// The game asks for the attic's pets to be added to the Pet Tome ("you'll need to update the rest").
    /// </summary>
    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_PETTOMESCANATTIC))]
    private void ReceivePetTomeScanAttic(WIZARDHOUSING_50_PROTOCOL.MSG_PETTOMESCANATTIC message) {
        Logger.Information("Pet tome: game sent MSG_PETTOMESCANATTIC GlobalID={0}.", Logger.Args(message.GlobalID));
        try {
            var wizard = GetActiveWizard();
            PetTome.Collect(wizard, out var added);
            foreach (var templateId in added) {
                SendPetTomePetAdded(wizard, templateId);
            }

            Logger.Information("Pet tome: the scan added {0} pet kinds for {1}.", Logger.Args(added.Count, wizard.CharId));
        }
        catch (Exception ex) {
            Logger.Error("Pet tome: MSG_PETTOMESCANATTIC failed. {0}", Logger.Args(ex));
        }
    }

    private void SendPetTomePetAdded(Wizard wizard, uint templateId)
        => SendToSocket(PetTome.PetAdded(wizard, templateId));

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_PETFEEDHAPPINESS))]
    private void ReceivePetFeedHappiness(WIZARD2_53_PROTOCOL.MSG_PETFEEDHAPPINESS message) {
        var wizard = GetActiveWizard();
        Logger.Information("Pet feeding: snack {0}, whole stack {1}, happiness {2}.",
            Logger.Args(message.SnackTemplateID, message.FeedStack, message.Happiness));

        var pet = PetCare.GetWornPet(wizard);
        var snack = wizard.PetSnackBehavior.GetSnack(message.SnackTemplateID);
        var count = message.FeedStack != 0 && snack is not null ? snack.m_quantity : 1;
        // From the feed window after a skipped game, that window waits for MSG_PETGAMESNACKFEEDSUCCESS first.
        var inFeedWindow = pet is not null && _skipAwaitingFeed != 0 && _skipAwaitingFeed == pet.m_globalID.Full;
        var held = new List<IMessage>();
        var problem = PetCare.Feed(wizard, pet, message.SnackTemplateID, count, inFeedWindow ? held.Add : SendToSocket, out var growth);
        if (problem is not null) {
            if (inFeedWindow) {
                SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMESNACKFEEDFAILED());
            }
            InformGameClient(problem);

            return;
        }

        if (inFeedWindow) {
            SendHeldThenFeedResult(held, "feed", growth.XpGained, WithActualChanges(
                (CoreObjectFactory.GetCoreTemplate(message.SnackTemplateID) as PetSnackItemTemplate)?.m_statModifierSet, growth));
        }

        SendToSocket(new WIZARD2_53_PROTOCOL.MSG_PETFEEDHAPPINESS {
            SnackTemplateID = message.SnackTemplateID,
            FeedStack = message.FeedStack,
            Happiness = PetProgress.GetBehavior(pet)?.m_happiness ?? message.Happiness,
        });
        InformGameClient(PetCare.Summary(pet, growth));
        ResummonAfterLevelUp(pet, growth);
    }

    /// <summary>
    /// Summons the pet again after it reached a new age: the age and talent rating shown with the pet in the world
    /// (Ancient 15 (51)) come from the summoned copy, which kept the old ones until the next login (Niduus, 2026-10-19).
    /// </summary>
    private void ResummonAfterLevelUp(WizClientObjectItem pet, PetProgress.PetGrowth growth) {
        if (pet is not null && growth is { LevelsGained: > 0 }) {
            TellOtherServices(new CHARACTER_103_PROTOCOL.MSG_RESUMMONPET { PetItemId = pet.m_globalID });
        }
    }

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_PETGAMESKIP))]
    private void ReceivePetGameSkip(WIZARD2_53_PROTOCOL.MSG_PETGAMESKIP message) {
        Logger.Information("Pet game skip: pet {0}, game {1}, bonus {2}.",
            Logger.Args(message.GlobalID, message.Game, message.Bonus));
        SkipPetGame(message.GlobalID, message.Game.ToString(), message.Bonus, null);
    }

    // The pet whose skipped game is waiting for the feed step of the results window, so the feed does not
    // count the game a second time.
    private ulong _skipAwaitingFeed;

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_PETGAMESKIPFEED))]
    private void ReceivePetGameSkipFeed(WIZARD2_53_PROTOCOL.MSG_PETGAMESKIPFEED message) {
        // Logged in full (as hex) so the next log shows how the game sends the chosen snack.
        byte[] data = message.Data;
        Logger.Information("Pet game skip and feed: pet {0}, game {1}, data {2}.",
            Logger.Args(message.GlobalID, message.Game, data is null ? "none" : System.Convert.ToHexString(data)));
        SkipPetGame(message.GlobalID, message.Game.ToString(), 0, data);
    }

    /// <summary>
    /// Skips a pet game and feeds the snack the player picked, if the game named one. With [Pets] SkipGameRewards the
    /// skip counts as a win (the game's experience and the stats of a track). Skipping costs no energy and needs no
    /// wins here.
    /// </summary>
    private void SkipPetGame(ulong petId, string gameName, int track, byte[] snackData) {
        var wizard = GetActiveWizard();
        var pet = (petId != 0 ? PetCare.FindPet(wizard, petId) : null) ?? PetCare.GetWornPet(wizard);
        var behavior = PetProgress.GetBehavior(pet);
        if (behavior is null) {
            InformGameClient("You need to wear a pet to train it.");

            return;
        }

        PetProgress.EnsureInitialized(pet);
        var growth = new PetProgress.PetGrowth();

        // The feed step after a skip: the game was already counted, so only the snack is eaten.
        var feedOnly = snackData is not null && _skipAwaitingFeed != 0 && _skipAwaitingFeed == pet.m_globalID.Full;
        _skipAwaitingFeed = 0;

        PetStatModificationSet chosen = null;
        var game = feedOnly ? null : Imlight.CoreLib.Game.Pet.PetGameConfig.FindGame(gameName);
        if (game is not null && PetRules.SkipGameRewards) {
            var cost = Imlight.CoreLib.Game.Pet.PetGameConfig.TryGetEnergyCost(gameName, behavior.m_level, out var energy) ? energy : 2;
            PetProgress.AddXp(pet, (uint) Math.Max(2, cost * 2), growth);
            var tracks = game.m_trackChoices;
            if (tracks is { Count: > 0 }) {
                chosen = tracks[track >= 0 && track < tracks.Count ? track : Random.Shared.Next(tracks.Count)];
                PetProgress.ApplyStatChanges(pet, chosen?.m_modifications, growth);
            }
        }

        // The results window first: level-up windows sent while it opens may not show.
        if (snackData is null && game is not null && PetRules.SkipShowsFeedWindow) {
            // MSG_PETGAMEEND alone did not open anything (0.9.61). Echo the skip back like the game's other pet
            // requests (MSG_PETFEEDHAPPINESS is answered the same way); the game then shows its results and
            // feed window, whose feed comes back as MSG_PETGAMESKIPFEED.
            SendToSocket(new WIZARD2_53_PROTOCOL.MSG_PETGAMESKIP {
                GlobalID = pet.m_globalID,
                Game = gameName,
                Bonus = (byte) Math.Clamp(track, 0, byte.MaxValue),
            });
            SendPetGameEnd(gameName, growth.XpGained, WithActualChanges(chosen, growth));
            _skipAwaitingFeed = pet.m_globalID.Full;
        }

        if (!feedOnly) {
            PetCare.Finish(wizard, pet, growth, SendToSocket);
        }

        var snack = FindSnackIn(wizard, snackData);
        if (snackData is { Length: > 0 } && snack is null) {
            // Logged so the next log shows how the game names the snack: compare these ids with the data above.
            Logger.Warning("Pet game skip and feed: no snack of {0} matched the data. Snacks held: {1}.",
                Logger.Args(wizard.PlayerNameBehavior?.GetWizardName(), string.Join(", ",
                    wizard.PetSnackBehavior?.Snacks?.Select(s => $"{s.m_templateID}/{s.m_globalID.Full:X}") ?? [])));
        }

        if (snack is not null) {
            // The feed window waits for MSG_PETGAMESNACKFEEDSUCCESS to fill its bars, so the pet's new state and
            // any level-up window follow it.
            var snackTemplateId = snack.m_templateID;
            var held = new List<IMessage>();
            if (PetCare.Feed(wizard, pet, snackTemplateId, 1, held.Add, out var fed) is null) {
                growth.XpGained += fed.XpGained;
                growth.LevelsGained += fed.LevelsGained;
                growth.StatChanges.AddRange(fed.StatChanges);
                var snackMods = (CoreObjectFactory.GetCoreTemplate(snackTemplateId) as PetSnackItemTemplate)?.m_statModifierSet;
                SendHeldThenFeedResult(held, gameName, fed.XpGained, WithActualChanges(snackMods, fed));
            }
            else {
                if (snackData is not null) {
                    SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMESNACKFEEDFAILED());
                }

                held.ForEach(SendToSocket);
            }
        }
        else if (snackData is { Length: > 0 }) {
            SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMESNACKFEEDFAILED());
        }

        InformGameClient(PetCare.Summary(pet, growth));
        ResummonAfterLevelUp(pet, growth);
    }

    private const uint SKIP_WINS = 100;

    /// <summary>
    /// A copy of a snack's or game track's stat changes with m_actualChange set to what the pet really gained, so
    /// the results window shows the real numbers (a stat at its maximum gains less, or nothing).
    /// </summary>
    private static PetStatModificationSet WithActualChanges(PetStatModificationSet set, PetProgress.PetGrowth growth) {
        if (set?.m_modifications is null) {
            return set;
        }

        return set with {
            m_modifications = [.. set.m_modifications.Select(mod => mod is null ? null : mod with {
                m_actualChange = (uint) Math.Max(0, growth.StatChanges
                    .Where(change => string.Equals(change.Stat, mod.m_name, StringComparison.OrdinalIgnoreCase))
                    .Sum(change => change.Change)),
            })],
        };
    }

    /// <summary>
    /// Sends the pet's new state, then the feed result. The feed window shows the XP and stats the pet has when the
    /// result arrives, so sending the state after it showed the numbers from before the snack (46 +15 instead of
    /// 61 +15). The level-up window did not show when sent after it either (Niduus, 2026-10-19).
    /// </summary>
    private void SendHeldThenFeedResult(List<IMessage> held, string gameName, uint xpGained, PetStatModificationSet statMods) {
        held.ForEach(SendToSocket);
        SendSnackFeedResult(gameName, xpGained, statMods);
    }

    private void SendSnackFeedResult(string gameName, uint xpGained, PetStatModificationSet statMods) {
        if (!TryWriteEndData(gameName, xpGained, statMods, out var data)) {
            return;
        }

        Logger.Information("Pet game skip and feed: sent the feed result for {0}.", Logger.Args(gameName));
        SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMESNACKFEEDSUCCESS { Data = data });
    }

    private void SendPetGameEnd(string gameName, uint xpGained, PetStatModificationSet statMods) {
        if (!TryWriteEndData(gameName, xpGained, statMods, out var data)) {
            return;
        }

        Logger.Information("Pet game skip: sent the end of game window for {0}.", Logger.Args(gameName));
        SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEEND {
            Game = gameName,
            Data = data,
        });
    }

    private bool TryWriteEndData(string gameName, uint xpGained, PetStatModificationSet statMods, out Imcodec.IO.ByteString data) {
        var endData = new PetGameEndData {
            m_Score = 1.0f,
            m_statMods = statMods,
            m_xpGain = xpGained,
            m_wins = SKIP_WINS,
        };

        // PetGameEndData's properties are Save|Copy|Public, none Transmit (as with PetGameInfo at the kiosk).
        var serializer = new Imcodec.ObjectProperty.ObjectSerializer(Versionable: false, Behaviors: Imcodec.ObjectProperty.SerializerFlags.None);
        if (!serializer.Serialize(endData, Imcodec.ObjectProperty.PropertyFlags.Prop_Public, out data)) {
            Logger.Error("Could not write the end of pet game data for {0}.", Logger.Args(gameName));

            return false;
        }

        return true;
    }

    /// <summary>
    /// Finds which of the wizard's snacks the skip-and-feed data names, by looking for a snack's template id or
    /// item id in it. The layout of that data is not known yet.
    /// </summary>
    private static ClientPetSnackItem FindSnackIn(Wizard wizard, byte[] data) {
        var snacks = wizard.PetSnackBehavior?.Snacks;
        if (data is null || snacks is null || snacks.Count == 0) {
            return null;
        }

        for (var i = 0; i + 4 <= data.Length; i++) {
            var id32 = BitConverter.ToUInt32(data, i);
            var id64 = i + 8 <= data.Length ? BitConverter.ToUInt64(data, i) : 0;
            var snack = snacks.FirstOrDefault(s => s.m_templateID == id32 || (id64 != 0 && s.m_globalID.Full == id64));
            if (snack is not null) {
                return snack;
            }
        }

        return null;
    }

    [MessageHandler(typeof(PET_9_PROTOCOL.MSG_PETGAMEJOIN))]
    private void ReceivePetGameJoin(PET_9_PROTOCOL.MSG_PETGAMEJOIN message) {
        // The pet morphing (hatching) window is HatchService's.
        if (string.Equals(message.Game.ToString(), "PetGameMorph", StringComparison.OrdinalIgnoreCase)) {
            return;
        }

        Logger.Information("Pet game join: game {0}, track {1}.", Logger.Args(message.Game, message.Track));
        SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEJOINRSP { Game = message.Game, Success = 0 });
        InformGameClient("Pet games are not in yet. Feed snacks from the snack window or with .pet feed to level your pet.");
    }

    /// <summary>
    /// Breeds two pets into an egg for this wizard: `.hatch`, the hatchmaking kiosk and hatching in person all
    /// come here, because the egg's timer lives in this service (Niduus, 2026-10-20).
    /// </summary>
    [MessageHandler(typeof(CHARACTER_103_PROTOCOL.MSG_BREEDPETS))]
    private void ReceiveBreedPets(CHARACTER_103_PROTOCOL.MSG_BREEDPETS message) {
        string problem;
        WizClientObjectItem egg = null;
        try {
            problem = Breed(message, out egg);
        }
        catch (Exception ex) {
            Logger.Error("Hatching: breeding {0} with {1} failed. {2}",
                Logger.Args(message.ParentA?.ItemId, message.ParentB?.ItemId, ex));
            problem = "Something went wrong while hatching; nothing was made. The server log has the details.";
        }

        if (problem is not null) {
            Logger.Information("Hatching ({0}) refused: {1}", Logger.Args(message.Source, problem));
        }

        // What each window waits for. Both status codes are guesses (0 = it worked, as with trades).
        switch (message.Source) {
            case HatchSource.InPerson:
                SendToSocket(new WIZARD_12_PROTOCOL.MSG_PETHATCHRESULT {
                    TargetGID = message.PartnerGameObjectId,
                    Status = (uint) (problem is null ? HatchStatus.Success : HatchStatus.Failed),
                });
                break;
            case HatchSource.Kiosk:
                SendToSocket(new WIZARD2_53_PROTOCOL.MSG_HATCHMAKINGSTATUS { ErrorCode = problem is null ? 0u : 1u });
                break;
            case HatchSource.Sigil:
                // The HatchService sends the player back from the pet morph zone once the egg is made.
                TellOtherServices(new CHARACTER_103_PROTOCOL.MSG_HATCHBRED { Success = problem is null });
                break;
        }

        if (problem is not null) {
            InformGameClient(problem);

            return;
        }

        var kind = PetHatching.KindName((uint) egg.m_templateID) ?? egg.m_templateID.ToString();
        var left = Math.Max(0, (long) (PetProgress.GetBehavior(egg)?.m_hatchedTimeSecs ?? 0) - DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        InformGameClient($"{message.ParentA.Label} and {message.ParentB.Label} made a {kind} egg! It hatches in "
            + (left >= 3600 ? $"{left / 3600} h {left % 3600 / 60} min." : left >= 60 ? $"{left / 60} min." : $"{left} s."));
    }

    private string Breed(CHARACTER_103_PROTOCOL.MSG_BREEDPETS message, out WizClientObjectItem egg) {
        egg = null;
        var wizard = GetActiveWizard();
        if (wizard is null) {
            return "No wizard is logged in.";
        }

        Logger.Information("Hatching ({0}) for {1}: parent A {2}; parent B {3}.",
            Logger.Args(message.Source, wizard.CharId, PetHatching.Describe(message.ParentA), PetHatching.Describe(message.ParentB)));

        // The wizard's own parent has to still be there.
        if (message.ParentA is null || PetCare.FindPet(wizard, message.ParentA.ItemId) is null) {
            return "Your pet for the hatch is no longer in your backpack.";
        }

        var cost = PetHatching.GoldCost;
        if (cost > 0 && wizard.GameStats.m_currentGold < cost) {
            return $"Hatching costs {cost} gold.";
        }

        egg = PetHatching.MakeEgg(wizard.CharId, message.ParentA, message.ParentB, Random.Shared, out var hatchSeconds, out var problem);
        if (egg is null) {
            return problem;
        }

        if (!wizard.AddPetToInventory(egg)) {
            egg = null;

            return "Your backpack has no room for the egg.";
        }

        if (!ItemSync.SendAdded(SessionActor.ActorRef, wizard, egg)) {
            Logger.Error("Hatching: egg {0} is in the backpack of {1}, but the game could not be told. It shows after a relog.",
                Logger.Args(egg.m_globalID.Full, wizard.CharId));
        }

        SendToSocket(new WIZARD2_53_PROTOCOL.MSG_ITEMACQUISITION {
            ItemGlobalID = egg.m_globalID,
            ItemLocation = 1,
            ItemTemplateID = (uint) egg.m_templateID
        });

        // The egg slot shares the item's global id, so MSG_HATCHEGGNOW and hatching find the item.
        var slot = wizard.PetOwnerBehavior.CreatePetEgg(egg.m_templateID, hatchSeconds, egg.m_globalID);
        WizardCollection.UpdateCharacterPetOwnerBehavior(wizard);

        if (cost > 0) {
            wizard.RemoveGold(cost);
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD {
                Gold = wizard.GameStats.m_currentGold,
                MaxGold = wizard.GameStats.m_baseGoldPouch
            });
        }

        var behavior = PetProgress.GetBehavior(egg);
        Logger.Information("Hatching: {0} got egg {1} ({2}, template {3}), hatches in {4} s. Talents {5}; derby {6}; max stats {7}.",
            Logger.Args(wizard.CharId, egg.m_globalID.Full, PetHatching.KindName((uint) egg.m_templateID), egg.m_templateID.Full, hatchSeconds,
                PetHatching.Talents(behavior?.m_allTalents), PetHatching.Talents(behavior?.m_allDerbyTalents),
                string.Join("/", (behavior?.m_maxStats ?? []).Select(s => $"{s.m_name} {s.m_value}"))));

        if (message.Source != HatchSource.Sigil) {
            SendEggCreatedMessages(slot);
        }
        else {
            // The morph window's egg picture: a guess that it wants the pet's template, as the field's name says.
            var name = CoreObjectFactory.FindBehaviorInstance<ClientPetNameBehavior>(egg, out var petName) ? petName.m_nameKeys : 0;
            SendToSocket(new PET_9_PROTOCOL.MSG_PETEGGMORPHED {
                PetTemplateGID = egg.m_templateID,
                PetName = name,
                HatchTime = SecondsLeft(slot.m_timeFinished)
            });
            SendToSocket(new PET_9_PROTOCOL.MSG_PETMORPHINGSLOT {
                GlobalID = slot.m_globalID,
                Removed = 0,
                ExpireTimeCount = (uint) slot.m_timeFinished
            });
        }

        StartEggTimer(slot);

        return null;
    }

    private void InitializeEggs(Wizard wizard) {
        var morphingSlots = wizard.PetOwnerBehavior.MorphingSlots;
        if (morphingSlots == null || morphingSlots.Count == 0) {
            return;
        }

        // Send creation messages for each egg so the client displays them.
        foreach (var egg in morphingSlots) {
            SendEggCreatedMessages(egg);
        }

        // Hatch any eggs whose timer expired while offline.
        var readyEggs = wizard.PetOwnerBehavior.GetReadyEggs();
        foreach (var egg in readyEggs) {
            HatchEgg(wizard, egg);
        }

        // Start timers for eggs still incubating.
        var pendingEggs = wizard.PetOwnerBehavior.GetPendingEggs();
        foreach (var egg in pendingEggs) {
            StartEggTimer(egg);
        }
    }

    // The game counts the egg window's timer down from this, one a second: seconds left, not a time of day.
    private static uint SecondsLeft(long finished)
        => (uint) Math.Max(0, finished - DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    private void SendEggCreatedMessages(CraftingSlot egg) {
        SendToSocket(new PET_9_PROTOCOL.MSG_PETEGGMORPHED {
            PetTemplateGID = egg.m_globalID,
            PetName = 0,
            HatchTime = SecondsLeft(egg.m_timeFinished)
        });

        SendToSocket(new PET_9_PROTOCOL.MSG_PETMORPHINGSLOT {
            GlobalID = egg.m_globalID,
            Removed = 0,
            ExpireTimeCount = (uint) egg.m_timeFinished
        });
    }

    private void StartEggTimer(CraftingSlot egg) {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var remainingSeconds = egg.m_timeFinished - (int) now;
        if (remainingSeconds <= 0) {
            // Already ready — hatch immediately.
            var wizard = GetActiveWizard();
            HatchEgg(wizard, egg);

            return;
        }

        Timers.StartSingleTimer(
            $"eggHatch_{egg.m_globalID}",
            new CHARACTER_103_PROTOCOL.MSG_DOEGGHATCH { EggGlobalId = egg.m_globalID },
            TimeSpan.FromSeconds(remainingSeconds)
        );
    }

    private void HatchEgg(Wizard wizard, CraftingSlot egg) {
        // Resolve the pet template from the egg's recipe name.
        if (!ServerPetOwnerBehavior.TryGetPetTemplateFromEgg(egg, out var petTemplateId)) {
            Logger.Debug("HatchEgg: could not parse pet template from egg {0}, recipe='{1}'.",
                Logger.Args(egg.m_globalID, egg.m_recipeName));

            return;
        }

        // Remove the egg from morphing slots and notify client.
        wizard.PetOwnerBehavior.HatchEgg(egg);
        TellOtherServices(new CHARACTER_103_PROTOCOL.MSG_BADGEEVENT { Kind = BadgeEventKind.Recheck });

        // Persistent save.
        WizardCollection.UpdateCharacterPetOwnerBehavior(wizard);

        SendToSocket(new PET_9_PROTOCOL.MSG_PETMORPHINGSLOT {
            GlobalID = egg.m_globalID,
            Removed = 1,
            ExpireTimeCount = (uint) egg.m_timeFinished
        });

        // The egg and the pet item share a global id.
        var pet = wizard.FindPetItem(egg.m_globalID);
        ClientPetItemBehavior petBehavior = null;
        if (pet is not null && PetProgression.Hatch(pet, (uint) Math.Min(egg.m_timeFinished, DateTimeOffset.UtcNow.ToUnixTimeSeconds()))) {
            wizard.SavePet(pet);
            CoreObjectFactory.FindBehaviorInstance(pet, out petBehavior);
        } else {
            Logger.Warning("Egg {EggId} hatched, but the wizard has no pet item with that id.", Logger.Args(egg.m_globalID.Full));
            pet = null;
        }

        // 1. MSG_PETLEVELUP — pet goes from level 0 (egg) to level 1.
        SendToSocket(new PET_9_PROTOCOL.MSG_PETLEVELUP {
            GlobalID = egg.m_globalID,
            OverallRating = (byte) (petBehavior?.m_overallRating ?? 0),
            ActiveRating = (byte) (petBehavior?.m_activeRating ?? 0),
            PetLevel = 1,
            NewTalent = 0,
            NewDerbyPower = 0,
            NewJewel = 0,
            Display = 1
        });

        // 2. MSG_PETUPDATEBEHAVIOR: the pet item's state, keyed by the egg gid, which is the pet item gid.
        if (PetBehaviorUpdate.TryCreate(pet, out var behaviorUpdate)) {
            SendToSocket(behaviorUpdate);
        }

        // 3. MSG_PETHATCHED — tells client the egg hatched into this pet template.
        SendToSocket(new PET_9_PROTOCOL.MSG_PETHATCHED {
            GlobalID = egg.m_globalID,
            TemplateID = petTemplateId
        });

        // 4. MSG_PETTOMEPETADDED — adds the pet to the player's pet tome, on the player's PetTomeBehavior.
        var newInTome = PetTome.Add(wizard, (uint) petTemplateId);
        if (newInTome) {
            SendPetTomePetAdded(wizard, (uint) petTemplateId);
        }

        Logger.Information("Pet hatched! Egg {0} → template {1} (new in the pet tome: {2})",
            Logger.Args(egg.m_globalID, petTemplateId, newInTome));
    }

}
