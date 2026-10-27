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
using Akka.Actor;
using Imcodec.Math;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Networking;

namespace Imlight.CoreLib.Shared.Packets; 

public sealed class COMBAT_106_PROTOCOL : IServerProtocol {

    public byte ServiceID => 106;
    public string ProtocolType => "Wizard dueling messages";
    public int ProtocolVersion => 1;
    public string ProtocolDescription => "Internal messages for dueling.";

    public sealed class MSG_STARTDUEL : IServerMessage {

        public byte MessageOrder => 1;
        public byte ServiceID => 106;

        public Dictionary<IActorRef, CoreObject> Participants;
        public IActorRef SigilActor;
        public ulong SigilId;
        public Vector3 SigilLocation;
        public Vector3 SigilOrientation;
        public CombatSigilTemplate SigilTemplate;

    }

    public sealed class MSG_ENDDUEL : IServerMessage {

        public byte MessageOrder => 2;
        public byte ServiceID => 106;

        public Dictionary<IActorRef, CoreObject> Participants;

    }

    public sealed class MSG_DUELDETAILS : IServerMessage {

        public byte MessageOrder => 3;
        public byte ServiceID => 106;

        public IActorRef DuelActor;
        public Duel Duel;
        public byte CreatureCount;
        public byte PlayerCount;

    }

    public sealed class MSG_ADDPARTICIPANT : IServerMessage {

        public byte MessageOrder => 4;
        public byte ServiceID => 106;

        public IActorRef Participant;
        public CoreObject ParticipantObject;

    }

    public sealed class MSG_COMBATDEATH : IServerMessage {

        public byte MessageOrder => 5;
        public byte ServiceID => 106;

    }

    public sealed class MSG_NEWROUND : IServerMessage {

        public byte MessageOrder => 8;
        public byte ServiceID => 106;

        public int Round;

    }

    internal sealed class MSG_ACTORADDEDTODUEL : IServerMessage {

        public byte MessageOrder => 9;
        public byte ServiceID => 106;

        public IActorRef DuelActor;
        public CombatDuelComponent Duel;
        public CombatDuelSubCircle SubCircle;
        public Vector3 SlotPosition;
        public float SlotOrientation;

    }

    public sealed class MSG_ACTORCOMBATMOVE : IServerMessage {

        public byte MessageOrder => 10;
        public byte ServiceID => 106;

        public IActorRef Actor;
        public byte MoveType;
        public byte SpellSelection;
        public uint SpellTarget;
        // SpellTarget as the client sent it, before it was turned from a bit into an index.
        public uint RawSpellTarget;
        public int TimeLeft;
        public int ShadowPactTarget;
        public int SelectedTieredSpellID;

    }

    public sealed class MSG_PLANNINGPHASEOVER : IServerMessage {

        public byte MessageOrder => 11;
        public byte ServiceID => 106;

    }

    public sealed class MSG_ROUNDRESOLUTION : IServerMessage {

        public byte MessageOrder => 12;
        public byte ServiceID => 106;

    }

    public sealed class MSG_QUERYCREATURESTATS : IServerMessage {

        public byte MessageOrder => 13;
        public byte ServiceID => 106;

    }

    public sealed class MSG_CREATURESTATS : IServerMessage {

        public byte MessageOrder => 14;
        public byte ServiceID => 106;

        public ServerWizGameStats GameStats;
        public float CombatIntelligence;
        public float CombatSelfishFactor;
        public float CombatAggressionFactor;
        public int CombatLevel;
        public MagicSchool MagicSchool;
        public List<SpellData> SpellList;

    }

    public sealed class MSG_COMBATDEFEAT : IServerMessage {

        public byte MessageOrder => 15;
        public byte ServiceID => 106;

        /// <summary>
        /// True when the player was still dead as the duel ended, as opposed to fleeing.
        /// </summary>
        public bool DiedInDuel;

        /// <summary>
        /// Where the player goes instead of the world's hub, when the zone is a copy with an exit of its own; else null.
        /// </summary>
        public string ExitZone;
        public string ExitLocation;

    }

    public sealed class MSG_COMBATWIN : IServerMessage {

        public byte MessageOrder => 16;
        public byte ServiceID => 106;

        public int UsedPips;
        public string[] MobAdjectives;

