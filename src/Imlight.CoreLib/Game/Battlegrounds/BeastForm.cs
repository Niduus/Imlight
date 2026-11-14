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

using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.Cryptography;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imcodec.IO;
using Imlight.Common;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Battlegrounds;

/*
 * A Beastmoon form outside battle. The client knows two effects for it:
 * - PolymorphEffect (m_templateID): the wizard is in the polymorph template's form. The arenas' base teleporters ask for
 *   one (ReqHasPolymorphEffect) before a wizard may leave the base.
 * - TransformationEffect: the look, from the template's own TransformationEffectInfo (race Transformation_BG_Cyclops_Life),
 *   the way .petform shows a pet (CommandPetFormProtocol).
 * Which of the two the client draws the beast from is not known yet, so `.bg form <name> poly|look|both` can send either
 * or both; both is the default. The server keeps both on the wizard (Wizard.GameEffects) under one slot, so they come
 * off together and come back after a zone change (Battlegrounds.AfterZoneChange).
 */
internal static class BeastForm {

    internal enum Look : byte { Both = 0, Polymorph = 1, Transformation = 2 }

    internal const string POLYMORPH_EFFECT_NAME = "Polymorph";
    internal const string TRANSFORMATION_EFFECT_NAME = "Transformation";

    // Marks the form's effects among the wizard's; an elixir's or the pet's look carries its own slot.
    internal static readonly uint SlotId = StringHash.Compute("BeastmoonForm");

    internal static bool IsInForm(Wizard wizard)
        => wizard?.GameEffects?.Exists(e => e?.m_itemSlotID == SlotId) == true;

    /// <summary>Whether the wizard has a polymorph effect (a beast form, or any other the server gave).</summary>
    internal static bool HasPolymorph(Wizard wizard)
        => wizard?.GameEffects?.Exists(e => e is PolymorphEffect) == true;

    /// <summary>Turns the wizard into the form and tells their game and the zone. Returns an error, or null.</summary>
    internal static string Start(Wizard wizard, IActorRef session, IActorRef zone, string formName, Look look) {
        if (wizard is null) {
            return "No wizard.";
        }

        if (wizard.IsInDuel) {
            return "You can't change form during a battle.";
        }

        var template = Battlegrounds.Form(formName, out var fileName);
        if (template is null) {
            return $"No beast form is called {formName}. Try .bg forms.";
        }

        End(wizard, session, zone);

        var player = Battlegrounds.GetOrAdd(wizard.CharId);
        player.FormTemplateId = template.m_templateID;
        player.FormName = fileName;
        player.FormLook = look;
        player.BeastHealth = 0;
        var added = AddEffects(wizard, player);
        foreach (var message in AddMessages(wizard, added)) {
            Broadcast(session, zone, message);
        }

        BeastCombat.ShowHealth(wizard, session);

        Logger.Information("Beastmoon | {0} took the form {1} (template {2}, creature {3}, look {4}): {5}.",
            Logger.Args(wizard.CharId, fileName, template.m_templateID, template.m_mobTemplateID, look,
                string.Join(", ", added.Select(e => e.GetType().Name))));

        return null;
    }

    /// <summary>
    /// Chooses the form a wizard takes on arriving in a battleground, without telling anyone: their effects are put on
    /// with the zone change (Battlegrounds.AfterZoneChange). Returns an error, or null.
    /// </summary>
    internal static string Choose(Wizard wizard, string formName, Look look) {
        var template = Battlegrounds.Form(formName, out var fileName);
        if (template is null) {
            return $"No beast form is called {formName}. Try .bg forms.";
        }

        var player = Battlegrounds.GetOrAdd(wizard.CharId);
        player.FormTemplateId = template.m_templateID;
        player.FormName = fileName;
        player.FormLook = look;
        player.BeastHealth = 0;

        return null;
    }

    /// <summary>"Life Cyclops" (a match's m_unlockedPolymorphTags) as its first-rank form, BG-Polymorph-Cyclops-Life-01.</summary>
    internal static string FormOfTag(string tag) {
        var words = tag?.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);

