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
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Commands;
using Imlight.CoreLib.Game.Commands.Protocols;
using Imlight.RemoteAdmin;

namespace Imlight.CoreLib.Game.Tools;

/// <summary>
/// Overlay buttons that run a command or cheat for the wizard in their own game window (Niduus, 2026-11-11: "I want the
/// cheat buttons and to link the cheats in the buttons but only for that client only"), with no username or password.
///
/// Each game window's overlay is linked to one wizard on the overlay's own live connection (OverlayGate's link, op
/// "cmd"). With one wizard playing from that PC and one game window, the link is made by itself. Otherwise the overlay
/// shows a short code on that game window and the player types ".link code" in that game's chat once: the wizard who
/// types it is the one that window's buttons act for. Every command still checks that the wizard's game connects from
/// the same address as the overlay, runs as that wizard with the account's own rank (as if typed in chat), and only
/// switches or uses that wizard's own cheats. A link ends when the overlay disconnects.
/// </summary>
public static class OverlayLinks {

    /// <summary>[Overlay] PlayerButtons: overlay buttons may run commands and cheats here. On unless turned off; live.</summary>
    public static bool On => ConfigurationManager.GetValue("Overlay.PlayerButtons", true);

    private static readonly TimeSpan s_codeLife = TimeSpan.FromMinutes(5);
    private const int RATE_COUNT = 10;
    private static readonly TimeSpan s_rateWindow = TimeSpan.FromSeconds(5);
    private const int MAX_VALUE = 300;

    private sealed record Code(long Link, long Window, string Address, DateTime Until);

    private static readonly ConcurrentDictionary<string, Code> s_codes = new();
    private static readonly ConcurrentDictionary<(long Link, long Window), ulong> s_bound = new();
    private static readonly ConcurrentDictionary<long, ConcurrentQueue<DateTime>> s_recent = new();

    /// <summary>The overlay link closed: its windows' links and codes go.</summary>
    public static void Closed(long link) {
        foreach (var key in s_bound.Keys.Where(k => k.Link == link).ToList()) {
            s_bound.TryRemove(key, out _);
        }

        foreach (var (code, _) in s_codes.Where(c => c.Value.Link == link).ToList()) {
            s_codes.TryRemove(code, out _);
        }

        s_recent.TryRemove(link, out _);
        s_mapAsks.TryRemove(link, out _);
        foreach (var key in s_mapNotes.Keys.Where(k => k.Link == link).ToList()) {
            s_mapNotes.TryRemove(key, out _);
        }
    }

    /// <summary>".link code" typed in the game by <paramref name="charId"/>: links that code's game window to this wizard.</summary>
    internal static string Link(ulong charId, string code) {
        code = new string((code ?? "").Where(char.IsDigit).ToArray());
        foreach (var (old, gone) in s_codes.Where(c => c.Value.Until < DateTime.UtcNow).ToList()) {
            s_codes.TryRemove(old, out _);
        }

        if (code.Length == 0) {
            return "Type .link and the code your overlay shows on this game window, such as .link 482913.";
        }

        if (!s_codes.TryRemove(code, out var found)) {
            return "That code isn't right or is too old. Click the button on the game again for a new code.";
        }

        s_bound[(found.Link, found.Window)] = charId;

        return "This game window's overlay buttons now work for you. Click the button again.";
    }

