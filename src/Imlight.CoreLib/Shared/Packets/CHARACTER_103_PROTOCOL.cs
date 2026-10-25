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

using Akka.Actor;
using Imcodec.IO;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Shared.Packets;

/// <summary>
/// What a <see cref="CHARACTER_103_PROTOCOL.MSG_BADGEEVENT"/> asks the badge service to do.
/// </summary>
public enum BadgeEventKind : byte {
    Award,
    Recheck,
}

public class CHARACTER_103_PROTOCOL : IServerProtocol {

    public byte ServiceID { get; } = 103;
    public string ProtocolType { get; } = "CHARACTER";
    public int ProtocolVersion { get; } = 1;
    public string ProtocolDescription { get; } = "Internal Character General Messages.";

    public class MSG_SETACTIVEWIZARD : IServerMessage {

        public byte MessageOrder { get; } = 1;
        public byte ServiceID { get; } = 103;

        public Wizard Wizard;

    }

    public class MSG_QUERYACTIVEWIZARD : IServerMessage {

        public byte MessageOrder { get; } = 2;
        public byte ServiceID { get; } = 103;

    }

    public class MSG_CHARACTER : IServerMessage {

        public byte MessageOrder { get; } = 3;
        public byte ServiceID { get; } = 103;

        public Wizard Wizard;
        public CoreObject WizardGameObject;

    }

    public class MSG_LEVELUP : IServerMessage {

        public byte MessageOrder { get; } = 4;
        public byte ServiceID { get; } = 103;

        public byte NewLevel;

    }

    public sealed class MSG_DOTELEPORTEFFECTS : IServerMessage {

        public byte MessageOrder { get; } = 5;
        public byte ServiceID { get; } = 103;

    }

    public sealed class MSG_ENTERSTATE : IServerMessage {

        public byte MessageOrder { get; } = 6;
        public byte ServiceID { get; } = 103;

        public string StateName;

    }

    public sealed class MSG_ADDDYNAMOD : IServerMessage {

        public byte MessageOrder { get; } = 7;
        public byte ServiceID { get; } = 103;

        public ResAddDynaMod DynaMod;
        public IActorRef ContextActor;

    }

    public sealed class MSG_REMOVEDYNAMOD : IServerMessage {

        public byte MessageOrder { get; } = 8;
        public byte ServiceID { get; } = 103;

        public ResRemoveDynaMod DynaMod;
        public IActorRef ContextActor;

    }

    public sealed class MSG_DOENERGYTICK : IServerMessage {

        public byte MessageOrder { get; } = 9;
        public byte ServiceID { get; } = 103;

    }

    public sealed class MSG_BUDDYREQUESTADDFWD : IServerMessage {

        public byte MessageOrder { get; } = 9;
        public byte ServiceID { get; } = 103;

        // Internal social routing always uses saved character IDs.
        public ulong RequesterCharId;
        public ulong RecipientCharId;
        public ByteString OwnerName;
        public byte OwnerLevel;
        public ByteString OwnerSchool;
        public byte Remove;

    }

    public sealed class MSG_BUDDYREQUESTREPLYFWD : IServerMessage {

        public byte MessageOrder { get; } = 10;
        public byte ServiceID { get; } = 103;

        // Internal social routing always uses saved character IDs.
        public ulong RequesterCharId;
        public ulong RecipientCharId;
        public bool Accept;
        public Relationship NewRelationship;

    }

    public sealed class MSG_BUDDYDROPFWD : IServerMessage {

        public byte MessageOrder { get; } = 11;
        public byte ServiceID { get; } = 103;

        // Internal social routing always uses saved character IDs.
        public ulong RequesterCharId;
        public ulong RecipientCharId;

    }

    public sealed class MSG_GAINXP : IServerMessage {

        public byte MessageOrder { get; } = 12;
        public byte ServiceID { get; } = 103;

        public int XP;

    }

