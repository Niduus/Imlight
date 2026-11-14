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
using System.Globalization;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Battlegrounds;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;
using BG = Imlight.CoreLib.Game.Battlegrounds.Battlegrounds;

namespace Imlight.CoreLib.Game.Zone.Core;

/// <summary>
/// The part of a zone a Beastmoon arena needs (Game/Battlegrounds): its control points, taken by standing in them
/// (ControlPoints.cs), and the match.
/// </summary>
/// <remarks>
/// The match, from its BattlegroundTemplate (the one the first wizard in went in for, Battlegrounds.MatchFor):
/// - a countdown of m_countdownSeconds (90) when the first wizard arrives. Everyone in it has BG-PlanningPhase, which the
///   base teleporters refuse (WizardActorDialogue_00000837), so no one leaves the base early. `.bg start` skips it.
/// - then m_matchTimeSeconds (2700 PvP, 660 PvE). The PvE scripts add to it (ResBGAdjustTime "Add" "12m" when a wave's
///   boss falls, "2m" for a time clam). When it runs out the zone posts EVENT_Battleground_EndBattle, which the
///   arenas end on, and the match ends.
/// - team points (ResUnknown4591AF80, 100 for a PvE arena's last boss), shown with MSG_BATTLEGROUNDUPDATEPOINTS.
/// - a PvE wave ends with ResUnknown5ABB361D (m_teleportTo Red_Start, m_zoneEvent Wave_End_Reset): everyone goes back
///   to the base and the next wave's event is posted. The raids use the same step, without its fields, for their win.
/// - a PvE match is won when the last boss falls (EVENT_PVE_Complete), and lost when the time runs out. The end goes to
///   the game as MSG_BATTLEGROUNDEND, and [Beastmoon] ReturnSeconds later everyone goes to WC_Duel_Arena (From_BG).
/// The game's numbers, seen in game (2026-11-14): MSG_BATTLEGROUNDUPDATEPOINTS Team 0 is the red team (its points show
/// by the Λ crest of the end panel), and MSG_BATTLEGROUNDEND EndCode names the team that won, the same for everyone:
/// 0 shows "Λ YOU WIN" to a red wizard, 1 "MONSTERS WIN" in a PvE match (the monsters are team B, Ω). Every player's
/// game works out for itself whether it won. 2 for a tie is a guess.
/// </remarks>
public partial class Zone {

    private const string BATTLEGROUND_TICK_TIMER = "battleground-tick";
    private const string BATTLEGROUND_COUNTDOWN_TIMER = "battleground-countdown";
    private const string BATTLEGROUND_MATCH_TIMER = "battleground-match";
    private const string BATTLEGROUND_HOME_TIMER = "battleground-home";

    private enum MatchPhase : byte { None, Countdown, Running, Ended }

    private enum MatchEnd : byte { Won = 1, Lost = 2, Tie = 3 }

    // MSG_BATTLEGROUNDEND EndCode: the team that won (see the remarks).
    private const byte END_TEAM_A = 0;
    private const byte END_TEAM_B = 1;
    private const byte END_TIE = 2;

    private ControlPoints _controlPoints;
    private string _matchName;
    private BattlegroundTemplate _match;
    private MatchPhase _matchPhase;
    private DateTime _matchPhaseEnds;
    // When the match's clock runs out. The game's match window counts the template's m_matchTimeSeconds from when it is
    // built (the wizard's zone load), so the countdown is part of the match time; the server keeps the same end.
    private DateTime _clockEnds;
    private readonly int[] _teamPoints = new int[3];
    private readonly Dictionary<IActorRef, ulong> _battlegroundCharIds = [];
    private (string Event, DateTime At) _lastWaveEnd;

    private bool IsBattlegroundArena => _controlPoints is not null || _match is not null;

