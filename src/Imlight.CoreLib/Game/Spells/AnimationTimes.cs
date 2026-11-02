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
 * ANIMATION TIMES
 * ========================================================================
 *
 * PURPOSE:
 * The app's Game Rules > Animations tab (Niduus, 2026-11-02: "Implement in the manager a Animations tab so players can
 * see the total animation seconds for config customization in great detail of each spell").
 *
 * The game never tells the server it has played a spell, so each round waits the server's own estimate of its
 * animations (CombatResolver.GetActionCinematicTime, SpellCinematics.GetMeasuredSpellTime). This file:
 * - Writes spell-animations.json beside ids.csv for the tab: every spell with the animation (cinematic) it plays, and
 *   every animation with its stages from the game files, the seconds the round waits for it and how they add up.
 * - Reads spiraldb-extra/Customs/AnimationTimes.json, the tab's own times, and watches it, so a change counts from the
 *   next round with no restart. Fields:
 *     Spells   animation name (a spell's m_spellBase, which spells that look the same share) to the seconds a round
 *              waits for it, in place of the estimate.
 *     General  the waits that are not one spell's animation: Casting (a spell's casting animation when its animation
 *              has no Casting stage), Pass, Defeat, Announcement, Charm (each charm, ward or trap used up) and OverTime
 *              (each damage or heal over time going off).
 *     Names    what the app shows for each entry; not used here.
 * The game files are never changed.
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 11/02/2026
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData;

namespace Imlight.CoreLib.Game.Spells;

internal static class AnimationTimes {

    // The General waits the tab can change, with what each is called there.
    internal const string CASTING = "Casting";
    internal const string PASS = "Pass";
    internal const string DEFEAT = "Defeat";
    internal const string ANNOUNCEMENT = "Announcement";
    internal const string CHARM = "Charm";
    internal const string OVER_TIME = "OverTime";

    // The most a round may be told to wait for one thing (the app's boxes stop there too).
    internal const float MAX_SECONDS = 60f;

    internal sealed class Times {
        public Dictionary<string, float> Spells { get; set; } = [];
        public Dictionary<string, float> General { get; set; } = [];
        public Dictionary<string, string> Names { get; set; } = [];
    }

    private sealed record State(Dictionary<string, float> Spells, Dictionary<string, float> General);

    private const string FOLDER = "Customs";
    internal const string FILE = "AnimationTimes.json";
    private const string REPORT_FILE = "spell-animations.json";
    // How long the file must stay still before it is read again (the app writes it in one go, an editor in several steps).
    private const int RELOAD_DELAY_IN_MS = 400;

    private static readonly JsonSerializerOptions s_options = new() {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly State s_empty = new(new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase));
    private static readonly object s_lock = new();
    private static State s_state;
    private static FileSystemWatcher s_watcher;
    private static Timer s_reloadTimer;

    private static State Current {
        get {
            var state = s_state;
            if (state is not null) {
                return state;
            }

            lock (s_lock) {
                if (s_state is null) {
                    s_state = Read();
                    Watch();
                }

                return s_state;
            }
        }
    }

    /// <summary>The seconds the tab set for an animation, if it set any.</summary>
    internal static bool TrySpell(string animation, out float seconds) {
        seconds = 0;
        if (string.IsNullOrEmpty(animation)) {
            return false;
        }

        var spells = Current.Spells;

        return spells.Count > 0 && spells.TryGetValue(animation, out seconds);
    }

    /// <summary>One of the General waits: the tab's, or the server's own.</summary>
    internal static float General(string name, float normal) {
        var general = Current.General;

        return general.Count > 0 && general.TryGetValue(name, out var seconds) ? seconds : normal;
    }

    private static void Watch() {
        try {
            s_reloadTimer = new Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);
            var dir = Path.Combine(SpiralDB.ExtraPath, FOLDER);
            Directory.CreateDirectory(dir);
            s_watcher = new FileSystemWatcher(dir, FILE) {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            s_watcher.Changed += (_, _) => ReloadSoon();
            s_watcher.Created += (_, _) => ReloadSoon();
            s_watcher.Deleted += (_, _) => ReloadSoon();
            s_watcher.Renamed += (_, _) => ReloadSoon();
            s_watcher.EnableRaisingEvents = true;
        }
        catch (Exception e) {
            Logger.Warning("Animation times: changes to {0} will need a server restart ({1}).", Logger.Args(FILE, e.Message));
        }
    }

    private static void ReloadSoon() => s_reloadTimer?.Change(RELOAD_DELAY_IN_MS, Timeout.Infinite);

    // A timer thread runs it, where an exception would end the server.
    private static void Reload() {
        try {
            var state = Read();
            lock (s_lock) {
                s_state = state;
            }

            Logger.Information("Animation times: {0} spell animation and {1} general times from the app are in use.",
                Logger.Args(state.Spells.Count, state.General.Count));
        }
        catch (Exception e) {
            Logger.Warning("Animation times: {0} could not be read again: {1}", Logger.Args(FILE, e.Message));
        }
    }

