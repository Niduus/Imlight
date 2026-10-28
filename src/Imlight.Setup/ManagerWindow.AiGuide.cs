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
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Imlight.Setup.Blocks;

namespace Imlight.Setup;

/// <summary>
/// The AI Guide page (Niduus, 2026-10-28): ask Claude, with your own key, what Ahziael can do, where things are and what
/// the server does. It is a guide only: it is given no tools, so its answers are words and nothing it says is run.
/// The reference it reads is made from what the app already knows (its pages, commands, guides, settings and patch
/// notes), so its answers follow the app as it changes.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string AI_GUIDE = "AI Guide";

    private readonly TextBox _aiKey = new() { Watermark = "Paste your key here (it starts with sk-ant-)", PasswordChar = '•', MinWidth = 320 };
    private readonly ComboBox _aiModel = new() { MinWidth = 260 };
    private readonly TextBlock _aiModelLine = new() { Foreground = MUTED, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _aiKeyState = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _aiChat = new() { Spacing = 10 };
    private readonly ScrollViewer _aiChatScroll = new() { MinHeight = 260, MaxHeight = 560 };
    private readonly WrapPanel _aiStarters = new();
    private readonly TextBox _aiQuestion = new() {
        Watermark = "Ask anything about Ahziael or your server. Enter sends; Shift+Enter starts a new line.",
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 44, MaxHeight = 140,
    };
    private readonly Button _aiAsk = Primary("Ask");
    private readonly Button _aiStop = Plain("Stop");
    private Section? _aiKeyCard;
    private readonly List<AiGuideTurn> _aiTurns = [];
    private CancellationTokenSource? _aiCancel;
    private string? _aiReference;

    private static readonly string[] s_aiStarters = [
        "What can Ahziael do?",
        "How do I start the server and play?",
        "How do I make a custom boss?",
        "Which settings change how battles work?",
        "How do I let a friend join my server?",
        "What does randomizer mode change?",
    ];

    private Control AiGuidePage() {
        var settings = AiGuide.Load(_manager.InstallFolder);
        _aiKey.Text = settings.Key;
        _aiModel.ItemsSource = AiGuide.MODELS.Select(m => m.Name).ToArray();
        _aiModel.SelectedIndex = Math.Max(0, Array.FindIndex(AiGuide.MODELS, m => m.Id == settings.Model));
        _aiModelLine.Text = AiGuide.MODELS[_aiModel.SelectedIndex].Line;
        _aiModel.SelectionChanged += (_, _) => {
            if (_aiModel.SelectedIndex < 0) {
                return;
            }

            _aiModelLine.Text = AiGuide.MODELS[_aiModel.SelectedIndex].Line;
            // The model is kept with the key, so it is remembered next time.
            var saved = AiGuide.Load(_manager.InstallFolder);
            if (saved.Key.Length > 0) {
                _ = Guard(() => {
                    AiGuide.Save(_manager.InstallFolder, saved with { Model = AiGuide.MODELS[_aiModel.SelectedIndex].Id });

                    return Task.CompletedTask;
                });
            }
        };

        var steps = new StackPanel { Spacing = 4 };
        string[] howTo = [
            "Press Get a key. Anthropic's website opens in your browser. Sign up, or log in if you have an account (an email address is enough).",
            "Press Add credit and add a small amount, such as $5. You pay only for the questions you ask. A Claude.ai Pro or Max plan does not cover this; it is billed separately.",
            "On the API keys page, press Create Key, name it Ahziael, and press Copy. The key starts with sk-ant- and is only shown once.",
            "Paste it in the box below and press Save key. That's it: ask away.",
        ];
        for (var i = 0; i < howTo.Length; i++) {
            steps.Children.Add(new TextBlock { Text = $"{i + 1}. {howTo[i]}", TextWrapping = TextWrapping.Wrap });
        }

        _aiKeyCard = Fold("key", AI_GUIDE, "Your key",
            "The AI Guide uses Claude, made by Anthropic, with your own key. No key comes with Ahziael.",
            _aiKeyState,
            Sub("No key yet? Four steps:"),
            steps,
            new WrapPanel {
                Children = {
                    Spaced(Action(Plain("Get a key"), () => { OpenInBrowser(AiGuide.KEYS_PAGE); return Task.CompletedTask; })),
                    Spaced(Action(Plain("Add credit"), () => { OpenInBrowser(AiGuide.BILLING_PAGE); return Task.CompletedTask; })),
                },
            },
            new WrapPanel {
                Children = {
                    Spaced(_aiKey),
                    Spaced(Action(Primary("Save key"), SaveAiKeyAsync)),
                    Spaced(Danger(Action(Plain("Remove key"), RemoveAiKeyAsync))),
                },
            },
            SwitchRow("Model", _aiModel, null),
            _aiModelLine,
            Sub("The guide reads all of Ahziael's help with every chat, which is why the first question costs more. After about five minutes " +
                "without a question, the next one costs like a first one again."),
            Sub("Your key is saved only on this PC, in Ahziael's own folder, and is sent only to Anthropic when you ask a question."));
        ShowAiKeyState(settings.Key);

        foreach (var starter in s_aiStarters) {
            var question = starter;
            _aiStarters.Children.Add(Spaced(Action(Plain(question), () => AskAiAsync(question))));
        }

        Action(_aiAsk, () => AskAiAsync(_aiQuestion.Text ?? ""));
        Action(_aiStop, () => {
            _aiCancel?.Cancel();

            return Task.CompletedTask;
        });
        _aiStop.IsVisible = false;
        _aiQuestion.AddHandler(KeyDownEvent, (_, e) => {
            if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)) {
                e.Handled = true;
                if (_aiAsk.IsEnabled && _aiCancel is null) {
                    _ = Guard(() => AskAiAsync(_aiQuestion.Text ?? ""));
                }
            }
        }, RoutingStrategies.Tunnel);

        _aiChatScroll.Content = _aiChat;
        ShowAiWelcome();

        var chatCard = Fold("chat", AI_GUIDE, "Ask the guide",
            "It explains Ahziael and the server in plain words, and tells you where to click. It only reads and explains: it cannot change Ahziael, " +
            "your settings, the server or any file.",
            _aiStarters,
            Card(_aiChatScroll),
            new DockPanel {
                Children = {
                    WithDock(WithMargin(new StackPanel {
                        Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Bottom,
                        Children = { _aiStop, _aiAsk, Action(Plain("New chat"), NewAiChatAsync) },
                    }, new Thickness(10, 0, 0, 0)), Dock.Right),
                    _aiQuestion,
                },
            },
            Sub("The guide knows Ahziael's pages, chat commands, guides, your server's settings and the latest changes. It can still be wrong now and then, " +
                "so check before relying on it."));

        return new DockPanel {
            Children = {
                WithDock(ManagerTheme.PageHeader("chat", ManagerTheme.Accent(AI_GUIDE), AI_GUIDE,
                    "Ask anything about " + Edition.NAME + " and your server, in plain words. It explains; it never changes anything."), Dock.Top),
                Page(_aiKeyCard, chatCard),
            },
        };
    }

    private void ShowAiKeyState(string key) {
        var has = key.Length > 0;
        _aiKeyState.Text = has
            ? "A key is saved. You're ready to ask."
            : "No key saved yet. Follow the steps below to get one; it takes a few minutes.";
        _aiKeyState.Foreground = has ? ManagerTheme.Accent("good") : ManagerTheme.Accent("warn");
        // With a key saved the section folds shut, so the chat has the room.
        _aiKeyCard?.SetOpen(!has);
    }

    private Task SaveAiKeyAsync() {
        var key = (_aiKey.Text ?? "").Trim();
        if (!AiGuide.LooksLikeKey(key)) {
            throw new PanelException("That doesn't look like an Anthropic key. It starts with sk-ant- and is long; copy it again with the Copy button.");
        }

        var model = AiGuide.MODELS[Math.Max(0, _aiModel.SelectedIndex)].Id;
        AiGuide.Save(_manager.InstallFolder, new AiGuide.Settings(key, model));
        _aiKey.Text = key;
        ShowAiKeyState(key);
        Say("Key saved on this PC. Ask the guide anything.");

        return Task.CompletedTask;
    }

    private async Task RemoveAiKeyAsync() {
        if (!await ConfirmAsync("Remove the saved key from this PC? The key itself keeps working on Anthropic's site until you delete it there.")) {
            return;
        }

        AiGuide.Forget(_manager.InstallFolder);
        _aiKey.Text = "";
        ShowAiKeyState("");
        Say("Key removed from this PC.");
    }

    private Task NewAiChatAsync() {
        _aiCancel?.Cancel();
        _aiTurns.Clear();
        // The settings may have changed since; the next question reads them again.
        _aiReference = null;
        ShowAiWelcome();

        return Task.CompletedTask;
    }

    private void ShowAiWelcome() {
        _aiChat.Children.Clear();
        _aiChat.Children.Add(AiBubble(false,
            $"Hi! I'm the {Edition.NAME} guide. Ask me what any page or setting does, how to make customs or cheats, which chat command does what, " +
            "or what the server is doing behind the scenes. I can't change anything myself, but I'll tell you exactly where to click."));
        _aiStarters.IsVisible = true;
    }

    private static Border AiBubble(bool fromPlayer, string text) => new() {
        Background = fromPlayer ? ManagerTheme.Brush(ManagerTheme.AccentColor(AI_GUIDE), 46) : ManagerTheme.SURFACE_HIGH_BRUSH,
        CornerRadius = new CornerRadius(ManagerTheme.RADIUS_SMALL), Padding = new Thickness(14, 10),
        HorizontalAlignment = fromPlayer ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        Margin = fromPlayer ? new Thickness(80, 0, 0, 0) : new Thickness(0, 0, 80, 0),
        Child = new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
    };

    private async Task AskAiAsync(string question) {
        question = question.Trim();
        if (question.Length == 0 || _aiCancel is not null) {
            return;
        }

        var settings = AiGuide.Load(_manager.InstallFolder);
        if (settings.Key.Length == 0) {
            _aiKeyCard?.SetOpen(true);
            throw new PanelException("Save your Anthropic key first: see Your key at the top of this page.");
        }

        _aiStarters.IsVisible = false;
        _aiQuestion.Text = "";
        _aiTurns.Add(new AiGuideTurn(true, question));
        _aiChat.Children.Add(AiBubble(true, question));
        var bubble = AiBubble(false, "Thinking…");
        var answerText = (SelectableTextBlock) bubble.Child!;
        _aiChat.Children.Add(bubble);
        _aiChatScroll.ScrollToEnd();

        _aiCancel = new CancellationTokenSource();
        _aiAsk.IsEnabled = false;
        _aiStop.IsVisible = true;
        var answer = new StringBuilder();
        try {
            _aiReference ??= await Task.Run(BuildAiReference);
            await foreach (var piece in AiGuide.AskAsync(settings, _aiReference, _aiTurns, _aiCancel.Token)) {
                answer.Append(piece);
                answerText.Text = answer.ToString();
                _aiChatScroll.ScrollToEnd();
            }
        }
        catch (OperationCanceledException) {
            answer.Append(answer.Length == 0 ? "(Stopped.)" : "\n\n(Stopped.)");
            answerText.Text = answer.ToString();
        }
        catch (Exception ex) {
            // The question is taken back out, so asking again does not send it twice.
            _aiTurns.RemoveAt(_aiTurns.Count - 1);
            answerText.Text = ex is PanelException ? ex.Message : $"Something went wrong: {ex.Message}";
            answerText.Foreground = ManagerTheme.Accent("warn");

            return;
        }
        finally {
            _aiCancel.Dispose();
            _aiCancel = null;
            _aiAsk.IsEnabled = true;
            _aiStop.IsVisible = false;
        }

        if (answer.Length == 0) {
            _aiTurns.RemoveAt(_aiTurns.Count - 1);
            answerText.Text = "(No answer came back. Try asking again.)";

            return;
        }

        _aiTurns.Add(new AiGuideTurn(false, answer.ToString()));
    }

    // ---------------------------------------------------------------------
    // What the guide reads
    // ---------------------------------------------------------------------

    /// <summary>
    /// The guide's rules and its reference: how Ahziael and the server fit together, every page, every chat command, the
    /// guides, this server's settings with what each does, and the latest patch notes. All of it comes from the app, so it
    /// is never out of date with the version installed.
    /// </summary>
    private string BuildAiReference() {
        var text = new StringBuilder();
        text.AppendLine($"""
            You are the {Edition.NAME} Guide, built into {Edition.NAME}, a Windows app created by Niduus. {Edition.NAME} runs a personal
            Wizard101 server called Imlight on the player's own PC, and lets them play on it, change it and make their own content.

            Your job is to help the person understand what {Edition.NAME} can do, where to find things in it, and what the server does
            behind the scenes. Most people asking are not technical.

            Rules:
            - You can only read and explain. You have no way to press buttons, change settings, edit files, run commands or change
              {Edition.NAME} or the server, so never say or suggest that you did or will. When the person wants something changed,
              tell them exactly where to click, naming pages, tabs, sections and buttons exactly as the reference names them.
            - Do not write code, scripts, patches or file edits for changing {Edition.NAME}, the server or the game. If asked, explain
              kindly that you are a guide only, and point to the page in {Edition.NAME} that does it, if there is one.
            - Base your answers on the reference below. If it does not cover something, say you are not sure instead of guessing,
              and suggest where in the app they could look.
            - Never suggest changing Wizard101's own game files. {Edition.NAME} never changes them: everything happens on the server,
              so the normal, latest Wizard101 game keeps working.
            - Write plain text in short, friendly sentences. Use numbered steps for anything with more than one click. Do not use
              Markdown symbols such as #, ** or tables. Chat commands are typed in the game's chat box and start with a dot.
            - Keep answers short unless the person asks for detail.

            How it all fits together:
            - Wizard101 (the game) runs on this PC as normal. It is never modified.
            - The Imlight server also runs on this PC. It does all the game's thinking: logins, characters, battles, spells, quests,
              drops, shops, pets, houses, creatures and the extra features and cheats. Start and stop it on Home or the Server tab.
            - {Edition.NAME} is the window around it all: it starts the server, opens the game pointed at it, shows the server's
              settings with what each does, and has pages for looking things up, making customs and cheats, and reading logs.
            - Settings live in a file called Imlight.ini; {Edition.NAME} shows each one on the Settings tab. Saved settings are used
              by the running server straight away, with no restart.
            - Chat commands need a permission level on the account: set it on the Accounts tab (Quality assurance or higher for most).
            - Customs (custom mobs, bosses, areas, drop tables) are kept in the server's spiraldb-extra folder and never change the
              game's own creatures. The Guides tab has step-by-step guides for making them.
            - {Edition.NAME} updates itself from the update panel at the top of the window; What's New lists every version's changes.

            The reference follows.

            """);

        text.AppendLine("== Pages and tabs ==");
        text.AppendLine("Home: Start here. Play, start or stop the server, see who is online, and search everything.");
        text.AppendLine("What's New: Every version's changes.");
        text.AppendLine("AI Guide: This chat.");
        foreach (var group in s_groups) {
            text.AppendLine($"{group.Name} page: {group.About} Tabs:");
            foreach (var name in group.Tabs) {
                text.AppendLine($"- {name}: {s_tabs[name].Line}");
            }
        }

        text.AppendLine();
        text.AppendLine("== Chat commands (type in the game's chat) ==");
        foreach (var command in Lookup.COMMANDS) {
            text.AppendLine($"- {command.Name}: {command.Typed}. {command.Help} Who: {command.Detail}.");
        }

        text.AppendLine();
        text.AppendLine("== Guides (the Guides tab) ==");
        text.AppendLine(GuidesMarkdown());

        text.AppendLine();
        text.AppendLine("== This server's settings (Settings tab; section, setting = value, then what it does) ==");
        try {
            if (_manager.IsInstalled) {
                foreach (var (section, settings) in _manager.ReadSettings()) {
                    text.AppendLine($"[{section}]");
                    foreach (var setting in settings) {
                        text.AppendLine(setting.Help.Length > 0 ? $"{setting.Key} = {setting.Value}: {setting.Help}" : $"{setting.Key} = {setting.Value}");
                    }
                }
            }
            else {
                text.AppendLine("The server is not installed yet, so there are no settings to read.");
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) {
            text.AppendLine("The settings file could not be read right now.");
        }

        text.AppendLine();
        text.AppendLine("== Latest changes (newest first) ==");
        var notes = _notes.Count > 0 ? _notes : BuiltInNotes();
        foreach (var version in notes.OrderByDescending(n => n.Version).Take(40)) {
            foreach (var change in version.Changes) {
                text.AppendLine($"- {version.Version}: {change}");
            }
        }

        return text.ToString();
    }

}
