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

using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.Cryptography;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.petform` (Niduus, 2026-10-23: "Play as your pet"): the wizard takes the look of the pet they wear, the way a
/// transformation elixir works. A pet template names its model by race (PetItemBehaviorTemplate.m_eRace), and
/// Races.xml holds every pet race; the colors and pattern pick the pet's textures, as on the summoned pet
/// (PetFactory.GetPetLook). Typed again, it turns back.
/// </summary>
internal class CommandPetFormProtocol : CommandProtocol {

    internal const string EFFECT_NAME = "Transformation";

    // Marks the pet look among the wizard's effects; an elixir's transformation carries its own slot.
    internal static readonly uint SlotId = StringHash.Compute("PetForm");

    internal override string Group { get; set; } = "";

    [Command("petform")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    [Alias("playaspet")]
    private void PetFormCommand()
        => Toggle(Context.Character, Context.SessionActor, Context.ZoneActor, text => InformSenderClient(text));

    internal static bool IsInPetForm(Wizard wizard)
        => wizard?.GameEffects?.Exists(e => e is TransformationEffect && e.m_itemSlotID == SlotId) == true;

    /// <summary>
    /// Turns the pet look on, or off when it is on. Used by `.petform` and the game's Play as Pet button.
    /// </summary>
    internal static void Toggle(Wizard wizard, IActorRef session, IActorRef zone, System.Action<string> inform) {
        var current = wizard.GameEffects.Find(e => e is TransformationEffect && e.m_itemSlotID == SlotId);
        if (current is not null) {
            wizard.GameEffects.Remove(current);
            Broadcast(session, zone, new GAME_5_PROTOCOL.MSG_REMOVEEFFECT {
                GameObjectID = wizard.GameObjectID,
                EffectNameID = current.m_effectNameID,
                InternalID = current.m_internalID,
            });
            session.Tell(new ZONE_102_PROTOCOL.MSG_PETFORMCHANGED { On = false }, null);
            TellGamePlayingAsPet(wizard, session, false);
            inform("You look like yourself again.");

            return;
        }

        var pet = PetCare.GetWornPet(wizard);
        if (PetFactory.GetPetLook(pet) is not { } look) {
            inform("Wear a pet first.");

            return;
        }

        // The race alone gave the race's first texture: pets that share a race (a Black Cat and a Fire Cat) all
        // showed the same one, and a pet stitched at Zeke showed its own kind (Niduus, 2026-10-23: "sometimes uses
        // the wrong model"). The game paints a transformation's colors and pattern the way it paints the summoned
        // pet (ClientTransformationEffect::ApplyEffectToBody), so they carry the pet's own textures.
        var effect = new TransformationEffect {
            m_sRace = look.Race.ToString(),
            m_eGender = look.Gender,
            m_fScale = look.Scale,
            m_nPrimaryColor = look.Primary,
            m_nSecondaryColor = look.Secondary,
            m_nPatternOption = look.Pattern,
            m_nGeometryOption = 0,
            m_effectNameID = StringHash.Compute(EFFECT_NAME),
            m_itemSlotID = SlotId,
            m_internalID = wizard.NextEffectId(),
        };

        var serializer = new CoreObjectSerializer(behaviors: SerializerFlags.None);
        var flags = PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit;
        if (!serializer.Serialize(effect, flags, out var data)) {
            inform("Could not turn you into your pet.");

            return;
        }

        wizard.GameEffects.Add(effect);
        Broadcast(session, zone, new GAME_5_PROTOCOL.MSG_ADDEFFECT {
            GameObjectID = wizard.GameObjectID,
            EffectData = data,
        });
        session.Tell(new ZONE_102_PROTOCOL.MSG_PETFORMCHANGED { On = true }, null);
        TellGamePlayingAsPet(wizard, session, true);
        Logger.Information("{0} took the look of their pet {1} (race {2}, colors {3}/{4}, pattern {5}, scale {6}).",
            Logger.Args(wizard.CharId, pet.m_templateID, (string) effect.m_sRace, look.Primary, look.Secondary, look.Pattern, look.Scale));
        inform("You now look like your pet. Press the button or type .petform again to turn back.");
    }

    // The game's own Play as Pet state (ClientPetOwnerBehavior.m_playingAsPet). Without the answer the game kept sending
    // "switch to pet" (Status 1) on every press and its HUD never changed. Sent back the way it came, with the new state.
    private static void TellGamePlayingAsPet(Wizard wizard, IActorRef session, bool on) {
        wizard.PetOwnerBehavior.PlayingAsPet = on;
        session.Tell(new WIZARD2_53_PROTOCOL.MSG_SWITCHTOPET {
            GlobalID = wizard.PetOwnerBehavior.EquippedPetGlobalId,
            Status = (byte) (on ? 1 : 0),
            PetTemplate = (uint) wizard.PetOwnerBehavior.EquippedPetTemplateId,
        }, null);
    }

    // The wizard's own game first, then everyone else in the zone.
    private static void Broadcast(IActorRef session, IActorRef zone, IMessage message) {
        session.Tell(message, null);
        zone?.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Message = message,
            Selfless = true,
            Sender = session,
            Targets = ZoneBroadcastTarget.Players,
        });
    }

}
