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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.IO;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Tools;
using Imlight.Common;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Forked from the Quest Team's QuestTeamMember (Game/QuestTeam): the body Ultimus plays through. One Ultimus wizard's game: a pretend game client inside the server. It logs in and moves from area to area the way
/// the real game does (session handshake, MSG_ATTACH with a login key, zone transfers), and sends only what a game sends:
/// steps (MSG_CLIENTMOVE), NPC clicks, quest accepts, dialog closes, training and potions. Everything the server sends it
/// arrives as the message itself. The real server code runs for it, so a quest that breaks for Ultimus breaks for a
/// player too. The Director (UltimusDirector) tells it where to go and what to click; it reports what it sees in
/// <see cref="View"/>.
/// </summary>
internal sealed class UltimusMember : ReceiveActor, IWithTimers {

    // ---- what the Director tells a wizard ----

    internal sealed record Connect;

    internal sealed record Disconnect(string Why);

    /// <summary>Walk through these points (the last is the goal), at a player's running pace.</summary>
    internal sealed record WalkTo(List<(float X, float Y)> Points, string Why);

    internal sealed record StopWalking;

    /// <summary>Click an option of an NPC or object in range, as the game does.</summary>
    internal sealed record Interact(ulong GlobalId, string Service, uint Index, string What);

    internal sealed record Train(ulong TrainerGlobalId, int Index, string What);

    internal sealed record UsePotion;
    internal sealed record BuyPotions;

    /// <summary>Say a Menu Chat phrase by its chat ID, as a player picks it from the menu.</summary>
    internal sealed record SayMenuChat(uint PhraseId);
    internal sealed record WhisperMenuChat(uint PhraseId, ulong CharId);

    /// <summary>A Menu Chat line with its own words, as the game's "My Quests" and "Locations" lines are (a quest's name).</summary>
    internal sealed record SayMenuChatText(string Text);
    internal sealed record WhisperMenuChatText(string Text, ulong CharId);

    /// <summary>
    /// A typed line, said nearby or whispered (Niduus, 2026-11-14: "He is free to use text chat this time"). It goes through
    /// the game's own chat rules for his account: when typed chat would hide it (menu chat only, or a word the filter turns
    /// red) it is said as a Menu Chat line with its own words instead, so it is never lost or shown as "...".
    /// </summary>
    internal sealed record SayText(string Text);
    internal sealed record WhisperText(string Text, ulong CharId);

    internal sealed record WorldTeleport(ulong DoorGlobalId, string World);

    /// <summary>Put on an item from the backpack, as the game's backpack does.</summary>
    internal sealed record Equip(ulong ItemId, string Slot, string What);

    /// <summary>Take an item off into the backpack, as the game's backpack does.</summary>
    internal sealed record Unequip(ulong ItemId, string Slot, string What);

    /// <summary>Clicks in the battle window, each after its delay, as a person makes them (Game/BotShared/BotBattle).</summary>
    internal sealed record Clicks(List<BotShared.BotBattle.Click> Each);

    private sealed record ClickNow(BotShared.BotBattle.Move Move);

    /// <summary>Ahziael sends it to the area its quest step is in, as a player's .stuck go does.</summary>
    internal sealed record GoToStep;
    /// <summary>Ahziael takes it through a walk-through exit that never took it on, to the area the exit leads to.</summary>
    internal sealed record ThroughExit(string To);
    /// <summary>A small hop to a clear spot by its goal (the line of sight circle's last resort).</summary>
    internal sealed record Hop(float X, float Y, float Z, string Why);
    // Stuck past stepping aside: the server takes it back to where its area starts.
    internal sealed record Recall;
    /// <summary>Mark Location and Recall Location, the buttons a player has (they cost mana, as for a player).</summary>
    /// <summary>Sells these backpack items to the shopkeeper, one by one, as a player does at a shop.</summary>
    internal sealed record Sell(ulong NpcGlobalId, List<ulong> Items, string What);
    /// <summary>Buys an item at the Bazaar (its Bazaar id), as a player does.</summary>
    internal sealed record BuyAtBazaar(ulong NpcGlobalId, ulong BazaarId, string What);
    internal sealed record MarkSpot;
    internal sealed record RecallSpot;
    internal sealed record SelectBadge(uint Id);
    internal sealed record GoToTeammate(ulong CharId);

    /// <summary>Leave the battle, as the Flee button does.</summary>
    internal sealed record Flee;

    /// <summary>Press the Dungeon Recall button.</summary>
    internal sealed record UseDungeonRecall;

    /// <summary>Buy one item at a shop (the shopkeeper by global id), as the shop window does.</summary>
    internal sealed record Buy(ulong NpcGlobalId, uint ItemTemplate, string What);

    /// <summary>Change the deck, as the deck window does: these out (one per copy), then these in.</summary>
    internal sealed record EditDeck(ulong DeckId, List<uint> Out, List<uint> In, string Why);
    internal sealed record ItemCards(ulong DeckId, List<(uint Id, bool Off)> Clicks);

    /// <summary>Ice asks a teammate into its group.</summary>
    internal sealed record Invite(ulong CharId, ulong GlobalId);

    // ---- the actor's own messages ----

