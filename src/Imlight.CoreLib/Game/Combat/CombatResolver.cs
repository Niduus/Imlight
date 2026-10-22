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
 * COMBAT ACTION RESOLUTION SYSTEM
 * ========================================================================
 * 
 * PURPOSE:
 * Serves as the entry point for resolving all combat actions, coordinating
 * the sequence of spells cast during a round and implementing the core
 * mathematical resolution of combat effects.
 * 
 * USAGE EXAMPLE:
 * var resolver = new CombatResolver(duel, subCircles);
 * resolver.Reset();
 * resolver.AddCombatMove(CombatMoveType.Attack, caster, target, spell);
 * float cinematicTime = resolver.ApplyQueuedCombatActions(out combatActionListObj);
 * 
 * NOTE:
 * This system works in conjunction with several specialized combat classes:
 * 
 * - CombatDuelComponent:    Orchestrates the overall duel, manages participants and phases
 * - CombatResolver:         Processes and resolves combat actions during the execution phase
 * - CombatDuelSubCircle:    Handles individual participant state and position
 * - CombatActionResolver:   Processes queued actions and resolves target selection
 * - CombatEffectApplicator: Applies spell effects with proper modifications
 * - CombatCharms:           Manages offensive modifiers that affect outgoing damage/healing
 * - CombatWards:            Handles defensive modifiers that affect incoming damage
 * - CombatDeck:             Controls spell deck management, drawing and discarding
 * - CombatEffectStack:      Tracks random/variable effect selection using bit-packing
 * 
 * The resolver determines hit/fizzle mechanics, processes spell accuracy,
 * manages the execution order, and coordinates timing of animations.
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 10/14/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.CoreLib.Game.Spells;
using Imlight.Common;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Cryptography;

namespace Imlight.CoreLib.Game.Combat;

public class QueuedCombatAction {
    
    public CombatDuelSubCircle SpellCaster;
    public CombatDuelSubCircle SelectedTarget;
    public Spell Spell;
    public SpellTemplate SpellTemplate;
    // A boss cheat with an Interrupt step went off before it: the spell fizzles (CombatResolver.BossCheats.cs).
    public bool Interrupted;
    
}

/// <summary>
/// Processes and resolves combat actions during the execution phase of a duel round.
/// </summary>
/// <remarks>
/// Responsible for determining the order of combat actions, handling fizzles, and coordinating 
/// the effects of spells on targets. Manages the flow of a combat turn by processing queued 
/// actions and calculating cinematic timing for visual feedback.
/// </remarks>
public partial class CombatResolver(Duel duel, CombatDuelSubCircle[] actorSubCircles) {
    
    private const int SPELL_FIZZLE_TIME = 4;

    // [Battle] ManaOnFizzle: a fizzled spell still costs its mana.
    // [Battle] FullPipConserve: a power pip only half used on a spell always comes back as a normal pip.
    private static readonly bool s_fullPipConserve = ConfigurationManager.GetValue("Battle.FullPipConserve", false);

    private static readonly bool s_manaOnFizzle = ConfigurationManager.GetValue("Battle.ManaOnFizzle", false);
    // How long the round waits for each animation. [Battle] FastAnimationTiming (on by default) uses the times measured
    // in Niduus's game log of 2026-10-22; off, the round waits the old, longer times (if the new ones ever cut a spell short).
    // Measured over 37 rounds: a pass is over in about 0.35 s, 0.5 s with the turn change (the next caster starts while it
    // still plays; 1 s each was waited), and a defeat takes 1 to 1.7 s. The casting animation itself takes 3.75 s, but
    // its 5 s also covers the moments between turns the estimate does not count, so it stays.
    internal static readonly bool FastAnimationTiming = ConfigurationManager.GetValue("Battle.FastAnimationTiming", true);
    private static readonly float SPELL_PASS_TIME = FastAnimationTiming ? 0.5f : 1.0f;
    private const float SPELL_CAST_TIME = 5.0f;
    // The most the round waits for a .c1s/.c2s/.cfs/.cls spell, whose casting animation the game skips.
    private static readonly float SkipCastSeconds = Math.Max(0.5f, Imlight.Common.ConfigurationManager.GetValue("Battle.SkipCastSeconds", 2f));
    private const float HANGING_EFFECT_CONSUME_TIME = 1.0f;
    private const float OVER_TIME_ACTIVATION_TIME = 2.0f;
    private static readonly float DEATH_ANIMATION_TIME = FastAnimationTiming ? 1.5f : 2.0f;

    private readonly Duel _duel = duel;

    private readonly CombatDuelSubCircle[] _subCircles = actorSubCircles;
    private CombatDuelSubCircle[] ActiveSubCircles => [.. _subCircles.Where(x => x.Occupied)];
    private List<QueuedCombatAction> _queuedCombatActions;

    // Daybreaker and Nightbringer: the spell is paid for (pips, card, fizzle check) when played, then casts on
    // the caster's next turn, at its start (Daybreaker) or its end (Nightbringer), even if they are stunned.
    // Free is a spell from .daybreaker or .nightbringer: no card was played, and its target is picked when it casts.
    // Target is the command's me / p1-p4 (circles of the caster's side) / e1-e4 (the other side's) / all, or null to
    // pick one.
    // When is 'f' for .cf (first in the round, before anyone acts) or 'l' for .cl (last, after everyone), else 'n':
    // around the caster's own card.
    // Announcement is a .ca line chained into the queue (`.c1 Fire Cat, ca 1`): it casts nothing and shows before the
    // queue's next spell.
    private sealed record DelayedCast(QueuedCombatAction Action, bool AtStart, int Round, bool Free = false, string Target = null,
                                      bool SkipCast = false, char When = 'n', string Announcement = null);
    private readonly List<DelayedCast> _delayedCasts = [];

    /// <summary>
    /// The team that plans and acts now. When set, the other team queues no moves and is not made to pass. When
    /// <c>null</c>, every team acts in the same round.
    /// </summary>
    internal CombatTeam? ActingTeam { get; set; }

    public void Reset() =>
        _queuedCombatActions = [];

    /// <summary>
    /// Applies queued combat actions to the duel, processing each action in order.
    /// </summary>
    /// <param name="combatActionListObj">The object that will hold the list of combat actions.</param>
    /// <returns>The total cinematic time for the actions applied.</returns>
    public float ApplyQueuedCombatActions(out CombatActionListObj combatActionListObj) {
        Logger.Debug("Duel {0} | Applying combat actions..", 
            Logger.Args(_duel.m_duelID.Full, _duel.m_roundNum));

        combatActionListObj = new CombatActionListObj { m_actionList = [] };

        // Some subcircles may not have queued actions. Ensure they do by adding a pass action.
        AddCasterPassActionIfNeeded();
        SortQueuedActions();

        StartHealthBeats();
        var cinematicTime = ProcessQueuedActions(combatActionListObj);
        var announced = ShowAnnouncements(combatActionListObj);
        cinematicTime += announced;
        TimeHealthBeats(announced, instant: cinematicTime <= 0 || _subCircles[0]._duelActor.CheatInstantCinematics);
        ApplyCastLooks(combatActionListObj);

        return cinematicTime;
    }

    /// <summary>
    /// A wizard's health globe (the orb at the bottom left) at a moment of the round's animations. The game only moves
    /// the globe on a health message, so it showed the health from the start of the round until the next round's
    /// combat health (Niduus, 2026-10-22: his friend's globe caught up only on the next turn). The duel sends each one
    /// as MSG_UPDATEHEALTH when its step of the round has played.
    /// </summary>
    internal readonly record struct HealthBeat(float Seconds, CombatDuelSubCircle Circle, int Health);

    /// <summary>This round's health globe changes, in the order they play.</summary>
    internal readonly List<HealthBeat> HealthBeats = [];

    // The health each wizard's globe was last given this round.
    private readonly Dictionary<CombatDuelSubCircle, int> _beatHealth = [];

    // Wizards whose globe shows their real health. A PvP duel only changes its copy (IsSandboxed).
    private IEnumerable<CombatDuelSubCircle> GlobeCircles()
        => _subCircles.Where(c => c is { Occupied: true, AddedToDuel: true, _wizard: not null, IsSandboxed: false }
            && c.ParticipantGameStats is not null);

    private void StartHealthBeats() {
        HealthBeats.Clear();
        _beatHealth.Clear();
        foreach (var circle in GlobeCircles()) {
            _beatHealth[circle] = circle.ParticipantGameStats.m_currentHitpoints;
        }
    }

    /// <summary>
    /// Notes the health of every wizard whose health changed in the step that just ended; their globe gets it once
    /// <paramref name="seconds"/> of the round have played (the end of that step's animation).
    /// </summary>
    private void NoteHealthBeat(float seconds) {
        foreach (var circle in GlobeCircles()) {
            var health = Math.Max(0, circle.ParticipantGameStats.m_currentHitpoints);
            if (_beatHealth.TryGetValue(circle, out var was) && was == health) {
                continue;
            }

            _beatHealth[circle] = health;
            HealthBeats.Add(new HealthBeat(seconds, circle, health));
        }
    }

