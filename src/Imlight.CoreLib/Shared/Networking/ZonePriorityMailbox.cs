using Akka.Actor;
using Akka.Configuration;
using Akka.Dispatch;
using Imcodec.MessageLayer.Generated;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Shared.Networking;

/// <summary>
/// Zone mailbox that serves moves and broadcasts first. It is stable: messages of equal priority keep their
/// send order, where the plain priority mailbox hands a backlog of them back almost in reverse.
/// </summary>
public class ZonePriorityMailbox : UnboundedStablePriorityMailbox {

    public ZonePriorityMailbox(Settings settings, Config config)
        : base(settings, config) { }

    protected override int PriorityGenerator(object message) {
        return message switch {
            // Other players' moves first of all, so they show on the other screens as soon as they arrive.
            ZONE_102_PROTOCOL.MSG_ZONEBROADCAST { Message: GAME_5_PROTOCOL.MSG_SERVERMOVE or GAME_5_PROTOCOL.MSG_MOVESTATE
                or GAME_5_PROTOCOL.MSG_JUMP } => 0,
            ZONE_102_PROTOCOL.MSG_PLAYERMOVE    => 0,
            ZONE_102_PROTOCOL.MSG_CREATUREMOVE  => 1,
            ZONE_102_PROTOCOL.MSG_ZONEBROADCAST => 2,
            // A summoned pet's spawn must stay ahead of the broadcast that dismisses it.
            ZONE_102_PROTOCOL.MSG_SPAWNENTITY   => 2,
            _ => 10,
        };
    }

}
