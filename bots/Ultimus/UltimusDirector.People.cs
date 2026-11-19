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
using System.Text.RegularExpressions;
using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Combat;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Memory of people (Niduus, 2026-11-16: "he remembers who he's talked to and what they were working on, and picks up where
/// they left off"). Each player who talks to him is kept in players.json with what they were on at the time, read from
/// their own quest book (their quest, its area and world, their level), the last few things they asked him and what they
/// did together (a quest he walked them through, a fight he helped in). When they come back after an hour or more he picks
/// up from there: "Last time you were on X. Still on it?" or "you've finished it since, nice work!". Asked "do you remember
/// me?", he says what he really has written down, nothing made up.
/// </summary>
internal sealed partial class UltimusDirectorActor {

    private const int ASKS_KEPT = 5, TOGETHER_KEPT = 10;

    private static readonly Regex s_rememberMe = new(@"\b(do\s+you\s+)?remember\s+me\b|\bknow\s+who\s+i\s+am\b|\bhave\s+we\s+met\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private void SaveMet() {
        try {
            File.WriteAllText(Path.Combine(UltimusWire.Folder, PLAYERS_FILE), JsonSerializer.Serialize(MetPlayers(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) {
            Logger.Debug("Ultimus: could not write {0}: {1}", Logger.Args(PLAYERS_FILE, e.Message));
        }
    }

    /// <summary>
    /// A player talked to him (already remembered by <see cref="Remember"/>): what they are working on now is written down,
    /// and, when they came back after a while, he picks up where they left off in a line a moment after his answer.
    /// </summary>
    private void NotePerson(UltimusVoice.Heard heard, bool cameBack) {
        if (!MetPlayers().TryGetValue(heard.SpeakerCharId, out var player)) {
            return;
        }

        var before = (Id: player.QuestId ?? "", Title: player.Quest ?? "", Area: player.Area ?? "");
        var wizard = PlayerCheats.LiveWizard(heard.SpeakerCharId);
        var now = BotAsks.QuestOf(wizard, "");
        if (cameBack && before.Id.Length > 0) {
            string line;
            if (now is not null && string.Equals(now.Id, before.Id, StringComparison.OrdinalIgnoreCase)) {
                line = $"Last time you were on \"{before.Title}\". Still on it? Say \"walk me through this quest\" and I'll guide you step by step.";
            }
            else if (wizard?.HasCompletedQuest(before.Id) == true) {
                line = $"Last time you were working on \"{before.Title}\", and you've finished it since. Nice work!" +
                       (now is not null ? $" Now you're on \"{now.Title}\"." : "");
            }
            else {
                line = $"Last time we talked you were on \"{before.Title}\"{(before.Area.Length > 0 ? $" in {before.Area}" : "")}.";
            }

            Note("Ultimus", $"PEOPLE: picks up with {heard.SpeakerName} where they left off: {line}");
            Think($"I remember {heard.SpeakerName}: {line}", "people-back|" + heard.SpeakerCharId);
            SayLater(heard.SpeakerCharId, heard.Whisper, line, 4);
        }

        if (now is not null) {
            player.QuestId = now.Id;
            player.Quest = now.Title;
            player.QuestWorld = now.World;
        }

        if (wizard is not null) {
            player.Area = UltimusText.Zone(wizard.Zone ?? "");
            player.Level = wizard.MagicSchoolBehavior?.Level ?? player.Level;
        }

        player.Asked ??= [];
        player.Asked.Add($"{DateTime.UtcNow:yyyy-MM-dd HH:mm} {UltimusText.Short(BotSocial.Scrub(heard.Text), 80)}");
        if (player.Asked.Count > ASKS_KEPT) {
            player.Asked.RemoveRange(0, player.Asked.Count - ASKS_KEPT);
        }

        SaveMet();
    }

    /// <summary>Something he did with a player (taught a quest, helped in a fight), kept with them.</summary>
    private void NoteTogether(ulong charId, string what) {
        if (!MetPlayers().TryGetValue(charId, out var player)) {
            return;
        }

        player.Together ??= [];
        player.Together.Add($"{DateTime.UtcNow:yyyy-MM-dd} {what}");
        if (player.Together.Count > TOGETHER_KEPT) {
            player.Together.RemoveRange(0, player.Together.Count - TOGETHER_KEPT);
        }

        SaveMet();
    }

    /// <summary>"Do you remember me?": what he has written down about them, in his words.</summary>
    private bool RememberMeAsk(UltimusVoice.Heard heard) {
        if (!s_rememberMe.IsMatch(heard.Text ?? "")) {
            return false;
        }

        if (!MetPlayers().TryGetValue(heard.SpeakerCharId, out var player) || player.Times <= 1) {
            SendReply(heard, "I don't think we've talked before! Nice to meet you.", "people memory");
            return true;
        }

        var parts = new List<string> { $"Of course, {heard.SpeakerName}! We first met on {player.First.ToLocalTime():d MMMM} and have talked {player.Times} times." };
        if (!string.IsNullOrEmpty(player.Quest)) {
            parts.Add($"You were working on \"{player.Quest}\"{(string.IsNullOrEmpty(player.Area) ? "" : $" in {player.Area}")}.");
        }

        if (player.Together is { Count: > 0 } together) {
            parts.Add($"Last thing we did together: {together[^1][11..]}.");
        }

        SendReply(heard, string.Join(" ", parts), "people memory");
        return true;
    }

    /// <summary>A line to a player a few seconds from now: a whisper, or said aloud when they spoke aloud.</summary>
    private void SayLater(ulong charId, bool whisper, string text, double seconds) {
        if (_actors.Count == 0) {
            return;
        }

        var parts = ChatParts(BotSocial.Scrub(text));
        for (var i = 0; i < parts.Count; i++) {
            object message = whisper ? new UltimusMember.WhisperText(parts[i], charId) : new UltimusMember.SayText(parts[i]);
            Context.System.Scheduler.ScheduleTellOnce(TimeSpan.FromSeconds(seconds + (PART_GAP_SECONDS * i)), _actors[0], message, Self);
        }
    }

}