    // The .ca lines play before the first spell, so every change comes that much later. With no cinematics they all
    // go at once, and only each wizard's last health.
    private void TimeHealthBeats(float announced, bool instant) {
        if (instant) {
            var last = HealthBeats.GroupBy(b => b.Circle).Select(g => g.Last() with { Seconds = 0 }).ToList();
            HealthBeats.Clear();
            HealthBeats.AddRange(last);

            return;
        }

        for (var i = 0; i < HealthBeats.Count; i++) {
            HealthBeats[i] = HealthBeats[i] with { Seconds = HealthBeats[i].Seconds + announced };
        }
    }

    /// <summary>
    /// .cast none: a wizard's spell actions (not their pet's) skip the casting animation. (A school's animation can't
    /// be shown: the game draws the casting sign from its own copy of the spell, Niduus's log 2026-10-20.)
    /// </summary>
    private void ApplyCastLooks(CombatActionListObj combatActionList) {
        foreach (var action in combatActionList.m_actionList) {
            if (action.m_spell is null || action.m_petCast || action.m_petCasted) {
                continue;
            }

            var caster = _subCircles.FirstOrDefault(c => c is not null && c.SlotIndex == action.m_spellCaster);
            if (caster is null || !caster.IsHumanPlayer || caster.ParticipantObject is null
                || !CastLook.TryGet(caster.ParticipantObject.m_globalID.Full, out var school)) {
                continue;
            }

            if (school == 0) {
                action.m_showCast = false;
            }
        }
    }

    // .ca (Niduus, 2026-10-20): lines shown like a boss's cheat, in the "Gained Power Pip" spot, as the round plays.
    private readonly List<(string Text, int Slot)> _announcements = [];

    /// <param name="text">The locale key to show.</param>
    /// <param name="slot">The circle of the player who typed it; the text shows over them.</param>
    internal void AddAnnouncement(string text, int slot) => _announcements.Add((text, slot));

    /// <summary>
    /// Shows the waiting .ca lines before the round's first spell. The game plays an action carrying a message
    /// (CombatAction.m_stringKeyMessage, the text boss cheats show) as an announcement and does not cast its spell
    /// (Niduus's log, 2026-10-20: his Minor Blessing never cast), so each line goes on its own copy of that action.
    /// </summary>
    /// <returns>The seconds the lines add to the round.</returns>
    private float ShowAnnouncements(CombatActionListObj combatActionList) {
        var index = combatActionList.m_actionList.FindIndex(a => a.m_spell is not null);
        if (index < 0 || _announcements.Count == 0) {
            return 0;
        }

        // The text shows over the action's caster, so each copy is cast by whoever typed .ca; it showed over the
        // enemy whose spell came first (Niduus, 2026-10-20). Their own spell is the copy when they have one.
        var spellAction = combatActionList.m_actionList[index];
        var lines = _announcements.Select(a => (combatActionList.m_actionList
                .FirstOrDefault(x => x.m_spell is not null && x.m_spellCaster == a.Slot) ?? spellAction) with {
            m_spellCaster = a.Slot,
            m_stringKeyMessage = a.Text,
            m_showCast = false,
        }).ToList();
        combatActionList.m_actionList.InsertRange(index, lines);
        Logger.Information("Duel {0} | Round {1} | .ca shows {2} before slot {3}'s spell.",
            Logger.Args(_duel.m_duelID.Full, _duel.m_roundNum, string.Join(", ", _announcements.Select(a => $"\"{a.Text}\" over slot {a.Slot}")),
                spellAction.m_spellCaster));
        var seconds = lines.Count * ANNOUNCEMENT_SECONDS;
        _announcements.Clear();

        return seconds;
    }

    /// <summary>
    /// Puts .ca lines at <paramref name="index"/>, each on its own copy of a spell action, cast by the player who
    /// typed them so the text shows over them.
    /// </summary>
    /// <returns>The seconds the lines add to the round.</returns>
    private float InsertAnnouncements(CombatActionListObj combatActionList, int index, CombatAction source, List<string> texts, int slot) {
        combatActionList.m_actionList.InsertRange(index, texts.Select(text => source with {
            m_spellCaster = slot,
            m_stringKeyMessage = text,
            m_showCast = false,
        }));
        Logger.Information("Duel {0} | Round {1} | queued .ca shows {2} over slot {3}.",
            Logger.Args(_duel.m_duelID.Full, _duel.m_roundNum, string.Join(", ", texts.Select(t => $"\"{t}\"")), slot));

        return texts.Count * ANNOUNCEMENT_SECONDS;
    }

    // How much an announcement adds to the round. The game shows it over the boss while the spell next to it plays:
    // in Niduus's log of 2026-10-22 the spell after one started straight away (Mega Taunt and its line were over in
    // 2.0 s, which waited 4 s; Minotaur Death Sun and Polymorph - Dragon NA started 0.1 s after the turn before them).
    // [Battle] FastAnimationTiming off keeps the old 2 s.
    private static readonly float ANNOUNCEMENT_SECONDS = FastAnimationTiming ? 0.5f : 2.0f;

    /// <summary>
    /// Adds a combat move to the queue for processing.
    /// </summary>
    /// <param name="type">The type of combat move (e.g., Attack, ChangeMind).</param>
    /// <param name="caster">The caster of the spell.</param>
    /// <param name="target">The target of the spell.</param>
    /// <param name="spell">The spell being cast.</param>
    public void AddCombatMove(CombatMoveType type,
                              CombatDuelSubCircle caster,
                              CombatDuelSubCircle target,
                              Spell spell) {
        if (!IsActing(caster)) {
            Logger.Debug("Duel {0} | Slot {1} | Move dropped, the other team is acting.",
                Logger.Args(_duel.m_duelID.Full, caster.SlotIndex));

            return;
        }

        // If this spell is already queued by the same caster, remove all of their queued actions.
        _queuedCombatActions.RemoveAll(x => x.SpellCaster == caster);

        if (type == CombatMoveType.ChangeMind) {
            // We can immediately return here. Anytime a caster doesn't have a queued action, they will pass their turn.
            return;
        }

        // Get the spell template.
        SpellTemplate spellTemplate = null;
        if (spell is not null) {
            spellTemplate = (SpellTemplate) CoreObjectFactory.GetCoreTemplate(spell.m_templateID);

            if (spellTemplate is null) {
                Logger.Error("Duel {0} | Slot {1} | Spell template {2} not found",
                    Logger.Args(_duel.m_duelID.Full, caster.SlotIndex, spell.m_templateID));
                return;
            }

            // An enchanted card casts with the enchantment's changes.
            if (spell.m_enchantment != 0 && CoreObjectFactory.GetCoreTemplate(spell.m_enchantment) is SpellTemplate enchantment) {
                spellTemplate = CombatEnchantments.Apply(spellTemplate, enchantment, out _);
            }
        }

        var queuedAction = new QueuedCombatAction {
            SpellCaster = caster,
            Spell = type == CombatMoveType.Attack ? spell : null,
            SpellTemplate = spellTemplate,
            SelectedTarget = target
        };
        _queuedCombatActions.Add(queuedAction);

        LogQueuedCombatAction(type, caster, target, spell);
    }

    /// <summary>
    /// Checks if all participants have enqueued actions.
    /// </summary>
    /// <returns>True if all participants have enqueued actions, false otherwise.</returns>
    public bool HaveAllParticipantsEnqueuedActions() {
        // Every circle that still has to move must have a move of its own. Counting is not enough: a move
        // from a circle that is not expected (a dead player's client still passes) would stand in for
        // someone else's and end the round early, as if they had passed too.
        return _subCircles
            .Where(circle => circle.AddedToDuel && circle.IsAlive && IsActing(circle))
            .All(circle => _queuedCombatActions.Any(action => action.SpellCaster == circle));
    }

    /// <summary>The move a given caster has queued this round, or null if none. Used to re-broadcast a
    /// summoned minion's AI-chosen move at planning-phase start so the client telegraphs it.</summary>
    public QueuedCombatAction GetQueuedAction(CombatDuelSubCircle caster)
        => _queuedCombatActions.FirstOrDefault(a => a.SpellCaster == caster);

    private bool IsActing(CombatDuelSubCircle circle)
        => ActingTeam is null || circle.OccupiedTeam == ActingTeam;

    private void AddCasterPassActionIfNeeded() {
        var castersWithoutActions = ActiveSubCircles
            .Where(subCircle => subCircle.AddedToDuel && subCircle.IsAlive && IsActing(subCircle))
            .Except(_queuedCombatActions.Select(action => action.SpellCaster))
            .ToList();

        foreach (var subCircle in castersWithoutActions) {
            var queuedAction = new QueuedCombatAction {
                SpellCaster = subCircle,
                SelectedTarget = null,
                Spell = null
            };
            _queuedCombatActions.Add(queuedAction);
        }
    }

    private void SortQueuedActions() => _queuedCombatActions.Sort((a, b) => {
        var aSlot = a.SpellCaster.SlotIndex;
        var bSlot = b.SpellCaster.SlotIndex;

        var aTeam = (int) a.SpellCaster.OccupiedTeam;
        var bTeam = (int) b.SpellCaster.OccupiedTeam;

        // Check if both actions belong to the same team
        if (aTeam == bTeam) {
            // Within the same team, sort by slot index (ascending)
            return aSlot.CompareTo(bSlot);
        }
        else {
            // Teams are different, prioritize team who acts first
            if (aTeam == _duel.m_firstTeamToAct) {
                return -1; // Team a acts first
            }
            else if (bTeam == _duel.m_firstTeamToAct) {
                return 1; // Team b acts first
            }
            else {
                return 0; // This should not happen.
            }
        }
    });

