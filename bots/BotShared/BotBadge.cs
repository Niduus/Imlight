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
using Imlight.Common;
using Imlight.CoreLib.Game.Badges;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// The badge the Quest Team and Ultimus always show under their names: Essential (Niduus, 2026-11-13: "QT and Ultimus
/// should have their badges locked to the Essential badge for display"). It replaces "the newest story badge".
/// </summary>
internal static class BotBadge {

    /// <summary>The Essential badge (the Blighted Veil raid's "Collect 250 essence"); its id is the badge's own.</summary>
    internal const uint ESSENTIAL_ID = 870908102;

    internal const string ESSENTIAL_NAME = "Essential";

    /// <summary>
    /// The Essential badge's id when the wizard should be told to pick it (it holds the badge, given here when it does not,
    /// as a player must hold a badge to pick it), or 0 when it already shows it or the badge list isn't loaded.
    /// </summary>
    internal static uint ToPick(Wizard wizard) {
        var catalog = BadgeCatalog.Current;
        if (wizard?.BadgeBehavior is not { } state || !catalog.IsReady || !catalog.TryGet(ESSENTIAL_ID, out _)) {
            return 0;
        }

        if (state.SelectedBadgeId == ESSENTIAL_ID) {
            return 0;
        }

        if (!BadgeView.IsComplete(state, catalog.TryGet(ESSENTIAL_ID, out var essential) ? essential : null)) {
            try {
                var changes = new BadgeEngine(wizard, catalog).Execute(engine => engine.Award(ESSENTIAL_ID));
                if (changes.StateChanged) {
                    WizardCollection.UpdateCharacterBadgeBehavior(wizard);
                }
            }
            catch (Exception e) {
                Logger.Warning("Could not give {0} the Essential badge: {1}", Logger.Args(wizard.CharId, e.Message));

                return 0;
            }
        }

        return ESSENTIAL_ID;
    }

}
