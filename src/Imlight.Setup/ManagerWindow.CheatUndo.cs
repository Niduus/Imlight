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
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace Imlight.Setup;

/// <summary>
/// Undo and Redo on a cheat's edit page (Niduus, 2026-11-19: "Let the blocking be more interactive"): every change to the
/// open cheat's blocks (added, moved, deleted, a box changed) can be taken back, and put back again, until the page is
/// left. Typing in one box counts as one change.
/// </summary>
internal sealed partial class ManagerWindow {

    private sealed class CheatHistory {
        internal List<(string Text, CheatRule State)> Done { get; } = [];
        internal List<(string Text, CheatRule State)> Undone { get; } = [];
        internal DateTime LastAt { get; set; }
        internal string LastShape { get; set; } = "";
    }

    private readonly ConditionalWeakTable<CheatRule, CheatHistory> _cheatHistory = new();

    private static string ShapeOf(CheatRule rule) => string.Join(",", rule.Ifs.Concat(rule.Dos).Select(b => b.Spec).Prepend(rule.When.Spec));

    /// <summary>Notes the cheat as it is now, after a change (or when its page opens).</summary>
    private void RecordCheat(CheatRule rule) {
        var history = _cheatHistory.GetOrCreateValue(rule);
        var text = rule.Canonical().ToJsonString() + (rule.Off ? "|off" : "");
        if (history.Done.Count > 0 && history.Done[^1].Text == text) {
            return;
        }

        // Typing in a box: one change, not one per letter.
        var shape = ShapeOf(rule);
        var now = DateTime.UtcNow;
        if (history.Done.Count > 1 && shape == history.LastShape && now - history.LastAt < TimeSpan.FromSeconds(1.5)) {
            history.Done[^1] = (text, rule.Copy());
        }
        else {
            history.Done.Add((text, rule.Copy()));
            if (history.Done.Count > 200) {
                history.Done.RemoveAt(0);
            }
        }

        history.Undone.Clear();
        history.LastAt = now;
        history.LastShape = shape;
    }

    private static void PutBack(CheatRule rule, CheatRule state) {
        var copy = state.Copy();
        rule.When = copy.When;
        rule.Off = copy.Off;
        rule.Ifs.Clear();
        rule.Ifs.AddRange(copy.Ifs);
        rule.Dos.Clear();
        rule.Dos.AddRange(copy.Dos);
    }

    private void UndoCheat(CheatBoard board, bool redo) {
        if (board.Editing is not { } rule || !_cheatHistory.TryGetValue(rule, out var history)) {
            return;
        }

        if (redo) {
            if (history.Undone.Count == 0) {
                Say("Nothing to redo.");

                return;
            }

            var next = history.Undone[^1];
            history.Undone.RemoveAt(history.Undone.Count - 1);
            history.Done.Add(next);
            PutBack(rule, next.State);
        }
        else {
            if (history.Done.Count < 2) {
                Say("Nothing to undo on this cheat.");

                return;
            }

            history.Undone.Add(history.Done[^1]);
            history.Done.RemoveAt(history.Done.Count - 1);
            PutBack(rule, history.Done[^1].State);
        }

        // The next change starts a change of its own.
        history.LastAt = DateTime.MinValue;
        var undone = history.Undone;
        var kept = undone.ToList();
        RenderCheats(board);
        // Showing the page notes the cheat again; that must not clear what can be redone.
        undone.Clear();
        undone.AddRange(kept);
    }

    private Control[] UndoButtons(CheatBoard board) {
        var undo = Action(Plain("↶ Undo"), () => {
            UndoCheat(board, redo: false);

            return Task.CompletedTask;
        });
        ToolTip.SetTip(undo, "Take back the last change to this cheat's blocks (also Ctrl+Z).");
        var redo = Action(Plain("↷ Redo"), () => {
            UndoCheat(board, redo: true);

            return Task.CompletedTask;
        });
        ToolTip.SetTip(redo, "Put back what Undo took back (also Ctrl+Y).");

        return [undo, redo];
    }

}
