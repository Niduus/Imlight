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
 * HOUSING SYSTEM
 * ========================================================================
 *
 * PURPOSE:
 * Finds, creates and shares the live attic of every character who is online.
 *
 * USAGE EXAMPLE:
 * if (AtticRegistry.TryEnter(wizard, out var state, out var created, out var audit)) { ... AtticRegistry.Release(state); }
 *
 * NOTE:
 * - A character has no attic until the first time one is needed. The document is created then, with the container id the
 *   client files the attic blobs under.
 * - A state leaves the registry when its last viewer leaves. It is already persisted by then.
 *
 * TODO:
 *
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/15/2026
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Imlight.Common;
using Imlight.CoreLib.Shared.Utilities;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Housing;

/// <summary>
/// The attics that are loaded, by character.
/// </summary>
internal static class AtticRegistry {

    private const int CreationLockCount = 32;

    private static readonly ConcurrentDictionary<ulong, AtticState> s_attics = new();
    private static readonly Lock[] s_creationLocks = [.. Enumerable.Range(0, CreationLockCount).Select(_ => new Lock())];

    /// <summary>
    /// Fills in what the client is told about the attic when the character enters the game: the container and the item count.
    /// </summary>
    /// <param name="wizard">The character.</param>
    internal static void Describe(Wizard wizard) {
        var attic = AtticCollection.Load(wizard.CharId);
        wizard.AtticContainerGid = attic?.ContainerGid ?? 0;
        wizard.AtticItemCount = attic?.Items.Count ?? 0;
    }

    /// <summary>
    /// Finds a character's attic, creating and storing it when the character has none, and counts a viewer in.
    /// </summary>
    /// <param name="wizard">The character.</param>
    /// <param name="state">The live attic when this returns <c>true</c>; call <see cref="Release"/> when the viewer is gone.</param>
    /// <param name="created">Whether this call created the attic document.</param>
    /// <param name="audit">What the audit found when this call made the state live; nothing otherwise.</param>
    /// <returns><c>false</c> when the attic cannot be loaded or stored.</returns>
    internal static bool TryEnter(Wizard wizard, out AtticState state, out bool created, out AtticAudit audit) {
        created = false;
        audit = default;
        while (true) {
            state = null;
            if (s_attics.TryGetValue(wizard.CharId, out var existing) && !existing.IsRetired) {
                state = existing;
            } else {
                if (existing is not null) {
                    Evict(existing);
                }

                lock (s_creationLocks[(int) (wizard.CharId % CreationLockCount)]) {
                    if (s_attics.TryGetValue(wizard.CharId, out var raced) && !raced.IsRetired) {
                        state = raced;
                    } else if (!TryPublish(wizard, out state, out created, out audit)) {
                        return false;
                    }
                }
            }

            if (state.Enter()) {
                SetSummary(wizard, state);

                return true;
            }

            Evict(state);
        }
    }

    /// <summary>
    /// Counts a viewer out of an attic and drops the attic from the registry when nobody is left.
    /// </summary>
    /// <param name="state">The attic the viewer entered with <see cref="TryEnter"/>.</param>
    internal static void Release(AtticState state) {
        if (state.Leave()) {
            Evict(state);
        }
    }

    /// <summary>
    /// Tells the character's own record what the attic holds now.
    /// </summary>
    /// <param name="wizard">The character.</param>
    /// <param name="state">Their attic.</param>
    internal static void SetSummary(Wizard wizard, AtticState state) {
        wizard.AtticContainerGid = state.ContainerGid;
        wizard.AtticItemCount = state.LiveCount;
    }

    private static void Evict(AtticState state)
        => s_attics.TryRemove(new KeyValuePair<ulong, AtticState>(state.CharId, state));

    private static bool TryPublish(Wizard wizard, out AtticState state, out bool created, out AtticAudit audit) {
        state = null;
        created = false;
        audit = default;
        var attic = AtticCollection.Load(wizard.CharId);
        if (attic is null) {
            var candidate = new Attic {
                Id = Attic.GetDocumentId(wizard.CharId),
                CharId = wizard.CharId,
                ContainerGid = (ulong) RandomGen.GenerateGUID(),
                CreatedUtc = DateTime.UtcNow,
            };
            created = AtticCollection.TryCreate(candidate, out attic);
            if (attic is null) {
                return false;
            }

            if (created) {
                Logger.Information("Created the attic {ContainerGid} of character {CharId}", Logger.Args(attic.ContainerGid, wizard.CharId));
            }
        }

        if (!AtticState.TryCreate(attic, out state, out audit)) {
            return false;
        }

        s_attics[wizard.CharId] = state;

        return true;
    }

}
