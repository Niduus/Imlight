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

using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using Imlight.Common;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// The Customs tab's portals from the game (CustomTeleporters, Portals.json), the way a house teleporter is set up:
/// `.portal new [name]` puts one where you stand, then walk to where it should send players and type `.portal here`
/// (Niduus, 2026-11-04: "I would also like a 'Teleport here' feature ... Basically to work how it does in housing").
/// `.portal walk` / `.portal press` pick how it is used, `.portal name &lt;text&gt;` its prompt name, `.portal list`,
/// `.portal delete`. Without a number a command means the portal you are setting up, else the nearest one.
/// </summary>
internal class CommandPortalProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    // How near a portal has to be to be "the nearest one" when no number is given.
    private const float NEAR = 600f;

    // The portal each wizard made last and has not given a destination yet (housing's "pick where it goes").
    private static readonly ConcurrentDictionary<ulong, int> s_settingUp = new();

    [Command("portal")]
    [Alias("portals")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void PortalCommand([Remainder] string what = "") {
        var text = (what ?? "").Trim();
        var space = text.IndexOf(' ');
        var verb = (space < 0 ? text : text[..space]).ToLowerInvariant();
        var rest = space < 0 ? "" : text[(space + 1)..].Trim();
        var me = Context.Character;
        if (me is null) {
            return;
        }

        switch (verb) {
            case "new":
            case "make":
                New(me, rest);

                break;
            case "here":
            case "teleporthere":
            case "to":
                Here(me, rest);

                break;
            case "walk":
                SetWalk(me, rest, true);

                break;
            case "press":
            case "interact":
                SetWalk(me, rest, false);

                break;
            case "name":
                Name(me, rest);

                break;
            case "list":
                List();

                break;
            case "delete":
            case "remove":
                Delete(me, rest);

                break;
            default:
                InformSenderClient("Portals: .portal new [name] puts a portal where you stand. Then walk to where it should send players " +
                    "and type .portal here. Also .portal walk (walking in teleports), .portal press (press X in it), " +
                    ".portal name <text> (its prompt name), .portal list and .portal delete. Add a portal's number to pick one; " +
                    "without it, the one you are setting up or the nearest. Ahziael's Customs tab has every setting.");

                break;
        }
    }

    private static (float X, float Y, float Z, float Yaw) SpotOf(Wizard me)
        => (MathF.Round(me.Location.X, 2), MathF.Round(me.Location.Y, 2), MathF.Round(me.Location.Z, 2), me.Orientation.Z);

    private void New(Wizard me, string name) {
        if (string.IsNullOrEmpty(me.Zone)) {
            InformSenderClient("Your area is not known yet. Walk a step and try again.");

            return;
        }

        var (x, y, z, yaw) = SpotOf(me);
        var id = 0;
        var ok = CustomTeleporters.EditPortals(portals => {
            id = 1;
            while (portals.Any(p => p.Id == id)) {
                id++;
            }

            portals.Add(new CustomTeleporters.Portal {
                Id = id, Name = name, Prompt = "Teleport", Zone = me.Zone, X = x, Y = y, Z = z, Yaw = yaw, Look = CustomTeleporters.SHADOW_PORTAL,
            });

            return true;
        });
        if (!ok) {
            InformSenderClient("Portals.json could not be read, so nothing was changed. Fix or delete it in the Customs folder.");

            return;
        }

        s_settingUp[me.CharId] = id;
        InformSenderClient($"Portal {id} is where you stand. Now walk to where it should send players (any area) and type .portal here.");
    }

    // The portal a command means: its number, the one being set up, or the nearest in this area.
    private CustomTeleporters.Portal Pick(Wizard me, System.Collections.Generic.List<CustomTeleporters.Portal> portals, string number, bool settingUpFirst) {
        if (number.Length > 0) {
            if (int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && portals.FirstOrDefault(p => p.Id == id) is { } numbered) {
                return numbered;
            }

            InformSenderClient($"There is no portal {number}. Type .portal list to see them.");

            return null;
        }

        if (settingUpFirst && s_settingUp.TryGetValue(me.CharId, out var pending) && portals.FirstOrDefault(p => p.Id == pending) is { } setUp) {
            return setUp;
        }

        var near = portals
            .Where(p => string.Equals(p.Zone, me.Zone, StringComparison.OrdinalIgnoreCase))
            .Select(p => (Portal: p, Far: MathF.Sqrt(((p.X - me.Location.X) * (p.X - me.Location.X)) + ((p.Y - me.Location.Y) * (p.Y - me.Location.Y)))))
            .Where(p => p.Far <= NEAR)
            .OrderBy(p => p.Far)
            .Select(p => p.Portal)
            .FirstOrDefault();
        if (near is null) {
            InformSenderClient("No portal is near you. Stand by one, or add its number (.portal list shows them).");
        }

        return near;
    }

    private void Here(Wizard me, string number) {
        if (string.IsNullOrEmpty(me.Zone)) {
            InformSenderClient("Your area is not known yet. Walk a step and try again.");

            return;
        }

        var (x, y, z, yaw) = SpotOf(me);
        string said = null;
        var ok = CustomTeleporters.EditPortals(portals => {
            // Housing's way: the portal being set up first, even from another area.
            if (Pick(me, portals, number, settingUpFirst: true) is not { } portal) {
                return false;
            }

            (portal.ToZone, portal.ToX, portal.ToY, portal.ToZ, portal.ToYaw) = (me.Zone, x, y, z, yaw);
            s_settingUp.TryRemove(me.CharId, out _);
            said = $"Portal {portal.Id} now sends players to where you stand, facing the way you face.";

            return true;
        });
        Report(ok, said);
    }

    private void SetWalk(Wizard me, string number, bool walk) {
        string said = null;
        var ok = CustomTeleporters.EditPortals(portals => {
            if (Pick(me, portals, number, settingUpFirst: true) is not { } portal) {
                return false;
            }

            portal.Walk = walk;
            said = walk ? $"Portal {portal.Id} now teleports whoever walks into it." : $"Portal {portal.Id} now teleports when X is pressed in it.";

            return true;
        });
        Report(ok, said);
    }

    private void Name(Wizard me, string rest) {
        // ".portal name 3 To the Library" names portal 3; ".portal name To the Library" the one being set up or the nearest.
        var space = rest.IndexOf(' ');
        var number = space > 0 && int.TryParse(rest[..space], out _) ? rest[..space] : "";
        var name = (number.Length > 0 ? rest[(space + 1)..] : rest).Trim();
        string said = null;
        var ok = CustomTeleporters.EditPortals(portals => {
            if (Pick(me, portals, number, settingUpFirst: true) is not { } portal) {
                return false;
            }

            portal.Name = name;
            said = name.Length > 0 ? $"Portal {portal.Id}'s prompt now says {name}." : $"Portal {portal.Id}'s prompt now says where it goes.";

            return true;
        });
        Report(ok, said);
    }

    private void Delete(Wizard me, string number) {
        string said = null;
        var ok = CustomTeleporters.EditPortals(portals => {
            if (Pick(me, portals, number, settingUpFirst: false) is not { } portal) {
                return false;
            }

            portals.Remove(portal);
            said = $"Portal {portal.Id} is deleted.";

            return true;
        });
        Report(ok, said);
    }

    private void List() {
        var portals = CustomTeleporters.ReadPortals(out var ok);
        if (!ok) {
            InformSenderClient("Portals.json could not be read. Fix or delete it in the Customs folder.");

            return;
        }

        InformSenderClient(portals.Count == 0
            ? "There are no portals yet. Type .portal new where one should stand."
            : string.Join("\n", portals.OrderBy(p => p.Id).Select(p =>
                $"{p.Id}. {(p.Name.Length > 0 ? p.Name : "(says where it goes)")} in {Short(p.Zone)}, " +
                $"{(string.IsNullOrEmpty(p.ToZone) ? "no destination yet" : "to " + Short(p.ToZone))}, {(p.Walk ? "walk in" : "press X")}")));
    }

    private static string Short(string zone) => zone?.Split('/').LastOrDefault() ?? "";

    private void Report(bool ok, string said) {
        if (!ok) {
            InformSenderClient("Portals.json could not be read, so nothing was changed. Fix or delete it in the Customs folder.");
        }
        else if (said is not null) {
            InformSenderClient(said + " It counts at once.");
        }
    }

}
