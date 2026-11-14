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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Imlight.Common;
using Imlight.CoreLib.Game.Quests;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// The read-ahead before each world (Niduus, 2026-11-14, "read-ahead check before each world"): when he starts a world's
/// story, the server's quest checker (the one on the Quest Testing tab, which reads the game's own zone files) is read for
/// that world's story quests, and every story step it says the server can't do yet is filed on the Ultimus Issues page
/// before he gets there, marked READ-AHEAD. He still plays every step as a player would; nothing is skipped. A check older
/// than a day, or none at all, is run again in the background first.
/// </summary>
internal sealed partial class UltimusDirectorActor {

    private readonly HashSet<string> _readAheadDone = new(StringComparer.OrdinalIgnoreCase);
    private string _readAheadWaiting;
    private DateTime _readAheadNextLook = DateTime.MinValue;
    private DateTime _readAheadAskedAt = DateTime.MinValue;

    private static string QuestCheckPath => Path.Combine(IdCatalog.OutputPath, QuestChecker.FILE_NAME);

    /// <summary>A world's story starts: its story quests are read ahead (now, or once the check is written).</summary>
    private void ReadAhead(string world) {
        if (string.IsNullOrEmpty(world) || _readAheadDone.Contains(world)) {
            return;
        }

        _readAheadWaiting = world;
        _readAheadNextLook = DateTime.MinValue;
        ReadAheadTick();
    }

    /// <summary>Each tick while a read-ahead waits for the quest check to be written.</summary>
    private void ReadAheadTick() {
        if (_readAheadWaiting is not { } world || DateTime.UtcNow < _readAheadNextLook) {
            return;
        }

        _readAheadNextLook = DateTime.UtcNow.AddMinutes(1);
        var fresh = File.Exists(QuestCheckPath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(QuestCheckPath) < TimeSpan.FromDays(1);
        if (!fresh) {
            // Asked once; given up after two hours (the check takes minutes, but a server with no zone files never writes it).
            if (_readAheadAskedAt == DateTime.MinValue) {
                _readAheadAskedAt = DateTime.UtcNow;
                if (QuestChecker.Start(IdCatalog.OutputPath)) {
                    Note("Ultimus", $"reads ahead in {WorldName(world)}: the quest check is being run in the background first.");
                }
            }
            else if (DateTime.UtcNow - _readAheadAskedAt > TimeSpan.FromHours(2)) {
                Note("Ultimus", $"could not read ahead in {WorldName(world)}: the quest check was not written. He plays on as usual.");
                _readAheadDone.Add(world);
                _readAheadWaiting = null;
                _readAheadAskedAt = DateTime.MinValue;
            }

            return;
        }

        _readAheadWaiting = null;
        _readAheadAskedAt = DateTime.MinValue;
        _readAheadDone.Add(world);
        var found = ReadAheadProblems(world);
        if (found is null) {
            return;
        }

        var leader = _team.FirstOrDefault()?.Wizard;
        var open = found.Where(f => leader is null || !leader.HasCompletedQuest(f.Quest)).ToList();
        if (open.Count == 0) {
            Note("Ultimus", $"READ-AHEAD: {WorldName(world)}: the quest check finds nothing wrong with its story quests.");
            Think($"Before {WorldName(world)} I read ahead: the quest check finds every story step there doable.", "readahead|" + world);
            return;
        }

        Note("Ultimus", $"READ-AHEAD: {WorldName(world)}: {open.Count} story quest(s) have a step the server may not be able to do yet; filed on the Issues page.",
            UltimusLink.NOTABLE);
        Think($"Before {WorldName(world)} I read ahead: {open.Count} story quest(s) there have a step the quest check says may not work. " +
              "I wrote them down for Niduus now, and I still try each one as a player would when I get there.", "readahead|" + world);
        // Filed straight to the Issues page: no live diagnosis (he is not at that quest yet, so it would read his current spot).
        foreach (var (quest, problems) in open.Take(40)) {
            if (!_reported.TryAdd("readahead|" + quest, DateTime.UtcNow)) {
                continue;
            }

            Count("issues raised");
            _readAheadFound[quest] = string.Join(" ", problems.Take(2));
            var text = $"READ-AHEAD ({WorldName(world)}): {string.Join(" ", problems.Take(3))} Found by the quest check before Ultimus got there; " +
                       "he still tries it as a player would when he does.";
            UltimusLink.Issue("quest", quest, UltimusText.Title(quest), world, text, "Every story step can be done on this server.", null, "",
                [$"Quest code: {quest}", $"Quest data file: data/spiraldb/QuestTemplates/questtemplates_{quest}.json", .. problems.Select(p => "Quest check: " + p)]);
        }
    }

    // The world's story quests (and quests the story needs) the check says have a problem, story order kept.
    private static List<(string Quest, List<string> Problems)> ReadAheadProblems(string world) {
        try {
            using var doc = JsonDocument.Parse(File.ReadAllText(QuestCheckPath));
            if (!doc.RootElement.TryGetProperty("Quests", out var quests) || quests.ValueKind != JsonValueKind.Array) {
                return null;
            }

            var found = new List<(string, List<string>)>();
            foreach (var q in quests.EnumerateArray()) {
                if (q.TryGetProperty("Ok", out var ok) && ok.ValueKind == JsonValueKind.True) {
                    continue;
                }

                var code = q.TryGetProperty("Code", out var c) ? c.GetString() : null;
                if (string.IsNullOrEmpty(code) || QuestTemplateCollection.GetQuestByName(code) is not { } template
                    || !string.Equals(QuestStory.WorldOf(template), world, StringComparison.OrdinalIgnoreCase)
                    || BotShared.BotStory.Rank(template) <= 0 || !UltimusPlanner.IsMainStoryWorld(template)) {
                    continue;
                }

                var problems = q.TryGetProperty("Problems", out var p) && p.ValueKind == JsonValueKind.Array
                    ? p.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrEmpty(x)).ToList()
                    : [];
                if (problems.Count > 0) {
                    found.Add((code, problems));
                }
            }

            return found;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            Logger.Debug("Ultimus: the quest check could not be read for the read-ahead: {0}", Logger.Args(ex.Message));
            return null;
        }
    }

}
