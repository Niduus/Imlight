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
 *
 * ========================================================================
 * SPELL CINEMATICS MANAGEMENT SYSTEM
 * ========================================================================
 * 
 * PURPOSE:
 * Provides centralized loading and retrieval of spell cinematic templates
 * with methods to extract timing information for different spell stages
 * as they are defined in the Root.wad
 * 
 * USAGE EXAMPLE:
 * var summonTime = SpellCinematics.GetSpellSummonTime("Fire Cat");
 * var totalTime = SpellCinematics.GetSpellTotalTime("Thunder Snake");
 * 
 * NOTE:
 * 
 * TODO:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 3/18/2025
 */

using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Spells;

/// <summary>
/// Manages the loading, caching, and timing calculations for spell cinematic templates.
/// </summary>
internal class SpellCinematics : RootDirectoryResourceSingleton<SpellCinematics>, IMemoryStreamDisposable {

    private const float HANGING_EFFECT_ADD_TIME = 1.0f;

    protected override string DirectoryName => "Cinematics";

    private static readonly Dictionary<string, CinematicTemplate> s_cinematicTemplates = [];

    protected override void AfterLoad() {
        var serializer = new BindSerializer();
        var counter = 0;

        foreach (var file in base.Files) {
            var fileRecord = file.Key;
            var fileStream = file.Value;

            if (!serializer.Deserialize<CinematicTemplate>(fileStream?.ToArray(), 1, out var cinematicTemplate)) {
                Logger.Error("Could not deserialize {0} as {1}", 
                    Logger.Args(fileRecord.FileName, nameof(CinematicTemplate)));

                continue;
            }

            // SKip if the template is already in the dictionary.
            if (s_cinematicTemplates.ContainsKey(cinematicTemplate.m_name)) {
                continue;
            }

            s_cinematicTemplates.Add(cinematicTemplate.m_name, cinematicTemplate);
            counter++;
        }

        Logger.Information("Loaded {0} cinematic templates.", 
            Logger.Args(counter));
    }

    /// <summary>
    /// How many spell animations were loaded from the game files.
    /// </summary>
    internal static int Count => s_cinematicTemplates.Count;

    /// <summary>
    /// Retrieves a cinematic template based on its name.
    /// </summary>
    /// <param name="name">The name of the spell.</param>
    /// <returns>The <see cref="CinematicTemplate"/> of the spell. </returns>
    internal static CinematicTemplate GetCinematicTemplate(string name) {
        if (s_cinematicTemplates.TryGetValue(name, out var cinematicTemplate)) {
            return cinematicTemplate;
        }

        return null;
    }

    /// <summary>
    /// Retrieves the summon time of a spell based on its name.
    /// </summary>
    /// <param name="name">The name of the spell.</param>
    /// <returns>The summon time of the spell.</returns>
    internal static float GetSpellSummonTime(string name) {
        var cinematicTemplate = GetCinematicTemplate(name);
        if (cinematicTemplate is null) {
            return 0.0f;
        }

        // Search the acts of the template to find type `SummonCinematicStageTemplate`.
        foreach (var act in cinematicTemplate.m_stages) {
            if (act is SummonCinematicStageTemplate summonCinematicStageTemplate) {
                return summonCinematicStageTemplate.m_duration;
            }

            // A boss's cheat cinematic (Polymorph - Dragon NA: 4.8 s of Malistaire turning into the Black Dragon) has
            // a plain stage named Summon; the round waits for it too, so it is not cut off before the dragon goes away.
            if (act.m_name == "Summon") {
                return act.m_duration;
            }
        }

        return 0.0f;
    }

    /// <summary>
    /// Retrieves the duration of a spell's cinematic act.
    /// </summary>
    /// <param name="name">The name of the spell.</param>
    /// <returns>The duration of the spell's cinematic act.</returns>
    internal static float GetSpellActTime(string name) {
        var cinematicTemplate = GetCinematicTemplate(name);
        if (cinematicTemplate is null) {
            return 0.0f;
        }

        // Search the acts of the template to find type `ActCinematicStageTemplate`.
        // We don't need to check for a certain act because all of them have the same duration anyways.
        foreach (var act in cinematicTemplate.m_stages) {
            if (act is ActCinematicStageTemplate actCinematicStageTemplate) {
                return actCinematicStageTemplate.m_duration;
            }
            else if (act.m_name == "Act") {
                return act.m_duration;
            }
        }

        return 0.0f;
    }

