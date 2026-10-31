using Akka.Actor;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.stuck`: runs every quest safety guard for your quests at once (Game/Quests/QuestSafety) and says what step each of
/// them is on: used quest objects come back, a quest with nothing to do moves on, a step that needs something turned off
/// is done for you or explained. Anyone can use it.
/// </summary>
internal class CommandStuckProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("stuck")]
    [Alias("unstuck")]
    private void StuckCommand()
        => Context.SessionActor.Tell(new CHARACTER_103_PROTOCOL.MSG_QUESTCOMMAND {
            Action = "stuck",
            QuestName = "",
        });

}
