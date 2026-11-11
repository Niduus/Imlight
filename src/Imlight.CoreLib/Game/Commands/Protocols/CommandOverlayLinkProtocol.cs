using Imlight.CoreLib.Game.Tools;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.link code`: links the Ahziael overlay buttons on this game window to the wizard who types it (OverlayLinks). The
/// overlay shows the code on the game window the first time one of its command or cheat buttons is clicked and it can't
/// tell by itself which wizard is in that window. No password: the code only shows on the player's own screen.
/// </summary>
internal class CommandOverlayLinkProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("link")]
    private void LinkCommand([Remainder] string code = "") => InformSenderClient(OverlayLinks.Link(Context.Character.CharId, code));

}
