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
 * JOURNEYMAN COMBAT AI
 * ========================================================================
 * 
 * PURPOSE:
 * Provides AI decision-making for NPCs in combat, handling targeting, spell selection,
 * and behavioral traits based on configurable personality factors.
 * 
 * USAGE EXAMPLE:
 * // Component is automatically attached to qualifying entities
 * 
 * NOTE:
 * Requires PathMovementComponent, NpcComponent, and StatsComponent to be attached to the entity.
 * Uses ConfigurationManager for combat behavior settings.
 *
 * TODO:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 08/14/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Zone.Components;

/// <summary>
/// Handles AI decision-making at a "journeyman" level for NPC entities during combat encounters.
/// </summary>
internal sealed class CombatCreatureAIComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IComponentFactory, IWithTimers {

    private const int COMBAT_DEATH_ANIMATION_IN_MS = 2000;

    public ITimerScheduler Timers { get; set; }

    // Imlight.ini long spelled it "HealingThreshhold", so a creature never counted anyone as hurt and never healed.
    private float _healingThreshold => ConfigurationManager.GetValue("Combat.HealingThreshold",
        ConfigurationManager.GetValue("Combat.HealingThreshhold", 0.75f));
    private float _healingPercentChance => ConfigurationManager.Settings["Combat.HealingPercentChance"].AsFloat();
    private float _preparePassChance => ConfigurationManager.Settings["Combat.PreparePassChance"].AsFloat();
    // [Combat] NaturalAttackChance: how often a natural attack (the "NA ..." melee cards) is picked while a real
    // attack spell can be paid for too. Picked like any other card, Skeletal Pirates swung almost every round.
    private static float s_naturalAttackChance => ConfigurationManager.GetValue("Combat.NaturalAttackChance", 0.25f);
    // [Combat] SaveUpChance: how often a creature that can only pay for its natural attack keeps its pips for the
    // bigger attack spell in its hand instead.
    private static float s_saveUpChance => ConfigurationManager.GetValue("Combat.SaveUpChance", 0.35f);
    private readonly int _damagedAggroIncrease   = ConfigurationManager.Settings["Combat.DamagedAggroIncrease"].AsInt();
    private readonly int _healingAggroIncrease   = ConfigurationManager.Settings["Combat.HealingAggroIncrease"].AsInt();
    private readonly int _provokeAggroIncrease   = ConfigurationManager.Settings["Combat.ProvokeAggroIncrease"].AsInt();
    private readonly int _pacifyAggroDecrease    = ConfigurationManager.Settings["Combat.PacifyAggroDecrease"].AsInt();
    private readonly Dictionary<int, int> _hateTable = [];
    private readonly Random _random = new();

    private PathMovementComponent _pathMovementComponent;
    private NpcComponent _npcComponent;
    private StatsComponent _stats;
    private CombatDuelComponent _currentDuelComponent;
    private CombatDuelSubCircle _currentSubCircle;

    // The chance that the creature will use a spell that is the most damaging spell in its hand.
    private float _intelligenceFactor;
    private bool _determinedSmartThisTurn;

    // The chance that the creature will attack the enemy team. If not, it will "prepare." (blade/heal/shield/trap/etc.)
    private float _aggressivenessFactor;
    private bool _determinedAggressiveThisTurn;

    // The chance that when a creature is preparing, it will buff itself rather than a teammate.
    private float _selfishnessFactor;
    private bool _determinedSelfishThisTurn;

    private Hand _roundHand;
    private CombatDuelSubCircle[] _friendlySubcircles
        => [.. _currentDuelComponent.ActiveSubCircles.Where(x => x.OccupiedTeam == _currentSubCircle.OccupiedTeam)];
    private bool _isHealingViable
        => _friendlySubcircles.Any(x => HealthFraction(x) < _healingThreshold);
    // Whole numbers divided gave 0 or 1, never the fraction in between.
    private static float HealthFraction(CombatDuelSubCircle circle)
        => (float) circle.ParticipantGameStats.m_currentHitpoints / Math.Max(1, circle.ParticipantGameStats.m_baseHitpoints);
    private bool _sentFinalKill;
    private bool _isInDuel;
    // Read by a sigil's battle (another actor) to see whether this monster may still join one late.
    internal bool IsInDuel => _isInDuel || _sentFinalKill;

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate gameObjectTemplate
        && gameObjectTemplate.m_behaviors.Any(x => x is NPCBehaviorTemplate)
        && gameObjectTemplate.m_behaviors.Any(x => x is DuelistBehaviorTemplate);