    public sealed class MSG_EXECUTERESULTS : IServerMessage {

        public byte MessageOrder { get; } = 13;
        public byte ServiceID { get; } = 103;

        public IActorRef PlayerRef;
        public CoreObject PlayerObj;
        public IActorRef ReplyTo;

    }

    public sealed class MSG_RESULTEXECUTED : IServerMessage {

        public byte MessageOrder { get; } = 14;
        public byte ServiceID { get; } = 103;

        public bool Success;

    }

    public sealed class MSG_INITIALIZEHANDLER : IServerMessage {

        public byte MessageOrder { get; } = 15;
        public byte ServiceID { get; } = 103;

        public object Context;
        public object Result;

    }

    public sealed class MSG_EXECUTEHANDLER : IServerMessage {

        public byte MessageOrder { get; } = 16;
        public byte ServiceID { get; } = 103;

    }

    public sealed class MSG_SENDQUESTOFFERCACHEOPTION : IServerMessage {

        public byte MessageOrder { get; } = 17;
        public byte ServiceID { get; } = 103;

        public QuestTemplate Quest;

    }

    public sealed class MSG_COMPLETEPERSONAGOAL : IServerMessage {

        public byte MessageOrder { get; } = 18;
        public byte ServiceID { get; } = 103;

        public ulong QuestID;
        public ulong GoalID;

        /// <summary>
        /// Optional: told <see cref="TransitionMessage"/> once the goal has actually completed (after its
        /// completion dialog closes), so the NPC can re-offer only after the quest is done.
        /// </summary>
        public IActorRef TransitionTarget;
        public IServerMessage TransitionMessage;

    }

    public sealed class MSG_COMPLETEUSAGEGOAL : IServerMessage {

        public byte MessageOrder { get; } = 19;
        public byte ServiceID { get; } = 103;

        public ulong QuestID;
        public ulong GoalID;

        /// <summary>
        /// True when another goal completed by the same use already shows this goal's completion dialog.
        /// </summary>
        public bool SuppressCompletionDialog;

    }

    public sealed class MSG_RESULTHANDLERCOMPLETED : IServerMessage {

        public byte MessageOrder { get; } = 20;
        public byte ServiceID { get; } = 103;

        public bool Success { get; init; }
        public System.Type ResultType { get; init; }

    }

    /// <summary>
    /// Internal timer message: fired when a pet egg's hatch time arrives.
    /// </summary>
    public sealed class MSG_DOEGGHATCH : IServerMessage {

        public byte MessageOrder { get; } = 21;
        public byte ServiceID { get; } = 103;

        public ulong EggGlobalId;

    }

    /// <summary>
    /// Sent by the ShopService to the EquipmentService after the equipped pet item changed its look or
    /// name: the pet following its owner is replaced by one built from <see cref="PetItemId"/>.
    /// </summary>
    public sealed class MSG_RESUMMONPET : IServerMessage {

        public byte MessageOrder { get; } = 22;
        public byte ServiceID { get; } = 103;

        public ulong PetItemId;

    }

    /// <summary>
    /// Asks an online player's session to answer another player's MSG_BUDDYSTATS request from its live
    /// character, replying straight to the requester's session.
    /// </summary>
    public sealed class MSG_BUDDYSTATSFWD : IServerMessage {

        public byte MessageOrder { get; } = 23;
        public byte ServiceID { get; } = 103;

        public GAME_5_PROTOCOL.MSG_BUDDYSTATS Request;
        public ulong BuddyCharId;
        public IActorRef Requester;
        public ulong RequesterGameObjectId;

    }

    /// <summary>
    /// Internal timer message: a persona goal's completion dialog was never reported closed
    /// (MSG_COMPLETEDIALOG), so the QuestService completes the goal anyway.
    /// </summary>
    public sealed class MSG_GOALDIALOGTIMEOUT : IServerMessage {