    private sealed record Opened(IActorRef Session, ushort Id, ByteString? Key, string Zone, ulong ZoneId, string Location);

    private sealed record Tick;

    private sealed record CloseDialog(int Number);

    private sealed record AcceptOffer(ulong MobileId, string Quest);

    private sealed record Retry;

    private const string TICK = "tick";
    private const double TICK_SECONDS = BotShared.BotWalk.TICK_SECONDS;

    // When the last step was taken (MinValue while standing), so each step is as long as the time that passed.
    private DateTime _lastStep = DateTime.MinValue;


    /// <summary>The least time a wizard stands after arriving in an area, while its walls and paths are read.</summary>
    internal const double ARRIVE_SECONDS = 2.5;

    // A mount's extra speed (a Ghulture: 40%).
    private const float MOUNT_SPEED = 1.4f;

    /// <summary>What a Ultimus wizard is doing and sees, for the Director (written by this actor only).</summary>
    internal sealed class Status {
        public string Role { get; init; } = "";
        public ulong CharId { get; init; }
        public ulong AccountId { get; init; }
        public volatile string Name = "";
        public volatile string State = "offline";
        public volatile string Zone = "";
        public volatile Wizard Wizard;
        public volatile IActorRef Session;
        public float X, Y, Z;
        public volatile bool Walking;
        public volatile bool Talking;
        public DateTime LastDialog = DateTime.MinValue;

        /// <summary>A cutscene is playing for him until then (he watches it, as a player does).</summary>
        public DateTime CutsceneUntil = DateTime.MinValue;
        public DateTime AttachedAt = DateTime.MinValue;
        public volatile string LastServerText = "";
        public volatile string LastTrained = "";
        public volatile int AttachFailures;

        /// <summary>The options of every NPC or object in range (by global id), as the game was sent them.</summary>
        public readonly ConcurrentDictionary<ulong, List<(string Service, uint Index, string Display)>> Options = new();

        public ulong GlobalId => Wizard.GetGameObjectId(CharId);

        public IActorRef ZoneActor => Session is null ? null : SessionObject?.GetZoneActor();

        public volatile Shared.Networking.SessionActor SessionObject;
    }

    public ITimerScheduler Timers { get; set; }

    internal Status View { get; }

    private readonly UltimusRoster.Member _member;
    private IActorRef _session;
    private ByteString? _key;
    private bool _wantOnline;
    private bool _attached;
    private int _connectTries;
    private int _dialogs;
    private ushort _mobileId;
    private byte _zoneCounter;
    private readonly Queue<(float X, float Y)> _route = new();
    private DateTime _recalledAt = DateTime.MinValue;
    private long _clicks;
    private (string Zone, ulong ZoneId, string Location)? _transfer;