        return words is { Length: 2 } ? $"{Battlegrounds.FORM_PREFIX}{words[1]}-{words[0]}-01" : null;
    }

    /// <summary>Ends the form, if the wizard has one. Returns whether they had.</summary>
    internal static bool End(Wizard wizard, IActorRef session, IActorRef zone) {
        if (wizard is null) {
            return false;
        }

        if (Battlegrounds.Of(wizard.CharId) is { } player) {
            player.FormTemplateId = 0;
            player.FormName = null;
        }

        var current = wizard.GameEffects.Where(e => e?.m_itemSlotID == SlotId).ToList();
        foreach (var effect in current) {
            wizard.GameEffects.Remove(effect);
            Broadcast(session, zone, new GAME_5_PROTOCOL.MSG_REMOVEEFFECT {
                GameObjectID = wizard.GameObjectID,
                EffectNameID = effect.m_effectNameID,
                InternalID = effect.m_internalID,
            });
        }

        // The health globe showed the beast's health (BeastCombat.ShowHealth); it shows the wizard's own again.
        if (current.Count > 0 && wizard.GameStats is { } stats) {
            session?.Tell(new WIZARD_12_PROTOCOL.MSG_UPDATEHEALTH {
                CharacterID = wizard.GameObjectID,
                NewHealth = stats.m_currentHitpoints,
                NewHealthMax = stats.GetClientTypeAlternative().m_baseHitpoints,
                DisplayDiff = 0,
            }, null);
        }

        return current.Count > 0;
    }

    /// <summary>
    /// Puts the form's effects on the wizard (without telling anyone): used by Start, and after a zone change, before
    /// the wizard's object is made (the game then gets them with the rest of the wizard's effects).
    /// </summary>
    internal static List<GameEffectBase> AddEffects(Wizard wizard, Battlegrounds.Player player) {
        var added = new List<GameEffectBase>();
        if (player.FormTemplateId == 0 || IsInForm(wizard)) {
            return added;
        }

        var template = Battlegrounds.Form(player.FormName, out _);
        if (template is null) {
            return added;
        }

        if (player.FormLook != Look.Transformation) {
            added.Add(new PolymorphEffect {
                m_templateID = template.m_templateID,
                m_effectNameID = StringHash.Compute(POLYMORPH_EFFECT_NAME),
                m_itemSlotID = SlotId,
                m_internalID = wizard.NextEffectId(),
            });
            wizard.GameEffects.Add(added[^1]);
        }

        if (player.FormLook != Look.Polymorph
            && template.m_effects?.OfType<TransformationEffectInfo>().FirstOrDefault() is { } info) {
            added.Add(new TransformationEffect {
                m_sRace = info.m_sRace,
                m_eGender = info.m_eGender,
                m_fScale = info.m_fScale,
                m_nPrimaryColor = info.m_nPrimaryColor,
                m_nSecondaryColor = info.m_nSecondaryColor,
                m_nPatternOption = info.m_nPatternOption,
                m_nGeometryOption = info.m_nGeometryOption,
                m_sNaturalAttack = info.m_sNaturalAttack,
                m_effectNameID = StringHash.Compute(string.IsNullOrEmpty((string) info.m_effectName) ? TRANSFORMATION_EFFECT_NAME : (string) info.m_effectName),
                m_itemSlotID = SlotId,
                m_internalID = wizard.NextEffectId(),
            });
            wizard.GameEffects.Add(added[^1]);
        }

        return added;
    }

    /// <summary>Shows a team change made in the zone (`.bg team`): the old team's effect off, the new one's on.</summary>
    internal static void ShowTeam(Wizard wizard, IActorRef session, IActorRef zone) {
        if (wizard?.GameEffects is null || Battlegrounds.Of(wizard.CharId) is not { } player) {
            return;
        }

        var before = wizard.GameEffects.Where(e => e?.m_itemSlotID == Battlegrounds.TeamSlotId).ToList();
        var added = Battlegrounds.AddTeamEffect(wizard, player);
        foreach (var effect in before.Where(e => !wizard.GameEffects.Contains(e))) {
            Broadcast(session, zone, new GAME_5_PROTOCOL.MSG_REMOVEEFFECT {
                GameObjectID = wizard.GameObjectID,
                EffectNameID = effect.m_effectNameID,
                InternalID = effect.m_internalID,
            });
        }

        if (added is not null) {
            foreach (var message in AddMessages(wizard, [added])) {
                Broadcast(session, zone, message);
            }
        }
    }

    /// <summary>The form and team as messages for one more player's game (a player who arrives later), or none.</summary>
    internal static List<IMessage> LookMessages(Wizard wizard)
        => wizard?.GameEffects is null ? [] : AddMessages(wizard, wizard.GameEffects.Where(e => e?.m_itemSlotID == SlotId || e?.m_itemSlotID == Battlegrounds.TeamSlotId));

    private static List<IMessage> AddMessages(Wizard wizard, IEnumerable<GameEffectBase> effects) {
        var serializer = new CoreObjectSerializer(behaviors: SerializerFlags.None);
        var flags = PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit;
        var messages = new List<IMessage>();
        foreach (var effect in effects) {
            if (serializer.Serialize(effect, flags, out ByteString data)) {
                messages.Add(new GAME_5_PROTOCOL.MSG_ADDEFFECT { GameObjectID = wizard.GameObjectID, EffectData = data });
            }
            else {
                Logger.Warning("Beastmoon | Could not serialize the form effect {0}.", Logger.Args(effect.GetType().Name));
            }
        }

        return messages;
    }

    // The wizard's own game first, then everyone else in the zone.
    private static void Broadcast(IActorRef session, IActorRef zone, IMessage message) {
        session?.Tell(message, null);
        zone?.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Message = message,
            Selfless = true,
            Sender = session,
            Targets = ZoneBroadcastTarget.Players,
        });
    }

}
