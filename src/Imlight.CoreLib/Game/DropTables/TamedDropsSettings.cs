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
 */

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Imlight.Common;

namespace Imlight.CoreLib.Game.DropTables;

/// <summary>What a Tamed Drops pity meter pays when it is full, or what a tier adds on every run.</summary>
internal enum TamedPrize {
    /// <summary>Nothing (as a pity prize: the tier has no meter).</summary>
    None,
    /// <summary>One of the boss's drops that is not junk.</summary>
    NotJunk,
    /// <summary>Gear odds multiplied by PrizeGearOdds for that run.</summary>
    DoubleGearOdds,
    /// <summary>A piece of the boss's gear for a school other than the wizard's.</summary>
    OtherSchoolGear,
    /// <summary>A piece of the boss's gear for any school.</summary>
    AnyGear,
    /// <summary>A piece of the boss's gear for the wizard's own school.</summary>
    OwnSchoolGear,
}

/// <summary>One Tamed Drops tier: the battles that earn it, its drop odds and its pity meter.</summary>
internal sealed class TamedTier {
    /// <summary>The tier, 5 (fastest) to 1 (slowest).</summary>
    public int Tier { get; set; }
    /// <summary>The most counted rounds that still earn this tier (tier 1 takes every battle longer than tier 2's).</summary>
    public int MaxRounds { get; set; }
    /// <summary>Runs that fill this tier's pity meter; 0 means the tier has no pity.</summary>
    public double PityRuns { get; set; }
    /// <summary>Multiplies the chance of every junk drop.</summary>
    public double JunkOdds { get; set; } = 1;
    /// <summary>Multiplies the chance of every other drop.</summary>
    public double OtherOdds { get; set; } = 1;
    /// <summary>What a full meter pays.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TamedPrize PityPrize { get; set; }
    /// <summary>What every run of this tier adds, pity or not.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TamedPrize EveryRun { get; set; }
}

/// <summary>
/// Every number and choice of Tamed Drops (Niduus, 2026-10-23: "make this a toggle and customizeable via manager").
/// The Manager's Tamed Drops card writes tamed-drops.json next to the server; the server reads it again whenever the file
/// changes, so a change applies from the next round or boss without a restart. With no file (or a value missing from
/// it) the defaults below are the behavior Tamed Drops first shipped with. The on/off switch stays [TamedDrops] Enabled.
/// </summary>
internal sealed class TamedDropsSettings {

    internal const string FILE = "tamed-drops.json";

    /// <summary>The five tiers, 5 first.</summary>
    public TamedTier[] Tiers { get; set; } = [
        new() { Tier = 5, MaxRounds = 1, PityRuns = 1, JunkOdds = 1, OtherOdds = 1, PityPrize = TamedPrize.NotJunk },
        new() { Tier = 4, MaxRounds = 3, PityRuns = 3, JunkOdds = 0.8, OtherOdds = 1.25, PityPrize = TamedPrize.DoubleGearOdds },
        new() { Tier = 3, MaxRounds = 5, PityRuns = 6, JunkOdds = 0.6, OtherOdds = 1.5, PityPrize = TamedPrize.OtherSchoolGear },
        new() { Tier = 2, MaxRounds = 8, PityRuns = 9, JunkOdds = 0.4, OtherOdds = 1.75, PityPrize = TamedPrize.AnyGear },
        new() { Tier = 1, MaxRounds = int.MaxValue, PityRuns = 12, JunkOdds = 0.25, OtherOdds = 2,
            PityPrize = TamedPrize.OwnSchoolGear, EveryRun = TamedPrize.OwnSchoolGear },
    ];

    /// <summary>How much a run fills the meter of the tier it earned.</summary>
    public double OwnTierFill { get; set; } = 1;
    /// <summary>How much a run fills the meters of the other tiers.</summary>
    public double OtherTierFill { get; set; } = 0.25;
    /// <summary>What the "double gear odds" prize multiplies gear odds by.</summary>
    public double PrizeGearOdds { get; set; } = 2;

    /// <summary>Which drops count as junk.</summary>
    public bool JunkReagents { get; set; } = true;
    public bool JunkTreasureCards { get; set; } = true;
    public bool JunkFurniture { get; set; } = true;
    public bool JunkPetSnacks { get; set; } = true;

