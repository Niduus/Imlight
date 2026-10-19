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
 * Last Updated: 10/14/2026
 */

using System;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Character;
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

        if (petOwnerBehavior.Energy < maxEnergy) {
            wizard.UpdateEnergy(petOwnerBehavior.Energy + 1);
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

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_PETFEEDHAPPINESS))]
    private void ReceivePetFeedHappiness(WIZARD2_53_PROTOCOL.MSG_PETFEEDHAPPINESS message) {
        var wizard = GetActiveWizard();
        Logger.Information("Pet feeding: snack {0}, whole stack {1}, happiness {2}.",
            Logger.Args(message.SnackTemplateID, message.FeedStack, message.Happiness));

        var pet = PetCare.GetWornPet(wizard);
        var snack = wizard.PetSnackBehavior.GetSnack(message.SnackTemplateID);
        var count = message.FeedStack != 0 && snack is not null ? snack.m_quantity : 1;
        var problem = PetCare.Feed(wizard, pet, message.SnackTemplateID, count, SendToSocket, out var growth);
        if (problem is not null) {
            InformGameClient(problem);

            return;
        }

        SendToSocket(new WIZARD2_53_PROTOCOL.MSG_PETFEEDHAPPINESS {
            SnackTemplateID = message.SnackTemplateID,
            FeedStack = message.FeedStack,
            Happiness = PetProgress.GetBehavior(pet)?.m_happiness ?? message.Happiness,
        });
        InformGameClient(PetCare.Summary(pet, growth));
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
    /// Counts a skipped pet game as a win: the pet gets the game's experience and the stats of a track, and eats
    /// the snack the player picked, if the game named one. Skipping costs no energy and needs no wins here.
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
        if (game is not null) {
            var cost = Imlight.CoreLib.Game.Pet.PetGameConfig.TryGetEnergyCost(gameName, behavior.m_level, out var energy) ? energy : 2;
            PetProgress.AddXp(pet, (uint) Math.Max(2, cost * 2), growth);
            var tracks = game.m_trackChoices;
            if (tracks is { Count: > 0 }) {
                chosen = tracks[track >= 0 && track < tracks.Count ? track : Random.Shared.Next(tracks.Count)];
                PetProgress.ApplyStatChanges(pet, chosen?.m_modifications, growth);
            }
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
        if (snack is not null && PetCare.Feed(wizard, pet, snack.m_templateID, 1, SendToSocket, out var fed) is null) {
            growth.XpGained += fed.XpGained;
            growth.LevelsGained += fed.LevelsGained;
            growth.StatChanges.AddRange(fed.StatChanges);
        }

        if (snackData is null && game is not null && PetRules.SkipShowsFeedWindow) {
            // Show the end of game window, the same as after playing, so the player can pick a snack to feed.
            SendPetGameEnd(gameName, growth.XpGained, chosen);
            _skipAwaitingFeed = pet.m_globalID.Full;
        }

        InformGameClient(PetCare.Summary(pet, growth));
    }

    private const uint SKIP_WINS = 100;

    private void SendPetGameEnd(string gameName, uint xpGained, PetStatModificationSet statMods) {
        var endData = new PetGameEndData {
            m_Score = 1.0f,
            m_statMods = statMods,
            m_xpGain = xpGained,
            m_wins = SKIP_WINS,
        };

        // PetGameEndData's properties are Save|Copy|Public, none Transmit (as with PetGameInfo at the kiosk).
        var serializer = new Imcodec.ObjectProperty.ObjectSerializer(Versionable: false, Behaviors: Imcodec.ObjectProperty.SerializerFlags.None);
        if (!serializer.Serialize(endData, Imcodec.ObjectProperty.PropertyFlags.Prop_Public, out var data)) {
            Logger.Error("Could not write the end of pet game data for {0}.", Logger.Args(gameName));

            return;
        }

        Logger.Information("Pet game skip: sent the end of game window for {0}.", Logger.Args(gameName));
        SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEEND {
            Game = gameName,
            Data = data,
        });
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
        Logger.Information("Pet game join: game {0}, track {1}.", Logger.Args(message.Game, message.Track));
        SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEJOINRSP { Game = message.Game, Success = 0 });
        InformGameClient("Pet games are not in yet. Feed snacks from the snack window or with .pet feed to level your pet.");
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

    private void SendEggCreatedMessages(CraftingSlot egg) {
        SendToSocket(new PET_9_PROTOCOL.MSG_PETEGGMORPHED {
            PetTemplateGID = egg.m_globalID,
            PetName = 0,
            HatchTime = (uint) egg.m_timeFinished
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

        // 4. MSG_PETTOMEPETADDED — adds the pet to the player's pet tome.
        var petTomeGlobalId = RandomGen.GenerateGUID();

        SendToSocket(new WIZARD2_53_PROTOCOL.MSG_PETTOMEPETADDED {
            GlobalID = petTomeGlobalId,
            PetTemplateID = (uint) petTemplateId
        });

        Logger.Information("Pet hatched! Egg {0} → template {1} (tome entry: {2})",
            Logger.Args(egg.m_globalID, petTemplateId, petTomeGlobalId));
    }

}
