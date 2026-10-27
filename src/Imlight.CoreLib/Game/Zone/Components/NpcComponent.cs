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
 * NPC COMPONENT 
 * ========================================================================
 * 
 * PURPOSE:
 * Manages core NPC behavior and interaction mechanics, including 
 * combat, proximity detection, and statistical attributes.
 * 
 * USAGE EXAMPLE:
 * 
 * NOTE:
 * Supports monster and non-monster NPC configurations.
 * 
 * TODO:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 3/18/2025
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Effects;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class NpcComponent : ZoneEntityComponent, IComponentFactory, IClientBehaviorProvider<NPCBehavior> {

    public bool NoTransfer { get; set; } = false;
    public bool IsMonster { get; private set; }
    public bool IsBossMonster { get; private set; }
    public float IntelligenceFactor { get; private set; }
    public float SelfishnessFactor { get; private set; }
    public float AggressiveFactor { get; private set; }
    public int StartingHealth { get; private set; }
    public int CurrentHealth { get; private set; }
    public MagicSchool MagicSchool { get; private set; }
    public int Level { get; private set; }
    public float Proximity { get; private set; }
    public string NameOverride { get; private set; }

    private readonly NPCBehaviorTemplate _npcBehaviorTemplate;
    private readonly DuelistBehaviorTemplate _duelistBehaviorTemplate;

    private StatsComponent _statsComponent;
    private CombatCreatureDeckComponent _deckComponent;

    public static bool ShouldAttachToEntity(CoreTemplate template) 
        => template is GameObjectTemplate gameObjectTemplate
        && gameObjectTemplate.m_behaviors.Any(x => x is NPCBehaviorTemplate);

    public NpcComponent(ZoneEntity entity) : base(entity) {
        _npcBehaviorTemplate = entity.Template.m_behaviors
            .OfType<NPCBehaviorTemplate>()
            .First();
        _duelistBehaviorTemplate = entity.Template.m_behaviors
            .OfType<DuelistBehaviorTemplate>()
            .FirstOrDefault();

        this.IsBossMonster = _npcBehaviorTemplate.m_bossMob;
        this.IntelligenceFactor = _npcBehaviorTemplate.m_fIntelligence;
        this.SelfishnessFactor = _npcBehaviorTemplate.m_fSelfishFactor;
        this.AggressiveFactor = _npcBehaviorTemplate.m_nAggressiveFactor;
        this.StartingHealth = _npcBehaviorTemplate.m_nStartingHealth;

        // A custom vendor or trainer with a creature's look never starts a battle (CustomNpcs).
        this.IsMonster = _duelistBehaviorTemplate is not null
            && !Imlight.CoreLib.Game.World.CustomNpcs.IsCustomTag(entity.Info?.m_zoneTag);
        this.Proximity = _duelistBehaviorTemplate?.m_npcProximity ?? 0;
        
        // Try to parse the npcBehaviorTemplate.m_schoolOfFocus to a MagicSchool.
        var parsedSchool = MagicSchool.Balance;
        if (    _npcBehaviorTemplate.m_schoolOfFocus != "" 
            && !Enum.TryParse(_npcBehaviorTemplate.m_schoolOfFocus, out parsedSchool)) {
            Logger.Error("Failed to parse magic school {0} for creature {1}.",
                Logger.Args(_npcBehaviorTemplate.m_schoolOfFocus, Entity.ActiveGameObject.m_globalID));

            return;
        }

        this.MagicSchool = parsedSchool;
        this.Level = _npcBehaviorTemplate.m_nLevel;
        // A custom vendor or trainer (CustomNpcs) and a custom (Customs.cs) show their own name; a trainer tome's
        // trainer is named after the set trainer part it teaches.
        this.NameOverride = Imlight.CoreLib.Game.World.CustomNpcs.NameFor(entity.Info?.m_zoneTag)
            ?? Imlight.CoreLib.Game.Combat.Customs.NameOf(entity.ActiveGameObject?.m_globalID.Full ?? 0)
            ?? Imlight.CoreLib.Game.World.HouseTrainers.NameFor(entity.Info?.m_zoneTag)
            ?? Imlight.CoreLib.Game.World.HouseVendors.NameFor(entity.Info?.m_zoneTag);
    }

    public override void OnStart() {
        // All NPCs have game stats.
        _statsComponent = Entity.GetComponentOfType<StatsComponent>();
        if (_statsComponent is null) {
            Logger.Error("NPC {0} does not have a StatsComponent.", Logger.Args(Entity.ActiveGameObject.m_globalID));

            return;
        }

        _statsComponent.Stats.m_currentHitpoints = StartingHealth;
        _statsComponent.Stats.m_baseHitpoints = StartingHealth;

        // Boss mobs will sometimes have base effects in their NPC template.
        if (_npcBehaviorTemplate.m_baseEffects.Count > 0) {
            foreach (var effect in _npcBehaviorTemplate.m_baseEffects) {
                CharacterEffectHelper.AddGameEffectToStats(_statsComponent.Stats, effect);
            }
        }

        // If the NPC is a monster, it will have a deck.
        if (IsMonster) {
            _deckComponent = Entity.GetComponentOfType<CombatCreatureDeckComponent>();
            if (_deckComponent is null) {
                Logger.Error("NPC {0} does not have a CombatDeckComponent.", 
                    Logger.Args(Entity.ActiveGameObject.m_debugName));

                return;
            }
        }
    }

    // [Battle] EngageExtraReach (live): how much farther than the game's own reach (the creature's m_npcProximity) a
    // wizard starts a battle by walking into a monster (Niduus, 2026-10-27: "Make combat easier to start/engage when
    // walking directly to the mob"). 0 = only the game's own reach.
    internal static float EngageExtraReach => Math.Clamp(ConfigurationManager.GetValue("Battle.EngageExtraReach", 50f), 0f, 500f);

    // Farther than this between two checks is a teleport, not a walk: only where the wizard landed counts.
    private const float MAX_STEP = 1500f;
    // A wizard standing in reach whose battle did not start (the nearest circle was busy, or the wizard was still
    // held by another battle) is tried again this often.
    private static readonly TimeSpan s_engageRetry = TimeSpan.FromSeconds(1);

    private readonly Dictionary<IActorRef, (float X, float Y, float Z)> _lastPlayerLocations = [];
    private readonly Dictionary<IActorRef, DateTime> _engageAsked = [];
    private readonly HashSet<IActorRef> _nearMisses = [];

    public override void OnPlayerMove(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        // Checked every quarter second, even standing still. A monster asks for a battle while a wizard is in reach and
        // not in one, again every second if it did not start, so standing next to a monster always ends up in a fight.
        if (!IsMonster || playerObj is null) {
            return;
        }

        if (!Reached(playerObj, playerActor, out var ground, out var height, out var reach)) {
            _engageAsked.Remove(playerActor);

            return;
        }

        var now = DateTime.UtcNow;
        if (_engageAsked.TryGetValue(playerActor, out var askedAt) && now - askedAt < s_engageRetry) {
            return;
        }

        if (OnProximityEnter(playerObj, playerActor, playerWizard)) {
            if (!_engageAsked.ContainsKey(playerActor)) {
                Logger.Debug("Engage: {0} touched {1}: {2} away on the ground, {3} in height (reach {4} = game {5} + {6}).",
                    Logger.Args(NameOverride ?? Entity.ActiveGameObject?.m_debugName, playerObj.m_debugName, (int) ground,
                        (int) height, (int) reach, (int) Proximity, (int) EngageExtraReach));
            }

            _engageAsked[playerActor] = now;
        }
    }

    /// <summary>
    /// True when the wizard is in this monster's reach now or walked through it since the last check. The wizard's
    /// whole walked line counts, so running past a monster between two checks still touches it; the monster is
    /// taken where it is right now along its walk, not where it was up to a quarter second ago; and distance is on
    /// the ground with room for height, so slopes and stairs do not keep a wizard out of reach.
    /// </summary>
    private bool Reached(CoreObject player, IActorRef playerActor, out float ground, out float height, out float reach) {
        reach = Proximity + EngageExtraReach;
        var now = (player.m_location.X, player.m_location.Y, player.m_location.Z);
        var from = _lastPlayerLocations.TryGetValue(playerActor, out var last) ? last : now;
        _lastPlayerLocations[playerActor] = now;

        var stepX = now.X - from.X;
        var stepY = now.Y - from.Y;
        var stepZ = now.Z - from.Z;
        var stepLengthSquared = (stepX * stepX) + (stepY * stepY);
        if (stepLengthSquared > MAX_STEP * MAX_STEP) {
            from = now;
            stepX = stepY = stepZ = 0;
            stepLengthSquared = 0;
        }

        var target = Entity.GetComponentOfType<PathMovementComponent>()?.CurrentPosition ?? Entity.ActiveGameObject.m_location;
        var t = stepLengthSquared > 0
            ? Math.Clamp((((target.X - from.X) * stepX) + ((target.Y - from.Y) * stepY)) / stepLengthSquared, 0f, 1f)
            : 1f;
        var dx = target.X - (from.X + (stepX * t));
        var dy = target.Y - (from.Y + (stepY * t));
        var dz = target.Z - (from.Z + (stepZ * t));
        ground = MathF.Sqrt((dx * dx) + (dy * dy));
        height = dz;

        if (ground <= reach && Math.Abs(dz) <= Math.Max(reach * 1.5f, 150f)) {
            _nearMisses.Remove(playerActor);

            return true;
        }

        // A wizard who came close but not close enough, once per approach, so a monster that did not fight shows in
        // the log.
        if (ground <= reach * 2 && _nearMisses.Add(playerActor)) {
            Logger.Debug("Engage: {0} did not start a battle with {1}: {2} away on the ground, {3} in height (reach {4} = game {5} + {6}).",
                Logger.Args(NameOverride ?? Entity.ActiveGameObject?.m_debugName, player.m_debugName, (int) ground,
                    (int) dz, (int) reach, (int) Proximity, (int) EngageExtraReach));
        }
        else if (ground > reach * 2) {
            _nearMisses.Remove(playerActor);
        }

        return false;
    }

    public override void OnPlayerLeave(IActorRef playerActor, ulong id) {
        _lastPlayerLocations.Remove(playerActor);
        _engageAsked.Remove(playerActor);
        _nearMisses.Remove(playerActor);
    }

    public NPCBehavior GetClientBehaviorInstance() => new() {
        m_isMonster = IsMonster,
        m_wsNameOverride = NameOverride,
    };

    private bool OnProximityEnter(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        // Bug fix: There is an off chance the player spawns in the middle of our proximity radius,
        // before the wizard even has time to load themselves into the world.
        if (playerWizard is null) {
            return false;
        }

        if (!IsMonster) {
            return true;
        }

        if (playerWizard.IsInCombatGrace || playerWizard.IsInDuel) {
            return false;
        }

        // A monster already fighting (or dying) starts no other battle; a battle's own minion never does.
        if (Entity.IsCombatOnlyMinion || Entity.GetComponentOfType<CombatCreatureAIComponent>()?.IsInDuel == true) {
            return false;
        }

        // Hey! I'm a dueling creature and a player just entered my proximity.
        // I really don't like that.
        var interactionMsg = new ZONE_102_PROTOCOL.MSG_REQUESTCOMBATSIGIL {
            StartingParticipants = new Dictionary<IActorRef, CoreObject> {
                { playerActor, playerObj },
                { Entity.SelfRef, Entity.ActiveGameObject },
            },
        };
        Entity.ZoneRef.Tell(interactionMsg);

        // We do nothing further here. This message will be sent to the ZoneSigilSupervisor
        // to locate the closest sigil to the player and the creature.
        return true;
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_QUERYCREATURESTATS))]
    private void ReceiveQueryGameStats(COMBAT_106_PROTOCOL.MSG_QUERYCREATURESTATS message) {
        var rsp = new COMBAT_106_PROTOCOL.MSG_CREATURESTATS {
            GameStats = _statsComponent.Stats,
            CombatIntelligence = IntelligenceFactor,
            CombatSelfishFactor = SelfishnessFactor,
            CombatAggressionFactor = AggressiveFactor,
            CombatLevel = Level,
            MagicSchool = MagicSchool,
            SpellList = _deckComponent?.Spells ?? [],
        };

        Sender.Tell(rsp);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_QUERYNEARESTDUELTARGET))]
    private void ReceiveQueryNearestDuelTarget(ZONE_102_PROTOCOL.MSG_QUERYNEARESTDUELTARGET message) {
        // Only dueling creatures whose aggro radius covers the player answer this query.
        if (!IsMonster || message.PlayerGameObject is null || !IsInRadius(message.PlayerGameObject, Proximity)) {
            return;
        }

        Sender.Tell(new ZONE_102_PROTOCOL.MSG_QUERYNEARESTDUELTARGETRSP {
            CreatureActor = Entity.SelfRef,
            CreatureObject = Entity.ActiveGameObject,
        });
    }

}