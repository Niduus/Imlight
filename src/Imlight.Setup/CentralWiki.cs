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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Imlight.Setup;

/// <summary>
/// Downloads pages of Wizard101 Central's wiki to the player's PC (Niduus, 2026-10-23), so boss cheats, spells and
/// creatures can be read from them without saving each page in a browser. Central blocks cloud machines, so it runs here.
/// </summary>
/// <remarks>
/// It is slow and polite on purpose: one request at a time, at least <see cref="MIN_DELAY"/> apart (longer if the site's
/// robots.txt asks), nothing robots.txt forbids, waiting when the site says it is busy, and never a page it already has
/// unless asked to look for a newer version. It reads the wiki's own API (api.php), which hands over the page source of 50
/// pages in one request, so a whole category costs a few requests instead of one per page. Everything goes in
/// <c>&lt;install&gt;\Central wiki\</c>: <c>Pages\&lt;namespace&gt;\&lt;title&gt;.txt</c> (the page source),
/// <c>Pages.csv</c> (what is there and which version) and <c>To do.txt</c> (what is left, so a stopped run carries on).
/// </remarks>
internal sealed class CentralWiki : IDisposable {

    internal const string SITE = "https://wiki.wizard101central.com";
    internal const string FOLDER_NAME = "Central wiki";
    internal static readonly TimeSpan MIN_DELAY = TimeSpan.FromSeconds(3);
    private const int BATCH = 50;
    private const int MAX_TRIES = 6;

    /// <summary>What the player ticked.</summary>
    internal sealed record Options(bool SaveHtml, bool CheckForNewer);

    /// <summary>How far a run has got, for the card's status line.</summary>
    internal sealed record Status(string Line, int Done, int Left);

    internal sealed class RefusedException(string message) : Exception(message);

    private readonly HttpClient _http;
    private readonly Uri _site;
    private readonly string _folder;
    private TimeSpan _delay = MIN_DELAY;
    private DateTime _lastRequest = DateTime.MinValue;
    private List<(string Rule, bool Allow)>? _robots;
    private Dictionary<string, int>? _namespaces;
    private int _saved;

    // Pages.csv: title -> (file under Pages, revision, the page it redirects to).
    private readonly Dictionary<string, (string File, long Revision, string? SameAs)> _index = new(StringComparer.Ordinal);

