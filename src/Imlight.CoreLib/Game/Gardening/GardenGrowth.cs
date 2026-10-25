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
 * Grows a house's garden over real time and carries out garden spells and harvests on it. Every change is also
 * written as the MSG_PATCHGARDENING patches that make each client's garden the same.
 *
 * USAGE EXAMPLE:
 * var garden = new GardenGrowth(house.Garden, now);
 * garden.Tick();
 * foreach (var patch in garden.Patches) { ... }
 *
 * NOTE:
 * - The patch commands are the client's (ClientHousingManager, 0x140f286e0): 1 adds soil, 2 removes the plant at a
 *   spot (or the soil when there is no plant), 3 adds a plant of a template, 4 sets a plant's flags, 5 is a harvest
 *   (flags and the next harvest time; an elder plant is removed), 6 takes the pest away, 7 brings a pest (template 0)
 *   or a protection, 8 revives a dead plant, 10 is stasis and 13 is Hasty Harvest.
 * - The client never moves a plant to its next stage by itself, so a new stage is patch 2, then patch 3 with the new
 *   template, then patch 4 with the plant's flags.
 * - The client mirrors some rules on its own copy, and the server does the same so both agree: a wilting plant whose
 *   needs are all met goes back to its young stage (patch 4 and 6), and a revived dead plant becomes its young stage.
 * - A plant with a need showing or a pest does not grow and is not ready to harvest until it is helped. A need left
 *   too long, or a pest, makes the plant wilt; a wilting plant dies when its time is up (Wizard101's own rules).
 * - The Manager's Plant growth card sets how fast every clock runs, and can pause the garden (GardenTiming).
 *
 * TODO:
 * - Good pests (pixies) never come; the client would show them with the pest picture of the plant's size.
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/24/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Gardening;

/// <summary>The client's GardenPlant flag bits.</summary>
internal static class GardenFlags {

    internal const uint Soil = 0x1;
    internal const uint Ready = 0x2;
    internal const uint NeedMask = 0x7C;
    internal const uint Pest = 0x80;
    internal const uint Protected = 0x100;
    internal const uint GoodPest = 0x400;
    internal const uint Stasis = 0x800;
    internal const uint ProviderMask = 0x3E000;

    // What a revive keeps (the client's own mask): soil, 0x200, a good pest, stasis and the high bits.
    internal const uint ReviveKeep = 0xFFFC0E01;

    internal static uint Need(GardenNeed need) => 0x4u << (int) need;

    internal static uint Provided(GardenNeed need) => 0x2000u << (int) need;

}

/// <summary>A patch the clients in the house apply to their garden.</summary>
internal readonly record struct GardenPatch(byte Command, int X, int Y, float Z, uint TemplateId, uint Time);

/// <summary>The commands of MSG_PATCHGARDENING.</summary>
internal static class GardenCommand {

    internal const byte AddSoil = 1;
    internal const byte Remove = 2;
    internal const byte AddPlant = 3;
    internal const byte SetFlags = 4;
    internal const byte Harvested = 5;
    internal const byte PestGone = 6;
    internal const byte PestOrProtection = 7;
    internal const byte Revive = 8;
    internal const byte Stasis = 10;
    internal const byte HarvestNow = 13;

}

/// <summary>Why a garden spell did nothing, as MSG_GARDENINGPELLFIZZLE shows it.</summary>
internal enum GardenFizzle : byte { Fizzle = 0, InsectsTooStrong = 1, NoEnergy = 2, NotEquipped = 3 }

/// <summary>
/// A garden being changed: the soil and plants, now, and the patches the changes make.
/// </summary>
internal sealed class GardenGrowth {

    private const long ProtectionSeconds = 48 * 3600;
    private const long StrongProtectionSeconds = 96 * 3600;
    private const float HeightReach = 180f;

    // The protections the client shows as providing one need instead of keeping pests away (0x141d9c740).
    private static readonly Dictionary<uint, GardenNeed> s_providers = new() {
        [1257000] = GardenNeed.Water,
        [1212014] = GardenNeed.Sun,
        [1257001] = GardenNeed.Pollination,
        [1257002] = GardenNeed.Magic,
        [1257003] = GardenNeed.Music,
    };

    private readonly List<GardenSpot> _spots;
    private readonly long _now;
    private readonly List<GardenPatch> _patches = [];
    private readonly GardenSpeeds _used;

    /// <param name="garden">The soil and plants.</param>
    /// <param name="now">Now, in unix seconds.</param>
    /// <param name="used">The speeds the garden's times were set with; null for the ini's (gardens made before the
    /// Plant growth card).</param>
    internal GardenGrowth(IEnumerable<GardenSpot> garden, long now, GardenSpeeds used = null) {
        _spots = [.. garden ?? []];
        _now = now;
        _used = used ?? GardenTiming.IniDefault;
        Speeds = GardenTiming.Current;
    }

    /// <summary>The speeds the garden grows with now (the Manager's Plant growth card); the house keeps them.</summary>
    internal GardenSpeeds Speeds { get; }

    /// <summary>The soil and plants after the changes.</summary>
    internal List<GardenSpot> Spots => _spots;

    /// <summary>The patches of the changes, in order.</summary>
    internal IReadOnlyList<GardenPatch> Patches => _patches;

    /// <summary>The number of plants (soil left out).</summary>
    internal int PlantCount => _spots.Count(spot => !spot.IsSoil());

    // A game time made shorter or longer by the Plant growth speeds. Needs, pests, wilting and protections use the
    // Needs clock; a stage's own growing time and the wait between harvests have their own.
    private long Scale(long seconds, GardenClock clock = GardenClock.Needs, PlantStage stage = PlantStage.PS_Young)
        => Speeds.Scale(seconds, clock, stage);

    // ---------------------------------------------------------------------------------------------------------------
    // Growing over time
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Grows every plant up to now.
    /// </summary>
    internal void Tick() {
        Retime();
        if (Speeds.Paused) {
            return;
        }

        for (var i = 0; i < _spots.Count; i++) {
            if (_spots[i].IsSoil()) {
                continue;
            }

            try {
                // A long time away can hold several steps, such as young to mature to elder.
                for (var step = 0; step < 8 && i < _spots.Count && !_spots[i].IsSoil(); step++) {
                    if (!Grow(i)) {
                        break;
                    }
                }
            } catch (Exception exception) {
                Logger.Error("Gardening: plant {TemplateId} at ({X}, {Y}) could not grow: {Reason}",
                    Logger.Args(_spots[i].TemplateId, _spots[i].X, _spots[i].Y, exception.Message));
            }
        }
    }

    // One step of a plant; true when it changed in a way that may let another step happen.
    private bool Grow(int index) {
        var spot = _spots[index];
        if (GardenTemplates.Plant(spot.TemplateId) is not { } info) {
            return false;
        }

        var checkedAt = spot.CheckedAt <= 0 ? _now : spot.CheckedAt;
        var flags = spot.Flags;

        // A plant with a need showing or a pest stands still: its timers move on by the time it waited.
        if (IsLiving(info.Stage) && IsHindered(flags) && _now > checkedAt) {
            var waited = _now - checkedAt;
            spot = spot with {
                NextStage = spot.NextStage > 0 ? spot.NextStage + waited : 0,
                HarvestAt = spot.HarvestAt > 0 && (flags & GardenFlags.Ready) == 0 ? spot.HarvestAt + waited : spot.HarvestAt,
            };
        }

        spot = spot with { CheckedAt = _now };

        // A protection that ran out.
        if (spot.ProtectionUntil > 0 && _now >= spot.ProtectionUntil) {
            spot = spot with {
                ProtectionTemplateId = 0,
                ProtectionUntil = 0,
                Flags = spot.Flags & ~(GardenFlags.Protected | GardenFlags.ProviderMask),
            };
        }

        switch (info.Stage) {
            case PlantStage.PS_Wilting:
                _spots[index] = spot;
                if (spot.NextStage > 0 && _now >= spot.NextStage && info.DeadTemplateId != 0) {
                    ChangeStage(index, info.DeadTemplateId, keepFlags: spot.Flags & ~GardenFlags.Ready);

                    return true;
                }

                SendFlagsIfChanged(index, flags);

                return false;
            case PlantStage.PS_Dead:
                _spots[index] = spot;
                SendFlagsIfChanged(index, flags);

                return false;
        }

        // Needs that come due. A protection that provides a need meets it as it comes.
        for (var need = GardenNeed.Water; need <= GardenNeed.Music; need++) {
            var due = NeedAt(spot, need);
            if (due <= 0 || _now < due || (spot.Flags & GardenFlags.Need(need)) != 0) {
                continue;
            }

            var rate = info.Needs[(int) need];
            if ((spot.Flags & GardenFlags.Provided(need)) != 0 || !rate.Used) {
                spot = WithNeedAt(spot, need, rate.Used ? _now + Scale(Between(rate.Min, rate.Max)) : 0);

                continue;
            }

            spot = spot with { Flags = spot.Flags | GardenFlags.Need(need) };
            if (rate.Wilt > 0) {
                spot = spot with { WiltAt = Sooner(spot.WiltAt, due + Scale(rate.Wilt)) };
            }
        }

        // Pests, on young and mature plants (the client shows them on no others).
        if (spot.PestCheckAt > 0 && _now >= spot.PestCheckAt) {
            var interval = info.PestUpdateSeconds > 0 ? info.PestUpdateSeconds : 4 * 3600;
            spot = spot with { PestCheckAt = _now + Scale(interval) };
            if ((spot.Flags & (GardenFlags.Pest | GardenFlags.Protected)) == 0
                && info.Stage is PlantStage.PS_Young or PlantStage.PS_Mature
                && RollPest(info, interval) is { } pest) {
                spot = spot with { Flags = spot.Flags | GardenFlags.Pest, PestTemplateId = pest.TemplateId };
                if (pest.WiltMax > 0) {
                    spot = spot with { WiltAt = Sooner(spot.WiltAt, _now + Scale(Between(pest.WiltMin, pest.WiltMax))) };
                }

                _spots[index] = spot;
                _patches.Add(Patch(GardenCommand.PestOrProtection, spot, 0));
                flags = spot.Flags;
            }
        }

        // Wilting: a need or a pest left too long.
        if (spot.WiltAt > 0 && _now >= spot.WiltAt) {
            if (IsHindered(spot.Flags) && info.WiltTemplateId != 0) {
                _spots[index] = spot;
                ChangeStage(index, info.WiltTemplateId, keepFlags: spot.Flags & ~GardenFlags.Ready);

                return true;
            }

            spot = spot with { WiltAt = 0 };
        }

        // Ready to harvest.
        if (info.Stage is PlantStage.PS_Mature or PlantStage.PS_Angel && (spot.Flags & GardenFlags.Ready) == 0
            && !IsHindered(spot.Flags) && spot.HarvestAt > 0 && _now >= spot.HarvestAt) {
            spot = spot with { Flags = spot.Flags | GardenFlags.Ready };
        }

        _spots[index] = spot;

        // The next stage.
        if (!IsHindered(spot.Flags) && spot.NextStage > 0 && _now >= spot.NextStage) {
            if (info.Stage is PlantStage.PS_Seedling or PlantStage.PS_Young or PlantStage.PS_Mature && info.NextStageTemplateId != 0
                && GardenTemplates.Plant(info.NextStageTemplateId) is not null) {
                ChangeStage(index, info.NextStageTemplateId, keepFlags: spot.Flags & (GardenFlags.Protected | GardenFlags.ProviderMask | GardenFlags.Stasis));

                return true;
            }

            _spots[index] = spot with { NextStage = 0 };
        }

        SendFlagsIfChanged(index, flags);

        return false;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // What the gardener does
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Finds the soil or plant at a spot.</summary>
    internal int IndexAt(int x, int y, bool soil)
        => _spots.FindIndex(spot => spot.X == x && spot.Y == y && spot.IsSoil() == soil);

    /// <summary>Lays soil on a spot.</summary>
    /// <returns><c>false</c> when something is there already.</returns>
    internal bool AddSoil(int x, int y, float z, uint soilTemplateId) {
        if (soilTemplateId == 0 || _spots.Any(spot => spot.X == x && spot.Y == y)) {
            return false;
        }

        var soil = new GardenSpot { X = x, Y = y, Z = z, TemplateId = soilTemplateId, Flags = GardenFlags.Soil };
        _spots.Add(soil);
        _patches.Add(Patch(GardenCommand.AddSoil, soil, soilTemplateId));

        return true;
    }

    /// <summary>Plants a seed on a spot: the plant starts as the seed's next stage, the seedling.</summary>
    /// <returns><c>false</c> when a plant is there already or the seed has no seedling.</returns>
    internal bool Plant(int x, int y, float z, PlantInfo seed) {
        if (seed is null || IndexAt(x, y, soil: false) >= 0 || GardenTemplates.Plant(seed.NextStageTemplateId) is null) {
            return false;
        }

        _spots.Add(new GardenSpot { X = x, Y = y, Z = z, SeedTemplateId = seed.TemplateId });
        StartStage(_spots.Count - 1, seed.NextStageTemplateId, 0);
        _patches.Add(Patch(GardenCommand.AddPlant, _spots[^1], _spots[^1].TemplateId));

        return true;
    }

    /// <summary>Takes away the plant at a spot, or the soil when there is no plant (Plow and the zap spell).</summary>
    internal bool Remove(int x, int y) {
        var index = IndexAt(x, y, soil: false);
        if (index < 0) {
            index = IndexAt(x, y, soil: true);
        }

        if (index < 0) {
            return false;
        }

        var spot = _spots[index];
        _spots.RemoveAt(index);
        _patches.Add(Patch(GardenCommand.Remove, spot, 0));

        return true;
    }

    /// <summary>Meets the needs a growing spell provides for the plants in its reach.</summary>
    /// <returns>The plants it reached.</returns>
    internal int Provide(GardenSpellInfo spell, float x, float y, float z) {
        var reached = 0;
        foreach (var index in InReach(x, y, z, spell.Radius, PlantStage.PS_Seedling, PlantStage.PS_Young, PlantStage.PS_Mature,
                     PlantStage.PS_Angel, PlantStage.PS_Wilting)) {
            reached++;
            var spot = _spots[index];
            var info = GardenTemplates.Plant(spot.TemplateId);
            var flags = spot.Flags;
            for (var need = GardenNeed.Water; need <= GardenNeed.Music; need++) {
                if (!spell.Provides[(int) need] || (flags & GardenFlags.Need(need)) == 0) {
                    continue;
                }

                var rate = info?.Needs[(int) need] ?? default;
                flags &= ~GardenFlags.Need(need);
                spot = WithNeedAt(spot, need, rate.Used ? _now + Scale(Between(rate.Min, rate.Max)) : 0);
            }

            _spots[index] = spot;
            SetFlags(index, flags);
        }

        return reached;
    }

    /// <summary>Takes the pests the spell is strong enough for off the plants in its reach.</summary>
    /// <param name="tooStrong">Whether a pest in reach was too strong for the spell.</param>
    /// <returns>The pests taken away.</returns>
    internal int FightPests(GardenSpellInfo spell, float x, float y, float z, out bool tooStrong) {
        tooStrong = false;
        var removed = 0;
        foreach (var index in InReach(x, y, z, spell.Radius)) {
            var spot = _spots[index];
            if ((spot.Flags & GardenFlags.Pest) == 0) {
                continue;
            }

            var rank = GardenTemplates.Pest(spot.PestTemplateId)?.Rank ?? 1;
            if (spell.BugZapLevel < rank) {
                tooStrong = true;

                continue;
            }

            removed++;
            var pest = spot.PestTemplateId == 0 ? 1u : spot.PestTemplateId;
            spot = spot with { Flags = spot.Flags & ~GardenFlags.Pest, PestTemplateId = 0 };
            if (!IsHindered(spot.Flags)) {
                spot = spot with { WiltAt = 0 };
            }

            _spots[index] = spot;
            _patches.Add(Patch(GardenCommand.PestGone, spot, pest));
            RecoverIfHelped(index);
        }

        return removed;
    }

    /// <summary>Puts a protection on the plants in reach: against pests, or one that provides a need.</summary>
    /// <returns>The plants protected.</returns>
    internal int Protect(GardenSpellInfo spell, float x, float y, float z) {
        if (spell.ProtectionTemplateId == 0) {
            return 0;
        }

        var name = GardenTemplates.Pest(spell.ProtectionTemplateId)?.Name ?? spell.Name;
        var lasts = name.Contains("Putrid", StringComparison.OrdinalIgnoreCase) || name.Contains("Massive", StringComparison.OrdinalIgnoreCase)
            ? StrongProtectionSeconds
            : ProtectionSeconds;
        var protectedCount = 0;
        foreach (var index in InReach(x, y, z, spell.Radius, PlantStage.PS_Seedling, PlantStage.PS_Young, PlantStage.PS_Mature, PlantStage.PS_Angel)) {
            protectedCount++;
            var spot = _spots[index];
            var flags = spot.Flags & ~(GardenFlags.Protected | GardenFlags.ProviderMask);
            if (s_providers.TryGetValue(spell.ProtectionTemplateId, out var need)) {
                // A provider meets its need for as long as it lasts.
                flags = (flags | GardenFlags.Provided(need)) & ~GardenFlags.Need(need);
            }
            else {
                flags |= GardenFlags.Protected;
            }

            _spots[index] = spot with { ProtectionTemplateId = spell.ProtectionTemplateId, ProtectionUntil = _now + Scale(lasts) };
            _patches.Add(Patch(GardenCommand.PestOrProtection, _spots[index], spell.ProtectionTemplateId));
            SetFlags(index, flags, force: true);
        }

        return protectedCount;
    }

    /// <summary>Brings wilting and dead plants in reach back to their young stage.</summary>
    /// <returns>The plants revived.</returns>
    internal int Revive(GardenSpellInfo spell, float x, float y, float z) {
        var revived = 0;
        foreach (var index in InReach(x, y, z, spell.Radius, PlantStage.PS_Wilting, PlantStage.PS_Dead).ToList()) {
            var spot = _spots[index];
            var info = GardenTemplates.Plant(spot.TemplateId);
            if (info?.YoungTemplateId is not > 0 || GardenTemplates.Plant(info.YoungTemplateId) is null) {
                continue;
            }

            revived++;
            var kept = spot.Flags & GardenFlags.ReviveKeep & ~GardenFlags.GoodPest;
            if (info.Stage == PlantStage.PS_Dead) {
                // The client turns a dead plant into its young stage itself.
                _patches.Add(Patch(GardenCommand.Revive, spot, 0));
                _spots[index] = spot with { PestTemplateId = 0 };
                StartStage(index, info.YoungTemplateId, kept);
            }
            else {
                _spots[index] = spot with { PestTemplateId = 0 };
                ChangeStage(index, info.YoungTemplateId, kept);
            }
        }

        return revived;
    }

    /// <summary>Puts the plant at a spot in stasis, so an elder plant grows back after its harvest (Second Spring).</summary>
    internal bool Stasis(int x, int y) {
        var index = IndexAt(x, y, soil: false);
        if (index < 0 || (_spots[index].Flags & GardenFlags.Stasis) != 0) {
            return false;
        }

        _spots[index] = _spots[index] with { Flags = _spots[index].Flags | GardenFlags.Stasis };
        _patches.Add(Patch(GardenCommand.Stasis, _spots[index], 0));

        return true;
    }

    /// <summary>Hasty Harvest: a mature plant becomes elder and ready to harvest at once.</summary>
    internal bool HarvestNow(int x, int y) {
        var index = IndexAt(x, y, soil: false);
        if (index < 0 || GardenTemplates.Plant(_spots[index].TemplateId) is not { Stage: PlantStage.PS_Mature } info
            || GardenTemplates.Plant(info.NextStageTemplateId) is null) {
            return false;
        }

        _patches.Add(Patch(GardenCommand.HarvestNow, _spots[index], 0));
        StartStage(index, info.NextStageTemplateId, (_spots[index].Flags & ~GardenFlags.Ready) | GardenFlags.Ready);
        _spots[index] = _spots[index] with { HarvestAt = _now };

        return true;
    }

    /// <summary>
    /// Harvests the plant at a spot.
    /// </summary>
    /// <param name="harvested">The stage that was harvested.</param>
    /// <param name="seedTemplateId">The seed the plant grew from.</param>
    /// <returns><c>false</c> when there is no plant ready to harvest there.</returns>
    internal bool Harvest(int x, int y, out PlantInfo harvested, out uint seedTemplateId) {
        harvested = null;
        seedTemplateId = 0;
        var index = IndexAt(x, y, soil: false);
        if (index < 0) {
            return false;
        }

        var spot = _spots[index];
        var info = GardenTemplates.Plant(spot.TemplateId);
        if (info is not { Stage: PlantStage.PS_Mature or PlantStage.PS_Angel } || (spot.Flags & GardenFlags.Ready) == 0
            || IsHindered(spot.Flags)) {
            return false;
        }

        harvested = info;
        seedTemplateId = spot.SeedTemplateId;
        var count = spot.HarvestCount + 1;
        var flags = spot.Flags & ~GardenFlags.Ready;
        if (info.Stage == PlantStage.PS_Angel) {
            // Second Spring: an elder plant in stasis grows back to mature instead of being used up.
            var matureId = info.MatureTemplateId != 0 ? info.MatureTemplateId : MatureBefore(spot);
            if ((spot.Flags & GardenFlags.Stasis) != 0 && GardenTemplates.Plant(matureId) is not null) {
                _spots[index] = spot with { HarvestCount = 0 };
                ChangeStage(index, matureId, flags & ~GardenFlags.Stasis);

                return true;
            }

            _spots.RemoveAt(index);
            _patches.Add(Patch(GardenCommand.Harvested, spot, flags, 0));

            return true;
        }

        var next = _now + Scale(Between(info.HarvestMin, info.HarvestMax), GardenClock.Harvest);
        spot = spot with { Flags = flags, HarvestCount = count, HarvestAt = next };
        _spots[index] = spot;
        _patches.Add(Patch(GardenCommand.Harvested, spot, flags, (uint) Math.Min(next, uint.MaxValue)));

        // A mature plant harvested as often as it can be becomes elder.
        if (info.MaximumHarvests > 0 && count >= info.MaximumHarvests && GardenTemplates.Plant(info.NextStageTemplateId) is not null) {
            ChangeStage(index, info.NextStageTemplateId, flags & (GardenFlags.Protected | GardenFlags.ProviderMask | GardenFlags.Stasis));
        }

        return true;
    }

    /// <summary>
    /// For testing: moves every plant to its next stage, meets its needs, takes its pests away and makes it ready.
    /// </summary>
    /// <returns>The plants changed.</returns>
    internal int Hurry(bool readyOnly) {
        var changed = 0;
        for (var i = 0; i < _spots.Count; i++) {
            if (_spots[i].IsSoil() || GardenTemplates.Plant(_spots[i].TemplateId) is not { } info) {
                continue;
            }

            changed++;
            if ((_spots[i].Flags & GardenFlags.Pest) != 0) {
                _patches.Add(Patch(GardenCommand.PestGone, _spots[i], _spots[i].PestTemplateId == 0 ? 1u : _spots[i].PestTemplateId));
                _spots[i] = _spots[i] with { Flags = _spots[i].Flags & ~GardenFlags.Pest, PestTemplateId = 0 };
            }

            var flags = _spots[i].Flags & ~GardenFlags.NeedMask;
            _spots[i] = _spots[i] with { WiltAt = 0 };
            if (info.Stage is PlantStage.PS_Wilting or PlantStage.PS_Dead) {
                if (GardenTemplates.Plant(info.YoungTemplateId) is not null) {
                    ChangeStage(i, info.YoungTemplateId, flags & ~GardenFlags.Ready);
                }

                continue;
            }

            if (!readyOnly && info.Stage is PlantStage.PS_Seedling or PlantStage.PS_Young or PlantStage.PS_Mature
                && GardenTemplates.Plant(info.NextStageTemplateId) is not null) {
                ChangeStage(i, info.NextStageTemplateId, flags & ~GardenFlags.Ready);
                info = GardenTemplates.Plant(_spots[i].TemplateId);
                flags = _spots[i].Flags;
            }

            if (info.Stage is PlantStage.PS_Mature or PlantStage.PS_Angel) {
                flags |= GardenFlags.Ready;
                _spots[i] = _spots[i] with { HarvestAt = _now };
            }

            SetFlags(i, flags);
        }

        return changed;
    }

    /// <summary>
    /// For testing: lets this many seconds of plant time pass for every plant, then grows them.
    /// </summary>
    /// <returns>The plants.</returns>
    internal int Age(long seconds) {
        if (seconds > 0) {
            for (var i = 0; i < _spots.Count; i++) {
                if (!_spots[i].IsSoil()) {
                    _spots[i] = Shift(_spots[i], -seconds, shiftChecked: false);
                }
            }
        }

        Retime();
        for (var i = 0; i < _spots.Count; i++) {
            if (_spots[i].IsSoil()) {
                continue;
            }

            for (var step = 0; step < 8 && i < _spots.Count && !_spots[i].IsSoil(); step++) {
                if (!Grow(i)) {
                    break;
                }
            }
        }

        return PlantCount;
    }

    /// <summary>
    /// For testing: puts every plant in a stage (seedling, young, mature, elder, wilting or dead) with fresh times.
    /// </summary>
    /// <returns>The plants changed.</returns>
    internal int SetStage(PlantStage stage) {
        var changed = 0;
        for (var i = 0; i < _spots.Count; i++) {
            if (_spots[i].IsSoil() || GardenTemplates.Plant(_spots[i].TemplateId) is not { } info
                || StageTemplate(_spots[i], info, stage) is not { } templateId) {
                continue;
            }

            changed++;
            if (templateId == _spots[i].TemplateId) {
                continue;
            }

            var flags = _spots[i].Flags & (GardenFlags.Protected | GardenFlags.ProviderMask);
            if (stage is PlantStage.PS_Angel) {
                flags |= GardenFlags.Ready;
            }

            ChangeStage(i, templateId, flags);
        }

        return changed;
    }

    // The template of a plant's stage: the seed's chain walked from the seedling.
    private static uint? StageTemplate(GardenSpot spot, PlantInfo info, PlantStage stage) {
        switch (stage) {
            case PlantStage.PS_Wilting:
                return info.WiltTemplateId != 0 ? info.WiltTemplateId : null;
            case PlantStage.PS_Dead:
                return info.DeadTemplateId != 0 ? info.DeadTemplateId : null;
        }

        var seed = GardenTemplates.Plant(spot.SeedTemplateId);
        var id = seed?.NextStageTemplateId ?? 0;
        if (id == 0 && info.Stage is PlantStage.PS_Wilting or PlantStage.PS_Dead) {
            id = info.YoungTemplateId;
        }
        else if (id == 0) {
            id = spot.TemplateId;
        }

        for (var step = 0; step < 6 && GardenTemplates.Plant(id) is { } at; step++) {
            if (at.Stage == stage) {
                return id;
            }

            if (at.NextStageTemplateId == 0 || at.Stage is PlantStage.PS_Angel or PlantStage.PS_Wilting or PlantStage.PS_Dead) {
                break;
            }

            id = at.NextStageTemplateId;
        }

        return null;
    }

    /// <summary>
    /// Fits the garden's times to the speeds of now: the time each plant still has left is stretched or shrunk by the
    /// change, and the time the garden was paused is added to every clock, so nothing grew then.
    /// </summary>
    private void Retime() {
        var same = _used.SameAs(Speeds);
        if (same && !Speeds.Paused) {
            return;
        }

        var changedAt = Math.Clamp(Speeds.ChangedAt, 0, _now);
        for (var i = 0; i < _spots.Count; i++) {
            var spot = _spots[i];
            if (spot.IsSoil()) {
                continue;
            }

            var checkedAt = spot.CheckedAt <= 0 ? _now : spot.CheckedAt;
            // The stretch of time the plant stood paused since it was last checked.
            long frozen = (_used.Paused, Speeds.Paused) switch {
                (true, true) => _now - checkedAt,
                (true, false) => Math.Max(0, (same ? _now : changedAt) - checkedAt),
                (false, true) => _now - Math.Max(checkedAt, changedAt),
                _ => 0,
            };
            if (frozen > 0) {
                spot = Shift(spot, frozen, shiftChecked: true);
            }

            if (!same && GardenTemplates.Plant(spot.TemplateId) is { } info) {
                double Ratio(GardenClock clock) => _used.Divisor(clock, info.Stage) / Speeds.Divisor(clock, info.Stage);
                var stage = Ratio(GardenClock.Stage);
                var harvest = Ratio(GardenClock.Harvest);
                var needs = Ratio(GardenClock.Needs);
                spot = spot with {
                    NextStage = Stretch(spot.NextStage, stage),
                    HarvestAt = (spot.Flags & GardenFlags.Ready) == 0 ? Stretch(spot.HarvestAt, harvest) : spot.HarvestAt,
                    WaterAt = Stretch(spot.WaterAt, needs),
                    SunAt = Stretch(spot.SunAt, needs),
                    PollinationAt = Stretch(spot.PollinationAt, needs),
                    MagicAt = Stretch(spot.MagicAt, needs),
                    MusicAt = Stretch(spot.MusicAt, needs),
                    WiltAt = Stretch(spot.WiltAt, needs),
                    PestCheckAt = Stretch(spot.PestCheckAt, needs),
                    ProtectionUntil = Stretch(spot.ProtectionUntil, needs),
                };
            }

            if (Speeds.Paused) {
                spot = spot with { CheckedAt = _now };
            }

            _spots[i] = spot;
        }
    }

    // A time still to come, its wait made ratio times as long.
    private long Stretch(long at, double ratio)
        => at <= _now || ratio == 1 ? at : _now + Math.Max(1, (long) Math.Round((at - _now) * ratio));

    // Every clock of a plant moved by seconds (a pause adds time; Age takes it away).
    private static GardenSpot Shift(GardenSpot spot, long seconds, bool shiftChecked) {
        static long By(long at, long seconds) => at > 0 ? Math.Max(1, at + seconds) : at;

        return spot with {
            NextStage = By(spot.NextStage, seconds),
            HarvestAt = (spot.Flags & GardenFlags.Ready) == 0 ? By(spot.HarvestAt, seconds) : spot.HarvestAt,
            WaterAt = By(spot.WaterAt, seconds),
            SunAt = By(spot.SunAt, seconds),
            PollinationAt = By(spot.PollinationAt, seconds),
            MagicAt = By(spot.MagicAt, seconds),
            MusicAt = By(spot.MusicAt, seconds),
            WiltAt = By(spot.WiltAt, seconds),
            PestCheckAt = By(spot.PestCheckAt, seconds),
            ProtectionUntil = By(spot.ProtectionUntil, seconds),
            StageStart = By(spot.StageStart, seconds),
            CheckedAt = shiftChecked ? By(spot.CheckedAt, seconds) : spot.CheckedAt,
        };
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------------

    // A new stage: the client removes the plant and adds it again with the new template, then takes its flags.
    private void ChangeStage(int index, uint templateId, uint keepFlags) {
        var before = _spots[index];
        _patches.Add(Patch(GardenCommand.Remove, before, 0));
        StartStage(index, templateId, keepFlags);
        var after = _spots[index];
        _patches.Add(Patch(GardenCommand.AddPlant, after, templateId));
        if (after.PestTemplateId != 0 && (after.Flags & GardenFlags.Pest) != 0) {
            _patches.Add(Patch(GardenCommand.PestOrProtection, after, 0));
        }

        if (after.ProtectionTemplateId != 0) {
            _patches.Add(Patch(GardenCommand.PestOrProtection, after, after.ProtectionTemplateId));
        }

        if (after.Flags != 0) {
            _patches.Add(Patch(GardenCommand.SetFlags, after, after.Flags));
        }
    }

    // Starts a stage's timers. A dead plant keeps no times; a wilting one only its time to die.
    private void StartStage(int index, uint templateId, uint flags) {
        var spot = _spots[index];
        var info = GardenTemplates.Plant(templateId);
        var next = info is null || info.NextStageSeconds <= 0 || info.Stage == PlantStage.PS_Angel ? 0 : _now + Scale(info.NextStageSeconds, GardenClock.Stage, info.Stage);
        var living = info is not null && IsLiving(info.Stage);
        var pestInterval = info?.PestUpdateSeconds > 0 ? info.PestUpdateSeconds : 4 * 3600;
        spot = spot with {
            TemplateId = templateId,
            Flags = flags,
            StageStart = _now,
            NextStage = next,
            CheckedAt = _now,
            WiltAt = 0,
            HarvestAt = info?.Stage switch {
                PlantStage.PS_Mature => _now + Scale(Between(info.HarvestMin, info.HarvestMax), GardenClock.Harvest),
                // An elder plant is ready for its last harvest at once.
                PlantStage.PS_Angel => _now,
                _ => 0,
            },
            PestCheckAt = living && info.Pests.Count > 0 ? _now + Scale(pestInterval) : 0,
            PestTemplateId = (flags & GardenFlags.Pest) != 0 ? spot.PestTemplateId : 0,
        };
        for (var need = GardenNeed.Water; need <= GardenNeed.Music; need++) {
            var rate = living ? info.Needs[(int) need] : default;
            spot = WithNeedAt(spot, need, rate.Used ? _now + Scale(Between(rate.Min, rate.Max)) : 0);
        }

        _spots[index] = spot;
    }

    // Sets a plant's flags and tells the clients, mirroring the client: a wilting plant with every need met goes back
    // to its young stage.
    private void SetFlags(int index, uint flags, bool force = false) {
        var spot = _spots[index];
        if (!force && spot.Flags == flags) {
            return;
        }

        _spots[index] = spot with { Flags = flags };
        _patches.Add(Patch(GardenCommand.SetFlags, _spots[index], flags));
        RecoverIfHelped(index);
    }

    private void SendFlagsIfChanged(int index, uint before) {
        if (_spots[index].Flags != before) {
            _patches.Add(Patch(GardenCommand.SetFlags, _spots[index], _spots[index].Flags));
            RecoverIfHelped(index);
        }
    }

    private void RecoverIfHelped(int index) {
        var spot = _spots[index];
        if (GardenTemplates.Plant(spot.TemplateId) is not { Stage: PlantStage.PS_Wilting } info) {
            return;
        }

        var hindering = (spot.Flags & GardenFlags.GoodPest) != 0 ? GardenFlags.NeedMask : GardenFlags.NeedMask | GardenFlags.Pest;
        if ((spot.Flags & hindering) != 0 || GardenTemplates.Plant(info.YoungTemplateId) is null) {
            return;
        }

        // The client switched the template already; only the server's times start over.
        StartStage(index, info.YoungTemplateId, spot.Flags);
    }

    private IEnumerable<int> InReach(float x, float y, float z, int radius, params PlantStage[] stages) {
        var reach = Math.Max(radius, 1);
        for (var i = 0; i < _spots.Count; i++) {
            var spot = _spots[i];
            if (spot.IsSoil()) {
                continue;
            }

            if (stages.Length > 0 && (GardenTemplates.Plant(spot.TemplateId) is not { } info || !stages.Contains(info.Stage))) {
                continue;
            }

            var dx = spot.X - x;
            var dy = spot.Y - y;
            if (dx * dx + dy * dy <= (double) reach * reach && MathF.Abs(spot.Z - z) < HeightReach) {
                yield return i;
            }
        }
    }

    private static PestInfo RollPest(PlantInfo info, long intervalSeconds) {
        var hours = intervalSeconds / 3600.0;
        foreach (var chance in info.Pests) {
            if (GardenTemplates.Pest(chance.TemplateId) is not { Good: false } pest) {
                continue;
            }

            if (Random.Shared.NextDouble() * 100.0 < chance.PercentPerHour * hours) {
                return pest;
            }
        }

        return null;
    }

    private uint MatureBefore(GardenSpot spot)
        => GardenTemplates.Plant(spot.SeedTemplateId) is { } seed && GardenTemplates.Plant(seed.NextStageTemplateId) is { } seedling
            && GardenTemplates.Plant(seedling.NextStageTemplateId) is { } young
            ? young.NextStageTemplateId
            : 0;

    private static bool IsLiving(PlantStage stage)
        => stage is PlantStage.PS_Seedling or PlantStage.PS_Young or PlantStage.PS_Mature or PlantStage.PS_Angel;

    private static bool IsHindered(uint flags)
        => (flags & GardenFlags.NeedMask) != 0 || ((flags & GardenFlags.Pest) != 0 && (flags & GardenFlags.GoodPest) == 0);

    private static long Between(long min, long max)
        => max <= min ? min : min + (long) (Random.Shared.NextDouble() * (max - min));

    private static long Sooner(long current, long candidate)
        => current <= 0 ? candidate : Math.Min(current, candidate);

    private static long NeedAt(GardenSpot spot, GardenNeed need) => need switch {
        GardenNeed.Water => spot.WaterAt,
        GardenNeed.Sun => spot.SunAt,
        GardenNeed.Pollination => spot.PollinationAt,
        GardenNeed.Magic => spot.MagicAt,
        _ => spot.MusicAt,
    };

    private static GardenSpot WithNeedAt(GardenSpot spot, GardenNeed need, long at) => need switch {
        GardenNeed.Water => spot with { WaterAt = at },
        GardenNeed.Sun => spot with { SunAt = at },
        GardenNeed.Pollination => spot with { PollinationAt = at },
        GardenNeed.Magic => spot with { MagicAt = at },
        _ => spot with { MusicAt = at },
    };

    private static GardenPatch Patch(byte command, GardenSpot spot, uint templateId, uint time = 0)
        => new(command, spot.X, spot.Y, spot.Z, templateId, time);

}
