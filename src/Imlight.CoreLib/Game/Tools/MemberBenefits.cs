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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.Tools;

/// <summary>
/// The member benefits the Imlight app switches on and off (Niduus, 2026-10-23). The app writes member-benefits.json next
/// to the server: each benefit, whether it is on, and when it turns itself off (or never). Every second the server
/// checks it; when a benefit turns on or off, by the app or by its timer, everyone online gets the game's benefit flags
/// again and a server message saying so. A wizard logging in is told which benefits are on and how long each has left.
/// With no file, [Character] MemberBenefits decides: every benefit on, or none.
/// </summary>
internal static class MemberBenefits {

    private const string BENEFITS_FILE = "member-benefits.json";

    // The game's member benefit flags (ElixirBenefitEffectTemplate m_flags), with the names the messages use.
    internal static readonly (string Key, string Name, uint Flag)[] All = [
        ("GardeningBonus", "Gardening Bonus", 1),
        ("DoublePetXP", "Double Pet XP", 2),
        ("FreeRespec", "Free Respec", 4),
        ("DailyQuestBonus", "Daily Quest Bonus", 8),
        ("MonsterMagic", "Monster Magic", 16),
        ("DailyPvPBonus", "Daily PvP Bonus", 32),
        ("FishingBonus", "Fishing Bonus", 64),
        ("CraftingBonus", "Crafting Bonus", 128),
        ("SecondChanceChestBonus", "Second Chance Chest", 256),
        ("DoubleScrollOfFortunePoints", "Double Scroll of Fortune Points", 512),
        ("FasterEnergyRegeneration", "Faster Energy Regeneration", 1024),
    ];

    private static readonly uint s_allFlags = All.Aggregate(0u, (sum, b) => sum | b.Flag);
    private static readonly bool s_iniDefault = ConfigurationManager.GetValue("Character.MemberBenefits", true);
    private static readonly double s_energyFactor = Math.Clamp(ConfigurationManager.GetValue("Character.FasterEnergyFactor", 2.0), 1.0, 60.0);

    private static Timer s_timer;
    private static ActorSystem s_system;
    private static DateTime s_fileTime;
    private static bool s_hasFile;

    // Each benefit's end (DateTime.MaxValue when it has no timer), for the ones switched on.
    private static Dictionary<string, DateTime> s_switchedOn = [];
    private static HashSet<string> s_active = [];

    // Accounts that logged in and have not yet been told the benefits.
    private static readonly ConcurrentDictionary<ulong, byte> s_toGreet = new();

