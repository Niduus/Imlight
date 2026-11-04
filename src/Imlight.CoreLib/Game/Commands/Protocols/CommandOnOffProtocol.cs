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
using System.Text;
using System.Text.Json;
using Imlight.Common;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.onoff`: Ahziael's Game Rules > On and Off from the game's chat (Niduus, 2026-11-04, chat commands for Ahziael's
/// features). It changes spiraldb-extra/Customs/TurnedOff.json, the file the tab writes; the server watches it, so the
/// change counts at once (<see cref="TurnedOff"/>). Administrators only.
/// <list type="bullet">
/// <item>.onoff: what is turned off now.</item>
/// <item>.onoff off Gobbler / .onoff on Gobbler: every NPC or creature with that exact name (or a template ID).</item>
/// <item>.onoff off zone WizardCity/WC_Ravenwood, .onoff off world Krokotopia, .onoff off custom 3.</item>
/// </list>
/// Also .turnoff and .turnon with the same words.
/// </summary>
internal class CommandOnOffProtocol : CommandProtocol {

    private const string FOLDER = "Customs";
    private const int PAGE_SIZE = 10;
    // A name matching more templates than this is too loose; the player is asked to pick.
    private const int MAX_TEMPLATES = 25;

    private static readonly JsonSerializerOptions s_options = new() {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
    };

    private static readonly object s_lock = new();

    internal override string Group { get; set; } = "";

    private static string FilePath => Path.Combine(SpiralDB.ExtraPath, FOLDER, TurnedOff.FILE);

    [Command("onoff")]
    [AuthRequired(AuthLevel.Administrator)]
    private void OnOffCommand([Remainder] string text = "") {
        var words = (text ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count == 0 || words[0].Equals("list", StringComparison.OrdinalIgnoreCase)
            || int.TryParse(words[0], NumberStyles.None, CultureInfo.InvariantCulture, out _) && words.Count == 1) {
            var page = words.Count > 0 && int.TryParse(words[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var asked) ? asked : 1;
            ShowOff(page);

            return;
        }

        var verb = words[0].ToLowerInvariant();
        if (verb is not ("on" or "off")) {
            InformSenderClient("Type .onoff off and a name to turn something off, .onoff on and a name to turn it back on, "
                + "or .onoff to see what is off. Such as .onoff off Gobbler or .onoff off world Krokotopia.");

            return;
        }

        Switch(verb == "off", string.Join(' ', words.Skip(1)));
    }

    [Command("turnoff")]
    [AuthRequired(AuthLevel.Administrator)]
    private void TurnOffCommand([Remainder] string what = "") => Switch(true, what);

    [Command("turnon")]
    [AuthRequired(AuthLevel.Administrator)]
    private void TurnOnCommand([Remainder] string what = "") => Switch(false, what);

    private void Switch(bool off, string what) {
        what = (what ?? "").Trim();
        if (what.Length == 0) {
            InformSenderClient($"Type what to turn {(off ? "off" : "on")}: a name, a template ID, zone <zone>, world <world> or custom <number>.");

            return;
        }

        var words = what.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var kind = words[0].ToLowerInvariant();
        var rest = words.Length > 1 ? words[1].Trim() : "";

        try {
            switch (kind) {
                case "custom" when int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out var custom) && custom > 0:
                    Save(off, s => Toggle(s.Customs, custom, off), $"Custom_{custom}", $"Custom {custom}", $"custom creature {custom}");

                    return;
                case "zone" when rest.Length > 0:
                    SwitchZone(off, rest, world: false);

                    return;
                case "world" when rest.Length > 0:
                    SwitchZone(off, rest, world: true);

                    return;
            }

            if (what.Contains('/')) {
                SwitchZone(off, what, world: false);

                return;
            }

            SwitchTemplates(off, what);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            InformSenderClient($"The On and Off list could not be changed: {ex.Message}");
        }
        catch (Exception ex) {
            // Such as the zone list not being read yet.
            Logger.Error("On and Off: .onoff {0} failed: {1}", Logger.Args(what, ex.Message));
            InformSenderClient("That could not be changed right now. Try again in a minute, or use Ahziael's Game Rules > On and Off.");
        }
    }

    private void SwitchTemplates(bool off, string what) {
        List<IdCatalogEntry> matches;
        if (ulong.TryParse(what, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) {
            matches = IdCatalog.IsReady ? IdCatalog.Search(null, what, 5) : [];
            if (matches.Count == 0) {
                matches = [new IdCatalogEntry(id, IdCatalogKind.Npc, $"Template {id}", "", 0, "")];
            }
        }
        else {
            if (!IdCatalog.IsReady) {
                InformSenderClient("The ID list is still being built. Try again in a minute, or use a template ID.");

                return;
            }

            var found = IdCatalog.Search(IdCatalogKind.Npc, what, 200).Concat(IdCatalog.Search(IdCatalogKind.Creature, what, 200)).ToList();
            matches = [.. found.Where(e => e.Name.Equals(what, StringComparison.OrdinalIgnoreCase))];
            if (matches.Count == 0) {
                var near = found.Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList();
                InformSenderClient($"No NPC or creature is called \"{what}\"."
                    + (near.Count > 0 ? $" Did you mean {string.Join(", ", near)}?" : " .lookup npc or .lookup mob finds names and IDs.")
                    + " For a zone or world, type zone or world first.");

                return;
            }

            if (matches.Count > MAX_TEMPLATES) {
                InformSenderClient($"{matches.Count} templates are called \"{what}\". Use one template ID instead (.lookup mob {what}).");

                return;
            }
        }

        Save(off, s => {
            var changed = 0;
            foreach (var match in matches) {
                changed += Toggle(s.Templates, match.Id, off);
                var key = match.Id.ToString(CultureInfo.InvariantCulture);
                if (off) {
                    s.Names[key] = match.Name;
                }
            }

            return changed;
        }, null, null, matches.Count == 1 ? (matches[0].Name.StartsWith("Template ", StringComparison.Ordinal) ? matches[0].Name : $"{matches[0].Name} ({matches[0].Id})") : $"{matches.Count} templates called {matches[0].Name}");
    }

    private void SwitchZone(bool off, string what, bool world) {
        var zones = AccessPassManager.Zones.ToList();
        what = what.Trim().Trim('/');
        string target;
        if (world) {
            var worlds = zones.Select(z => z.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var wanted = Squash(what);
            target = worlds.FirstOrDefault(w => Squash(w) == wanted) ?? worlds.FirstOrDefault(w => Squash(w).StartsWith(wanted, StringComparison.Ordinal));
            if (target is null) {
                InformSenderClient($"There is no world called \"{what}\". Worlds: {string.Join(", ", worlds.Take(30))}.");

                return;
            }
        }
        else {
            target = zones.FirstOrDefault(z => z.Equals(what, StringComparison.OrdinalIgnoreCase));
            if (target is null) {
                var near = zones.Where(z => z.Contains(what, StringComparison.OrdinalIgnoreCase)).Take(6).ToList();
                InformSenderClient($"There is no zone called \"{what}\"."
                    + (near.Count > 0 ? $" Did you mean {string.Join(", ", near)}?" : " Zones look like WizardCity/WC_Ravenwood."));

                return;
            }

            if (off && target.Equals(TurnedOff.ALWAYS_ON_ZONE, StringComparison.OrdinalIgnoreCase)) {
                InformSenderClient("The Commons can't be turned off, so there is always somewhere to go.");

                return;
            }
        }

        Save(off, s => {
            var had = s.Zones.RemoveAll(z => z.Trim().Trim('/').Equals(target, StringComparison.OrdinalIgnoreCase));
            if (off) {
                s.Zones.Add(target);
            }

            return off ? (had == 0 ? 1 : 0) : had;
        }, target, target, world ? $"the world {target}" : $"the zone {target}");
    }

    // Changes the file; nameKey/name, when given, keep what the app shows for the entry.
    private void Save(bool off, Func<TurnedOff.Switches, int> change, string nameKey, string name, string shown) {
        int changed;
        lock (s_lock) {
            var path = FilePath;
            var switches = File.Exists(path)
                ? JsonSerializer.Deserialize<TurnedOff.Switches>(File.ReadAllText(path), s_options) ?? new()
                : new TurnedOff.Switches();
            switches.Templates ??= [];
            switches.Customs ??= [];
            switches.CustomNpcs ??= [];
            switches.Zones ??= [];
            switches.Names ??= [];

            changed = change(switches);
            if (changed == 0) {
                InformSenderClient($"{Cap(shown)} is already {(off ? "off" : "on")}.");

                return;
            }

            if (nameKey is not null && off) {
                switches.Names[nameKey] = name;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".saving";
            File.WriteAllText(temp, JsonSerializer.Serialize(switches, s_options));
            File.Move(temp, path, overwrite: true);
        }

        Logger.Information("On and Off changed in game by {0}: {1} turned {2}.", Logger.Args(Context.Account?.Username, shown, off ? "off" : "on"));
        InformSenderClient(off
            ? $"Turned off {shown}. It leaves every zone now (one in a battle finishes it first). .onoff on brings it back."
            : $"Turned {shown} back on. It comes back right away.");
    }

    private static int Toggle<T>(List<T> list, T value, bool off) {
        if (off) {
            if (list.Contains(value)) {
                return 0;
            }

            list.Add(value);

            return 1;
        }

        return list.RemoveAll(v => Equals(v, value));
    }

    private void ShowOff(int page) {
        TurnedOff.Switches switches;
        try {
            switches = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<TurnedOff.Switches>(File.ReadAllText(FilePath), s_options) ?? new()
                : new TurnedOff.Switches();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            InformSenderClient($"The On and Off list could not be read: {ex.Message}");

            return;
        }

        var names = switches.Names ?? [];
        var rows = (switches.Templates ?? []).Select(t => $"{names.GetValueOrDefault(t.ToString(CultureInfo.InvariantCulture), "Template")} ({t})")
            .Concat((switches.Customs ?? []).Select(c => $"Custom {c}"))
            .Concat((switches.CustomNpcs ?? []).Select(n => $"Custom vendor or trainer {n}"))
            .Concat((switches.Zones ?? []).Select(z => z.Contains('/') ? $"Zone {z}" : $"World {z}"))
            .ToList();
        if (rows.Count == 0) {
            InformSenderClient("Nothing is turned off. Type .onoff off and a name to turn something off.");

            return;
        }

        var pages = (rows.Count + PAGE_SIZE - 1) / PAGE_SIZE;
        page = Math.Clamp(page, 1, pages);
        var text = new StringBuilder()
            .AppendLine($"<center>Turned off: {rows.Count}{(pages > 1 ? $" (page {page} of {pages})" : "")}</center>")
            .AppendLine();
        foreach (var row in rows.Skip((page - 1) * PAGE_SIZE).Take(PAGE_SIZE)) {
            text.AppendLine(row);
        }

        text.AppendLine();
        if (page < pages) {
            text.AppendLine($"More: type .onoff {page + 1}");
        }

        text.Append(".onoff on and the name turns one back on.");
        InformSenderClient(text.ToString(), true);
    }

    private static string Cap(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string Squash(string text) => string.Concat(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant));

}
