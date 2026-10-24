using System;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.photo` opens the Photomancy camera tome anywhere (Niduus, 2026-10-24). The game's own tome stands in the Golem
/// Tower, which is out of the way once the start of the game is done. [Photomancy] Command turns it off.
/// </summary>
internal class CommandPhotoProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("photo")]
    [Alias("photomancy", "camera")]
    private void PhotoCommand() {
        if (!Photomancy.Photomancy.CommandOn) {
            InformSenderClient("The .photo command is turned off on this server. Use the Photomancy Tome in the Golem Tower.");

            return;
        }

        try {
            Photomancy.Photomancy.OpenTome(Context.SessionActor, Context.Character);
            InformSenderClient("Opening the Photomancy tome.");
        }
        catch (Exception ex) {
            Logger.Error("Photomancy: .photo failed. {0}", Logger.Args(ex.Message));
            InformSenderClient("Could not open the Photomancy tome.");
        }
    }

}