    /// <summary>Whether rounds everyone chose to pass stop counting after PassesThatCount in a row.</summary>
    public bool PassesStopCounting { get; set; } = true;
    /// <summary>Rounds in a row everyone chose to pass that still count; the next one and those after it do not.</summary>
    public int PassesThatCount { get; set; } = 2;
    /// <summary>Whether a stunned or confused wizard's pass always counts (it was not a choice).</summary>
    public bool ForcedPassesCount { get; set; } = true;

    /// <summary>Whether every monster's loot follows Tamed Drops, not just bosses'.</summary>
    public bool AllMonsters { get; set; }
    /// <summary>Whether the wizard is told the tier, rounds and pity after a boss.</summary>
    public bool ShowNote { get; set; } = true;

    private static readonly JsonSerializerOptions s_options = new() {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private static readonly object s_lock = new();
    private static TamedDropsSettings s_current = new();
    private static DateTime s_written = DateTime.MinValue;

    /// <summary>The settings as tamed-drops.json has them now (read again when the file changed).</summary>
    internal static TamedDropsSettings Current {
        get {
            try {
                var written = File.Exists(FILE) ? File.GetLastWriteTimeUtc(FILE) : DateTime.MinValue;
                if (written == s_written) {
                    return s_current;
                }

                lock (s_lock) {
                    if (written != s_written) {
                        var read = written == DateTime.MinValue
                            ? new TamedDropsSettings()
                            : JsonSerializer.Deserialize<TamedDropsSettings>(File.ReadAllText(FILE), s_options) ?? new();
                        s_current = read.Checked();
                        s_written = written;
                        Logger.Information("Tamed Drops | Settings {0}: {1}", Logger.Args(
                            written == DateTime.MinValue ? "are the defaults" : $"read from {FILE}", s_current.Describe()));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) {
                // The app may still be writing the file; it is read again the next time it is asked for.
                Logger.Debug("Tamed Drops | Could not read {0} yet: {1}", Logger.Args(FILE, ex.Message));
            }

            return s_current;
        }
    }

    /// <summary>The tier a battle of this many counted rounds earns: the fastest tier whose most rounds it is within.</summary>
    internal int TierFor(int rounds) {
        foreach (var tier in Tiers.Where(t => t.Tier > 1)) {
            if (rounds <= tier.MaxRounds) {
                return tier.Tier;
            }
        }

        return 1;
    }

    internal TamedTier Of(int tier) => Tiers.First(t => t.Tier == tier);

    // Fills in what the file left out or got wrong, so every tier 1-5 is there once and the rounds go up with each tier.
    private TamedDropsSettings Checked() {
        var defaults = new TamedDropsSettings().Tiers;
        Tiers = [.. defaults.Select(d => Tiers?.FirstOrDefault(t => t?.Tier == d.Tier) ?? d)];
        var least = 1;
        foreach (var tier in Tiers) {
            tier.MaxRounds = tier.Tier == 1 ? int.MaxValue : Math.Max(least, tier.MaxRounds);
            least = tier.Tier == 1 ? least : tier.MaxRounds + 1;
            tier.PityRuns = Math.Max(0, tier.PityRuns);
            tier.JunkOdds = Math.Max(0, tier.JunkOdds);
            tier.OtherOdds = Math.Max(0, tier.OtherOdds);
        }

        OwnTierFill = Math.Max(0, OwnTierFill);
        OtherTierFill = Math.Max(0, OtherTierFill);
        PrizeGearOdds = Math.Max(0, PrizeGearOdds);
        PassesThatCount = Math.Max(0, PassesThatCount);

        return this;
    }

    private string Describe()
        => string.Join("; ", Tiers.Select(t => $"T{t.Tier} rounds<={(t.Tier == 1 ? "any" : t.MaxRounds.ToString())} pity {t.PityRuns} "
               + $"({t.PityPrize}) junk x{t.JunkOdds} other x{t.OtherOdds} every run {t.EveryRun}"))
           + $"; fill {OwnTierFill}/{OtherTierFill}; prize gear x{PrizeGearOdds}; junk "
           + $"R{(JunkReagents ? 1 : 0)} TC{(JunkTreasureCards ? 1 : 0)} F{(JunkFurniture ? 1 : 0)} S{(JunkPetSnacks ? 1 : 0)}; "
           + $"passes {(PassesStopCounting ? $"stop after {PassesThatCount}" : "always count")}, forced count {ForcedPassesCount}; "
           + $"all monsters {AllMonsters}; note {ShowNote}";

}
