/*
 * PET STITCH PROXIES
 *
 * The game's stitch window only lists hats, robes, boots and wands from the backpack it knows. So when a pet
 * stitcher (Prospector Zeke) opens it, the client is told about one stand-in wand per pet, named after the pet
 * (Niduus, 2026-10-20: "trick Zeke to only show pets"). The stand-ins are never saved; a stitch request naming
 * them is mapped back to the pets, and they are taken away again after the request or the next visit.
 */

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.Shared.Utilities;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Seamstress;

internal static class PetStitchProxies {

    // Wand of the Novice: a plain level 1 wand the window lists under the wand tab.
    private static uint s_wandTemplateId => (uint) ConfigurationManager.GetValue("Seamstress.PetStandInTemplate", 126925);

    private static readonly ConcurrentDictionary<ulong, Dictionary<ulong, ulong>> s_proxies = new();

    /// <summary>
    /// Takes away the old stand-ins and gives the client new ones for the wizard's pets.
    /// </summary>
    /// <param name="stitchedOnly">True for the unstitch window: only stitched pets, shown as stitched.</param>
    internal static void Show(IActorRef session, Wizard wizard, bool stitchedOnly) {
        Clear(session, wizard);
        var pets = (wizard.InventoryBehavior?.Items ?? []).Concat(wizard.EquipmentBehavior?.EquippedItems ?? [])
            .Where(PetStitch.IsPet)
            .Where(pet => !stitchedOnly || ItemHelper.IsStitched(pet))
            .DistinctBy(pet => (ulong) pet.m_globalID)
            .ToList();
        var map = new Dictionary<ulong, ulong>();
        foreach (var pet in pets) {
            if (CoreObjectFactory.FinalizeCoreObject(s_wandTemplateId) is not WizClientObjectItem proxy) {
                Logger.Warning("[Seamstress] Pet stand-in template {0} is not an item.", Logger.Args(s_wandTemplateId));

                return;
            }

            proxy.m_globalID = RandomGen.GenerateGUID();
            proxy.m_characterId = wizard.CharId;
            proxy.m_displayKey = PetName(pet);
            proxy.m_primaryColor = pet.m_primaryColor;
            proxy.m_secondaryColor = pet.m_secondaryColor;
            if (stitchedOnly) {
                proxy.m_displayID = s_wandTemplateId;
            }

            if (ItemSync.SendAdded(session, wizard, proxy)) {
                map[proxy.m_globalID] = pet.m_globalID;
            }
        }

        s_proxies[wizard.CharId] = map;
        Logger.Information("[Seamstress] Showed {0} pets of {1} as stand-in wands{2}.",
            Logger.Args(map.Count, wizard.CharId, stitchedOnly ? " (unstitch)" : ""));
    }

    /// <summary>
    /// The pet a stand-in stands for, or the id itself when it is no stand-in.
    /// </summary>
    internal static ulong Resolve(Wizard wizard, ulong itemId)
        => s_proxies.TryGetValue(wizard.CharId, out var map) && map.TryGetValue(itemId, out var petId) ? petId : itemId;

    internal static void Clear(IActorRef session, Wizard wizard) {
        if (!s_proxies.TryRemove(wizard.CharId, out var map)) {
            return;
        }

        foreach (var proxyId in map.Keys) {
            ItemSync.SendRemoved(session, wizard, proxyId);
        }
    }

    private static string PetName(WizClientObjectItem pet) {
        var template = CoreObjectFactory.GetCoreTemplate(PetFactory.LookTemplateId(pet)) as GameObjectTemplate;

        return template?.m_displayName ?? "Pet";
    }

}
