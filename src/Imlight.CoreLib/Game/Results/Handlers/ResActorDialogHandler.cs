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

using System.Linq;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Madlibs;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Results.Handlers;

internal sealed class ResActorDialogHandler : BaseResultHandler<ResActorDialog> {

    public override bool Execute(IResultContext context) {
        if (Result is null) {
            return false;
        }

        // A one-off line (m_oneShot, with the registry entry the game names for it, e.g. "OnceOff24" on the Sunken City
        // boss taunt) plays once per character, the way the game does: walking through its volume again stays quiet.
        if (!OneShotFirstTime(context)) {
            return true;
        }

        var dialog = Result.m_dialog;
        QuestMadlibs.NameDialogSpeakers(dialog);

        // Serialize the actor dialog into network format.
        var serializer = new ObjectSerializer(Versionable: false);
        if (!serializer.Serialize(dialog, 16, out var serializedData)) {
            Logger.Error("Failed to serialize dialog.");
                
            return false;
        }

        var actorDialogMsg = new WIZARD_12_PROTOCOL.MSG_ACTORDIALOG {
            ActorDialog = serializedData
        };

        context.GetPlayerRef().Tell(actorDialogMsg, Self);

        return true;
    }

    /// <summary>
    /// True when the dialog should play: it is not a one-off, or this character has not heard it yet (then it is
    /// marked heard). A one-off with no registry entry of its own is keyed by its zone and first line.
    /// </summary>
    private bool OneShotFirstTime(IResultContext context) {
        if (!Result.m_oneShot) {
            return true;
        }

        var key = OneShotKey(Result, context.GetZonePath());
        if (key is null) {
            return true;
        }

        var wizard = ZoneTrigger.QueryWizard(context.GetPlayerRef());
        if (wizard is null) {
            return true;
        }

        if (wizard.HasRegistryValue(key)) {
            Logger.Debug("One-off dialog {0} already played for this character; not played again.", Logger.Args(key));

            return false;
        }

        wizard.SetRegistryValue(key, 1);

        return true;
    }

    internal static string OneShotKey(ResActorDialog result, string zonePath) {
        string entry = result.m_registryEntry;
        if (!string.IsNullOrEmpty(entry)) {
            return entry;
        }

        var firstEntry = result.m_dialog?.m_dialogEntries?.OfType<NPCDialogEntry>().FirstOrDefault();
        string firstLine = firstEntry is null ? null : (string) firstEntry.m_dialog;
        if (string.IsNullOrEmpty(firstLine) || string.IsNullOrEmpty(zonePath)) {
            return null;
        }

        return $"OneShotDialog:{zonePath}:{firstLine}";
    }

}