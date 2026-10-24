using System;
using System.Linq;
using System.Text;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Services;

/// <summary>
/// The Guild Museum in a session (Niduus, 2026-10-24): a won battle fills the artifacts of the creatures it defeated
/// (and says so in chat), a gallery shows the wizard's exhibit tiers a few seconds after they arrive, and the museum
/// messages the game sends are answered and written to the log with the numbering they fit. See Game/GuildMuseum.
/// </summary>
internal class GuildMuseumService(SessionActor sessionActor) : MessageService(sessionActor) {

    private const string SHOW_EXHIBITS_TIMER = "GuildMuseumShowExhibits";
    private static readonly TimeSpan SHOW_EXHIBITS_AFTER = TimeSpan.FromSeconds(3);

    /// <summary>Asks the session to show the gallery's exhibit tiers again (the timer after arriving, `.museum show`).</summary>
    internal sealed class ShowExhibits : IServerMessage {
        public byte MessageOrder => 1;
        public byte ServiceID => 250;
    }

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new GuildMuseumService(parentActor));

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_COMBATWIN))]
    private void ReceiveCombatWin(COMBAT_106_PROTOCOL.MSG_COMBATWIN message) {
        try {
            var wizard = GetActiveWizard();
            if (wizard is null || !GuildMuseum.GuildMuseum.Enabled) {
                return;
            }

            var lines = GuildMuseum.GuildMuseum.OnCombatWin(wizard.CharId, wizard.Zone, message.MobTemplateIds ?? []);
            if (GuildMuseum.GuildMuseum.ChatOn) {
                foreach (var line in lines) {
                    InformGameClient(line);
                }
            }
        }
        catch (Exception ex) {
            Logger.Error("Guild Museum | Adding the essence of a won battle failed: {0}", Logger.Args(ex));
        }
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ADDPLAYERRSP))]
    private void ReceiveAddPlayerRsp(ZONE_102_PROTOCOL.MSG_ADDPLAYERRSP message) {
        try {
            if (GuildMuseum.GuildMuseum.Enabled && GuildMuseum.GuildMuseum.GalleryInZone(GetActiveWizard()?.Zone) is not null) {
                // The stands are the zone's own objects; the client needs them loaded before it is told their look.
                Timers.StartSingleTimer(SHOW_EXHIBITS_TIMER, new ShowExhibits(), SHOW_EXHIBITS_AFTER);
            }
        }
        catch (Exception ex) {
            Logger.Error("Guild Museum | Checking for a gallery failed: {0}", Logger.Args(ex));
        }
    }

    [InternalMessageHandler(typeof(ShowExhibits))]
    private void ReceiveShowExhibits(ShowExhibits _) => SendExhibits();

    /// <summary>Tells the client the tier of every exhibit of the gallery the wizard is in. Returns how many.</summary>
    internal int SendExhibits() {
        var wizard = GetActiveWizard();
        var gallery = GuildMuseum.GuildMuseum.GalleryInZone(wizard?.Zone);
        if (gallery is null) {
            return 0;
        }

        var count = 0;
        foreach (var (worldId, exhibitId, state, _) in GuildMuseum.GuildMuseum.StatesOf(wizard.CharId, gallery)) {
            SendToSocket(new WIZARD3_56_PROTOCOL.MSG_UPDATEMUSEUMEXHIBIT { WorldID = worldId, ExhibitID = exhibitId, State = state });
            count++;
        }

        Logger.Information("Guild Museum | Showed {0} the {1} exhibits of the {2} Gallery (ids {3}).",
            Logger.Args(wizard.CharId, count, gallery.World, GuildMuseum.GuildMuseum.Ids));
        if (GuildMuseum.GuildMuseum.ChatOn) {
            InformGameClient(GuildMuseum.GuildMuseum.GalleryLine(wizard.CharId, gallery));
        }

        return count;
    }

    #region What the game sends

    [MessageHandler(typeof(WIZARD3_56_PROTOCOL.MSG_REQUESTMUSEUMCURATOR))]
    private void ReceiveRequestCurator(WIZARD3_56_PROTOCOL.MSG_REQUESTMUSEUMCURATOR message) {
        try {
            Logger.Information("Guild Museum | The game asked for the curator: GlobalID={0} WorldID={1} ExhibitID={2} ({3}) Buffer={4}.",
                Logger.Args(message.GlobalID, message.WorldID, message.ExhibitID,
                    GuildMuseum.GuildMuseum.DescribeIds(message.WorldID, message.ExhibitID), Hex(message.Buffer)));

            // Answered as pressing X at that curator would be.
            var curator = GetZoneObject(message.GlobalID)?.GetComponentOfType<GuildMuseumCuratorComponent>();
            curator?.OnServiceInteraction(SessionActor.ActorRef, GetActiveWizard(), GetActiveGameObject(), 0);
        }
        catch (Exception ex) {
            Logger.Error("Guild Museum | Answering the curator request failed: {0}", Logger.Args(ex));
        }
    }

    [MessageHandler(typeof(WIZARD3_56_PROTOCOL.MSG_REQUESTARTIFACTLOCATION))]
    private void ReceiveArtifactLocation(WIZARD3_56_PROTOCOL.MSG_REQUESTARTIFACTLOCATION message) {
        try {
            var artifact = GuildMuseum.GuildMuseum.Galleries
                .SelectMany(g => g.Exhibits).SelectMany(e => e.Artifacts)
                .FirstOrDefault(a => a.TemplateID == message.TemplateID);
            Logger.Information("Guild Museum | The game asked where artifact {0} ({1}) is found. GlobalID={2} Buffer={3}.",
                Logger.Args(message.TemplateID, artifact?.Name ?? "not an artifact", message.GlobalID, Hex(message.Buffer)));
            if (artifact is null) {
                return;
            }

            var where = string.IsNullOrEmpty(artifact.From) ? "Any creature in this world" : artifact.From;
            SendToSocket(new WIZARD3_56_PROTOCOL.MSG_REQUESTARTIFACTLOCATION {
                GlobalID = message.GlobalID,
                TemplateID = message.TemplateID,
                Buffer = Encoding.UTF8.GetBytes(where),
            });
            if (GuildMuseum.GuildMuseum.ChatOn) {
                InformGameClient($"Museum: {artifact.Name} essence comes from {where}.");
            }
        }
        catch (Exception ex) {
            Logger.Error("Guild Museum | Answering where an artifact is found failed: {0}", Logger.Args(ex));
        }
    }

    [MessageHandler(typeof(WIZARD3_56_PROTOCOL.MSG_REQUESTGUILDMUSEUMDOOR))]
    private void ReceiveMuseumDoor(WIZARD3_56_PROTOCOL.MSG_REQUESTGUILDMUSEUMDOOR message)
        => Logger.Information("Guild Museum | The game asked for a museum door: WorldID={0} ({1}) Position={2}. The doors stand on the museum island already.",
            Logger.Args(message.WorldID, GuildMuseum.GuildMuseum.DescribeIds(message.WorldID, 0), message.Position));

    [MessageHandler(typeof(WIZARD3_56_PROTOCOL.MSG_REQUESTMUSEUMESSENCEDATA))]
    private void ReceiveEssenceData(WIZARD3_56_PROTOCOL.MSG_REQUESTMUSEUMESSENCEDATA message)
        => Logger.Information("Guild Museum | The game asked for museum essence data: PartyGID={0} Status={1} Buffer={2}.",
            Logger.Args(message.PartyGID, message.Status, Hex(message.Buffer)));

    private static string Hex(byte[] data) => data is null || data.Length == 0 ? "(empty)" : Convert.ToHexString(data);

    #endregion

}