    /// <summary>
    /// Retrieves the casting time of a spell based on its name.
    /// </summary>
    /// <param name="name">The name of the spell.</param>
    /// <returns>The casting time of the spell.</returns>
    internal static float GetSpellCastingTime(string name) {
        var cinematicTemplate = GetCinematicTemplate(name);
        if (cinematicTemplate is null) {
            return 0.0f;
        }

        // Search the acts of the template to find type `CastingCinematicStageTemplate`.
        foreach (var act in cinematicTemplate.m_stages) {
            if (act.m_name == "Casting") {
                return act.m_duration;
            }
        }

        return 0.0f;
    }

    /// <summary>
    /// Calculates the total time of a spell's cinematic based on its name.
    /// </summary>
    /// <param name="name">The name of the spell.</param>
    /// <returns>The total time of the spell's cinematic.</returns>
    internal static float GetSpellTotalTime(string name) {
        var cinematicTemplate = GetCinematicTemplate(name);
        if (cinematicTemplate is null) {
            return 0.0f;
        }

        float totalTime = 0.0f;
        foreach (var act in cinematicTemplate.m_stages) {
            // Add 1 second to the total time if the act is a hanging effect.
            if (act.m_name.ToString().Contains("AddHanging")) {
                totalTime += HANGING_EFFECT_ADD_TIME;
                continue;
            }

            totalTime += act.m_duration;
        }

        return totalTime;
    }

    // Cinematics the game does not play past the caster's casting animation, measured in Niduus's game log
    // (2026-10-22): Malistaire's Mega Taunt took 3.8 s every time (7 rounds), while its stages add up to 14.5 s,
    // which kept the first round of his battle waiting about 16 s after everything had played.
    private static readonly HashSet<string> s_castOnly = ["Malistaire Taunt"];

    // Cinematics whose stages do not say how long the game plays them, with the time measured in Niduus's game log
    // (2026-10-22) plus a little. Shadow Self Tank (Ahk'To - Shadow Self Tank) played in 7.3 s, while the casting
    // animation the estimate adds (it has no Casting stage) and its Act came to 11.9 s. Meteor Strike played in 10.9 to
    // 11.9 s (5 rounds), longer than its stages (10.7 s), so the turn after it could start early.
    private static readonly Dictionary<string, float> s_measuredSeconds = new() {
        ["Shadow Self Tank"] = 8.0f,
        ["Meteor Strike"] = 12.0f,
    };

    /// <summary>
    /// How long a spell's animation plays: the casting animation, the summon, and the act (both acts of a two-act
    /// cinematic such as Minotaur or Empower, which the game plays one after the other and the old estimate cut short;
    /// one of the variants when there are three or more, such as Scarab). With no act stage, every stage plays in turn.
    /// </summary>
    /// <param name="name">The spell's base name (its cinematic).</param>
    /// <param name="castSeconds">The casting animation, for a cinematic with no Casting stage of its own.</param>
    internal static float GetMeasuredSpellTime(string name, float castSeconds) {
        var time = 0f;
        foreach (var part in MeasuredParts(name, castSeconds)) {
            time += part.Seconds;
        }

        return time;
    }

