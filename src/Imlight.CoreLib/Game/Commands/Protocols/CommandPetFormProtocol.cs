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

using System.Collections.Concurrent;
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.Cryptography;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imcodec.IO;
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

    // The wizards in their pet's look, by character. A zone change rebuilds the wizard's effects from the gear
    // (CharacterHelper.RecalculateGameStats) and may load the wizard again, so the look is put back from here.
    private static readonly ConcurrentDictionary<ulong, byte> s_inPetForm = new();

    // Pet games (the kiosk's rooms) have the player steer the pet itself, so the look waits outside them.
    private const string PetGameWorldPrefix = "ThePhantomZoneWorld/";

    /// <summary>
    /// Turns the pet look on, or off when it is on. Used by `.petform` and the game's Play as Pet button.
    /// </summary>
    internal static void Toggle(Wizard wizard, IActorRef session, IActorRef zone, System.Action<string> inform) {
        if (IsInPetForm(wizard)) {
            End(wizard, session, zone, inform);
        }
        else {
            Start(wizard, session, zone, inform);
        }
    }

    /// <summary>
    /// Turns the pet look off and tells the game, if the wizard has it. Used when the pet comes off and when a battle
    /// starts, so nothing is left behind. Returns whether it was on.
    /// </summary>
    internal static bool End(Wizard wizard, IActorRef session, IActorRef zone, System.Action<string> inform) {
        if (wizard is null) {
            return false;
        }

        s_inPetForm.TryRemove(wizard.CharId, out _);
        var current = wizard.GameEffects.Find(e => e is TransformationEffect && e.m_itemSlotID == SlotId);
        if (current is null) {
            // The game may still think it plays as the pet (its HUD), so it is told either way.
            if (wizard.PetOwnerBehavior.PlayingAsPet) {
                TellGamePlayingAsPet(wizard, session, false);
            }

            return false;
        }

        wizard.GameEffects.Remove(current);
        Broadcast(session, zone, new GAME_5_PROTOCOL.MSG_REMOVEEFFECT {
            GameObjectID = wizard.GameObjectID,
            EffectNameID = current.m_effectNameID,
            InternalID = current.m_internalID,
        });
        session.Tell(new ZONE_102_PROTOCOL.MSG_PETFORMCHANGED { On = false }, null);
        TellGamePlayingAsPet(wizard, session, false);
        inform?.Invoke("You look like yourself again.");

        return true;
    }

    private static void Start(Wizard wizard, IActorRef session, IActorRef zone, System.Action<string> inform) {
        // A battle keeps the wizard's own look (the look ends when one starts, CombatService).
        if (wizard.IsInDuel) {
            TellGamePlayingAsPet(wizard, session, false);
            inform("You can't play as your pet during a battle.");

            return;
        }

        if (IsPetGameZone(wizard)) {
            TellGamePlayingAsPet(wizard, session, false);
            inform("You can't play as your pet during a pet game.");

            return;
        }

        var pet = PetCare.GetWornPet(wizard);
        if (!TryMakeEffect(wizard, out var effect, out var look) || !TrySerialize(effect, out var data)) {
            TellGamePlayingAsPet(wizard, session, false);
            inform(pet is null ? "Wear a pet first." : "Could not turn you into your pet.");

            return;
        }

        wizard.GameEffects.Add(effect);
        s_inPetForm[wizard.CharId] = 0;
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

    /// <summary>
    /// After a zone change (doors, .tp, teleporters, Home, dungeons, the Manager's teleports): the wizard's effects were
    /// just rebuilt from the gear, so a wizard who played as their pet gets the look back, as long as they still wear
    /// a pet. Called before the wizard's object is made, so the game is told the right Play as Pet state with it.
    /// </summary>
    internal static void RestoreAfterZoneChange(Wizard wizard) {
        if (wizard is null) {
            return;
        }

        var wanted = s_inPetForm.ContainsKey(wizard.CharId);
        wizard.PetOwnerBehavior.PlayingAsPet = false;
        if (!wanted || IsInPetForm(wizard)) {
            wizard.PetOwnerBehavior.PlayingAsPet = IsInPetForm(wizard);

            return;
        }

        // In a pet game the look waits, and comes back on the way out.
        if (IsPetGameZone(wizard)) {
            return;
        }

        if (!TryMakeEffect(wizard, out var effect, out _)) {
            // The pet came off somewhere along the way: the wizard is simply themselves again.
            s_inPetForm.TryRemove(wizard.CharId, out _);
            Logger.Information("{0} played as their pet but wears none after the zone change; the look ended.",
                Logger.Args(wizard.CharId));

            return;
        }

        wizard.GameEffects.Add(effect);
        wizard.PetOwnerBehavior.PlayingAsPet = true;
    }

    /// <summary>A new login starts in the wizard's own look, like the game does.</summary>
    internal static void Forget(ulong charId)
        => s_inPetForm.TryRemove(charId, out _);

    private static bool IsPetGameZone(Wizard wizard)
        => wizard.Zone?.StartsWith(PetGameWorldPrefix, System.StringComparison.OrdinalIgnoreCase) == true;

    private static bool TryMakeEffect(Wizard wizard, out TransformationEffect effect, out (eRace Race, eGender Gender, float Scale, int Primary, int Secondary, int Pattern) look) {
        effect = null;
        look = default;
        if (PetFactory.GetPetLook(PetCare.GetWornPet(wizard)) is not { } found) {
            return false;
        }

        look = found;
        // The race alone gave the race's first texture: pets that share a race (a Black Cat and a Fire Cat) all
        // showed the same one, and a pet stitched at Zeke showed its own kind (Niduus, 2026-10-23: "sometimes uses
        // the wrong model"). The game paints a transformation's colors and pattern the way it paints the summoned
        // pet (ClientTransformationEffect::ApplyEffectToBody), so they carry the pet's own textures.
        effect = new TransformationEffect {
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

        return true;
    }

    private static bool TrySerialize(TransformationEffect effect, out ByteString data) {
        var serializer = new CoreObjectSerializer(behaviors: SerializerFlags.None);
        var flags = PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit;

        return serializer.Serialize(effect, flags, out data);
    }

    /// <summary>The pet look as a message for one more player's game (a player who arrives later), or null.</summary>
    internal static IMessage LookMessage(Wizard wizard) {
        var form = wizard?.GameEffects?.Find(e => e is TransformationEffect && e.m_itemSlotID == SlotId);
        if (form is not TransformationEffect effect || !TrySerialize(effect, out var data)) {
            return null;
        }

        return new GAME_5_PROTOCOL.MSG_ADDEFFECT { GameObjectID = wizard.GameObjectID, EffectData = data };
    }

    /// <summary>Tells the game it plays as its pet again, after a zone change put the look back.</summary>
    internal static void TellGameAfterZoneChange(Wizard wizard, IActorRef session) {
        if (IsInPetForm(wizard)) {
            TellGamePlayingAsPet(wizard, session, true);
        }
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