        public byte MessageOrder { get; } = 24;
        public byte ServiceID { get; } = 103;

        public ulong QuestID;
        public ulong GoalID;

    }

    /// <summary>
    /// Sent by the ResCinematicActor handler to the player's session: the CinematicService plays it.
    /// </summary>
    public sealed class MSG_PLAYCINEMATICACTOR : IServerMessage {

        public byte MessageOrder { get; } = 25;
        public byte ServiceID { get; } = 103;

        public ResCinematicActor Cinematic;
        public IActorRef ZoneActor;

    }

    /// <summary>
    /// Internal timer message: a cinematic actor's state has run its auto transition time.
    /// </summary>
    public sealed class MSG_CINEMATICAUTOSTATE : IServerMessage {

        public byte MessageOrder { get; } = 26;
        public byte ServiceID { get; } = 103;

        public int RunId;
        public string StateName;

    }

    /// <summary>
    /// Internal timer message: a cinematic dialog was never reported closed.
    /// </summary>
    public sealed class MSG_CINEMATICDIALOGTIMEOUT : IServerMessage {

        public byte MessageOrder { get; } = 27;
        public byte ServiceID { get; } = 103;

        public int RunId;
        public string StateName;

    }

    /// <summary>
    /// Internal timer message: a finished cinematic's actor is removed from the client.
    /// </summary>
    public sealed class MSG_CINEMATICREMOVEACTOR : IServerMessage {

        public byte MessageOrder { get; } = 28;
        public byte ServiceID { get; } = 103;

        public int RunId;

    }

    /// <summary>
    /// Sent to every player's session when a trigger puts a named zone object into a state: objects only the
    /// client owns (doors, collision) learn it through a dynamic mod, as live does.
    /// </summary>
    public sealed class MSG_SENDDYNAMODSTATE : IServerMessage {

        public byte MessageOrder { get; } = 29;
        public byte ServiceID { get; } = 103;

        public string ObjectName;
        public string StateName;

    }

    /// <summary>
    /// Sent by the quest commands: the quest service runs the action through its normal goal, quest and
    /// removal paths and replies to the player with a server message.
    /// </summary>
    public sealed class MSG_QUESTCOMMAND : IServerMessage {

        public byte MessageOrder { get; } = 30;
        public byte ServiceID { get; } = 103;

        /// <summary>
        /// One of: remove, completegoal, complete, grant, forcegrant, list, info.
        /// </summary>
        public string Action;

        /// <summary>
        /// The quest name, or empty for the only active quest.
        /// </summary>
        public string QuestName;

    }

    /// <summary>
    /// Sent by a service to the EquipmentService: takes the equipped item off the player the way an unequip
    /// request from the client does. The EquipmentService answers with <see cref="MSG_UNEQUIPITEMRSP"/>.
    /// </summary>
    public sealed class MSG_UNEQUIPITEM : IServerMessage {

        public byte MessageOrder { get; } = 31;
        public byte ServiceID { get; } = 103;

        public ulong ItemId;

    }

    /// <summary>
    /// The EquipmentService's answer to <see cref="MSG_UNEQUIPITEM"/>.
    /// </summary>
    public sealed class MSG_UNEQUIPITEMRSP : IServerMessage {

        public byte MessageOrder { get; } = 32;
        public byte ServiceID { get; } = 103;

        /// <summary>
        /// True when the item is no longer equipped.
        /// </summary>
        public bool Success;

    }

    /// <summary>
    /// Sent to the BadgeService: award one badge, or recheck the auto-add badges against the character's state.
    /// </summary>
    public sealed class MSG_BADGEEVENT : IServerMessage {

        public byte MessageOrder { get; } = 40;
        public byte ServiceID { get; } = 103;

        public BadgeEventKind Kind;

        /// <summary>
        /// The badge to award; only used by <see cref="BadgeEventKind.Award"/>.
        /// </summary>
        public uint BadgeId;

    }

