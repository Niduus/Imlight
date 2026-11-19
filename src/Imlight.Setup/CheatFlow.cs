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

namespace Imlight.Setup;

/// <summary>
/// Holder blocks (Niduus, 2026-11-19: "Let the blocking be more interactive"). Repeat, the "only if" blocks and Otherwise
/// worked on "the next N blocks", a number typed in the block. Now they hold their blocks: the blocks inside show
/// indented under them, closed by an end strip, and a block dropped inside (or dragged out) changes the number by itself.
/// The cheat file stays as it was (the holder's Count, the server's "the next Count steps", counted flat, so a holder in
/// a holder counts its own blocks too); only the editor shows and keeps it as a tree.
/// </summary>
internal static class CheatFlow {

    // The holders and their sentences without the "next N blocks" number, which the editor now keeps.
    private static readonly Dictionary<string, string> s_holders = new(StringComparer.OrdinalIgnoreCase) {
        ["D.Repeat"] = "Repeat {Times} times:",
        ["D.IfVar"] = "Only if {Scope} variable {Var} is {Op} {Amount}:",
        ["D.IfCleared"] = "Only if at least {Amount} effects came off {Each}:",
        ["D.IfConverted"] = "Only if at least {Amount} effects were converted {Each}:",
        ["D.Otherwise"] = "Otherwise:",
    };

    internal static bool IsHolder(CheatBlock block) => s_holders.ContainsKey(block.Spec);

    /// <summary>The sentence a block shows: a holder's without its number of blocks.</summary>
    internal static string Sentence(CheatSpec spec) => s_holders.GetValueOrDefault(spec.Id) ?? spec.Sentence;

    /// <summary>Whether a field is left off a block (the number of blocks a holder holds).</summary>
    internal static bool Hidden(CheatSpec spec, string key) => key == "Count" && s_holders.ContainsKey(spec.Id);

    /// <summary>How many blocks after the holder at i it holds, as the server counts them (at least 1, never past the end).</summary>
    internal static int Inside(IReadOnlyList<CheatBlock> dos, int i) => !IsHolder(dos[i]) ? 0 : Math.Min(Math.Max(1, dos[i].Number("Count")), dos.Count - i - 1);

    /// <summary>One line of the editor: a block (Index into the Dos) at a depth, or the end strip of the holder at Index.</summary>
    internal readonly record struct Line(int Index, int Depth, bool End);

    /// <summary>The Dos as the editor shows them: each block at its depth, with an end strip after each holder's blocks.</summary>
    internal static List<Line> Layout(IReadOnlyList<CheatBlock> dos) {
        var lines = new List<Line>();
        var open = new Stack<(int Holder, int End)>();
        for (var i = 0; i < dos.Count; i++) {
            while (open.Count > 0 && open.Peek().End <= i) {
                var (holder, _) = open.Pop();
                lines.Add(new Line(holder, open.Count, true));
            }

            lines.Add(new Line(i, open.Count, false));
            if (IsHolder(dos[i])) {
                // A holder inside another never reaches past its end.
                var end = i + 1 + Inside(dos, i);
                if (open.Count > 0) {
                    end = Math.Min(end, open.Peek().End);
                }

                open.Push((i, end));
            }
        }

        while (open.Count > 0) {
            var (holder, _) = open.Pop();
            lines.Add(new Line(holder, open.Count, true));
        }

        return lines;
    }

    // The holders whose blocks have the block at i in them.
    private static List<CheatBlock> HoldersOf(IReadOnlyList<CheatBlock> dos, int i)
        => [.. Enumerable.Range(0, Math.Min(i, dos.Count)).Where(h => IsHolder(dos[h]) && i <= h + Inside(dos, h)).Select(h => dos[h])];

    /// <summary>
    /// Where a block goes when put at a gap of the editor's lines (0: above the first): its index in the Dos, and the
    /// holders it goes into. Before a holder's end strip is inside that holder; after it, outside.
    /// </summary>
    internal static (int Index, List<CheatBlock> Into) At(IReadOnlyList<CheatBlock> dos, int gap) {
        var lines = Layout(dos);
        if (gap >= lines.Count) {
            return (dos.Count, []);
        }

        var line = lines[Math.Max(0, gap)];
        if (!line.End) {
            return (line.Index, HoldersOf(dos, line.Index));
        }

        var index = line.Index + 1 + Inside(dos, line.Index);

        return (index, [dos[line.Index], .. HoldersOf(dos, line.Index)]);
    }

    /// <summary>The blocks that move with the one at i: itself, and all a holder holds.</summary>
    internal static int Length(IReadOnlyList<CheatBlock> dos, int i) {
        var length = 1;
        if (IsHolder(dos[i])) {
            length += Inside(dos, i);
        }

        return length;
    }

    /// <summary>Puts blocks in at index, into the given holders. Returns the holders that had to take the block after them (left empty).</summary>
    internal static void Insert(List<CheatBlock> dos, int index, IReadOnlyList<CheatBlock> blocks, IEnumerable<CheatBlock> into) {
        // By what each holds now (an empty one at the very end holds none, though its number says 1).
        foreach (var (holder, now) in into.Distinct().Select(h => (h, Inside(dos, dos.IndexOf(h)))).ToList()) {
            holder.Set("Count", now + blocks.Count);
        }

        dos.InsertRange(Math.Clamp(index, 0, dos.Count), blocks);
    }

