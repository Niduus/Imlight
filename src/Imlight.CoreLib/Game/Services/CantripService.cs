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
 * CANTRIP SERVICE
 * ========================================================================
 * 
 * PURPOSE:
 * Manages player cantrip (minor magic) interactions, including spell casting, 
 * energy management, and special effect processing.
 * 
 * USAGE EXAMPLE:
 * Internal service handling various cantrip-related messages and actions 
 * within the game server's session management system.
 * 
 * NOTE:
 * - Handles multiple types of cantrip effects (emote, teleport, invisibility)
 * - Implements energy cost and cooldown management
 * 
 * TODO:
 * - Review and complete unhandled ritual cantrip scenarios
 * - Investigate string hash computations
 * - Refine energy shop and energy refill logic
 * 
 * Created by: Jeff
 * Version: KALI 1.0
 * Last Updated: 3/18/2025
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.Cryptography;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Cantrips;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Character;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal class CantripService(SessionActor sessionActor) : MessageService(sessionActor) {

    private const int CANTRIP_CAST_TIME = 3;

    private readonly TimeSpan _cantripCastTimeSpan = TimeSpan.FromSeconds(CANTRIP_CAST_TIME);
    private readonly CoreObjectSerializer _effectSerializer = new(
        behaviors: SerializerFlags.None
    );

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new CantripService(parentActor));

    // [Cantrips] UnlockAll (live): every wizard has the Cantrips tab at the top cantrip level and knows every cantrip,
    // without the game's unlock quest (Niduus, 2026-10-27: "We might need cantrips as well. For now, unlock by default").
    internal static bool UnlockAll => ConfigurationManager.GetValue("Cantrips.UnlockAll", true);

    // Sneak (CSE_NoAggro): monsters do not start a battle with the wizard until this time.
    private static readonly ConcurrentDictionary<ulong, DateTime> s_sneakUntil = new();
    private const int SNEAK_SECONDS = 30;

    /// <summary>True while the wizard's Sneak cantrip keeps monsters from starting a battle.</summary>
    internal static bool IsSneaking(ulong charId)
        => s_sneakUntil.TryGetValue(charId, out var until) && DateTime.UtcNow < until;

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_PRELOGIN))]
    private void ReceivePreLogin(ZONE_102_PROTOCOL.MSG_PRELOGIN message) {
        try {
            UnlockCantrips();
        }
        catch (Exception e) {
            Logger.Error("Cantrips: could not unlock the cantrips: {0}", Logger.Args(e));
        }
    }

    /// <summary>
    /// With [Cantrips] UnlockAll on, raises the wizard to the top cantrip level and teaches every cantrip they do not
    /// know yet. Runs on every zone entry, so turning the setting on reaches wizards already playing at their next
    /// zone. Turning it off takes nothing away.
    /// </summary>
    private void UnlockCantrips() {
        if (!UnlockAll) {
            return;
        }

        var wizard = GetActiveWizard();
        if (wizard?.GameStats is null || wizard.SpellbookBehavior is null) {
            return;
        }

        _ = CantripFactory.Instance;
        var maxLevel = (byte) Math.Clamp(CantripFactory.GetMaxCantripLevel(), 1, byte.MaxValue);
        if (wizard.GameStats.m_cantripLevel < maxLevel) {
            wizard.UpdateCantripLevel(maxLevel);
            SendToSocket(new CANTRIPSMESSAGES_57_PROTOCOL.MSG_UPDATECANTRIPXP {
                GlobalID = wizard.GameObjectID,
                XP = wizard.GameStats.m_cantripXP,
                Level = maxLevel
            });
            Logger.Information("Cantrips: {0} is cantrip level {1} now.", Logger.Args(wizard.CharId, maxLevel));
        }

        var taught = new List<string>();
        foreach (var (id, template) in CantripFactory.PlayerCantrips()) {
            if (wizard.SpellbookBehavior.HasSpell(id)) {
                continue;
            }

            var spell = SpellFactory.GetSpell(id);
            if (spell is null || !wizard.LearnSpell(spell)) {
                continue;
            }

            SendToSocket(new WIZARD_12_PROTOCOL.MSG_ADDSPELLTOBOOK { SpellID = (int) id });
            taught.Add(template.m_name.ToString());
        }

        if (taught.Count > 0) {
            Logger.Information("Cantrips: taught {0} {1} cantrips: {2}.",
                Logger.Args(wizard.CharId, taught.Count, string.Join(", ", taught)));
        }
    }

    [MessageHandler(typeof(CANTRIPSMESSAGES_57_PROTOCOL.MSG_CANTRIPSSPELLCAST))]
    private void ReceiveCantripSpellCast(CANTRIPSMESSAGES_57_PROTOCOL.MSG_CANTRIPSSPELLCAST message) {
        var wizard = GetActiveWizard();
        CantripsSpellTemplate cantrip = CantripFactory.CreateCantripTemplateFromId(message.SpellTemplateID);
        if (wizard is null || cantrip is null) {
            return;
        }

        // Cantrips the server cannot do yet (healing, mana and fishing luck radiances, Calescent Tracker, Second Mark
        // and Second Recall) cost no energy and say so, instead of taking energy and doing nothing.
        if (cantrip.m_cantripsSpellEffect is CantripsSpellEffect.CSE_Beneficial or CantripsSpellEffect.CSE_HotCold
            or CantripsSpellEffect.CSE_Mark or CantripsSpellEffect.CSE_Recall) {
            SendToSocket(new CANTRIPSMESSAGES_57_PROTOCOL.MSG_CANTRIPSRESPONSE { EnergyUsed = 0, CooldownSeconds = 0, OutOfEnergy = 0 });
            InformGameClient("This cantrip does not work on this server yet.");

            return;
        }

        // Rituals require a target to be selected first, so don't use energy here
        if (cantrip.m_cantripsSpellEffect != CantripsSpellEffect.CSE_Ritual) {
            bool hasEnergy = CastCantrip(message.SpellTemplateID);

            if (!hasEnergy) {
                return;
            }
        }

        var castEffect = new CANTRIPSMESSAGES_57_PROTOCOL.MSG_CASTEFFECT {
            GameObjectID = wizard.GameObject.m_globalID,
            SpellTemplateID = (int) message.SpellTemplateID
        };

        switch (cantrip.m_cantripsSpellEffect) {
            case CantripsSpellEffect.CSE_Emote:
                CastEmoteCantrip(cantrip, ref castEffect);
                break;
            case CantripsSpellEffect.CSE_PlayEffect:
            case CantripsSpellEffect.CSE_PlaySchoolEffect:
                CastPlayEffectCantrip(cantrip, ref castEffect);
                break;
            case CantripsSpellEffect.CSE_NoAggro:
                CastSneakCantrip(wizard);
                break;
            case CantripsSpellEffect.CSE_Teleport:
                CastTeleportCantrip(cantrip);
                break;
            case CantripsSpellEffect.CSE_Ritual:
                CastRitualCantrip((int) message.SpellTemplateID);
                return; // User needs to select a target first
            case CantripsSpellEffect.CSE_Invisibility:
                CastInvisCantrip(cantrip);
                break;
        }

        ZoneBroadcast(castEffect, isSelfless: false);
    }

    [MessageHandler(typeof(CANTRIPSMESSAGES_57_PROTOCOL.MSG_CASTRITUAL))]
    private void ReceiveCastRitual(CANTRIPSMESSAGES_57_PROTOCOL.MSG_CASTRITUAL message) {
        bool hasEnergy = CastCantrip((uint) message.SpellTemplateID);

        if (!hasEnergy) {
            return;
        }

        SendToSocket(message);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONETRANSFER))]
    private void ReceiveZoneTransferRequest(ZONE_102_PROTOCOL.MSG_ZONETRANSFER message) {
        TellOtherServices(message);
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_ADDEFFECT))]
    private void ReceiveAddEffect(GAME_5_PROTOCOL.MSG_ADDEFFECT message) {
        SendToSocket(message);
    }

    [MessageHandler(typeof(CANTRIPSMESSAGES_57_PROTOCOL.MSG_CANCELINVISIBLITY))]
    private void ReceiveCancelInvisibility(CANTRIPSMESSAGES_57_PROTOCOL.MSG_CANCELINVISIBLITY message) {
        var wizard = GetActiveWizard();
        var effect = wizard.GameEffects.Find(e => e.m_effectNameID == StringHash.Compute("CantripsMajorInvisibilityEffect"));
        if (effect == null) {
            return;
        }

        wizard.GameEffects.Remove(effect);
        var removeEffect = new GAME_5_PROTOCOL.MSG_REMOVEEFFECT {
            GameObjectID = wizard.GameObject.m_globalID,
            EffectNameID = effect.m_effectNameID,
            InternalID = effect.m_internalID
        };

        SendToSocket(removeEffect);
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_ENERGYSHOPOPEN))]
    private void ReceiveEnergyShopOpen(WIZARD_12_PROTOCOL.MSG_ENERGYSHOPOPEN message) {
        // This would usually open a menu to spend crowns to refill energy
        // We aren't going to do that, but it will refill your energy if you are QA or above
        var wizard = GetActiveWizard();
        MaybeBackflip(wizard);
        if (wizard.Account.AuthLevel >= AuthLevel.QualityAssurance) {
            // Fill to the full energy with gear and pets (Niduus: Refill gave the level's base energy, not the max). The game
            // adds the gear itself, so its max is sent without it, as the energy ticks do.
            var fullEnergy = wizard.GameStats.m_energyMax;
            var clientMaxEnergy = wizard.GameStats.GetClientTypeAlternative().m_energyMax;

            wizard.UpdateEnergy(fullEnergy);

            // Inform the client of the change.
            var networkMessage = new PET_9_PROTOCOL.MSG_PETENERGYTICK() {
                GlobalID = wizard.GameObject.m_globalID,
                Energy = fullEnergy,
                MaxEnergy = clientMaxEnergy,
                TickTime = (int) wizard.PetOwnerBehavior.LastEnergyTickEpoch
            };
            SendToSocket(networkMessage);
            InformGameClient("Refilled energy.");
        }
        else {
            InformGameClient("You cannot do that.");

            return;
        }
    }

    private bool CastCantrip(uint spellTemplateID) {
        var wizard = GetActiveWizard();
        CantripsSpellTemplate cantrip = CantripFactory.CreateCantripTemplateFromId(spellTemplateID);

        bool hasEnergy = UseEnergy(wizard, cantrip.m_energyCost);
        byte outOfEnergy = 0;
        if (!hasEnergy) {
            cantrip.m_energyCost = 0;
            outOfEnergy = 1;
        }

        var cantripResponse = new CANTRIPSMESSAGES_57_PROTOCOL.MSG_CANTRIPSRESPONSE {
            EnergyUsed = (uint) cantrip.m_energyCost,
            CooldownSeconds = (uint) cantrip.m_cooldownSeconds,
            OutOfEnergy = outOfEnergy
        };
        SendToSocket(cantripResponse);

        if (!hasEnergy) {
            return false;
        }
        return true;
    }

    private bool UseEnergy(Wizard wizard, int energyCost) {
        var newEnergy = wizard.PetOwnerBehavior.Energy - energyCost;
        if (newEnergy < 0) {
            return false;
        }
        wizard.UpdateEnergy(newEnergy);

        // The client has a max energy increase effect applied, so sending it here would double the energy client side.
        var magicSchool = wizard.MagicSchoolBehavior.MagicSchool;
        var level = wizard.MagicSchoolBehavior.Level;
        var baseStats = MagicLevelsConfig.GetPlayerLevelInfo(magicSchool, level);
        var normMaxEnergy = baseStats.m_petEnergy;

        var networkMessage = new PET_9_PROTOCOL.MSG_PETENERGYTICK() {
            GlobalID = wizard.GameObject.m_globalID,
            Energy = newEnergy,
            MaxEnergy = normMaxEnergy,
            TickTime = (int) wizard.PetOwnerBehavior.LastEnergyTickEpoch
        };

        SendToSocket(networkMessage);

        return true;
    }

    private void CastEmoteCantrip(CantripsSpellTemplate cantrip, ref CANTRIPSMESSAGES_57_PROTOCOL.MSG_CASTEFFECT castEffect) {
        castEffect.AnimationName = cantrip.m_animationNames[0];
    }

    private void CastPlayEffectCantrip(CantripsSpellTemplate cantrip, ref CANTRIPSMESSAGES_57_PROTOCOL.MSG_CASTEFFECT castEffect) {
        // note that the only cantrip from this section that needs rng is the dice roll one
        // all the other cantrips in this section only have 1 animation
        var count = Math.Min(cantrip.m_animationKFMs?.Count ?? 0, cantrip.m_animationNames?.Count ?? 0);
        if (count == 0) {
            return;
        }

        var rand = new Random();
        int num = rand.Next(count);
        castEffect.AnimationKFM = cantrip.m_animationKFMs[num];
        castEffect.AnimationName = cantrip.m_animationNames[num];
    }

    private void CastSneakCantrip(Wizard wizard) {
        // Monsters leave the wizard alone for 30 seconds (NpcComponent.OnPlayerMove asks IsSneaking).
        s_sneakUntil[wizard.CharId] = DateTime.UtcNow.AddSeconds(SNEAK_SECONDS);
        ZoneBroadcast(new CANTRIPSMESSAGES_57_PROTOCOL.MSG_CANTRIPNOAGGRO { GlobalID = wizard.GameObjectID }, isSelfless: false);
    }

    private void CastTeleportCantrip(CantripsSpellTemplate cantrip) {
        // "Zone" or "Zone=Location" (To Buttersfield: "Wysteria/PA_Hub=Target_TeleportToHampshireButtersfield").
        var parameter = cantrip.m_effectParameter.ToString() ?? "";
        var split = parameter.IndexOf('=');
        var tpmsg = new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
            DestinationLocation = split > 0 ? parameter[(split + 1)..] : "Start",
            DestinationZone = split > 0 ? parameter[..split] : parameter,
            SendToClient = true,
            OwnerCharId = GetActiveWizard().CharId
        };
        Timers.StartSingleTimer("zonetransfer", tpmsg, _cantripCastTimeSpan);
    }

    private void CastRitualCantrip(int spellTemplateID) {
        var castRitualMsg = new CANTRIPSMESSAGES_57_PROTOCOL.MSG_CASTRITUAL {
            SpellTemplateID = spellTemplateID,
            Phase = 0
        };
        SendToSocket(castRitualMsg);
    }

    private void CastInvisCantrip(CantripsSpellTemplate cantrip) {
        var wizard = GetActiveWizard();
        GameEffectBase effect;
        if (cantrip.m_name == "CantripsMinorInvisibility") {
            effect = new NamedEffect {
                m_effectNameID = 1662424096, // if someone can find the string for this stringhash i will cum rapidly and forcefully, breaking the sound barrier in the process
                m_internalID = wizard.NextEffectId(),
                m_endTime = (uint) DateTimeOffset.UtcNow.AddSeconds(60).ToUnixTimeSeconds()
            };
        }
        else if (cantrip.m_name == "CantripsMajorInvisibility") {
            effect = new CantripsMajorInvisibilityEffect {
                m_effectNameID = StringHash.Compute("CantripsMajorInvisibilityEffect"),
                m_internalID = wizard.NextEffectId()
            };
        }
        else {
            return;
        }

        // Attempt to serialize the effect.
        var flags = PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit;
        if (!_effectSerializer.Serialize(effect, flags, out var serializedEffect)) {
            return;
        }

        var addEffect = new GAME_5_PROTOCOL.MSG_ADDEFFECT {
            GameObjectID = wizard.GameObject.m_globalID,
            EffectData = serializedEffect
        };

        Timers.StartSingleTimer("inviseffect", addEffect, _cantripCastTimeSpan);
    }

    private void MaybeBackflip(Wizard wizard) {
        // 1 in 20 chance the player does a backflip instead :D
        var rand = new Random();
        int num = rand.Next(20);
        if (num != 0) {
            return;
        }

        uint backflipID = 1521398842;
        CantripsSpellTemplate cantrip = CantripFactory.CreateCantripTemplateFromId(backflipID);
        var castEffect = new CANTRIPSMESSAGES_57_PROTOCOL.MSG_CASTEFFECT {
            GameObjectID = wizard.GameObject.m_globalID,
            SpellTemplateID = (int) backflipID
        };

        CastEmoteCantrip(cantrip, ref castEffect);
        SendToSocket(castEffect);
        InformGameClient("backflip!");
    }

}