    /// <summary>
    /// Starts watching the app's file. Calling it again does nothing.
    /// </summary>
    internal static void Start(ActorSystem system) {
        if (Interlocked.CompareExchange(ref s_system, system, null) is not null) {
            return;
        }

        Load();
        s_active = ActiveNow();
        s_timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// The member benefit flags the game gets. Every benefit on sends every bit, as before.
    /// </summary>
    internal static uint Flags(uint stored) {
        if (!s_hasFile) {
            return s_iniDefault ? uint.MaxValue : stored;
        }

        var flags = All.Where(b => s_active.Contains(b.Key)).Aggregate(0u, (sum, b) => sum | b.Flag);

        return flags == s_allFlags ? uint.MaxValue : flags;
    }

    /// <summary>
    /// Whether a benefit (a key of <see cref="All"/>) is on right now, for the server's own systems that it changes.
    /// </summary>
    internal static bool IsOn(string key)
        => s_hasFile ? s_active.Contains(key) : s_iniDefault;

    /// <summary>
    /// The seconds between energy ticks: the game's own time, or that divided by [Character] FasterEnergyFactor while
    /// Faster Energy Regeneration is on.
    /// </summary>
    internal static int EnergyTickSeconds(int gameSeconds)
        => IsOn(FASTER_ENERGY) ? Math.Max(1, (int) Math.Round(gameSeconds / s_energyFactor)) : gameSeconds;

    internal const string GARDENING = "GardeningBonus";
    internal const string DOUBLE_PET_XP = "DoublePetXP";
    internal const string DAILY_QUEST = "DailyQuestBonus";
    internal const string FISHING = "FishingBonus";
    internal const string CRAFTING = "CraftingBonus";
    internal const string FASTER_ENERGY = "FasterEnergyRegeneration";
    internal const string DOUBLE_SOF = "DoubleScrollOfFortunePoints";

    /// <summary>
    /// The login server calls this when an account logs in; its next arrival in the world tells it the benefits.
    /// </summary>
    internal static void LoggedIn(ulong accountId) => s_toGreet[accountId] = 0;

    /// <summary>
    /// The lines a wizard arriving in the world is told, once per login: every benefit on, with the time left.
    /// </summary>
    internal static List<string> GreetingFor(ulong accountId) {
        if (!s_toGreet.TryRemove(accountId, out _)) {
            return [];
        }

        var active = All.Where(b => s_active.Contains(b.Key)).ToList();
        if (active.Count == 0) {
            return [];
        }

        return ["Member benefits on:", .. active.Select(b => $"{b.Name}{TimeLeft(b.Key)}")];
    }

    private static void Tick() {
        try {
            if (File.Exists(BENEFITS_FILE) ? File.GetLastWriteTimeUtc(BENEFITS_FILE) != s_fileTime : s_hasFile) {
                Load();
            }

            var now = ActiveNow();
            var turnedOn = All.Where(b => now.Contains(b.Key) && !s_active.Contains(b.Key)).ToList();
            var turnedOff = All.Where(b => !now.Contains(b.Key) && s_active.Contains(b.Key)).ToList();
            if (turnedOn.Count == 0 && turnedOff.Count == 0) {
                return;
            }

            s_active = now;
            var lines = turnedOn.Select(b => $"Member benefit on: {b.Name}{TimeLeft(b.Key)}")
                .Concat(turnedOff.Select(b => $"Member benefit off: {b.Name}"))
                .ToList();
            foreach (var line in lines) {
                Logger.Information("{0}", Logger.Args(line));
            }

            Broadcast(lines);
        }
        catch (Exception ex) {
            Logger.Error("Could not check the member benefits: {0}", Logger.Args(ex.Message));
        }
    }

    private static void Broadcast(List<string> lines) {
        var flags = Flags(0);
        foreach (var player in OnlinePlayerCollection.GetOnlinePlayers().Where(p => p.CharacterId != 0 && p.ActorPath is not null)) {
            var session = s_system.ActorSelection(player.ActorPath);
            session.Tell(new WIZARD2_53_PROTOCOL.MSG_UPDATESSUBSCRIBERBEBEFITFLAGS { SubscriberBenefitFlags = flags });
            foreach (var line in lines) {
                session.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = line, Modal = 0 });
            }
        }
    }

    private static HashSet<string> ActiveNow() {
        if (!s_hasFile) {
            return s_iniDefault ? [.. All.Select(b => b.Key)] : [];
        }

        var now = DateTime.UtcNow;

        return [.. s_switchedOn.Where(b => b.Value > now).Select(b => b.Key)];
    }

    // " (1d 23h left)", or nothing for a benefit with no timer.
    private static string TimeLeft(string key) {
        if (!s_switchedOn.TryGetValue(key, out var until) || until == DateTime.MaxValue) {
            return "";
        }

        var left = until - DateTime.UtcNow;
        if (left <= TimeSpan.Zero) {
            return "";
        }

        var text = left.TotalDays >= 1 ? $"{(int) left.TotalDays}d {left.Hours}h"
            : left.TotalHours >= 1 ? $"{left.Hours}h {left.Minutes}m"
            : $"{Math.Max(1, (int) Math.Ceiling(left.TotalMinutes))}m";

        return $" ({text} left)";
    }

    private static void Load() {
        if (!File.Exists(BENEFITS_FILE)) {
            s_hasFile = false;
            s_switchedOn = [];

            return;
        }

        try {
            var time = File.GetLastWriteTimeUtc(BENEFITS_FILE);
            using var json = JsonDocument.Parse(File.ReadAllText(BENEFITS_FILE));
            var switchedOn = new Dictionary<string, DateTime>();
            if (json.RootElement.TryGetProperty("Benefits", out var benefits) && benefits.ValueKind == JsonValueKind.Array) {
                foreach (var benefit in benefits.EnumerateArray()) {
                    if (!benefit.TryGetProperty("Key", out var key) || key.GetString() is not { } name
                        || !benefit.TryGetProperty("On", out var on) || on.ValueKind != JsonValueKind.True) {
                        continue;
                    }

                    var until = benefit.TryGetProperty("Until", out var end) && end.ValueKind == JsonValueKind.String
                        && end.TryGetDateTime(out var at) ? at.ToUniversalTime() : DateTime.MaxValue;
                    switchedOn[name] = until;
                }
            }

            s_switchedOn = switchedOn;
            s_hasFile = true;
            s_fileTime = time;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            // The app is still writing the file; it is read on the next round.
        }
    }

}
