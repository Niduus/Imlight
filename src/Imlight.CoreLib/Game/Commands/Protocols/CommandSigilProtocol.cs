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
 * HOUSE GAUNTLETS
 * ========================================================================
 *
 * PURPOSE:
 * Sets a house gauntlet's sigil by hand, in the game (Niduus, 2026-10-24: "Can we make a feature so I can manually
 * adjust the sigil?"). It works on the placed gauntlet nearest to the caller and saves for its template, so every copy
 * of that gauntlet takes it (GauntletSigils, gauntlet-sigils.json). The pad moves at once.
 *
 * USAGE EXAMPLE:
 * .sigil here          // the pad's centre goes where you stand; its arc faces away from the gauntlet
 * .sigil here face     // the same, but the arc faces the way you face
 * .sigil size 0.8      .sigil up 10      .sigil down 5      .sigil turn 90      .sigil reset      .sigil info
 *
 * NOTE:
 * - Quality assurance and higher, or the owner of the house the caller is in.
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/24/2026
 */

using System;
using System.Globalization;
using Imlight.CoreLib.Game.Housing;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

internal class CommandSigil : CommandProtocol {

    internal override string Group { get; set; } = "";

    // How far from a gauntlet the caller may stand.
    private const float Reach = 600f;

    private const string Usage = "Use .sigil here, .sigil here face, .sigil size <n>, .sigil up <n>, .sigil down <n>, "
        + ".sigil turn <degrees>, .sigil reset or .sigil info, near a gauntlet in your house.";