    /// <summary>
    /// Sent by the badge commands: the badge service runs the action through its normal award, counter and title paths and replies to the player with a server message.
    /// </summary>
    public sealed class MSG_BADGECOMMAND : IServerMessage {

        public byte MessageOrder { get; } = 41;
        public byte ServiceID { get; } = 103;

        /// <summary>
        /// One of: grant, progress, list, reset, title.
        /// </summary>
        public string Action;

        /// <summary>
        /// The badge id or name for grant, the counter key for progress, the title key for title.
        /// </summary>
        public string Argument;

        /// <summary>
        /// The amount added to the counter; only used by progress.
        /// </summary>
        public ulong Amount;

    }

    /// <summary>
    /// Sent by a bank NPC to the BankService of the player who picked its bank option: the bank counts as open
    /// until the window is closed, the zone changes or the session attaches again.
    /// </summary>
    public sealed class MSG_BEGINBANKSESSION : IServerMessage {

        public byte MessageOrder { get; } = 50;
        public byte ServiceID { get; } = 103;

        /// <summary>
        /// The global id of the bank NPC.
        /// </summary>
        public ulong EntityId;

    }

    /// <summary>
    /// Sent by the garden command to the HousingService of the player: shows the garden, grows it for testing or sets the
    /// gardening rank (Game/Services/HousingService.Gardening).
    /// </summary>
    public sealed class MSG_GARDENCOMMAND : IServerMessage {

        public byte MessageOrder { get; } = 147;
        public byte ServiceID { get; } = 103;

        /// <summary>status, grow, ready, rank, spells, seeds, speed, pause, resume, stage, age or times.</summary>
        public string Action;

        public int Value;

        /// <summary>The rest of the command: speed takes "[part] number", stage a stage name, age a time.</summary>
        public string Text;

    }

    /// <summary>
    /// Sent by an Item Finder object (a placed Backpack Butler or a bank) or the itemfinder command to the BankService
    /// of the player: it opens the game's Item Finder window and fills it with the bank, the attic and the houses.
    /// </summary>
    public sealed class MSG_BEGINITEMFINDER : IServerMessage {

        public byte MessageOrder { get; } = 90;
        public byte ServiceID { get; } = 103;

        /// <summary>
        /// The global id of the object the window was opened from; 0 for the command.
        /// </summary>
        public ulong EntityId;

    }

    /// <summary>
    /// Sent by the EquipmentService to the RentalService after a rental item was equipped: the timer of the rental
    /// starts if it has not started yet.
    /// </summary>
    public sealed class MSG_STARTRENTAL : IServerMessage {

        public byte MessageOrder { get; } = 60;
        public byte ServiceID { get; } = 103;

        public ulong ItemId;

    }

    /// <summary>
    /// Internal timer message and request to the RentalService: expires every rental of the player that has run out,
    /// then arms the timer for the next one.
    /// </summary>
    public sealed class MSG_CHECKRENTALS : IServerMessage {

        public byte MessageOrder { get; } = 61;
        public byte ServiceID { get; } = 103;

    }

    /// <summary>
    /// Sent by the RentalService to the EquipmentService: the rental has run out, so it is taken off the player and removed.
    /// </summary>
    public sealed class MSG_EXPIRERENTAL : IServerMessage {

        public byte MessageOrder { get; } = 62;
        public byte ServiceID { get; } = 103;

        public ulong ItemId;

    }

    /// <summary>
    /// Fired every second to tick down active elixir playtime.
    /// </summary>
    public sealed class MSG_DOELIXIRTICK : IServerMessage {

        public byte MessageOrder { get; } = 63;
        public byte ServiceID { get; } = 103;

    }

    /// <summary>
    /// Synchronizes an updated Crown Shop wishlist to an online character's session.
    /// </summary>
    public sealed class MSG_UPDATE_WISHLIST : IServerMessage {