    /// <summary>
    /// Takes out the block at i (with the blocks it holds when whole is true; else only the block, so a holder's blocks
    /// stay where they were). Returns the holders left with none, which now hold the block after them, as the cheat file
    /// cannot say "none".
    /// </summary>
    internal static List<CheatBlock> Remove(List<CheatBlock> dos, int i, bool whole) {
        var length = whole ? Length(dos, i) : 1;
        var holders = Holding(dos, HoldersOf(dos, i));
        dos.RemoveRange(i, length);

        return Shrink(dos, holders, length);
    }

    // Holders with what each holds now.
    private static List<(CheatBlock Holder, int Now)> Holding(IReadOnlyList<CheatBlock> dos, IEnumerable<CheatBlock> holders)
        => [.. holders.Distinct().Select(h => (h, Inside(dos, IndexOf(dos, h))))];

    private static int IndexOf(IReadOnlyList<CheatBlock> dos, CheatBlock block) {
        for (var i = 0; i < dos.Count; i++) {
            if (ReferenceEquals(dos[i], block)) {
                return i;
            }
        }

        return -1;
    }

    // Holders that lost blocks; one left with none holds the block after it (or nothing at the very end).
    private static List<CheatBlock> Shrink(IReadOnlyList<CheatBlock> dos, List<(CheatBlock Holder, int Now)> holders, int by) {
        var emptied = new List<CheatBlock>();
        foreach (var (holder, now) in holders) {
            var count = now - by;
            if (count < 1) {
                emptied.Add(holder);
            }

            holder.Set("Count", Math.Max(1, count));
        }

        emptied.RemoveAll(h => { var at = IndexOf(dos, h); return at < 0 || at == dos.Count - 1; });

        return emptied;
    }

    /// <summary>
    /// Moves the block at from (with what it holds) to a gap of the editor's lines. False when the gap is inside the
    /// block itself. The holders it left that now hold nothing are given back in emptied.
    /// </summary>
    internal static bool Move(List<CheatBlock> dos, int from, int gap, out List<CheatBlock> emptied) {
        emptied = [];
        var length = Length(dos, from);
        var moving = dos.GetRange(from, length);
        var (to, into) = At(dos, gap);
        if (into.Any(moving.Contains) || to > from && to < from + length) {
            return false;
        }

        var left = HoldersOf(dos, from);
        // The holders it stays in keep their number.
        var both = left.Intersect(into).ToList();
        var leaving = Holding(dos, left.Except(both));
        dos.RemoveRange(from, length);
        emptied = Shrink(dos, leaving, length);
        if (to > from) {
            to -= length;
        }

        Insert(dos, to, moving, into.Except(both));
        emptied.RemoveAll(h => into.Contains(h));

        return true;
    }

    /// <summary>Copies the block at i (with what it holds) right after it, inside the same holders.</summary>
    internal static void Duplicate(List<CheatBlock> dos, int i) {
        var length = Length(dos, i);
        var copies = dos.GetRange(i, length).Select(b => b.Copy()).ToList();
        Insert(dos, i + length, copies, HoldersOf(dos, i));
    }

}

/// <summary>
/// Warnings on blocks that can't work as they stand (Niduus, 2026-11-19: "Let the blocking be more interactive"): shown on
/// the block itself while editing, so a cheat that would do nothing is seen before it is saved. They never stop a save.
/// </summary>
internal static class CheatProblems {

    internal static string? Of(CheatRule rule, CheatBlock block) {
        var spec = block.Kind;
        // A box that picks a spell, left empty.
        if (spec.Fields.Any(f => f.Type == CheatFieldType.Spell) && block.Text("Spell").Length == 0 && block.Id("SpellID") == 0
            && spec.Id is "D.Cast" or "D.FreeCast" or "D.CastBy") {
            return "No spell picked yet: this block casts nothing.";
        }

        if (spec.Fields.Any(f => f.Type == CheatFieldType.Wizard) && block.Text("Wizard").Length == 0 && block.Text("Name").Length == 0) {
            return "No wizard picked yet: this block never holds.";
        }

        var at = rule.Dos.IndexOf(block);
        if (at < 0) {
            return null;
        }

        if (CheatFlow.IsHolder(block) && CheatFlow.Inside(rule.Dos, at) == 0) {
            return "This holder holds no blocks yet: drag blocks above its end strip.";
        }

        if (block.Spec == "D.Otherwise") {
            // Right after an "only if" holder's blocks.
            var before = Enumerable.Range(0, at).LastOrDefault(h => CheatFlow.IsHolder(rule.Dos[h]) && h + CheatFlow.Inside(rule.Dos, h) == at - 1, -1);
            if (before < 0 || rule.Dos[before].Spec is "D.Repeat" or "D.Otherwise") {
                return "Otherwise works only right under an \"only if\" holder (only if a variable, only if something came off, only if converted). Here it never goes off.";
            }
        }

        if (block.Spec == "D.Stop" && at == rule.Dos.Count - 1) {
            return "Nothing comes after this Stop, so it does nothing.";
        }

        if (block.Spec == "D.Wait" && at == rule.Dos.Count - 1) {
            return "Nothing comes after this wait, so there is nothing to wait for.";
        }

        return null;
    }

}