    [Command("sigil")]
    private void SigilCommand([Remainder] string text = "") {
        var words = (text ?? "").Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var wizard = Context.Character;
        if (words.Length == 0) {
            InformSenderClient(Usage);

            return;
        }

        var house = wizard is null || wizard.AttachedLotGid == 0 ? null : HouseRegistry.Get(wizard.AttachedLotGid);
        if (house is null) {
            InformSenderClient("Go into a house first: .sigil sets the sigil of a gauntlet placed in it.");

            return;
        }

        // The owner of the house, or quality assurance (no infraction for a player who is not either: it is a house tool).
        if (house.OwnerCharId != wizard.CharId && (Context.Account is null || Context.Account.AuthLevel < AuthLevel.QualityAssurance)) {
            InformSenderClient("Only the owner of this house can set its gauntlets' sigils.");

            return;
        }

        var spot = wizard.Location;
        var gauntlet = HouseGauntlets.NearestIn(house, spot, Reach);
        if (gauntlet is null) {
            InformSenderClient($"No gauntlet is placed within {Reach:0} of you. Stand near the one to set.");

            return;
        }

        var id = gauntlet.TemplateId;
        var name = HouseGauntlets.NameOf(id);
        var now = HouseGauntlets.PlaceOf(gauntlet);
        string said;
        switch (words[0]) {
            case "info":
                InformSenderClient(Describe(gauntlet, name));

                return;

            case "here": {
                var dx = spot.X - gauntlet.Position.X;
                var dy = spot.Y - gauntlet.Position.Y;
                var yaw = gauntlet.Yaw;
                // Along the gauntlet's front (-cos yaw, sin yaw) and its side (sin yaw, cos yaw).
                var along = (-dx * MathF.Cos(yaw)) + (dy * MathF.Sin(yaw));
                var side = (dx * MathF.Sin(yaw)) + (dy * MathF.Cos(yaw));
                float faceX, faceY;
                if (words.Length > 1 && words[1] is "face" or "facing") {
                    // The way the caller faces (a guess at the game's forward for a character's yaw).
                    var look = wizard.Orientation.Z;
                    (faceX, faceY) = (-MathF.Sin(look), -MathF.Cos(look));
                } else {
                    var length = MathF.Sqrt((dx * dx) + (dy * dy));
                    if (length < 20f) {
                        InformSenderClient("You are standing on the gauntlet. Step out to where the sigil's centre should be, or use .sigil here face.");

                        return;
                    }

                    (faceX, faceY) = (dx / length, dy / length);
                }

                // A pad of yaw p faces (-cos p, sin p).
                var padYaw = MathF.Atan2(faceY, -faceX);
                var turn = Degrees(padYaw - yaw);
                var height = spot.Z - gauntlet.Position.Z;
                GauntletSigils.Change(id, name, s => {
                    s.OffsetX = MathF.Round(along, 1);
                    s.OffsetY = MathF.Round(side, 1);
                    s.Turn = MathF.Round(turn, 1);
                    s.Height = MathF.Round(height, 1);
                });
                said = $"The sigil's centre is now where you stand ({along:0} in front, {side:0} to the side, {height:0} up), its arc turned {turn:0} degrees";
                break;
            }

            case "size" or "scale" when Number(words, out var size) && size >= 0.1f && size <= 5f:
                GauntletSigils.Change(id, name, s => s.Scale = size);
                said = $"The sigil's size is now {size:0.##}";
                break;

            case "up" or "down" when Number(words, out var by):
                var raised = now.Height + (words[0] == "up" ? by : -by);
                GauntletSigils.Change(id, name, s => s.Height = MathF.Round(raised, 1));
                said = $"The sigil now stands {raised:0} up";
                break;

            case "turn" or "rotate" when Number(words, out var degrees):
                var turned = Degrees((now.Turn + degrees) * MathF.PI / 180f);
                GauntletSigils.Change(id, name, s => s.Turn = MathF.Round(turned, 1));
                said = $"The sigil's arc is now turned {turned:0} degrees from the gauntlet"
                    + (GauntletSigils.Of(id) is { OffsetX: not null } ? " (it turns where it stands)" : " (the whole sigil swings round the gauntlet)");
                break;

            case "reset":
                said = GauntletSigils.Remove(id) ? "The sigil is back to its defaults" : "The sigil already had its defaults";
                break;

            default:
                InformSenderClient(words[0] is "size" or "scale" or "up" or "down" or "turn" or "rotate"
                    ? $"Give a number, such as .sigil {words[0]} {(words[0] is "size" or "scale" ? "0.8" : words[0] is "turn" or "rotate" ? "90" : "10")}"
                      + (words[0] is "size" or "scale" ? " (from 0.1 to 5)." : ".")
                    : Usage);

                return;
        }

        HouseGauntlets.Sync(Context.ZoneActor, house, "a sigil was set by hand");
        InformSenderClient($"{said} for {name} (template {id}), and for every copy of it.");
    }

    private static string Describe(HouseGauntlet gauntlet, string name) {
        var place = HouseGauntlets.PlaceOf(gauntlet);
        var dx = place.X - gauntlet.Position.X;
        var dy = place.Y - gauntlet.Position.Y;
        var yaw = gauntlet.Yaw;
        var along = (-dx * MathF.Cos(yaw)) + (dy * MathF.Sin(yaw));
        var side = (dx * MathF.Sin(yaw)) + (dy * MathF.Cos(yaw));

        return $"{name} (template {gauntlet.TemplateId}): sigil centre {MathF.Sqrt((dx * dx) + (dy * dy)):0} away ({along:0} in front, "
            + $"{side:0} to the side), {place.Height:0} up, arc turned {place.Turn:0} degrees, size {place.Scale:0.##}; "
            + (place.ByHand ? $"set by hand ({GauntletSigils.FILE})." : "the defaults.");
    }

    private static bool Number(string[] words, out float value) {
        value = 0;

        return words.Length > 1 && float.TryParse(words[1], NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);
    }

    // An angle in radians as degrees from -180 to 180.
    private static float Degrees(float radians) {
        var degrees = radians * 180f / MathF.PI % 360f;

        return degrees > 180f ? degrees - 360f : degrees <= -180f ? degrees + 360f : degrees;
    }

}
