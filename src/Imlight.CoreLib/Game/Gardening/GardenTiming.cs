/*
 * Imlight
 * Copyright (C) 2026 Revive101
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
 *
 * ========================================================================
 * GARDENING
 * ========================================================================
 *
 * PURPOSE:
 * How fast plants grow (Niduus, 2026-10-25: "Make them obey to configs in manager for their age. and dynamically
 * update"). The Manager's Plant growth card writes garden-growth.json next to the server; the server reads it again
 * within a second of a change, and every garden takes the new speeds at its next growth check (every 10 seconds while
 * someone is in the house), with no restart.
 *
 * USAGE EXAMPLE:
 * var seconds = GardenTiming.Current.Scale(info.NextStageSeconds, GardenClock.Stage, PlantStage.PS_Seedling);
 *
 * NOTE:
 * - Each number is "times faster" than the game's own time: 1 is the game, 60 makes an hour a minute, 0.5 is twice as
 *   slow. Speed is for everything; Seedling, Young and Mature are for the time each of those stages takes to grow to
 *   the next; Harvest is for the wait between harvests; Needs is for needs, pests, wilting and protections. A time is
 *   divided by Speed times its own number.
 * - Paused stops every plant: no growing, no needs, no pests, no wilting, and harvests do not come due.
 * - A garden keeps the speeds its times were set with (House.GardenTiming). When the speeds change, the time each plant
 *   still has left is stretched or shrunk to the new speeds, so a change counts at once, also for plants that were
 *   halfway through a stage.
 * - With no file, [Gardening] Speed in Imlight.ini is the Speed.
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/25/2026
 */

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Gardening;

/// <summary>Which of a plant's clocks a time belongs to.</summary>
internal enum GardenClock { Stage, Harvest, Needs }

/// <summary>
/// The plant growth speeds of the Manager's Plant growth card.
/// </summary>
internal static class GardenTiming {

    internal const string FILE = "garden-growth.json";

    private static readonly JsonSerializerOptions s_json = new() {
        WriteIndented = true, PropertyNameCaseInsensitive = true, AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly object s_lock = new();
    private static GardenSpeeds s_current;
    private static DateTime s_fileTime;
    private static bool s_hadFile;
    private static long s_checkedAt;

    /// <summary>The speeds now, read again from the file when it changed (checked at most once a second).</summary>
    internal static GardenSpeeds Current {
        get {
            var tick = Environment.TickCount64;
            if (s_current is not null && tick - Interlocked.Read(ref s_checkedAt) < 1000) {
                return s_current;
            }

            lock (s_lock) {
                Interlocked.Exchange(ref s_checkedAt, tick);
                try {
                    var exists = File.Exists(FILE);
                    var time = exists ? File.GetLastWriteTimeUtc(FILE) : default;
                    if (s_current is null || exists != s_hadFile || time != s_fileTime) {
                        var loaded = exists ? Read() : null;
                        s_hadFile = exists;
                        s_fileTime = time;
                        var next = (loaded ?? FromIni()).Clean();
                        if (next.ChangedAt <= 0) {
                            next = next with { ChangedAt = new DateTimeOffset(exists ? time : DateTime.UtcNow).ToUnixTimeSeconds() };
                        }

                        if (s_current is not null && !next.SameAs(s_current)) {
                            Logger.Information("Gardening: plant growth is now {0}.", Logger.Args(next.Describe()));
                        }

                        s_current = next;
                    }
                } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                    s_current ??= FromIni().Clean();
                }

                return s_current;
            }
        }
    }

    /// <summary>
    /// Changes the speeds, for the test commands: writes the file the Manager's card reads, so both show the same.
    /// </summary>
    internal static GardenSpeeds Change(Func<GardenSpeeds, GardenSpeeds> change) {
        lock (s_lock) {
            var next = change(Current with { }).Clean() with { ChangedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
            var temp = $"{FILE}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(next, s_json));
            File.Move(temp, FILE, overwrite: true);
            Interlocked.Exchange(ref s_checkedAt, 0);

            return Current;
        }
    }

    private static GardenSpeeds Read() {
        try {
            return JsonSerializer.Deserialize<GardenSpeeds>(File.ReadAllText(FILE), s_json);
        } catch (Exception exception) when (exception is JsonException or IOException or NotSupportedException) {
            Logger.Warning("Gardening: {0} could not be read ({1}); the [Gardening] Speed of Imlight.ini is used.", Logger.Args(FILE, exception.Message));

            return null;
        }
    }

    /// <summary>The speeds of Imlight.ini alone, which gardens made before the Plant growth card were timed with.</summary>
    internal static GardenSpeeds IniDefault => s_iniDefault.Value;

    private static readonly Lazy<GardenSpeeds> s_iniDefault = new(() => FromIni().Clean());

    private static GardenSpeeds FromIni()
        => new() { Speed = ConfigurationManager.GetValue("Gardening.Speed", 1d) };

}

/// <summary>
/// How fast plants grow: each number is times faster than the game. Stored with a house's garden too
/// (House.GardenTiming), as the speeds its times were set with.
/// </summary>
public sealed record GardenSpeeds {

    private const double Min = 0.01;
    private const double Max = 100000;

    public double Speed { get; init; } = 1;
    public double Seedling { get; init; } = 1;
    public double Young { get; init; } = 1;
    public double Mature { get; init; } = 1;
    public double Harvest { get; init; } = 1;
    public double Needs { get; init; } = 1;
    public bool Paused { get; init; }

    /// <summary>When the speeds last changed (unix seconds), so a pause counts from then.</summary>
    public long ChangedAt { get; init; }

    /// <summary>
    /// A game time made shorter (or longer) by these speeds.
    /// </summary>
    internal long Scale(long seconds, GardenClock clock, PlantStage stage) {
        if (seconds <= 0) {
            return 0;
        }

        return Math.Max(1, (long) Math.Round(seconds / Divisor(clock, stage)));
    }

    /// <summary>How many times faster than the game a clock runs.</summary>
    internal double Divisor(GardenClock clock, PlantStage stage) {
        var own = clock switch {
            GardenClock.Harvest => Harvest,
            GardenClock.Needs => Needs,
            _ => stage switch {
                PlantStage.PS_Seedling => Seedling,
                PlantStage.PS_Young => Young,
                PlantStage.PS_Mature => Mature,
                // Wilting to dead is a need left too long.
                _ => Needs,
            },
        };

        return Math.Max(Min, Speed * own);
    }

    internal GardenSpeeds Clean()
        => this with {
            Speed = Fix(Speed), Seedling = Fix(Seedling), Young = Fix(Young), Mature = Fix(Mature),
            Harvest = Fix(Harvest), Needs = Fix(Needs),
        };

    /// <summary>Whether two sets of speeds grow plants the same (the time they changed left out).</summary>
    internal bool SameAs(GardenSpeeds other)
        => other is not null && Speed == other.Speed && Seedling == other.Seedling && Young == other.Young
            && Mature == other.Mature && Harvest == other.Harvest && Needs == other.Needs && Paused == other.Paused;

    internal string Describe()
        => $"{(Paused ? "PAUSED, " : "")}speed x{Speed:0.##} (seedling x{Seedling:0.##}, young x{Young:0.##}, " +
           $"mature x{Mature:0.##}, harvests x{Harvest:0.##}, needs and pests x{Needs:0.##})";

    private static double Fix(double value)
        => double.IsFinite(value) && value > 0 ? Math.Clamp(value, Min, Max) : 1;

}
