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

using System;
using System.Linq;
using Imlight.Common;
using Imlight.CoreLib.Game.Groups;
using Imlight.CoreLib.Game.Ultimus;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// True Form, the helper bot anyone can call (Niduus, 2026-11-19: "EVERYONE"). ".trueform" calls him to you; ".trueform
/// attack" (defend, support, auto, evil) sets how he plays; ".trueform use Dragonblade" keeps a spell in his hand;
/// ".trueform leave" sends him back to his spot; ".trueform where" says where he is and who he is with. ".trueform home"
/// (QA) keeps the spot you stand on as his spot.
/// </summary>
internal class CommandTrueForm : TrueFormCommandBase {

    internal override string Group { get; set; } = "trueform";

    [Command("call")]
    [Alias("join", "come", "help")]
    private void CallCommand() => CallHim();

    [Command("leave")]
    [Alias("bye", "dismiss")]
    private void LeaveCommand() {
        var me = Context.Character.CharId;
        var him = TrueForm.CharId;
        if (him == 0 || !GroupRegistry.AreGrouped(me, him)) {
            InformSenderClient("True Form isn't in your group.");
            return;
        }

        TrueForm.AskLeave();
        InformSenderClient("True Form says goodbye and heads back to his spot in The Commons.");
    }

    [Command("mode")]
    private void ModeCommand([Remainder] string words = "") => SetMode(words);

    [Command("use")]
    [Alias("spell")]
    private void UseCommand([Remainder] string spell = "") {
        var me = Context.Character.CharId;
        var him = TrueForm.CharId;
        if (him == 0 || !GroupRegistry.AreGrouped(me, him)) {
            InformSenderClient("True Form takes spell asks from his group. Call him first with .trueform");
            return;
        }

        if (string.IsNullOrWhiteSpace(spell)) {
            if (TrueForm.Requested.Count == 0) {
                InformSenderClient("Say which spell, like .trueform use Dragonblade (.trueform use clear forgets them).");
            }
            else {
                InformSenderClient("True Form keeps these in his hand: " +
                                   string.Join(", ", TrueForm.Requested.Select(id => TrueFormPool.Of(id)?.Shown ?? id.ToString())) + ".");
            }

            return;
        }

        if (spell.Trim().Equals("clear", StringComparison.OrdinalIgnoreCase)) {
            TrueForm.ClearRequested();
            InformSenderClient("True Form picks all his cards himself again.");
            return;
        }

        if (!TrueFormPool.Ready) {
            InformSenderClient("True Form is still reading his spells. Ask again in a moment.");
            return;
        }

        if (TrueFormPool.Find(spell, TrueForm.SpellLevel(), TrueForm.DevSpellsNow) is not { } found) {
            InformSenderClient($"True Form has no spell called \"{spell.Trim()}\" at level {TrueForm.SpellLevel()}.");
            return;
        }

        TrueForm.Request(found.Id);
        InformSenderClient($"True Form keeps {found.Shown} in his hand from the next round.");
    }

    [Command("where")]
    [Alias("status", "who")]
    private void WhereCommand() {
        var members = UltimusDirector.MembersOf(UltimusSeat.TrueForm);
        if (members.Count == 0 || members[0].State != "playing") {
            InformSenderClient("True Form is not in the game right now. He is turned on in Imlight.ini ([TrueForm] Enabled).");
            return;
        }

        var with = TrueForm.With is { Length: > 0 } w ? $"helping {w}" : "free: anyone can call him with .trueform";
        InformSenderClient($"True Form is in {ZoneNames.Shown(members[0].Zone)}, {with}. He plays {TrueForm.Words(TrueForm.Now)}; " +
                           $"his spells match level {TrueForm.SpellLevel()}" + (TrueForm.DevSpellsNow ? ", dev spells on" : "") + ".");
    }

    [Command("home")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void HomeCommand() {
        var me = Context.Character;
        if (string.IsNullOrEmpty(me?.Zone)) {
            return;
        }

        var spot = new TrueForm.Spot(me.Zone, me.Location.X, me.Location.Y, me.Location.Z, me.Orientation.Z);
        TrueForm.SetHome(spot);
        InformSenderClient($"True Form's spot is now here, in {ZoneNames.Shown(me.Zone)}. He walks to it whenever nobody has him.");
    }

}

/// <summary>".trueform" on its own, and ".trueform attack" (a mode word right after it): a command with no group.</summary>
internal class CommandTrueFormCall : TrueFormCommandBase {

    internal override string Group { get; set; } = "";

    [Command("trueform")]
    [Alias("tf")]
    private void TrueFormCommand([Remainder] string words = "") {
        if (string.IsNullOrWhiteSpace(words)) {
            CallHim();
            return;
        }

        SetMode(words);
    }

}

/// <summary>What both True Form commands share (abstract: the bots side does not register it as a command).</summary>
internal abstract class TrueFormCommandBase : CommandProtocol {

    protected void CallHim() {
        var me = Context.Character;
        if (TrueForm.CharId == 0) {
            InformSenderClient("True Form is not in the game right now.");
            return;
        }

        if (GroupRegistry.AreGrouped(me.CharId, TrueForm.CharId)) {
            InformSenderClient($"True Form is already with you. He plays {TrueForm.Words(TrueForm.Now)}.");
            return;
        }

        TrueForm.Call(me.CharId, me.PlayerNameBehavior?.GetWizardName() ?? "");
        InformSenderClient("You called True Form. Watch for his group invite (or his answer if he's busy).");
    }

    protected void SetMode(string words) {
        if (TrueForm.ModeFrom(words) is not { } mode) {
            InformSenderClient(".trueform calls him. Then: .trueform attack, defend, support, auto or evil; .trueform use <spell>; .trueform leave; .trueform where.");
            return;
        }

        if (TrueForm.CharId == 0 || !GroupRegistry.AreGrouped(Context.Character.CharId, TrueForm.CharId)) {
            InformSenderClient("True Form takes orders from his group. Call him first with .trueform");
            return;
        }

        TrueForm.SetMode(mode);
        InformSenderClient($"True Form now plays {TrueForm.Words(mode)}.");
    }

}
