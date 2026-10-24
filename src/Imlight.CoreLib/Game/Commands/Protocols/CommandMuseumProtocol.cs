using System;
using System.Linq;
using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.museum` for the Guild Museum (Niduus, 2026-10-24; Game/GuildMuseum): how far each gallery is, a gallery's exhibits,
/// the way into a gallery and back, and for testing, filling the exhibits to a tier and switching the numbers the
/// museum messages use. [GuildMuseum] Command turns it off.
/// </summary>
internal class CommandMuseumProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("museum")]
    [Alias("gallery", "galleries")]
    private void MuseumCommand([Remainder] string text = "") {
        if (!GuildMuseum.GuildMuseum.Enabled || !GuildMuseum.GuildMuseum.CommandOn) {
            InformSenderClient("The Guild Museum command is turned off on this server.");

            return;
        }

        try {
            var words = (text ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var first = words.Length > 0 ? words[0].ToLowerInvariant() : "";
            var me = Context.Character;
            switch (first) {
                case "":
                    InformSenderClient(string.Join("<br>", GuildMuseum.GuildMuseum.Galleries.Select(g => GuildMuseum.GuildMuseum.GalleryLine(me.CharId, g)))
                        + "<br>.museum avalon or .museum khrysalis takes you into a gallery, .museum avalon list shows its exhibits, .museum back takes you out.");

                    return;
                case "back":
                case "out":
                case "leave":
                    Travel(GuildMuseum.GuildMuseum.ReturnTransfer(me), "Going to the museum island.");

                    return;
                case "show":
                    Context.SessionActor.Tell(new GuildMuseumService.ShowExhibits());
                    InformSenderClient("Showing this gallery's exhibits again.");

                    return;
                case "ids":
                    IdsCommand(words.Skip(1).FirstOrDefault());

                    return;
                case "fill":
                    FillCommand(words.Skip(1).ToArray());

                    return;
            }

            var gallery = GuildMuseum.GuildMuseum.FindGallery(first);
            if (gallery is null) {
                InformSenderClient("Use .museum, .museum avalon, .museum khrysalis, .museum avalon list or .museum back.");

                return;
            }

            if (words.Length > 1 && words[1].Equals("list", StringComparison.OrdinalIgnoreCase)) {
                var lines = gallery.Exhibits.Select(e => {
                    var tier = GuildMuseum.GuildMuseum.TierOf(me.CharId, e);

                    return tier >= e.MaxTier && tier > 0 ? $"{e.Name}: complete" : $"{e.Name}: tier {tier} of {e.MaxTier}";
                });
                InformSenderClient(GuildMuseum.GuildMuseum.GalleryLine(me.CharId, gallery) + "<br>" + string.Join("<br>", lines));

                return;
            }

            Travel(GuildMuseum.GuildMuseum.EntryTransfer(gallery, me), $"Going to the {gallery.World} Gallery.");
        }
        catch (Exception ex) {
            Logger.Error("Guild Museum | .museum {0} failed: {1}", Logger.Args(text, ex));
            InformSenderClient("The museum command did not work. The server log has the details.");
        }
    }

    private void IdsCommand(string style) {
        if (Context.Account.AuthLevel < AuthLevel.QualityAssurance) {
            InformSenderClient("You do not have permission to use this command.");

            return;
        }

        if (!Enum.TryParse<GuildMuseum.GuildMuseum.IdStyle>(style ?? "", true, out var picked)) {
            InformSenderClient($"The museum messages use {GuildMuseum.GuildMuseum.Ids} numbers. Pick one: .museum ids hash, index, index1 or door.");

            return;
        }

        GuildMuseum.GuildMuseum.Ids = picked;
        Context.SessionActor.Tell(new GuildMuseumService.ShowExhibits());
        InformSenderClient($"The museum messages use {picked} numbers now (until the server restarts). Look at the exhibit stands, or press X at one.");
    }

    private void FillCommand(string[] words) {
        if (Context.Account.AuthLevel < AuthLevel.QualityAssurance) {
            InformSenderClient("You do not have permission to use this command.");

            return;
        }

        // .museum fill 2, .museum fill avalon 3, .museum fill 0 (empty again).
        var gallery = words.Length > 1 ? GuildMuseum.GuildMuseum.FindGallery(words[0]) : null;
        var tierWord = words.Length > 1 ? words[1] : words.FirstOrDefault();
        if (!int.TryParse(tierWord, out var tier) || tier < 0 || tier > 4) {
            InformSenderClient("Use .museum fill 1 (2, 3, or 0 for empty), or .museum fill avalon 3 for one gallery.");

            return;
        }

        var count = GuildMuseum.GuildMuseum.SetTier(Context.Character.CharId, gallery, tier);
        Context.SessionActor.Tell(new GuildMuseumService.ShowExhibits());
        InformSenderClient($"Set {count} artifacts{(gallery is null ? "" : $" of the {gallery.World} Gallery")} to tier {tier}.");
    }

    private void Travel(ZONE_102_PROTOCOL.MSG_ZONETRANSFER transfer, string saying) {
        InformSenderClient(saying);
        Context.SessionActor.Tell(transfer);
    }

}