        /// <summary>The adjectives of each defeated mob on its own, so a quest step counts a mob once.</summary>
        public string[][] MobAdjectivesPerMob;
        public ulong[] MobTemplateIds;

        /// <summary>What each defeated mob drops by: a custom's key (Customs.cs) or its template id.</summary>
        public ulong[] MobLootKeys;

        /// <summary>
        /// Character IDs of every player who fought in the duel, the winner included.
        /// </summary>
        public ulong[] AllyCharIds;

        /// <summary>
        /// The rounds the battle took for Tamed Drops (TamedDrops.cs): a round everyone chose to pass stops counting from
        /// the third in a row; a stunned or confused wizard's pass is never a choice.
        /// </summary>
        public int CountedRounds;

        /// <summary>
        /// The Animus this wizard extracted in the battle (Monstrology), by the monster's tome id, or null.
        /// </summary>
        public Dictionary<uint, int> Animus;

    }

    public sealed class MSG_COMBATEFFECT : IServerMessage {
        
        public byte MessageOrder => 17;
        public byte ServiceID => 106;

        public CombatDuelSubCircle Caster;
        public CombatDuelSubCircle[] Targets;
        public SpellEffect Effect;
        
    }

    public sealed class MSG_PLANNINGPHASEBEGIN : IServerMessage {
        
        public byte MessageOrder => 18;
        public byte ServiceID => 106;
        
    }

    public sealed class MSG_NOAGGROGRACEOVER : IServerMessage {
        
        public byte MessageOrder => 19;
        public byte ServiceID => 106;
        
    }

    public sealed class MSG_ACTORCOMBATDRAW : IServerMessage {

        public byte MessageOrder => 20;
        public byte ServiceID => 106;

        public IActorRef Actor;

    }

    internal sealed class MSG_CHEATINSTAWIN : IServerMessage {

        public byte MessageOrder => 21;
        public byte ServiceID => 106;

    }

    internal sealed class MSG_CHEATTOGGLECINEMATICS : IServerMessage {

        public byte MessageOrder => 22;
        public byte ServiceID => 106;

    }

    internal sealed class MSG_CHEATINSTANTCINEMATICS : IServerMessage {

        public byte MessageOrder => 23;
        public byte ServiceID => 106;

        public bool Enabled;

    }

    internal sealed class MSG_CHEATTOGGLENOFIZZLE : IServerMessage {

        public byte MessageOrder => 24;
        public byte ServiceID => 106;

    }

    internal sealed class MSG_CHEATNOFIZZLE : IServerMessage {

        public byte MessageOrder => 25;
        public byte ServiceID => 106;

        public bool Enabled;
        public IActorRef Actor;

    }

    /// <summary>
    /// Sent to each participant session when a PvP duel is over. Unlike a PvE win it grants no experience or loot.
    /// </summary>
    public sealed class MSG_PVPDUELENDED : IServerMessage {

        public byte MessageOrder => 26;
        public byte ServiceID => 106;

        public ulong MatchId;
        public int WinningTeam;
        public bool Forfeited;

    }

    /// <summary>
    /// Sent by a session to the duel of a PvP match when its client reports that it finished the victory scene.
    /// </summary>
    public sealed class MSG_PVPVICTORYSEEN : IServerMessage {

        public byte MessageOrder => 27;
        public byte ServiceID => 106;

        public IActorRef Actor;

    }

    /// <summary>
    /// A timer message of a PvP duel: the clients had enough time for the victory scene, end the duel.
    /// </summary>
    public sealed class MSG_PVPENDGRACEOVER : IServerMessage {

        public byte MessageOrder => 28;
        public byte ServiceID => 106;

    }

    /// <summary>
    /// .draw: puts copies of a spell into the sender's hand in the current duel, as many as fit.
    /// </summary>
    internal sealed class MSG_CHEATDRAWCARDS : IServerMessage {

        public byte MessageOrder => 29;
        public byte ServiceID => 106;

        public uint SpellTemplateId;
        public string SpellName;
        public int Amount;
        public IActorRef Actor;

    }

    /// <summary>
    /// .pip give: gives the sender pips in the current battle.
    /// </summary>
    internal sealed class MSG_CHEATGIVEPIPS : IServerMessage {

        public byte MessageOrder => 30;
        public byte ServiceID => 106;

        public string Kind;
        public int Amount;
        public IActorRef Actor;

    }