    private float ProcessQueuedActions(CombatActionListObj combatActionList) {
        var cinematicTime = 0.0f;
        var instantCinematics = _subCircles[0]._duelActor.CheatInstantCinematics;

        _petCastsThisRound.Clear();
        PetCastCards.Clear();
        // A form taken last round right after an afterlife, shown now (CombatResolver.Afterlife.cs).
        cinematicTime += ReplayPolymorphs(combatActionList, cinematicTime);
        // .cf (Niduus, 2026-10-22; was .c1 f / .c2 f): the queue casts before anyone's turn, then the round goes on.
        cinematicTime += CastEveryonesDelayed('f', combatActionList, cinematicTime);
        NoteHealthBeat(cinematicTime);
        // Boss cheats for the start of the round (BossCheats.cs).
        cinematicTime += BossCheatsAtRoundStart(combatActionList, cinematicTime);
        ProcessAfterlives();
        NoteHealthBeat(cinematicTime);
        foreach (var action in _queuedCombatActions) {
            cinematicTime += WithPetMayCasts(() => CastDelayed(action.SpellCaster, atStart: true, combatActionList, cinematicTime),
                combatActionList, cinematicTime);
            NoteHealthBeat(cinematicTime);
            // A boss cheat that interrupts goes off before the spell it answers.
            cinematicTime += BossCheatsBefore(action, combatActionList, cinematicTime);
            NoteHealthBeat(cinematicTime);
            var before = combatActionList.m_actionList.Count;
            cinematicTime += WithPetMayCasts(() => ProcessQueuedAction(action, combatActionList, cinematicTime),
                combatActionList, cinematicTime);
            // A participant defeated by that move comes back if an afterlife waits on them (CombatResolver.Afterlife.cs).
            ProcessAfterlives();
            NoteHealthBeat(cinematicTime);
            // A boss answers the move that was just made (a player's cast or pass, or its health falling).
            cinematicTime += BossCheatsAfter(action.SpellCaster, before, combatActionList, cinematicTime);
            NoteHealthBeat(cinematicTime);
            cinematicTime += WithPetMayCasts(() => CastDelayed(action.SpellCaster, atStart: false, combatActionList, cinematicTime),
                combatActionList, cinematicTime);
            NoteHealthBeat(cinematicTime);
        }
        // Boss cheats for the end of the round, after everyone's move.
        cinematicTime += BossCheatsAtRoundEnd(combatActionList, cinematicTime);
        NoteHealthBeat(cinematicTime);
        // .cl (was .c1 l / .c2 l): the queue casts after everyone's turn, then the round ends.
        cinematicTime += CastEveryonesDelayed('l', combatActionList, cinematicTime);
        // Malistaire went invisible (Niduus, 2026-10-22): a participant defeated by a round-end cheat or a last (.cl)
        // cast was never given its afterlife, so the round's end took him out of the battle (MSG_COMBATDEATH and
        // MSG_COMBATREMOVE) while the game, which works out the afterlife itself, still had him standing there.
        ProcessAfterlives();
        NoteHealthBeat(cinematicTime);

        return instantCinematics ? 0 : cinematicTime;
    }

    // Casts every caster's f (first) or l (last) queue, c1 before c2 for each caster.
    private float CastEveryonesDelayed(char when, CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        var cinematicTime = 0.0f;
        var casters = _delayedCasts.Where(d => d.When == when && d.Round < _duel.m_roundNum)
            .Select(d => d.Action.SpellCaster).Distinct().ToList();
        foreach (var caster in casters) {
            foreach (var atStart in new[] { true, false }) {
                cinematicTime += WithPetMayCasts(
                    () => CastDelayed(caster, atStart, combatActionList, cinematicTimeSoFar + cinematicTime, when),
                    combatActionList, cinematicTimeSoFar + cinematicTime);
            }
        }

        return cinematicTime;
    }

    // How many May Casts each owner's pet has done this round ([PetCasting] MaxPerRound).
    private readonly Dictionary<CombatDuelSubCircle, int> _petCastsThisRound = [];

    /// <summary>
    /// This round's pet May Cast cards per owner. The game casts only cards it finds in the caster's hand (it skipped
    /// every other way: "does not spell in hand"; a pet item card in hand played with the pet's animation, Niduus's
    /// log 2026-10-20), so the duel sends the owner a hand holding them before the round's actions.
    /// </summary>
    internal readonly Dictionary<CombatDuelSubCircle, List<Spell>> PetCastCards = [];

    /// <summary>
    /// Runs one step of the round, then lets pets May Cast for what happened in it: a spell cast by a teammate (or
    /// the owner), a teammate taking damage, or a teammate being healed, pet casts included ([PetCasting] Chain).
    /// </summary>
    private float WithPetMayCasts(Func<float> step, CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        if (!PetMayCasts.Enabled || PetMayCasts.MaxPerRound == 0) {
            return step();
        }

        var health = HealthNow();
        var before = combatActionList.m_actionList.Count;
        var cinematicTime = step();

        try {
            // A May Cast can set off more May Casts, the pet's own or another pet's (Niduus, 2026-10-20), up to
            // [PetCasting] MaxChain links; MaxPerRound still caps each pet.
            for (var link = 0; link <= PetMayCasts.MaxChain; link++) {
                // The pet casts about to be added are the next link's step.
                var nextHealth = HealthNow();
                var nextBefore = combatActionList.m_actionList.Count;
                var time = PetMayCast(health, before, combatActionList, cinematicTimeSoFar + cinematicTime);
                if (time <= 0) {
                    break;
                }

                cinematicTime += time;
                if (!PetMayCasts.Chain) {
                    break;
                }

                health = nextHealth;
                before = nextBefore;
            }
        }
        catch (Exception e) {
            Logger.Error("Duel {0} | Pet May Cast failed: {1}", Logger.Args(_duel.m_duelID.Full, e));
        }

        return cinematicTime;
    }

    private bool BattleDecided() {
        var standing = _subCircles.Where(c => c.Occupied && c.AddedToDuel && c.IsAlive).Select(c => c.OccupiedTeam).Distinct();

        return standing.Count() < 2;
    }

    private Dictionary<CombatDuelSubCircle, int> HealthNow()
        => _subCircles.Where(c => c.Occupied && c.AddedToDuel && c.ParticipantGameStats is not null)
            .ToDictionary(c => c, c => c.ParticipantGameStats.m_currentHitpoints);

    private float PetMayCast(Dictionary<CombatDuelSubCircle, int> health, int before, CombatActionListObj combatActionList,
                             float cinematicTimeSoFar) {
        var cast = new HashSet<CombatTeam>();
        var damaged = new HashSet<CombatTeam>();
        var healed = new HashSet<CombatTeam>();
        var passed = new HashSet<CombatTeam>();
        for (var i = before; i < combatActionList.m_actionList.Count; i++) {
            var done = combatActionList.m_actionList[i];
            // Passing counts too (Niduus, 2026-10-20).
            if (done.m_spell is null
                    && _subCircles.FirstOrDefault(c => c.Occupied && c.SlotIndex == done.m_spellCaster) is { } passer) {
                passed.Add(passer.OccupiedTeam);
            }

            if (done.m_spell is not null && done.m_spellHits == (char) 1 && (PetMayCasts.Chain || !done.m_petCast)
                    && _subCircles.FirstOrDefault(c => c.Occupied && c.SlotIndex == done.m_spellCaster) is { } caster) {
                cast.Add(caster.OccupiedTeam);

                // A damage or heal spell landing on a teammate counts even when the server's health did not move:
                // gear can take all of a hit's damage here while the game still shows it (Niduus's log, 2026-10-20).
                var effects = (CoreObjectFactory.GetCoreTemplate(done.m_spell.m_templateID) as SpellTemplate)?.m_effects ?? [];
                var damages = effects.Any(e => e is not null && s_damageEffects.Contains(e.m_effectType));
                var heals = effects.Any(e => e is not null && s_healEffects.Contains(e.m_effectType));
                foreach (var slot in done.m_targetSubcircleList ?? []) {
                    if (_subCircles.FirstOrDefault(c => c.Occupied && c.SlotIndex == slot) is not { } hit) {
                        continue;
                    }

                    if (damages) {
                        damaged.Add(hit.OccupiedTeam);
                    }

                    if (heals) {
                        healed.Add(hit.OccupiedTeam);
                    }
                }
            }
        }

        foreach (var (circle, hitpoints) in health) {
            var now = circle.ParticipantGameStats?.m_currentHitpoints ?? hitpoints;
            if (now < hitpoints) {
                damaged.Add(circle.OccupiedTeam);
            }
            else if (now > hitpoints) {
                healed.Add(circle.OccupiedTeam);
            }
        }

        if (cast.Count + damaged.Count + healed.Count + passed.Count == 0) {
            return 0;
        }

        // A won or lost battle ends without more May Casts, which only made its end wait (Niduus, 2026-10-20).
        if (BattleDecided()) {
            return 0;
        }

        var cinematicTime = 0.0f;
        var owners = _subCircles.Where(c => c.Occupied && c.AddedToDuel && c.IsAlive && c._wizard is not null).ToList();
        foreach (var owner in owners) {
            var team = owner.OccupiedTeam;
            if (!cast.Contains(team) && !damaged.Contains(team) && !healed.Contains(team) && !passed.Contains(team)) {
                continue;
            }

            var used = _petCastsThisRound.GetValueOrDefault(owner);
            foreach (var mayCast in PetMayCasts.ForWizard(owner._wizard).OrderBy(_ => Random.Shared.Next())) {
                if (used >= PetMayCasts.MaxPerRound || !owner.IsAlive) {
                    break;
                }

                var chances = PetMayCasts.Chances(mayCast);
                var reason = cast.Contains(team) && PetMayCasts.Roll(chances.Cast) ? "a cast"
                    : damaged.Contains(team) && PetMayCasts.Roll(chances.Damage) ? "damage"
                    : healed.Contains(team) && PetMayCasts.Roll(chances.Heal) ? "a heal"
                    : passed.Contains(team) && PetMayCasts.Roll(chances.Pass) ? "a pass"
                    : null;
                if (reason is null) {
                    continue;
                }

                var time = CastPetSpell(owner, mayCast, reason, combatActionList, cinematicTimeSoFar + cinematicTime);
                if (time > 0) {
                    cinematicTime += time;
                    used++;
                }
            }

            _petCastsThisRound[owner] = used;
        }

        return cinematicTime;
    }

