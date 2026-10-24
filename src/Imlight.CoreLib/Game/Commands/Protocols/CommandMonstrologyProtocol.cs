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
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.Auth;
using Imlight.CoreLib.Game.MonsterMagic;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.tome` opens the Monstrology tome and says the wizard's Monstrology level, XP and monsters. For testing (quality
/// assurance): `.tome level 20` sets the Monstrology level, `.tome animus Dishonored Samoorai 30` (a name or template id)
/// adds Animus to the tome.
/// </summary>
internal class CommandMonstrologyProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("tome")]
    [Alias("monstrology")]
    private void TomeCommand([Remainder] string text = "") {
        var wizard = Context.Character;
        if (wizard is null) {
            return;
        }

        var words = (text ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) {
            if (Monstrology.Open(wizard)) {
                WizardCollection.UpdateCharacterMonsterTome(wizard);
            }

            Context.SessionActor.Tell(new WIZARD2_53_PROTOCOL.MSG_MONSTERMAGICUSETOME(), ActorRefs.NoSender);
            InformSenderClient($"Monstrology level {Monstrology.LevelOf(wizard)}, XP {wizard.GameStats.m_monsterMagicXP}, "
                + $"{wizard.MonsterTome?.Count(m => m.Value > 0) ?? 0} monsters with Animus.");

            return;
        }

        if (!AuthorityRequester.RequestAuthority(AuthLevel.QualityAssurance, Context.Account, "Command tome " + words[0])) {
            InformSenderClient("You do not have permission to use this command.");

            return;
        }

        if (words[0].Equals("level", StringComparison.OrdinalIgnoreCase) && words.Length > 1 && int.TryParse(words[1], out var level)) {
            level = Monstrology.SetLevel(wizard, level);
            SendLevel(wizard);
            InformSenderClient($"Your Monstrology level is now {level}.");

            return;
        }

        if (words[0].Equals("animus", StringComparison.OrdinalIgnoreCase) && words.Length > 1) {
            var count = 10;
            var nameWords = words[1..];
            if (nameWords.Length > 1 && int.TryParse(nameWords[^1], out var asked)) {
                count = Math.Clamp(asked, 1, 999);
                nameWords = nameWords[..^1];
            }

            var name = string.Join(' ', nameWords);
            var templateId = uint.TryParse(name, out var id) ? id
                : (uint) (IdCatalog.Search(IdCatalogKind.Creature, name, 20)
                    .FirstOrDefault(e => Monstrology.MonsterOf((uint) e.Id) is not null)?.Id ?? 0);
            if (Monstrology.MonsterOf(templateId) is not { } monster) {
                InformSenderClient($"No monster with a Monstrology page matches \"{name}\".");

                return;
            }

            var before = Monstrology.Keep(wizard, new Dictionary<uint, int> { [monster.TomeId] = count });
            SendLevel(wizard);
            if (Monstrology.LevelOf(wizard) > before) {
                Context.SessionActor.Tell(new WIZARD2_53_PROTOCOL.MSG_MONSTERMAGICLEVELUP {
                    GlobalID = wizard.GameObjectID,
                    NewLevel = Monstrology.LevelOf(wizard),
                }, ActorRefs.NoSender);
            }

            Context.SessionActor.Tell(new WIZARD2_53_PROTOCOL.MSG_REQUESTMONSTERTOME {
                GlobalID = wizard.GameObjectID,
                MonsterData = Monstrology.Write(wizard.MonsterTome),
            }, ActorRefs.NoSender);
            InformSenderClient($"Added {count} {monster.Name} Animus (rank {monster.Rank}, template {monster.TomeId}). "
                + $"You have {wizard.MonsterTome.GetValueOrDefault(monster.TomeId)}.");

            return;
        }

        InformSenderClient("Use .tome, .tome level 20 or .tome animus <monster> 30.");
    }

    private void SendLevel(Wizard wizard)
        => Context.SessionActor.Tell(new WIZARD2_53_PROTOCOL.MSG_UPDATEMONSTERMAGICXP {
            GlobalID = wizard.GameObjectID,
            XP = wizard.GameStats.m_monsterMagicXP,
            Level = Monstrology.LevelOf(wizard),
        }, ActorRefs.NoSender);

}