    private void SetUpBattleground(ZONE_102_PROTOCOL.MSG_ZONELOADRESULTS message) {
        if (!BG.Enabled || !BG.IsBattlegroundZone(ZonePath)) {
            return;
        }

        _controlPoints = ControlPoints.Discover(ZonePath, message.TriggerData, message.VolumeData, message.ZoneData);
        if (_controlPoints is null) {
            return;
        }

        Timers.StartPeriodicTimer(BATTLEGROUND_TICK_TIMER,
            new ZONE_102_PROTOCOL.MSG_BATTLEGROUNDCONTROL { Kind = BattlegroundControlKind.Tick },
            TimeSpan.FromMilliseconds(ControlPoints.TickMilliseconds));
    }

    /// <summary>A wizard arrived: the first one starts the match's countdown, and anyone during it is in its planning phase.</summary>
    private void NoteBattlegroundPlayerJoined(IActorRef player, Wizard wizard) {
        if (_controlPoints is null || player is null || wizard is null) {
            return;
        }

        _battlegroundCharIds[player] = wizard.CharId;
        // The battleground map's icons, once the wizard's game has built its map.
        if (_controlPoints.PoiMessage() is { } poi) {
            Context.System.Scheduler.ScheduleTellOnce(TimeSpan.FromSeconds(5), player, poi, Self);
        }
        if (_match is null) {
            (_matchName, _match) = BG.MatchFor(ZonePath, BG.Of(wizard.CharId)?.MatchName);
            if (_match is null) {
                Logger.Warning("Beastmoon | {0}: no Beastmoon match is set in this arena; its points work but there is no match.",
                    Logger.Args(ZonePath));

                return;
            }

            _controlPoints.PvE = _match.m_pveMode;
            StartCountdown();
        }
        else if (_matchPhase is MatchPhase.Countdown or MatchPhase.Running) {
            CorrectClock(player);
        }

        if (_matchPhase == MatchPhase.Countdown) {
            BG.SetEffect(wizard.CharId, BG.PLANNING_EFFECT, true);
            player.Tell(ServerLine($"{_matchName}: the match starts in {SecondsLeft()} seconds. Choose your form; the base's way out opens then."));
        }

        if (_teamPoints.Any(p => p != 0)) {
            foreach (var team in new[] { BG.Team.Red, BG.Team.Blue }) {
                player.Tell(PointsMessage(team, 0));
            }
        }
    }

    private void StartCountdown() {
        var seconds = BG.CountdownSeconds >= 0 ? BG.CountdownSeconds : (int) Math.Round(_match.m_countdownSeconds);
        Logger.Information("Beastmoon | {0}: {1} ({2}) begins: {3} seconds of countdown, then {4} seconds.",
            Logger.Args(ZonePath, _matchName, _match.m_pveMode ? "PvE" : "PvP", seconds, _match.m_matchTimeSeconds));
        _clockEnds = DateTime.UtcNow.AddSeconds(_match.m_matchTimeSeconds);
        if (seconds <= 0) {
            BeginMatch();

            return;
        }

        _matchPhase = MatchPhase.Countdown;
        _matchPhaseEnds = DateTime.UtcNow.AddSeconds(seconds);
        Timers.StartSingleTimer(BATTLEGROUND_COUNTDOWN_TIMER,
            new ZONE_102_PROTOCOL.MSG_BATTLEGROUNDCONTROL { Kind = BattlegroundControlKind.CountdownEnd }, TimeSpan.FromSeconds(seconds));
    }

    private void BeginMatch() {
        if (_match is null || _matchPhase is MatchPhase.Running or MatchPhase.Ended) {
            return;
        }

        Timers.Cancel(BATTLEGROUND_COUNTDOWN_TIMER);
        foreach (var charId in _battlegroundCharIds.Values) {
            BG.SetEffect(charId, BG.PLANNING_EFFECT, false);
        }

        _matchPhase = MatchPhase.Running;
        SetMatchTime((int) Math.Ceiling((_clockEnds - DateTime.UtcNow).TotalSeconds));
        TellArena($"The match has begun! {FormatTime(SecondsLeft())} on the clock.");
        Logger.Information("Beastmoon | {0}: the match has begun.", Logger.Args(ZonePath));
    }

