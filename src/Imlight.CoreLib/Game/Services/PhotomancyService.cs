using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.Game.Results;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Services;

/// <summary>
/// Photomancy in game (Niduus, 2026-10-24): the photos the game takes. Each photographed object with photo results
/// (PhotographedObjectBehavior, such as the Darkmoor "Photomance" quest objects) runs them for the player, so the
/// photo quests move on. The tome itself opens from the Photomancy Tome object or `.photo` (see Game/Photomancy).
/// </summary>
internal class PhotomancyService(SessionActor sessionActor) : MessageService(sessionActor) {

    // The same object in a burst of photos runs its results once.
    private static readonly TimeSpan SAME_OBJECT_AGAIN_AFTER = TimeSpan.FromSeconds(10);
    private readonly Dictionary<uint, DateTime> _lastRun = [];
    private bool _loggedUnread;

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new PhotomancyService(parentActor));

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_PHOTOTAKEN))]
    private void ReceivePhotoTaken(WIZARD2_53_PROTOCOL.MSG_PHOTOTAKEN message) {
        try {
            var wizard = GetActiveWizard();
            string filter = message.Filter;
            byte[] buffer = message.Buffer;

            if (!Photomancy.Photomancy.TryReadPhoto(message.Buffer, out var items)) {
                if (!_loggedUnread) {
                    _loggedUnread = true;
                    Logger.Warning("Photomancy: {0} took a photo (filter \"{1}\") but its data could not be read: {2}",
                        Logger.Args(wizard?.CharId, filter, Convert.ToHexString(buffer ?? [])));
                }

                return;
            }

            Logger.Information("Photomancy: {0} took a photo (filter \"{1}\") with {2} photographed objects: {3}.",
                Logger.Args(wizard?.CharId, filter, items.Count, string.Join(", ", items)));

            var playerObject = wizard?.GetInitializedGameObject();
            if (playerObject is null) {
                return;
            }

            var now = DateTime.UtcNow;
            foreach (var templateId in items.Distinct()) {
                if (_lastRun.TryGetValue(templateId, out var last) && now - last < SAME_OBJECT_AGAIN_AFTER) {
                    continue;
                }

                var results = Photomancy.Photomancy.ResultsFor(templateId, out var name);
                if (results is null) {
                    continue;
                }

                _lastRun[templateId] = now;
                Logger.Information("Photomancy: {0} photographed {1} ({2}); running its {3} photo results.",
                    Logger.Args(wizard.CharId, name, templateId, results.m_results.Count));
                ResultDispatcher.ExecuteResults(Context, results, SessionActor.ActorRef, playerObject);
            }
        }
        catch (Exception ex) {
            Logger.Error("Photomancy: handling a photo failed. {0}", Logger.Args(ex));
        }
    }

}
