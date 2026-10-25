using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

/// <summary>
/// A raid gate on the battle island of the guild grounds (RaidGate1-3, RaidGateBehavior; Game/Raids). Pressing X lists
/// the raids; picking one opens a portal in the gate (with [Raids] NeedKey, it uses up a key for that raid from the
/// backpack). While the portal is open, walking into the gate goes into the raid copy of the wizard who opened it, so a
/// group plays one raid together; the first one in starts it fresh.
/// </summary>
internal sealed class RaidGateComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    // GuildRaidGate states (StateData): the portal shows in the gate, and the gate goes dark again when it closes.
    private const string PORTAL_OPEN_STATE = "Portal Appear Transition";
    private const string CLOSED_STATE = "Inactive";
    private const float INTERACTION_TOLERANCE = 2.0f;
    // How close to the gate walking in takes a wizard into the raid (the portals' RaidTeleportBehavior m_range).
    private const float PORTAL_RANGE = 175f;
    private static readonly TimeSpan ResendAfter = TimeSpan.FromSeconds(15);

    private Raids.Raids.Portal _portal;
    private readonly Dictionary<ulong, DateTime> _sent = [];
    private readonly HashSet<ulong> _toldFull = [];

    public string ServiceName     => "RaidGate";
    public string NpcIcon         => null;
    public string NpcNameKey      => null;
    public string NpcTextKey      => null;
    public WizBangs WizBang       => WizBangs.None;
    public string StateName       => null;
    public string InteractWizBang => null;
    public string DisplayKey      => null;

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => Raids.Raids.Enabled && template is GameObjectTemplate go
        && go.m_behaviors?.OfType<RaidGateBehaviorTemplate>().Any(b => !b.m_isExtractor) == true;

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard playerCharacter)
        => Raids.Raids.All.Select(raid => new KioskOption {
            m_serviceName = ServiceName,
            m_displayKey = raid.KeyNameKey,
            m_iconKey = "Kiosk",
        });

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        try {
            if (playerCharacter is null || serviceOptionIndex >= Raids.Raids.All.Count) {
                return;
            }

            if (!IsInHorizontalRadius(playerObject, ((IServiceComponent) this).DEFAULT_INTERACTION_RADIUS * INTERACTION_TOLERANCE)) {
                return;
            }

            var raid = Raids.Raids.All[(int) serviceOptionIndex];
            if (_portal is { IsOpen: true } open) {
                if (open.OwnerCharId != playerCharacter.CharId || open.Raid == raid) {
                    Tell(playerActor, $"A portal to {open.Raid.Name} is already open in this gate. Walk into the gate to go in.");

                    return;
                }
            }

            if (Raids.Raids.NeedKey && !UseKey(playerActor, playerCharacter, raid)) {
                Tell(playerActor, $"You need a raid key for {raid.Name} in your backpack to open this gate.");

                return;
            }

            _portal = Raids.Raids.Open(raid, playerCharacter.CharId);
            _sent.Clear();
            _toldFull.Clear();
            Entity.ChangeState(PORTAL_OPEN_STATE);
            Logger.Information("Raids | {0} opened a portal to {1} at {2}.",
                Logger.Args(playerCharacter.CharId, raid.Name, Entity.ActiveGameObject?.m_debugName));
            Tell(playerActor, $"The portal to {raid.Name} is open for {Raids.Raids.PortalMinutes / 60.0:0.#} hour(s). "
                + $"Walk into the gate to go in. Up to {raid.Players} wizards can go in together.");
        }
        catch (Exception ex) {
            Logger.Error("Raids | Opening a raid gate failed: {0}", Logger.Args(ex));
        }
    }

    public override void OnPlayerMove(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        if (_portal is null || playerWizard is null || playerObj is null) {
            return;
        }

        if (!_portal.IsOpen) {
            _portal = null;
            Entity.ChangeState(CLOSED_STATE);

            return;
        }

        if (!IsInHorizontalRadius(playerObj, PORTAL_RANGE)) {
            return;
        }

        var charId = playerWizard.CharId;
        if (_sent.TryGetValue(charId, out var last) && DateTime.UtcNow - last < ResendAfter) {
            return;
        }

        var portal = _portal;
        if (!portal.Entered.ContainsKey(charId) && portal.Entered.Count >= portal.Raid.Players) {
            if (_toldFull.Add(charId)) {
                Tell(playerActor, $"{portal.Raid.Name} already has {portal.Raid.Players} wizards in it.");
            }

            return;
        }

        _sent[charId] = DateTime.UtcNow;
        portal.Entered.TryAdd(charId, 0);
        var fresh = portal.TryStart();
        if (charId != portal.OwnerCharId) {
            ZoneInstanceOwners.Assign(charId, portal.Raid.Zone, portal.OwnerCharId);
        }
        else {
            ZoneInstanceOwners.Clear(charId, portal.Raid.Zone);
        }

        Logger.Information("Raids | {0} goes into {1} (the copy of {2}, fresh {3}).",
            Logger.Args(charId, portal.Raid.Name, portal.OwnerCharId, fresh));
        playerActor.Tell(new CHARACTER_103_PROTOCOL.MSG_DOTELEPORTEFFECTS());
        playerActor.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
            DestinationZone = portal.Raid.Zone,
            DestinationLocation = Raids.Raids.START_LOCATION,
            SendToClient = true,
            IsPrivate = true,
            OwnerCharId = portal.OwnerCharId,
            ResetInstance = fresh,
        });
    }

    private static bool UseKey(IActorRef playerActor, Wizard wizard, Raids.Raids.Raid raid) {
        var key = wizard.InventoryBehavior.Items.FirstOrDefault(item => raid.Keys.Any(k => item.m_templateID == k));
        if (key is null) {
            return false;
        }

        ulong keyId = key.m_globalID;
        if (!wizard.RemoveItemFromInventory(keyId)) {
            return false;
        }

        playerActor.Tell(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_REMOVEITEM { GlobalID = wizard.GameObjectID, ItemID = keyId });

        return true;
    }

    private static void Tell(IActorRef playerActor, string line)
        => playerActor.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = line, Modal = 0 });

}