    public UltimusMember(UltimusRoster.Member member, Status view) {
        _member = member;
        View = view;
        View.Name = member.Name;

        Receive<Connect>(_ => {
            _wantOnline = true;
            _connectTries = 0;
            Open(null);
        });
        Receive<Disconnect>(m => {
            _wantOnline = false;
            Close(m.Why);
        });
        Receive<Retry>(_ => {
            if (_wantOnline && _session is null) {
                Open(_transfer);
            }
        });
        Receive<Opened>(OnOpened);
        Receive<Attached>(OnAttached);
        Receive<Arrived>(OnArrived);
        Receive<WalkTo>(m => {
            _route.Clear();
            // Not while arriving in an area: a walk planned from where it stood before is not walked here.
            if (!_attached || View.State != "playing") {
                View.Walking = false;

                return;
            }

            // Every walk stays on the legal ground (Niduus, 2026-11-14: "MAke sure he respects the world/games boundaries"):
            // a leg over a wall or the Minimap's black is cut where the floor ends, whatever planned it.
            var legal = OverlayQuestPath.Legal(View.Zone, View.X, View.Y, m.Points, out var cut);
            if (cut && DateTime.UtcNow - _cutSaid > TimeSpan.FromSeconds(30)) {
                _cutSaid = DateTime.UtcNow;
                Note($"keeps to the walkable ground: a walk ({m.Why}) went over a wall or out of bounds in {UltimusText.Zone(View.Zone)}, so it stops where the floor ends.");
                BotMistakes.Record("Ultimus", BotMistakes.NO_WAY, View.Zone, View.X, View.Y, m.Why,
                    "A planned walk went over a wall or off the walkable map; it was cut where the floor ends.",
                    "Every walk is checked against the area's floor before it is walked.", "The way finder or the step's spot needs a look here.");
            }

            foreach (var point in legal) {
                _route.Enqueue(point);
            }

            View.Walking = _route.Count > 0;
        });
        Receive<StopWalking>(_ => {
            _route.Clear();
            View.Walking = false;
        });
        Receive<Tick>(_ => Step());
        Receive<Interact>(m => {
            Send(new QUEST_MESSAGES_52_PROTOCOL.MSG_INTERACTNPC {
                GlobalID = m.GlobalId, ServiceName = m.Service, ServiceIndex = m.Index, Reinteract = 0, RequestedSigilMode = 0,
            });
            // The game drops an NPC's options once one is used; the server sends them again when the talk is over.
            View.Options.TryRemove(m.GlobalId, out _);
        });
        Receive<Train>(m => Send(new WIZARD_12_PROTOCOL.MSG_TRAIN { MobileID = m.TrainerGlobalId, TrainingIndex = m.Index }));
        Receive<SayMenuChat>(m => Send(new GAME_5_PROTOCOL.MSG_REQUESTRADIALQUICKCHAT { MessageID = m.PhraseId }));
        Receive<WhisperMenuChat>(m => Send(new GAME_5_PROTOCOL.MSG_REQUESTDIRECTEDQUICKCHAT { TargetID = m.CharId, MessageID = m.PhraseId }));
        Receive<SayMenuChatText>(m => Send(new GAME_5_PROTOCOL.MSG_REQUESTRADIALQUICKCHATEXT { Message = m.Text }));
        Receive<WhisperMenuChatText>(m => Send(new GAME_5_PROTOCOL.MSG_REQUESTDIRECTEDQUICKCHATEXT { TargetID = m.CharId, Message = m.Text }));
        Receive<SayText>(m => {
            if (TypedShows(m.Text)) {
                Send(new GAME_5_PROTOCOL.MSG_REQUESTRADIALCHAT { Message = Social.RadialChatText.Like(new byte[] { 0, 0 }, m.Text) });
            }
            else {
                Send(new GAME_5_PROTOCOL.MSG_REQUESTRADIALQUICKCHATEXT { Message = m.Text });
            }
        });
        Receive<WhisperText>(m => {
            if (TypedShows(m.Text)) {
                Send(new GAME_5_PROTOCOL.MSG_REQUESTDIRECTEDCHAT { TargetID = m.CharId, Message = m.Text });
            }
            else {
                Send(new GAME_5_PROTOCOL.MSG_REQUESTDIRECTEDQUICKCHATEXT { TargetID = m.CharId, Message = m.Text });
            }
        });
        Receive<UsePotion>(_ => Send(new WIZARD_12_PROTOCOL.MSG_USEPOTION()));
        // "Fill All" at a potion shop, as a player presses it in the shop's window.
        Receive<BuyPotions>(_ => Send(new WIZARD_12_PROTOCOL.MSG_POTIONBUYREQUEST { AmountEnum = 1 }));
        Receive<WorldTeleport>(m => Send(new WIZARD_12_PROTOCOL.MSG_WORLDTELEPORTREQUEST { TeleportID = m.DoorGlobalId, World = m.World }));
        Receive<CloseDialog>(m => FinishDialog(m.Number));
        Receive<Invite>(m => Send(new GAME_5_PROTOCOL.MSG_PARTYREQUESTINVITE {
            SourceCharacterID = _member.CharId, SourceGlobalID = View.GlobalId, TargetCharacterID = m.CharId, TargetGlobalID = m.GlobalId,
        }));
        // Ultimus quests alone: every group invitation is turned down, from players, friends and bots alike (Niduus,
        // 2026-11-13), so the inviter's game shows the no at once instead of waiting.
        Receive<GAME_5_PROTOCOL.MSG_PARTYREQUESTJOIN>(m => {
            Note($"turns down a group invitation from {m.PlayerNameBlob}.");
            Send(new GAME_5_PROTOCOL.MSG_PARTYREQUESTDECLINE { SourceCharacterID = _member.CharId, PartyID = m.PartyID });
        });
        // Ultimus is the one bot a player can befriend: a friend request sent to him is accepted (he never sends one
        // himself). Being friends lets the player see him on the friends list and teleport to him.
        Receive<GAME_5_PROTOCOL.MSG_BUDDYREQUESTADD>(m => {
            if (m.Remove != 0 || m.ListOwnerGID == _member.CharId || QuestTeam.QuestTeamRoster.IsMember(m.ListOwnerGID)) {
                return;
            }

            Note($"accepts a friend request from {m.OwnerName}.");
            Send(new GAME_5_PROTOCOL.MSG_BUDDYREQUESTACCEPT { ListOwnerGID = m.ListOwnerGID, EntryGID = _member.CharId });
        });
        Receive<Equip>(m => {
            Note($"puts on {m.What}.");
            Send(new GAME_5_PROTOCOL.MSG_EQUIPITEM { IsEquip = 1, ItemID = m.ItemId, SlotName = m.Slot });
        });
        Receive<Clicks>(m => {
            for (var i = 0; i < m.Each.Count; i++) {
                Timers.StartSingleTimer($"click-{_clicks++}", new ClickNow(m.Each[i].Move), TimeSpan.FromSeconds(Math.Max(0.05, m.Each[i].AfterSeconds)));
            }
        });
        Receive<Hop>(m => {
            if (!_attached) {
                return;
            }

            // Never to a spot a player could not stand on (off the walkable map, in a wall).
            if (!OverlayQuestPath.OnFloor(View.Zone, m.X, m.Y)) {
                Note($"does not hop to ({m.X:0}, {m.Y:0}) ({m.Why}): that spot is off the walkable ground.");

                return;
            }

            _route.Clear();
            View.Walking = false;
            Note($"hops to ({m.X:0}, {m.Y:0}): {m.Why}.");
            Place(m.X, m.Y, m.Z);
            Send(new GAME_5_PROTOCOL.MSG_CLIENTMOVE {
                LocationX = Compress(m.X), LocationY = Compress(m.Y), LocationZ = Compress(m.Z), Direction = 0, ZoneCounter = _zoneCounter,
            });
        });
        Receive<GoToStep>(_ => {
            _recalledAt = DateTime.UtcNow;
            _session?.Tell(new CHARACTER_103_PROTOCOL.MSG_QUESTCOMMAND { Action = "botgo" });
        });
        Receive<ThroughExit>(m => {
            _recalledAt = DateTime.UtcNow;
            _session?.Tell(new CHARACTER_103_PROTOCOL.MSG_QUESTCOMMAND { Action = "botzone", QuestName = m.To });
        });
        Receive<Recall>(_ => {
            _recalledAt = DateTime.UtcNow;
            _session?.Tell(new CHARACTER_103_PROTOCOL.MSG_QUESTCOMMAND { Action = "botrecall" });
        });
        Receive<GoToTeammate>(m => Send(new WIZARD_12_PROTOCOL.MSG_GOTOFRIENDLYPLAYER { TargetCharacterID = m.CharId }));
        Receive<SelectBadge>(m => Send(new GAME_5_PROTOCOL.MSG_SELECT_BADGE { BadgeNameID = m.Id }));
        Receive<Sell>(m => {
            Note($"sells {m.Items.Count} things it will not wear ({m.What}).");
            foreach (var id in m.Items) {
                Send(new WIZARD_12_PROTOCOL.MSG_SHOPSELLREQUEST { npcGlobalID = m.NpcGlobalId, GlobalID = id, quantity = 1, IsQuickSell = 0 });
            }

            Send(new WIZARD_12_PROTOCOL.MSG_DONESHOPPING());
        });
        Receive<BuyAtBazaar>(m => {
            Note($"buys {m.What} at the Bazaar.");
            Send(new WIZARD_12_PROTOCOL.MSG_AUCTIONHOUSEREQUEST { Command = 1, npcGlobalID = m.NpcGlobalId, itemTemplateID = m.BazaarId });
            Send(new WIZARD_12_PROTOCOL.MSG_AUCTIONHOUSEREQUEST { Command = 3, npcGlobalID = m.NpcGlobalId, itemTemplateID = m.BazaarId });
        });
        Receive<MarkSpot>(_ => Send(new GAME_5_PROTOCOL.MSG_MARK_LOCATION()));
        Receive<RecallSpot>(_ => Send(new GAME_5_PROTOCOL.MSG_RECALL_LOCATION()));
        Receive<ClickNow>(m => Send(BotShared.BotBattle.ToGameMessage(m.Move)));
        Receive<Flee>(_ => Send(new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATMOVE {
            MoveType = (byte) Combat.CombatMoveType.Flee, SpellSelection = 0, SpellTarget = 0, TimeLeft = 0,
        }));
        Receive<UseDungeonRecall>(_ => Send(new WIZARD2_53_PROTOCOL.MSG_RESUMEINSTANCETELEPORTPLAYER()));
        Receive<Unequip>(m => {
            Note($"takes off {m.What} (developers' gear is not for Ultimus).");
            Send(new GAME_5_PROTOCOL.MSG_EQUIPITEM { IsEquip = 0, ItemID = m.ItemId, SlotName = m.Slot });
        });
        Receive<Buy>(m => {
            Note($"buys {m.What}.");
            Send(new WIZARD_12_PROTOCOL.MSG_SHOPBUYREQUEST { npcGlobalID = m.NpcGlobalId, ShopID = m.ItemTemplate, quantity = 1, CurrencyType = 0 });
            Send(new WIZARD_12_PROTOCOL.MSG_DONESHOPPING());
        });
        Receive<ItemCards>(m => {
            // One click per copy in the deck window: the server crosses out (or puts back) one copy each time.
            foreach (var (id, off) in m.Clicks) {
                if (BotCards.SpellHash(id) is var hash and not 0) {
                    Send(new WIZARD2_53_PROTOCOL.MSG_UPDATEITEMSPELLEXCLUSIONLIST { SpellID = hash, DeckID = m.DeckId, Exclude = (byte) (off ? 1 : 0) });
                }
            }
        });
        Receive<EditDeck>(m => {
            Note($"changes its deck ({m.Why}): {m.Out.Count} out, {m.In.Count} in.");
            foreach (var id in m.Out) {
                Send(new WIZARD_12_PROTOCOL.MSG_REMOVESPELLFROMDECK { SpellID = (int) id, DeckID = m.DeckId });
            }

            foreach (var id in m.In) {
                Send(new WIZARD_12_PROTOCOL.MSG_ADDSPELLTODECK { SpellID = (int) id, DeckID = m.DeckId });
            }
        });
        Receive<AcceptOffer>(m => Send(new QUEST_MESSAGES_52_PROTOCOL.MSG_ACCEPTQUEST { MobileID = m.MobileId, QuestName = m.Quest }));

        // What the server sends this wizard's game.
        Receive<ControlMessageProtocol.SessionOffer>(m => {
            // The offer comes as the session starts, often before the answer saying which session it is: kept until then.
            _offer = m;
            AcceptOfferOfSession();
        });
        Receive<ControlMessageProtocol.KeepAliveServer>(m => SendControl(new ControlMessageProtocol.KeepAliveResponse {
            SessionId = m.SessionId, Milliseconds = (ushort) (DateTime.UtcNow.TimeOfDay.TotalMilliseconds % 1000),
        }));
        Receive<GAME_5_PROTOCOL.MSG_LOGINCOMPLETE>(OnLoginComplete);
        Receive<GAME_5_PROTOCOL.MSG_ATTACHFAILED>(m => {
            View.AttachFailures++;
            UltimusLink.Abnormal($"the server refused to let it into {(_transfer?.Zone ?? View.Zone)} (attach error {m.Error}); a player would see a loading screen that fails.");
            Note($"could not enter {(_transfer?.Zone ?? View.Zone)} (error {m.Error}); trying again shortly");
            Close("attach failed");
            Timers.StartSingleTimer("retry", new Retry(), TimeSpan.FromSeconds(Math.Min(60, 5 * View.AttachFailures)));
        });
        Receive<GAME_5_PROTOCOL.MSG_ZONETRANSFERREQUEST>(m => {
            _route.Clear();
            View.Walking = false;
            View.State = "zoning";
            Send(new GAME_5_PROTOCOL.MSG_ZONETRANSFERACK());
        });
        Receive<GAME_5_PROTOCOL.MSG_SERVERTRANSFER>(OnServerTransfer);
        Receive<GAME_5_PROTOCOL.MSG_SERVERTELEPORT>(m => {
            if (m.MobileID == _mobileId && _mobileId != 0) {
                // Sent somewhere else in the area: a walk planned from the old spot is not walked from the new one.
                _route.Clear();
                View.Walking = false;
                var (x, y) = (Expand(m.LocationX), Expand(m.LocationY));
                // Moved far in a moment in the same area, while walking its way: a teleporter it stepped on. Remembered
                // (UltimusPads), so the next time it knows where that pad goes, as a player would.
                var settled = _attached && DateTime.UtcNow - View.AttachedAt > TimeSpan.FromSeconds(5) && DateTime.UtcNow - _recalledAt > TimeSpan.FromSeconds(30);
                var padHere = UltimusPads.In(View.Zone).Any(p => Far(p.X, p.Y, View.X, View.Y) < 300 && Far(p.ToX, p.ToY, x, y) < 400);
                if (settled && Far(View.X, View.Y, x, y) > 800 && !padHere && UltimusPads.Learn(View.Zone, View.X, View.Y, x, y)) {
                    Note($"learns a teleporter in {UltimusText.Zone(View.Zone)}: ({View.X:0}, {View.Y:0}) goes to ({x:0}, {y:0}).");
                    padHere = true;
                }

                // Moved far by the server without asking (it did not walk there or use a door): odd for a player.
                if (settled && !padHere && Far(View.X, View.Y, x, y) > 1500) {
                    UltimusLink.Abnormal($"was moved by the server in {UltimusText.Zone(View.Zone)} from ({View.X:0}, {View.Y:0}) to ({x:0}, {y:0}) without asking.");
                }

                Place(x, y, Expand(m.LocationZ));
            }
        });
        Receive<QUEST_MESSAGES_52_PROTOCOL.MSG_SENDNPCOPTIONS>(OnNpcOptions);
        Receive<GAME_5_PROTOCOL.MSG_LEAVESERVICERANGE>(m => View.Options.TryRemove(m.MobileID, out _));
        Receive<QUEST_MESSAGES_52_PROTOCOL.MSG_QUESTOFFER>(m => {
            string quest = m.QuestName;
            // Only the main story: a side quest or a side world's quest is left (its goal is the Main Story above all).
            var template = QuestTemplateCollection.GetQuestByName(quest);
            // Niduus, 2026-11-13: a main quest is taken even when it is not the story (the game may not let it be left),
            // then ignored unless the story needs it.
            if (template is null || !BotStory.Takes(template) || !UltimusPlanner.IsMainStoryWorld(template)) {
                Note($"is offered {UltimusText.Title(quest)}; leaves it (not the Main Story)");
                Send(new QUEST_MESSAGES_52_PROTOCOL.MSG_DECLINEQUEST { QuestName = quest });

                return;
            }

            Note(BotStory.Plays(template)
                ? $"is offered {UltimusText.Title(quest)}; accepts it"
                : $"is offered {UltimusText.Title(quest)}; accepts it but ignores it (a main quest, not the story; done only if the story needs it)");
            // A moment to "read" the offer, as a player does.
            Timers.StartSingleTimer("offer-" + quest, new AcceptOffer(m.MobileID, quest), TimeSpan.FromSeconds(0.7));
        });
        Receive<WIZARD_12_PROTOCOL.MSG_ACTORDIALOG>(OnDialog);
        // A cutscene: he watches it to the end instead of walking off (Niduus, 2026-11-14, approved: patience with cutscenes).
        Receive<GAME_5_PROTOCOL.MSG_PLAYCINEMATIC>(_ => Cutscene());
        Receive<CHARACTER_103_PROTOCOL.MSG_PLAYCINEMATICACTOR>(_ => Cutscene());
        Receive<WIZARD_12_PROTOCOL.MSG_SPELLTRAINCOMPLETE>(m => {
            var name = UltimusText.SpellName((uint) m.SpellID);
            View.LastTrained = m.Success == 1 ? name : "";
            Note(m.Success == 1 ? $"learned {name}" : $"could not learn {name}");
        });
        Receive<EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE>(m => {
            var text = m.Message.ToString() ?? "";
            if (text.Length > 0) {
                View.LastServerText = text;
                Note($"the server says: {UltimusText.Short(text, 200)}");
            }
        });

        // Anything else the server sends is what a game would show; it changes nothing here.
        ReceiveAny(_ => { });
    }

