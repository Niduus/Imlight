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
using Avalonia.Layout;
using Avalonia.Media;
using Imlight.Setup.Blocks;

namespace Imlight.Setup;

/// <summary>
/// Who gets a player cheat, picked in one box (Niduus, 2026-11-19: "Allow for Server owners to make cheats for individual
/// players, this way, the host doesn't have to be in the same fight to proc said cheats"). The server could already give
/// a cheat to any wizard on any PC through the "who gets it" If blocks (PlayerCheats.cs); this box writes those blocks
/// for the owner: only the wizard on this PC, every wizard, picked wizards, or every wizard of picked accounts. Picked
/// wizards (or accounts) are one If block each in one OR group, so the cheat is each one's own, in any battle, with the
/// owner nowhere near. A cheat whose blocks say something else (NOT, a school, a friend...) shows as "set by the If
/// blocks", and its blocks are left as they are until another choice is picked. The list page can show the cheats of one
/// wizard only ("Show cheats for"); a new cheat made then is for that wizard.
/// </summary>
internal sealed partial class ManagerWindow {

    private enum CheatWhoMode { ThisPc, Everyone, Wizards, Accounts, Custom }

    // The "who gets it" Ifs the box writes and reads. The others (friend, group, rank...) make it Custom.
    private static readonly HashSet<string> s_whoSpecs = ["C.Wizard", "C.WizardName", "C.Account", "C.ThisPc", "C.Friend", "C.Grouped", "C.Rank", "C.AnyWizard"];

    private static bool IsWhoBlock(CheatBlock b) => s_whoSpecs.Contains(b.Spec) && b.Text("Who").Length == 0;

    // A block the box itself would write: about "it", not turned round.
    private static bool IsPlainWho(CheatBlock b) => IsWhoBlock(b) && !b.Flag("Not") && b.Spec is "C.Wizard" or "C.Account" or "C.AnyWizard" or "C.ThisPc";

    private static (CheatWhoMode Mode, List<string> Picked) WhoOf(CheatRule rule) {
        var who = rule.Ifs.Where(IsWhoBlock).ToList();
        if (who.Count == 0) {
            return (CheatWhoMode.ThisPc, []);
        }

        if (who.Any(b => !IsPlainWho(b))) {
            return (CheatWhoMode.Custom, []);
        }

        // One block on its own, or all in one OR group with nothing else in it.
        var groups = who.Select(b => b.Number("Any")).Distinct().ToList();
        var oneGroup = who.Count == 1 || groups.Count == 1 && groups[0] > 0
            && rule.Ifs.Where(b => b.Number("Any") == groups[0]).All(IsWhoBlock);
        if (!oneGroup) {
            return (CheatWhoMode.Custom, []);
        }

        if (who.All(b => b.Spec == "C.ThisPc")) {
            return (CheatWhoMode.ThisPc, []);
        }

        if (who.All(b => b.Spec == "C.AnyWizard")) {
            return (CheatWhoMode.Everyone, []);
        }

        if (who.All(b => b.Spec == "C.Wizard" && b.Text("Wizard").Length > 0 && !b.Text("Wizard").Contains(','))) {
            return (CheatWhoMode.Wizards, [.. who.Select(b => b.Text("Wizard"))]);
        }

        if (who.All(b => b.Spec == "C.Account" && b.Text("Account").Length > 0 && !b.Text("Account").Contains(','))) {
            return (CheatWhoMode.Accounts, [.. who.Select(b => b.Text("Account"))]);
        }

        return (CheatWhoMode.Custom, []);
    }

