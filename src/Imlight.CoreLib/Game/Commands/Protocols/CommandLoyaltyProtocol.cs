using System;
using System.Linq;
using Akka.Actor;
using Imlight.CoreLib.Shared.Packets;
using Imlight.Common;
using Imlight.CoreLib.Game.Loyalty;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.loyalty` for Majid's Loyalty Store (Niduus, 2026-10-25; Game/Loyalty/LoyaltyStore.cs): what he sells now and how
/// long for, your Loyalty Tokens, and claiming this rotation's tokens. The server's testers can set an account's
/// tokens (`.loyalty tokens 50`).
/// </summary>
internal class CommandLoyaltyProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    private const string MAJID_ZONE = "WizardCity/WC_Shop_Area";
    // Teleport_to_WC-Shop-NPC-Loyalty as "X,Y,Z,Yaw", facing Majid (-3947.9, -3052.3).
    private const string MAJID_SPOT = "-4079.562,-3188.625,-29.36362,0.8";

    [Command("loyalty")]
    [Alias("majid", "loyaltystore")]
    private void LoyaltyCommand([Remainder] string text = "") {
        try {
            var words = (text ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var first = words.Length > 0 ? words[0].ToLowerInvariant() : "";
            var me = Context.Character;
            var tester = Context.Account.AuthLevel >= AuthLevel.QualityAssurance;
            switch (first) {
                case "":
                case "status":
                case "wares":
                    InformSenderClient(Status(me, tester));

                    return;
                case "go":
                case "goto":
                case "visit":
                    // The game's own spot next to Majid in the Shopping District (Zone places: Teleport_to_WC-Shop-NPC-Loyalty).
                    // The game ignores a zone transfer to the zone it is already in, so there the wizard is moved instead.
                    if (string.Equals(me.Zone, MAJID_ZONE, StringComparison.OrdinalIgnoreCase)) {
                        Context.SessionActor.Tell(new CHARACTER_103_PROTOCOL.MSG_DOTELEPORTEFFECTS());
                        Context.SessionActor.Tell(new CHARACTER_103_PROTOCOL.MSG_MOVEINZONE { Location = MAJID_SPOT });
                    }
                    else {
                        Context.SessionActor.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
                            DestinationZone = MAJID_ZONE,
                            DestinationLocation = "Teleport_to_WC-Shop-NPC-Loyalty",
                            SendToClient = true,
                            OwnerCharId = me.CharId,
                        });
                    }

                    Logger.Information("Loyalty Store | {0} goes to Majid from {1}.", Logger.Args(me.CharId, me.Zone));
                    InformSenderClient("Taking you to Majid in the Shopping District.");

                    return;
                case "claim":
                    var claimed = LoyaltyStoreService.Claim(me.AccountId, me.CharId, me.PlayerNameBehavior?.GetWizardName() ?? "");
                    InformSenderClient(claimed is null
                        ? "You claimed this rotation's tokens already."
                        : $"You claimed {claimed.Tokens} Loyalty Tokens; you have {claimed.Balance}.");

                    return;
                case "tokens":
                case "token":
                    if (!tester) {
                        InformSenderClient("Only the server's testers can set Loyalty Tokens.");

                        return;
                    }

                    if (words.Length < 2 || !int.TryParse(words[1], out var tokens) || tokens < 0) {
                        InformSenderClient("Use .loyalty tokens <number>, such as .loyalty tokens 50.");

                        return;
                    }

                    LoyaltyStore.Change(me.AccountId, account => account.Tokens = Math.Min(tokens, 100000));
                    InformSenderClient($"Your account now has {Math.Min(tokens, 100000)} Loyalty Tokens. Talk to Majid again to see them.");

                    return;
            }

            InformSenderClient("Use .loyalty, .loyalty go, .loyalty claim" + (tester ? " or .loyalty tokens <number>." : "."));
        }
        catch (Exception ex) {
            Logger.Error("Loyalty Store | .loyalty {0} failed: {1}", Logger.Args(text, ex));
            InformSenderClient("The Loyalty Store command did not work. The server log has the details.");
        }
    }

    private static string Status(Wizard me, bool tester) {
        var now = LoyaltyStore.Now();
        if (now is null) {
            return "Majid has no wares: the rotation file is missing.";
        }

        var (tokens, claim) = LoyaltyStore.Change(me.AccountId, account => (account.Tokens, LoyaltyStore.CanClaim(account)));
        var lines = new System.Collections.Generic.List<string> {
            $"Majid (Shopping District, by the far end of the shops; .loyalty go takes you there) sells the {now.Rotation.Name} wares{LoyaltyStore.TimeLeft(now)}:",
        };
        lines.AddRange(now.Rotation.Items.Select(i => $"- {i.Name}: {i.Price} token{(i.Price == 1 ? "" : "s")}, up to {i.Limit}"));
        lines.Add($"You have {tokens} Loyalty Tokens." + (claim > 0 ? $" {claim} more can be claimed: .loyalty claim or Claim in his window." : ""));
        if (tester) {
            lines.Add("Testers: .loyalty tokens <number> sets your tokens.");
        }

        return string.Join("<br>", lines);
    }

}
