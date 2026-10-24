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
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Game.MonsterMagic;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

/// <summary>
/// Monstrology for one session (Monstrology.cs): the tome window's data, its Create buttons, the Animus kept after a
/// won battle, and the Monstrology level and XP the game shows.
/// </summary>
internal class MonstrologyService(SessionActor sessionActor) : MessageService(sessionActor) {

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new MonstrologyService(parentActor));

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE))]
    private void ReceiveAttachComplete(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE message) {
        try {
            var wizard = GetActiveWizard();
            if (Monstrology.Open(wizard)) {
                WizardCollection.UpdateCharacterMonsterTome(wizard);
                Logger.Information("Monstrology: opened for {0} at level {1}.", Logger.Args(wizard.CharId, Monstrology.LevelOf(wizard)));
            }

            if (Monstrology.LevelOf(wizard) > 0) {
                SendXp(wizard);
            }
        }
        catch (Exception e) {
            Logger.Error("Monstrology: login update failed: {0}", Logger.Args(e));
        }
    }

    /// <summary>
    /// The tome window asks for the wizard's monsters; the answer's MonsterData becomes MonsterMagicBehavior.m_monsterData.
    /// </summary>
    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_REQUESTMONSTERTOME))]
    private void ReceiveRequestMonsterTome(WIZARD2_53_PROTOCOL.MSG_REQUESTMONSTERTOME message) {
        try {
            var wizard = GetActiveWizard();
            if (Monstrology.Open(wizard)) {
                WizardCollection.UpdateCharacterMonsterTome(wizard);
            }

            SendTome(wizard);
            Logger.Information("Monstrology: sent the tome of {0} ({1} monsters).", Logger.Args(wizard.CharId, wizard.MonsterTome?.Count ?? 0));
        }
        catch (Exception e) {
            Logger.Error("Monstrology: MSG_REQUESTMONSTERTOME failed: {0}", Logger.Args(e));
        }
    }

    /// <summary>
    /// A Create button of the tome: 1 a Summon Monster card, 2 a house guest, 3 an Expel Monster card.
    /// </summary>
    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_MONSTERMAGICREQUESTCREATE))]
    private void ReceiveRequestCreate(WIZARD2_53_PROTOCOL.MSG_MONSTERMAGICREQUESTCREATE message) {
        var result = Monstrology.FAILED;
        string why;
        try {
            why = Create(GetActiveWizard(), message.RequestType, message.MobTemplate, out var made);
            if (why is null) {
                result = message.RequestType;
                Logger.Information("Monstrology: {0} made {1} from monster {2}.",
                    Logger.Args(GetActiveWizard().CharId, made, message.MobTemplate));
            }
        }
        catch (Exception e) {
            why = "something went wrong";
            Logger.Error("Monstrology: MSG_MONSTERMAGICREQUESTCREATE failed: {0}", Logger.Args(e));
        }

        if (why is not null) {
            Logger.Information("Monstrology: request {0} for monster {1} failed: {2}.", Logger.Args(message.RequestType, message.MobTemplate, why));
            InformGameClient($"Monstrology: {why}.");
        }

        SendToSocket(new WIZARD2_53_PROTOCOL.MSG_MONSTERMAGICREQUESTCREATE {
            GlobalID = message.GlobalID,
            RequestType = result,
            MobTemplate = message.MobTemplate,
        });
        if (result != Monstrology.FAILED) {
            SendTome(GetActiveWizard());
        }
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_COMBATWIN))]
    private void ReceiveCombatWin(COMBAT_106_PROTOCOL.MSG_COMBATWIN message) {
        if (message.Animus is not { Count: > 0 } animus) {
            return;
        }

        try {
            var wizard = GetActiveWizard();
            var before = Monstrology.Keep(wizard, animus);
            var total = animus.Values.Sum();
            InformGameClient(string.Join("\n", animus.Select(a => a.Value == 1
                ? $"Kept 1 {Monstrology.NameOf(a.Key)} Animus"
                : $"Kept {a.Value} {Monstrology.NameOf(a.Key)} Animus")));
            Logger.Information("Monstrology: {0} kept {1} Animus ({2}); XP {3}, level {4}.",
                Logger.Args(wizard.CharId, total, string.Join(", ", animus.Select(a => $"{a.Key} x{a.Value}")),
                    wizard.GameStats.m_monsterMagicXP, Monstrology.LevelOf(wizard)));

            SendXp(wizard);
            if (Monstrology.LevelOf(wizard) > before) {
                SendToSocket(new WIZARD2_53_PROTOCOL.MSG_MONSTERMAGICLEVELUP {
                    GlobalID = wizard.GameObjectID,
                    NewLevel = Monstrology.LevelOf(wizard),
                });
            }

            SendTome(wizard);
        }
        catch (Exception e) {
            Logger.Error("Monstrology: keeping the battle's Animus failed: {0}", Logger.Args(e));
        }
    }

    // Makes what a Create button asks for; returns null when it was made, else why not.
    private string Create(Wizard wizard, int requestType, uint tomeId, out string made) {
        made = null;
        if (!Monstrology.Enabled) {
            return "Monstrology is switched off on this server";
        }

        if (requestType is not (Monstrology.SUMMON_CARD or Monstrology.HOUSE_GUEST or Monstrology.EXPEL_CARD)) {
            return $"unknown request {requestType}";
        }

        var monster = Monstrology.MonsterOf(tomeId);
        if (monster is null) {
            return "that monster has no Monstrology page";
        }

        if (monster.Rank > Monstrology.LevelOf(wizard)) {
            return $"{monster.Name} is rank {monster.Rank}, above your Monstrology level";
        }

        var (animus, gold) = Monstrology.Cost(monster, requestType);
        if ((wizard.MonsterTome?.GetValueOrDefault(tomeId) ?? 0) < animus) {
            return $"not enough {monster.Name} Animus ({animus} needed)";
        }

        if (wizard.GameStats.m_currentGold < gold) {
            return $"not enough gold ({gold} needed)";
        }

        if (requestType == Monstrology.HOUSE_GUEST) {
            if (monster.Magic.m_isBoss || monster.Magic.m_houseGuestTemplateID == 0) {
                return $"{monster.Name} cannot be a house guest";
            }

            if (!wizard.AddItemToInventory(monster.Magic.m_houseGuestTemplateID, out var item)) {
                return "no room in the backpack";
            }

            if (!ItemSync.SendAdded(SessionActor.ActorRef, wizard, item)) {
                wizard.RemoveItemFromInventory(item.m_globalID);
                WizardItemCollection.RemoveItem(wizard.CharId, item.m_globalID);

                return "the house guest could not be sent to the game";
            }

            SendToSocket(new WIZARD2_53_PROTOCOL.MSG_ITEMACQUISITION {
                ItemGlobalID = item.m_globalID,
                ItemTemplateID = monster.Magic.m_houseGuestTemplateID,
                ItemLocation = 1,
            });
            made = $"house guest {monster.Magic.m_houseGuestTemplateID}";
        }
        else {
            var name = Monstrology.CardName(monster, requestType);
            var spell = name is null ? null : SpellFactory.GetSpell(name);
            if (spell is null || CoreObjectFactory.GetCoreTemplate(spell.m_templateID) is not SpellTemplate template) {
                return $"the game has no {(requestType == Monstrology.SUMMON_CARD ? "Summon" : "Expel")} card for {monster.Name}";
            }

            LootGranter.GrantTreasureCards(SessionActor.ActorRef, wizard, template, spell.m_templateID, 1);
            made = $"card {name} ({spell.m_templateID})";
        }

        Monstrology.Spend(wizard, tomeId, animus);
        if (gold > 0) {
            wizard.RemoveGold(gold);
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD {
                Gold = wizard.GameStats.m_currentGold,
                MaxGold = wizard.GameStats.m_baseGoldPouch,
            });
        }

        return null;
    }

    private void SendTome(Wizard wizard)
        => SendToSocket(new WIZARD2_53_PROTOCOL.MSG_REQUESTMONSTERTOME {
            GlobalID = wizard.GameObjectID,
            MonsterData = Monstrology.Write(wizard.MonsterTome),
        });

    private void SendXp(Wizard wizard)
        => SendToSocket(new WIZARD2_53_PROTOCOL.MSG_UPDATEMONSTERMAGICXP {
            GlobalID = wizard.GameObjectID,
            XP = wizard.GameStats.m_monsterMagicXP,
            Level = Monstrology.LevelOf(wizard),
        });

}