    public override void OnStart() {
        // Optional for a summoned minion, which must not roam or trip the sigil's OnCreatureMove.
        _pathMovementComponent = Entity.GetComponentOfType<PathMovementComponent>();
        if (_pathMovementComponent is null && !Entity.IsCombatOnlyMinion) {
            Logger.Warning(
                "{0} requires component of type {1} to be attached to the entity. Found on GameObject {2}",
                Logger.Args(
                    nameof(CombatCreatureAIComponent),
                    nameof(PathMovementComponent),
                    Entity.ActiveGameObject.m_debugName
                )
            );
        }

        _npcComponent = Entity.GetComponentOfType<NpcComponent>();
        if (_npcComponent is null) {
            Logger.Warning(
                "{0} requires component of type {1} to be attached to the entity. Found on GameObject {2}",
                Logger.Args(
                    nameof(CombatCreatureAIComponent),
                    nameof(NpcComponent),
                    Entity.ActiveGameObject.m_debugName
                )
            );

            return;
        }

        _intelligenceFactor = _npcComponent.IntelligenceFactor;
        _aggressivenessFactor = _npcComponent.AggressiveFactor;
        _selfishnessFactor = _npcComponent.SelfishnessFactor;

        _stats = Entity.GetComponentOfType<StatsComponent>();
        if (_stats is null) {
            Logger.Warning(
                "{0} requires component of type {1} to be attached to the entity. Found on GameObject {2}",
                Logger.Args(
                    nameof(CombatCreatureAIComponent),
                    nameof(StatsComponent),
                    Entity.ActiveGameObject.m_debugName
                )
            );

            return;
        }
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_ACTORADDEDTODUEL))]
    private void ReceiveCombatAdded(COMBAT_106_PROTOCOL.MSG_ACTORADDEDTODUEL message) {
        if (_sentFinalKill) {
            return;
        }

        _isInDuel = true;
        _currentDuelComponent = message.Duel;
        _currentSubCircle = message.SubCircle;
        _pathMovementComponent?.Stop(); // null for a summoned minion, which has no world movement
        InitiatizeHateTable();
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_QUERYENCOUNTERMINIONS))]
    private void ReceiveQueryEncounterMinions(ZONE_102_PROTOCOL.MSG_QUERYENCOUNTERMINIONS message) {
        // Only a creature of the zone's own that is not fighting (or dying) answers: a battle's minion stays in it.
        var creature = Entity.ActiveGameObject;
        if (_isInDuel || _sentFinalKill || Entity.IsCombatOnlyMinion || creature is null
            || message.TemplateIds is null || !message.TemplateIds.Contains(creature.m_templateID.Full)) {
            return;
        }

        if (message.Radius > 0) {
            var dx = creature.m_location.X - message.Center.X;
            var dy = creature.m_location.Y - message.Center.Y;
            if ((dx * dx) + (dy * dy) > message.Radius * message.Radius) {
                return;
            }
        }

        Sender.Tell(new ZONE_102_PROTOCOL.MSG_QUERYENCOUNTERMINIONSRSP {
            CreatureActor = Entity.SelfRef,
            CreatureObject = creature,
        });
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_NEWROUND))]
    private void ReceiveNewCombatRound(COMBAT_106_PROTOCOL.MSG_NEWROUND message) {
        if (_sentFinalKill) {
            return;
        }

        if (_currentDuelComponent.IsScriptedDuel()) {
            return;
        }

        _roundHand = _currentSubCircle.DrawFreshHand();

        DetermineAttitude();
        var action = DetermineTurnAction();

        // Send the action to the duel actor.
        _currentDuelComponent.ActorRef.Tell(action, Self);
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_COMBATEFFECT))]
    private void ReceiveCombatEffect(COMBAT_106_PROTOCOL.MSG_COMBATEFFECT message) {
        if (_sentFinalKill || _currentSubCircle is null || message.Caster is null || message.Targets is null) {
            return;
        }

        // Determine if I am included in the target array.
        var isTarget = message.Targets.Any(x => x.SlotIndex == _currentSubCircle.SlotIndex);
        var isHealing = message.Effect.m_effectType is kSpellEffects.kHeal
                                                    or kSpellEffects.kHealOverTime
                                                    or kSpellEffects.kHealPercent;

        // Ignore if the caster is on my team.
        var isOnMyTeam = message.Caster.OccupiedTeam == _currentSubCircle.OccupiedTeam;
        if (isOnMyTeam) {
            return;
        }

        if (isTarget) {
            var isPacify = message.Effect.m_effectType is kSpellEffects.kPacify;
            var isProvoke = message.Effect.m_effectType is kSpellEffects.kTaunt;

            int hateValue;
            if (isPacify) {
                hateValue = -_pacifyAggroDecrease;
            }
            else if (isProvoke) {
                hateValue = _provokeAggroIncrease;
            }
            else {
                hateValue = _damagedAggroIncrease;
            }

            UpdateHateTable(message.Caster.SlotIndex, hateValue);
        }
        else if (isHealing) {
            UpdateHateTable(message.Caster.SlotIndex, _healingAggroIncrease);
        }
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_COMBATDEATH))]
    private void ReceiveCombatDeath(COMBAT_106_PROTOCOL.MSG_COMBATDEATH message) {
        if (_sentFinalKill || !_isInDuel) {
            Entity.DeleteObject();

            return;
        }

        // An encounter minion can die before its duel slot is fully set up: nothing to remove from, just go.
        if (_currentDuelComponent is null || _currentSubCircle?.ParticipantObject is null) {
            _sentFinalKill = true;
            Entity.DeleteObject();
            if (_currentSubCircle?.IsSummonedMinion == true) {
                CombatDuelComponent.OnMinionRemoved(_currentSubCircle);
            }

            return;
        }

        // A minion has no world to return to: remove and delete now, no death-animation timer.
        if (_currentSubCircle?.IsSummonedMinion == true) {
            _currentDuelComponent.DuelBroadcast(new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATREMOVE {
                DuelID = _currentDuelComponent.SigilId,
                ParticipantID = _currentSubCircle.ParticipantObject.m_globalID
            });
            _sentFinalKill = true;
            Entity.DeleteObject();

            // Free the sub-circle so a future summon can reuse the slot.
            CombatDuelComponent.OnMinionRemoved(_currentSubCircle);

            return;
        }

        // I have died. Remove myself from the current duel.
        _currentDuelComponent.DuelBroadcast(new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATREMOVE {
            DuelID = _currentDuelComponent.SigilId,
            ParticipantID = _currentSubCircle.ParticipantObject.m_globalID
        });

        // Send this message to ourselves again after a delay to delete the entity. This lets the combat death animation play out,
        // and then the entity will be deleted.
        _sentFinalKill = true;
        var delay = TimeSpan.FromMilliseconds(COMBAT_DEATH_ANIMATION_IN_MS);
        Timers.StartSingleTimer("FinalKill", new COMBAT_106_PROTOCOL.MSG_COMBATDEATH(), delay);
    }

    private void DetermineAttitude() {
        _determinedSmartThisTurn = _random.NextDouble() < _intelligenceFactor;
        _determinedAggressiveThisTurn = _random.NextDouble() < _aggressivenessFactor;
        _determinedSelfishThisTurn = _random.NextDouble() < _selfishnessFactor;

        Logger.Debug("Duel {0} | Slot {1} | Smart: {2} | Aggressive: {3} | Selfish: {4}",
            Logger.Args(_currentDuelComponent.SigilId,
                _currentSubCircle.SlotIndex,
                _determinedSmartThisTurn,
                _determinedAggressiveThisTurn,
                _determinedSelfishThisTurn));
    }

    private COMBAT_106_PROTOCOL.MSG_ACTORCOMBATMOVE DetermineTurnAction() {
        // If I'm stunned, pass.
        if (_currentSubCircle.CombatParticipant.m_stunned > 0) {
            _currentSubCircle.CombatParticipant.m_stunned--;

            Logger.Debug("Duel {0} | Slot {1} | Stunned. Passing.",
                Logger.Args(_currentDuelComponent.SigilId, _currentSubCircle.SlotIndex));

            return new COMBAT_106_PROTOCOL.MSG_ACTORCOMBATMOVE {
                Actor = Entity.SelfRef,
                MoveType = (byte) CombatMoveType.Pass,
                SpellSelection = 0,
                SpellTarget = 0,
            };
        }

        // If we want to be aggressive and we have something to cast, do it.
        var hasDamageSpells = GetCastableDamageSpells(_roundHand.m_spellList).Count > 0;
        if (_determinedAggressiveThisTurn && hasDamageSpells) {
            return DetermineAggressiveBehavior();
        }
        else {
            return DetermineDefensiveBehavior();
        }
    }

    private COMBAT_106_PROTOCOL.MSG_ACTORCOMBATMOVE DetermineAggressiveBehavior() {
        // We want to cast a spell.
        var msg = new COMBAT_106_PROTOCOL.MSG_ACTORCOMBATMOVE {
            Actor = Entity.SelfRef,
            MoveType = (byte) CombatMoveType.Attack,
            SpellSelection = 0,
            SpellTarget = 0,
        };

        var castableDamageSpells = GetCastableDamageSpells(_roundHand.m_spellList);

        // If we have no castable damage spells, we'll just pass.
        if (castableDamageSpells.Count == 0) {
            Logger.Debug("Duel {0} | Slot {1} | No castable damage spells.",
                Logger.Args(_currentDuelComponent.SigilId, _currentSubCircle.SlotIndex));

            msg.MoveType = (byte) CombatMoveType.Pass;
            return msg;
        }

        var realAttacks = castableDamageSpells.Where(x => !IsNaturalAttack(x)).ToList();
        if (realAttacks.Count > 0 && realAttacks.Count < castableDamageSpells.Count
            && _random.NextDouble() >= s_naturalAttackChance) {
            castableDamageSpells = realAttacks;
        }
        else if (realAttacks.Count == 0 && _random.NextDouble() < s_saveUpChance
                 && SpellEffectFilter.FilterSpellsByOutgoingDamage(_roundHand.m_spellList).Any(x => !IsNaturalAttack(x))) {
            Logger.Debug("Duel {0} | Slot {1} | Only the natural attack is affordable. Saving pips for a bigger attack.",
                Logger.Args(_currentDuelComponent.SigilId, _currentSubCircle.SlotIndex));

            msg.MoveType = (byte) CombatMoveType.Pass;
            return msg;
        }

        var targetIdx = GetMostHatedTarget();
        msg.SpellTarget = (byte) targetIdx;

        // Are we smart enough to use our highest pip spell?
        if (_determinedSmartThisTurn) {
            var highestPipSpell = castableDamageSpells
                .OrderByDescending(x => x.m_pipCost.m_spellRank)
                .FirstOrDefault();
            if (highestPipSpell is not null) {
                msg.SpellSelection = (byte) _roundHand.m_spellList.IndexOf(highestPipSpell);
                return msg;
            }
        }

        // Otherwise, choose a random spell.
        var randomIdx = _random.Next(castableDamageSpells.Count);
        var selectedSpell = castableDamageSpells[randomIdx];
        msg.SpellSelection = (byte) _roundHand.m_spellList.IndexOf(selectedSpell);

        return msg;
    }

    private COMBAT_106_PROTOCOL.MSG_ACTORCOMBATMOVE DetermineDefensiveBehavior() {
        var msg = new COMBAT_106_PROTOCOL.MSG_ACTORCOMBATMOVE {
            Actor = Entity.SelfRef,
            MoveType = (byte) CombatMoveType.Attack,
            SpellSelection = 0,
            SpellTarget = 0,
        };

        // Healing is the highest priority defensive action.
        if (_isHealingViable && _random.NextDouble() < _healingPercentChance) {
            var healingSpells = GetCastableHealingSpells(_roundHand.m_spellList);
            if (healingSpells.Count > 0) {
                return DetermineHealingBehavior();
            }
        }

        // Check what we have available to "prepare" with.
        var buffSpells = GetCastableBuffSpells(_roundHand.m_spellList);
        var debuffSpells = GetCastableDebuffSpells(_roundHand.m_spellList);

        // If we have nothing to prepare with, fall back to attacking.
        // Otherwise damage-only creatures pass every time the aggressiveness RNG fails.
        if (buffSpells.Count == 0 && debuffSpells.Count == 0) {
            return GetCastableDamageSpells(_roundHand.m_spellList).Count > 0
                ? DetermineAggressiveBehavior()
                : DetermineAnyCastableBehavior();
        }

        // We have buffs or debuffs available. There's a small chance we pass anyway.
        if (_random.NextDouble() < _preparePassChance) {
            Logger.Debug("Duel {0} | Slot {1} | Preparing, but passing.",
                Logger.Args(_currentDuelComponent.SigilId, _currentSubCircle.SlotIndex));

            msg.MoveType = (byte) CombatMoveType.Pass;
            return msg;
        }

        // Flip a coin to either cast a buff or a debuff.
        var coinFlip = _random.NextDouble();
        if (coinFlip < 0.5 && buffSpells.Count > 0) {
            // Cast a buff.
            var randomIdx = _random.Next(buffSpells.Count);
            var selectedSpell = buffSpells[randomIdx];
            msg.SpellSelection = (byte) _roundHand.m_spellList.IndexOf(selectedSpell);

            // Are we selfish? If so, cast it on ourselves.
            if (_determinedSelfishThisTurn) {
                msg.SpellTarget = (uint) _currentSubCircle.SlotIndex;
            }
            else {
                // Otherwise, select a random teammate.
                var randomTeammate = _friendlySubcircles[_random.Next(_friendlySubcircles.Length)];
                msg.SpellTarget = (byte) (randomTeammate.SlotIndex + 1);
            }
        }
        else if (debuffSpells.Count > 0) {
            // Cast a debuff on our most hated enemy.
            var randomIdx = _random.Next(debuffSpells.Count);
            var selectedSpell = debuffSpells[randomIdx];
            msg.SpellSelection = (byte) _roundHand.m_spellList.IndexOf(selectedSpell);

            var targetIdx = GetMostHatedTarget();
            msg.SpellTarget = (byte) targetIdx;
        }

        return msg;
    }

    // Nothing to attack, buff or debuff with: cast any other card it can pay for (a shield, a stun, a spell the
    // filters do not know) before passing. A monster only passes when it cannot pay for anything.
    private COMBAT_106_PROTOCOL.MSG_ACTORCOMBATMOVE DetermineAnyCastableBehavior() {
        var msg = new COMBAT_106_PROTOCOL.MSG_ACTORCOMBATMOVE {
            Actor = Entity.SelfRef,
            MoveType = (byte) CombatMoveType.Pass,
            SpellSelection = 0,
            SpellTarget = 0,
        };

        // Heals wait for someone hurt (the healing roll above).
        var heals = GetCastableHealingSpells(_roundHand.m_spellList);
        var castable = GetCastableSpells(_roundHand.m_spellList)
            .Where(x => !heals.Contains(x) && CoreObjectFactory.GetCoreTemplate(x.m_templateID) is SpellTemplate)
            .ToList();
        if (castable.Count == 0) {
            var pips = _currentSubCircle.CombatParticipant.m_pipCount;
            Logger.Debug("Duel {0} | Slot {1} | Nothing castable in a hand of {2} with {3} pips + {4} power. Passing.",
                Logger.Args(_currentDuelComponent.SigilId, _currentSubCircle.SlotIndex,
                    _roundHand.m_spellList.Count, pips.m_genericPips, pips.m_powerPips));

            return msg;
        }

        var selectedSpell = castable[_random.Next(castable.Count)];
        msg.MoveType = (byte) CombatMoveType.Attack;
        msg.SpellSelection = (byte) _roundHand.m_spellList.IndexOf(selectedSpell);
        msg.SpellTarget = SpellEffectFilter.TargetsEnemy(selectedSpell)
            ? (byte) GetMostHatedTarget()
            : (uint) _currentSubCircle.SlotIndex;

        return msg;
    }

    private COMBAT_106_PROTOCOL.MSG_ACTORCOMBATMOVE DetermineHealingBehavior() {
        // We've determined that we want to heal. Either us or a teammate.
        var castableHealingSpells = GetCastableHealingSpells(_roundHand.m_spellList);
        var msg = new COMBAT_106_PROTOCOL.MSG_ACTORCOMBATMOVE {
            Actor = Entity.SelfRef,
            MoveType = (byte) CombatMoveType.Attack,
            SpellSelection = 0,
            SpellTarget = 0,
        };

        // Determine if we're smart enough to use the highest pip healing spell.
        if (_determinedSmartThisTurn) {
            var highestPipHealingSpell = castableHealingSpells
                .OrderByDescending(x => x.m_pipCost.m_spellRank)
                .FirstOrDefault();
            if (highestPipHealingSpell is not null) {
                msg.SpellSelection = (byte) _roundHand.m_spellList.IndexOf(highestPipHealingSpell);
            }
        }

        // Otherwise, choose a random healing spell.
        var randomIdx = _random.Next(castableHealingSpells.Count);
        var selectedSpell = castableHealingSpells[randomIdx];
        msg.SpellSelection = (byte) _roundHand.m_spellList.IndexOf(selectedSpell);

        // If we're below the healing threshold, and selfish enough, heal ourselves.
        // The battle's stats: a boss changed in the app's Bosses tab fights with a copy of the creature's own.
        var self = _currentSubCircle.ParticipantGameStats ?? _stats.Stats;
        var selfThresholdMatched = (float) self.m_currentHitpoints / Math.Max(1, self.m_baseHitpoints) < _healingThreshold;
        if (selfThresholdMatched && _determinedSelfishThisTurn) {
            msg.SpellTarget = (uint) _currentSubCircle.SlotIndex;
        }
        else {
            // Otherwise, heal the teammate with the lowest health percentage.
            var lowestHealthTeammate = _friendlySubcircles
                .OrderBy(HealthFraction)
                .First();

            msg.SpellTarget = (byte) (lowestHealthTeammate.SlotIndex + 1);
        }

        return msg;
    }

    // A creature's natural attack: the game names those cards "NA <creature>" (and a few "<name> NA").
    private static bool IsNaturalAttack(Spell spell) {
        var name = CoreObjectFactory.GetCoreTemplate(spell.m_templateID) is SpellTemplate template
            ? (string) template.m_name ?? ""
            : "";

        return name.StartsWith("NA ", StringComparison.Ordinal) || name.EndsWith(" NA", StringComparison.Ordinal);
    }

    private List<Spell> GetCastableSpells(List<Spell> spells) {
        var castableSpells = new List<Spell>();
        foreach (var spell in spells) {
            if (!_currentSubCircle.HasPipsForSpell(spell)) {
                continue;
            }

            castableSpells.Add(spell);
        }

        return castableSpells;
    }

    private List<Spell> GetCastableDamageSpells(List<Spell> spells) {
        var castableSpells = GetCastableSpells(spells);

        return SpellEffectFilter.FilterSpellsByOutgoingDamage(castableSpells);
    }

    private List<Spell> GetCastableHealingSpells(List<Spell> spells) {
        var castableSpells = GetCastableSpells(spells);

        return SpellEffectFilter.FilterSpellsByHealing(castableSpells);
    }

    private List<Spell> GetCastableBuffSpells(List<Spell> spells) {
        var castableSpells = GetCastableSpells(spells);

        return SpellEffectFilter.FilterSpellsByBuff(castableSpells);
    }

    private List<Spell> GetCastableDebuffSpells(List<Spell> spells) {
        var castableSpells = GetCastableSpells(spells);

        return SpellEffectFilter.FilterSpellsByDebuff(castableSpells);
    }

    private void InitiatizeHateTable() {
        // Create the hate table from the other side's slots. Monster slots are 0-3, so slot 3 (where an
        // encounter minion often joins) belongs to the monster side too.
        var half = _currentDuelComponent.SubCircles.Length / 2;
        var onMonsterSide = _currentSubCircle.SlotIndex < half;
        int start = onMonsterSide ? half : 0;
        int end = onMonsterSide ? _currentDuelComponent.SubCircles.Length : half;

        // Joining a second time (or a late re-add) must not throw on keys already there.
        _hateTable.Clear();
        for (int i = start; i < end; i++) {
            _hateTable[i] = 0;
        }

        // Our initial target will be whomever is across from us. The wrap matters for a summoned
        // minion, which sits in a player slot (4-7): +4 alone would land outside the hate table.
        var targetIdx = (_currentSubCircle.SlotIndex + 4) % _currentDuelComponent.SubCircles.Length;
        UpdateHateTable(targetIdx, 1);
    }

    private int GetMostHatedTarget() {
        var orderedHateTable = _hateTable.OrderByDescending(x => x.Value);

        // Pick the highest hated target that is still alive.
        foreach (var (targetIdx, hateValue) in orderedHateTable) {
            var target = _currentDuelComponent.SubCircles[targetIdx];
            if (target is null || !target.Occupied) {
                continue;
            }

            if (target.IsAlive) {
                return targetIdx;
            }
        }

        return 0;
    }

    private void UpdateHateTable(int targetIdx, int hateValue) {
        // Make sure we aren't targeting ourselves.
        if (_currentSubCircle is null || targetIdx == _currentSubCircle.SlotIndex) {
            Logger.Error("Creature tried to target itself.");
            return;
        }

        // A caster who joined after the table was built (or a slot we never listed) gets an entry now.
        _hateTable[targetIdx] = (_hateTable.TryGetValue(targetIdx, out var current) ? current : 0) + hateValue;
    }

}