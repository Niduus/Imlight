using Akka.Actor;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.questbook`: each quest in your quest book with the step you are on, the step after it and the quest it opens
/// (Game/Services/QuestService.Book). Anyone can use it.
/// </summary>
internal class CommandQuestBookProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("questbook")]
    [Alias("myquests")]
    private void QuestBookCommand()
        => Context.SessionActor.Tell(new CHARACTER_103_PROTOCOL.MSG_QUESTCOMMAND {
            Action = "book",
            QuestName = "",
        });

}
