using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.IO;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Photomancy;

/// <summary>
/// Photomancy, the camera tome (Niduus, 2026-10-24). The game draws the tome, takes the picture and saves it on the
/// player's computer; the server only opens the tome (MSG_PHOTOMANCYUSETOME), says which filters are unlocked
/// (WizGameStats m_photoFilters and MSG_UPDATEPHOTOFILTERS) and, when a photo is taken (MSG_PHOTOTAKEN), runs the
/// results of each photographed object (PhotographedObjectBehavior), which is how the photo quests move on.
/// </summary>
internal static class Photomancy {

    /// <summary>The Photomancy Tome object in the Golem Tower (Wizard City).</summary>
    internal const uint TOME_TEMPLATE_ID = 1457173;
    internal const string TOME_OBJECT_NAME = "PhotomancyTome";

    // [Photomancy] AllFilters: every photo filter is open to every wizard (each bit is one filter).
    private static readonly bool s_allFilters = ConfigurationManager.GetValue("Photomancy.AllFilters", true);

    // [Photomancy] Command: anyone can open the tome anywhere with .photo.
    internal static readonly bool CommandOn = ConfigurationManager.GetValue("Photomancy.Command", true);

    /// <summary>The filters to send, from the wizard's own saved ones.</summary>
    internal static uint Filters(uint stored)
        => s_allFilters ? uint.MaxValue : stored;

    /// <summary>
    /// Sends the wizard's filters and opens the tome.
    /// </summary>
    internal static void OpenTome(IActorRef session, Wizard wizard) {
        session.Tell(new WIZARD3_56_PROTOCOL.MSG_UPDATEPHOTOFILTERS {
            Filters = Filters(wizard?.GameStats?.m_photoFilters ?? 0),
        }, ActorRefs.NoSender);
        session.Tell(new WIZARD2_53_PROTOCOL.MSG_PHOTOMANCYUSETOME(), ActorRefs.NoSender);
        Logger.Information("Photomancy: opened the tome for {0}.", Logger.Args(wizard?.CharId));
    }

    /// <summary>
    /// Reads the photographed objects the game sends with a photo (PhotoInformation m_photoWorthyItems).
    /// </summary>
    internal static bool TryReadPhoto(ByteString buffer, out List<uint> items) {
        items = [];
        byte[] bytes = buffer;
        if (bytes is null || bytes.Length == 0) {
            return true;
        }

        // How the game writes it is not known yet, so the usual ways are each tried.
        (bool Versionable, SerializerFlags Flags, PropertyFlags Mask)[] ways = [
            (false, SerializerFlags.None, PropertyFlags.Prop_Public),
            (false, SerializerFlags.SerializeFlags, PropertyFlags.Prop_Public),
            (false, SerializerFlags.None, PropertyFlags.Prop_Transmit),
            (true, SerializerFlags.None, PropertyFlags.Prop_Public),
        ];
        foreach (var way in ways) {
            try {
                var serializer = new ObjectSerializer(Versionable: way.Versionable, Behaviors: way.Flags);
                if (serializer.Deserialize<PhotoInformation>(bytes, way.Mask, out var info) && info is not null) {
                    items = info.m_photoWorthyItems?.ToList() ?? [];

                    return true;
                }
            }
            catch (Exception) {
                // Try the next way.
            }
        }

        return false;
    }

    /// <summary>
    /// The results to run for one photographed object: its template's PhotographedObjectBehavior results.
    /// </summary>
    internal static ResultList ResultsFor(uint templateId, out string name) {
        name = null;
        try {
            if (CoreObjectFactory.GetCoreTemplate(templateId) is not GameObjectTemplate template) {
                return null;
            }

            name = template.m_objectName.ToString();
            var results = template.m_behaviors?
                .OfType<PhotographedObjectBehaviorTemplate>()
                .SelectMany(b => b.m_results?.m_results ?? [])
                .Where(r => r is not null)
                .ToList();

            return results is { Count: > 0 } ? new ResultList { m_results = results } : null;
        }
        catch (Exception ex) {
            Logger.Error("Photomancy: reading template {0} failed. {1}", Logger.Args(templateId, ex.Message));

            return null;
        }
    }

}
