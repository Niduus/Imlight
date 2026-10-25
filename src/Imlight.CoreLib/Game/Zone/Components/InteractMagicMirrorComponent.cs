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
 * INTERACT MAGIC MIRROR
 * ========================================================================
 *
 * PURPOSE:
 * The Magic Mirror shop NPC: talking to it opens the game's mirror window (MSG_MAGICMIRRORSHOPOPEN). What the window
 * asks for is answered by MagicMirrorService.
 *
 * USAGE EXAMPLE:
 * Attached to every NPC whose template has a MagicMirrorShopBehaviorTemplate (WC-ShopArea-MagicMirror, 1439031).
 *
 * NOTE:
 * The window reads its options from the NPC's template itself, so the server only names the NPC.
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/24/2026
 */

using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class InteractMagicMirrorComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    // The option's line in the NPC dialog: the first of these the game has, else the shop's name.
    private static readonly string[] s_displayKeys = ["GUI_MagicMirror", "GUI_MagicMirrorShop", "GUI_MagicMirrorOption"];
    private static string s_displayKey;

    public string ServiceName     => "MagicMirrorShopService";
    public string NpcIcon         => null;
    public string NpcNameKey      => null;
    public string NpcTextKey      => null;
    public WizBangs WizBang       => WizBangs.Shopping;
    public string StateName       => "Shop";
    public string InteractWizBang => "Registrar";
    public string DisplayKey      => s_displayKey ??= FindDisplayKey();

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => MagicMirror.IsMirror(template);

    public override void OnStart()
        => Logger.Information("Magic Mirror | {0} (object {1}) offers the Magic Mirror in {2}.",
            Logger.Args((Entity.Template as GameObjectTemplate)?.m_objectName.ToString(), Entity.ActiveGameObject.m_globalID.Full,
                Entity.Zone?.ZoneData?.m_zoneName.ToString()));

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard _)
        => [
            new MagicMirrorShopOption {
                m_displayKey = DisplayKey,
                m_iconKey = "MagicMirrorShop",
                m_serviceName = ServiceName,
            }
        ];

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        MagicMirror.Load(Entity.Template);
        playerActor.Tell(new WIZARD2_53_PROTOCOL.MSG_MAGICMIRRORSHOPOPEN { GlobalID = Entity.ActiveGameObject.m_globalID.Full });
        Logger.Information("Magic Mirror | {0} opened the Magic Mirror.", Logger.Args(playerCharacter?.CharId));

        Broadcast(new GAME_5_PROTOCOL.MSG_WIZBANG { WizBangID = (uint) WizBang, GameObjectID = playerObject.m_globalID });
        Broadcast(new GAME_5_PROTOCOL.MSG_ENTERSTATE { State = StringHash.Compute(StateName), GameObjectID = playerObject.m_globalID });
    }

    private void Broadcast(IMessage message)
        => Entity.ZoneRef.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST { Message = message, Selfless = false });

    private static string FindDisplayKey() {
        var key = s_displayKeys.FirstOrDefault(k => Locale.GetEnglishName(k).Length > 0)
            ?? Locale.KeyOf("Magic Mirror")
            ?? s_displayKeys[0];
        Logger.Information("Magic Mirror | The dialog option shows {0} (\"{1}\").", Logger.Args(key, Locale.GetEnglishName(key)));

        return key;
    }

}
