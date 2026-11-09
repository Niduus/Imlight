using Akka.Actor;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.stuck`: runs every quest safety guard for your quests at once (Game/Quests/QuestSafety) and says what step each of
/// them is on: used quest objects come back, a quest with nothing to do moves on, a step that needs something turned off
/// is done for you or explained. `.stuck go` also takes you to where your quest's step happens, when a door or teleport
/// left you in the wrong place. Anyone can use it.
/// </summary>
internal class CommandStuckProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("stuck")]
    [Alias("unstuck")]
    private void StuckCommand([Remainder] string argument = "")
        => Context.SessionActor.Tell(new CHARACTER_103_PROTOCOL.MSG_QUESTCOMMAND {
            // ".stuck go" also takes the wizard to where their quest's step happens, when a door left them somewhere else.
            Action = (argument ?? "").Trim().Equals("go", System.StringComparison.OrdinalIgnoreCase) ? "stuckgo" : "stuck",
            QuestName = "",
        });

}
