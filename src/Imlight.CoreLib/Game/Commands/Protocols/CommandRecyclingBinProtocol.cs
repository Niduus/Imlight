using Imlight.CoreLib.Game.ItemSafety;
using Imlight.CoreLib.Game.Services;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.recyclebin`: opens the game's Recycling Bin window with the items you threw away (Niduus, 2026-11-04), the same
/// window the game's own button opens. Anyone can use it.
/// </summary>
internal class CommandRecyclingBinProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("recyclebin")]
    [Alias("recyclingbin", "bin")]
    private void RecyclingBinCommand() {
        if (!RecyclingBin.Enabled) {
            InformSenderClient("The Recycling Bin is turned off on this server.");

            return;
        }

        RecyclingBinService.SendBin(Context.SessionActor, Context.Character);
    }

}
