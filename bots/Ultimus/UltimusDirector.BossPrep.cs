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
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Before a boss (Niduus, 2026-11-16, "All right now": deck and gear per boss from its known cheats and schools, and a boss
/// playbook read from the game data). When his next fight is a creature with cheats, he reads them first (BotBossPlaybook,
/// from the cheat file the server runs), says his plan in his thoughts, makes his deck for it (fewer of the cards it
/// punishes: BotCards.Situation.Punished) and, against a boss whose free moves hit hard, puts on his "Support" gear set
/// (resist and health) piece by piece as a player swaps gear in the backpack. After the fight his usual questing gear comes
/// back by itself (KeepGearAndDecks).
/// </summary>
internal sealed partial class UltimusDirectorActor {

    private readonly Dictionary<ulong, DateTime> _bossRead = [];
    private ulong _bossGearFor;
    private DateTime _bossGearUntil = DateTime.MinValue;

    /// <summary>His next move is a fight: the creature's playbook read and his gear made ready for it.</summary>
    private void BossPrep(UltimusPlanner.Target target) {
        if (target?.Kind != UltimusPlanner.FIGHT || target.Template == 0) {
            return;
        }

        var plan = BotBossPlaybook.For(target.Template);
        if (plan.Empty) {
            return;
        }

        var situation = BotCards.SituationOf(target.Template);
        if (situation.Boss && plan.HitsHard) {
            _bossGearFor = target.Template;
            _bossGearUntil = DateTime.UtcNow.AddMinutes(20);
        }

        if (_bossRead.TryGetValue(target.Template, out var at) && DateTime.UtcNow - at < TimeSpan.FromHours(1)) {
            return;
        }

        _bossRead[target.Template] = DateTime.UtcNow;
        var name = plan.Name.Length > 0 ? plan.Name : "it";
        var rules = plan.Describe();
        var advice = plan.Advice();
        Note("Ultimus", $"BOSS PLAYBOOK: {name}: {string.Join("; ", rules.Take(8))}.");
        UltimusJournal.Write("boss", $"Read up on {name}'s cheats before fighting it.", "", "", name);
        Think($"Before fighting {name} I read up on its cheats (the game's own cheat file): {string.Join("; ", rules.Take(5))}." +
              (advice.Count > 0 ? $" My plan: {string.Join("; ", advice.Take(4))}." : "") +
              (_bossGearFor == target.Template ? " Its free moves hit hard, so I wear my Support gear set (resist and health) for it." : ""),
            "boss-playbook|" + target.Template);
    }

    /// <summary>
    /// While a hard-hitting boss is his next fight: one piece of his Support set goes on (true), as a player swaps gear by
    /// hand; false when nothing is left to change or no boss is coming.
    /// </summary>
    private bool BossGearStep(int index, UltimusMember.Status member, Wizard wizard) {
        if (!BossGearHeld()) {
            return false;
        }

        var support = BotGearSets.Roles.FirstOrDefault(r => r.Style == BotStyle.Tank);
        if (support is null || wizard.EquipmentBehavior is not { } equipment) {
            return false;
        }

        foreach (var piece in BotGearSets.OutfitFor(wizard, support.Style)) {
            var worn = equipment.GetItemInSlot(piece.SlotType);
            if (worn is not null && worn.m_globalID.Full == piece.ItemId) {
                continue;
            }

            var item = (wizard.InventoryBehavior?.Items ?? []).FirstOrDefault(i => i is not null && i.m_globalID.Full == piece.ItemId);
            if (item is null) {
                continue;
            }

            var read = BotGear.Read((uint) Math.Min((ulong) item.m_templateID, uint.MaxValue));
            var what = read?.Name ?? "a piece of his Support set";
            Note(member.Role, $"{member.Name} puts on {what} from its Support set for the boss ahead.");
            _actors[index].Tell(new UltimusMember.Equip(item.m_globalID.Full, piece.SlotType.ToString(), what));
            return true;
        }

        return false;
    }

    /// <summary>Whether the boss gear stays on now: the hard-hitting boss is still his next fight (or he is fighting it).</summary>
    private bool BossGearHeld() {
        if (_bossGearFor == 0) {
            return false;
        }

        var stillNext = (_target?.Kind == UltimusPlanner.FIGHT && _target.Template == _bossGearFor)
                        || (_team.Count > 0 && CombatDuelComponent.IsInBattle(_team[0].GlobalId));
        if (stillNext && DateTime.UtcNow < _bossGearUntil) {
            return true;
        }

        _bossGearFor = 0;
        return false;
    }

}
