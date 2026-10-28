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
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Layout;
using Imlight.Setup.Blocks;

namespace Imlight.Setup;

/// <summary>
/// The cheat blocks' wizard and account boxes (Niduus, 2026-10-28: "picking their account and having the condition of 'If
/// wizard is' and have a drop down of the wizard list on said account with school"). The server writes every account and
/// its wizards to wizard-list.json (the server's WizardList.cs) a little after it starts and when a wizard comes in.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string WIZARD_LIST_FILE = "wizard-list.json";

    private sealed record ListedWizard(string Id, string Name, string School, int Level);

    private sealed record ListedAccount(string Account, List<ListedWizard> Wizards);

    private List<ListedAccount>? _wizardList;
    private DateTime _wizardListTime;

    // The server's list, read again when it changed. Null when the server has not written one yet.
    private List<ListedAccount>? WizardListNow() {
        var file = Path.Combine(_manager.ServerFolder, WIZARD_LIST_FILE);
        try {
            var time = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
            if (time == DateTime.MinValue) {
                return _wizardList;
            }

            if (_wizardList is not null && time == _wizardListTime) {
                return _wizardList;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(file));
            _wizardList = [
                .. document.RootElement.GetProperty("Accounts").EnumerateArray().Select(a => new ListedAccount(Text(a, "Account"),
                    a.TryGetProperty("Wizards", out var wizards) && wizards.ValueKind == JsonValueKind.Array
                        ? [.. wizards.EnumerateArray().Select(w => new ListedWizard(Text(w, "Id"), Text(w, "Name"), Text(w, "School"),
                            w.TryGetProperty("Level", out var level) && level.TryGetInt32(out var n) ? n : 0))]
                        : [])),
            ];
            _wizardListTime = time;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException) {
            // Being written: the last one read stays.
        }

        return _wizardList;

        static string Text(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    }

    private const string NO_WIZARD_LIST = "Start the server once (Home page) so it can list every account and its wizards; until then type the wizard's whole name.";

    // An account, then one of its wizards with its school and level. With no list yet, the wizard's name is typed.
    private Control WizardField(CheatBlock block, Action changed) {
        var list = WizardListNow();
        if (list is null || list.Count == 0) {
            var typed = BlockFields.Words(block.Text("Name"), t => {
                block.Set("Name", t.Trim());
                block.Set("Wizard", "");
                changed();
            }, "the wizard's whole name");
            ToolTip.SetTip(typed, NO_WIZARD_LIST);

            return typed;
        }

        var badge = new Border();
        var wizards = new ContentControl();
        void ShowWizards(string account) {
            var on = list.FirstOrDefault(a => a.Account.Equals(account, StringComparison.OrdinalIgnoreCase))?.Wizards ?? [];
            var options = on.Select(w => (w.Id, $"{w.Name}  ·  {(w.School.Length > 0 ? w.School : "no school")}, level {w.Level}")).ToList();
            var current = block.Text("Wizard");
            if (current.Length == 0 && block.Text("Name").Length > 0 && on.FirstOrDefault(w => w.Name.Equals(block.Text("Name"), StringComparison.OrdinalIgnoreCase)) is { } named) {
                current = named.Id;
            }

            options.Insert(0, ("", on.Count == 0 ? "no wizards on this account" : "pick a wizard"));
            badge.Child = SchoolBadge(on.FirstOrDefault(w => w.Id == current)?.School ?? "", 20);
            wizards.Content = BlockFields.Choice(options, current, id => {
                var wizard = on.FirstOrDefault(w => w.Id == id);
                block.Set("Wizard", wizard?.Id ?? "");
                block.Set("Name", wizard?.Name ?? "");
                badge.Child = SchoolBadge(wizard?.School ?? "", 20);
                changed();
            });
        }

        var accounts = list.Select(a => (a.Account, $"{a.Account} ({a.Wizards.Count} wizard{(a.Wizards.Count == 1 ? "" : "s")})")).ToList();
        accounts.Insert(0, ("", "pick an account"));
        var account = BlockFields.Choice(accounts, block.Text("Account"), name => {
            block.Set("Account", name);
            block.Set("Wizard", "");
            block.Set("Name", "");
            ShowWizards(name);
            changed();
        });
        ToolTip.SetTip(account, "The account first, then one of its wizards. The list comes from the server, so a wizard made a moment ago shows once it has logged in.");
        ShowWizards(block.Text("Account"));

        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { account, WithCenter(badge), wizards } };
    }

    // An account (any account: every wizard).
    private Control AccountField(CheatBlock block, string key, Action changed) {
        var list = WizardListNow();
        if (list is null || list.Count == 0) {
            var typed = BlockFields.Words(block.Text(key), t => {
                block.Set(key, t.Trim());
                changed();
            }, "the account's username (empty: any)");
            ToolTip.SetTip(typed, NO_WIZARD_LIST);

            return typed;
        }

        var options = list.Select(a => (a.Account, a.Account)).ToList();
        options.Insert(0, ("", "any account"));

        return BlockFields.Choice(options, block.Text(key), name => {
            block.Set(key, name);
            changed();
        });
    }

}
