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
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Zone.Components;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// A fight he keeps losing: he works out the best remedy himself (Niduus, 2026-11-14: "IF he continues to fail a fight, he
/// should use logic to resort to what he thinks is the best method"), within his rules (main story only, quests like a
/// human, no XP farming). From what the lost fights show he ranks the remedies and tries the best one not yet tried at that
/// step, saying why:
///   full     he went in hurt, low on mana or with no potions: next time full health, full mana and potions first (the
///            one case besides being under 25% where he goes out of his way to heal or buy potions).
///   gear     he has gold past his reserve: a shopping trip for a clear upgrade, even in push mode.
///   deck     a deck rebuilt for this creature's school and tricks.
///   level    a side quest of his level given nearby (never farming creatures).
///   patience rested and careful, and he waits a few minutes by the fight for a player who may join, then tries again.
/// </summary>
internal sealed partial class UltimusDirectorActor {

    private sealed class LossPlan {
        public readonly List<string> Tried = [];
        public string Now = "";
        public double StartHealth = 1;
        public double StartMana = 1;
        public int StartPotions;
    }

    private readonly Dictionary<string, LossPlan> _lossPlans = [];
    private DateTime _gearPushUntil = DateTime.MinValue;
    private DateTime _patienceUntil = DateTime.MinValue;

    // What he had going into each fight, for the reasoning after a loss.
    private (double Health, double Mana, int Potions) _fightStartKit = (1, 1, 0);

    private void NoteFightKit(UltimusMember.Status me) {
        if (me.Wizard?.GameStats is not { } stats) {
            return;
        }

        _fightStartKit = (stats.m_currentHitpoints / (double) Math.Max(1, stats.m_baseHitpoints + stats.m_bonusHitpoints),
            stats.m_currentMana / (double) Math.Max(1, stats.m_baseMana + stats.m_bonusMana), (int) stats.m_potionCharge);
    }

    /// <summary>The remedy he is on for this step ("" = none yet).</summary>
    private string RemedyFor(string stepKey) => stepKey is not null && _lossPlans.TryGetValue(stepKey, out var plan) ? plan.Now : "";

    /// <summary>A fight lost at <paramref name="stepKey"/> for the <paramref name="losses"/>th time: the next remedy, reasoned out.</summary>
    private void PlanAfterLoss(UltimusMember.Status me, string stepKey, int losses) {
        if (losses < 2 || string.IsNullOrEmpty(stepKey)) {
            return;
        }

        if (!_lossPlans.TryGetValue(stepKey, out var plan)) {
            _lossPlans[stepKey] = plan = new LossPlan();
        }

        (plan.StartHealth, plan.StartMana, plan.StartPotions) = _fightStartKit;
        var gold = me.Wizard?.GameStats?.m_currentGold ?? 0;
        var spare = gold - GoldReserve(me);

        // Each remedy scored from what the lost fights show; the best one not yet tried wins.
        var scores = new List<(string Remedy, double Score, string Why)> {
            ("full", plan.StartHealth < 0.95 || plan.StartMana < 0.6 || plan.StartPotions == 0 ? 90 : 5,
                $"I went in with {plan.StartHealth:P0} health, {plan.StartMana:P0} mana and {plan.StartPotions} potions"),
            ("gear", spare > 300 ? 60 + Math.Min(20, spare / 500.0) : 10, $"I have {gold:N0} gold, {Math.Max(0, spare):N0} past my reserve, for better gear"),
            ("deck", 50, "a deck made for this creature's school and tricks may turn it"),
            ("level", 35, "a side quest of my level nearby makes me stronger without farming"),
            ("patience", 20, "rested and careful, and a player may join to help"),
        };

        var pick = scores.Where(s => !plan.Tried.Contains(s.Remedy)).OrderByDescending(s => s.Score).FirstOrDefault();
        if (pick.Remedy is null) {
            // Every remedy tried: start over with the best one, as a player tries again.
            plan.Tried.Clear();
            pick = scores.OrderByDescending(s => s.Score).First();
        }

        plan.Tried.Add(pick.Remedy);
        plan.Now = pick.Remedy;
        var plain = pick.Remedy switch {
            "full" => "next time I go in at full health and mana with potions, even if I have to fetch them",
            "gear" => "I shop for a clear gear upgrade first",
            "deck" => "I rebuild my deck for this creature",
            "level" => "I do a side quest of my level nearby first",
            _ => "I rest, go in careful, and wait a few minutes by the fight in case a player joins",
        };
        Think($"I lost this fight {losses} times. Weighing my options: {string.Join("; ", scores.OrderByDescending(s => s.Score).Select(s => $"{s.Remedy} {s.Score:0}"))}. " +
              $"Best one I haven't tried here: {pick.Remedy}, because {pick.Why}. So {plain}.", "loss-plan|" + stepKey + "|" + losses);
        Note("Ultimus", $"after losing {losses} times he tries: {pick.Remedy} ({pick.Why}).", UltimusLink.NOTABLE);

        switch (pick.Remedy) {
            case "gear":
                _gearPushUntil = DateTime.UtcNow.AddMinutes(20);
                _nextShopping = DateTime.UtcNow;
                break;
            case "deck":
                PrepareDeck(0, _target?.Kind == UltimusPlanner.FIGHT ? UltimusScout.SituationOf(_target.Template) : BotCards.Situation.Unknown, force: true);
                break;
            case "patience":
                _patienceUntil = DateTime.UtcNow.AddMinutes(4);
                break;
        }
    }

    /// <summary>The "patience" remedy: he waits by the fight a few minutes (a player may come to help); true while waiting.</summary>
    private bool WaitingForHelp(UltimusMember.Status leader, UltimusPlanner.Target target) {
        if (target?.Kind != UltimusPlanner.FIGHT || DateTime.UtcNow >= _patienceUntil || RemedyFor(target.Key) != "patience"
            || CombatDuelComponent.IsInBattle(leader.GlobalId)) {
            return false;
        }

        _doing = "resting by a fight he lost, in case a player comes to help";
        _lastProgress = DateTime.UtcNow;
        _healWaiting = true;

        return true;
    }

}
