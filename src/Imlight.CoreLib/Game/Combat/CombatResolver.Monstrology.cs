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
using Imlight.CoreLib.Game.MonsterMagic;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Combat;

/// <summary>
/// Monstrology in battle, as the game's own help says: a damage spell with an Extract spell on it takes 1 Animus from each
/// monster of that kind it hits, and 1 more if the hit defeats it. A monster gives its Animus once a battle to each
/// wizard. Above the wizard's Monstrology level a monster gives Animus only now and then ([Monstrology] HighRankChance);
/// below it there may be a bonus Animus (BonusChance). The Animus is only kept if the battle is won (TakeAnimus).
/// </summary>
public partial class CombatResolver {

    // The Animus each wizard extracted this battle, by its circle, then by the monster's tome id.
    private readonly Dictionary<CombatDuelSubCircle, Dictionary<uint, int>> _animus = [];

    // Each wizard and monster (by its global id) that already gave Animus this battle.
    private readonly HashSet<(CombatDuelSubCircle Wizard, ulong Monster)> _extractedFrom = [];

    /// <summary>
    /// The Animus a wizard extracted this battle, taken out of the battle (for the win), or null when there is none.
    /// </summary>
    internal Dictionary<uint, int> TakeAnimus(CombatDuelSubCircle wizard) {
        if (wizard is null || !_animus.Remove(wizard, out var animus) || animus.Count == 0) {
            return null;
        }

        return animus;
    }

    // After a wizard's move: an Extract-enchanted damage spell that landed takes Animus from the monsters it hit.
    private void NoteExtractions(QueuedCombatAction queued, int before, CombatActionListObj combatActionList) {
        try {
            var mover = queued?.SpellCaster;
            if (!Monstrology.Enabled || mover is null || !mover.IsHumanPlayer || mover._wizard is not { } wizard
                || queued.Spell is not { m_enchantment: not 0 } card
                || CoreObjectFactory.GetCoreTemplate(card.m_enchantment) is not SpellTemplate enchantment
                || !Monstrology.IsExtract(enchantment)) {
                return;
            }

            var kinds = Monstrology.ExtractKinds(enchantment);
            var landed = combatActionList.m_actionList.Skip(before)
                .Where(a => a.m_spellCaster == mover.SlotIndex && a.m_spell is not null && a.m_spellHits is (char) 1 or (char) 2)
                .ToList();
            var hit = landed.SelectMany(a => a.m_targetSubcircleList ?? [])
                .Distinct()
                .Select(slot => _subCircles.FirstOrDefault(c => c is not null && c.Occupied && c.SlotIndex == slot))
                .Where(c => c is not null && c.OccupiedTeam != mover.OccupiedTeam && !c.IsHumanPlayer)
                .ToList();
            if (hit.Count == 0) {
                return;
            }

            Monstrology.Open(wizard);
            var level = Monstrology.LevelOf(wizard);
            var lines = new List<string>();
            foreach (var monster in hit) {
                lines.Add(Extract(mover, wizard, monster, kinds, level));
            }

            Logger.Information("Duel {0} | Slot {1} | Monstrology ({2}): {3}",
                Logger.Args(_duel.m_duelID.Full, mover.SlotIndex, (string) enchantment.m_name, string.Join("; ", lines)));
            mover.ParticipantActor?.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE {
                Message = string.Join("\n", lines.Where(l => l is not null)),
                Modal = 0,
            });
        }
        catch (Exception e) {
            Logger.Error("Duel {0} | Monstrology extraction failed: {1}", Logger.Args(_duel.m_duelID.Full, e));
        }
    }

    // One monster hit by an Extract spell: the line the wizard is told.
    private string Extract(CombatDuelSubCircle mover, Imlight.CoreLib.WizardData.Models.Player.Wizard wizard, CombatDuelSubCircle target,
                           HashSet<string> kinds, int level) {
        var monster = Monstrology.MonsterOf((uint) target.ParticipantObject.m_templateID);
        if (monster is null) {
            return "Failed to collect any Monster Animus - Monster not collectable";
        }

        if (kinds.Count > 0 && !monster.Kinds.Overlaps(kinds)) {
            return $"Failed to collect any Monster Animus - Wrong monster type ({monster.Name})";
        }

        if (!_extractedFrom.Add((mover, target.ParticipantObject.m_globalID))) {
            return $"Failed to collect any Monster Animus - Already Collected ({monster.Name})";
        }

        var chance = monster.Rank > level ? Monstrology.HighRankChance : 100;
        chance = chance * Math.Clamp(100 - monster.Magic.m_collectionResistance, 0, 100) / 100;
        if (Random.Shared.Next(100) >= chance) {
            return $"Failed to collect any Monster Animus - Monstrology level too low ({monster.Name} is rank {monster.Rank})";
        }

        var count = 1 + (target.IsAlive ? 0 : 1);
        if (level > monster.Rank && Random.Shared.Next(100) < Monstrology.BonusChance) {
            count++;
        }

        count = Monstrology.WithBenefit(wizard, count);
        if (!_animus.TryGetValue(mover, out var animus)) {
            _animus[mover] = animus = [];
        }

        animus[monster.TomeId] = animus.GetValueOrDefault(monster.TomeId) + count;

        return count == 1 ? $"Collected 1 {monster.Name} Animus" : $"Collected {count} {monster.Name} Animus";
    }

}
