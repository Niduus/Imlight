using System;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.Game.Quests;
using Imlight.CoreLib.Shared.Networking;

namespace Imlight.CoreLib.Game.Services;

/// <summary>
/// The tutorial tip log the game asks for at login, and the tips the player reads. The tips held back until their
/// quests are done are worked out in <see cref="TutorialTipGate"/>; QuestService sends the log again as quests move on.
/// </summary>
internal class TutorialTipService(SessionActor sessionActor) : MessageService(sessionActor) {

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new TutorialTipService(parentActor));

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_REQUESTTUTORIALTIPLOG))]
    private void ReceiveRequestTutorialTipLog(WIZARD2_53_PROTOCOL.MSG_REQUESTTUTORIALTIPLOG _)
        => TutorialTipGate.Refresh(GetActiveWizard(), GetActiveGameObject(), SessionActor.ActorRef, SendToSocket, always: true);

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_READTUTORIALTIP))]
    private void ReceiveReadTutorialTip(WIZARD2_53_PROTOCOL.MSG_READTUTORIALTIP message) {
        try {
            TutorialTipGate.MarkRead(GetActiveWizard(), message.TutorialTipID);
        }
        catch (Exception ex) {
            Logger.Error("Tutorial tips | Keeping read tip {0} failed: {1}", Logger.Args(message.TutorialTipID, ex));
        }
    }

    // The game sends its log back when it dropped a tip from ours (one that is always repeatable); ours stays as it is.
    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_UPDATETUTORIALTIPLOG))]
    private void ReceiveUpdateTutorialTipLog(WIZARD2_53_PROTOCOL.MSG_UPDATETUTORIALTIPLOG _) { }

}