    private static readonly HashSet<kSpellEffects> s_damageEffects = [
        kSpellEffects.kDamage, kSpellEffects.kDamageNoCrit, kSpellEffects.kDamageOverTime, kSpellEffects.kMaxHealthDamage,
        kSpellEffects.kStealHealth, kSpellEffects.kDamagePerTotalPipPower, kSpellEffects.kMinionRankDamage,
        kSpellEffects.kDeferredDamage, kSpellEffects.kDivideDamage,
    ];

    private static readonly HashSet<kSpellEffects> s_healEffects = [
        kSpellEffects.kHeal, kSpellEffects.kHealOverTime, kSpellEffects.kHealPercent, kSpellEffects.kMaxHealthHeal,
        kSpellEffects.kSetHealPercent,
    ];

    private float CastPetSpell(CombatDuelSubCircle owner, PetMayCasts.MayCast mayCast, string reason,
                               CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        // As the pet's item card only with [PetCasting] AsItemCard (Niduus, 2026-10-20, wanted that look): flagged so,
        // the game still skipped the cast ("does not spell in hand").
        var test = PetMayCasts.TestMode is not null;
        var normal = test && PetMayCasts.TestNormalCard ? PetMayCasts.NormalCard(mayCast) : null;
        var card = normal is not null ? SpellFactory.GetSpell(normal) : SpellFactory.GetSpell(mayCast.SpellTemplateId);
        // Free: the game takes a card's pips off the caster when it plays it, so the pet's copy costs none (Niduus's
        // pips went down for every May Cast, 2026-10-20).
        var spell = card is null ? null : (!test && PetMayCasts.AsItemCard ? card with { m_itemCard = true } : card)
            with { m_pipCost = new SpellRank() };
        if (spell is null || CoreObjectFactory.GetCoreTemplate(spell.m_templateID) is not SpellTemplate template) {
            return 0;
        }

        // On the owner; a card that hits enemies goes at one.
        var target = PickFreeTarget(owner, template, null);
        if (target is null || !target.IsAlive || !target.AddedToDuel) {
            return 0;
        }

        Logger.Information("Duel {0} | Slot {1} | Pet May Cast {2} casts {3} on slot {4} after {5}{6}.",
            Logger.Args(_duel.m_duelID.Full, owner.SlotIndex, mayCast.Talent, normal ?? mayCast.SpellName, target.SlotIndex,
                reason, test ? $" (test {PetMayCasts.TestMode}: {PetMayCasts.TestModes[PetMayCasts.TestMode.Value]})" : ""));
        owner._duelActor.CurrentActionCinematicOffsetSeconds = cinematicTimeSoFar;
        var action = new QueuedCombatAction { SpellCaster = owner, SelectedTarget = target, Spell = spell, SpellTemplate = template };
        var before = combatActionList.m_actionList.Count;
        // The pet's card plays as the owner's own cast, with the whole casting animation: Pet - Sleet Storm - Trigger took
        // 7.3 to 8.0 s every time in Niduus's log of 2026-10-22 (6 rounds), while the round waited [PetCasting]
        // CastSeconds (4 s), so the turns after it started early. With [Battle] FastAnimationTiming the measured
        // animation (8.0 s for Sleet Storm) is waited; CastSeconds caps the old, longer estimates only.
        var full = HandleSuccessfulAction(action, combatActionList, alreadyPaid: true);
        var cinematicTime = FastAnimationTiming ? full : Math.Min(full, PetMayCasts.CastSeconds);
        ShowInHand(owner, spell, template, combatActionList, before);
        // Sent as a plain cast, like the .db/.nb free casts the game plays. Flagged forced, as an item card or as the
        // pet's, the game skipped it ("does not spell in hand"; Niduus's logs, 2026-10-20). The flags stay as tests.
        for (var i = before; i < combatActionList.m_actionList.Count; i++) {
            combatActionList.m_actionList[i].m_forceSpell = test ? PetMayCasts.TestForced : PetMayCasts.ForceSpell;
            if (test ? PetMayCasts.TestPetFlags : PetMayCasts.ShowAsPetCast) {
                combatActionList.m_actionList[i].m_petCast = true;
                combatActionList.m_actionList[i].m_petCasted = true;
                combatActionList.m_actionList[i].m_petCastTarget = target.SlotIndex;
            }
        }

        return cinematicTime;
    }

    /// <summary>
    /// Puts a free cast (pet May Cast, .c1/.c2) into its caster's hand for the game, which skips spells it can't find
    /// there ("does not spell in hand"), and takes its pip cost off the copy the game plays. The rank comes off after
    /// resolving, because the server also reads m_regularAdjust as a damage/heal bonus.
    /// </summary>
    private void ShowInHand(CombatDuelSubCircle owner, Spell spell, SpellTemplate template, CombatActionListObj combatActionList, int before) {
        if (spell is null) {
            return;
        }

        if (!PetCastCards.TryGetValue(owner, out var cards)) {
            PetCastCards[owner] = cards = [];
        }

        var rank = template?.m_spellRank?.m_spellRank ?? 0;
        if (PetMayCasts.ZeroCost && rank > 0) {
            var free = spell with { m_regularAdjust = spell.m_regularAdjust - rank };
            for (var i = before; i < combatActionList.m_actionList.Count; i++) {
                if (ReferenceEquals(combatActionList.m_actionList[i].m_spell, spell)) {
                    combatActionList.m_actionList[i].m_spell = free;
                }
            }

            spell = free;
        }

        cards.Add(spell);
    }

    /// <summary>
    /// .daybreaker / .nightbringer: queues a spell that casts for free this round, at the start or the end of the
    /// caster's turn. Several can be queued to chain them.
    /// </summary>
    /// <returns>How many of these the caster has queued for this round, or 0 if the spell is unknown.</returns>
    internal int AddFreeDelayedCast(CombatDuelSubCircle caster, uint spellTemplateId, bool atStart, string target = null,
                                    bool skipCast = false, char when = 'n', bool enchant = false) {
        return AddFreeDelayedCast(caster, spellTemplateId, atStart, out _, target, skipCast, when, enchant);
    }

