#if !HELLHOUND
using System;
using System.IO;
using System.Text.Json;

namespace Imlight.Setup;

/// <summary>
/// The Claude connector's three choices (Server > Ahziael > Claude): on or off, whether Claude may change things, and the port
/// on this PC. Kept in claude-connector.json in the install folder, so "Imlight.exe --mcp" (started by Claude desktop)
/// reads the same port. Off until Niduus turns it on. No key or password is ever kept here.
/// </summary>
internal sealed class ClaudeConnectorSettings {

    internal const string FILE = "claude-connector.json";
    internal const int DEFAULT_PORT = 47102;

    /// <summary>Let Claude use Ahziael (its connector listens on 127.0.0.1 only).</summary>
    public bool On { get; set; }

    /// <summary>Let Claude change things too (run commands, announce, change a setting), not only look.</summary>
    public bool Changes { get; set; }

    public int Port { get; set; } = DEFAULT_PORT;

    internal int UsablePort => Port is > 1024 and < 65536 ? Port : DEFAULT_PORT;

    internal static ClaudeConnectorSettings Load(string installFolder) {
        try {
            var path = Path.Combine(installFolder, FILE);
            return File.Exists(path) ? JsonSerializer.Deserialize<ClaudeConnectorSettings>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            return new();
        }
    }

    internal void Save(string installFolder) {
        try {
            File.WriteAllText(Path.Combine(installFolder, FILE), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            throw new PanelException($"That choice could not be saved ({ex.Message}). It goes back to how it was the next time Ahziael opens.");
        }
    }

}
#endif