    internal CentralWiki(string installFolder, string site = SITE) {
        _site = new Uri(site.TrimEnd('/') + "/");
        _folder = Path.Combine(installFolder, FOLDER_NAME);
        _http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromMinutes(2) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ImlightManager", SelfUpdate.CurrentVersion.ToString()));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(personal wiki reader; one request at a time)"));
        LoadIndex();
    }

    public void Dispose() => _http.Dispose();

    internal static string Folder(string installFolder) => Path.Combine(installFolder, FOLDER_NAME);

    private string PagesFolder => Path.Combine(_folder, "Pages");
    private string IndexFile => Path.Combine(_folder, "Pages.csv");
    private string ToDoFile => Path.Combine(_folder, "To do.txt");
    private string ListFile => Path.Combine(_folder, "Still to look up.txt");

    internal int PagesSaved => _index.Values.Count(page => page.SameAs is null);

    /// <summary>The pages a stopped run had left.</summary>
    internal List<string> ToDo() => ReadLines(ToDoFile);

    /// <summary>The lines a stopped run hadn't finished looking up (a category, a star).</summary>
    internal List<string> ListLeft() => ReadLines(ListFile);

    /// <summary>Whether a stopped run left anything to carry on with.</summary>
    internal bool HasLeft => ToDo().Count > 0 || ListLeft().Count > 0;

    private static List<string> ReadLines(string file) {
        try {
            return File.Exists(file) ? File.ReadAllLines(file).Where(line => line.Length > 0).ToList() : [];
        }
        catch (IOException) {
            return [];
        }
    }

    // ---------------------------------------------------------------------
    // A run
    // ---------------------------------------------------------------------

    /// <summary>
    /// Finds every page the lines ask for, adds them to the to-do list, then downloads the to-do list. Each line is a page
    /// name or link, <c>Category:Name</c> (every page in it and its subcategories), <c>Creature:Mal*</c> (every page whose
    /// name starts so), <c>Creature:*</c> (every page of that kind) or <c>*</c> (the whole wiki).
    /// </summary>
    internal async Task RunAsync(IEnumerable<string> lines, Options options, IProgress<Status> say, CancellationToken stop) {
        Directory.CreateDirectory(PagesFolder);
        await ReadRobotsAsync(stop);
        if (!Allowed("/wiki/api.php?action=query")) {
            throw new RefusedException("Central's robots.txt asks programs not to use its wiki API, so nothing was downloaded.");
        }

        var toDo = ToDo();
        var seen = new HashSet<string>(toDo, StringComparer.Ordinal);
        void Add(string title) {
            if (seen.Add(title)) {
                toDo.Add(title);
            }
        }

        // What is still to be looked up is kept too, so a run stopped while listing a big category starts it again.
        var asked = ListLeft().Concat(lines).Where(line => line.Trim().Length > 0).Distinct().ToList();
        SaveLines(ListFile, asked);
        while (asked.Count > 0) {
            var raw = asked[0];
            var line = PageName(raw);
            stop.ThrowIfCancellationRequested();
            if (line == "*" || line.EndsWith('*')) {
                say.Report(new Status($"Listing {line}…", 0, toDo.Count));
                await ListPrefixAsync(line, Add, say, toDo, stop);
            }
            else if (line.StartsWith("Category:", StringComparison.OrdinalIgnoreCase) && !raw.TrimEnd().EndsWith('!')) {
                say.Report(new Status($"Listing {line}…", 0, toDo.Count));
                await ListCategoryAsync(line, Add, say, toDo, stop);
            }
            else if (line.TrimEnd('!').Length > 0) {
                Add(line.TrimEnd('!'));
            }

            SaveToDo(toDo);
            asked.RemoveAt(0);
            SaveLines(ListFile, asked);
        }

        if (!options.CheckForNewer) {
            toDo.RemoveAll(Have);
        }
        else {
            await DropUnchangedAsync(toDo, say, stop);
        }

        SaveToDo(toDo);
        _saved = 0;
        var missing = new List<string>();
        var tries = new Dictionary<string, int>();
        while (toDo.Count > 0) {
            stop.ThrowIfCancellationRequested();
            // A page too big to come with others is asked for alone.
            var batch = tries.ContainsKey(toDo[0]) ? [toDo[0]] : toDo.TakeWhile(title => !tries.ContainsKey(title)).Take(BATCH).ToList();
            say.Report(new Status($"Downloading {Plural(toDo.Count, "page")}, about {Eta(toDo.Count, options)} to go…", _saved, toDo.Count));
            var got = await DownloadAsync(batch, options, stop);
            foreach (var title in batch) {
                if (got.TryGetValue(title, out var saved)) {
                    toDo.Remove(title);
                    if (!saved) {
                        missing.Add(title);
                    }
                }
                else if ((tries[title] = tries.GetValueOrDefault(title) + 1) >= 3) {
                    toDo.Remove(title);
                    missing.Add(title);
                }
            }

            SaveIndex();
            SaveToDo(toDo);
        }

        SaveToDo(toDo);
        var done = _saved;
        var line2 = done == 0 && missing.Count == 0 ? "Nothing new to download: you have every page already."
            : $"Done: downloaded {Plural(done, "page")}." + (missing.Count == 0 ? ""
                : $" Central has no page called {string.Join(", ", missing.Take(5))}{(missing.Count > 5 ? $" and {missing.Count - 5} more" : "")}.");
        say.Report(new Status(line2, done, 0));
    }

    private string Eta(int pages, Options options) {
        var requests = (pages + BATCH - 1) / BATCH + (options.SaveHtml ? pages : 0);
        var time = TimeSpan.FromSeconds(requests * (_delay.TotalSeconds + 1));

        return time.TotalMinutes < 1 ? "a minute" : time.TotalHours < 1 ? $"{(int)Math.Ceiling(time.TotalMinutes)} minutes"
            : $"{time.TotalHours:0.#} hours";
    }

    /// <summary>
    /// Downloads up to 50 pages in one request. Returns, for each title it heard back about, whether it was saved (false:
    /// Central has no such page); a title it didn't hear about (too big to come along) is asked for again.
    /// </summary>
    private async Task<Dictionary<string, bool>> DownloadAsync(List<string> titles, Options options, CancellationToken stop) {
        using var json = await ApiAsync(new() {
            ["action"] = "query", ["prop"] = "revisions", ["rvprop"] = "ids|timestamp|content", ["rvslots"] = "main",
            ["redirects"] = "1", ["titles"] = string.Join("|", titles),
        }, stop);
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (!json.RootElement.TryGetProperty("query", out var query)) {
            return result;
        }

        // What each asked title became: written the wiki's way first, then where a redirect points.
        var final = titles.ToDictionary(title => title, title => title, StringComparer.Ordinal);
        foreach (var step in new[] { "normalized", "redirects" }) {
            if (query.TryGetProperty(step, out var moves)) {
                foreach (var move in moves.EnumerateArray()) {
                    var from = move.GetProperty("from").GetString()!;
                    var to = move.GetProperty("to").GetString()!;
                    foreach (var key in final.Keys.Where(key => final[key] == from).ToList()) {
                        final[key] = to;
                    }
                }
            }
        }

        var pages = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (query.TryGetProperty("pages", out var list)) {
            foreach (var page in list.ValueKind == JsonValueKind.Array ? list.EnumerateArray() : list.EnumerateObject().Select(p => p.Value)) {
                pages[page.GetProperty("title").GetString()!] = page;
            }
        }

        foreach (var title in titles) {
            if (!pages.TryGetValue(final[title], out var page)) {
                continue;
            }

            if (page.TryGetProperty("missing", out _) || page.TryGetProperty("invalid", out _)) {
                result[title] = false;
                continue;
            }

            if (!page.TryGetProperty("revisions", out var revisions) || revisions.GetArrayLength() == 0
                || PageText(revisions[0]) is not { } text) {
                continue;
            }

            var name = final[title];
            if (titles.IndexOf(title) > titles.FindIndex(other => final[other] == name) && _index.TryGetValue(name, out var same)) {
                // Asked for twice in one go (by a redirect and by its own name): saved once.
                if (name != title) {
                    _index[title] = (same.File, same.Revision, name);
                }

                result[title] = true;
                continue;
            }

            var revision = revisions[0].TryGetProperty("revid", out var id) ? id.GetInt64() : 0;
            var file = FileFor(name);
            var path = Path.Combine(PagesFolder, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, text, stop);
            var render = "wiki/index.php?action=render&title=" + Uri.EscapeDataString(name);
            if (options.SaveHtml && Allowed("/" + render)) {
                var html = await GetAsync(new Uri(_site, render), stop);
                await File.WriteAllTextAsync(Path.ChangeExtension(path, ".html"), html, stop);
            }

            _index[name] = (file, revision, null);
            if (name != title) {
                _index[title] = (file, revision, name);
            }

            result[title] = true;
            _saved++;
        }

        return result;
    }

    // The page source, where old and new versions of the wiki put it.
    private static string? PageText(JsonElement revision) {
        if (revision.TryGetProperty("slots", out var slots) && slots.TryGetProperty("main", out var main)) {
            revision = main;
        }

        return revision.TryGetProperty("*", out var text) || revision.TryGetProperty("content", out text) ? text.GetString() : null;
    }

    /// <summary>With "check for newer versions": asks for only the version numbers of pages it has, and keeps only changed ones.</summary>
    private async Task DropUnchangedAsync(List<string> toDo, IProgress<Status> say, CancellationToken stop) {
        var have = toDo.Where(Have).ToList();
        for (var i = 0; i < have.Count; i += BATCH) {
            stop.ThrowIfCancellationRequested();
            say.Report(new Status($"Checking {Plural(have.Count - i, "page")} you have for newer versions…", 0, toDo.Count));
            var batch = have.Skip(i).Take(BATCH).Select(title => _index[title].SameAs ?? title).Distinct().ToList();
            using var json = await ApiAsync(new() {
                ["action"] = "query", ["prop"] = "revisions", ["rvprop"] = "ids", ["titles"] = string.Join("|", batch),
            }, stop);
            if (!json.RootElement.TryGetProperty("query", out var query) || !query.TryGetProperty("pages", out var list)) {
                continue;
            }

            foreach (var page in list.ValueKind == JsonValueKind.Array ? list.EnumerateArray() : list.EnumerateObject().Select(p => p.Value)) {
                var title = page.GetProperty("title").GetString()!;
                if (page.TryGetProperty("revisions", out var revisions) && revisions.GetArrayLength() > 0
                    && _index.TryGetValue(title, out var known) && revisions[0].GetProperty("revid").GetInt64() == known.Revision) {
                    toDo.RemoveAll(asked => asked == title || _index.TryGetValue(asked, out var alias) && alias.SameAs == title);
                }
            }
        }
    }

    private bool Have(string title) => _index.TryGetValue(title, out var page) && File.Exists(Path.Combine(PagesFolder, page.File));

    // ---------------------------------------------------------------------
    // Finding pages
    // ---------------------------------------------------------------------

    private async Task ListCategoryAsync(string category, Action<string> add, IProgress<Status> say, List<string> toDo, CancellationToken stop) {
        var categories = new Queue<string>([category]);
        var visited = new HashSet<string>(StringComparer.Ordinal) { category };
        while (categories.TryDequeue(out var next)) {
            await ListAsync(new() { ["list"] = "categorymembers", ["cmtitle"] = next, ["cmprop"] = "title|ns", ["cmlimit"] = "500" },
                "categorymembers", page => {
                    var title = page.GetProperty("title").GetString()!;
                    switch (page.GetProperty("ns").GetInt32()) {
                        case 14:
                            if (visited.Add(title)) {
                                categories.Enqueue(title);
                            }

                            break;
                        // Pictures and their pages: not page text.
                        case 6: break;
                        default: add(title); break;
                    }
                }, () => say.Report(new Status($"Listing {next}: {Plural(toDo.Count, "page")} found so far…", 0, toDo.Count)), stop);
        }
    }

    private async Task ListPrefixAsync(string line, Action<string> add, IProgress<Status> say, List<string> toDo, CancellationToken stop) {
        var namespaces = await NamespacesAsync(stop);
        var prefix = line.TrimEnd('*');
        IEnumerable<int> spaces;
        var colon = prefix.IndexOf(':');
        if (line == "*") {
            // The whole wiki: every kind of page with readable text, without talk pages, user pages, pictures or the wiki's own messages.
            spaces = namespaces.Values.Distinct().Where(id => id >= 0 && id % 2 == 0 && id is not (2 or 6 or 8)).Order();
            prefix = "";
        }
        else if (colon > 0 && namespaces.TryGetValue(prefix[..colon].Trim().ToLowerInvariant(), out var id)) {
            spaces = [id];
            prefix = prefix[(colon + 1)..].TrimStart();
        }
        else {
            spaces = [0];
        }

        foreach (var space in spaces) {
            var args = new Dictionary<string, string> {
                ["list"] = "allpages", ["apnamespace"] = space.ToString(CultureInfo.InvariantCulture), ["aplimit"] = "500",
                ["apfilterredir"] = "nonredirects",
            };
            if (prefix.Length > 0) {
                args["apprefix"] = prefix;
            }

            await ListAsync(args, "allpages", page => add(page.GetProperty("title").GetString()!),
                () => say.Report(new Status($"Listing {line}: {Plural(toDo.Count, "page")} found so far…", 0, toDo.Count)), stop);
        }
    }

    /// <summary>A list the wiki hands over 500 at a time, following its "continue" (new versions) or "query-continue" (old ones).</summary>
    private async Task ListAsync(Dictionary<string, string> args, string name, Action<JsonElement> each, Action page, CancellationToken stop) {
        args["action"] = "query";
        args["continue"] = "";
        while (true) {
            using var json = await ApiAsync(new(args), stop);
            if (json.RootElement.TryGetProperty("query", out var query) && query.TryGetProperty(name, out var items)) {
                foreach (var item in items.EnumerateArray()) {
                    each(item);
                }
            }

            page();
            JsonElement more;
            if (json.RootElement.TryGetProperty("continue", out more)) {
                // The new way: these go back as they are.
            }
            else if (json.RootElement.TryGetProperty("query-continue", out var old) && old.TryGetProperty(name, out more)) {
                args.Remove("continue");
            }
            else {
                return;
            }

            foreach (var value in more.EnumerateObject()) {
                args[value.Name] = value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString()! : value.Value.GetRawText();
            }
        }
    }

    /// <summary>Every namespace name ("creature", "spell", "category", their other names), lower case, to its number.</summary>
    private async Task<Dictionary<string, int>> NamespacesAsync(CancellationToken stop) {
        if (_namespaces is not null) {
            return _namespaces;
        }

        using var json = await ApiAsync(new() { ["action"] = "query", ["meta"] = "siteinfo", ["siprop"] = "namespaces|namespacealiases" }, stop);
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        var query = json.RootElement.GetProperty("query");
        foreach (var space in query.GetProperty("namespaces").EnumerateObject().Select(p => p.Value)) {
            var id = space.GetProperty("id").GetInt32();
            foreach (var key in new[] { "*", "name", "canonical" }) {
                if (space.TryGetProperty(key, out var text) && text.GetString() is { Length: > 0 } spelled) {
                    names[spelled.ToLowerInvariant()] = id;
                }
            }
        }

        if (query.TryGetProperty("namespacealiases", out var aliases)) {
            foreach (var alias in aliases.EnumerateArray()) {
                if (alias.TryGetProperty("*", out var text) || alias.TryGetProperty("alias", out text)) {
                    names[text.GetString()!.ToLowerInvariant()] = alias.GetProperty("id").GetInt32();
                }
            }
        }

        return _namespaces = names;
    }

    /// <summary>
    /// The page name in a line: a link to the wiki (<c>…/wiki/Creature:Rattlebones</c> or <c>…index.php?title=…</c>) or a
    /// name, with underscores as spaces, the way the wiki writes titles.
    /// </summary>
    internal static string PageName(string line) {
        var text = line.Trim().Trim('"', '<', '>');
        if (Uri.TryCreate(text, UriKind.Absolute, out var link) && link.Scheme.StartsWith("http", StringComparison.Ordinal)) {
            var title = System.Web.HttpUtility.ParseQueryString(link.Query)["title"];
            if (title is null) {
                var path = link.AbsolutePath;
                var at = path.IndexOf("/wiki/", StringComparison.OrdinalIgnoreCase);
                title = Uri.UnescapeDataString(at >= 0 ? path[(at + 6)..] : path.TrimStart('/'));
            }

            text = title;
        }

        text = text.Replace('_', ' ').Trim();
        if (text.Length > 0 && char.IsLower(text[0])) {
            text = char.ToUpperInvariant(text[0]) + text[1..];
        }

        return text;
    }

    /// <summary>Where a page is kept: a folder for its kind ("Creature", "Spell", "Main"), and its name made safe for Windows.</summary>
    internal static string FileFor(string title) {
        var colon = title.IndexOf(':');
        var kind = colon > 0 && !title[..colon].Contains(' ') ? title[..colon] : "Main";
        var name = colon > 0 && kind != "Main" ? title[(colon + 1)..] : title;
        var safe = new StringBuilder();
        foreach (var c in name) {
            safe.Append(c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsControl(c) ? '_' : c);
        }

        var file = safe.ToString().Trim().TrimEnd('.');
        if (file.Length > 150) {
            file = file[..150];
        }

        if (file.Length == 0 || file.Split('.')[0].ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL" or "COM1" or "LPT1") {
            file = "_" + file;
        }

        return Path.Combine(kind, file + ".txt");
    }

    private static string Plural(int count, string word) => count == 1 ? $"1 {word}" : $"{count:N0} {word}s";

    // ---------------------------------------------------------------------
    // Asking politely
    // ---------------------------------------------------------------------

    private async Task<JsonDocument> ApiAsync(Dictionary<string, string> args, CancellationToken stop) {
        args["format"] = "json";
        // Asks the wiki to turn the request away while its database is behind, rather than add to its load.
        args["maxlag"] = "5";
        var uri = new Uri(_site, "wiki/api.php");
        for (var attempt = 1; ; attempt++) {
            var text = await GetAsync(uri, stop, args);
            JsonDocument json;
            try {
                json = JsonDocument.Parse(text);
            }
            catch (JsonException) {
                throw new RefusedException(text.Contains("challenge", StringComparison.OrdinalIgnoreCase) || text.Contains("Just a moment", StringComparison.Ordinal)
                    ? "Central only lets web browsers in right now, so the app can't download from it. Save the pages in your browser instead (Ctrl+S)."
                    : "Central's wiki didn't answer the way a wiki does, so nothing more was downloaded. Try again later.");
            }

            if (json.RootElement.TryGetProperty("error", out var error)) {
                var code = error.TryGetProperty("code", out var c) ? c.GetString() : "";
                json.Dispose();
                if (code == "maxlag" && attempt < MAX_TRIES) {
                    await Task.Delay(TimeSpan.FromSeconds(10 * attempt), stop);
                    continue;
                }

                throw new RefusedException($"Central's wiki said no ({code}). Try again later.");
            }

            return json;
        }
    }

    /// <summary>
    /// One request, after waiting its turn. When the site is busy (429, 503) it waits as long as the site asks, or longer
    /// each time, and tries again. Long requests (50 page names) are sent as a form, the way the wiki accepts them.
    /// </summary>
    private async Task<string> GetAsync(Uri uri, CancellationToken stop, Dictionary<string, string>? args = null) {
        var query = args is null ? "" : string.Join("&", args.Select(a => Uri.EscapeDataString(a.Key) + "=" + Uri.EscapeDataString(a.Value)));
        for (var attempt = 1; ; attempt++) {
            var wait = _lastRequest + _delay - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) {
                await Task.Delay(wait, stop);
            }

            HttpResponseMessage response;
            try {
                using var request = args is null ? new HttpRequestMessage(HttpMethod.Get, uri)
                    : query.Length < 1800 ? new HttpRequestMessage(HttpMethod.Get, new Uri(uri + "?" + query))
                    : new HttpRequestMessage(HttpMethod.Post, uri) { Content = new FormUrlEncodedContent(args) };
                response = await _http.SendAsync(request, stop);
            }
            catch (HttpRequestException) when (attempt < MAX_TRIES) {
                _lastRequest = DateTime.UtcNow;
                await Task.Delay(TimeSpan.FromSeconds(15 * attempt), stop);
                continue;
            }
            catch (TaskCanceledException) when (!stop.IsCancellationRequested && attempt < MAX_TRIES) {
                _lastRequest = DateTime.UtcNow;
                continue;
            }
            finally {
                _lastRequest = DateTime.UtcNow;
            }

            using (response) {
                var code = (int)response.StatusCode;
                if (code is 429 or 502 or 503 or 504 && attempt < MAX_TRIES) {
                    var asked = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow);
                    var pause = asked is { } a && a > TimeSpan.Zero ? a : TimeSpan.FromSeconds(30 * attempt);
                    // A site that asked for a break once gets more room between requests from then on.
                    _delay = TimeSpan.FromSeconds(Math.Min(_delay.TotalSeconds * 2, 30));
                    await Task.Delay(pause > TimeSpan.FromMinutes(10) ? TimeSpan.FromMinutes(10) : pause, stop);
                    continue;
                }

                if (code is 401 or 403) {
                    throw new RefusedException("Central turned the app away (it may only let web browsers in). Save the pages in your browser instead (Ctrl+S).");
                }

                if (code == 404 && args is not null) {
                    throw new RefusedException("Central's wiki API wasn't found, so the app can't download from it.");
                }

                if (!response.IsSuccessStatusCode) {
                    throw new RefusedException($"Central answered {code} {response.ReasonPhrase}. Try again later.");
                }

                return await response.Content.ReadAsStringAsync(stop);
            }
        }
    }

    /// <summary>
    /// Reads the site's robots.txt: the rules for this app by name, else the ones for every program, and the delay
    /// it asks for between requests (never less than <see cref="MIN_DELAY"/>).
    /// </summary>
    private async Task ReadRobotsAsync(CancellationToken stop) {
        if (_robots is not null) {
            return;
        }

        string text;
        try {
            text = await GetAsync(new Uri(_site, "robots.txt"), stop);
        }
        catch (RefusedException ex) when (!ex.Message.Contains("turned the app away", StringComparison.Ordinal)) {
            // No robots.txt: nothing is off limits.
            text = "";
        }

        var groups = new List<(List<string> Agents, List<(string, bool)> Rules, double? Delay)>();
        (List<string> Agents, List<(string, bool)> Rules, double? Delay)? current = null;
        var lastWasAgent = false;
        foreach (var raw in text.Split('\n')) {
            var line = raw.Split('#')[0].Trim();
            var colon = line.IndexOf(':');
            if (colon < 0) {
                continue;
            }

            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            if (key == "user-agent") {
                if (!lastWasAgent || current is null) {
                    current = ([], [], null);
                    groups.Add(current.Value);
                }

                current.Value.Agents.Add(value.ToLowerInvariant());
                lastWasAgent = true;
                continue;
            }

            lastWasAgent = false;
            if (current is null) {
                continue;
            }

            switch (key) {
                case "disallow" when value.Length > 0: current.Value.Rules.Add((value, false)); break;
                case "allow" when value.Length > 0: current.Value.Rules.Add((value, true)); break;
                case "crawl-delay" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds):
                    groups[^1] = current.Value with { Delay = seconds };
                    current = groups[^1];
                    break;
            }
        }

        var mine = groups.FirstOrDefault(g => g.Agents.Any(agent => agent.Length > 1 && "imlightmanager".Contains(agent, StringComparison.Ordinal)));
        if (mine.Agents is null) {
            mine = groups.FirstOrDefault(g => g.Agents.Contains("*"));
        }

        _robots = mine.Rules ?? [];
        if (mine.Delay is { } delay && delay > _delay.TotalSeconds) {
            _delay = TimeSpan.FromSeconds(Math.Min(delay, 120));
        }
    }

    /// <summary>Whether robots.txt lets programs read this path: the longest matching rule wins, as search engines read it.</summary>
    internal bool Allowed(string path) {
        var best = (Length: -1, Allow: true);
        foreach (var (rule, allow) in _robots ?? []) {
            if (Matches(rule, path) && (rule.Length > best.Length || rule.Length == best.Length && allow)) {
                best = (rule.Length, allow);
            }
        }

        return best.Allow;
    }

    private static bool Matches(string rule, string path) {
        var anchored = rule.EndsWith('$');
        var parts = (anchored ? rule[..^1] : rule).Split('*');
        if (!path.StartsWith(parts[0], StringComparison.Ordinal)) {
            return false;
        }

        var at = parts[0].Length;
        for (var i = 1; i < parts.Length; i++) {
            var found = path.IndexOf(parts[i], at, StringComparison.Ordinal);
            if (found < 0) {
                return false;
            }

            at = found + parts[i].Length;
        }

        return !anchored || at == path.Length || parts.Length > 1 && path.EndsWith(parts[^1], StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    // Pages.csv and To do.txt
    // ---------------------------------------------------------------------

    private void LoadIndex() {
        try {
            if (!File.Exists(IndexFile)) {
                return;
            }

            foreach (var line in File.ReadAllLines(IndexFile).Skip(1)) {
                var cells = Csv(line);
                if (cells.Count >= 3) {
                    _index[cells[0]] = (cells[1], long.TryParse(cells[2], out var revision) ? revision : 0,
                        cells.Count > 3 && cells[3].Length > 0 ? cells[3] : null);
                }
            }
        }
        catch (IOException) {
            // Being written by another run; it starts from what is on disk next time.
        }
    }

    private void SaveIndex() {
        static string Cell(string? text) => text is null ? "" : text.IndexOfAny([',', '"', '\n']) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
        var lines = new List<string> { "Page,Saved as,Version,Same page as" };
        lines.AddRange(_index.OrderBy(page => page.Key, StringComparer.Ordinal)
            .Select(page => $"{Cell(page.Key)},{Cell(page.Value.File)},{page.Value.Revision},{Cell(page.Value.SameAs)}"));
        var temp = IndexFile + ".new";
        File.WriteAllLines(temp, lines);
        File.Move(temp, IndexFile, overwrite: true);
    }

    private static List<string> Csv(string line) {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++) {
            var c = line[i];
            if (quoted) {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"') {
                    quoted = false;
                }
                else {
                    cell.Append(c);
                }
            }
            else if (c == '"') {
                quoted = true;
            }
            else if (c == ',') {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else {
                cell.Append(c);
            }
        }

        cells.Add(cell.ToString());

        return cells;
    }

    private void SaveToDo(List<string> toDo) => SaveLines(ToDoFile, toDo);

    private void SaveLines(string file, List<string> lines) {
        if (lines.Count == 0) {
            if (File.Exists(file)) {
                File.Delete(file);
            }

            return;
        }

        Directory.CreateDirectory(_folder);
        var temp = file + ".new";
        File.WriteAllLines(temp, lines);
        File.Move(temp, file, overwrite: true);
    }
}