    // Words in a server message that mean something went wrong (pink in the log).
    private static readonly string[] s_oddWords = ["error", "failed", "could not", "couldn't", "cannot", "can't", "broken", "invalid", "missing"];

    internal static Props Props(UltimusRoster.Member member, Status view)
        => Akka.Actor.Props.Create(() => new UltimusMember(member, view));

    protected override void PostStop() {
        try {
            Close("the Ultimus stopped");
        }
        catch (Exception) {
            // Its session closes by itself when this actor is gone (it watches it).
        }

        base.PostStop();
    }

    // ---------------------------------------------------------------------
    // Logging in and changing areas
    // ---------------------------------------------------------------------

    private void Open((string Zone, ulong ZoneId, string Location)? transfer) {
        if (_session is not null) {
            return;
        }

        View.State = transfer is null ? "logging in" : "zoning";
        var self = Self;
        var account = AccountCollection.GetAccountForCharacterList(_member.AccountId);
        if (account is null) {
            Note("its account is gone; Ultimus needs a Restart in Ahziael");
            View.State = "offline";

            return;
        }

        var wizard = account.Characters?.FirstOrDefault(c => c.CharId == _member.CharId);
        var zone = transfer?.Zone ?? wizard?.Zone ?? "";
        var zoneId = transfer?.ZoneId ?? 0;
        var location = transfer?.Location ?? (wizard is null ? "" : Util.GetCompactStringFromVector(wizard.Location, wizard.Orientation));
        var key = _key;
        _ = System.Threading.Tasks.Task.Run(async () => {
            var useKey = key ?? await QuestTeam.QuestTeamHost.CreateKey(account);
            var (session, id) = useKey is null ? (null, (ushort) 0) : await QuestTeam.QuestTeamHost.OpenSession(self, _member.Role);
            self.Tell(new Opened(session, id, useKey, zone, zoneId, location));
        });
    }

