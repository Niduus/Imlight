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
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;

namespace Imlight.Setup;

/// <summary>The Central wiki card on the Tools tab: download pages of Wizard101 Central's wiki to this PC.</summary>
internal sealed partial class ManagerWindow {

    private readonly TextBox _wikiPages = new() {
        AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, MinHeight = 110, MaxHeight = 260,
        Watermark = "One per line, for example:\nCreature:Rattlebones\nhttps://wiki.wizard101central.com/wiki/Spell:Fire_Cat\nCategory:Death Spells\nCreature:Malistaire*",
    };
    private readonly CheckBox _wikiHtml = new() { Content = "Also save how each page looks (slower: one more request per page)" };
    private readonly CheckBox _wikiNewer = new() { Content = "Check pages I already have for newer versions" };
    private readonly TextBlock _wikiNow = Sub("");
    private readonly Button _wikiStart = Primary("Download");
    private readonly Button _wikiStop = Plain("Stop");
    private CancellationTokenSource? _wikiRun;

    private Control CentralWikiCard() {
        _wikiStop.IsEnabled = false;
        _wikiStop.Click += (_, _) => _wikiRun?.Cancel();
        Action(_wikiStart, DownloadWikiAsync);
        ShowWikiFolder();

        return Shut(Fold("download", "Guides", "Central wiki pages",
            "Downloads pages of Wizard101 Central's wiki to this PC, slowly and politely, for boss cheats, spells and creatures.",
            _wikiNow,
            WithHint(_wikiPages,
                "Type or paste what to download, one per line: a page name (Creature:Rattlebones) or its link from your browser. "
                + "Category:Name gets every page in that category. A star gets every page starting with what is before it: "
                + "Creature:Malistaire* is every Malistaire, Creature:* every creature, and * alone the whole wiki (that one takes hours)."),
            _wikiHtml,
            _wikiNewer,
            WithHint(Row(_wikiStart, _wikiStop, Action(Plain("Open folder"), OpenWikiFolderAsync)),
                "It asks Central for 50 pages at a time, waits at least 3 seconds between requests, follows Central's robots.txt "
                + "and slows down when Central is busy. Pages you already have are skipped. You can press Stop or close the app at "
                + "any time: press Download again (even with the box empty) and it carries on where it stopped. "
                + "Pages are saved as text in the Central wiki folder, one folder per kind of page.")));
    }

    private void ShowWikiFolder() {
        using var wiki = new CentralWiki(_manager.InstallFolder);
        _wikiNow.Text = (wiki.PagesSaved == 0 ? "No pages downloaded yet." : $"{wiki.PagesSaved:N0} pages downloaded so far.")
            + (wiki.HasLeft ? " Some are left from last time: press Download to carry on." : "");
    }

    private async Task DownloadWikiAsync() {
        var lines = (_wikiPages.Text ?? "").Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
        using var wiki = new CentralWiki(_manager.InstallFolder);
        if (lines.Count == 0 && !wiki.HasLeft) {
            Say("Type the pages to download in the box first, one per line.");

            return;
        }

        var options = new CentralWiki.Options(_wikiHtml.IsChecked == true, _wikiNewer.IsChecked == true);
        using var run = _wikiRun = new CancellationTokenSource();
        var progress = new Progress<CentralWiki.Status>(status => _wikiNow.Text = status.Line);
        _wikiStop.IsEnabled = true;
        try {
            await Task.Run(() => wiki.RunAsync(lines, options, progress, run.Token));
            _wikiPages.Text = "";
            Say("Central wiki pages downloaded. Open folder shows them.");
        }
        catch (OperationCanceledException) {
            _wikiNow.Text = $"Stopped. {wiki.PagesSaved:N0} pages downloaded so far" + (wiki.HasLeft ? ". Press Download to carry on." : ".");
        }
        catch (CentralWiki.RefusedException ex) {
            _wikiNow.Text = ex.Message;
            Say(ex.Message, isError: true);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException) {
            _wikiNow.Text = $"Stopped: {ex.Message} Press Download to try again; it carries on where it stopped.";
        }
        finally {
            _wikiRun = null;
            _wikiStop.IsEnabled = false;
        }
    }

    private Task OpenWikiFolderAsync() => Open(Directory.CreateDirectory(CentralWiki.Folder(_manager.InstallFolder)).FullName);
}
