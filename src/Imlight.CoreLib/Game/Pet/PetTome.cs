/*
 * Imlight
 * Copyright (C) 2025 Revive101
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
 * PET TOME
 * ========================================================================
 *
 * PURPOSE:
 * The Pet Tome, the book the Pets page of the spellbook links to. It lists every pet in the game and highlights
 * the kinds the player has owned. The game asks for it with MSG_REQUESTPETTOME and reads the answer's PetData into
 * PetTomeBehavior.m_petData (PetTomeBehaviorBase::OnPostLoad): uint32 0x2DEC0DED, uint32 version 1, uint16 count of
 * the owned pet templates, uint16 count of a second template list, then the two lists of uint32 template ids.
 *
 * USAGE EXAMPLE:
 * var templates = PetTome.Collect(wizard);
 * var data = PetTome.Write(templates);
 *
 * NOTE:
 * The tome keeps a kind once it was seen (ServerPetOwnerBehavior.PetTome), so a pet sold or released stays in it,
 * like the game's own tome. Each request adds the pets in the backpack, worn, in the bank and in the attic.
 *
 * TODO:
 * - What the second template list holds is not known; it is sent empty.
 *
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/23/2026
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Akka.Actor;
using Imcodec.IO;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Pet;

/// <summary>
/// The pet kinds a wizard has owned, and the game's PetTomeBehavior data for them.
/// </summary>
internal static class PetTome {

    private const uint Identifier = 0x2DEC0DED;
    private const uint Version = 1;

    /// <summary>
    /// Whether the wizard's tome has a pet kind.
    /// </summary>
    internal static bool Contains(Wizard wizard, uint templateId)
        => wizard?.PetOwnerBehavior?.PetTome?.Contains(templateId) == true;

    /// <summary>
    /// Adds a pet kind to the wizard's tome and saves it.
    /// </summary>
    /// <returns>True when the kind was not in the tome yet.</returns>
    internal static bool Add(Wizard wizard, uint templateId) {
        if (wizard?.PetOwnerBehavior is not { } owner || !PetFactory.IsPetTemplate(templateId)) {
            return false;
        }

        owner.PetTome ??= [];
        if (owner.PetTome.Contains(templateId)) {
            return false;
        }

        owner.PetTome.Add(templateId);
        WizardCollection.UpdateCharacterPetOwnerBehavior(wizard);

        return true;
    }

    /// <summary>
    /// Tells the game that a pet the wizard just got is in the tome, when it is a new kind. The game asks for the tome
    /// once a session, so without this a new kind showed only after a relog (Niduus, 2026-10-23). An egg is not a pet
    /// yet; hatching it announces it.
    /// </summary>
    internal static void Announce(IActorRef session, Wizard wizard, WizClientObjectItem item) {
        try {
            if (session is null || item is null || PetProgress.GetBehavior(item) is not { } pet || pet.m_level == 0
                || !Add(wizard, (uint) item.m_templateID)) {
                return;
            }

            session.Tell(PetAdded(wizard, (uint) item.m_templateID));
            Logger.Information("Pet tome: {0} got pet kind {1}; the game was told.", Logger.Args(wizard.CharId, item.m_templateID));
        }
        catch (Exception e) {
            Logger.Error("Pet tome: could not announce pet {0}: {1}", Logger.Args(item?.m_templateID, e));
        }
    }

    /// <summary>
    /// The message that adds a kind to the open tome (PetTomeBehavior::MSG_PetTomePetAdded, on the player's object)
    /// and says "&lt;pet&gt; added to your Pet Tome!" (GUI2_00000494).
    /// </summary>
    internal static WIZARD2_53_PROTOCOL.MSG_PETTOMEPETADDED PetAdded(Wizard wizard, uint templateId) => new() {
        GlobalID = wizard.GameObjectID,
        PetTemplateID = templateId,
    };

    /// <summary>
    /// Adds every pet the wizard has now (backpack, worn, bank, attic) to the tome and returns the whole tome.
    /// </summary>
    /// <param name="wizard">The wizard.</param>
    /// <param name="added">The kinds this call added.</param>
    internal static IReadOnlyList<uint> Collect(Wizard wizard, out List<uint> added) {
        added = [];
        if (wizard?.PetOwnerBehavior is not { } owner) {
            return [];
        }

        owner.PetTome ??= [];
        foreach (var templateId in OwnedNow(wizard)) {
            if (!owner.PetTome.Contains(templateId)) {
                owner.PetTome.Add(templateId);
                added.Add(templateId);
            }
        }

        if (added.Count > 0) {
            WizardCollection.UpdateCharacterPetOwnerBehavior(wizard);
        }

        return owner.PetTome;
    }

    /// <summary>
    /// The pet kinds in the wizard's attic.
    /// </summary>
    internal static IEnumerable<uint> InAttic(Wizard wizard) {
        Attic attic = null;
        try {
            attic = AtticCollection.Load(wizard.CharId);
        }
        catch (Exception e) {
            Logger.Error("Pet tome: could not read the attic of {0}: {1}", Logger.Args(wizard.CharId, e));
        }

        return (attic?.Items ?? []).Where(item => item is not null).Select(item => item.TemplateId).Where(PetFactory.IsPetTemplate);
    }

    /// <summary>
    /// Writes a tome in the PetTomeBehavior data format.
    /// </summary>
    internal static ByteString Write(IReadOnlyCollection<uint> templates) {
        var kinds = templates.Distinct().Take(ushort.MaxValue).ToList();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Identifier);
        writer.Write(Version);
        writer.Write((ushort) kinds.Count);
        writer.Write((ushort) 0);
        foreach (var templateId in kinds) {
            writer.Write(templateId);
        }

        writer.Flush();

        return stream.ToArray();
    }

    private static IEnumerable<uint> OwnedNow(Wizard wizard) {
        // An egg still in its hatching slot shares its pet item's id and is not a pet yet.
        var eggs = new HashSet<ulong>((wizard.PetOwnerBehavior.MorphingSlots ?? []).Select(egg => (ulong) egg.m_globalID));
        var items = (wizard.InventoryBehavior?.Items ?? Enumerable.Empty<WizClientObjectItem>())
            .Concat(wizard.EquipmentBehavior?.EquippedItems ?? Enumerable.Empty<WizClientObjectItem>())
            .Concat(wizard.StorageBehavior?.Items ?? Enumerable.Empty<WizClientObjectItem>());

        return items.Where(item => item is not null && !eggs.Contains(item.m_globalID))
            .Select(item => (uint) item.m_templateID)
            .Where(PetFactory.IsPetTemplate)
            .Concat(InAttic(wizard))
            .Distinct();
    }

}
