/*
 * Pet Will Cast talents (Niduus, 2026-10-27: "we also may need pet Willcast to work ... Add config for (Willcast
 * doesn't cost Reagents)").
 *
 * A Will Cast is a pet talent with a ProvidePetPowerInfo that has m_combatTalent on (TalentData/PetPowers, such as
 * Talent-Power-Combat-Wyldfire-01, "Willcast [Wyldfire]"). The game files give everything about it:
 *   m_extra1          the card it casts (the pet copy, such as PetPower-Combat-Wyldfire-01)
 *   m_rampUp          the round of the battle it can first be used in (1 = right away)
 *   m_cooldown        the rounds to wait after a use
 *   m_usesPerCombat   the most uses in one battle (0 = no limit)
 *   m_happinessCost   the pet happiness each use takes
 *   m_noPVP           no use in PvP
 * Most come "-Locked" on a pet: unlocking one (MSG_UNLOCKPETTALENT) costs the m_unlockReagent* reagents once and swaps
 * the talent for its m_unlockedTemplate. The reagents are not paid per cast.
 *
 * [Pets] in Imlight.ini, all read each time so a change applies without a restart:
 *   WillCasts                 false: no Will Casts at all.
 *   WillCastsFreeReagents     true: locked Will Casts work right away and unlock without reagents.
 *   WillCastsCostHappiness    false: a Will Cast takes no happiness.
 *   WillCastCardData          how the pet card is sent to the game (MSG_SHOWPETCARD PetData): Spell (the card, serialized
 *                             like a hand card), SpellName or TalentName. The game files name the message but not its
 *                             layout, so this is a test switch.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Pet;

internal static class PetWillCasts {

    /// <summary>A Will Cast of a pet, the card it casts and whether it still has to be unlocked.</summary>
    internal sealed record WillCast(string Talent, uint TalentId, ProvidePetPowerInfo Power, string SpellName,
                                    uint SpellTemplateId, bool Locked) {

        internal int Cooldown => Math.Max(0, Power.m_cooldown);
        internal int RampUp => Math.Max(1, Power.m_rampUp);
        internal int UsesPerCombat => Math.Max(0, Power.m_usesPerCombat);
        internal int HappinessCost => Math.Max(0, Power.m_happinessCost);
        internal bool NoPvP => Power.m_noPVP;
        internal bool MyTurn => Power.m_myTurn;

        /// <summary>Whether it can be cast: unlocked, or locked with WillCastsFreeReagents on.</summary>
        internal bool Usable => !Locked || FreeReagents;

        internal string ShownName => CardName(SpellName, SpellTemplateId);

    }

    internal static bool Enabled => ConfigurationManager.GetValue("Pets.WillCasts", true);
    internal static bool FreeReagents => ConfigurationManager.GetValue("Pets.WillCastsFreeReagents", false);
    internal static bool CostsHappiness => ConfigurationManager.GetValue("Pets.WillCastsCostHappiness", true);
    internal static string CardData => (ConfigurationManager.GetSetting("Pets.WillCastCardData") ?? "Spell").Trim();

    /// <summary>The pet the wizard wears, or null.</summary>
    internal static WizClientObjectItem WornPet(Wizard wizard)
        => wizard?.EquipmentBehavior?.EquippedItems?.FirstOrDefault(item => PetProgress.GetBehavior(item) is not null);

    /// <summary>The Will Casts of the pet a wizard wears, its jewel's talents included, in talent order.</summary>
    internal static List<WillCast> ForWizard(Wizard wizard) => ForPet(WornPet(wizard));

    /// <summary>The Will Casts of a pet, its jewel's talents included, in talent order.</summary>
    internal static List<WillCast> ForPet(WizClientObjectItem pet) {
        var behavior = pet is null ? null : PetProgress.GetBehavior(pet);
        if (behavior is null) {
            return [];
        }

        var result = new List<WillCast>();
        var talents = (behavior.m_expressedTalents ?? []).Select(PetData.GetTalent)
            .Concat(JewelSockets.GetPetJewelTalents(pet).Select(PetData.GetTalent))
            .OfType<PetTalentTemplate>();
        foreach (var talent in talents) {
            if (Resolve(talent) is { } willCast && result.All(other => other.TalentId != willCast.TalentId)) {
                result.Add(willCast);
            }
        }

        return result;
    }

    /// <summary>The Will Cast of a talent, or null for a talent that is not one or whose card is not in the game files.</summary>
    internal static WillCast Resolve(PetTalentTemplate talent) {
        var power = talent?.m_effectList?.OfType<ProvidePetPowerInfo>().FirstOrDefault(info => info.m_combatTalent);
        if (power is null) {
            return null;
        }

        string talentName = talent.m_talentName;
        var spellName = FindCard(power);
        var spell = spellName is null ? null : SpellFactory.GetSpell(spellName);
        if (spell is null) {
            Logger.Warning("Pet Will Cast {0}: no card found for {1}.", Logger.Args(talentName, (string) power.m_extra1));

            return null;
        }

        var locked = !string.IsNullOrEmpty((string) power.m_unlockedTemplate);

        return new WillCast(talentName, PetData.TalentId(talentName), power, spellName, spell.m_templateID, locked);
    }

    private static string FindCard(ProvidePetPowerInfo power) {
        string extra = power.m_extra1;
        if (!string.IsNullOrEmpty(extra) && SpellFactory.Exists(extra)) {
            return extra;
        }

        string trigger = power.m_triggerName;

        return PetTalentsTriggeredSpells.GetSpellNames(trigger ?? "").FirstOrDefault(SpellFactory.Exists);
    }

    /// <summary>The English name of a card ("Wyldfire"), else its template name.</summary>
    internal static string CardName(string spellName, uint templateId) {
        try {
            if (CoreObjectFactory.GetCoreTemplate(templateId) is SpellTemplate template) {
                string key = template.m_displayName;
                var shown = string.IsNullOrEmpty(key) ? "" : Locale.GetEnglishName(key);
                if (!string.IsNullOrWhiteSpace(shown)) {
                    return shown.Trim();
                }
            }
        }
        catch (Exception) {
            // The template name is used instead.
        }

        return spellName;
    }

    /// <summary>The reagents that unlock a locked Will Cast, as (template id, amount).</summary>
    internal static List<(uint TemplateId, int Amount)> UnlockReagents(ProvidePetPowerInfo power)
        => [.. new[] {
                (power.m_unlockReagentTemplate1, power.m_unlockReagentAmount1),
                (power.m_unlockReagentTemplate2, power.m_unlockReagentAmount2),
                (power.m_unlockReagentTemplate3, power.m_unlockReagentAmount3),
                (power.m_unlockReagentTemplate4, power.m_unlockReagentAmount4),
            }.Where(need => need.Item1 != 0 && need.Item2 > 0)];

    /// <summary>The English name of an item template, such as a reagent.</summary>
    internal static string ItemName(uint templateId) {
        try {
            if (CoreObjectFactory.GetCoreTemplate(templateId) is GameObjectTemplate template) {
                string key = template.m_displayName;
                var shown = string.IsNullOrEmpty(key) ? "" : Locale.GetEnglishName(key);
                if (!string.IsNullOrWhiteSpace(shown)) {
                    return shown.Trim();
                }

                string name = template.m_objectName;
                if (!string.IsNullOrWhiteSpace(name)) {
                    return name;
                }
            }
        }
        catch (Exception) {
            // The id is used instead.
        }

        return templateId.ToString();
    }

    /// <summary>
    /// Unlocks a locked Will Cast of a pet: takes its reagents from the wizard's bag (none with WillCastsFreeReagents)
    /// and swaps the talent for its unlocked one. Tells the session the reagent and pet changes for the game.
    /// </summary>
    /// <param name="session">The wizard's session, for the reagent and pet messages.</param>
    /// <param name="wizard">The pet's owner.</param>
    /// <param name="pet">The pet item.</param>
    /// <param name="talentId">The locked talent's id (StringHash of its name).</param>
    /// <param name="problem">What went wrong, in words for the player; null when it was unlocked.</param>
    /// <returns>The unlocked talent's name, or null.</returns>
    internal static string Unlock(IActorRef session, Wizard wizard, WizClientObjectItem pet, uint talentId, out string problem) {
        problem = null;
        if (PetData.GetTalent(talentId) is not PetTalentTemplate talent
                || talent.m_effectList?.OfType<ProvidePetPowerInfo>().FirstOrDefault() is not { } power
                || string.IsNullOrEmpty((string) power.m_unlockedTemplate)) {
            problem = "That talent has nothing to unlock.";

            return null;
        }

        string unlockedName = power.m_unlockedTemplate;
        if (PetData.GetTalent(unlockedName) is null) {
            problem = $"The game files have no unlocked talent {unlockedName}.";

            return null;
        }

        var behavior = PetProgress.GetBehavior(pet);
        if (behavior is null || behavior.m_allTalents?.Contains(talentId) != true && behavior.m_expressedTalents?.Contains(talentId) != true) {
            problem = "Your pet does not have that talent.";

            return null;
        }

        if (!FreeReagents) {
            var needs = UnlockReagents(power);
            var missing = needs.Where(need => (wizard.AlchemyBehavior?.GetReagent(need.TemplateId)?.m_quantity ?? 0) < need.Amount)
                .Select(need => $"{need.Amount} {ItemName(need.TemplateId)} (you have {wizard.AlchemyBehavior?.GetReagent(need.TemplateId)?.m_quantity ?? 0})")
                .ToList();
            if (missing.Count > 0) {
                problem = "Unlocking it needs " + string.Join(", ", missing) + ".";

                return null;
            }

            foreach (var need in needs) {
                var reagent = wizard.AlchemyBehavior.GetReagent(need.TemplateId);
                if (reagent is null || !wizard.RemoveReagents(reagent.m_globalID, need.Amount, out var left)) {
                    continue;
                }

                session.Tell(left.m_quantity > 0
                    ? new WIZARD_12_PROTOCOL.MSG_REAGENTUPDATE { GlobalID = wizard.GameObjectID, ItemID = left.m_globalID, Quantity = left.m_quantity }
                    : new WIZARD_12_PROTOCOL.MSG_REAGENTREMOVE { GlobalID = wizard.GameObjectID, ItemID = left.m_globalID });
            }
        }

        if (!PetProgression.ReplaceTalent(pet, talentId, PetData.TalentId(unlockedName))) {
            problem = "The talent could not be changed.";

            return null;
        }

        wizard.SavePet(pet);
        if (PetBehaviorUpdate.TryCreate(pet, out var update)) {
            session.Tell(update);
        }

        return unlockedName;
    }

}