    private void OnOpened(Opened opened) {
        if (opened.Session is null) {
            _connectTries++;
            Note("could not connect to the game server; trying again shortly");
            View.State = "offline";
            Timers.StartSingleTimer("retry", new Retry(), TimeSpan.FromSeconds(Math.Min(60, 5 * _connectTries)));

            return;
        }

        if (!_wantOnline) {
            opened.Session.Tell("Close");

            return;
        }

        _key = opened.Key;
        _session = opened.Session;
        _sessionId = opened.Id;
        View.Session = _session;
        AcceptOfferOfSession();
        _attached = false;
        _transfer = null;
        View.Options.Clear();

        // The session starts with the SessionOffer (answered above); then the game attaches its wizard to its area.
        Send(new GAME_5_PROTOCOL.MSG_ATTACH {
            LoginKey = opened.Key ?? ByteString.Empty,
            UserID = _member.AccountId,
            CharID = _member.CharId,
            ZoneName = opened.Zone,
            ZoneID = opened.ZoneId,
            Location = opened.Location,
            Reattach = 0,
            Retry = 0,
        });
    }

    private void OnLoginComplete(GAME_5_PROTOCOL.MSG_LOGINCOMPLETE message) {
        string zone = message.ZoneName;
        View.Zone = zone;
        _zoneCounter++;

        // The game has loaded the area: dialogs held for the loading screen come now.
        Send(new WIZARD2_53_PROTOCOL.MSG_CLIENTZONED { ZoneNameID = StringHash.Compute(zone) });

        // Its own wizard, live: where it stands, its quests, its spells.
        var self = Self;
        var session = _session;
        _ = System.Threading.Tasks.Task.Run(async () => {
            try {
                await System.Threading.Tasks.Task.Delay(800);
                var answer = await session.Ask<CHARACTER_103_PROTOCOL.MSG_CHARACTER>(new CHARACTER_103_PROTOCOL.MSG_QUERYACTIVEWIZARD(), TimeSpan.FromSeconds(10));
                var sessionObject = await session.Ask<Shared.Networking.SessionActor>("Identify", TimeSpan.FromSeconds(10));
                self.Tell(new Attached(answer.Wizard, sessionObject));
            }
            catch (Exception e) {
                Logger.Warning("Ultimus {0}: could not read its wizard after entering {1}: {2}", Logger.Args(_member.Role, zone, e.Message));
            }
        });
    }

