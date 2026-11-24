#if !HELLHOUND
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Imlight.Setup;

/// <summary>
/// Adds Ahziael to Claude desktop's list of connectors (its claude_desktop_config.json gets an "ahziael" entry that starts
/// "Imlight.exe --mcp --dir &lt;install folder&gt;"), keeping everything else in that file and a copy of it as it was. Only
/// when asked, from Server > Ahziael > Claude. No key or password goes in it.
/// </summary>
internal static class ClaudeSetup {

    /// <summary>The command Claude starts: this Ahziael, with --mcp and its install folder.</summary>
    internal static (string Command, string[] Args) Command(string installFolder) {
        var exe = Environment.ProcessPath ?? InstalledCommands.INSTALLED_EXE;
        // Run from source ("dotnet ImlightManager.dll"): Claude starts dotnet with the dll.
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) {
            return (exe, [Path.Combine(AppContext.BaseDirectory, typeof(ClaudeSetup).Assembly.GetName().Name + ".dll"), "--mcp", "--dir", installFolder]);
        }

        return (exe, ["--mcp", "--dir", installFolder]);
    }

    /// <summary>Where Claude desktop keeps its settings on this PC (the usual place, and the Microsoft Store one when it exists).</summary>
    internal static List<string> ConfigFiles() {
        var files = new List<string>();
        if (OperatingSystem.IsWindows()) {
            files.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json"));
            var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
            try {
                foreach (var pkg in Directory.Exists(packages) ? Directory.EnumerateDirectories(packages, "Claude_*") : []) {
                    files.Add(Path.Combine(pkg, "LocalCache", "Roaming", "Claude", "claude_desktop_config.json"));
                }
            } catch (Exception) {
                // Can't look there.
            }
        } else if (OperatingSystem.IsMacOS()) {
            files.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Claude", "claude_desktop_config.json"));
        } else {
            files.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "Claude", "claude_desktop_config.json"));
        }

        return files;
    }

    internal static string AddToClaudeDesktop(string installFolder) {
        var (command, args) = Command(installFolder);
        var done = new List<string>();
        foreach (var file in ConfigFiles()) {
            var folder = Path.GetDirectoryName(file)!;
            // Only where Claude desktop is (its folder exists), apart from the usual place.
            if (!Directory.Exists(folder) && file != ConfigFiles()[0]) {
                continue;
            }

            JsonObject root;
            if (File.Exists(file)) {
                var text = File.ReadAllText(file);
                try {
                    root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject ?? new JsonObject();
                } catch (JsonException) {
                    return $"Claude desktop's settings file couldn't be read, so it was left alone: {file}";
                }

                File.Copy(file, file + ".before-ahziael", overwrite: true);
            } else {
                Directory.CreateDirectory(folder);
                root = new JsonObject();
            }

            if (root["mcpServers"] is not JsonObject servers) {
                servers = new JsonObject();
                root["mcpServers"] = servers;
            }

            servers[ClaudeTools.SERVER_NAME] = new JsonObject {
                ["command"] = command,
                ["args"] = new JsonArray(args.Select(a => (JsonNode) a).ToArray()),
            };
            File.WriteAllText(file, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            done.Add(file);
        }

        return done.Count == 0
            ? "Claude desktop wasn't found on this PC."
            : "Done. Quit Claude desktop fully (also from the tray by the clock) and open it again: Ahziael shows in its connectors (the + button > Connectors).";
    }

    internal static string ClaudeCodeLine(int port) => $"claude mcp add --transport http {ClaudeTools.SERVER_NAME} http://127.0.0.1:{port}/mcp";
}
#endif
