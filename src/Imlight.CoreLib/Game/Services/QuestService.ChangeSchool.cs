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

using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.CoreLib.Game.Services;

/// <summary>
/// `.changeschool &lt;school&gt;` (Niduus, 2026-11-04: "converts the wizard into that school along with their spells,
/// unlearning previous schools spells in favor of learning the new schools spells").
///
/// 1. The old school's spells leave the spellbook and decks (with Spellement upgrades of them). Cantrips, fishing,
///    gardening and monster magic spells, spells creatures drop (lore spells) and boss and dev spells stay. Any training
///    points they cost come back, as with `.unlearn`.
/// 2. The new school's spells the wizard had bought with training points become their own school's, which are free,
///    so those points come back too.
/// 3. The wizard becomes the new school (base health, mana and power pip chance of that school at their level). A
///    secondary school that was the new school is forgotten so they can pick another at a trainer.
/// 4. They learn the new school's spells the game would have given by now: the free trainer spells their level allows
///    and the new school's spells from quests at or below their level.
///
/// The chat command (CommandChangeSchoolProtocol) previews it with <see cref="PlanSchoolChange"/> and sends
/// MSG_QUESTCOMMAND "changeschool" with the school to do it here.
/// </summary>
internal partial class QuestService {

    /// <summary>The schools a wizard can change to: the seven schools of the character creator.</summary>
    internal static readonly MagicSchool[] ChangeableSchools = [
        MagicSchool.Balance, MagicSchool.Death, MagicSchool.Fire, MagicSchool.Ice, MagicSchool.Life, MagicSchool.Myth, MagicSchool.Storm,
    ];

    /// <summary>What a school change would take and give back.</summary>
    internal sealed record SchoolChangePlan(List<uint> OldSpells, int PointsBack, Dictionary<uint, int> NewSchoolBought);

    // Fishing, gardening and monster magic spells have a school too, but have nothing to do with it.
    private static readonly Lazy<HashSet<uint>> s_skillSpells = new(() => [
        .. VanillaTrainers.All.SelectMany(t => t.Spells).Where(s => s.Kind is "Fishing" or "Gardening" or "MonsterMagic").Select(s => s.Id)
    ]);

    internal static MagicSchool? FindChangeableSchool(string text) {
        text = (text ?? "").Trim();

        return ChangeableSchools.Any(s => string.Equals(s.ToString(), text, StringComparison.OrdinalIgnoreCase))
            ? ChangeableSchools.First(s => string.Equals(s.ToString(), text, StringComparison.OrdinalIgnoreCase))
            : null;
    }

    /// <summary>The spells changing to <paramref name="to"/> would take, and the training points it would give back.</summary>
    internal static SchoolChangePlan PlanSchoolChange(Wizard wizard, MagicSchool to) {
        var from = wizard.MagicSchoolBehavior.MagicSchool.ToString();
        var costs = new Dictionary<uint, int>();
        foreach (var spell in TrainingReset.BoughtSpells(wizard)) {
            costs.TryAdd(spell.SpellId, spell.Cost);
        }

        var old = new List<uint>();
        var newSchoolBought = new Dictionary<uint, int>();
        foreach (var id in (wizard.SpellbookBehavior?.LearnedSpellTemplateIds ?? []).Distinct()) {
            if (CoreObjectFactory.GetCoreTemplate(id) is not SpellTemplate template || template is CantripsSpellTemplate) {
                continue;
            }

            var school = (string) template.m_sMagicSchoolName ?? "";
            if (string.Equals(school, to.ToString(), StringComparison.OrdinalIgnoreCase)) {
                if (costs.TryGetValue(id, out var cost) && cost > 0) {
                    newSchoolBought[id] = cost;
                }

                continue;
            }

            if (!string.Equals(school, from, StringComparison.OrdinalIgnoreCase) || s_skillSpells.Value.Contains(id)
                || s_dropSpells.Value.ContainsKey(id) || BossDevSpells.KindOf(id) is not null) {
                continue;
            }

            old.Add(id);
        }

        var back = old.Sum(id => costs.GetValueOrDefault(id)) + newSchoolBought.Values.Sum();

        return new SchoolChangePlan(old, back, newSchoolBought);
    }

    private void CommandChangeSchool(Wizard wizard, string schoolText) {
        if (wizard is null) {
            return;
        }

        if (FindChangeableSchool(schoolText) is not { } to) {
            CommandReply("Change school: pick Balance, Death, Fire, Ice, Life, Myth or Storm.");
            return;
        }

        var from = wizard.MagicSchoolBehavior.MagicSchool;
        if (from == to) {
            CommandReply($"You are already a {to} wizard.");
            return;
        }

        if (wizard.IsInDuel) {
            CommandReply("Finish your battle first, then type .changeschool again.");
            return;
        }

        var plan = PlanSchoolChange(wizard, to);
        var actor = SessionActor.ActorRef;

        // 1. The old school's spells go (not onto the .unlearn undo list: they belong to the old school).
        var taken = SpellUnlearn.Unlearn(actor, wizard, plan.OldSpells, remember: false);

        // 2. The new school's bought spells are now their own school's, which are free.
        var refunded = TrainingReset.Forget(wizard, plan.NewSchoolBought.Keys).Values.Sum();
        if (refunded > 0) {
            var newPoints = wizard.MagicSchoolBehavior.TrainingPoints + refunded;
            wizard.UpdateTrainingPoints(newPoints);
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATETRAINING { TrainingPoints = newPoints });
        }

        // 3. The school itself.
        wizard.SetSchool(to);
        VanillaTrainers.ForgetSecondaryIf(wizard, to.ToString());

        // 4. The new school's spells the game would have given by now.
        var school = to.ToString();
        bool OfNewSchool(uint id)
            => CoreObjectFactory.GetCoreTemplate(id) is SpellTemplate spell
               && string.Equals((string) spell.m_sMagicSchoolName, school, StringComparison.OrdinalIgnoreCase);

        var taught = new List<(string Spell, string From)>();
        try {
            CollectTrainerSpells(wizard, free: true, paid: false, taught, OfNewSchool);
            CollectQuestSpells(wizard, finished: true, side: true, taught, OfNewSchool);
        }
        catch (Exception ex) {
            Logger.Error("Change school | Teaching {0} the {1} spells stopped: {2}", Logger.Args(wizard.CharId, school, ex));
            CommandReply($"Change school: teaching the {school} spells stopped ({ex.Message}). Type .quest collectspells to try again.");
        }

        var points = taken.Sum(t => t.PointsBack) + refunded;
        Logger.Information("Change school | {0}: {1} -> {2}, {3} spells unlearned, {4} learned, {5} training points back.",
            Logger.Args(wizard.PlayerNameBehavior?.GetWizardName(), from, to, taken.Count, taught.Count, points));

        CommandReply($"You are now a {to} wizard! {taken.Count} {from} spells left your spellbook and decks, and you learned "
            + $"{taught.Count} {to} spells" + (points > 0 ? $". You got {points} training points back." : ".")
            + " Log out and back in so the game shows your new school, then put your new spells in your deck."
            + " Gear made for your old school may need swapping.");
    }

}
