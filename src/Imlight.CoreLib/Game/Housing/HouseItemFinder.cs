/*
 * Imlight
 * Copyright (C) 2026 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 *
 * ========================================================================
 * HOUSE ITEM FINDER
 * ========================================================================
 *
 * PURPOSE:
 * Builds what the game's Item Finder window is told about the attic and the houses of a character: one
 * MSG_ITEMFINDERREQUESTHOUSE per attic part and per house, then MSG_ITEMFINDERHOUSECOMPLETE.
 *
 * USAGE EXAMPLE:
 * foreach (var message in HouseItemFinder.Messages(wizard)) { session.Tell(message); }
 *
 * NOTE:
 * Read from the game (WizardGraphicalClient.exe, Wizard_1_610, 2026-10-24):
 * - ItemFinderWindow::MSG_ItemFinderRequestHouse (0x1410b3ee0) keeps Data, HouseTemplateID and SectionCode; the
 *   window parses Data later (0x1410b1360) as a serialized Blob whose m_data is a HousingBlob, the same bytes as the
 *   house's MSG_SEND_BLOB, and lists each object of m_housingBlobObjectList by its template.
 * - HouseTemplateID 0 marks the items as in the attic (AtticIcon). Any other id marks them as in a house
 *   (HouseIcon) and names the house after that template (ItemFinderHouseData, 0x1410a7940); the dorm structure
 *   164117 has its own name. SectionCode 1 picks one of two house texts (GUI2_00001083, else GUI2_00001084), and
 *   SectionCode 0 also sets the location flag 0x800. Which text means inside is not known; a structure (the inside
 *   of a residence) is sent with 1 and a lot with 0.
 * - MSG_ItemFinderHouseComplete (0x1410b40b0) closes the "please wait" window and fills the list. Until it comes
 *   the window keeps waiting, so it is always sent, even when nothing could be read.
 *
 * TODO:
 *
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/24/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Housing;

/// <summary>
/// The attic and house part of the Item Finder window.
/// </summary>
internal static class HouseItemFinder {

    private const uint AtticHouseTemplateId = 0;
    private const sbyte LotSection = 0;
    private const sbyte StructureSection = 1;

    /// <summary>
    /// Builds the messages that tell the Item Finder window where the character's housing items are.
    /// </summary>
    /// <param name="wizard">The character.</param>
    /// <returns>The messages to send in order, ending with MSG_ITEMFINDERHOUSECOMPLETE.</returns>
    internal static List<IMessage> Messages(Wizard wizard) {
        var messages = new List<IMessage>();
        if (wizard is not null) {
            AddAttic(wizard, messages);
            AddHouses(wizard, messages);
        }

        messages.Add(new WIZARD2_53_PROTOCOL.MSG_ITEMFINDERHOUSECOMPLETE());

        return messages;
    }

    private static void AddAttic(Wizard wizard, List<IMessage> messages) {
        try {
            if (!AtticRegistry.TryEnter(wizard, out var attic, out _, out _)) {
                Logger.Warning("Item Finder: the attic of {CharId} could not be read.", Logger.Args(wizard.CharId));

                return;
            }

            try {
                foreach (var subType in attic.SubTypes()) {
                    var snapshot = attic.Snapshot(subType);
                    var objects = snapshot.Slots.Where(item => item is not null).Select(item => item.ToBlobObject()).ToList();
                    if (objects.Count > 0) {
                        Add(messages, objects, subType, attic.ContainerGid, snapshot.Revision, AtticHouseTemplateId, LotSection,
                            $"attic part {subType} of {wizard.CharId}");
                    }
                }
            }
            finally {
                AtticRegistry.Release(attic);
            }
        }
        catch (Exception exception) {
            Logger.Error("Item Finder: the attic of {CharId} could not be listed: {Error}", Logger.Args(wizard.CharId, exception));
        }
    }

    private static void AddHouses(Wizard wizard, List<IMessage> messages) {
        foreach (var (templateId, lotGid) in wizard.HouseLots.ToList()) {
            if (lotGid == 0 || !HouseTemplates.TryGet(templateId, out var template)) {
                continue;
            }

            try {
                if (!HouseRegistry.TryEnter(lotGid, out var house)) {
                    Logger.Warning("Item Finder: house {LotGid} (template {TemplateId}) of {CharId} could not be read.",
                        Logger.Args(lotGid, templateId, wizard.CharId));

                    continue;
                }

                try {
                    var snapshot = house.Snapshot();
                    var objects = snapshot.Slots.Where(item => item is not null).Select(HouseVaults.ToBlobObject).ToList();
                    Add(messages, objects, "Item", house.LotGid, snapshot.Revision, templateId,
                        template.IsStructure ? StructureSection : LotSection, $"house {lotGid} (template {templateId})");
                }
                finally {
                    HouseRegistry.Release(house);
                }
            }
            catch (Exception exception) {
                Logger.Error("Item Finder: house {LotGid} of {CharId} could not be listed: {Error}",
                    Logger.Args(lotGid, wizard.CharId, exception));
            }
        }
    }

    private static void Add(List<IMessage> messages, List<HousingBlobObject> objects, string subType, ulong associatedGid,
                            uint revision, uint houseTemplateId, sbyte section, string what) {
        var blob = new Blob {
            m_type = HousingBlobs.BlobType,
            m_subType = subType,
            m_versionNumber = revision,
            m_packageNumber = 0,
            m_associatedGID = associatedGid,
            m_epochDays = HousingBlobs.EpochDaysNow(),
            m_data = new HousingBlob { m_housingBlobObjectList = objects },
        };

        try {
            messages.Add(new WIZARD2_53_PROTOCOL.MSG_ITEMFINDERREQUESTHOUSE {
                Data = HousingBlobs.Serialize(blob),
                HouseTemplateID = houseTemplateId,
                SectionCode = section,
            });
            Logger.Information("Item Finder: {What} lists {Count} items.", Logger.Args(what, objects.Count));
        }
        catch (InvalidOperationException exception) {
            Logger.Error("Item Finder: {What} could not be sent: {Reason}", Logger.Args(what, exception.Message));
        }
    }

}