        public byte MessageOrder { get; } = 33;
        public byte ServiceID { get; } = 103;

        public ulong CharId;
        public ByteString WishlistData;
        public uint RemovedTemplateId;

    }

    /// <summary>
    /// Asks PetService to breed two pets into an egg for this session's wizard (Niduus, 2026-10-20). ParentA is
    /// the wizard's own pet; ParentB may be another player's.
    /// </summary>
    internal sealed class MSG_BREEDPETS : IServerMessage {

        public byte MessageOrder { get; } = 70;
        public byte ServiceID { get; } = 103;

        public PetParent ParentA;
        public PetParent ParentB;
        public HatchSource Source;
        // In person: the partner's game object id, for MSG_PETHATCHRESULT.
        public ulong PartnerGameObjectId;

    }

    /// <summary>
    /// What one hatching partner's session tells the other's (see HatchService).
    /// </summary>
    internal enum HatchPeerKind : byte {
        Invite,
        Refused,
        Join,
        Ready,
        PetChosen,
        Left,
        // In the pet morph zone: Hatch (1) or Lend (2) on the "Hatch or Lend Pet" screen.
        Choice,
        // In the pet morph zone: the hatcher's egg is made, so the lender goes back too (they get no egg).
        EggMade,
        // On the sigil: the sender stepped off during the pair's countdown, so the partner's countdown stops too.
        TripCancelled,
    }

    /// <summary>
    /// Sent between the sessions of two players hatching in person.
    /// </summary>
    internal sealed class MSG_HATCHPEER : IServerMessage {

        public byte MessageOrder { get; } = 71;
        public byte ServiceID { get; } = 103;

        public HatchPeerKind Kind;
        public ulong FromCharId;
        public IActorRef FromSession;
        public IActorRef FromZone;
        public int Value;
        public PetParent Pet;
        // In the pet morph zone: the sender's PetGameMorphPartner (character id and pets), written for the game.
        public ByteString MorphData;

    }

    /// <summary>
    /// The hatchmaking kiosk was used: opens the kiosk window for this session.
    /// </summary>
    internal sealed class MSG_OPENHATCHKIOSK : IServerMessage {

        public byte MessageOrder { get; } = 72;
        public byte ServiceID { get; } = 103;

        public ulong KioskGID;

    }

    /// <summary>
    /// The in-person hatch invite went unanswered for too long.
    /// </summary>
    internal sealed class MSG_HATCHINVITETIMEOUT : IServerMessage {

        public byte MessageOrder { get; } = 73;
        public byte ServiceID { get; } = 103;

        public ulong PeerCharId;

    }

    /// <summary>
    /// What a pet morphing sigil in the Pet Pavilion hatchery asks of a session (see InteractPetMorphSigilComponent).
    /// </summary>
    internal enum HatchSigilRole : byte {
        // The player pressed a solo (1P) sigil: hatch two of their own pets.
        Solo,
        // The player pressed a duo sigil with another player on it: ask that player to hatch in person.
        Requester,
        // Another player pressed the duo sigil this player stands on, naming this player.
        Partner,
        // The player pressed a duo sigil nobody else stands on.
        Alone,
        // The player pressed a duo sigil another player stands on who has not pressed X yet: wait for them.
        WaitingForPartner,
    }

    /// <summary>
    /// Sent by a pet morphing sigil (zone actor) to the session of a player on it.
    /// </summary>
    internal sealed class MSG_HATCHSIGIL : IServerMessage {

        public byte MessageOrder { get; } = 74;
        public byte ServiceID { get; } = 103;