    /// <summary>Writes who gets the cheat as its "who gets it" Ifs (at the top of its Ifs), taking out the ones there were.</summary>
    private void SetWho(CheatRule rule, CheatWhoMode mode, IReadOnlyList<string> picked) {
        rule.Ifs.RemoveAll(IsWhoBlock);
        var blocks = new List<CheatBlock>();
        switch (mode) {
            case CheatWhoMode.Everyone:
                blocks.Add(CheatBlock.New(CheatSpecs.Get("C.AnyWizard")));
                break;
            case CheatWhoMode.Wizards:
                var list = WizardListNow() ?? [];
                foreach (var id in picked) {
                    var (account, wizard) = list.SelectMany(a => a.Wizards.Select(w => (a.Account, w))).FirstOrDefault(x => x.w.Id == id);
                    var block = CheatBlock.New(CheatSpecs.Get("C.Wizard"));
                    block.Set("Wizard", id);
                    block.Set("Name", wizard?.Name ?? "");
                    block.Set("Account", account ?? "");
                    blocks.Add(block);
                }

                break;
            case CheatWhoMode.Accounts:
                foreach (var account in picked) {
                    var block = CheatBlock.New(CheatSpecs.Get("C.Account"));
                    block.Set("Account", account);
                    blocks.Add(block);
                }

                break;
        }

        // Two or more: one OR group, a number no other If of the cheat uses.
        if (blocks.Count > 1) {
            var used = rule.Ifs.Select(b => b.Number("Any")).ToHashSet();
            var group = Enumerable.Range(1, 9).FirstOrDefault(n => !used.Contains(n), 9);
            foreach (var block in blocks) {
                block.Set("Any", group);
            }
        }

        rule.Ifs.InsertRange(0, blocks);
    }

    // The words for who gets a cheat (the list's "Show cheats for" and the cheat's head).
    private string WhoWords(CheatRule rule) {
        var (mode, picked) = WhoOf(rule);
        var list = WizardListNow() ?? [];

        return mode switch {
            CheatWhoMode.ThisPc => "the wizard you play on this PC",
            CheatWhoMode.Everyone => "every wizard",
            CheatWhoMode.Wizards => string.Join(", ", picked.Select(id => list.SelectMany(a => a.Wizards).FirstOrDefault(w => w.Id == id)?.Name ?? $"wizard {id}")),
            CheatWhoMode.Accounts => "the wizards of " + string.Join(", ", picked),
            _ => "the wizards its If blocks pick",
        };
    }

