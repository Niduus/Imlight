#if !HELLHOUND
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Imlight.Setup;

/// <summary>
/// The tools Ahziael offers Claude through its connector, in one list both Ahziael and the "Imlight.exe --mcp" bridge hand
/// out. Tools that look are there while the connector is on; tools that change something (run a command, announce, change
/// a setting) only work while Server > Ahziael > Claude > "Let Claude make changes too" is on. Nothing here reads the game's
/// memory or touches its files: Claude sees what Ahziael already sees (the server, its logs, the game's own log, the game
/// windows on this PC and what the overlay is told).
/// </summary>
internal static class ClaudeTools {

    internal const string SERVER_NAME = "ahziael";

    internal sealed record Tool(string Name, string Title, string Description, JsonObject Input, bool Changes = false);

    private static JsonObject Props(params (string Name, string Type, string About)[] props) {
        var p = new JsonObject();
        foreach (var (name, type, about) in props) {
            p[name] = new JsonObject { ["type"] = type, ["description"] = about };
        }

        return new JsonObject { ["type"] = "object", ["properties"] = p };
    }

    private static JsonObject Needs(this JsonObject schema, params string[] names) {
        schema["required"] = new JsonArray(names.Select(n => (JsonNode) n).ToArray());
        return schema;
    }

    private const string WINDOW = "Which game window, by its number from list_game_windows (default: the first one).";

    internal static readonly IReadOnlyList<Tool> All = [
        new("get_status", "What Ahziael sees now",
            "Ahziael's version, whether the server is on and ready (or which other PC's server Ahziael manages), Randomizer and Die Delete, how many players are online, the Wizard101 windows open on this PC, the overlay's link to the server, and whether Claude may make changes.",
            Props()),
        new("list_players", "Who is online",
            "Everyone playing on the server now: account, wizard, level, school, zone (with its place name), whether in a battle, and since when.",
            Props()),
        new("get_positions", "Where each wizard stands",
            "Each wizard's zone, X, Y, Z and facing (radians and degrees), the time of their last move, whether their game runs on this PC, and their wizard id, as the server last wrote them (positions.txt).",
            Props()),
        new("list_game_windows", "The Wizard101 windows on this PC",
            "Each open Wizard101 game window: its number (use it with the other game tools), title, the size of its inside and where that is on the screen, its display scale, whether it is minimized, and the server it is connected to.",
            Props()),
        new("take_game_picture", "Take a picture of a game window",
            "A picture of the inside of one Wizard101 window as it is right now (no window frame). By default the game alone, without Ahziael's overlay; with_overlay shows what the player sees, overlay included (the window must not be covered by another).",
            Props(("window", "integer", WINDOW),
                ("with_overlay", "boolean", "Include Ahziael's overlay (copies the screen there). Default false."),
                ("max_width", "integer", "Widest the picture may be, in pixels (default 1280)."))),
        new("take_ahziael_picture", "Take a picture of Ahziael",
            "A picture of Ahziael's own window as it is on screen now (the page that is open, its cards and messages).",
            Props(("max_width", "integer", "Widest the picture may be, in pixels (default 1280)."))),
        new("get_minimap", "What the overlay minimap knows",
            "For one game window: the place, the wizard's spot and facing, and the people, creatures, doors and other marks the server sent the overlay's minimap (name, kind, level, job, X, Y, height). Only while that window's overlay shows a minimap.",
            Props(("window", "integer", WINDOW), ("limit", "integer", "Most marks to list (default 80)."))),
        new("get_quest_tracker", "What the Quest Tracker shows",
            "For one game window: the quests the overlay's Quest Tracker shows (title, kind, world, level), their steps and where each step goes, the picked quest and its way there. Only while that window's overlay shows a Quest Tracker.",
            Props(("window", "integer", WINDOW))),
        new("read_server_log", "Read the server's log",
            "The latest lines of the server's log (repeats folded into one line with a count).",
            Props(("lines", "integer", "How many lines (default 80, most 500)."),
                ("search", "string", "Only lines with all these words (optional)."),
                ("level", "string", "Only this level: INF, WRN, ERR, DBG or FTL (optional)."))),
        new("read_game_log", "Read Wizard101's own log",
            "The latest lines of the log Wizard101 writes about itself (WizardClient.log): zones loading, combat windows, quests, cinematics, errors. Read only; Ahziael never changes it.",
            Props(("lines", "integer", "How many lines (default 80, most 500)."),
                ("search", "string", "Only lines with all these words (optional)."),
                ("area", "string", "Only one area of the game, such as ClientDuel, CORE_SEER, CLIENT_QUEST, Cinematics, GUI or Spell (optional)."))),
        new("read_ahziael_events", "Read Ahziael's Logs list",
            "Ahziael's crashes, errors and refusals and the server's crash reports from Server > Logs, newest first.",
            Props(("count", "integer", "How many (default 20)."))),
        new("find_settings", "Find server settings",
            "Server settings (Imlight.ini) whose section, name, value or help text has these words, with their values and help. Values that look like passwords or keys are hidden.",
            Props(("search", "string", "Words to look for (empty lists the sections)."), ("limit", "integer", "Most to list (default 40)."))),
        new("run_command", "Run a chat command",
            "Runs a chat command (such as .elixir50 or .tp WC_Hub) as the wizard playing on this PC, the way Ahziael's command box does. Needs \"Let Claude make changes too\".",
            Props(("command", "string", "The command, with its dot."), ("wizard_id", "string", "Only this wizard (its id from get_positions) runs it (optional).")).Needs("command"), Changes: true),
        new("announce", "Announce to the server",
            "Shows an announcement to everyone playing: a box on screen, a chat line, or both. Needs \"Let Claude make changes too\".",
            Props(("text", "string", "What to announce."), ("where", "string", "box, chat or both (default both).")).Needs("text"), Changes: true),
        new("change_setting", "Change a server setting",
            "Changes one setting already in Imlight.ini (see find_settings). The server uses most settings at once; the answer says when one waits for a restart. Needs \"Let Claude make changes too\".",
            Props(("section", "string", "The section, as find_settings shows it."), ("key", "string", "The setting's name."), ("value", "string", "Its new value.")).Needs("section", "key", "value"), Changes: true),
    ];

