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
 */

/*
 * ========================================================================
 * TEMPLATE FILE DIALOG
 * ========================================================================
 *
 * PURPOSE:
 * Reads the ActorDialog an object's template file holds, ready to send with MSG_ACTORDIALOG. Used for objects whose
 * interact option is a class (or has fields) the server does not read with the template, such as the Deckathalon Tome's
 * pages and the Scales of Knowledge's "Level Scaling 101".
 *
 * USAGE EXAMPLE:
 * var data = TemplateFileDialog.Read(templateId, "Deckathalon Tome");
 * if (data is not null) { playerActor.Tell(new WIZARD_12_PROTOCOL.MSG_ACTORDIALOG { ActorDialog = data }); }
 *
 * NOTE:
 * The dialog starts with its class hash at a whole byte of the file, so it is read as a file of its own (the BiND
 * header, then the object). The first ActorDialog of the file is read. Each template is read once.
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/28/2026
 */

using System;
using System.Collections.Concurrent;
using System.Linq;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Madlibs;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Zone.Components;

internal static class TemplateFileDialog {

    // Each template's dialogue, read once from its file (null when it has none).
    private static readonly ConcurrentDictionary<uint, byte[]> s_dialogs = new();

    /// <summary>
    /// The template's first ActorDialog, serialized for MSG_ACTORDIALOG, or null when its file has none.
    /// </summary>
    internal static byte[] Read(uint templateId, string label)
        => s_dialogs.GetOrAdd(templateId, id => ReadFromFile(id, label));

    private static byte[] ReadFromFile(uint templateId, string label) {
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

            Logger.Information("{0} {1}: read {2} pages ({3}).",
                Logger.Args(label, templateId, dialog.m_dialogEntries.Count, string.Join(", ", dialog.m_dialogEntries.Select(e => e?.m_dialog.ToString()))));

            return serialized;
        }
        catch (Exception e) {
            Logger.Warning("{0} {1}: could not read its dialogue: {2}", Logger.Args(label, templateId, e.Message));

            return null;
        }
    }

}