    private sealed record Attached(Wizard Wizard, Shared.Networking.SessionActor SessionObject);

    private void OnAttached(Attached attached) {
        if (attached.Wizard is null || _session is null) {
            return;
        }

        _attached = true;
        View.Wizard = attached.Wizard;
        // A game asks for its friends list once it is in, and only then does the server count it as present for groups and
        // friend teleports (found by the Quest Team): asked here too, so a player can group with Ultimus.
        Send(new GAME_5_PROTOCOL.MSG_BUDDYREQUESTLIST { ListOwnerGID = attached.Wizard.GameObjectID });
        View.SessionObject = attached.SessionObject;
        View.Name = attached.Wizard.PlayerNameBehavior?.GetWizardName() ?? View.Name;
        _mobileId = (ushort) (attached.Wizard.GameObject?.m_nMobileID ?? 0);
        var spot = attached.Wizard.Location;
        Place(spot.X, spot.Y, spot.Z);
        View.AttachedAt = DateTime.UtcNow;
        View.AttachFailures = 0;
        Note($"is in {UltimusText.Zone(View.Zone)}");
        Timers.StartPeriodicTimer(TICK, new Tick(), TimeSpan.FromSeconds(TICK_SECONDS));

        // A moment after arriving, before the first step: the area's walls, ground and paths are read first, as a player's
        // game loads them behind the loading screen (Niduus, 2026-11-13: "take a moment after each zone transfer for those
        // collisions to be recognized"). The Director gives no orders until it is "playing".
        View.State = "arriving";
        var self = Self;
        var zone = View.Zone;
        var counter = _zoneCounter;
        _ = System.Threading.Tasks.Task.Run(async () => {
            var started = DateTime.UtcNow;
            BotShared.BotStuck.ReadArea(zone);
            var left = TimeSpan.FromSeconds(ARRIVE_SECONDS) - (DateTime.UtcNow - started);
            if (left > TimeSpan.Zero) {
                await System.Threading.Tasks.Task.Delay(left);
            }

            self.Tell(new Arrived(counter));
        });

        // A new wizard starts in the tutorial; a player may skip it, and so does Ultimus (Niduus's rules: from the very
        // start, as a player would).
        if (View.Zone.StartsWith("WizardCity/Tutorial", StringComparison.OrdinalIgnoreCase)) {
            Note("skips the tutorial, as a player may");
            Send(new GAME_5_PROTOCOL.MSG_SERVERTUTORIALCOMMAND {
                QuestToAdd = "", QuestToRemove = "", GoalToComplete = "SkipTutorialGoal", EventToPost = "", Action = "", Value = 0,
            });
        }
    }

