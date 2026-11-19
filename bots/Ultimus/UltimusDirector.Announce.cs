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
using System.Linq;
using Akka.Actor;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Things Ultimus says on his own (Niduus, 2026-11-14, approved all five): when he leaves for a while, when he is stuck and
/// asks the way, his big moments (a world done, a level, a tough foe beaten), party mode hellos and goodbyes, and before a
/// hard fight. Only when someone can hear: a real player in his area (said aloud), or his group mate elsewhere (whispered).
/// Each kind at most once in 10 minutes, and nothing within 90 seconds of his last such line, so he never spams.
/// The private-details block applies as to everything he says.
/// </summary>
internal sealed partial class UltimusDirectorActor {

    internal const string SAY_TRIP = "trip", SAY_STUCK = "stuck", SAY_MOMENT = "moment", SAY_PARTY = "party", SAY_TOUGH = "tough";

    private readonly Dictionary<string, DateTime> _announcedAt = [];
    private DateTime _lastAnnounce = DateTime.MinValue;

    private void Announce(string kind, string text, bool urgent = false) {
        if (string.IsNullOrWhiteSpace(text) || _team.Count == 0 || _actors.Count == 0) {
            return;
        }

        var now = DateTime.UtcNow;
        if (!urgent && (now - _lastAnnounce < TimeSpan.FromSeconds(90)
                        || _announcedAt.TryGetValue(kind, out var at) && now - at < TimeSpan.FromMinutes(10))) {
            return;
        }

        var me = _team[0];
        var line = BotShared.BotSocial.Scrub(text);
        if (string.IsNullOrWhiteSpace(line)) {
            return;
        }

        if (PlayerNearby(me)) {
            _actors[0].Tell(new UltimusMember.SayText(line));
        }
        else if (_partyMate != 0) {
            _actors[0].Tell(new UltimusMember.WhisperText(line, _partyMate));
        }
        else {
            return;
        }

        _announcedAt[kind] = now;
        _lastAnnounce = now;
        Note("Ultimus", $"says on his own ({kind}): \"{line}\"");
    }

    // A real player (not a server wizard) in his area right now.
    private static bool PlayerNearby(UltimusMember.Status me) {
        try {
            return OnlinePlayerCollection.GetCachedOnlinePlayers().Any(p => p.CharacterId != me.CharId && !UltimusRoster.IsMember(p.CharacterId)
                && !QuestTeam.QuestTeamRoster.IsMember(p.CharacterId) && !string.IsNullOrEmpty(p.CurrentZone)
                && OverlayQuestTracker.SameZone(p.CurrentZone, me.Zone ?? ""));
        }
        catch (InvalidOperationException) {
            return false;
        }
    }

}