    private void SetMatchTime(int seconds) {
        seconds = Math.Max(0, seconds);
        _matchPhaseEnds = DateTime.UtcNow.AddSeconds(seconds);
        _clockEnds = _matchPhaseEnds;
        Timers.StartSingleTimer(BATTLEGROUND_MATCH_TIMER,
            new ZONE_102_PROTOCOL.MSG_BATTLEGROUNDCONTROL { Kind = BattlegroundControlKind.TimeUp }, TimeSpan.FromSeconds(seconds));
    }

    /// <summary>
    /// The game's clock (MSG_BattlegroundTimeAdjust, read from WizardGraphicalClient: ClientBattlegroundManager and
    /// BattlegroundWindow). The match window is built when the wizard's zone loads, if the last queue message
    /// (MSG_BATTLEGROUNDQUEUEUPDATE, Battlegrounds.QueueMessage) named a battleground event; it ends its clock
    /// m_matchTimeSeconds after that and shows it once m_countdownSeconds are over. DeltaS adds seconds (before the
    /// clock shows, to its start too) and the game tells the wizard; 0x7FFFFFFF stops the clock, 0x80000001 starts it again.
    /// </summary>
    private static WIZARD2_53_PROTOCOL.MSG_BattlegroundTimeAdjust ClockMessage(int deltaSeconds) => new() { DeltaS = deltaSeconds };

    private const int CLOCK_STOP = int.MaxValue;

    // A wizard who arrived after the match began: their window's clock started at their arrival, so it is moved to the
    // match's end, once their window is up.
    private void CorrectClock(IActorRef player) {
        var delta = (int) Math.Round((_clockEnds - DateTime.UtcNow.AddSeconds(_match.m_matchTimeSeconds)).TotalSeconds);
        if (Math.Abs(delta) >= 2) {
            Context.System.Scheduler.ScheduleTellOnce(TimeSpan.FromSeconds(5), player, ClockMessage(delta), Self);
        }
    }

    private int SecondsLeft() => Math.Max(0, (int) Math.Ceiling((_matchPhaseEnds - DateTime.UtcNow).TotalSeconds));

    /// <summary>The script posted an event (EVENT_PVE_Complete ends a PvE match).</summary>
    private void NoteBattlegroundEvent(ZONE_102_PROTOCOL.MSG_POSTEVENT message) {
        if (_controlPoints is null || string.IsNullOrEmpty(message.EventName)) {
            return;
        }

        if (string.Equals(message.EventName, BG.PVE_COMPLETE_EVENT, StringComparison.OrdinalIgnoreCase)) {
            EndMatch(MatchEnd.Won, "the last boss has fallen");

            return;
        }

    }

    /// <summary>A wizard moved (MSG_PLAYERMOVE): the control points see who stands in them.</summary>
    private void NoteBattlegroundPlayerMoved(ZONE_102_PROTOCOL.MSG_PLAYERMOVE message)
        => _controlPoints?.OnPlayerMoved(message.PlayerActor, message.PlayerObject);

