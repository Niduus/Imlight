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
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Imlight.Setup;

/// <summary>Where the server the app runs comes from (the Server source card on the Tools tab).</summary>
internal enum ServerSourceKind {
    /// <summary>The edition's own repository (Niduus/Imlight for the Manager), the way it always was.</summary>
    Default,
    /// <summary>Another GitHub copy of Imlight: an owner/repo and a branch.</summary>
    GitHub,
    /// <summary>A folder on this PC: an Imlight source checkout to build, or a built server to run where it is.</summary>
    Folder,
}

/// <summary>
/// The server source, kept in server-source.txt in the install folder next to the app's other marker files. No file
/// means the default, so an install that never changed it works exactly as before. Setup builds from it (SetupOptions)
/// and every path to the server (InstalledCommands.ServerFolder) follows it.
/// </summary>
internal sealed partial class ServerSource {

    internal const string FILE_NAME = "server-source.txt";

    /// <summary>Written next to a built server: which repository, branch and commit (or folder) it was built from.</summary>
    internal const string STAMP_FILE = "server-source-commit.txt";

    internal const string DEFAULT_BRANCH = "quality-assurance";

    private const string SECTION = "Server Source";

    internal ServerSourceKind Kind { get; init; }

    internal string Repository { get; init; } = Edition.DEFAULT_REPOSITORY;

    internal string Branch { get; init; } = DEFAULT_BRANCH;

    internal string? Folder { get; init; }

    /// <summary>What the default is called in the app.</summary>
    internal static string DefaultName => Edition.IS_MANAGER ? "Niduus's server" : "Original Imlight";

    // Read on every path to the server (the status check runs every 2 seconds), so kept until the file changes.
    private sealed record Cached(string Path, DateTime Written, ServerSource Source);

    private static Cached? s_cache;

    internal static ServerSource Load(string installFolder) {
        var path = Path.Combine(installFolder, FILE_NAME);
        try {
            var written = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (s_cache is { } cached && cached.Path == path && cached.Written == written) {
                return cached.Source;
            }

            var values = IniFile.ReadAll(path);
            string? Value(string key) => values.TryGetValue((SECTION, key), out var value) && value.Trim().Length > 0 ? value.Trim() : null;
            var kind = Enum.TryParse<ServerSourceKind>(Value("Kind"), ignoreCase: true, out var parsed) ? parsed : ServerSourceKind.Default;
            var source = kind switch {
                ServerSourceKind.GitHub when Value("Repository") is { } repo => new ServerSource {
                    Kind = kind, Repository = repo, Branch = Value("Branch") ?? DEFAULT_BRANCH,
                },
                ServerSourceKind.Folder when Value("Folder") is { } folder => new ServerSource { Kind = kind, Folder = folder },
                _ => new ServerSource(),
            };
            s_cache = new Cached(path, written, source);

            return source;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return new ServerSource();
        }
    }

    /// <summary>Saves the choice. The default removes the file, as the other marker files do.</summary>
    internal void Save(string installFolder) {
        var path = Path.Combine(installFolder, FILE_NAME);
        if (Kind == ServerSourceKind.Default) {
            File.Delete(path);

            return;
        }

        File.WriteAllLines(path, [
            "; Where Imlight gets its server. The Server source card on the Tools tab in the app changes it;",
            "; deleting this file goes back to " + DefaultName + ".",
            $"[{SECTION}]",
            $"Kind = {Kind}",
            Kind == ServerSourceKind.GitHub ? $"Repository = {Repository}" : $"Folder = {Folder}",
            Kind == ServerSourceKind.GitHub ? $"Branch = {Branch}" : "",
        ]);
    }

    // ---------------------------------------------------------------------
    // Folders
    // ---------------------------------------------------------------------

    private static string ServerExeName => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Imlight.Director.exe" : "Imlight.Director";

    /// <summary>An Imlight source checkout: it has the server's project, which setup builds.</summary>
    internal static bool IsCheckout(string folder)
        => File.Exists(Path.Combine(folder, "src", "Imlight.Director", "Imlight.Director.csproj"));