    private sealed record Arrived(byte ZoneCounter);

    // The area is read and the moment has passed: the Director may move this wizard again.
    private void OnArrived(Arrived arrived) {
        if (_attached && arrived.ZoneCounter == _zoneCounter && View.State == "arriving") {
            _route.Clear();
            View.State = "playing";
        }
    }

    private void OnServerTransfer(GAME_5_PROTOCOL.MSG_SERVERTRANSFER message) {
        // The game leaves this connection and attaches again in the new area, with the same login key.
        _transfer = ((string) message.ZoneName, (ulong) message.ZoneID, (string) message.Location);
        Note($"goes to {UltimusText.Zone(_transfer.Value.Zone)}");
        Close("zone change", keepWanting: true);
        Open(_transfer);
    }

    private void Close(string why, bool keepWanting = false) {
        Timers?.Cancel(TICK);
        _route.Clear();
        View.Walking = false;
        View.Talking = false;
        View.Options.Clear();
        _attached = false;
        if (_session is not null) {
            _session.Tell("Close");
            _session = null;
            View.Session = null;
            View.SessionObject = null;
        }

        if (!keepWanting) {
            View.State = "offline";
        }
    }

    // ---------------------------------------------------------------------
    // Walking
    // ---------------------------------------------------------------------

    private void Step() {
        if (!_attached || _route.Count == 0) {
            View.Walking = false;
            _lastStep = DateTime.MinValue;

            return;
        }

        // A wizard in a battle stands on its circle; a wizard reading a dialog stands still.
        if (Zone.Components.CombatDuelComponent.IsInBattle(View.GlobalId)) {
            _route.Clear();
            View.Walking = false;

            return;
        }

        // On its Ghulture it goes faster, as a player on a mount does.
        var mounted = View.Wizard?.EquipmentBehavior?.GetItemInSlot(EquipmentSlotType.Mount) is not null;
        var now = DateTime.UtcNow;
        var seconds = _lastStep == DateTime.MinValue ? TICK_SECONDS : (now - _lastStep).TotalSeconds;
        _lastStep = now;
        var (nx, ny, yaw) = BotShared.BotWalk.Step(_route, View.X, View.Y, seconds, BotShared.BotWalk.RunSpeed * (mounted ? MOUNT_SPEED : 1f));

        var nz = UltimusGround.HeightAt(View.Zone, nx, ny, View.Z);
        Place(nx, ny, nz);
        Send(new GAME_5_PROTOCOL.MSG_CLIENTMOVE {
            LocationX = Compress(nx), LocationY = Compress(ny), LocationZ = Compress(nz),
            Direction = (byte) Math.Clamp((int) MathF.Round(yaw / MathF.Tau * 250), 0, 249),
            ZoneCounter = _zoneCounter,
        });
        View.Walking = _route.Count > 0;
    }

    private void Place(float x, float y, float z) {
        View.X = x;
        View.Y = y;
        View.Z = z;
    }

