using Akka.Actor;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.skip`: on a ride (the Krokotopia flying ship, the Dragonspyre flight, Marleybone's airships), get off at the end of
/// the trip now instead of waiting it out (Niduus, 2026-11-04: "the boat trip in krok doesn't have a skip"). See
/// <see cref="RideZones"/>.
/// </summary>
internal class CommandSkipRideProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("skip")]
    [Alias("skipride")]
    private void SkipCommand() {
        var wizard = Context.Character;
        if (wizard is null || !RideZones.TryGetTripEnd(wizard.Zone, out var zone, out var location)) {
            InformSenderClient("There is no ride to skip here. Use .skip while on a ride, like the Krokotopia flying ship.");

            return;
        }

        Context.SessionActor.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
            DestinationZone = zone,
            DestinationLocation = location,
            SendToClient = true,
            ByZoneDoor = true,
            OwnerCharId = wizard.CharId,
        });
    }

}