    /// <summary>A server that is already built: its program and its Config\Imlight.ini.</summary>
    internal static bool IsBuiltServer(string folder)
        => File.Exists(Path.Combine(folder, ServerExeName)) && File.Exists(Path.Combine(folder, "Config", "Imlight.ini"));

    /// <summary>The source checkout to build, when the folder choice is one.</summary>
    internal string? Checkout => Kind == ServerSourceKind.Folder && Folder is { } folder && IsCheckout(folder) ? Path.GetFullPath(folder) : null;

    /// <summary>The built server to run where it is, when the folder choice is one (a checkout is built instead).</summary>
    internal string? BuiltFolder => Kind == ServerSourceKind.Folder && Folder is { } folder && !IsCheckout(folder) && IsBuiltServer(folder)
        ? Path.GetFullPath(folder)
        : null;

    /// <summary>Where the server lives: the built folder itself, otherwise the install's own Server folder.</summary>
    internal string ServerFolder(string installFolder) => BuiltFolder ?? Path.Combine(installFolder, "Server");

    /// <summary>What a folder is, in a few words for the app, or null when it is neither kind.</summary>
    internal static string? DescribeFolder(string folder)
        => IsCheckout(folder) ? "An Imlight source folder. Installing builds it into the app's own Server folder; your folder is not changed."
            : IsBuiltServer(folder) ? "A built server. The app runs it right where it is, with its own settings and logs."
            : null;

    /// <summary>The choice in a few words, such as "Revive101/Imlight (quality-assurance)".</summary>
    internal string Describe() => Kind switch {
        ServerSourceKind.GitHub => $"{Repository} ({Branch})",
        ServerSourceKind.Folder => $"the folder {Folder}",
        _ => DefaultName,
    };

    // ---------------------------------------------------------------------
    // GitHub
    // ---------------------------------------------------------------------

    [GeneratedRegex(@"^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$")]
    private static partial Regex RepositoryPattern();

    /// <summary>
    /// The owner/repo in what the person typed, which may be the whole github.com address. Null when it is not one.
    /// </summary>
    internal static string? ParseRepository(string text) {
        var repo = text.Trim();
        var at = repo.IndexOf("github.com", StringComparison.OrdinalIgnoreCase);
        if (at >= 0) {
            repo = repo[(at + "github.com".Length)..].Trim('/', ':');
        }

        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) {
            repo = repo[..^4];
        }

        // github.com/owner/repo/tree/branch: only the first two parts.
        var parts = repo.Split('/', StringSplitOptions.RemoveEmptyEntries);
        repo = parts.Length >= 2 ? $"{parts[0]}/{parts[1]}" : repo;

        return RepositoryPattern().IsMatch(repo) ? repo : null;
    }

    /// <summary>The newest commit on a branch, or null when GitHub cannot be reached or the branch is not there.</summary>
    internal static async Task<string?> LatestCommitAsync(string repo, string branch) {
        try {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ImlightSetup", SelfUpdate.CurrentVersion.ToString()));
            // This media type answers with the commit's hash alone.
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.sha"));
            var sha = (await http.GetStringAsync($"https://api.github.com/repos/{repo}/commits/{Uri.EscapeDataString(branch)}")).Trim();

            return sha.Length == 40 ? sha : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException) {
            return null;
        }
    }

    /// <summary>The stamp a build from a GitHub copy gets: repository, branch and commit, on one line.</summary>
    internal static string Stamp(string repo, string branch, string commit) => $"{repo} {branch} {commit}";

    /// <summary>
    /// For a GitHub copy: the newest commit when it is not the one the installed server was built from, else null
    /// (also when GitHub cannot be reached).
    /// </summary>
    internal async Task<string?> NewerCommitAsync(string installFolder) {
        if (Kind != ServerSourceKind.GitHub || await LatestCommitAsync(Repository, Branch) is not { } latest) {
            return null;
        }

        string? built;
        try {
            var file = Path.Combine(ServerFolder(installFolder), STAMP_FILE);
            built = File.Exists(file) ? File.ReadAllText(file).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            built = null;
        }

        return string.Equals(built, Stamp(Repository, Branch, latest), StringComparison.OrdinalIgnoreCase) ? null : latest;
    }

}