    /// <summary>The tools as MCP's tools/list answer has them.</summary>
    internal static JsonObject ListResult() {
        var tools = new JsonArray();
        foreach (var t in All) {
            tools.Add(new JsonObject {
                ["name"] = t.Name,
                ["title"] = t.Title,
                ["description"] = t.Description,
                ["inputSchema"] = t.Input.DeepClone(),
                ["annotations"] = new JsonObject {
                    ["title"] = t.Title,
                    ["readOnlyHint"] = !t.Changes,
                    ["destructiveHint"] = false,
                    ["openWorldHint"] = false,
                },
            });
        }

        return new JsonObject { ["tools"] = tools };
    }

    internal static readonly string[] Versions = ["2025-06-18", "2025-03-26", "2024-11-05"];

    internal static JsonObject InitializeResult(JsonNode? request) {
        var asked = request?["params"]?["protocolVersion"]?.GetValue<string>();
        return new JsonObject {
            ["protocolVersion"] = asked is not null && Versions.Contains(asked) ? asked : Versions[0],
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = SERVER_NAME, ["title"] = Edition.NAME, ["version"] = SelfUpdate.CurrentVersion.ToString() },
            ["instructions"] = "Ahziael is the app that runs Niduus's own Wizard101 server (Imlight) and draws an overlay over the real Wizard101 game on this PC. "
                               + "Start with get_status. list_game_windows numbers the game windows; take_game_picture shows one. "
                               + "Positions, the minimap and the Quest Tracker come from the server; read_game_log is the game's own log. "
                               + "Ahziael does not read the game's memory, so the camera and the game's open windows are only in the pictures.",
        };
    }

    internal static JsonObject Text(string text, bool error = false) => new() {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = error,
    };

    internal static JsonObject Picture(byte[] png, string words) => new() {
        ["content"] = new JsonArray(
            new JsonObject { ["type"] = "image", ["data"] = System.Convert.ToBase64String(png), ["mimeType"] = "image/png" },
            new JsonObject { ["type"] = "text", ["text"] = words }),
        ["isError"] = false,
    };

    internal static JsonObject Reply(JsonNode? id, JsonNode result) => new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result };

    internal static JsonObject Fail(JsonNode? id, int code, string message) => new() {
        ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

}
#endif
