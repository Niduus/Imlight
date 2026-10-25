using System;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Services;

/// <summary>
/// Shows the floating islands of the guild grounds (Niduus, 2026-10-25). When the grounds load, the game hides every
/// Island_0N of the scene and asks the server who is visiting (MSG_GUILDVISITORREQUEST, or MSG_UNLOCKTRIGGERDATA for
/// a member). Its social-systems answer MSG_SOCIALCONTROL with Data "unlockall" shows them all again, so the islands
/// float in view and the pads to them land on ground, guild or not (a guild house deed in the backpack works too).
/// </summary>
internal class GuildGroundsService(SessionActor sessionActor) : MessageService(sessionActor) {

    private const string SHOW_ISLANDS_TIMER = "GuildGroundsShowIslands";
    // Once more after the zone is in, in case the game's hiding came after the first answer.
    private static readonly TimeSpan SHOW_ISLANDS_AFTER = TimeSpan.FromSeconds(3);

    /// <summary>Asks the session to show the islands again (the timer after arriving).</summary>
    internal sealed class ShowIslands : IServerMessage {
        public byte MessageOrder => 1;
        public byte ServiceID => 251;
    }

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new GuildGroundsService(parentActor));

    [MessageHandler(typeof(WIZARD3_56_PROTOCOL.MSG_GUILDVISITORREQUEST))]
    private void ReceiveGuildVisitorRequest(WIZARD3_56_PROTOCOL.MSG_GUILDVISITORREQUEST _) => SendShowIslands();

    [MessageHandler(typeof(WIZARD3_56_PROTOCOL.MSG_UNLOCKTRIGGERDATA))]
    private void ReceiveUnlockTriggerData(WIZARD3_56_PROTOCOL.MSG_UNLOCKTRIGGERDATA _) => SendShowIslands();

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ADDPLAYERRSP))]
    private void ReceiveAddPlayerRsp(ZONE_102_PROTOCOL.MSG_ADDPLAYERRSP _) {
        try {
            if (OnGuildGrounds()) {
                Timers.StartSingleTimer(SHOW_ISLANDS_TIMER, new ShowIslands(), SHOW_ISLANDS_AFTER);
            }
        }
        catch (Exception ex) {
            Logger.Error("Guild grounds | Checking for the grounds failed: {0}", Logger.Args(ex));
        }
    }

    [InternalMessageHandler(typeof(ShowIslands))]
    private void ReceiveShowIslands(ShowIslands _) {
        if (OnGuildGrounds()) {
            SendShowIslands();
        }
    }

    private bool OnGuildGrounds()
        => string.Equals(GetActiveWizard()?.Zone, GuildMuseum.GuildMuseum.GUILD_GROUNDS_ZONE, StringComparison.OrdinalIgnoreCase);

    private void SendShowIslands() {
        try {
            var wizard = GetActiveWizard();
            SendToSocket(new WIZARD3_56_PROTOCOL.MSG_SOCIALCONTROL {
                PlayerGID = wizard?.GameObjectID ?? 0,
                Data = "unlockall",
                Value = 0,
            });
            Logger.Information("Guild grounds | Told the game to show every floating island.");
        }
        catch (Exception ex) {
            Logger.Error("Guild grounds | Showing the islands failed: {0}", Logger.Args(ex));
        }
    }

}
