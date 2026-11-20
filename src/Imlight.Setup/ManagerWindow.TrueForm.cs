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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// Bots > True Form (Niduus, 2026-11-19: "add TF page in Ahziael"): the helper bot anyone can call. Where he is, who he is
/// helping and how he plays, his switches ([TrueForm] in Imlight.ini, live), his stats, gear, deck and jewels. The server's
/// True Form writes status.json, gear.txt and jewels.txt in Server\logs\trueform; this page reads them every 3 seconds.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string TRUE_FORM_TAB = "True Form";
    private const string TRUE_FORM_SECTION = "TrueForm";
    private static readonly string[] s_trueFormModes = ["Auto", "Attack", "Defend", "Support"];

    private readonly TextBlock _tfState = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _tfDetail = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ToggleSwitch _tfEnabled = new() { OnContent = "On", OffContent = "Off" };
    private readonly ToggleSwitch _tfDevSpells = new() { OnContent = "On", OffContent = "Off" };
    private readonly ToggleSwitch _tfLive = new() { OnContent = "On", OffContent = "Off" };
    private readonly ComboBox _tfMode = new() { Width = 200, ItemsSource = s_trueFormModes };
    private readonly TextBox _tfStitchAccount = new() { Width = 200, Watermark = "1234" };
    private readonly TextBox _tfStitchCharacter = new() { Width = 260, Watermark = "Myth Stitch" };
    private readonly TextBox _tfGear = new() {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = MONO, FontSize = 12.5, Height = 220,
    };
    private readonly TextBox _tfJewels = new() {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = MONO, FontSize = 12.5, Height = 220,
    };
    private readonly WizardSheet _tfSheet = new("True Form is not playing right now. Once he is, his stats, gear, deck and spells show here.") { Compact = true };
    private readonly DispatcherTimer _tfTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _showingTrueForm;
    // Stats, gear, jewels and deck are read when the page opens, on "Refresh now", or every tick with live updates on
    // (Niduus, 2026-11-20: "have TF page update dynamically as changes are made to stats and deck" / "Also to be turned
    // off by default").
    private bool _tfWasVisible;
    private bool _tfReadNow = true;
    private string _tfSheetText = "";

    private string TrueFormFolder => Path.Combine(_manager.LogFolder, "trueform");

    private Control TrueFormTab() {
        _tfEnabled.IsCheckedChanged += async (_, _) => {
            if (!_showingTrueForm) {
                await Guard(() => SaveTrueFormAsync("Enabled", _tfEnabled.IsChecked == true ? "true" : "false",
                    _tfEnabled.IsChecked == true ? "True Form is on. With the server running, he logs in within a few seconds." : "True Form is off. He logs out within a few seconds."));
            }
        };
        _tfDevSpells.IsCheckedChanged += async (_, _) => {
            if (!_showingTrueForm) {
                await Guard(() => SaveTrueFormAsync("DevSpells", _tfDevSpells.IsChecked == true ? "true" : "false",
                    _tfDevSpells.IsChecked == true ? "True Form may use dev spells while grouped with a player (never ones that one-shot)." : "True Form uses the game's own spells only."));
            }
        };
        _tfLive.IsCheckedChanged += async (_, _) => {
            if (!_showingTrueForm) {
                await Guard(() => SaveTrueFormAsync("LiveUpdates", _tfLive.IsChecked == true ? "true" : "false",
                    _tfLive.IsChecked == true ? "Live updates on: his stats, gear, jewels and deck redraw here as they change." : "Live updates off: this page reads him when opened or when you click Refresh now."));
            }
        };
        _tfMode.SelectionChanged += async (_, _) => {
            if (!_showingTrueForm && _tfMode.SelectedItem is string mode) {
                await Guard(() => SaveTrueFormAsync("Mode", mode, $"True Form plays {mode} when his group asks for nothing (from his next hand)."));
            }
        };

        LoadTrueFormSettings();
        ShowCardsOn(_tfSheet);
        _tfTimer.Tick += (_, _) => RefreshTrueForm();
        _tfTimer.Start();
        RefreshTrueForm();

        return Page(
            Fold("user", BOTS, "True Form",
                "The helper bot anyone can call (his real name is Ahziael). Top level Myth with every spell in the game, one of each. " +
                "Alone he waits at his spot in The Commons by the carousel. In the game, .trueform (or \"True Form, join me\") calls him: " +
                "he sends a group invite, hands you the lead, follows you and fights with you at your level. .trueform attack, defend, " +
                "support, auto or evil sets how he plays; .trueform use Dragonblade keeps a spell in his hand; .trueform leave sends him " +
                "home; .trueform where says where he is. One True Form for the whole server. Changes here count right away.",
                SpaceNotice("True Form"),
                _tfState,
                _tfDetail,
                SwitchRow("True Form", _tfEnabled, "On: he logs in and waits at his spot. Off: he logs out."),
                SwitchRow("How he plays", _tfMode, "When his group asks for nothing. Auto picks attack, defend or support each round from how the fight goes."),
                SwitchRow("Dev spells", _tfDevSpells, "While grouped with a player he may also use dev spells, never ones that one-shot."),
                SwitchRow("Look from account", _tfStitchAccount, "The account of the character whose hat, robe, boots and wand he looks like."),
                SwitchRow("Look from character", _tfStitchCharacter, "That character is only looked at, never changed. He takes the look when the server starts him."),
                Row(Action(Plain("Save look"), SaveTrueFormLookAsync), Action(Plain("Open his folder"), () => {
                    Directory.CreateDirectory(TrueFormFolder);

                    return Open(TrueFormFolder);
                }))),
            Fold("user", BOTS, "His stats, gear, deck and spells",
                "As the game has him: read when this page opens and when you click Refresh now, or every few seconds with Live updates on. " +
                "Under each piece: its sockets and the jewel in each; under his pet: its talents.",
                SwitchRow("Live updates", _tfLive, "On: this page redraws his stats, gear, jewels and deck every few seconds as they change. Off (the default): only when opened or refreshed."),
                Row(Action(Plain("Refresh now"), () => {
                    _tfReadNow = true;
                    RefreshTrueForm();
                    Say("True Form's stats, gear, jewels and deck read again.");

                    return Task.CompletedTask;
                })),
                _tfSheet.View),
            Fold("user", BOTS, "His gear and jewels",
                "What he wears (the top level pieces for a Myth wizard who hits, shields and heals) and the jewel in every socket. " +
                "From his last start: logs\\trueform\\gear.txt and jewels.txt.",
                new TextBlock { Text = "Gear", FontWeight = FontWeight.SemiBold },
                LogWell(_tfGear),
                new TextBlock { Text = "Jewels", FontWeight = FontWeight.SemiBold },
                LogWell(_tfJewels)));
    }

    private void LoadTrueFormSettings() {
        _showingTrueForm = true;
        try {
            _tfEnabled.IsChecked = !string.Equals(_manager.GetSetting(TRUE_FORM_SECTION, "Enabled")?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
            _tfDevSpells.IsChecked = !string.Equals(_manager.GetSetting(TRUE_FORM_SECTION, "DevSpells")?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
            _tfLive.IsChecked = string.Equals(_manager.GetSetting(TRUE_FORM_SECTION, "LiveUpdates")?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
            var mode = _manager.GetSetting(TRUE_FORM_SECTION, "Mode")?.Trim() ?? "Auto";
            _tfMode.SelectedItem = s_trueFormModes.FirstOrDefault(m => m.Equals(mode, StringComparison.OrdinalIgnoreCase)) ?? "Auto";
            _tfStitchAccount.Text = _manager.GetSetting(TRUE_FORM_SECTION, "StitchAccount")?.Trim() ?? "";
            _tfStitchCharacter.Text = _manager.GetSetting(TRUE_FORM_SECTION, "StitchCharacter")?.Trim() ?? "";
        }
        finally {
            _showingTrueForm = false;
        }
    }

    private Task SaveTrueFormAsync(string key, string value, string said) {
        if (!_manager.HasSetting(TRUE_FORM_SECTION, key)) {
            LoadTrueFormSettings();
            throw new PanelException($"This server's Imlight.ini has no [TrueForm] {key} yet. Click \"Update Ahziael\" once, then try again.");
        }

        _manager.SaveSettings([new Setting(TRUE_FORM_SECTION, key, value, "")]);
        _settings = [];
        Say(said);
        RefreshTrueForm();

        return Task.CompletedTask;
    }

    private Task SaveTrueFormLookAsync() {
        if (!_manager.HasSetting(TRUE_FORM_SECTION, "StitchAccount") || !_manager.HasSetting(TRUE_FORM_SECTION, "StitchCharacter")) {
            throw new PanelException("This server's Imlight.ini has no [TrueForm] StitchAccount yet. Click \"Update Ahziael\" once, then try again.");
        }

        var account = (_tfStitchAccount.Text ?? "").Trim();
        var character = (_tfStitchCharacter.Text ?? "").Trim();
        _manager.SaveSettings([
            new Setting(TRUE_FORM_SECTION, "StitchAccount", account, ""),
            new Setting(TRUE_FORM_SECTION, "StitchCharacter", character, ""),
        ]);
        _settings = [];
        Say(account.Length == 0 || character.Length == 0
            ? "Saved: True Form keeps his gear's own look."
            : $"Saved: True Form takes the look of {character} on account {account} the next time the server starts him.");

        return Task.CompletedTask;
    }

    private void RefreshTrueForm() {
        if (!_tfState.IsEffectivelyVisible) {
            _tfWasVisible = false;
            return;
        }

        // Coming back to the page reads him once; after that only live updates or Refresh now do.
        if (!_tfWasVisible) {
            _tfWasVisible = true;
            _tfReadNow = true;
        }

        var readAll = _tfReadNow || _tfLive.IsChecked == true;

        var running = InstalledCommands.IsServerRunning(_manager.InstallFolder);
        var status = ReadTco7Status(TrueFormFolder);
        var at = DateTime.TryParse((string?) status?["at"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var written)
            ? written.ToUniversalTime() : DateTime.MinValue;
        var live = running && status is not null && DateTime.UtcNow - at < TimeSpan.FromSeconds(30);
        var me = (status?["members"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
        var tf = status?["trueForm"] as JsonObject;
        _tfState.Text = !running ? "The server is not running, so True Form is not either."
            : _tfEnabled.IsChecked != true ? "Off: True Form is logged out."
            : !live ? "Starting: True Form logs in within a few seconds (the first start reads the game's gear and may take a few minutes)."
            : $"Playing: level {(int?) me?["level"] ?? 0} in {(string?) me?["zone"] ?? "?"}"
              + ((bool?) me?["battle"] == true ? ", in a battle" : "")
              + ((string?) tf?["with"] is { Length: > 0 } with ? $", helping {with}." : ", free: anyone can call him with .trueform.");
        _tfState.Foreground = live ? ManagerTheme.Accent("good") : MUTED;
        _tfDetail.Text = !live || tf is null ? ""
            : $"He plays {(string?) tf["mode"]}{((bool?) tf["asked"] == true ? " (his group asked)" : "")}. " +
              $"His spells match level {(int?) tf["spellLevel"] ?? 0}{((bool?) tf["devSpells"] == true ? ", dev spells on" : "")}. " +
              $"His spot: {(string?) tf["home"]}." +
              ((tf["requested"] as JsonArray) is { Count: > 0 } kept ? $" Kept in his hand: {string.Join(", ", kept.Select(k => (string?) k))}." : "") +
              $" Health {(string?) me?["health"] ?? "?"}.";

        if (!readAll) {
            return;
        }

        var gear = ReadAllOf(Path.Combine(TrueFormFolder, "gear.txt"), 64 * 1024).TrimEnd();
        gear = gear.Length > 0 ? gear : "Nothing yet: written when the server makes him ready.";
        if (_tfGear.Text != gear) {
            _tfGear.Text = gear;
        }

        var jewels = ReadAllOf(Path.Combine(TrueFormFolder, "jewels.txt"), 64 * 1024).TrimEnd();
        jewels = jewels.Length > 0 ? jewels : "Nothing yet: written when the server makes him ready.";
        if (_tfJewels.Text != jewels) {
            _tfJewels.Text = jewels;
        }

        if (_tfSheet.View.IsEffectivelyVisible) {
            _tfReadNow = false;
            var members = live ? me is null ? "" : status!["members"]!.ToJsonString() : "";
            if (members != _tfSheetText) {
                _tfSheetText = members;
                _tfSheet.Show(members.Length == 0 ? null : JsonNode.Parse(members) as JsonArray);
            }
        }
    }

}
