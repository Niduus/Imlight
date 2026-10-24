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
 * MAGIC MIRROR SERVICE
 * ========================================================================
 *
 * PURPOSE:
 * The Magic Mirror window's Buy button (MSG_MAGICMIRRORREQUESTCHANGE): the new look is shown on the wizard
 * (MSG_MAGICMIRRORCHANGEPLAYER, to the wizard and everyone in the zone), saved as Wizard.MirrorLook, and sent again
 * each time the wizard enters a zone. See MagicMirror for what is not known yet.
 *
 * NOTE:
 * The mirror is free here (live charges gold for each change).
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/24/2026
 */

using System;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal class MagicMirrorService(SessionActor sessionActor) : MessageService(sessionActor) {

    private System.Threading.Timer _resendTimer;

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new MagicMirrorService(parentActor));

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_MAGICMIRRORREQUESTCHANGE))]
    private void ReceiveRequestChange(WIZARD2_53_PROTOCOL.MSG_MAGICMIRRORREQUESTCHANGE message) {
        try {
            var wizard = GetActiveWizard();
            if (wizard is null) {
                return;
            }

            // The options are read when a mirror opens; after a server restart with the window still open, from the NPC.
            if (MagicMirror.OptionOf(message.HairStyleID) is null && GetZoneObject(message.GlobalID)?.Template is { } template
                && MagicMirror.IsMirror(template)) {
                MagicMirror.Load(template);
            }

            Logger.Information("Magic Mirror | {0} asked for: object {1}, hair style {2}, hair color {3}, skin color {4}, face {5}, " +
                "eye color {6}, face paint {7}, nose {8}, mouth {9}, accessory {10}, eyebrow color {11}, lip color {12}.",
                Logger.Args(wizard.CharId, message.GlobalID, message.HairStyleID, message.HairColorID, message.SkinColorID,
                    message.FaceStyleID, message.EyeColorID, message.FacePaintID, message.NoseStyleID, message.MouthStyleID,
                    message.AccessoryStyleID, message.EyeBrowColorID, message.LipColorID));

            var look = MagicMirror.FromRequest(message, wizard.MirrorLook, out var notes);
            Logger.Information("Magic Mirror | {0}: {1}.", Logger.Args(wizard.CharId, notes));
            Apply(wizard, look);
        }
        catch (Exception ex) {
            Logger.Error("Magic Mirror | Changing the look failed. {0}", Logger.Args(ex));
        }
    }

    /// <summary>Shows a look on the wizard, to everyone in the zone, and saves it (also used by .mirror look).</summary>
    [InternalMessageHandler(typeof(ApplyMirrorLook))]
    private void ReceiveApplyLook(ApplyMirrorLook message) {
        try {
            if (GetActiveWizard() is not { } wizard) {
                return;
            }

            // No look: send the saved one again (after entering a zone).
            if (message.Look is null) {
                if (wizard.MirrorLook is not null) {
                    Show(wizard);
                }

                return;
            }

            Apply(wizard, message.Look);
        }
        catch (Exception ex) {
            Logger.Error("Magic Mirror | Applying a look failed. {0}", Logger.Args(ex));
        }
    }

    private void Apply(Wizard wizard, int[] look) {
        wizard.MirrorLook = MagicMirror.Normalize(look);
        WizardCollection.UpdateCharacterMirrorLook(wizard);
        Show(wizard);
        Logger.Information("Magic Mirror | {0} now looks: {1}.", Logger.Args(wizard.CharId, MagicMirror.Describe(wizard.MirrorLook)));
    }

    // Each zone the wizard enters starts from the saved character, so the mirror look is sent again, and once more a
    // few seconds later in case the first one came while the game was still building the scene.
    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE))]
    private void ReceiveAttachComplete(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE message) {
        try {
            var wizard = GetActiveWizard();
            if (wizard?.MirrorLook is null) {
                return;
            }

            Show(wizard);

            // The timer runs off the actor, so it only asks the actor to send it again.
            var self = Self;
            _resendTimer?.Dispose();
            _resendTimer = new System.Threading.Timer(_ => self.Tell(new ApplyMirrorLook { Look = null }),
                null, TimeSpan.FromSeconds(3), System.Threading.Timeout.InfiniteTimeSpan);
        }
        catch (Exception ex) {
            Logger.Error("Magic Mirror | Sending the saved look failed. {0}", Logger.Args(ex));
        }
    }

    private void Show(Wizard wizard) {
        var change = MagicMirror.ChangeMessage(wizard.GameObjectID, wizard.MirrorLook);
        SendToSocket(change);
        ZoneBroadcast(change, isSelfless: true);
    }

    protected override void OnDispose() {
        base.OnDispose();
        _resendTimer?.Dispose();
    }

}

/// <summary>Tells a session's MagicMirrorService to show and save a look (`.mirror look`), or with no look to send the saved one again.</summary>
internal sealed class ApplyMirrorLook : IServerMessage {

    public byte MessageOrder { get; } = 1;
    public byte ServiceID { get; } = 105;

    public int[] Look { get; init; }

}
