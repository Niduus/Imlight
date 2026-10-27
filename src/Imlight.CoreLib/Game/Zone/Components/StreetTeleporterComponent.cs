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
 * STREET TELEPORTER
 * ========================================================================
 *
 * PURPOSE:
 * The game's own street teleporter stones (Niduus 2026-10-27: "existing street teleporters do not work"): a street's
 * hub of three stones (WC_Firecat_TeleporterA/B/C) and, far down the street, the "To Teleporter Hub" stone of each
 * (WC_Firecat_TeleporterA_ToMain). The game files give them nothing but a Press X; where they go is the server's
 * part. A stone sends the wizard to its partner in the same zone: the hub stone to its _ToMain stone and back.
 * Other worlds name theirs the same way (KT_TeleporterB_ToHub_Arena, MS_NEW_Ham_TeleporterA_ToMain).
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed partial class StreetTeleporterComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    // "Press X to Teleport", the stones' own line.
    private const string TELEPORT_TEXT_KEY = "GUI_00005578";

    // How long the teleport effect plays before the player is moved, as the game's own teleports do.
    private const int EFFECT_DELAY_IN_MS = 1500;

    // How far in front of the stone the wizard lands, clear of its collision.
    private const float ARRIVE_DISTANCE = 150f;

    // The stone's pair key ("WC_Firecat_TeleporterA") and whether it is the far stone (_ToMain, _ToHub, _BackToHub).
    [GeneratedRegex(@"^(?<pair>.+?Teleport(?:er)?[A-Z])(?<far>_(?:ToMain|ToHub|BackToHub)(?:_.*)?)?$", RegexOptions.IgnoreCase)]
    private static partial Regex StoneName();

    public string ServiceName     => "Interact";
    public string NpcIcon         => (Entity.Template as GameObjectTemplate)?.m_sIcon;
    public string NpcNameKey      => (Entity.Template as GameObjectTemplate)?.m_displayName;
    public string NpcTextKey      => TELEPORT_TEXT_KEY;
    public WizBangs WizBang       => WizBangs.None;
    public string StateName       => null;
    public string InteractWizBang => null;
    public string DisplayKey      => null;

    private bool _partnerLooked;
    private CoreObjectInfo _partner;

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate go
           && go.m_behaviors?.OfType<InteractableBehaviorTemplate>().Any() == true
           && !go.m_behaviors.Any(b => b is NPCBehaviorTemplate)
           && Parse(go.m_objectName.ToString()) is not null
           // A stone the zone data already sends somewhere is the door component's.
           && !InteractDoorTeleportComponent.ShouldAttachToEntity(template);

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard playerCharacter) {
        // A Customs hub stone uses these templates too and goes where Niduus set it.
        if (CustomTeleporters.For(Entity.Info?.m_zoneTag) is not null || Partner() is null) {
            return [];
        }

        return [new InteractableOption { m_serviceName = ServiceName }];
    }

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        var partner = Partner();
        if (partner is null || playerActor is null || CustomTeleporters.For(Entity.Info?.m_zoneTag) is not null) {
            return;
        }

        // The wizard lands a step from the partner stone toward the middle of the hub's stones: inside the ring at the
        // hub, back up the street at a far stone (the stones' own yaws do not say which side is open ground).
        var (towardX, towardY) = HubMiddle() ?? (partner.m_location.X + 1, partner.m_location.Y);
        var dx = towardX - partner.m_location.X;
        var dy = towardY - partner.m_location.Y;
        var length = MathF.Max(MathF.Sqrt((dx * dx) + (dy * dy)), 0.001f);
        var x = partner.m_location.X + (dx / length * ARRIVE_DISTANCE);
        var y = partner.m_location.Y + (dy / length * ARRIVE_DISTANCE);
        // Facing on along that line (a wizard with yaw w faces (-sin w, -cos w)).
        var wizardYaw = MathF.Atan2(-dx, -dy);
        var spot = string.Join(",", new[] { x, y, partner.m_location.Z, wizardYaw }.Select(v => v.ToString(CultureInfo.InvariantCulture)));

        Logger.Information("Street teleporter: {0} uses {1} in {2} and goes to {3} at {4}.",
            Logger.Args(playerCharacter?.CharId ?? 0, (Entity.Template as GameObjectTemplate)?.m_objectName, Entity.Zone?.ZonePath,
                partner.m_zoneTag, spot));

        playerActor.Tell(new CHARACTER_103_PROTOCOL.MSG_DOTELEPORTEFFECTS(), ActorRefs.NoSender);
        var move = new CHARACTER_103_PROTOCOL.MSG_MOVEINZONE { Location = spot };
        Task.Delay(EFFECT_DELAY_IN_MS).ContinueWith(_ => playerActor.Tell(move, ActorRefs.NoSender));
    }

    // The other stone of the pair in this zone: the hub stone for a far one, the far one for a hub stone.
    private CoreObjectInfo Partner() {
        if (_partnerLooked) {
            return _partner;
        }

        _partnerLooked = true;
        var me = Parse((Entity.Template as GameObjectTemplate)?.m_objectName.ToString());
        var objects = Entity.Zone?.ZoneData?.m_objectList;
        if (me is null || objects is null) {
            return null;
        }

        _partner = objects.FirstOrDefault(o => o is not null && o.m_templateID != 0
            && Parse(CoreObjectFactory.FileNameOf(o.m_templateID)) is { } other
            && string.Equals(other.Pair, me.Value.Pair, StringComparison.OrdinalIgnoreCase)
            && other.Far != me.Value.Far);

        if (_partner is null) {
            Logger.Debug("Street teleporter {0} in {1} has no partner stone in the zone.",
                Logger.Args((Entity.Template as GameObjectTemplate)?.m_objectName, Entity.Zone?.ZonePath));
        }

        return _partner;
    }

    // The middle of this street's hub stones (WC_Firecat_TeleporterA/B/C), or null with fewer than two.
    private (float X, float Y)? HubMiddle() {
        var me = Parse((Entity.Template as GameObjectTemplate)?.m_objectName.ToString());
        var objects = Entity.Zone?.ZoneData?.m_objectList;
        if (me is null || objects is null) {
            return null;
        }

        var street = me.Value.Pair[..^1];
        var hubStones = objects.Where(o => o is not null && o.m_templateID != 0
            && Parse(CoreObjectFactory.FileNameOf(o.m_templateID)) is { Far: false } other
            && other.Pair.StartsWith(street, StringComparison.OrdinalIgnoreCase) && other.Pair.Length == street.Length + 1).ToList();
        if (hubStones.Count < 2) {
            return null;
        }

        return (hubStones.Average(o => o.m_location.X), hubStones.Average(o => o.m_location.Y));
    }

    private static (string Pair, bool Far)? Parse(string name) {
        if (string.IsNullOrEmpty(name)) {
            return null;
        }

        var match = StoneName().Match(name);

        return match.Success ? (match.Groups["pair"].Value, match.Groups["far"].Success) : null;
    }

}