    // .c1 e (Niduus, 2026-10-20): the first of these enchantments that changes the spell goes on it.
    private static readonly string[] s_autoEnchants = Imlight.Common.ConfigurationManager
        .GetValue("Battle.AutoEnchants", "Epic; Primordial; Sharpened Blade; Potent Trap; Potent Ward - Amulet; Aegis; Indemnity")
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <param name="enchantment">The enchantment the e option put on the spell, or null.</param>
    internal int AddFreeDelayedCast(CombatDuelSubCircle caster, uint spellTemplateId, bool atStart, out string enchantment,
                                    string target = null, bool skipCast = false, char when = 'n', bool enchant = false,
                                    IReadOnlyList<string> enchants = null) {
        enchantment = null;
        var spell = SpellFactory.GetSpell(spellTemplateId);
        if (spell is null || CoreObjectFactory.GetCoreTemplate(spellTemplateId) is not SpellTemplate template) {
            return 0;
        }

        // Player cheats' picked enchantments (Niduus, 2026-10-22: "if enchant, allow select(s)"): each one that changes
        // the spell goes on, in order, so several can stack. The card shows the first.
        var picked = enchant ? (enchants ?? []).Where(name => !string.IsNullOrWhiteSpace(name)).ToList() : [];
        foreach (var name in picked) {
            var enchantSpell = SpellFactory.Exists(name) ? SpellFactory.GetSpell(name) : null;
            if (enchantSpell is null || CoreObjectFactory.GetCoreTemplate(enchantSpell.m_templateID) is not SpellTemplate enchantTemplate
                || !CombatEnchantments.IsEnchantment(enchantTemplate)) {
                Logger.Warning("Free cast {0}: \"{1}\" is not an enchantment, so it was left off.", Logger.Args(spellTemplateId, name));
                continue;
            }

            var stacked = CombatEnchantments.Apply(template, enchantTemplate, out var changed);
            if (!changed) {
                continue;
            }

            if (spell.m_enchantment == 0) {
                CombatEnchantments.Enchant(spell, enchantSpell);
            }

            template = stacked;
            spell.m_accuracy = (byte) Math.Clamp(template.m_accuracy, 0, 100);
            enchantment = enchantment is null ? name : $"{enchantment} + {name}";
        }

        if (enchant && picked.Count == 0) {
            foreach (var name in s_autoEnchants) {
                var enchantSpell = SpellFactory.Exists(name) ? SpellFactory.GetSpell(name) : null;
                var enchanted = spell with { };
                if (enchantSpell is not null && CombatEnchantments.Enchant(enchanted, enchantSpell)
                    && CoreObjectFactory.GetCoreTemplate(enchantSpell.m_templateID) is SpellTemplate enchantTemplate) {
                    spell = enchanted;
                    template = CombatEnchantments.Apply(template, enchantTemplate, out _);
                    enchantment = name;
                    break;
                }
            }
        }

        var action = new QueuedCombatAction { SpellCaster = caster, Spell = spell, SpellTemplate = template };
        _delayedCasts.Add(new DelayedCast(action, atStart, (int) _duel.m_roundNum - 1, Free: true, Target: target, SkipCast: skipCast,
                                       When: when));

        return _delayedCasts.Count(d => d.Free && d.Announcement is null && d.Action.SpellCaster == caster && d.AtStart == atStart && d.When == when);
    }

    /// <summary>
    /// Queues a .ca line in the caster's c1 or c2 queue, in its place among the queued spells.
    /// </summary>
    internal void AddDelayedAnnouncement(CombatDuelSubCircle caster, string text, bool atStart, char when) {
        var action = new QueuedCombatAction { SpellCaster = caster };
        _delayedCasts.Add(new DelayedCast(action, atStart, (int) _duel.m_roundNum - 1, Free: true, When: when, Announcement: text));
    }

    private static readonly HashSet<kEffectTarget> s_enemyTargets = [
        kEffectTarget.kEnemySingle, kEffectTarget.kEnemyTeam, kEffectTarget.kEnemyTeamAllAtOnce,
        kEffectTarget.kAtLeastOneEnemy, kEffectTarget.kPreselectedEnemySingle, kEffectTarget.kMultiTargetEnemy,
        kEffectTarget.kEnemyMinion,
    ];

    /// <summary>
    /// The circle a free cast's pN (circle N of the caster's side) or eN (circle N of the other side) names, in the
    /// order the game numbers each side's circles (slot order: the creature side's symbols 1-4, the other side's
    /// 13-16), occupied or not. Null for any other code.
    /// </summary>
    internal static CombatDuelSubCircle FreeTargetCircle(CombatDuelSubCircle caster, string code) {
        if (caster is null || code is not { Length: 2 } || code[0] is not ('p' or 'e') || code[1] is < '1' or > '4') {
            return null;
        }

        var ownSide = code[0] == 'p';
        var side = caster._duelActor.SubCircles
            .Where(c => c is not null && (c.SlotType == caster.SlotType) == ownSide)
            .OrderBy(c => c.SlotIndex).ToList();
        var number = code[1] - '1';

        return number < side.Count ? side[number] : null;
    }

    /// <summary>
    /// A free delayed spell's target: for a spell aimed at enemies, the enemy the caster picked this round or else
    /// the first one standing; for anything else, the caster.
    /// </summary>
    private CombatDuelSubCircle PickFreeTarget(CombatDuelSubCircle caster, SpellTemplate template, string code) {
        // me is the caster. pN is whoever stands on circle N of the caster's side and eN on circle N of the other
        // side, in the game's order (Niduus, 2026-10-22: p1 was the caster, so his friend on circle 1 was never hit).
        // An empty circle casts nothing: the command already told the caster who stands where.
        if (code == "me") {
            return caster;
        }

        if (FreeTargetCircle(caster, code) is { } circle) {
            return circle.Occupied ? circle : null;
        }

        if (!(template.m_effects ?? []).Any(effect => effect is not null && s_enemyTargets.Contains(effect.m_effectTarget))) {
            return caster;
        }

        var picked = _queuedCombatActions.FirstOrDefault(a => a.SpellCaster == caster)?.SelectedTarget;
        if (picked is not null && picked.IsAlive && picked.AddedToDuel && picked.OccupiedTeam != caster.OccupiedTeam) {
            return picked;
        }

        return caster._duelActor.ActiveSubCircles
            .FirstOrDefault(c => c.IsAlive && c.AddedToDuel && c.OccupiedTeam != caster.OccupiedTeam);
    }

    /// <summary>
    /// Casts the caster's Daybreaker (<paramref name="atStart"/>) or Nightbringer spells from an earlier round.
    /// </summary>
    private float CastDelayed(CombatDuelSubCircle caster, bool atStart, CombatActionListObj combatActionList, float cinematicTimeSoFar,
                              char when = 'n') {
        var due = _delayedCasts
            .Where(d => d.Action.SpellCaster == caster && d.AtStart == atStart && d.When == when && d.Round < _duel.m_roundNum)
            .ToList();
        var cinematicTime = 0.0f;
        var lines = new List<string>();
        foreach (var delayed in due) {
            _delayedCasts.Remove(delayed);
            if (delayed.Announcement is not null) {
                lines.Add(delayed.Announcement);
                continue;
            }

            var action = delayed.Action;
            // "all" (Niduus, 2026-10-20): a single-target spell casts once on each enemy (or each teammate).
            var targets = delayed.Free && delayed.Target == "all"
                ? AllFreeTargets(caster, action.SpellTemplate)
                : [delayed.Free ? PickFreeTarget(caster, action.SpellTemplate, delayed.Target) : action.SelectedTarget];
            foreach (var target in targets) {
                var revive = target is not null && !target.IsAlive && IsRevive(action.SpellTemplate)
                             && target.OccupiedTeam == caster.OccupiedTeam;
                if (!caster.IsAlive || target is null || (!target.IsAlive && !revive) || !target.AddedToDuel) {
                    continue;
                }

                try {
                    Logger.Information("Duel {0} | Slot {1} | {2} casts its delayed spell {3}{4}.",
                        Logger.Args(_duel.m_duelID.Full, caster.SlotIndex, atStart ? "Daybreaker" : "Nightbringer",
                            action.Spell?.m_templateID, delayed.Free ? " (command)" : ""));
                    caster._duelActor.CurrentActionCinematicOffsetSeconds = cinematicTimeSoFar + cinematicTime;

                    // The card goes out without its enchantment, or the game would play the delay a second time. Its
                    // effects keep the enchantment's changes (they come from the queued spell template).
                    var cast = new QueuedCombatAction {
                        SpellCaster = action.SpellCaster,
                        SelectedTarget = target,
                        // A command's free cast keeps the e option's enchantment: it never carries a delay.
                    Spell = delayed.Free ? action.Spell : DelayedCard(action.Spell),
                        SpellTemplate = action.SpellTemplate,
                    };
                    var before = combatActionList.m_actionList.Count;
                    var time = HandleSuccessfulAction(cast, combatActionList, alreadyPaid: true);
                    // Chained .ca lines show just before this spell.
                    if (lines.Count > 0 && before < combatActionList.m_actionList.Count) {
                        cinematicTime += InsertAnnouncements(combatActionList, before, combatActionList.m_actionList[before], lines, caster.SlotIndex);
                        before += lines.Count;
                        lines.Clear();
                    }
                    // .c1s/.c2s (Niduus, 2026-10-20): the game is told not to show the cast, so the wait is shorter.
                    if (delayed.SkipCast) {
                        for (var i = before; i < combatActionList.m_actionList.Count; i++) {
                            combatActionList.m_actionList[i].m_showCast = false;
                        }

                        time = Math.Min(time, SkipCastSeconds);
                    }

                    cinematicTime += time;
                    // A command's free cast is in no hand, so the game skipped it when a card had been played (Niduus's
                    // log, 2026-10-20) while the next round still waited for it.
                    if (delayed.Free) {
                        ShowInHand(caster, cast.Spell, action.SpellTemplate, combatActionList, before);
                    }
                    // The spell going off when the delay runs out. A command's free cast had no enchanted card on the
                    // table, so the game is shown it as a normal cast; flagged, it waits for a card it never saw.
                    for (var i = before; !delayed.Free && i < combatActionList.m_actionList.Count; i++) {
                        combatActionList.m_actionList[i].m_delayedEnchanted = true;
                    }
                }
                catch (Exception e) {
                    Logger.Error("Duel {0} | Slot {1} | delayed spell failed: {2}",
                        Logger.Args(_duel.m_duelID.Full, caster.SlotIndex, e));
                }
            }
        }

        // Lines with no spell after them show after the spell before them, or else before the round's first spell.
        if (lines.Count > 0) {
            var last = combatActionList.m_actionList.FindLastIndex(a => a.m_spell is not null);
            if (last >= 0) {
                cinematicTime += InsertAnnouncements(combatActionList, last + 1, combatActionList.m_actionList[last], lines, caster.SlotIndex);
            }
            else {
                lines.ForEach(line => AddAnnouncement(line, caster.SlotIndex));
            }
        }

        return cinematicTime;
    }