    /// <summary>One button press (op "cmd") on overlay link <paramref name="link"/>: checked, run, answered.</summary>
    public static async Task<JsonObject> RunAsync(long link, int linkWindows, JsonObject header, string address) {
        address = OverlayGate.Normalize(address);
        var window = (long?) header["window"] ?? 0;
        var kind = ((string) header["kind"] ?? "").Trim().ToLowerInvariant();
        var value = ((string) header["value"] ?? "").ReplaceLineEndings(" ").Trim();
        if (!On) {
            return RemoteAdminWire.Error("The host has turned off overlay buttons that run commands and cheats on this server.");
        }

        if (value.Length > MAX_VALUE) {
            return RemoteAdminWire.Error("That command is too long.");
        }

        var times = s_recent.GetOrAdd(link, _ => new ConcurrentQueue<DateTime>());
        while (times.TryPeek(out var oldest) && DateTime.UtcNow - oldest > s_rateWindow) {
            times.TryDequeue(out _);
        }

        if (times.Count >= RATE_COUNT) {
            return RemoteAdminWire.Error("Slow down a little: at most 10 clicks every 5 seconds.");
        }

        times.Enqueue(DateTime.UtcNow);

        // The mob (creature) cheats a button can set off: every creature cheat whose When is "pressed", by name.
        if (kind == "mobcheats") {
            var list = MobCheats();
            var answer = RemoteAdminWire.Ok();
            answer["cheats"] = list;
            answer["text"] = list.Count == 0 ? "No creature on this server has a \"when pressed\" cheat yet." : "";

            return answer;
        }

        string text;
        string mobCheat = null;
        switch (kind) {
            case "cheats":
                text = "";
                break;
            case "mobcheat":
                if (value.Length == 0) {
                    return RemoteAdminWire.Error("This button has no mob cheat picked yet.");
                }

                // Pressed below, for this wizard's battle only, once the wizard is known.
                mobCheat = value;
                text = "";
                break;
            case "cheat":
                if (value.Length == 0) {
                    return RemoteAdminWire.Error("This button has no cheat picked yet.");
                }

                text = "cheat " + value;
                break;
            case "command":
                text = value.TrimStart('.').Trim();
                if (text.Length == 0) {
                    return RemoteAdminWire.Error("This button has no command yet.");
                }

                break;
            default:
                return RemoteAdminWire.Error($"This server doesn't know \"{kind}\". Update Ahziael on both PCs.");
        }

        var charId = await WizardFor(link, window, linkWindows, address);
        if (charId == 0) {
            if (!(await WizardsAt(address)).Any()) {
                return RemoteAdminWire.Error("No wizard is in the game on this server from this PC. Log in to the game first, then try again.");
            }

            // Not linked yet: a code for this game window, typed in its game's chat.
            var code = NewCode(link, window, address);
            var ask = RemoteAdminWire.Error($"Type .link {code} in this game's chat once, then click again.");
            ask["link"] = code;

            return ask;
        }

        var output = new ConcurrentQueue<string>();
        var found = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new OverlayPlayerCommand(charId, address, text, output, found);
        foreach (var service in AgentBridge.CommandServices) {
            service.Tell(request);
        }

        if (await Task.WhenAny(found.Task, Task.Delay(2000)) != found.Task) {
            // That wizard left: the link goes, and the next click links again.
            s_bound.TryRemove((link, window), out _);

            return RemoteAdminWire.Error("The wizard this game window was linked to isn't in the game any more. Click again.");
        }

        if (!found.Task.Result) {
            s_bound.TryRemove((link, window), out _);

            return RemoteAdminWire.Error("That wizard is playing from another PC. Buttons only work for the game on this PC.");
        }

        if (mobCheat is not null) {
            PlayerCheatPresses.Press(charId, $"name:{mobCheat.ToLowerInvariant()}");
            var pressed = RemoteAdminWire.Ok();
            pressed["text"] = $"{mobCheat} is pressed. If a creature in your battle has a \"when pressed\" cheat by that name, it goes off at the next move.";

            return pressed;
        }

        if (text.Length > 0) {
            Logger.Information("Overlay buttons: wizard {0} ran \"{1}\".", Logger.Args(charId, text));
            for (var i = 0; i < 15; i++) {
                await Task.Delay(100);
                if (!output.IsEmpty && i >= 2) {
                    break;
                }
            }
        }

        var reply = RemoteAdminWire.Ok();
        reply["text"] = string.Join("\n", output);
        if (kind is "cheats" or "cheat") {
            reply["cheats"] = PlayerCheatChat.ListJson(charId);
        }

        return reply;
    }

    private static readonly ConcurrentDictionary<long, ConcurrentQueue<DateTime>> s_mapAsks = new();

    /// <summary>
    /// The minimap of one game window (op "map", OverlayMinimap), for the wizard that window is linked to; linked by
    /// itself like a button, otherwise the map shows the ".link" code to type.
    /// </summary>
    public static async Task<JsonObject> MapAsync(long link, int linkWindows, JsonObject header, string address) {
        var answer = await MapOfAsync(link, linkWindows, header, address);
        // What the minimap shows instead of a map goes in the log once each time it changes, so a blank map can be told apart.
        var error = answer["ok"]?.GetValue<bool>() == true ? "" : (string) answer["error"] ?? "";
        var key = (link, (long?) header["window"] ?? 0);
        if (error != "busy" && (!s_mapNotes.TryGetValue(key, out var said) || said != error)) {
            s_mapNotes[key] = error;
            Logger.Information("Overlay minimap from {0}: {1}", Logger.Args(OverlayGate.Normalize(address), error.Length == 0 ? "the map is sent." : error));
        }

        return answer;
    }

    private static readonly ConcurrentDictionary<(long Link, long Window), string> s_mapNotes = new();