        public HatchSigilRole Role;
        public ulong SigilGID;
        public string SigilName;
        public ulong PartnerCharId;
        public ulong PartnerGameObjectId;
        public string PartnerName;
        // The pad, for the sigil countdown and the way back: "X,Y,Z,heading", its sigil type and radius.
        public string SigilLoc;
        public string SigilType;
        public float Radius;
        // The zone the pad is in.
        public string ZoneName;
        // Which of the pad's circles this player is put on: 0 for the first player on the pad, 1 for the second.
        public int FaceSlot;
        // From the in-person hatch window (no sigil): straight to the hatching room, with no countdown.
        public bool FromHatchWindow;
        // The pad's countdown: seconds left for this player (0 = the usual length). The pad runs one countdown; a
        // player hopping back on joins it instead of starting it over (Niduus, 2026-10-21).
        public float CountdownSeconds;
        // Keep a countdown this player already has running (only the destination changes).
        public bool KeepCountdown;

    }

    /// <summary>
    /// Why the trip to the pet morph zone did not happen (see <see cref="MSG_HATCHTRIPFAILED"/>).
    /// </summary>
    internal enum HatchTripFailure : byte {
        // The player stepped off the pad during the countdown.
        Cancelled,
        // The session was already counting down or travelling.
        Busy,
        // The zone could not be entered (not loaded, refused).
        TransferFailed,
    }

    /// <summary>
    /// Told by the ZoneService to the HatchService when a hatching sigil's countdown or transfer to the pet morph zone
    /// does not go through.
    /// </summary>
    internal sealed class MSG_HATCHTRIPFAILED : IServerMessage {

        public byte MessageOrder { get; } = 75;
        public byte ServiceID { get; } = 103;

        public HatchTripFailure Failure;
        public string Detail;

    }

    /// <summary>
    /// Told by the PetService to the HatchService after a hatch from the pet morph zone: whether the egg was made.
    /// </summary>
    internal sealed class MSG_HATCHBRED : IServerMessage {

        public byte MessageOrder { get; } = 76;
        public byte ServiceID { get; } = 103;

        public bool Success;

    }

    /// <summary>
    /// The HatchService's timers in the pet morph zone (see HatchService).
    /// </summary>
    internal enum HatchZoneTimerKind : byte {
        // Send the game the morph window's data, once it has had a moment to load the zone.
        Init,
        // The partner has not arrived; hatch alone.
        PartnerWait,
        // Too long in the zone; back to the hatchery.
        Timeout,
        // The egg is made (or the hatch is over); back to the hatchery.
        Return,
    }

    internal sealed class MSG_HATCHZONETIMER : IServerMessage {

        public byte MessageOrder { get; } = 77;
        public byte ServiceID { get; } = 103;

        public HatchZoneTimerKind Kind;
        public string Reason;

    }

    /// <summary>
    /// A Pixie Post for the online wizard whose session gets this (PixiePostService).
    /// </summary>
    internal sealed class MSG_PIXIEPOSTDELIVER : IServerMessage {

        public byte MessageOrder { get; } = 78;
        public byte ServiceID { get; } = 103;

        public ulong RecipientCharId;
        public PixiePost Post;

    }

    /// <summary>
    /// Moves the player inside the zone they are in, to a spot (a compact "x,y,z,yaw" string): a raid's teleport pads
    /// (Game/Raids).
    /// </summary>
    public sealed class MSG_MOVEINZONE : IServerMessage {

        public byte MessageOrder { get; } = 148;
        public byte ServiceID { get; } = 103;

        public string Location;

    }

    /// <summary>
    /// Sends the player out of a raid, back to the guild grounds they came from (Game/Raids).
    /// </summary>
    public sealed class MSG_RAIDLEAVE : IServerMessage {

        public byte MessageOrder { get; } = 149;
        public byte ServiceID { get; } = 103;

    }

    /// <summary>
    /// Gives the player the drops of the raid they won (Game/Raids/RaidRewards): every wizard in the raid gets them.
    /// </summary>
    public sealed class MSG_RAIDREWARD : IServerMessage {

        public byte MessageOrder { get; } = 150;
        public byte ServiceID { get; } = 103;

        public string Zone;

    }

}
