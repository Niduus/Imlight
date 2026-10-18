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
 */

using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

/// <summary>
/// The pet game tents in the Pet Pavilion: talking to one opens the pet game window for its game, where the pet can
/// skip the game and be fed a snack.
/// </summary>
internal sealed class InteractPetGameKioskComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    public string ServiceName     => "PetGameKioskService";
    public string NpcIcon         => "GUI/Art/Art_Quest_Minigame.dds";
    public string NpcNameKey      => "GUI_00002918";
    public string NpcTextKey      => "GUI_PlayMini";
    public WizBangs WizBang       => WizBangs.None;
    public string StateName       => null;
    public string InteractWizBang => null;
    public string DisplayKey      => "GUI_Kiosk";

    private const uint SKIP_WINS = 100;

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => GameName(template) is not null;

    private static string GameName(CoreTemplate template)
        => template is GameObjectTemplate gameObjectTemplate
            && gameObjectTemplate.m_behaviors?.OfType<PetGameKioskBehaviorTemplate>().FirstOrDefault() is { } kiosk
            && !string.IsNullOrEmpty(kiosk.m_petGameName)
            ? kiosk.m_petGameName
            : null;

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard playerCharacter)
        => [
            new PetGameKioskOption {
                m_displayKey = DisplayKey,
                m_iconKey = "Kiosk",
                m_serviceName = ServiceName,
            }
        ];

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        var gameName = GameName(Entity.Template);
        var game = Imlight.CoreLib.Game.Pet.PetGameConfig.FindGame(gameName);
        if (game is null) {
            Logger.Warning("Pet game kiosk {0} names the game {1}, which PetGames.xml does not have.",
                Logger.Args(Entity.ActiveGameObject.m_debugName, gameName));

            return;
        }

        // Every PetGameInfo property is Save|Copy|Public and none is Transmit, so a Transmit mask wrote an empty
        // object and the game crashed reading it.
        var serializer = new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.None);
        if (!serializer.Serialize(game, PropertyFlags.Prop_Public, out var gameInfo)) {
            Logger.Error("Could not write the pet game info of {0}.", Logger.Args(gameName));

            return;
        }

        // Skipping is free here: fill the energy bar so the game lets the player skip and feed right away.
        var petOwner = playerCharacter?.PetOwnerBehavior;
        var maxEnergy = playerCharacter?.GameStats?.m_energyMax ?? 0;
        if (petOwner is not null && maxEnergy > 0 && petOwner.Energy < maxEnergy) {
            playerCharacter.UpdateEnergy(maxEnergy);
            playerActor.Tell(new PET_9_PROTOCOL.MSG_PETENERGYTICK {
                GlobalID = playerCharacter.GameObjectID,
                Energy = petOwner.Energy,
                MaxEnergy = playerCharacter.GameStats.GetClientTypeAlternative().m_energyMax,
                TickTime = (int) (System.DateTimeOffset.UtcNow.ToUnixTimeSeconds() + Imlight.CoreLib.Game.Pet.AdvancedPetsConfig.EnergyTickInSeconds),
            });
        }

        Logger.Information("{0} opened the pet game window for {1}.",
            Logger.Args(playerCharacter?.PlayerNameBehavior.GetWizardName(), gameName));
        playerActor.Tell(new WIZARD_12_PROTOCOL.MSG_PETGAMEKIOSK {
            GlobalID = Entity.ActiveGameObject.m_globalID,
            GameInfo = gameInfo,
            // Enough wins that the game offers Skip without having to win first.
            GamesWon = SKIP_WINS,
        });
    }

}