    /// <summary>The box at the top of a player cheat's edit page: who gets this cheat.</summary>
    private Control WhoGetsBox(CheatBoard board, CheatRule rule) {
        var (mode, picked) = WhoOf(rule);
        var modes = new List<(string Value, string Label)> {
            (nameof(CheatWhoMode.ThisPc), "Only me (the wizard I play on this PC)"),
            (nameof(CheatWhoMode.Everyone), "Every wizard on the server"),
            (nameof(CheatWhoMode.Wizards), "Only the wizards I tick"),
            (nameof(CheatWhoMode.Accounts), "Every wizard on the accounts I tick"),
        };
        if (mode == CheatWhoMode.Custom) {
            modes.Add((nameof(CheatWhoMode.Custom), "Set by the If blocks below"));
        }

        var ticks = new WrapPanel { Orientation = Orientation.Horizontal };
        var note = Sub("");
        var holder = new StackPanel { Spacing = 6 };

        void Changed() {
            UpdateCheatHead(board, rule);
            // The blocks below show the "who gets it" Ifs the box wrote.
            RenderEditPage(board, rule, opened: false);
        }

        void ShowTicks(CheatWhoMode now, List<string> chosen) {
            ticks.Children.Clear();
            var wizards = WizardListNow();
            note.Text = now switch {
                CheatWhoMode.ThisPc => "Goes off for the wizard you play on this PC only, as before.",
                CheatWhoMode.Everyone => "Every wizard gets it, on any PC, in any battle. Add If blocks (school, level...) below to narrow it.",
                CheatWhoMode.Wizards => "Each ticked wizard gets it in their own battles, on any PC. You don't need to be in the fight.",
                CheatWhoMode.Accounts => "Every wizard on each ticked account gets it in their own battles, on any PC.",
                _ => "The If blocks below say who gets it (NOT, friend of, in a group with, rank...). Pick another choice above to replace them.",
            };
            if (now is not (CheatWhoMode.Wizards or CheatWhoMode.Accounts)) {
                return;
            }

            if (wizards is null || wizards.Count == 0) {
                ticks.Children.Add(Sub(NO_WIZARD_LIST + " (or use a \"wizard's name is\" If block)."));

                return;
            }

            void Tick(string value, string label, string school) {
                var box = new CheckBox { IsChecked = chosen.Contains(value), Margin = new Thickness(0, 0, 14, 2) };
                box.Content = new StackPanel {
                    Orientation = Orientation.Horizontal, Spacing = 6,
                    Children = { SchoolBadge(school, 16), new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center } },
                };
                box.IsCheckedChanged += (_, _) => Guarded(() => {
                    chosen.Remove(value);
                    if (box.IsChecked == true) {
                        chosen.Add(value);
                    }

                    SetWho(rule, now, chosen);
                    Changed();
                });
                ticks.Children.Add(box);
            }

            if (now == CheatWhoMode.Accounts) {
                foreach (var account in wizards) {
                    Tick(account.Account, $"{account.Account} ({account.Wizards.Count} wizard{(account.Wizards.Count == 1 ? "" : "s")})", "");
                }

                return;
            }

            foreach (var account in wizards.Where(a => a.Wizards.Count > 0)) {
                ticks.Children.Add(new TextBlock {
                    Text = account.Account, FontWeight = FontWeight.SemiBold, Foreground = MUTED, Margin = new Thickness(0, 4, 10, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                foreach (var wizard in account.Wizards) {
                    Tick(wizard.Id, $"{wizard.Name} (level {wizard.Level})", wizard.School);
                }
            }
        }

        var choice = BlockFields.Choice(modes, mode.ToString(), value => Guarded(() => {
            var now = Enum.Parse<CheatWhoMode>(value);
            if (now == CheatWhoMode.Custom) {
                return;
            }

            SetWho(rule, now, []);
            Changed();
        }));
        ShowTicks(mode, picked);
        holder.Children.Add(new StackPanel {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { new TextBlock { Text = "Who gets this cheat:", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }, choice },
        });
        holder.Children.Add(note);
        holder.Children.Add(ticks);

        return ManagerTheme.Item(holder);
    }

    // The list page's "Show cheats for": every cheat, or those one wizard gets (picked by id), or the ones of this PC.
    private string _playerCheatsFor = "";

    private const string FOR_THIS_PC = "@thispc";

    private Control ShowCheatsForBox(CheatBoard board) {
        var options = new List<(string, string)> { ("", "every cheat"), (FOR_THIS_PC, "the wizard I play on this PC") };
        foreach (var account in WizardListNow() ?? []) {
            options.AddRange(account.Wizards.Select(w => (w.Id, $"{w.Name} ({account.Account}, {(w.School.Length > 0 ? w.School : "no school")} {w.Level})")));
        }

        var choice = BlockFields.Choice(options, _playerCheatsFor, value => Guarded(() => {
            _playerCheatsFor = value;
            RenderCheats(board);
        }));
        ToolTip.SetTip(choice, "Pick a wizard to see only the cheats that wizard gets. New cheat then makes a cheat for that wizard.");

        return new StackPanel {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { new TextBlock { Text = "Show cheats for:", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }, choice },
        };
    }

    /// <summary>Whether the list shows this cheat under "Show cheats for" (a cheat set by its If blocks shows for everyone, as it may be theirs).</summary>
    private bool ShownFor(CheatRule rule) {
        if (_playerCheatsFor.Length == 0) {
            return true;
        }

        var (mode, picked) = WhoOf(rule);
        if (_playerCheatsFor == FOR_THIS_PC) {
            return mode is CheatWhoMode.ThisPc or CheatWhoMode.Everyone or CheatWhoMode.Custom;
        }

        var account = (WizardListNow() ?? []).FirstOrDefault(a => a.Wizards.Any(w => w.Id == _playerCheatsFor))?.Account ?? "";

        return mode switch {
            CheatWhoMode.Everyone or CheatWhoMode.Custom => true,
            CheatWhoMode.Wizards => picked.Contains(_playerCheatsFor),
            CheatWhoMode.Accounts => picked.Contains(account, StringComparer.OrdinalIgnoreCase),
            _ => false,
        };
    }

    /// <summary>A new player cheat made while one wizard is shown is for that wizard.</summary>
    private void NewCheatFor(CheatRule rule) {
        if (_playerCheatsFor.Length > 0 && _playerCheatsFor != FOR_THIS_PC) {
            SetWho(rule, CheatWhoMode.Wizards, [_playerCheatsFor]);
        }
    }

}