    /// <summary>
    /// What <see cref="GetMeasuredSpellTime"/> adds up, part by part (the app's Animations tab shows each part).
    /// </summary>
    internal static List<AnimationTimes.Part> MeasuredParts(string name, float castSeconds) {
        var template = string.IsNullOrEmpty(name) ? null : GetCinematicTemplate(name);
        if (template?.m_stages is not { Count: > 0 } stages) {
            return [new("Casting animation (the game files have no animation of this name)", castSeconds)];
        }

        if (s_castOnly.Contains(name)) {
            return [new("Casting animation only (measured in the game: it plays no more than that)", castSeconds)];
        }

        if (s_measuredSeconds.TryGetValue(name, out var measured)) {
            return [new("Measured in the game (its stages do not say how long it plays)", measured)];
        }

        var parts = new List<AnimationTimes.Part>();

        // The stage GetSpellSummonTime reads.
        var summon = stages.FirstOrDefault(s => s is SummonCinematicStageTemplate)
            ?? stages.FirstOrDefault(s => s is not null && s.m_name == "Summon");
        var casting = stages.FirstOrDefault(s => s is not null && s.m_name == "Casting");
        parts.Add(casting is { m_duration: > 0.1f }
            ? new("Casting stage", casting.m_duration)
            : new("Casting animation (no Casting stage, so the usual one)", castSeconds));
        if (summon is { m_duration: > 0f }) {
            parts.Add(new($"Summon stage ({(string) summon.m_name})", summon.m_duration));
        }

        var acts = stages.Where(s => s is ActCinematicStageTemplate && s.m_duration > 0.1f).ToList();
        if (acts.Count == 0 && stages.FirstOrDefault(s => s is not null && s.m_name == "Act") is { m_duration: > 0.1f } act) {
            acts.Add(act);
        }

        if (acts.Count == 2) {
            // Both acts of a two-act cinematic such as Minotaur or Empower: the game plays one after the other.
            parts.Add(new($"Act stage ({(string) acts[0].m_name}), first of two", acts[0].m_duration));
            parts.Add(new($"Act stage ({(string) acts[1].m_name}), second of two", acts[1].m_duration));
        }
        else if (acts.Count > 0) {
            // Three or more are variants (Scarab): one of them plays.
            var longest = acts.OrderByDescending(a => a.m_duration).First();
            parts.Add(new(acts.Count == 1 ? $"Act stage ({(string) longest.m_name})"
                : $"Act stage ({(string) longest.m_name}), the longest of {acts.Count} variants, of which one plays", longest.m_duration));
        }
        else {
            // The other stages, each once (the old sum counted the summon and casting a second time: Ghoul waited 19.7 s
            // for 15.6 s of animation).
            foreach (var stage in stages) {
                if (stage is null || ReferenceEquals(stage, summon) || ReferenceEquals(stage, casting)) {
                    continue;
                }

                if (stage.m_name.ToString().Contains("AddHanging")) {
                    parts.Add(new($"{(string) stage.m_name} (a charm or ward going on, counted as 1 s)", HANGING_EFFECT_ADD_TIME));
                    continue;
                }

                parts.Add(new($"{(string) stage.m_name} stage", stage.m_duration));
            }
        }

        return parts;
    }

    /// <summary>
    /// The old, longer estimate ([Battle] FastAnimationTiming off), part by part: the summon, the casting animation, and
    /// the act, or with no act every stage again.
    /// </summary>
    internal static List<AnimationTimes.Part> OldParts(string name, float castSeconds) {
        var parts = new List<AnimationTimes.Part>();
        var summon = GetSpellSummonTime(name);
        if (summon > 0f) {
            parts.Add(new("Summon stage", summon));
        }

        var castTime = GetSpellCastingTime(name);
        parts.Add(castTime > 0.1f ? new("Casting stage", castTime) : new("Casting animation (the usual one)", castSeconds));
        var actTime = GetSpellActTime(name);
        parts.Add(actTime > 0.1f ? new("Act stage", actTime) : new("Every stage again (no Act stage)", GetSpellTotalTime(name)));

        return parts;
    }

    /// <summary>The old, longer estimate ([Battle] FastAnimationTiming off).</summary>
    internal static float GetOldSpellTime(string name, float castSeconds) {
        var time = 0f;
        foreach (var part in OldParts(name, castSeconds)) {
            time += part.Seconds;
        }

        return time;
    }

    public void DisposeStream()
        => s_cinematicTemplates.Clear();

}
