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
 */

/*
 * ========================================================================
 * INTERACT DECKATHALON TOME COMPONENT
 * ========================================================================
 *
 * PURPOSE:
 * The Deckathalon Tome (WC-HUB-NPC15_A and _B, the book on a pedestal) as in the live game (Niduus, 2026-10-25:
 * "Deckathalon Tome has legacy behavior. Correct it"): pressing X on it plays its pages, the dialogue its template
 * lists (_A: five pages with the arena camera and the FTUE-Deckathalon01..04 windows, _B: one page), instead of
 * teaching spells. As a house trainer (the trainer hall's tomes) it keeps teaching (InteractTrainerComponent).
 *
 * NOTE:
 * The template's interact option is a class the server does not know, so its dialogue is not read with the template.
 * It is a plain ActorDialog inside the template's file, so that object is read from the file by itself.
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/25/2026
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Madlibs;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class InteractDeckathalonTomeComponent(ZoneEntity entity)
    : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    private static readonly HashSet<string> s_tomes = new(StringComparer.OrdinalIgnoreCase) { "WC-HUB-NPC15_A", "WC-HUB-NPC15_B" };

    // Each tome's dialogue, read once from its template's file (null when it has none).
    private static readonly ConcurrentDictionary<uint, byte[]> s_dialogs = new();

    public string ServiceName => "Interact";
    public string NpcIcon => (Entity.Template as GameObjectTemplate)?.m_sIcon;
    public string NpcNameKey => (Entity.Template as GameObjectTemplate)?.m_displayName;
    public string NpcTextKey => null;
    public WizBangs WizBang => WizBangs.None;
    public string StateName => null;
    public string InteractWizBang => null;
    public string DisplayKey => null;

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate go && !go.m_objectName.IsEmpty && s_tomes.Contains(go.m_objectName.ToString());

    /// <summary>Whether this entity is a Deckathalon Tome that reads its pages: one that is not a house trainer.</summary>
    internal static bool ReadsPages(ZoneEntity entity)
        => entity?.Template is GameObjectTemplate go && !go.m_objectName.IsEmpty && s_tomes.Contains(go.m_objectName.ToString())
           && !HouseTrainers.IsHouseTrainer(entity.Info?.m_zoneTag);

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard playerCharacter)
        => ReadsPages(Entity) ? [new InteractableOption { m_serviceName = ServiceName }] : [];

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        try {
            if (!ReadsPages(Entity) || Entity.Template is not GameObjectTemplate template) {
                return;
            }

            var templateId = (uint) template.m_templateID;
            var data = s_dialogs.GetOrAdd(templateId, ReadDialog);
            if (data is null) {
                Logger.Warning("Deckathalon Tome {0}: its template has no dialogue to read.", Logger.Args(templateId));

                return;
            }

            playerActor.Tell(new WIZARD_12_PROTOCOL.MSG_ACTORDIALOG { ActorDialog = data });
        }
        catch (Exception e) {
            Logger.Error("Deckathalon Tome: reading its pages failed: {0}", Logger.Args(e));
        }
    }

    // Reads the template's ActorDialog: it starts with its class hash, at a whole byte of the file, so it is read as
    // a file of its own (the BiND header, then the object).
    private static byte[] ReadDialog(uint templateId) {
        try {
            var file = CoreObjectFactory.TemplateManifest?.m_serializedTemplates?
                .FirstOrDefault(location => location?.m_id == templateId)?.m_filename;
            if (string.IsNullOrEmpty(file)) {
                return null;
            }

            using var stream = RootArchiveLoader.GetFileStream(file);
            var data = stream.ToArray();
            if (data.Length < 8) {
                return null;
            }

            var hash = BitConverter.GetBytes(new ActorDialog().GetHash());
            var at = data.AsSpan(8).IndexOf(hash);
            if (at < 0) {
                return null;
            }

            at += 8;
            var buffer = new byte[8 + data.Length - at];
            data.AsSpan(0, 8).CopyTo(buffer);
            data.AsSpan(at).CopyTo(buffer.AsSpan(8));
            if (!new BindSerializer().Deserialize(buffer, out ActorDialog dialog) || dialog?.m_dialogEntries is not { Count: > 0 }) {
                return null;
            }

            QuestMadlibs.NameDialogSpeakers(dialog);
            if (!new ObjectSerializer(Versionable: false).Serialize(dialog, 16, out var serialized)) {
                return null;
            }

            Logger.Information("Deckathalon Tome {0}: read {1} pages ({2}).",
                Logger.Args(templateId, dialog.m_dialogEntries.Count, string.Join(", ", dialog.m_dialogEntries.Select(e => e?.m_dialog.ToString()))));

            return serialized;
        }
        catch (Exception e) {
            Logger.Warning("Deckathalon Tome {0}: could not read its dialogue: {1}", Logger.Args(templateId, e.Message));

            return null;
        }
    }

}