    private void NoteBattlegroundPlayerLeft(IActorRef player) {
        _controlPoints?.OnPlayerLeft(player);
        _battlegroundCharIds.Remove(player);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_BATTLEGROUNDCONTROL))]
    private void ReceiveBattlegroundControl(ZONE_102_PROTOCOL.MSG_BATTLEGROUNDCONTROL message) {
        switch (message.Kind) {
            case BattlegroundControlKind.Tick:
                TickControlPoints();
                break;
            case BattlegroundControlKind.Status:
                Sender.Tell(new ZONE_102_PROTOCOL.MSG_BATTLEGROUNDCONTROL {
                    Kind = BattlegroundControlKind.Status,
                    Text = BattlegroundStatus(),
                });
                break;
            case BattlegroundControlKind.Reset:
                if (_controlPoints is not null) {
                    foreach (var point in _controlPoints.Points) {
                        point.Owner = BG.Team.None;
                        point.Capturing = BG.Team.None;
                        point.Contested = false;
                        point.Progress = 0;
                    }
                }
                break;
            case BattlegroundControlKind.Start:
            case BattlegroundControlKind.CountdownEnd:
                if (_matchPhase == MatchPhase.Countdown) {
                    BeginMatch();
                }
                break;
            case BattlegroundControlKind.TimeUp:
                if (_matchPhase == MatchPhase.Running) {
                    Logger.Information("Beastmoon | {0}: the time is up.", Logger.Args(ZonePath));
                    Self.Tell(new ZONE_102_PROTOCOL.MSG_POSTEVENT { EventName = BG.END_BATTLE_EVENT });
                    EndMatch(TimeUpResult(), "the time is up");
                }
                break;
            case BattlegroundControlKind.AdjustTime:
                AdjustMatchTime(message.Action, message.Seconds);
                break;
            case BattlegroundControlKind.Points:
                AddPoints(message);
                break;
            case BattlegroundControlKind.WaveEnd:
                EndWave(message.Location, message.EventName);
                break;
            case BattlegroundControlKind.End:
                // Only the arenas' end-of-time triggers end a match with it; a PvE win is EVENT_PVE_Complete.
                EndMatch(TimeUpResult(), "the arena ended it");
                break;
            case BattlegroundControlKind.SendHome:
                SendBattlegroundHome();
                break;
        }
    }

    // How a match the clock ended went: a PvE match was not finished; a PvP match goes to the team with more points.
    private MatchEnd TimeUpResult()
        => _match?.m_pveMode != false ? MatchEnd.Lost
            : _teamPoints[(int) BG.Team.Red] == _teamPoints[(int) BG.Team.Blue] ? MatchEnd.Tie
            : MatchEnd.Won;

    private void AdjustMatchTime(string action, int seconds) {
        if (_matchPhase != MatchPhase.Running) {
            Logger.Information("Beastmoon | {0}: a time change ({1} {2}s) came outside the match; it is not used.",
                Logger.Args(ZonePath, action, seconds));

            return;
        }

        var left = SecondsLeft();
        var now = (action ?? "").Trim().ToLowerInvariant() switch {
            "add" => left + seconds,
            "remove" or "subtract" => left - seconds,
            _ => seconds,
        };
        SetMatchTime(now);
        Logger.Information("Beastmoon | {0}: the clock goes from {1}s to {2}s ({3} {4}s).",
            Logger.Args(ZonePath, left, Math.Max(0, now), action, seconds));
        var clock = ClockMessage(Math.Max(0, now) - left);
        foreach (var player in _players) {
            player.Tell(clock);
        }

        TellArena(now > left ? $"+{FormatTime(now - left)}! {FormatTime(now)} left." : $"{FormatTime(Math.Max(0, now))} left.");
    }

    private void AddPoints(ZONE_102_PROTOCOL.MSG_BATTLEGROUNDCONTROL message) {
        var team = (BG.Team) message.Team;
        if (team == BG.Team.None) {
            team = BG.Of(message.Player)?.Team ?? BG.Team.None;
        }

        if (team == BG.Team.None) {
            team = BG.Team.Red;
        }

        _teamPoints[(int) team] += message.Points;
        var points = PointsMessage(team, message.Points, message.PointEvent);
        foreach (var player in _players) {
            player.Tell(points);
        }

        Logger.Debug("Beastmoon | {0}: {1} points for the {2} team ({3} in all, of {4}).",
            Logger.Args(ZonePath, message.Points, team, _teamPoints[(int) team], _match?.m_maximumDisplayPoints ?? 0));
    }

    // Red is 0 and blue 1 here: a guess (see the remarks).
    private WIZARD2_53_PROTOCOL.MSG_BATTLEGROUNDUPDATEPOINTS PointsMessage(BG.Team team, int delta, int pointEvent = 0)
        => new() {
            Points = _teamPoints[(int) team],
            Delta = delta,
            Team = (byte) (team == BG.Team.Blue ? 1 : 0),
            PointEvent = pointEvent,
            Tag = "",
        };

    private void EndWave(string location, string eventName) {
        // The step can arrive twice for one wave (36 ms apart in BG_PVE_Zone_01); the second would post the next wave's
        // event again and move everyone twice.
        if (string.Equals(_lastWaveEnd.Event, eventName, StringComparison.OrdinalIgnoreCase) && DateTime.UtcNow - _lastWaveEnd.At < TimeSpan.FromSeconds(5)) {
            return;
        }

        _lastWaveEnd = (eventName, DateTime.UtcNow);
        Logger.Information("Beastmoon | {0}: the wave is over; everyone goes to {1}, then {2}.", Logger.Args(ZonePath, location, eventName));
        _controlPoints?.NextWave(string.IsNullOrEmpty(location) ? [] : _players);

        if (!string.IsNullOrEmpty(location)) {
            var spot = GetLocationFromString(location);
            var text = string.Join(",", new[] { spot.X, spot.Y, spot.Z, spot.W }.Select(v => v.ToString(CultureInfo.InvariantCulture)));
            foreach (var player in _players) {
                player.Tell(new CHARACTER_103_PROTOCOL.MSG_DOTELEPORTEFFECTS());
                player.Tell(new CHARACTER_103_PROTOCOL.MSG_MOVEINZONE { Location = text });
            }
        }

        if (!string.IsNullOrEmpty(eventName)) {
            // A wizard comes with it, so the steps that speak to a player (the next wave's message) have one.
            var first = _players.FirstOrDefault();
            Self.Tell(new ZONE_102_PROTOCOL.MSG_POSTEVENT { EventName = eventName, PlayerActor = first });
        }
    }

    private void EndMatch(MatchEnd end, string why) {
        if (_match is null || _matchPhase == MatchPhase.Ended) {
            return;
        }

        _matchPhase = MatchPhase.Ended;
        Timers.Cancel(BATTLEGROUND_COUNTDOWN_TIMER);
        Timers.Cancel(BATTLEGROUND_MATCH_TIMER);
        var red = _teamPoints[(int) BG.Team.Red];
        var blue = _teamPoints[(int) BG.Team.Blue];
        Logger.Information("Beastmoon | {0}: {1} is over ({2}): {3}. Points red {4}, blue {5}.",
            Logger.Args(ZonePath, _matchName, why, end, red, blue));

        var pve = _match.m_pveMode;
        // In PvE the wizards are team A and the monsters team B; in PvP the team with more points won.
        var code = pve
            ? end == MatchEnd.Won ? END_TEAM_A : END_TEAM_B
            : red == blue ? END_TIE : red > blue ? END_TEAM_A : END_TEAM_B;
        var won = pve ? end == MatchEnd.Won : null as bool?;
        foreach (var (player, charId) in _battlegroundCharIds) {
            // Each wizard's session gives their rewards (Game/Services/BattlegroundService.cs).
            var theirs = won ?? (red != blue && (BG.Of(charId)?.Team == BG.Team.Red) == (red > blue));
            player.Tell(new ZONE_102_PROTOCOL.MSG_BATTLEGROUNDCONTROL { Kind = BattlegroundControlKind.Reward, Points = theirs ? 1 : 0 });
        }

        foreach (var player in _battlegroundCharIds.Keys) {
            player.Tell(ClockMessage(CLOCK_STOP));
            player.Tell(new WIZARD2_53_PROTOCOL.MSG_BATTLEGROUNDEND { EndCode = code, PvE = (byte) (pve ? 1 : 0) });
        }

        var outcome = pve
            ? end == MatchEnd.Won ? "Victory! The arena is yours." : "The time is up. The arena held."
            : red == blue ? $"A tie, {red} to {blue}." : $"The {(red > blue ? "Red" : "Blue")} team wins, {Math.Max(red, blue)} to {Math.Min(red, blue)}.";
        TellArena($"The match is over: {outcome} Everyone goes back in {BG.ReturnSeconds} seconds.");
        Timers.StartSingleTimer(BATTLEGROUND_HOME_TIMER,
            new ZONE_102_PROTOCOL.MSG_BATTLEGROUNDCONTROL { Kind = BattlegroundControlKind.SendHome }, TimeSpan.FromSeconds(BG.ReturnSeconds));
    }

    private void SendBattlegroundHome() {
        foreach (var player in _players.ToList()) {
            player.Tell(new CHARACTER_103_PROTOCOL.MSG_DOTELEPORTEFFECTS());
            player.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
                DestinationZone = BG.RETURN_ZONE,
                DestinationLocation = BG.RETURN_LOCATION,
                SendToClient = true,
            });
        }
    }

    private void TickControlPoints() {
        if (_controlPoints is null || _players.Count == 0 || _matchPhase == MatchPhase.Ended) {
            return;
        }

        var posts = new List<ControlPoints.Post>();
        var tells = new List<ControlPoints.Tell>();
        _controlPoints.Tick(ControlPoints.TickMilliseconds / 1000f, posts, tells);
        ScoreTick(posts);

        // A wizard of the team that caused it comes with each event: the script's steps that act on a player (the
        // point's teleporter, its message) run for them, and the ones that change the zone run once.
        foreach (var post in posts) {
            Self.Tell(new ZONE_102_PROTOCOL.MSG_POSTEVENT {
                EventName = post.EventName,
                PlayerActor = post.Player,
                PlayerGameObject = post.PlayerObject,
            });
        }

        foreach (var tell in tells) {
            foreach (var player in tell.Players) {
                player.Tell(ServerLine(tell.Text));
            }
        }
    }

    private float _dominationClock;

    // The match's score from the rings (BattlegroundScore): a ring taken, and every DominationSeconds each ring held.
    // In PvE the monsters (team B) hold every ring the wizards do not.
    private void ScoreTick(List<ControlPoints.Post> posts) {
        if (_matchPhase != MatchPhase.Running || _match is null) {
            return;
        }

        foreach (var post in posts) {
            var team = post.EventName.StartsWith("EVENT_CTF_Red_", StringComparison.OrdinalIgnoreCase) ? BG.Team.Red
                : post.EventName.StartsWith("EVENT_CTF_Blue_", StringComparison.OrdinalIgnoreCase) ? BG.Team.Blue
                : BG.Team.None;
            if (team != BG.Team.None) {
                AddPoints(new ZONE_102_PROTOCOL.MSG_BATTLEGROUNDCONTROL {
                    Kind = BattlegroundControlKind.Points, Team = (byte) team, Points = BattlegroundScore.CapturePoints,
                    PointEvent = BattlegroundScore.PE_COMBAT_CAP,
                });
            }
        }

        _dominationClock += ControlPoints.TickMilliseconds / 1000f;
        if (_dominationClock < BattlegroundScore.DominationSeconds) {
            return;
        }

        _dominationClock = 0;
        var red = _controlPoints.Points.Count(p => p.Owner == BG.Team.Red);
        var blue = _match.m_pveMode ? _controlPoints.Points.Count - red : _controlPoints.Points.Count(p => p.Owner == BG.Team.Blue);
        foreach (var (team, held) in new[] { (BG.Team.Red, red), (BG.Team.Blue, blue) }) {
            if (held > 0 && BattlegroundScore.DominationPoints > 0) {
                AddPoints(new ZONE_102_PROTOCOL.MSG_BATTLEGROUNDCONTROL {
                    Kind = BattlegroundControlKind.Points, Team = (byte) team, Points = held * BattlegroundScore.DominationPoints,
                });
            }
        }
    }

    private void TellArena(string text) {
        foreach (var player in _players) {
            player.Tell(ServerLine(text));
        }
    }

    private static EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE ServerLine(string text) => new() { Message = text, Modal = 0 };

    private string BattlegroundStatus() {
        if (_controlPoints is null) {
            return "This zone is not a Beastmoon arena.";
        }

        var match = _match is null
            ? "No match."
            : $"{_matchName} ({(_match.m_pveMode ? "PvE" : "PvP")}): " + _matchPhase switch {
                MatchPhase.Countdown => $"starts in {SecondsLeft()}s.",
                MatchPhase.Running => $"{FormatTime(SecondsLeft())} left.",
                MatchPhase.Ended => "over.",
                _ => "not started.",
            } + $" Points red {_teamPoints[(int) BG.Team.Red]}, blue {_teamPoints[(int) BG.Team.Blue]} (of {_match.m_maximumDisplayPoints}).";

        return match + " Points: " + _controlPoints.Status();
    }

}
