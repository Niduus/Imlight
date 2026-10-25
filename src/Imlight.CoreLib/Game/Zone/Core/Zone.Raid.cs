using System;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Zone.Core;

/// <summary>
/// The part of a zone a raid needs (Game/Raids): its timer, its win and its end, after which everyone in it goes back
/// to the guild grounds.
/// </summary>
public partial class Zone {

    private const string RAID_TIMER = "raid-timer";
    private const string RAID_HOME_TIMER = "raid-home";

    private bool _raidEnding;
    private bool _raidWon;
    private DateTime _raidTimeUp = DateTime.MinValue;

    private bool IsRaid => Raids.Raids.IsRaidZone(ZonePath);

    /// <summary>
    /// Lets a raid's pads work for a smaller group than the raid is made for ([Raids] SmallGroups).
    /// </summary>
    private void SetUpRaid(WizZoneTriggers triggers) {
        if (!IsRaid || !Raids.Raids.SmallGroups) {
            return;
        }

        var counters = Raids.Raids.PresenceCounters(triggers);
        var full = Raids.Raids.For(ZonePath)?.Players ?? 12;
        ScriptState.UseSmallGroups(counters, full);
        Logger.Information("Raids | {0}: the pads that count wizards ({1}) work for groups of fewer than {2}.",
            Logger.Args(ZonePath, string.Join(", ", counters), full));
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_RAIDCONTROL))]
    private void ReceiveRaidControl(ZONE_102_PROTOCOL.MSG_RAIDCONTROL message) {
        switch (message.Kind) {
            case RaidControlKind.ToPlayers:
                if (message.Message is not null) {
                    foreach (var player in _players) {
                        player.Tell(message.Message);
                    }
                }
                break;
            case RaidControlKind.Timer:
                SetRaidTimer(message.Seconds);
                break;
            case RaidControlKind.TimeUp:
                Logger.Information("Raids | {0}: the time is up.", Logger.Args(ZonePath));
                _raidTimeUp = DateTime.MinValue;
                Self.Tell(new ZONE_102_PROTOCOL.MSG_POSTEVENT { EventName = Raids.Raids.TIME_UP_EVENT });
                break;
            case RaidControlKind.Win:
                if (!_raidWon) {
                    _raidWon = true;
                    Logger.Information("Raids | {0}: the raid is won.", Logger.Args(ZonePath));
                    TellRaid(Tools.Announcements.Text("raid.won"));
                    if (Raids.RaidRewards.Has(ZonePath)) {
                        foreach (var player in _players) {
                            player.Tell(new CHARACTER_103_PROTOCOL.MSG_RAIDREWARD { Zone = ZonePath });
                        }
                    }
                }
                break;
            case RaidControlKind.End:
                EndRaid();
                break;
            case RaidControlKind.SendHome:
                foreach (var player in _players) {
                    player.Tell(new CHARACTER_103_PROTOCOL.MSG_RAIDLEAVE());
                }
                break;
            case RaidControlKind.SigilSpell:
                if (message.Spells.Length > 0) {
                    Raids.Raids.CastSigilSpells(ZonePath, _instancedCharIds.Values, message.Spells);
                    Logger.Information("Raids | {0}: sigil spell(s) {1} go into the battles here.",
                        Logger.Args(ZonePath, string.Join(", ", message.Spells)));
                    TellRaid(Tools.Announcements.Text("raid.sigil"));
                }
                break;
            case RaidControlKind.Status:
                Sender.Tell(new ZONE_102_PROTOCOL.MSG_RAIDCONTROL { Kind = RaidControlKind.Status, Text = RaidStatus() });
                break;
        }
    }

    private void SetRaidTimer(int seconds) {
        if (seconds <= 0) {
            Timers.Cancel(RAID_TIMER);
            _raidTimeUp = DateTime.MinValue;

            return;
        }

        Timers.StartSingleTimer(RAID_TIMER, new ZONE_102_PROTOCOL.MSG_RAIDCONTROL { Kind = RaidControlKind.TimeUp },
            TimeSpan.FromSeconds(seconds));
        _raidTimeUp = DateTime.UtcNow.AddSeconds(seconds);
        Logger.Information("Raids | {0}: the raid timer is set to {1} seconds.", Logger.Args(ZonePath, seconds));

        // The short ones (1 or 5 seconds) end a part of the raid at once; only a real timer is worth a line.
        if (seconds >= 60) {
            TellRaid(Tools.Announcements.Text("raid.timer", ("time", FormatTime(seconds))));
        }
    }

    private void EndRaid() {
        if (_raidEnding) {
            return;
        }

        _raidEnding = true;
        Timers.Cancel(RAID_TIMER);
        _raidTimeUp = DateTime.MinValue;
        Raids.Raids.EndRun(ZonePath, _instancedCharIds.Values);

        var seconds = Raids.Raids.ReturnSeconds;
        Logger.Information("Raids | {0}: the raid is over ({1}); players go back in {2} seconds.",
            Logger.Args(ZonePath, _raidWon ? "won" : "not won", seconds));
        TellRaid(Tools.Announcements.Text(_raidWon ? "raid.complete" : "raid.over", ("seconds", seconds)));
        Timers.StartSingleTimer(RAID_HOME_TIMER, new ZONE_102_PROTOCOL.MSG_RAIDCONTROL { Kind = RaidControlKind.SendHome },
            TimeSpan.FromSeconds(seconds));
    }

    private void TellRaid(string line) {
        if (line is null) {
            // Turned off in the Manager's Announcements tab.
            return;
        }

        foreach (var player in _players) {
            player.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = line, Modal = 0 });
        }
    }

    private string RaidStatus() {
        var raid = Raids.Raids.For(ZonePath);
        var name = raid?.Name ?? ZonePath;
        var time = _raidTimeUp > DateTime.UtcNow
            ? $"{FormatTime((int) (_raidTimeUp - DateTime.UtcNow).TotalSeconds)} left"
            : "no timer running";
        var state = _raidEnding ? (_raidWon ? "complete" : "over") : _raidWon ? "won" : "in progress";
        var pads = ScriptState.PresenceCounters.Count == 0
            ? "none"
            : string.Join(", ", ScriptState.PresenceCounters.Select(c => $"{c} {ScriptState.GetCounter(c)}"));

        return $"{name}: {state}, {time}, {_players.Count} wizard(s) inside. Pads: {pads}.";
    }

    private static string FormatTime(int seconds)
        => seconds >= 60 ? $"{seconds / 60} minute(s)" + (seconds % 60 > 0 ? $" {seconds % 60} second(s)" : "") : $"{seconds} second(s)";

}
