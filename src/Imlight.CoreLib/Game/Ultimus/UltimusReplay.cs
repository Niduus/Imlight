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
using System.IO;
using System.Linq;
using Imlight.CoreLib.Game.BotShared;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// The replay pack (approved 2026-11-14): every bug Niduus showed in a screenshot, as a check of the decision behind it,
/// run by Ultimus's self-test after each update. A failing check is written up pink and listed in replay-pack.txt, so a
/// fixed bug that comes back is seen at once. Add a line here for each new screenshot bug.
/// </summary>
internal static class UltimusReplay {

    private sealed record Case(string Bug, Func<string> Check);

    private static readonly Case[] s_cases = [
        new("Waited to heal at 875 of 1,099 health (2026-11-14)", () =>
            UltimusDirectorActor.HealChoice(onlyPrep: true, potion: false, wispHere: false, shopHops: 3, wispHops: 2) == "go-on" ? null : "he would still stop to heal"),
        new("Stood waiting to heal with wisps a few areas away (2026-11-14)", () =>
            UltimusDirectorActor.HealChoice(false, false, false, shopHops: -1, wispHops: 2) == "wisp-area" ? null : "he would not go for the wisps"),
        new("Potions closer than wisps: he buys bottles (2026-11-14)", () =>
            UltimusDirectorActor.HealChoice(false, false, false, shopHops: 1, wispHops: 3) == "shop" ? null : "he would not go to the closer shop"),
        new("Wisps and shop the same distance: free wisps first (2026-11-14)", () =>
            UltimusDirectorActor.HealChoice(false, false, false, shopHops: 2, wispHops: 2) == "wisp-area" ? null : "he would pay instead of free wisps"),
        new("Waited for wisps at 63% health (2026-11-14 \"oh boy\")", () =>
            !UltimusDirectorActor.HuntsAt(0.63) && UltimusDirectorActor.HuntsAt(0.2) ? null : "he would still hunt or wait above 25%"),
        new("Farmed Desert Golems for XP instead of questing (2026-11-14)", () =>
            UltimusPlaybook.Value("Push.SeekFights") < 0.5 && UltimusMission.HardLimit("Push.SeekFights") ? null : "seeking fights for XP is on or tunable"),
        new("Menu Chat \"Hi\" answered \"Hello?\" (2026-11-14)", () =>
            Enumerable.Range(0, 20).Select(i => MenuChatBook.Exact(new Random(i), "Hello", "Hi", "Hey", "Greetings")).Any(p => p?.Text.TrimEnd().EndsWith('?') == true)
                ? "a puzzled \"Hello?\" can still be picked" : null),
        new("\"Follow me\" answered \"Sorry, I am hatching with someone else\" (2026-11-14)", () =>
            Enumerable.Range(0, 20).Select(i => MenuChatBook.Exact(new Random(i), "No thanks", "No, thank you", "Sorry", "Not now", "No"))
                .Any(p => p is not null && (MenuChatBook.Says(p.Text, "hatching") || MenuChatBook.Says(p.Text, "busy"))) ? "an untrue Menu Chat line can still be picked" : null),
    ];

    /// <summary>The failed checks (plain words), chat answers included; written to replay-pack.txt.</summary>
    internal static List<string> Run() {
        var failed = new List<string>();
        foreach (var c in s_cases) {
            try {
                if (c.Check() is { } why) {
                    failed.Add($"{c.Bug}: {why}");
                }
            }
            catch (Exception e) {
                failed.Add($"{c.Bug}: the check broke ({e.Message})");
            }
        }

        failed.AddRange(BotSocialChecks.Run().Select(f => "Chat: " + f));
        try {
            var total = s_cases.Length + BotSocialChecks.All.Length;
            File.WriteAllLines(Path.Combine(UltimusWire.Folder, "replay-pack.txt"),
                [$"Replay pack {DateTime.Now:yyyy-MM-dd HH:mm}: {total - failed.Count} of {total} screenshot bugs stay fixed.", .. failed.Select(f => "FAILED " + f)]);
        }
        catch (IOException) {
            // The self-test still says it.
        }

        return failed;
    }

    internal static int Count => s_cases.Length + BotSocialChecks.All.Length;

}
