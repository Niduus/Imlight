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
 * INTERACT DUNGEON SIGIL COMPONENT
 * ========================================================================
 * 
 * PURPOSE:
 * Makes a client-shipped dungeon-entry sigil pad (a MinigameSigilInfo, the "Street N <name> Instance
 * Sigil" octagons, tid 107081) a press-X interactable. The pad resolves where it leads from the zone's
 * teleport table and, when it resolves, lights its Glowy swirl for each joining player and hands the
 * pressing player's session a MSG_STARTSIGILENTRY (dismount + snap + countdown + private-instance
 * transfer, owned by ZoneService).
 *
 * A pad for several players (MinigameSemiCircleSigil4Actor: 4) sends the players who press it during one
 * countdown into one fresh private copy together, as the hatching duo pad does (Niduus, 2026-10-22): the
 * first starts the countdown and owns the copy; each other one joins that countdown on a face of their own
 * and arrives a second after the owner, in the owner's copy. A player who steps off the pad or leaves the
 * zone drops out; when the owner does, the next player takes the copy over.
 * 
 * USAGE EXAMPLE:
 * Attaches automatically to sigil-pad templates (ShouldAttachToEntity); a pad with no resolvable
 * destination (e.g. a gauntlet teleport) stays inert and unlit.
 * 
 * NOTE:
 * Each pad entity resolves and caches its own destination from ZoneDataCollection, so nothing global
 * tracks sigils: the zone data is the single source of truth. Matches the same Street-N naming the
 * combat sigil supervisor conventions use; the fallback derives "<parent>/Interiors/<leaf>_T<N>" for
 * towers whose entrance teleport isn't named like the pad.
 * 
 * TODO:
 * - Quest/DynaMod gating of the Glowy state (when a pad should be dark) is future work.
 * 
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 08/13/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.Math;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Housing;
using Imlight.CoreLib.Game.Sigils;
using Imlight.CoreLib.Game.TeamUp;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed partial class InteractDungeonSigilComponent(ZoneEntity entity)
    : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory, IWithTimers {

    public ITimerScheduler Timers { get; set; }

    private const uint SIGIL_TEMPLATE_ID = 107081;
    private const float DEFAULT_PAD_RADIUS = 370.0f;

    public string ServiceName => "Interact";
    public string NpcIcon => "";
    // The prompt names the dungeon by its zone's own name (WizardZone_*), the same text as the game's sigil names
    // (SigilText.lang); without it the pad said only "Press X to Talk" (Niduus, 2026-10-25).
    public string NpcNameKey => TryResolveDestination(out var sigil) ? TeamUpSigils.DisplayKeyNow(sigil.DestinationZone) : "";
    public string NpcTextKey => string.IsNullOrEmpty(NpcNameKey) ? "" : "GUI_EnterDoor";
    public WizBangs WizBang => WizBangs.None;
    public string StateName => null;
    public string InteractWizBang => null;
    public string DisplayKey => null;

    public float DEFAULT_INTERACTION_RADIUS
        => SigilInfo is not null && SigilInfo.m_radius > 0 ? SigilInfo.m_radius : DEFAULT_PAD_RADIUS;

    // The pad's lit state in MinigameSigilStatesNew; its animation is the pad's "On".
    private const string LIT_STATE = ZoneObjects.SIGIL_LIT_STATE;

    // The sigil countdown (ZoneService's), and how much later than the owner the others go, so the owner's fresh copy
    // is there first and theirs joins it.
    private const float COUNTDOWN_SECONDS = 10.0f;
    private const float JOINER_DELAY_SECONDS = 1.0f;
    // With less countdown left than this, a player pressing the pad starts a trip of their own.
    private const float PARTY_CLOSED_SECONDS = 1.5f;

    private ResolvedSigil _resolved;
    // The dungeon a housing gauntlet's countdown goes to, picked for the player who started it.
    private string _partyZone;
    // The players counting down on the pad together, the owner of the copy first.
    private readonly List<PartyMember> _party = [];
    private DateTime _partyEnds;
    // Players the pad has been lit for since they joined (once more on their first move).
    private readonly HashSet<IActorRef> _litFor = [];
    // Every player in the zone the pad knows of, so a look changed in the Customs tab reaches them all at once.
    private readonly HashSet<IActorRef> _players = [];
    // A changed look is sent once the pad was sent again (RenderComponent's 400 ms), and the last state of a look that
    // goes through another (Red, through Glowy) once more a moment later.
    private static readonly TimeSpan RELIGHT_ALL_DELAY = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan CONFIRM_DELAY = TimeSpan.FromMilliseconds(1500);
    private const string RELIGHT_ALL_TIMER = "SigilRelightAll";
    private MinigameSigilInfo SigilInfo 
        => Entity.Info as MinigameSigilInfo;
    private bool IsAvailable
         => SigilInfo is not null && TryResolveDestination(out _);

    [GeneratedRegex(@"street\s*(\d+)\s+(.+?)\s+instance\s+sigil", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-US")]
    private static partial Regex StreetTowerRegex();
    [GeneratedRegex(@"tower\s*0*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-US")]
    private static partial Regex TowerNumberRegex();

    public static bool ShouldAttachToEntity(CoreTemplate template) {
        if (template is not GameObjectTemplate go) {
            return false;
        }

        if (go.m_templateID == SIGIL_TEMPLATE_ID) {
            return true;
        }

        // Name-based catch for other sigil-pad templates; the available-gate below keeps anything
        // that isn't a resolvable dungeon entry inert.
        if (go.m_objectName.IsEmpty) {
            return false;
        }

        var name = go.m_objectName.ToString();

        return (name.Contains("Sigil") && !name.Contains("Combat"))
            || name.Contains("TeleportSemiCircle")
            || name.Contains("TeleportFullCircle");
    }

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard _) {
        if (!IsAvailable) {
            return [];
        }

        // Team Up (Game/TeamUp): the pad is listed by its global id, which the sigil window's Team Up! button sends, and
        // a pad that takes a team offers the game's sigil option, whose window has that button.
        var obj = Entity.ActiveGameObject;
        if (TryResolveDestination(out var sigil) && obj is not null && HouseGauntlets.PadOf(obj.m_globalID.Full) is null) {
            TeamUpSigils.Register(obj.m_globalID.Full, Entity.Zone?.ZonePath, obj.m_location.X, obj.m_location.Y,
                sigil.DestinationZone, sigil.DestinationLoc, sigil.Variant);
            if (TeamUpBoard.IsEnabled && TeamUpBoard.SigilButton && PartySize() > 1) {
                return [
                    new MinigameSigilOption {
                        m_serviceName = ServiceName,
                        m_teamUpAllowed = 1,
                        m_successZone = sigil.DestinationZone,
                    }
                ];
            }
        }

        return [
            new InteractableOption { m_serviceName = ServiceName }
        ];
    }

    public override void OnPlayerJoin(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        _players.Add(playerActor);
        _litFor.Remove(playerActor);
        Light(playerActor, "join");
    }

    // The join-time state may be sent before the game has the pad, which leaves it Off, so it goes once more on the
    // player's first move, as the hatching pads do (Niduus, 2026-10-21: the Ravenwood sigil showed Off).
    public override void OnPlayerMove(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        if (playerWizard is not null) {
            _players.Add(playerActor);
            if (_litFor.Add(playerActor)) {
                Light(playerActor, "first move");
            }
        }

        // A player of the countdown who stepped off the pad drops out of it (ZoneService cancels their own trip).
        var pad = Entity.ActiveGameObject?.m_location;
        if (playerObj is not null && pad is { } at && _party.Any(m => m.Actor.Equals(playerActor))
            && SigilPadCheck.IsOff(at.X - playerObj.m_location.X, at.Y - playerObj.m_location.Y, DEFAULT_INTERACTION_RADIUS)) {
            LeaveParty(playerActor);
        }
    }

    public override void OnPlayerLeave(IActorRef playerActor, ulong id) {
        _players.Remove(playerActor);
        _litFor.Remove(playerActor);
        Timers?.Cancel(("SigilConfirm", playerActor));
        LeaveParty(playerActor);
    }

    // The Customs tab moved the pad, changed its look or where it leads while the zone runs. RenderComponent moves it
    // and sends it again (400 ms later); the look goes to every player in the zone a moment after that, so the change
    // shows without moving or coming into the zone again (a look sent only on the next move was lost when the pad was
    // sent again after it, with the state the zone loaded it in). Its destination is read again on the next press.
    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONEOBJECTSCHANGED))]
    private void ReceiveZoneObjectsChanged(ZONE_102_PROTOCOL.MSG_ZONEOBJECTSCHANGED message) {
        if (Entity.ActiveGameObject is not { } obj || !message.Moved.TryGetValue(obj.m_globalID.Full, out var moved)) {
            return;
        }

        _resolved = null;
        // The pad's own info takes the file's new look now (RenderComponent rebinds it too; either may run first).
        ZoneObjects.Rebind(Entity.Info, moved);
        if (Entity.Info is { } info && moved is not null && !string.IsNullOrEmpty(moved.m_startState)) {
            info.m_startState = moved.m_startState;
        }

        _litFor.Clear();
        _litFor.UnionWith(_players);
        Timers?.StartSingleTimer(RELIGHT_ALL_TIMER,
            new ZONE_102_PROTOCOL.MSG_DUNGEONSIGILRELIGHT { Why = "look changed in the Customs tab" }, RELIGHT_ALL_DELAY);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_DUNGEONSIGILRELIGHT))]
    private void ReceiveRelight(ZONE_102_PROTOCOL.MSG_DUNGEONSIGILRELIGHT message) {
        if (message.Player is not null) {
            if (_players.Contains(message.Player)) {
                Light(message.Player, message.Why, message.LastOnly);
            }

            return;
        }

        foreach (var player in _players.ToList()) {
            Light(player, message.Why);
        }
    }

    private void Light(IActorRef playerActor, string why, bool lastOnly = false) {
        if (playerActor is null || !IsAvailable) {
            return;
        }

        // Red (the Advanced Mode look) is only reached from Glowy in the pad's state set, so it goes through Glowy.
        var look = ZoneObjects.LitStateOf(SigilInfo) ?? LIT_STATE;
        var path = ZoneObjects.SigilStatePath(look);
        var states = lastOnly ? path[^1..] : path;
        foreach (var state in states) {
            Entity.ChangeStateExclusiveSender(state, playerActor);
        }

        Logger.Debug("Dungeon sigil {0} (gid {1}): sent state {2} (hash {3}) to {4} ({5}).",
            Logger.Args(Entity.Info?.m_zoneTag ?? SigilInfo.m_zoneTag2.ToString(), Entity.ActiveGameObject?.m_globalID.Full ?? 0,
                string.Join(" then ", states), string.Join(", ", states.Select(s => StringHash.Compute(s))),
                playerActor.Path.Name, why));

        // A look of two states: its last one goes once more a moment later, for a game still busy with the first.
        if (!lastOnly && path.Length > 1) {
            Timers?.StartSingleTimer(("SigilConfirm", playerActor),
                new ZONE_102_PROTOCOL.MSG_DUNGEONSIGILRELIGHT { Player = playerActor, LastOnly = true, Why = why + ", again" },
                CONFIRM_DELAY);
        }
    }

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        if (!TryResolveDestination(out var sigil)) {
            return;
        }

        var obj = Entity.ActiveGameObject;
        var pad = obj.m_location;
        var heading = obj.m_orientation.Z;
        var sigilLoc = Util.GetCompactStringFromVector(new Vector4(pad.X, pad.Y, pad.Z, heading));

        // Hand off to the player's session, which owns dismount + snap + countdown + transfer. The pad
        // object's gid lets the session trip the client's native on-face countdown on it.
        var entry = new ZONE_102_PROTOCOL.MSG_STARTSIGILENTRY {
            SigilLoc = sigilLoc,
            SigilGID = (ulong) obj.m_globalID,
            SigilType = SigilInfo.m_sigilType.ToString() ?? "",
            Radius = DEFAULT_INTERACTION_RADIUS,
            SigilScale = obj.m_fScale,
            DestinationZone = sigil.DestinationZone,
            DestinationLoc = sigil.DestinationLoc,
            Variant = sigil.Variant,
        };

        var now = DateTime.UtcNow;
        if (now >= _partyEnds) {
            _party.Clear();
        }

        // Pressing again while counting down changes nothing.
        if (_party.Any(m => m.Actor.Equals(playerActor) || m.CharId == playerCharacter.CharId)) {
            return;
        }

        // The sigil of a housing gauntlet (Game/Housing/HouseGauntlets): its dungeon is picked for the level of the player
        // who starts the countdown, and only the owner of the house starts a one-shot.
        var gauntlet = HouseGauntlets.PadOf(obj.m_globalID.Full);
        var size = PartySize();
        var left = (float) (_partyEnds - now).TotalSeconds;
        var startsTrip = size <= 1 || _party.Count >= size || _party.Count == 0 || left < PARTY_CLOSED_SECONDS;
        if (gauntlet is not null) {
            if (startsTrip) {
                if (gauntlet.Gauntlet.IsOneShot && playerCharacter.CharId != gauntlet.OwnerCharId) {
                    Logger.Information("Gauntlet: character {CharId} is not the owner {OwnerId} of lot {LotGid}, so one-shot {TemplateId} does not start.",
                        Logger.Args(playerCharacter.CharId, gauntlet.OwnerCharId, gauntlet.LotGid, gauntlet.Gauntlet.TemplateId));
                    playerActor.Tell(new WIZARDHOUSING_50_PROTOCOL.MSG_DISPOSABLEDUNGEONNOOWNER());

                    return;
                }

                var level = (int) (playerCharacter.MagicSchoolBehavior?.Level ?? 1);
                var picked = HouseGauntlets.ZoneFor(gauntlet.Gauntlet.Sigil, level);
                if (string.IsNullOrEmpty(picked)) {
                    return;
                }

                _partyZone = picked;
                Logger.Information("Gauntlet: character {CharId}, level {Level}, pressed the sigil of gauntlet {TemplateId} of lot {LotGid}; its dungeon is {Zone}.",
                    Logger.Args(playerCharacter.CharId, level, gauntlet.Gauntlet.TemplateId, gauntlet.LotGid, picked));
            }

            entry.DestinationZone = _partyZone ?? entry.DestinationZone;
            entry.LogName = $"Housing gauntlet {gauntlet.Gauntlet.TemplateId}";
            HouseGauntlets.Begin(playerCharacter.CharId, gauntlet, entry.DestinationZone);
        }

        if (size <= 1 || _party.Count >= size) {
            // A pad for one, or a full team: a trip and a fresh copy of their own.
            playerActor.Tell(entry);

            return;
        }

        if (_party.Count == 0 || left < PARTY_CLOSED_SECONDS) {
            // The first player (or one too late for the countdown running) starts a countdown and owns a fresh copy.
            _party.Clear();
            _party.Add(new PartyMember(playerActor, playerCharacter.CharId, 0));
            _partyEnds = now + TimeSpan.FromSeconds(COUNTDOWN_SECONDS);
            playerActor.Tell(entry);

            return;
        }

        // Another player: the countdown already running, on a face of their own, into the owner's copy.
        var face = Enumerable.Range(0, size).First(f => _party.All(m => m.FaceSlot != f));
        _party.Add(new PartyMember(playerActor, playerCharacter.CharId, face));
        entry.FaceSlot = face;
        entry.InstanceOwnerCharId = _party[0].CharId;
        entry.KeepInstance = true;
        entry.CountdownSeconds = left + JOINER_DELAY_SECONDS;
        entry.NoTimerBroadcast = true;
        playerActor.Tell(entry);

        Logger.Information("Dungeon sigil {0}: {1} joins the countdown of {2} ({3} of {4}, {5:0.0} s left).",
            Logger.Args(SigilInfo.m_zoneTag2.ToString(), playerCharacter.CharId, _party[0].CharId, _party.Count, size, left));
    }

    private sealed record PartyMember(IActorRef Actor, ulong CharId, int FaceSlot);

    // How many players the pad takes at once: the N of its sigil type's "NActor", 1 for a pad that names none.
    private int PartySize() {
        var match = Regex.Match(SigilInfo?.m_sigilType.ToString() ?? "", @"(\d+)Actor", RegexOptions.IgnoreCase);

        return match.Success && int.TryParse(match.Groups[1].Value, out var n) && n > 1 ? Math.Min(n, 4) : 1;
    }

    private void LeaveParty(IActorRef playerActor) {
        var index = _party.FindIndex(m => m.Actor.Equals(playerActor));
        if (index < 0) {
            return;
        }

        _party.RemoveAt(index);
        var left = (float) (_partyEnds - DateTime.UtcNow).TotalSeconds;
        if (index != 0 || _party.Count == 0 || left <= 0) {
            return;
        }

        // The owner dropped out: the next player owns a fresh copy and the others go into theirs. The countdowns are
        // timed again (not redrawn) so the new owner still goes first.
        var owner = _party[0];
        foreach (var member in _party) {
            var isOwner = member == owner;
            member.Actor.Tell(new ZONE_102_PROTOCOL.MSG_STARTSIGILENTRY {
                SigilLoc = Util.GetCompactStringFromVector(new Vector4(Entity.ActiveGameObject.m_location.X,
                    Entity.ActiveGameObject.m_location.Y, Entity.ActiveGameObject.m_location.Z,
                    Entity.ActiveGameObject.m_orientation.Z)),
                SigilGID = (ulong) Entity.ActiveGameObject.m_globalID,
                SigilType = SigilInfo.m_sigilType.ToString() ?? "",
                Radius = DEFAULT_INTERACTION_RADIUS,
                SigilScale = Entity.ActiveGameObject.m_fScale,
                DestinationZone = HouseGauntlets.PadOf(Entity.ActiveGameObject.m_globalID.Full) is not null && _partyZone is not null
                    ? _partyZone
                    : _resolved.DestinationZone,
                DestinationLoc = _resolved.DestinationLoc,
                Variant = _resolved.Variant,
                FaceSlot = member.FaceSlot,
                InstanceOwnerCharId = isOwner ? 0 : owner.CharId,
                KeepInstance = !isOwner,
                CountdownSeconds = isOwner ? left : left + JOINER_DELAY_SECONDS,
                NoTimerBroadcast = true,
                Restart = true,
            });
        }

        Logger.Information("Dungeon sigil {0}: the owner left the countdown; {1} owns the copy now ({2} players).",
            Logger.Args(SigilInfo.m_zoneTag2.ToString(), owner.CharId, _party.Count));
    }

    private sealed record ResolvedSigil(string DestinationZone, string DestinationLoc, string Variant = "");

    private static readonly Regex s_streetTowerRx
        = StreetTowerRegex();

    private static readonly Regex s_towerNumRx
        = TowerNumberRegex(); // A tower number anywhere in the tag: "ToTower01" -> 1, "Street 4 Tower 1 Instance Sigil" -> 1.

    private static string NormalizeSigilToken(string s)
        => Regex.Replace(s ?? "", "[^A-Za-z0-9]", "").ToLowerInvariant();

    private bool TryResolveDestination(out ResolvedSigil sigil) {
        sigil = null;
        if (_resolved is not null) {
            sigil = _resolved;

            return true;
        }

        if (SigilInfo is null) {
            return false;
        }

        var zonePath = Entity.Zone?.ZonePath;
        if (string.IsNullOrEmpty(zonePath)) {
            return false;
        }

        // A pad added from spiraldb-extra/ZoneObjects names its own destination (ZoneObjects.cs).
        if (ZoneObjects.TryGetDestination(SigilInfo, out var addedZone, out var addedLoc, out var addedVariant)) {
            _resolved = new ResolvedSigil(addedZone, addedLoc, addedVariant);
            sigil = _resolved;

            return true;
        }

        var tag = SigilInfo.m_zoneTag2.ToString() ?? "";
        var zoneData = ZoneDataCollection.GetZoneData(zonePath);

        var teleport = MatchEntranceTeleport(tag, zoneData);
        if (teleport?.Teleport is not null) {
            _resolved = new ResolvedSigil(
                teleport.Teleport.m_destinationZone ?? "",
                teleport.Teleport.m_destinationLoc ?? "");

            sigil = _resolved;

            return !string.IsNullOrEmpty(_resolved.DestinationZone);
        }

        if (TryDeriveTowerDestination(zonePath, tag, zoneData, out var destZone, out var destLoc)) {
            _resolved = new ResolvedSigil(destZone, destLoc);
            sigil = _resolved;

            return true;
        }

        return false;
    }

    private static WizardTeleportData MatchEntranceTeleport(string tag, WizardZoneData zoneData) {
        if (zoneData?.Teleports is null || zoneData.Teleports.Count == 0) {
            return null;
        }

        // A teleport named exactly like the pad comes first (spiraldb-extra/DungeonTeleports names them so): "Street 6
        // Tower 1" must not take the teleport of "Street 6 CustTower 1".
        var tagNorm = NormalizeSigilToken(tag);
        var exact = zoneData.Teleports.FirstOrDefault(t => {
            var norm = NormalizeSigilToken(t.TriggerName);

            return norm.Length > 0 && norm == tagNorm;
        });
        if (exact is not null) {
            return exact;
        }

        var tagMatch = s_streetTowerRx.Match(tag);
        if (tagMatch.Success) {
            var streetTok = "street" + tagMatch.Groups[1].Value;
            var instance = NormalizeSigilToken(tagMatch.Groups[2].Value);

            return zoneData.Teleports.FirstOrDefault(t => {
                var norm = NormalizeSigilToken(t.TriggerName);

                return norm.Contains(streetTok) && norm.Contains(instance);
            });
        }

        return null;
    }

    private static bool TryDeriveTowerDestination(string zoneName, string tag, WizardZoneData zoneData,
                                                  out string destZone, out string destLoc) {
        destZone = destLoc = null;
        var m = s_towerNumRx.Match(tag ?? "");
        if (!m.Success) {
            return false;
        }

        var slash = zoneName.LastIndexOf('/');
        if (slash < 0) {
            return false;
        }

        var instancePrefix = $"{zoneName[..slash]}/Interiors/{zoneName[(slash + 1)..]}_T";
        var candidate = instancePrefix + int.Parse(m.Groups[1].Value);
        if (ZoneDataCollection.GetZoneData(candidate) is null) {
            return false; // the instance zone must actually exist
        }

        var sibling = zoneData?.Teleports?.FirstOrDefault(t =>
            (t.Teleport?.m_destinationZone ?? "").StartsWith(instancePrefix, StringComparison.OrdinalIgnoreCase));
        if (sibling?.Teleport is null) {
            return false; // no known entry loc for this instance family; don't guess a position
        }

        destZone = candidate;
        destLoc = sibling.Teleport.m_destinationLoc ?? "";

        return true;
    }

}
