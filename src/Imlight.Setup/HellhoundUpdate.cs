#if HELLHOUND
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Imlight.Setup;

/// <summary>
/// Hellhound keeps itself up to date (Niduus, 2026-11-22: "Hellhound should auto update..."). It looks in the private bots
/// repo (Niduus/ahziael-bots) for a newer hellhound-v&lt;major&gt;.&lt;minor&gt; release when it starts and every 30 minutes,
/// downloads its Hellhound.exe, checks it against GitHub's own SHA-256 of the file, puts it in place of the running exe
/// (the old one is renamed Hellhound.old.exe and removed next start) and starts it on the same address, so the open
/// window carries on by itself. The repo is private, so this needs a read-only GitHub key that Niduus pastes into
/// Hellhound once; it is kept only in %LOCALAPPDATA%\Hellhound\github-key.txt and sent only to api.github.com.
/// </summary>
internal static class HellhoundUpdate {

    /// <summary>What Hellhound was started with, passed on to the new copy.</summary>
    internal static string[] Args { get; set; } = [];

    /// <summary>Hellhound's own number, major.minor (Niduus: "0 is major, 2 is minor; updating bot logic doesn't qualify").</summary>
    internal static string CurrentVersion {
        get {
            var v = typeof(HellhoundUpdate).Assembly.GetName().Version;
            return v == null || (v.Major == 0 && v.Minor == 0) ? "dev" : "v" + v.Major + "." + v.Minor;
        }
    }


    private const string REPOSITORY = "Niduus/ahziael-bots";
    private static readonly TimeSpan s_every = TimeSpan.FromMinutes(30);

    private static readonly SemaphoreSlim s_one = new(1, 1);

    internal static string State { get; private set; } = "Not looked yet.";

    internal static string Latest { get; private set; } = "";

    internal static bool HasKey => Key() is not null;

    private static string KeyFile => Path.Combine(HellhoundStart.DataFolder, "github-key.txt");

    internal static void Start() => _ = Task.Run(async () => {
        await Task.Delay(TimeSpan.FromSeconds(20));
        while (true) {
            await CheckAsync();
            await Task.Delay(s_every);
        }
    });

    /// <summary>Saves the pasted key (after checking that it can read the bots repo) and looks for an update at once.</summary>
    internal static string SaveKey(string key) {
        key = (key ?? "").Trim();
        if (key.Length is < 20 or > 400 || key.Any(char.IsWhiteSpace)) {
            return "That doesn't look like a GitHub key. Copy the whole key (it starts with github_pat_) and paste it again.";
        }

        try {
            using var http = Client(key);
            using var answer = http.GetAsync($"https://api.github.com/repos/{REPOSITORY}").GetAwaiter().GetResult();
            if (!answer.IsSuccessStatusCode) {
                return $"GitHub said no to that key ({(int) answer.StatusCode}). Check it has access to the ahziael-bots repo with Contents: Read-only.";
            }
        }
        catch (Exception e) {
            return "Could not reach GitHub to check the key: " + e.Message;
        }

        Directory.CreateDirectory(HellhoundStart.DataFolder);
        File.WriteAllText(KeyFile, key);
        _ = Task.Run(CheckAsync);

        return "Key saved. Hellhound now keeps itself up to date, and the server gets bot updates with the same key.";
    }

    internal static async Task CheckAsync() {
        if (!await s_one.WaitAsync(0)) {
            return;
        }

        try {
            await CheckOnceAsync();
        }
        catch (Exception e) {
            State = "Could not look for a Hellhound update: " + e.Message;
            HellhoundStart.Log(State);
        }
        finally {
            s_one.Release();
        }
    }

    private static async Task CheckOnceAsync() {
        if (Key() is not { } key) {
            State = "Off: Hellhound needs a GitHub key to read the private bots repo (see below).";
            return;
        }

        var mine = Number(CurrentVersion);
        using var http = Client(key);
        using var list = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{REPOSITORY}/releases?per_page=30"));
        JsonElement? best = null;
        var bestNumber = (0, 0);
        foreach (var release in list.RootElement.EnumerateArray()) {
            var tag = release.GetProperty("tag_name").GetString() ?? "";
            if (!tag.StartsWith("hellhound-v", StringComparison.Ordinal) || release.GetProperty("draft").GetBoolean()) {
                continue;
            }

            var number = Number(tag["hellhound-v".Length..]);
            if (number.CompareTo(bestNumber) > 0) {
                bestNumber = number;
                best = release.Clone();
            }
        }

        Latest = best is null ? "" : $"v{bestNumber.Item1}.{bestNumber.Item2}";
        if (best is not { } newest || bestNumber.CompareTo(mine) <= 0) {
            State = $"Up to date (looked at {DateTime.Now:HH:mm}).";
            return;
        }

        if (mine == (0, 0)) {
            State = $"{Latest} is out; this is a test copy, so it does not replace itself.";
            return;
        }

        var asset = newest.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(a => a.GetProperty("name").GetString() == "Hellhound.exe");
        if (asset.ValueKind != JsonValueKind.Object) {
            State = $"{Latest} is out but has no Hellhound.exe yet.";
            return;
        }

        State = $"Downloading {Latest}...";
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.GetProperty("url").GetString());
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        using var answer = await http.SendAsync(request);
        answer.EnsureSuccessStatusCode();
        var bytes = await answer.Content.ReadAsByteArrayAsync();
        var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
        var sha = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (bytes.Length < 100_000 || bytes[0] != 'M' || bytes[1] != 'Z' || (digest.Length > 0 && !digest.Equals(sha, StringComparison.OrdinalIgnoreCase))) {
            State = $"{Latest} did not download cleanly; it will try again later.";
            return;
        }

        Swap(bytes);
    }

    // The running exe can be renamed but not overwritten: rename it, write the new one in its place, start it on the
    // same address and step aside.
    private static void Swap(byte[] bytes) {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Hellhound does not know where it is.");
        var old = Path.Combine(Path.GetDirectoryName(exe)!, "Hellhound.old.exe");
        var fresh = exe + ".new";
        File.WriteAllBytes(fresh, bytes);
        if (File.Exists(old)) {
            File.Delete(old);
        }

        File.Move(exe, old);
        File.Move(fresh, exe);
        HellhoundStart.Log($"Updated itself to {Latest}; starting it.");
        State = $"Updated to {Latest}. Restarting...";

        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var arg in Args.Where(a => a != "--after-update")) {
            start.ArgumentList.Add(arg);
        }

        start.ArgumentList.Add("--after-update");
        HellhoundStart.LetGo();
        Process.Start(start)?.Dispose();
        Environment.Exit(0);
    }

    /// <summary>Removes the exe an update left behind.</summary>
    internal static void Tidy() {
        try {
            if (Environment.ProcessPath is { } exe && Path.Combine(Path.GetDirectoryName(exe)!, "Hellhound.old.exe") is var old && File.Exists(old)) {
                File.Delete(old);
            }
        }
        catch (Exception) {
            // Still in use for a moment: next start removes it.
        }
    }

    private static (int, int) Number(string text) {
        var parts = text.TrimStart('v').Split('.');
        return parts.Length >= 2 && int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor) ? (major, minor) : (0, 0);
    }

    private static string? Key() {
        try {
            return File.Exists(KeyFile) && File.ReadAllText(KeyFile).Trim() is { Length: > 0 } key ? key : null;
        }
        catch (Exception) {
            return null;
        }
    }

    private static HttpClient Client(string key) {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Hellhound", "1"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);

        return http;
    }
}
#endif
