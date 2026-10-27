using System;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Shared.Networking;

namespace Imlight.CoreLib.Game.Services;

/// <summary>
/// The answer of the game's paid loot roll window, which a Second Chance Chest opens (SecondChanceChests.cs): yes opens
/// the chest once for its crowns, no closes the window.
/// </summary>
internal class SecondChanceChestService(SessionActor sessionActor) : MessageService(sessionActor) {

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new SecondChanceChestService(parentActor));

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_PAID_LOOT_ROLL_RESPONSE))]
    private void ReceivePaidLootRollResponse(WIZARD_12_PROTOCOL.MSG_PAID_LOOT_ROLL_RESPONSE message) {
        try {
            var wizard = GetActiveWizard();
            Logger.Debug("Second Chance Chest {0}: the window answered {1}.", Logger.Args(message.Id, message.Response));
            if (wizard is null || message.Response == 0 || !SecondChanceChests.IsChest(message.Id)) {
                return;
            }

            var refused = SecondChanceChests.Open(SessionActor.ActorRef, GetActiveGameObject(), wizard, message.Id);
            if (refused is not null) {
                SendToSocket(new WIZARD_12_PROTOCOL.MSG_PAID_LOOT_ROLL_ERROR());
                InformGameClient(refused);
            }
        }
        catch (Exception ex) {
            Logger.Error("Second Chance Chest | Opening chest {0} failed: {1}", Logger.Args(message.Id, ex));
        }
    }

}