    private static readonly HashSet<kEffectTarget> s_singleEnemy = [
        kEffectTarget.kEnemySingle, kEffectTarget.kPreselectedEnemySingle, kEffectTarget.kAtLeastOneEnemy,
        kEffectTarget.kMultiTargetEnemy, kEffectTarget.kEnemyMinion,
    ];

    private static readonly HashSet<kEffectTarget> s_singleFriend = [
        kEffectTarget.kFriendlySingle, kEffectTarget.kFriendlySingleNotMe, kEffectTarget.kSelf,
        kEffectTarget.kMultiTargetFriendly, kEffectTarget.kFriendlyMinion,
    ];

    /// <summary>
    /// The targets of an "all" free cast: every enemy standing for a single-enemy spell, every teammate standing for
    /// a single-friend spell, else one cast as usual (the spell already hits everyone it can).
    /// </summary>
    private List<CombatDuelSubCircle> AllFreeTargets(CombatDuelSubCircle caster, SpellTemplate template) {
        var effects = (template.m_effects ?? []).Where(effect => effect is not null).ToList();
        // A revive (Guardian Spirit) goes on fallen teammates too.
        var revive = IsRevive(template);
        var standing = caster._duelActor.ActiveSubCircles
            .Where(c => c.AddedToDuel && (c.IsAlive || revive && c.OccupiedTeam == caster.OccupiedTeam)).OrderBy(c => c.SlotIndex);
        if (effects.Any(effect => s_singleEnemy.Contains(effect.m_effectTarget))) {
            return [.. standing.Where(c => c.OccupiedTeam != caster.OccupiedTeam)];
        }

        if (effects.Any(effect => s_singleFriend.Contains(effect.m_effectTarget))) {
            return [.. standing.Where(c => c.OccupiedTeam == caster.OccupiedTeam)];
        }

        return [PickFreeTarget(caster, template, null)];
    }

    // A spell that brings a defeated participant back: it has an afterlife (kAfterlife), at the top or in a list.
    internal static bool IsRevive(SpellTemplate template)
        => template?.m_effects?.Any(e => e is not null && (e.m_effectType == kSpellEffects.kAfterlife
            || e is EffectListSpellEffect list && (list.m_effectList ?? []).Any(x => x?.m_effectType == kSpellEffects.kAfterlife))) == true;

    private float ProcessQueuedAction(QueuedCombatAction action, CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        var cinematicTime = 0.0f;
        {
            // If the caster is dead, skip this action.
            if (!action.SpellCaster.IsAlive) {
                Logger.Debug("Duel {0} | Slot {1} | Caster is dead. Skipping action.",
                    Logger.Args(_duel.m_duelID.Full, action.SpellCaster.SlotIndex));

                return cinematicTime;
            }

            // We want to invoke the overtime effects after we check for death.
            // This is because the overtime effects can kill a participant, and we want to see the animation.
            cinematicTime += InvokeOverTimeEffects(action.SpellCaster);

            // A null spell indicates the caster is passing their turn.
            if (action.Spell is null || action.SelectedTarget is null) {
                Logger.Debug("Duel {0} | Slot {1} | Caster is passing their turn.",
                    Logger.Args(_duel.m_duelID.Full, action.SpellCaster.SlotIndex));

                cinematicTime += HandlePassAction(action, combatActionList);

                return cinematicTime;
            }

            // If our target is gone or we're stunned, pass the turn. A revive (an afterlife, such as Guardian Spirit) may
            // go on a defeated teammate.
            var revivesFallenAlly = !action.SelectedTarget.IsAlive && IsRevive(action.SpellTemplate)
                                    && action.SelectedTarget.OccupiedTeam == action.SpellCaster.OccupiedTeam;
            if (    action.SpellCaster.CombatParticipant.m_stunned > 0 
                || (!action.SelectedTarget.IsAlive && !revivesFallenAlly)
                || !action.SelectedTarget.AddedToDuel) {
                // Only a stunned caster loses a round of stun (it went below zero for anyone whose target fell).
                if (action.SpellCaster.CombatParticipant.m_stunned > 0) {
                    action.SpellCaster.CombatParticipant.m_stunned--;
                }

                cinematicTime += HandlePassAction(action, combatActionList);

                Logger.Debug("Duel {0} | Slot {1} | Spell cannot occur because target is dead or caster is stunned.",
                    Logger.Args(_duel.m_duelID.Full, action.SpellCaster.SlotIndex));

                return cinematicTime;
            }

            // A boss cheat interrupted it: it fizzles and the card is used up, as a fizzle.
            if (action.Interrupted) {
                cinematicTime += HandleFizzleAction(action, combatActionList);
                UseUpCard(action.SpellCaster, action.Spell);
                Logger.Information("Duel {0} | Slot {1} | A boss cheat interrupts the spell.",
                    Logger.Args(_duel.m_duelID.Full, action.SpellCaster.SlotIndex));

                return cinematicTime;
            }

            // Determine if this spell hits or fizzles.
            var spellHits = SpellHits(action.SpellCaster, action.Spell);
            if (!spellHits) {
                cinematicTime += HandleFizzleAction(action, combatActionList);

                // Increase pips used counter by 1, even if the spell fizzled.
                action.SpellCaster._usedPipsForExperienceGain++;

                if (s_manaOnFizzle && action.Spell is not null) {
                    action.SpellCaster.DeductMana(action.Spell.m_pipCost.m_spellRank);
                }
            }
            else if (IsDelayCast(action.Spell, out var atStart)) {
                // The enchanted card is cast now, which plays the Daybreaker or Nightbringer delay; the game reads
                // an action whose spell carries that enchantment as the delay itself. The card's own spell lands on
                // the caster's next turn. It is paid for now.
                var played = InitializeCombatAction(action);
                played.m_targetSubcircleList.Add(action.SelectedTarget.SlotIndex);
                // Marks this as the delay itself. With no flag the game cast the card's spell now and again when the
                // delay ran out (two Tower Shields); with m_delayedEnchanted it cast it now. (Niduus, 2026-10-19)
                played.m_delayed = true;
                DoSpellCastConsequences(action.SpellCaster, played);
                combatActionList.m_actionList.Add(played);
                _delayedCasts.Add(new DelayedCast(action, atStart, (int) _duel.m_roundNum));
                Logger.Information("Duel {0} | Slot {1} | {2} delays the spell to the caster's next turn.",
                    Logger.Args(_duel.m_duelID.Full, action.SpellCaster.SlotIndex, atStart ? "Daybreaker" : "Nightbringer"));
                cinematicTime += GetActionCinematicTime(action);
            }
            else {
                // Record when this caster's cinematic begins so a summoned minion appears with its cast.
                action.SpellCaster._duelActor.CurrentActionCinematicOffsetSeconds = cinematicTimeSoFar + cinematicTime;
                cinematicTime += HandleSuccessfulAction(action, combatActionList);
            }
        }

        return cinematicTime;
    }

    /// <summary>
    /// True for a card enchanted with any Daybreaker or Nightbringer (every tier), and for the game's ready-made
    /// delayed spells such as "Frost Beetle - Nightbringer" or "Imp - Daybreaker".
    /// </summary>
    private static bool IsDelayCast(Spell spell, out bool atStart) {
        atStart = false;
        if (spell is null) {
            return false;
        }

        if (spell.m_enchantment != 0
            && CoreObjectFactory.GetCoreTemplate(spell.m_enchantment) is SpellTemplate enchantment
            && CombatEnchantments.IsDaybreakerOrNightbringer(enchantment)) {
            atStart = enchantment.m_name.ToString()?.Contains("Daybreaker", StringComparison.OrdinalIgnoreCase) == true;

            return true;
        }

        if (DelayedBaseName(spell) is not null) {
            atStart = CoreObjectFactory.GetCoreTemplate(spell.m_templateID) is SpellTemplate own
                && own.m_name.ToString()?.EndsWith("Daybreaker", StringComparison.OrdinalIgnoreCase) == true;

            return true;
        }

        return false;
    }

    /// <summary>
    /// For a ready-made delayed spell ("Frost Beetle - Nightbringer"), the name of the card it casts.
    /// </summary>
    private static string DelayedBaseName(Spell spell) {
        if (CoreObjectFactory.GetCoreTemplate(spell.m_templateID) is not SpellTemplate own) {
            return null;
        }

        var name = own.m_name.ToString() ?? "";
        foreach (var suffix in new[] { " - Nightbringer", " - Daybreaker" }) {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) {
                return name[..^suffix.Length];
            }
        }