    /// <summary>
    /// .mastery: the sender counts as mastering these schools for the rest of the current battle.
    /// </summary>
    internal sealed class MSG_CHEATMASTERY : IServerMessage {

        public byte MessageOrder => 31;
        public byte ServiceID => 106;

        public uint[] Schools;
        public IActorRef Actor;

    }

    /// <summary>
    /// .daybreaker and .nightbringer: the sender casts a spell for free this round, at the start or the end of
    /// their turn.
    /// </summary>
    internal sealed class MSG_CHEATDELAYCAST : IServerMessage {

        public byte MessageOrder => 32;
        public byte ServiceID => 106;

        public uint SpellTemplateId;
        public string SpellName;
        public bool AtStart;
        // me (the sender), p1-p4 (circles of the sender's side, in the game's order), e1-e4 (the other side's), all, or null.
        public string Target;
        // .c1s/.c2s/.cfs/.cls: the game skips the casting animation (CombatAction.m_showCast off).
        public bool SkipCast;
        // .cf / .cl: 'f' casts before anyone's turn this round, 'l' after everyone's; 'n' (.c1/.c2) around the sender's card.
        public char When = 'n';
        // .c1 e: the spell gets the best enchantment that fits it ([Battle] AutoEnchants).
        public bool Enchant;
        // .c1 Fire Cat, ca 1: a .ca line (locale key) queued in its place instead of a spell; Shown is its English text.
        public string Announcement;
        public string AnnouncementShown;
        public IActorRef Actor;

    }

    /// <summary>
    /// .joinme: a creature joins the sender's battle on the enemy side.
    /// </summary>
    internal sealed class MSG_CHEATJOINME : IServerMessage {

        public byte MessageOrder => 33;
        public byte ServiceID => 106;

        public uint CreatureTemplateId;
        public string CreatureName;
        // A custom of the Customs tab (Customs.cs) based on the creature, or 0.
        public int CustomId;
        public IActorRef Actor;

    }

    /// <summary>
    /// .wegofirst / .theygofirst: the sender's side, or the other side, acts first.
    /// </summary>
    internal sealed class MSG_CHEATTURNORDER : IServerMessage {

        public byte MessageOrder => 36;
        public byte ServiceID => 106;

        public bool WeFirst;
        public IActorRef Actor;

    }

    /// <summary>
    /// .ca: a line shown like a boss's cheat when the round's animations play.
    /// </summary>
    internal sealed class MSG_CHEATANNOUNCE : IServerMessage {

        public byte MessageOrder => 34;
        public byte ServiceID => 106;

        // The line's key (Table_Id), which the game shows the line by, and its English text.
        public string Text;
        public string Shown;
        public IActorRef Actor;

    }

    /// <summary>
    /// A timer message of a duel: a step of the round's animations has played, so the wizard's health globe gets the
    /// health it left them with (CombatResolver.HealthBeats).
    /// </summary>
    internal sealed class MSG_HEALTHGLOBEDUE : IServerMessage {

        public byte MessageOrder => 35;
        public byte ServiceID => 106;

        public IActorRef Actor;
        public Imlight.CoreLib.WizardData.Models.Player.Wizard Wizard;
        public int Health;

    }

    /// <summary>
    /// The wizard picked an Archmastery school on the deck or the Power Orb (MSG_SetDeckArchmastery) while in a
    /// battle: new school pips come in that school from the next round. 0 is their own school.
    /// </summary>
    internal sealed class MSG_SETARCHMASTERYSCHOOL : IServerMessage {

        public byte MessageOrder => 37;
        public byte ServiceID => 106;

        public uint School;
        public IActorRef Actor;

    }

    /// <summary>
    /// A pet Will Cast: the game's Will Cast button (MSG_PETWILLCAST) or `.willcast` in a battle. Talent picks one by
    /// its talent id (0: the one the game's card names, else the first ready one); List only says each one's state.
    /// </summary>
    internal sealed class MSG_PETWILLCASTREQUEST : IServerMessage {

        public byte MessageOrder => 38;
        public byte ServiceID => 106;

        // What the game sent as PetCastingSpell, or null from the command.
        public byte[] GameData;
        // What the game sent as Target, or -1.
        public int Target = -1;
        public uint Talent;
        public bool List;
        public IActorRef Actor;

    }

}
