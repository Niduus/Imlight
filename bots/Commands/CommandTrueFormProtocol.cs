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
using Imlight.CoreLib.Auth;
using Imlight.CoreLib.Game.Groups;
using Imlight.CoreLib.Game.Ultimus;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// True Form, the helper bot anyone can call (Niduus, 2026-11-19: "EVERYONE"). ".trueform" (or ".tf") calls him to you;
/// ".trueform attack" (defend, support, auto, evil) sets how he plays; ".trueform use Dragonblade" keeps a spell in his
/// hand; ".trueform leave" sends him back to his spot; ".trueform where" says where he is and who he is with;
/// ".trueform home" (QA) keeps the spot you stand on as his spot.
/// One command with no group that reads its own words: a "trueform" group answered ".trueform" alone with the command
/// list instead of calling him (Niduus, 2026-11-19: "not responding well with commands").
/// </summary>
internal class CommandTrueForm : CommandProtocol {

    internal override string Group { get; set; } = "";

    private const string USAGE = ".trueform calls True Form. Then: .trueform attack, defend, support, auto or evil; " +
                                 ".trueform use <spell> (.trueform use clear); .trueform leave; .trueform where.";

    [Command("trueform")]
    [Alias("tf", "truef")]
    private void TrueFormCommand([Remainder] string words = "") {
        var text = (words ?? "").Trim();
        var space = text.IndexOf(' ');
        var first = (space < 0 ? text : text[..space]).ToLowerInvariant();
        var rest = space < 0 ? "" : text[(space + 1)..].Trim();
        switch (first) {
            case "" or "call" or "join" or "come" or "help" or "here" or "invite":
                Call();
                return;
            case "leave" or "bye" or "dismiss":
                Leave();
                return;
            case "home":
                Home();
                return;
            case "use" or "spell" or "keep":
                Use(rest);
                return;
            case "where" or "status" or "who" or "info":
                Where();
                return;
            case "mode":
                SetMode(rest);
                return;
            case "?" or "commands" or "usage":
                InformSenderClient(USAGE);
                return;
            default:
                SetMode(text);
                return;
        }
    }

    private bool WithHim(out ulong him) {
        him = TrueForm.CharId;
        return him != 0 && GroupRegistry.AreGrouped(Context.Character.CharId, him);
    }

    private void Call() {
        var me = Context.Character;
        if (TrueForm.CharId == 0) {
            InformSenderClient("True Form is not in the game right now.");
            return;
        }

        if (WithHim(out _)) {
            InformSenderClient($"True Form is already with you. He plays {TrueForm.Words(TrueForm.Now)}. " + USAGE);
            return;
        }

        TrueForm.Call(me.CharId, me.PlayerNameBehavior?.GetWizardName() ?? "");
        InformSenderClient(TrueForm.With is { Length: > 0 } busy
            ? $"True Form is helping {busy} right now; he'll whisper you."
            : "You called True Form. Accept his group invite when it comes (in a battle, it comes when the battle ends).");
    }

    private void Leave() {
        if (!WithHim(out _)) {
            InformSenderClient("True Form isn't in your group.");
            return;
        }

        TrueForm.AskLeave();
        InformSenderClient("True Form says goodbye and heads back to his spot in The Commons.");
    }

    private void SetMode(string words) {
        if (TrueForm.ModeFrom(words) is not { } mode) {
            InformSenderClient(USAGE);
            return;
        }

        if (!WithHim(out _)) {
            InformSenderClient("True Form takes orders from his group. Call him first with .trueform");
            return;
        }

        TrueForm.SetMode(mode);
        InformSenderClient($"True Form now plays {TrueForm.Words(mode)} (from his next hand).");
    }

    private void Use(string spell) {
        // "please", "for me", "in your hand" are not part of a spell's name.
        spell = System.Text.RegularExpressions.Regex.Replace(TrueForm.Order(spell), @"\b(in (your|ur) hand|in hand|the spell|spell|card)\b", " ").Trim();
        if (!WithHim(out _)) {
            InformSenderClient("True Form takes spell asks from his group. Call him first with .trueform");
            return;
        }

        if (string.IsNullOrWhiteSpace(spell)) {
            InformSenderClient(TrueForm.Requested.Count == 0
                ? "Say which spell, like .trueform use Dragonblade (.trueform use clear forgets them)."
                : "True Form keeps these in his hand: " + string.Join(", ", TrueForm.Requested.Select(id => TrueFormPool.Of(id)?.Shown ?? id.ToString())) + ".");
            return;
        }

        if (spell.Equals("clear", StringComparison.OrdinalIgnoreCase)) {
            TrueForm.ClearRequested();
            InformSenderClient("True Form picks all his cards himself again.");
            return;
        }

        if (!TrueFormPool.Ready) {
            InformSenderClient("True Form is still reading his spells. Ask again in a moment.");
            return;
        }

        if (TrueFormPool.Find(spell, TrueForm.SpellLevel(), TrueForm.DevSpellsNow) is not { } found) {
            InformSenderClient($"True Form has no spell called \"{spell}\" at level {TrueForm.SpellLevel()}.");
            return;
        }

        TrueForm.Request(found.Id);
        InformSenderClient($"True Form keeps {found.Shown} in his hand from his next hand.");
    }

    private void Where() {
        var members = UltimusDirector.MembersOf(UltimusSeat.TrueForm);
        if (members.Count == 0 || members[0].State != "playing") {
            InformSenderClient("True Form is not in the game right now. He is turned on in Imlight.ini ([TrueForm] Enabled).");
            return;
        }

        var with = TrueForm.With is { Length: > 0 } w ? $"helping {w}" : "free: anyone can call him with .trueform";
        InformSenderClient($"True Form is in {ZoneNames.Shown(members[0].Zone)}, {with}. He plays {TrueForm.Words(TrueForm.Now)}; " +
                           $"his spells match level {TrueForm.SpellLevel()}" + (TrueForm.DevSpellsNow ? ", dev spells on" : "") + ".");
    }

    private void Home() {
        if (!AuthorityRequester.RequestAuthority(AuthLevel.QualityAssurance, Context.Account, "Command trueform home")) {
            InformSenderClient("Only QA can set True Form's spot.");
            return;
        }

        var me = Context.Character;
        if (string.IsNullOrEmpty(me?.Zone)) {
            return;
        }

        TrueForm.SetHome(new TrueForm.Spot(me.Zone, me.Location.X, me.Location.Y, me.Location.Z, me.Orientation.Z));
        InformSenderClient($"True Form's spot is now here, in {ZoneNames.Shown(me.Zone)}. He walks to it whenever nobody has him.");
    }

}