        return null;
    }

    /// <summary>
    /// The card as it is shown when the delayed spell lands: without the enchantment, or as its base card, so the
    /// game does not play the delay a second time.
    /// </summary>
    private static Spell DelayedCard(Spell spell) {
        if (spell.m_enchantment != 0) {
            return spell with { m_enchantment = 0 };
        }

        return DelayedBaseName(spell) is { } baseName && SpellFactory.GetSpell(baseName) is { } baseSpell ? baseSpell : spell;
    }

    private float HandleFizzleAction(QueuedCombatAction action, CombatActionListObj combatActionList) {
        var fizzleAction = InitializeCombatAction(action);
        fizzleAction.m_spellHits = (char) 0;
        fizzleAction.m_targetSubcircleList.Add(action.SelectedTarget.SlotIndex);
        combatActionList.m_actionList.Add(fizzleAction);

        Logger.Debug("Duel {0} | Slot {1} | Spell fizzled.",
            Logger.Args(_duel.m_duelID.Full, action.SpellCaster.SlotIndex));

        return SPELL_FIZZLE_TIME;
    }

    private float HandleSuccessfulAction(QueuedCombatAction action, CombatActionListObj combatActionList, bool alreadyPaid = false) {
        var cinematicTime = 0.0f;
        var combatAction = InitializeCombatAction(action);
        var spellWorthCasting = CombatActionResolver.ProcessedQueuedCombatAction(action, ref combatAction, ref cinematicTime, out var notes);

        LogCombatAction(action, combatAction, spellWorthCasting, notes);

        combatActionList.m_actionList.Add(combatAction);

        if (action.Spell is null) {
            return SPELL_PASS_TIME;
        }

        // Not worth casting: the action goes out as a pass (m_spell is null), so the game plays a pass, not the spell.
        // The round used to wait the whole spell anyway (Niduus's log 2026-10-22: 67.7 s for 24.4 s of animations, and
        // 52.6 s for 38.4 s, each with one "not worth casting").
        if (!spellWorthCasting) {
            return SPELL_PASS_TIME;
        }

        if (!alreadyPaid) {
            DoSpellCastConsequences(action.SpellCaster, combatAction);
        }

        var animation = GetActionCinematicTime(action);
        Logger.Debug("Duel {0} | Slot {1} | Spell {2} ({3}) waits {4:0.0} s: animation {5:0.0} s + effects {6:0.0} s.",
            Logger.Args(_duel.m_duelID.Full, action.SpellCaster.SlotIndex, action.Spell.m_templateID,
                (string) action.SpellTemplate?.m_name ?? "?", animation + cinematicTime, animation, cinematicTime));

        return animation + cinematicTime;
    }

    private float HandlePassAction(QueuedCombatAction action, CombatActionListObj combatActionList) {
        var passCombatAction = InitializeCombatAction(action);
        passCombatAction.m_spell = null;
        passCombatAction.m_spellHits = (char) 0;
        combatActionList.m_actionList.Add(passCombatAction);

        return SPELL_PASS_TIME;
    }

    private float InvokeOverTimeEffects(CombatDuelSubCircle caster) {
        var cinematicTime = 0.0f;
        if (caster?._hangingEffects is null) {
            return cinematicTime;
        }

        try {
            // A copy of the list, since ticks take effects (and used wards) off it.
            var overTime = caster._hangingEffects
                .Where(x => x is not null && CombatRequirements.IsOverTime(x))
                .ToList();
            var auras = caster._hangingEffects.Where(CombatRequirements.IsAura).ToList();

            foreach (var effect in overTime) {
                switch (effect.m_effectType) {
                    case kSpellEffects.kDamageOverTime: {
                        cinematicTime += OVER_TIME_ACTIVATION_TIME;
                        // Only the wards of the tick's school count, and they are used up (they were not before).
                        var used = CombatEffectApplicator.TickDamage(caster, effect, effect.m_paramPerRound);
                        cinematicTime += HANGING_EFFECT_CONSUME_TIME * used;
                        break;
                    }
                    case kSpellEffects.kDeferredDamage:
                        // A bomb waits its rounds, then hits once with all of its damage (it did nothing before).
                        if (effect.m_numRounds <= 1) {
                            cinematicTime += OVER_TIME_ACTIVATION_TIME;
                            cinematicTime += HANGING_EFFECT_CONSUME_TIME
                                             * CombatEffectApplicator.TickDamage(caster, effect, effect.m_paramPerRound);
                        }
                        break;
                    case kSpellEffects.kHealOverTime:
                        // A heal over time ends with the participant: it never revives them, including after a DoT now.
                        if (!caster.IsAlive) {
                            CombatCharms.RemoveExact(caster._hangingEffects, [effect]);
                            continue;
                        }

                        cinematicTime += OVER_TIME_ACTIVATION_TIME;
                        caster.HealParticipant(effect.m_paramPerRound);
                        break;
                }

                effect.m_numRounds--;
                if (effect.m_numRounds <= 0) {
                    CombatCharms.RemoveExact(caster._hangingEffects, [effect]);
                }
            }

            // Auras (Fortify, Amplify, Infallible...) last their rounds and then go (they were used up on the first hit).
            foreach (var aura in auras) {
                aura.m_numRounds--;
                if (aura.m_numRounds <= 0) {
                    CombatCharms.RemoveExact(caster._hangingEffects, [aura]);
                }
            }
        }
        catch (Exception ex) {
            Logger.Error("Duel {0} | Slot {1} | Over time effects failed: {2}",
                Logger.Args(_duel.m_duelID.Full, caster.SlotIndex, ex));
        }

        // Is the participant dead after the effects?
        // If so, add a death animation time.
        if (!caster.IsAlive) {
            cinematicTime += DEATH_ANIMATION_TIME;
        }

        return cinematicTime;
    }

    private void LogQueuedCombatAction(CombatMoveType type, CombatDuelSubCircle caster, CombatDuelSubCircle target, Spell spell) {
        if (type == CombatMoveType.ChangeMind) {
            Logger.Debug("Duel {0} | Slot {1} | Caster changed their mind and is not casting a spell",
                Logger.Args(_duel.m_duelID.Full, caster.SlotIndex));

            return;
        }

        var targetOrSelf = target is null
            ? "null" : (target.SlotIndex == caster.SlotIndex ? "self" : target.SlotIndex.ToString());
        var spellOrPass = spell is null ? "pass" : spell.m_templateID.ToString();
        Logger.Debug("Duel {0} | Slot {1} | Caster is casting spell {2} against target {3}",
            Logger.Args(_duel.m_duelID.Full, caster.SlotIndex, spellOrPass, targetOrSelf));
    }

    private void LogCombatAction(QueuedCombatAction action, CombatAction combatAction, bool spellWorthCasting, string notes) {
        var spellId = action.Spell?.m_templateID ?? 0;
        var spellName = action.SpellTemplate?.m_name ?? "?";
        if (spellWorthCasting) {
            var targetsStringForLog = string.Join(", ", combatAction.m_targetSubcircleList);
            var critSuffix = combatAction.m_CritHitList?.Any(x => x.m_mult > 0f) == true ? " (Crits!)" : "";
            Logger.Debug("Duel {0} | Slot {1} | Spell {2} ({3}) hits targets [{4}]{5}",
                Logger.Args(_duel.m_duelID.Full, action.SpellCaster.SlotIndex, spellId, spellName, targetsStringForLog, critSuffix));

            // Parts of the spell the server skipped (it still cast): say which and why.
            if (!string.IsNullOrEmpty(notes)) {
                Logger.Debug("Duel {0} | Slot {1} | Spell {2} ({3}) skipped parts: {4}",
                    Logger.Args(_duel.m_duelID.Full, action.SpellCaster.SlotIndex, spellId, spellName, notes));
            }
        }
        else {
            Logger.Debug("Duel {0} | Slot {1} | Spell {2} ({3}) not worth casting ({4}). Passing turn.",
                Logger.Args(_duel.m_duelID.Full, action.SpellCaster.SlotIndex, spellId, spellName,
                            string.IsNullOrEmpty(notes) ? "no effect found a target" : notes));
            combatAction.m_spell = null;
        }
    }

    private static CombatAction InitializeCombatAction(QueuedCombatAction action) => new() {
        m_spellCaster = action.SpellCaster.SlotIndex,
        m_targetSubcircleList = [],
        m_showCast = true,
        m_spellHits = (char) 1,
        m_spell = ShownSpell(action),
        m_serializedBlocks = "\u0000\u0000",
        m_shadowPactTarget = -1,
        m_petCastTarget = -1,
        m_CritHitList = [],
    };

    // [Battle] PlayerCastsUseNormalAnimation (Niduus, 2026-10-22, "Swap where possible"): a wizard casting a boss's or
    // monster's copy of a spell (the Emulate globals and the like) sees the animation of the player spell that does the
    // same thing, without the boss's own part first. Copies with no such player spell keep their animation.
    private static readonly bool s_playerCastsUseNormalAnimation =
        Imlight.Common.ConfigurationManager.GetValue("Battle.PlayerCastsUseNormalAnimation", true);

    private static readonly Lazy<Dictionary<uint, uint>> s_lookalikes = new(BuildLookalikes);

    private static Dictionary<uint, uint> BuildLookalikes() {
        try {
            var ids = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            foreach (var location in CoreObjectFactory.TemplateManifest?.m_serializedTemplates ?? []) {
                string file = location?.m_filename;
                if (!string.IsNullOrEmpty(file) && location.m_id <= uint.MaxValue) {
                    ids.TryAdd(file, (uint) location.m_id);
                }
            }

            var spells = SpellFactory.AllSpellTemplates()
                .Where(s => ids.ContainsKey(s.Path))
                .Select(s => (s.Path, s.Template, ids[s.Path]));
            _ = SpellCinematics.Instance;
            var twins = SpellLookalikes.Build(spells, name => SpellCinematics.GetCinematicTemplate(name) is not null);
            Logger.Information("Spell animations: {0} boss and monster spells play a player spell's animation when a wizard casts them.",
                Logger.Args(twins.Count));

            return twins;
        }
        catch (Exception e) {
            Logger.Error("Spell animations: could not match boss spells to player spells: {0}", Logger.Args(e));

            return [];
        }
    }

    /// <summary>The spell the game is told was cast: for a wizard casting a boss copy, its player twin (same effects).</summary>
    private static Spell ShownSpell(QueuedCombatAction action) {
        if (!s_playerCastsUseNormalAnimation || action.Spell is null || action.SpellCaster is not { IsHumanPlayer: true }
            || !s_lookalikes.Value.TryGetValue(action.Spell.m_templateID, out var twin)) {
            return action.Spell;
        }

        Logger.Debug("Spell animations: wizard cast {0}, shown with the animation of {1}.", Logger.Args(action.Spell.m_templateID, twin));

        return action.Spell with { m_templateID = twin };
    }

    private static float GetActionCinematicTime(QueuedCombatAction action) {
        if (action.Spell is null) {
            return SPELL_PASS_TIME;
        }

        var spellName = SpellFactory.GetBaseSpellName(ShownSpell(action).m_templateID);
        var cinematicFactory = SpellCinematics.Instance;

        if (FastAnimationTiming) {
            try {
                return SpellCinematics.GetMeasuredSpellTime(spellName, SPELL_CAST_TIME);
            }
            catch (Exception e) {
                Logger.Error("Spell {0} | Could not work out its animation time, using the old one: {1}",
                    Logger.Args(action.Spell.m_templateID, e));
            }
        }

        // All spells will always have a summon time.
        var count = SpellCinematics.GetSpellSummonTime(spellName);

        // Check if this spell has a special casting time. If not, just add the default casting time.
        var castTime = SpellCinematics.GetSpellCastingTime(spellName);
        count += castTime > 0.1f ? castTime : SPELL_CAST_TIME;

        // Check to see if the spell has an act time. If it does, add it to the total time.
        // Otherwise, return the total time.
        var actTime = SpellCinematics.GetSpellActTime(spellName);
        if (actTime <= 0.1f) {
            return count + SpellCinematics.GetSpellTotalTime(spellName);
        }

        count += actTime;

        return count;
    }

    private static bool SpellHits(CombatDuelSubCircle caster, Spell spell) {
        if (caster is null || spell is null) {
            return false;
        }

        if (ConsumeDispell(caster, spell.m_magicSchoolID)) {
            return false;
        }

        if (caster.CheatNoFizzle) {
            return true;
        }

        var spellAccuracy = (int) spell.m_accuracy;
        var schoolName = MagicSchools.GetMagicSchool(spell.m_magicSchoolID)?.m_schoolName ?? "";
        try {
            // Accuracy is in points added to the card's own (a 70% card and 10% accuracy is 80%). The old sum floored
            // a multiplier, so any accuracy loss below 1.0 made the card 0% (always fizzles) and any gain was lost.
            var stats = caster.ResolverGameStats;
            if (stats is not null) {
                var increase = CombatEffectApplicator.AsFraction(caster.GetStatBySchool(stats.m_accBonusPercent, schoolName) + stats.m_accBonusPercentAll);
                var decrease = CombatEffectApplicator.AsFraction(caster.GetStatBySchool(stats.m_accReducePercent, schoolName) + stats.m_accReducePercentAll);
                spellAccuracy += (int) Math.Round((increase - decrease) * 100);
            }

            // An accuracy bubble on the battlefield.
            spellAccuracy += CombatEffectApplicator.GetGlobalEffect(caster._duelActor?.Duel, kSpellEffects.kModifyAccuracy, schoolName);
        }
        catch (Exception ex) {
            Logger.Error("Spell {0}: accuracy stats failed: {1}", Logger.Args(spell.m_templateID, ex));
        }

        // Apply any hanging accuracy effects (accuracy charms and auras, Smoke Screen style debuffs).
        spellAccuracy = ConsumeHangingAccuracyEffects(spellAccuracy, caster, schoolName);

        var hitChance = Random.Shared.Next(0, 100);
        
        return hitChance <= spellAccuracy;
    }

    /// <summary>
    /// Takes a played card out of the hand: a treasure card is used up for good (and leaves the deck), any
    /// other card is discarded and comes back on reshuffle.
    /// </summary>
    internal static void UseUpCard(CombatDuelSubCircle caster, Spell spell) {
        if (spell.m_treasureCard) {
            var consumedTemplateId = caster.ConsumeFromVault(spell);
            if (consumedTemplateId != 0 && caster._wizard != null && !caster.IsSandboxed) {
                // Persist the removal from the player's treasure card book.
                caster._wizard.SpellbookBehavior.RemoveTreasureCard(consumedTemplateId);
                WizardCollection.RemoveTreasureCard(caster._wizard, consumedTemplateId);

                // Also remove from the equipped deck's spell list so it doesn't
                // reappear when the player re-opens their deck after combat.
                var deckSlot = caster._wizard.EquipmentBehavior.SlotList
                    .FirstOrDefault(s => s.SlotType == EquipmentSlotType.Deck);
                if (deckSlot?.ItemId != null) {
                    caster._wizard.RemoveSpellFromDeck(consumedTemplateId, deckSlot.ItemId.Value);

                    // Tell the client to remove the TC from the deck UI.
                    var spellTemplate = CoreObjectFactory.GetCoreTemplate(consumedTemplateId) as SpellTemplate;
                    if (spellTemplate != null) {
                        var spellHash = StringHash.Compute(spellTemplate.m_name);
                        caster.ParticipantActor.Tell(
                            new WIZARD_12_PROTOCOL.MSG_REMOVETREASURESPELLFROMDECK {
                                SpellID = (int) spellHash,
                                EnchantmentID = 0,
                                DeckID = deckSlot.ItemId.Value,
                                Success = 1,
                                Destroy = 0
                            }, ActorRefs.NoSender);
                    }
                }
            }
        }
        else {
            caster.DiscardCard(spell);
        }
    }

    private static void DoSpellCastConsequences(CombatDuelSubCircle caster, CombatAction action) {
        if (action.m_spell is null) {
            return;
        }

        // Treasure cards are permanently consumed on successful cast.
        // Regular spells are discarded and return to the deck on reshuffle.
        UseUpCard(caster, action.m_spell);

        // FullPipConserve: like a 100% pip conserve stat, a half-used power pip comes back as a normal pip.
        var conservePips = s_fullPipConserve && caster._wizard is not null;

        // Spend any school pips the spell costs.
        caster.DeductSchoolPips(action.m_spell);

        // Deduce the players mana by the rank of the spell.
        caster.DeductMana(action.m_spell.m_pipCost.m_spellRank);

        // X-pip spells spend what they used (same GetXPipCost that chose the tier).
        if (CombatActionResolver.IsXPipSpell(action.m_spell)) {
            var xCost = CombatActionResolver.GetXPipCost(action.m_spell, caster);
            caster._usedPipsForExperienceGain += xCost;

            caster.DeductPips((MagicSchool) action.m_spell.m_magicSchoolID, xCost, conservePips);
        }
        else {
            // Increase the used pips for experience gain by the rank of the spell.
            // Even 0-rank spells will give the caster 1 pip for experience gain.
            caster._usedPipsForExperienceGain += Math.Max((byte) 1, action.m_spell.m_pipCost.m_spellRank);

            caster.DeductPips((MagicSchool) action.m_spell.m_magicSchoolID, action.m_spell.m_pipCost.m_spellRank, conservePips);
        }
    }

    private static bool ConsumeDispell(CombatDuelSubCircle caster, uint magicSchoolId) {
        var dispellHangingEffect = caster._hangingEffects
            .FirstOrDefault(x => x.m_effectType == kSpellEffects.kDispel
                     && StringHash.Compute(x.m_sDamageType) == magicSchoolId);

        if (dispellHangingEffect is not null) {
            caster._hangingEffects.Remove(dispellHangingEffect);
            return true;
        }
        else {
            return false;
        }
    }

    /// <remarks>
    /// The old code matched m_damageType (always 0 in the game data, so no accuracy charm ever counted), added
    /// floor(1 + param / 100) (1 point) and removed from the list while going through it, which threw.
    /// </remarks>
    private static int ConsumeHangingAccuracyEffects(int startingAccuracy, CombatDuelSubCircle caster, string schoolName) {
        if (caster?._hangingEffects is null) {
            return startingAccuracy;
        }

        var used = CombatCharms.OnePerSpell(caster._hangingEffects
            .Where(x => x is not null && x.m_effectType == kSpellEffects.kModifyAccuracy
                        && CombatCharms.SchoolMatches(x.m_sDamageType, schoolName)));

        startingAccuracy += used.Sum(x => x.m_effectParam);
        CombatCharms.RemoveExact(caster._hangingEffects, used.Where(x => !CombatRequirements.IsAura(x)));

        return startingAccuracy;
    }

}