    private static ushort Compress(float coordinate) => unchecked((ushort) (short) MathF.Round(coordinate / 4));

    private static float Expand(ushort compressed) => unchecked((short) compressed) * 4f;

    private static float Far(float x1, float y1, float x2, float y2) => MathF.Sqrt(((x2 - x1) * (x2 - x1)) + ((y2 - y1) * (y2 - y1)));

    // ---------------------------------------------------------------------
    // NPCs and dialogs
    // ---------------------------------------------------------------------

    private static readonly ObjectSerializer s_options = new(false, SerializerFlags.None);

    private void OnNpcOptions(QUEST_MESSAGES_52_PROTOCOL.MSG_SENDNPCOPTIONS message) {
        try {
            byte[] data = message.Options;
            if (data is null || !s_options.Deserialize<ServiceMementoBase>(data, 4u, out var memento) || memento?.m_serviceOptions is null) {
                return;
            }

            View.Options[message.MobileID] = [.. memento.m_serviceOptions.Where(o => o is not null)
                .Select(o => ((string) o.m_serviceName ?? "", o.m_serviceIndex, (string) o.m_displayKey ?? ""))];
        }
        catch (Exception e) {
            Logger.Debug("Ultimus {0}: could not read an NPC's options: {1}", Logger.Args(_member.Role, e.Message));
        }
    }

    // Each dialog is "read" for a moment (longer for more lines), its entries' events reported as the game reports them,
    // and then closed: that is what finishes a "talk to" step.
    private readonly Dictionary<int, List<string>> _openDialogs = [];

    private void OnDialog(WIZARD_12_PROTOCOL.MSG_ACTORDIALOG message) {
        var events = new List<string>();
        var lines = 1;
        try {
            byte[] data = message.ActorDialog;
            if (data is not null && new ObjectSerializer(false).Deserialize<ActorDialog>(data, 16u, out var dialog) && dialog?.m_dialogEntries is { } entries) {
                lines = Math.Max(1, entries.Count);
                events.AddRange(entries.Where(e => e is not null && !string.IsNullOrEmpty(e.m_dialogEvent)).Select(e => (string) e.m_dialogEvent));
            }
        }
        catch (Exception e) {
            Logger.Debug("Ultimus {0}: could not read a dialog: {1}", Logger.Args(_member.Role, e.Message));
        }

        var number = ++_dialogs;
        _openDialogs[number] = events;
        View.Talking = true;
        View.LastDialog = DateTime.UtcNow;
        Timers.StartSingleTimer("dialog-" + number, new CloseDialog(number), TimeSpan.FromSeconds(Math.Min(UltimusPlaybook.Value("Pace.DialogMost"), 1.0 + (lines * UltimusPlaybook.Value("Pace.DialogPerLine")))));
    }

    private void Cutscene() {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(UltimusPlaybook.Value("Pace.CutsceneSeconds"));
        if (until > View.CutsceneUntil) {
            View.CutsceneUntil = until;
        }
    }

    private void FinishDialog(int number) {
        if (!_openDialogs.Remove(number, out var events)) {
            return;
        }

        foreach (var entryEvent in events) {
            Send(new WIZARD_12_PROTOCOL.MSG_COMPLETEDIALOG { MobileID = 0, CompletionType = "ENTRY", EntryEvent = entryEvent });
        }

        Send(new WIZARD_12_PROTOCOL.MSG_COMPLETEDIALOG { MobileID = 0, CompletionType = "", EntryEvent = "" });
        View.LastDialog = DateTime.UtcNow;
        View.Talking = _openDialogs.Count > 0;
    }

    // ---------------------------------------------------------------------
    // Sending
    // ---------------------------------------------------------------------

    private ControlMessageProtocol.SessionOffer _offer;
    private ushort _sessionId;

    private void AcceptOfferOfSession() {
        if (_offer is not { } offer || _session is null || offer.SessionId != _sessionId) {
            return;
        }

        _offer = null;
        SendControl(new ControlMessageProtocol.SessionAccept {
            SessionId = offer.SessionId, TimestampUpper = offer.TimestampUpper, TimestampLower = offer.TimestampLower, Milliseconds = offer.Milliseconds,
        });
    }

    private void Send(IMessage message) {
        if (_session is null) {
            return;
        }

        _session.Tell(new SERVER_100_PROTOCOL.MSG_RECEIVEDPACKET { Packet = message });
    }

    // Whether his account's chat level lets this typed line show as it is (not menu chat only, no word the filter hides).
    private bool TypedShows(string text) {
        var mode = Social.ChatFilter.CurrentChatMode(AccountCollection.GetAccountForCharacterList(_member.AccountId));

        return mode switch {
            ChatMode.Closed => false,
            ChatMode.Filtered => Social.ChatFilter.FirstRedWord(text, textChat: true) is null,
            _ => true,
        };
    }

    private void SendControl(IMessage message) => _session?.Tell(new SERVER_100_PROTOCOL.MSG_RECEIVEDPACKET { Packet = message });

    private void Note(string text) => UltimusLink.Note(UltimusRoster.NAME, text);

    private DateTime _cutSaid = DateTime.MinValue;

}