    private static State Read() {
        var path = Path.Combine(SpiralDB.ExtraPath, FOLDER, FILE);
        if (!File.Exists(path)) {
            return s_empty;
        }

        try {
            var times = JsonSerializer.Deserialize<Times>(File.ReadAllText(path), s_options) ?? new Times();

            return new State(Clean(times.Spells), Clean(times.General));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
            Logger.Warning("Animation times: {0} could not be read, so the server's own times are used: {1}", Logger.Args(FILE, e.Message));

            return s_empty;
        }

        static Dictionary<string, float> Clean(Dictionary<string, float> read) {
            var clean = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, seconds) in read ?? []) {
                if (!string.IsNullOrWhiteSpace(name) && float.IsFinite(seconds)) {
                    clean[name.Trim()] = Math.Clamp(seconds, 0f, MAX_SECONDS);
                }
            }

            return clean;
        }
    }

    // ---------------------------------------------------------------------
    // The tab's list (spell-animations.json)
    // ---------------------------------------------------------------------

    internal sealed record Stage(string Name, string Kind, float Seconds);
    internal sealed record Part(string What, float Seconds);
    internal sealed record Animation(string Name, float Fast, float Old, List<Part> Parts, List<Part> OldParts, List<Stage> Stages, bool Found);
    internal sealed record SpellRow(uint Id, string Name, string Shown, string School, int Pips, bool XPips, bool Treasure, string Animation);
    internal sealed record Normal(string Name, float Fast, float Old, string What);
    internal sealed record Report(string Written, bool FastTiming, float ExtraSeconds, List<Normal> General, List<Animation> Animations, List<SpellRow> Spells);

    private static readonly JsonSerializerOptions s_write = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Works the list out and writes it (IdCatalog runs it after the server's start).</summary>
    internal static void WriteReport(string folder, IReadOnlyList<Normal> general, float castSeconds) {
        _ = SpellCinematics.Instance;
        _ = CoreObjectFactory.Instance;
        _ = Locale.Instance;
        // Each spell's template id, by its file (as CombatResolver's side lookalikes find them).
        var ids = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (var location in CoreObjectFactory.TemplateManifest?.m_serializedTemplates ?? []) {
            string file = location?.m_filename;
            if (!string.IsNullOrEmpty(file) && location.m_id <= uint.MaxValue) {
                ids.TryAdd(file, (uint) location.m_id);
            }
        }

        var spells = new List<SpellRow>();
        var animations = new Dictionary<string, Animation>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, template) in SpellFactory.AllSpellTemplates()) {
            try {
                string name = template?.m_name;
                if (string.IsNullOrEmpty(name)) {
                    continue;
                }

                string animation = template.m_spellBase;
                animation ??= "";
                var id = file is not null && ids.TryGetValue(file, out var found) ? found : SpellFactory.TemplateIdOf(template);
                spells.Add(new SpellRow(id, name, Shown(template.m_displayName), (string) template.m_sMagicSchoolName ?? "",
                    template.m_spellRank?.m_spellRank ?? 0, template.m_spellRank?.m_xPipSpell == true, template.m_Treasure, animation));
                if (animation.Length > 0 && !animations.ContainsKey(animation)) {
                    animations[animation] = Describe(animation, castSeconds);
                }
            }
            catch (Exception e) {
                Logger.Debug("Animation times: skipped a spell: {0}", Logger.Args(e.Message));
            }
        }

        var report = new Report(DateTime.Now.ToString("yyyy-MM-dd HH:mm"), Imlight.CoreLib.Game.Combat.CombatResolver.FastAnimationTiming,
            Math.Clamp(ConfigurationManager.GetValue("Battle.AnimationExtraSeconds", 1f), 0f, 10f), [.. general],
            [.. animations.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)],
            [.. spells.OrderBy(s => s.Shown.Length > 0 ? s.Shown : s.Name, StringComparer.OrdinalIgnoreCase)]);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, REPORT_FILE);
        File.WriteAllText(path + ".saving", JsonSerializer.Serialize(report, s_write));
        File.Move(path + ".saving", path, overwrite: true);
        Logger.Information("Animation times: wrote {0} spells and {1} animations for the app's Animations tab.",
            Logger.Args(spells.Count, animations.Count));
    }

    private static Animation Describe(string name, float castSeconds) {
        var template = SpellCinematics.GetCinematicTemplate(name);
        var stages = new List<Stage>();
        foreach (var stage in template?.m_stages ?? []) {
            if (stage is null) {
                continue;
            }

            var kind = stage.GetType().Name;
            kind = kind.EndsWith("CinematicStageTemplate", StringComparison.Ordinal) ? kind[..^"CinematicStageTemplate".Length] : kind;
            stages.Add(new Stage((string) stage.m_name ?? "", kind.Length > 0 ? kind : "Stage", stage.m_duration));
        }

        var parts = SpellCinematics.MeasuredParts(name, castSeconds);
        var oldParts = SpellCinematics.OldParts(name, castSeconds);

        return new Animation(name, Round(parts.Sum(p => p.Seconds)), Round(oldParts.Sum(p => p.Seconds)),
            [.. parts.Select(p => p with { Seconds = Round(p.Seconds) })], [.. oldParts.Select(p => p with { Seconds = Round(p.Seconds) })],
            [.. stages.Select(s => s with { Seconds = Round(s.Seconds) })], template is not null);
    }

    private static float Round(float seconds) => MathF.Round(seconds, 2);

    private static string Shown(string key) {
        if (string.IsNullOrEmpty(key)) {
            return "";
        }

        try {
            var text = Locale.GetEnglishName(key);

            return string.IsNullOrWhiteSpace(text) ? "" : text.Trim();
        }
        catch (Exception) {
            return "";
        }
    }

}
