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
 * INTERACT TRAINER
 * ========================================================================
 * 
 * PURPOSE:
 * Manages spell training interactions for NPCs, dynamically generating 
 * spell training options based on player characteristics and spell requirements.
 * 
 * USAGE EXAMPLE:
 * 
 * NOTE:
 * Dynamically resolves spell training options from NPC spell inventory.
 * Considers player's magic school, level, and existing spellbook.
 * 
 * TODO:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 3/18/2025
 */

using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Randomization;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class InteractTrainerComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    public string ServiceName     => "WizTrainingService";
    public string NpcIcon         => null;
    public string NpcNameKey      => null;
    public string NpcTextKey      => "GUI_NPCInteractText";
    public WizBangs WizBang       => WizBangs.Training;
    public string StateName       => "Shop";
    public string InteractWizBang => "Registrar";
    public string DisplayKey      => "GUI_ShopOptionEquipment";

    public List<NPCSpellEntry> SpellInventory { get; private set; } = [];
    private List<ServiceOptionBase> _serviceOptionBases = [];

    public static bool ShouldAttachToEntity(CoreTemplate template) 
        // Attach if the template is an NPC and has a spell inventory in Dragon database.
        => template is GameObjectTemplate goTemplate 
        && goTemplate.m_behaviors.Any(x => x is NPCBehaviorTemplate) 
        && (NpcSpellInventoryCollection.TryGetNpcInventory(goTemplate.m_templateID, out _)
            || IsTestTrainerNpc(goTemplate));

    /// <summary>
    /// The test trainer (Mr. Lincoln in Ravenwood) teaches every enchantment spell and every shadow spell for free, at any level.
    /// </summary>
    public bool IsTestTrainer { get; private set; }

    /// <summary>
    /// True for Mr. Lincoln in Ravenwood, the test trainer. (Abner in the Commons only shows one kind of
    /// service, so he keeps the treasure card shop.)
    /// </summary>
    internal static bool IsTestTrainerNpc(GameObjectTemplate template) {
        var names = $"{template.m_objectName} {Imlight.CoreLib.Shared.Resources.Locale.GetEnglishName(template.m_displayName.ToString())}";

        return names.Contains("Lincoln", System.StringComparison.OrdinalIgnoreCase);
    }

    private static readonly System.Lazy<List<NPCSpellEntry>> s_allEnchantmentSpells
        = new(() => [.. BuildTestSpells(enchantments: true), .. BuildTestSpells(enchantments: false)]);

    private static List<NPCSpellEntry> BuildTestSpells(bool enchantments) {
        var entries = new List<NPCSpellEntry>();
        var seen = new HashSet<string>();
        foreach (var (_, spell) in Imlight.CoreLib.Game.Spells.SpellFactory.AllSpellTemplates()) {
            string name = spell.m_name;
            if (string.IsNullOrEmpty(name) || spell.m_Treasure || name.EndsWith(" TC")
                || string.IsNullOrEmpty((string) spell.m_sMagicSchoolName) || string.IsNullOrEmpty((string) spell.m_displayName)
                || (enchantments
                    ? !Imlight.CoreLib.Game.Combat.CombatEnchantments.IsEnchantment(spell)
                    : (string) spell.m_sMagicSchoolName != "Shadow" || Imlight.CoreLib.Game.Combat.CombatEnchantments.IsEnchantment(spell))
                || !seen.Add(name)
                || Imlight.CoreLib.Game.Spells.SpellFactory.GetSpell(name) is not { } learnable) {
                continue;
            }

            entries.Add(new NPCSpellEntry { TemplateID = learnable.m_templateID, RequiredSpellID = 0, Level = 1 });
        }

        Logger.Information("Test trainer teaches {0} {1} spells.", Logger.Args(entries.Count, enchantments ? "enchantment" : "shadow"));

        return entries;
    }

    public override void OnStart() {
        if (Entity.Template is GameObjectTemplate testTrainer && IsTestTrainerNpc(testTrainer)) {
            IsTestTrainer = true;
            SpellInventory = s_allEnchantmentSpells.Value;
            InitializeServiceBases();

            return;
        }

        // Search Dragon database for our spell inventory.
        if (    Entity.Template is not GameObjectTemplate goTemplate
            || !NpcSpellInventoryCollection.TryGetNpcInventory(goTemplate.m_templateID, out var spellInventory)) {
            Logger.Error("{0} on entity {1} has no spell inventory.", 
                Logger.Args(ServiceName, Entity.ActiveGameObject.m_debugName));

            return;
        }

        SpellInventory = spellInventory.Spells;
        InitializeServiceBases();
    }

    private const int TEST_TRAINER_MAX_OPTIONS = 30;

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard wizard) {
        if (wizard is null) {
            return IsTestTrainer ? _serviceOptionBases.Take(TEST_TRAINER_MAX_OPTIONS) : _serviceOptionBases;
        }

        // We've initialized a list of service option bases in InitializeServiceBases.
        // However, these options can change depending on how far the player has progressed.
        // We must now edit each service option to reflect the player's status.
        List<ServiceOptionBase> newServiceOptions = [];

        for (int i = 0; i < _serviceOptionBases.Count; i++) {
            // The game disconnects when an NPC offers hundreds of options at once, so the test trainer offers the
            // first few spells the wizard doesn't know yet; training one brings the next into the list.
            if (IsTestTrainer && newServiceOptions.Count >= TEST_TRAINER_MAX_OPTIONS) {
                break;
            }

            // In randomizer mode the wizard learns a swapped spell, so the trainer offers that one instead.
            var trainerSpellId = (uint) SpellInventory[i].TemplateID;
            var spellId = IsTestTrainer ? trainerSpellId : Randomizer.MapSpell(wizard, trainerSpellId);
            if (CoreObjectFactory.GetCoreTemplate(spellId) is not SpellTemplate spellTemplate) {
                continue;
            }

            var hasSpell = wizard.SpellbookBehavior.HasSpell(spellId);
            if (hasSpell) {
                continue;
            }

            // Copy the shared option so one wizard's view never leaks into another's.
            var newBase = (WizTrainingOption) _serviceOptionBases[i] with { };
            if (spellId != trainerSpellId) {
                newBase.m_spellName = spellTemplate.m_name;
                newBase.m_displayKey = spellTemplate.m_displayName;
            }

            // Training costs nothing for the wizard's own school (or for everyone with FreeTraining), else one point.
            newBase.m_trainingCost = CostFor(wizard, spellTemplate);

            // NoLevelRequirements lets any level train any spell.
            var requiredLevel = Randomizer.NoLevelRequirements ? 1 : SpellInventory[i].Level;
            newBase.m_requiredLevel = requiredLevel;

            // Check if wizard has enough training points to train the spell, and if the
            // wizard is high enough level to train the spell.
            if (wizard.MagicSchoolBehavior.TrainingPoints < newBase.m_trainingCost
                    || wizard.MagicSchoolBehavior.Level < requiredLevel) {
                newBase.m_bCanTrain = false;
                newBase.m_failedRequirement = null;

                newServiceOptions.Add(newBase);
                continue;
            }

            // If the spell has no required spell, it can be trained, else check if the wizard has the required spell.
            var originalRequiredId = RequiredSpellFor(wizard, SpellInventory[i]);
            if (originalRequiredId == 0) {
                newBase.m_bCanTrain = true;
                newBase.m_failedRequirement = null;
                newBase.m_requirements = null;
            }
            else {
                // The required spell is swapped the same way, so a randomized chain still lines up.
                var requiredSpellId = Randomizer.MapSpell(wizard, (uint) originalRequiredId);
                if (CoreObjectFactory.GetCoreTemplate(requiredSpellId) is not SpellTemplate reqSpellTemplate) {
                    // A requirement that can't be found doesn't block training.
                    newBase.m_bCanTrain = true;
                    newBase.m_failedRequirement = null;
                    newBase.m_requirements = null;
                    newServiceOptions.Add(newBase);
                    continue;
                }

                if (requiredSpellId != SpellInventory[i].RequiredSpellID) {
                    newBase.m_requirements = HasSpellRequirement(reqSpellTemplate);
                }

                var hasReqSpell = wizard.SpellbookBehavior.HasSpell(requiredSpellId);

                if (!hasReqSpell) {
                    newBase.m_bCanTrain = false;
                    newBase.m_failedRequirement = new ReqHasSpell() {
                        m_applyNOT = false,
                        m_operator = Operator.ROP_AND,
                        m_spellName = reqSpellTemplate.m_name
                    };
                }
                else {
                    newBase.m_bCanTrain = true;
                    newBase.m_failedRequirement = null;
                }
            }

            newServiceOptions.Add(newBase);
        }

        return newServiceOptions;
    }

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) { }

    /// <summary>
    /// How many training points a spell costs this wizard: none for their own school or with FreeTraining, else one.
    /// </summary>
    internal int CostFor(Wizard wizard, SpellTemplate spellTemplate)
        => IsTestTrainer ? 0 : TrainingCost(wizard, spellTemplate);

    internal static int TrainingCost(Wizard wizard, SpellTemplate spellTemplate) {
        if (Randomizer.FreeTraining) {
            return 0;
        }

        string school = spellTemplate.m_sMagicSchoolName;

        return school == wizard.MagicSchoolBehavior.MagicSchool.ToString() ? 0 : 1;
    }

    /// <summary>
    /// The spell (before randomizer swaps) a wizard must know to train this entry, or 0 for none.
    /// NoSpellRequirements removes them all. RandomizeSpellRequirements gives each spell that has one a
    /// requirement picked from this trainer's lower level spells, so every chain can still be finished.
    /// </summary>
    private ulong RequiredSpellFor(Wizard wizard, NPCSpellEntry entry) {
        if (Randomizer.NoSpellRequirements || entry.RequiredSpellID == 0) {
            return 0;
        }

        if (!Randomizer.RandomizeSpellRequirements) {
            return entry.RequiredSpellID;
        }

        var lower = SpellInventory
            .Where(other => other.Level < entry.Level && other.TemplateID != entry.TemplateID)
            .Select(other => other.TemplateID)
            .OrderBy(id => id)
            .ToArray();

        return lower.Length == 0 ? 0 : lower[Randomizer.PickIndex(wizard, "requirements", entry.TemplateID, lower.Length)];
    }

    private void InitializeServiceBases() {
        // Foreach spell in the inventory, create a service option. This method serves to initialize defaults.
        // When a player interacts with this object, we'll go back and edit each service option
        // depending on the player's current level, school, and existing spell inventory.
        _serviceOptionBases = [];

        foreach (var spell in SpellInventory) {
            var template = CoreObjectFactory.GetCoreTemplate(spell.TemplateID);
            if (template is null || template is not SpellTemplate spellTemplate) {
                Logger.Error("Trainer {0} has an invalid spell template with ID {1}",
                    Logger.Args(Entity.ActiveGameObject.m_templateID, spell.TemplateID));

                continue;
            }

            var spellOption = new WizTrainingOption {
                m_iconKey = WizBang.ToString(),
                m_serviceName = ServiceName,

                m_spellName = spellTemplate.m_name,
                m_displayKey = spellTemplate.m_displayName,
                m_requiredLevel = spell.Level,
                m_trainingIndex = SpellInventory.IndexOf(spell),
                m_trainingCost = spellTemplate.m_trainingCost,
            };

            if (spell.RequiredSpellID != 0) {
                var reqTemplate = CoreObjectFactory.GetCoreTemplate(spell.RequiredSpellID);
                if (reqTemplate is null || reqTemplate is not SpellTemplate reqSpellTemplate) {
                    Logger.Error("Trainer {0} has an invalid required spell template with ID {1}",
                        Logger.Args(Entity.ActiveGameObject.m_templateID, spell.RequiredSpellID));

                    continue;
                }

                spellOption.m_requirements = HasSpellRequirement(reqSpellTemplate);
            }
            else {
                spellOption.m_requirements = null;
            }

            _serviceOptionBases.Add(spellOption);
        }
    }

    private static RequirementList HasSpellRequirement(SpellTemplate requiredSpell)
        => new() {
            m_applyNOT = false,
            m_operator = Operator.ROP_AND,
            m_requirements = new List<Requirement>() {
                new ReqHasSpell() {
                    m_applyNOT = false,
                    m_operator = Operator.ROP_AND,
                    m_spellName = requiredSpell.m_name,
                }
            }
        };

}