    private static async Task<JsonObject> MapOfAsync(long link, int linkWindows, JsonObject header, string address) {
        address = OverlayGate.Normalize(address);
        var window = (long?) header["window"] ?? 0;
        if (!OverlayMinimap.On) {
            return RemoteAdminWire.Error("The host has turned off the minimap on this server.");
        }

        // A few windows each asking once a second is fine; more is slowed down.
        var times = s_mapAsks.GetOrAdd(link, _ => new ConcurrentQueue<DateTime>());
        while (times.TryPeek(out var oldest) && DateTime.UtcNow - oldest > TimeSpan.FromSeconds(1)) {
            times.TryDequeue(out _);
        }

        if (times.Count >= 8) {
            return RemoteAdminWire.Error("busy");
        }

        times.Enqueue(DateTime.UtcNow);

        var charId = await WizardFor(link, window, linkWindows, address);
        if (charId == 0) {
            if (!(await WizardsAt(address)).Any()) {
                return RemoteAdminWire.Error("Log in to the game to see the map.");
            }

            var code = CodeFor(link, window, address);
            var ask = RemoteAdminWire.Error($"Type .link {code} in this game's chat once to show your map here.");
            ask["link"] = code;

            return ask;
        }

        var reply = new TaskCompletionSource<(JsonObject Map, IActorRef Zone, string Error)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var others = new ConcurrentQueue<(ulong CharId, IActorRef Zone, string Name, float X, float Y)>();
        var ask2 = new OverlayMapAsk(charId, address, reply, others);
        foreach (var service in AgentBridge.CommandServices) {
            service.Tell(ask2);
        }

        if (await Task.WhenAny(reply.Task, Task.Delay(2000)) != reply.Task) {
            s_bound.TryRemove((link, window), out _);

            return RemoteAdminWire.Error("The wizard this game window was linked to isn't in the game any more.");
        }

        var (map, zone, error) = reply.Task.Result;
        if (map is null) {
            s_bound.TryRemove((link, window), out _);

            return RemoteAdminWire.Error(error);
        }

        // The other sessions answer about as fast; a short wait lets them.
        await Task.Delay(60);
        OverlayMinimap.AddWizards(map, charId, zone, others);
        // The streets of the zone, drawn from above (ZoneGround), sent once per zone: the overlay says which it has.
        var zonePath = (string) map["zone"] ?? "";
        if (OverlayMinimap.Streets && zonePath.Length > 0 && !string.Equals((string) header["ground"] ?? "", zonePath, StringComparison.OrdinalIgnoreCase)) {
            var ground = Zone.Core.ZoneGround.Of(zonePath);
            map["ground"] = ground.IsEmpty
                ? new JsonObject { ["zone"] = zonePath }
                : new JsonObject {
                    ["zone"] = zonePath, ["left"] = ground.Left, ["top"] = ground.Top, ["cell"] = ground.Cell,
                    ["w"] = ground.Width, ["h"] = ground.Height, ["runs"] = ground.Runs,
                };
        }

        var answer = RemoteAdminWire.Ok();
        answer["map"] = map;

        return answer;
    }

    // The code waiting for this game window, or a new one (the map asks every second; the code stays the same).
    private static string CodeFor(long link, long window, string address) {
        var waiting = s_codes.FirstOrDefault(c => c.Value.Link == link && c.Value.Window == window && c.Value.Until > DateTime.UtcNow + TimeSpan.FromMinutes(1));

        return waiting.Key ?? NewCode(link, window, address);
    }

    // The wizard this game window is linked to: as linked before, or by itself when this PC has one wizard in the game
    // and one game window (window 0 is Ahziael's designer: the window's own link when the overlay has only one).
    // Every creature cheat that goes off when pressed, once per name, with the creatures that have it.
    private static JsonArray MobCheats() {
        var names = new System.Collections.Generic.SortedDictionary<string, System.Collections.Generic.List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (template, cheats) in BossCheats.All()) {
            foreach (var cheat in cheats.Where(c => c.Is("Pressed") && !string.IsNullOrWhiteSpace(c.Name))) {
                var name = cheat.Name.Trim();
                if (!names.TryGetValue(name, out var owners)) {
                    names[name] = owners = [];
                }

                var owner = BossCheats.NameOf(template);
                if (owners.Count < 3 && !string.IsNullOrWhiteSpace(owner) && !owners.Contains(owner)) {
                    owners.Add(owner);
                }
            }
        }

        var list = new JsonArray();
        foreach (var (name, owners) in names.Take(500)) {
            list.Add(new JsonObject { ["number"] = 0, ["name"] = name, ["canUse"] = true, ["owner"] = string.Join(", ", owners) });
        }

        return list;
    }

    private static async Task<ulong> WizardFor(long link, long window, int linkWindows, string address) {
        if (s_bound.TryGetValue((link, window), out var bound)) {
            return bound;
        }

        if (window == 0) {
            var mine = s_bound.Where(b => b.Key.Link == link).Select(b => b.Value).Distinct().ToList();
            if (mine.Count == 1) {
                return mine[0];
            }
        }

        var wizards = await WizardsAt(address);
        if (wizards.Count == 1 && linkWindows <= 1 && OverlayGate.Covered(address) <= 1) {
            s_bound[(link, window)] = wizards[0];

            return wizards[0];
        }

        return 0;
    }

    private static async Task<System.Collections.Generic.List<ulong>> WizardsAt(string address) {
        var found = new ConcurrentQueue<ulong>();
        var ask = new OverlayWizardsAt(address, found);
        foreach (var service in AgentBridge.CommandServices) {
            service.Tell(ask);
        }

        await Task.Delay(300);

        return found.Distinct().ToList();
    }

    private static string NewCode(long link, long window, string address) {
        foreach (var (old, _) in s_codes.Where(c => c.Value.Link == link && c.Value.Window == window || c.Value.Until < DateTime.UtcNow).ToList()) {
            s_codes.TryRemove(old, out _);
        }

        while (true) {
            var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (s_codes.TryAdd(code, new Code(link, window, address, DateTime.UtcNow + s_codeLife))) {
                return code;
            }
        }
    }

}
