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
 * INTERACT LOYALTY STORE
 * ========================================================================
 *
 * PURPOSE:
 * Majid's Loyalty Store option. The game's own option (ClientLoyaltyStoreOption::RequestAction) asks the server for
 * the store with MSG_REQUESTEXPLICITLOYALTYSTOREUPDATE, which LoyaltyStoreService answers; the server only offers it.
 *
 * USAGE EXAMPLE:
 * Attached to Majid (WC-Shop-NPC-Loyalty, 1507720) in the Wizard City Shopping District.
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/25/2026
 */

using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Loyalty;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class InteractLoyaltyStoreComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    // The option's line in the NPC dialog: the first of these the game has, else the store's name.
    private static readonly string[] s_displayKeys = ["MembershipLoyalty_LoyaltyStoreTitle", "GUI_LoyaltyStore", "GUI_ShopOptionLoyalty"];
    private static string s_displayKey;

    public string ServiceName     => "LoyaltyStoreService";
    public string NpcIcon         => null;
    public string NpcNameKey      => null;
    public string NpcTextKey      => null;
    public WizBangs WizBang       => WizBangs.Shopping;
    public string StateName       => "Shop";
    public string InteractWizBang => "Shopping";
    public string DisplayKey      => s_displayKey ??= FindDisplayKey();

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => LoyaltyStore.IsMajid(template);

    public override void OnStart()
        => Logger.Information("Loyalty Store | Majid (object {0}) opens his store in {1}.",
            Logger.Args(Entity.ActiveGameObject.m_globalID.Full, Entity.Zone?.ZoneData?.m_zoneName.ToString()));

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard _)
        => [
            new LoyaltyStoreOption {
                m_displayKey = DisplayKey,
                m_iconKey = "Shopping",
                m_serviceName = ServiceName,
            }
        ];

    // The game opens the store itself (MSG_REQUESTEXPLICITLOYALTYSTOREUPDATE); here the wizard only looks like shopping.
    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        Logger.Information("Loyalty Store | {0} picked Majid's store.", Logger.Args(playerCharacter?.CharId));
        Broadcast(new GAME_5_PROTOCOL.MSG_WIZBANG { WizBangID = (uint) WizBang, GameObjectID = playerObject.m_globalID });
        Broadcast(new GAME_5_PROTOCOL.MSG_ENTERSTATE { State = StringHash.Compute(StateName), GameObjectID = playerObject.m_globalID });
    }

    private void Broadcast(IMessage message)
        => Entity.ZoneRef.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST { Message = message, Selfless = false });

    private static string FindDisplayKey() {
        var key = s_displayKeys.FirstOrDefault(k => Locale.GetEnglishName(k).Length > 0)
            ?? Locale.KeyOf("Loyalty Store")
            ?? s_displayKeys[0];
        Logger.Information("Loyalty Store | The dialog option shows {0} (\"{1}\").", Logger.Args(key, Locale.GetEnglishName(key)));

        return key;
    }

}
