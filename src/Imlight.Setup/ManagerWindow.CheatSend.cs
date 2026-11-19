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
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Imlight.Setup.Blocks;

namespace Imlight.Setup;

/// <summary>
/// Copy, move or link a player cheat to a wizard or an account that already exists (Niduus, 2026-11-19: "in cheats add
/// ability to move, link and/or copy cheats to an existing wizard/account"). Copy makes a second cheat, of that wizard
/// (or account) only, to change on its own. Move gives this cheat to that wizard (or account) instead of whoever had it.
/// Link adds that wizard (or account) to whoever has it, so one cheat is shared and a change to it counts for all of
/// them. Each writes the cheat's "who gets it" If blocks, as the Who gets this cheat box does.
/// </summary>
internal sealed partial class ManagerWindow {

    private enum CheatSend { Copy, Move, Link }

    /// <summary>Whoever has the cheat as picks ("pc", "w:id", "a:account"); null for every wizard or a cheat set by its own If blocks.</summary>
    private static List<string>? WhoItems(CheatRule rule) {
        var (mode, picked) = WhoOf(rule);

        return mode switch {
            CheatWhoMode.ThisPc => ["pc"],
            CheatWhoMode.Wizards => [.. picked.Select(id => "w:" + id)],
            CheatWhoMode.Accounts => [.. picked.Select(a => "a:" + a)],
            CheatWhoMode.Mixed => picked,
            _ => null,
        };
    }

    // Picks written back the plainest way: no blocks for this PC only, the plain wizards or accounts box, else mixed.
    private void SetWhoItems(CheatRule rule, List<string> items) {
        items = [.. items.Distinct(StringComparer.OrdinalIgnoreCase)];
        if (items.SequenceEqual(["pc"])) {
            SetWho(rule, CheatWhoMode.ThisPc, []);
        }
        else if (items.All(i => i.StartsWith("w:", StringComparison.Ordinal))) {
            SetWho(rule, CheatWhoMode.Wizards, [.. items.Select(i => i[2..])]);
        }
        else if (items.All(i => i.StartsWith("a:", StringComparison.Ordinal))) {
            SetWho(rule, CheatWhoMode.Accounts, [.. items.Select(i => i[2..])]);
        }
        else {
            SetWho(rule, CheatWhoMode.Mixed, items);
        }
    }

    /// <summary>Copies, moves or links a cheat to one pick. Returns what it did, in words.</summary>
    private string SendCheat(CheatBoard board, CheatRule rule, CheatSend how, string target) {
        var list = board.List!.Cheats;
        var words = PickWords(target);
        switch (how) {
            case CheatSend.Copy: {
                var copy = rule.Copy();
                SetWhoItems(copy, [target]);
                list.Insert(list.IndexOf(rule) + 1, copy);

                return $"Copied for {words}, right under this one. Change the copy on its own; the first one stays as it was.";
            }
            case CheatSend.Move:
                SetWhoItems(rule, [target]);

                return $"Moved: this cheat is now for {words} only.";
            default: {
                var items = WhoItems(rule);
                if (items is null) {
                    var (mode, _) = WhoOf(rule);

                    return mode == CheatWhoMode.Everyone ? "This cheat is already for every wizard, so there is nothing to link."
                        : throw new PanelException("This cheat's If blocks say who gets it (NOT, friend of, rank...), so a link can't be added. Use Copy to, or Move to.");
                }

                if (items.Contains(target, StringComparer.OrdinalIgnoreCase)) {
                    return $"{char.ToUpperInvariant(words[0])}{words[1..]} already has this cheat.";
                }

                SetWhoItems(rule, [.. items, target]);

                return $"Linked: {words} shares this cheat now ({WhoWords(rule)}). A change to it counts for all of them.";
            }
        }
    }

    /// <summary>The button on a player cheat that copies, moves or links it to a wizard or account.</summary>
    private Control SendCheatButton(CheatBoard board, CheatRule rule) {
        var button = new Button { Content = BlockIcons.Icon("link", 15, Brushes.White), Padding = new Thickness(6) };
        ToolTip.SetTip(button, "Copy, move or link this cheat to a wizard or account");
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight, ShowMode = FlyoutShowMode.Standard };
        button.Flyout = flyout;
        button.Click += (_, _) => flyout.Content = SendCheatPanel(board, rule, flyout);

        return button;
    }

    private Control SendCheatPanel(CheatBoard board, CheatRule rule, Flyout flyout) {
        var wizards = WizardListNow();
        var how = CheatSend.Copy;
        var target = "";
        var hows = new List<(string, string)> {
            (nameof(CheatSend.Copy), "Copy to (a second cheat, just for them)"),
            (nameof(CheatSend.Move), "Move to (give this cheat to them instead)"),
            (nameof(CheatSend.Link), "Link to (share this cheat with them too)"),
        };
        var targets = new List<(string, string)> { ("", "pick a wizard or account"), ("pc", "Me (the wizard I play on this PC)") };
        foreach (var account in wizards ?? []) {
            targets.Add(("a:" + account.Account, $"Account {account.Account}: all {account.Wizards.Count} wizard{(account.Wizards.Count == 1 ? "" : "s")}"));
            targets.AddRange(account.Wizards.Select(w => ("w:" + w.Id, $"    {w.Name} ({account.Account}, {(w.School.Length > 0 ? w.School : "no school")} {w.Level})")));
        }

        var note = Sub("");
        void Explain() => note.Text = how switch {
            CheatSend.Copy => "Makes a new cheat like this one for them only. Each can then be changed on its own.",
            CheatSend.Move => $"This cheat stops being for {WhoWords(rule)} and is for them only.",
            _ => $"Now for {WhoWords(rule)}. Linking adds them, so the same cheat is theirs too and one change counts for all.",
        };
        Explain();

        var go = Action(Primary("Do it"), () => {
            if (target.Length == 0) {
                throw new PanelException("Pick a wizard or account first.");
            }

            var done = SendCheat(board, rule, how, target);
            flyout.Hide();
            RenderCheats(board);
            Say(done + " Press Save player cheats to keep it.");

            return System.Threading.Tasks.Task.CompletedTask;
        });

        var panel = new StackPanel {
            Spacing = 8, Width = 430,
            Children = {
                new TextBlock { Text = "Copy, move or link this cheat", FontWeight = FontWeight.SemiBold },
                BlockFields.Choice(hows, how.ToString(), v => {
                    how = Enum.Parse<CheatSend>(v);
                    Explain();
                }),
                BlockFields.Choice(targets, "", v => target = v),
                note,
                go,
            },
        };
        if (wizards is null || wizards.Count == 0) {
            panel.Children.Insert(3, Sub(NO_WIZARD_LIST));
        }

        return panel;
    }

}
