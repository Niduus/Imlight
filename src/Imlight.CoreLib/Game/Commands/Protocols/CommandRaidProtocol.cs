using System;
using System.Globalization;
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.raid` for the guild raids (Game/Raids): how the raid you are in is going, leaving it, and for testing, going
/// straight into a raid, ending it, and pushing a step of its script that is stuck (an event, a counter).
/// </summary>
internal class CommandRaidProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    private const string HELP = ".raid shows the raid you are in. .raid leave takes you back to the guild grounds. "
        + "Testing: .raid go void (straight into The Voracious Void, fresh), .raid end, .raid timer 30, "
        + ".raid event EventName, .raid counter CounterName 4, .raid loot (The Voracious Void's drops, once more).";

    [Command("raid")]
    [Alias("raids")]
    private void RaidCommand([Remainder] string text = "") {
        if (!Raids.Raids.Enabled) {
            InformSenderClient("Guild raids are turned off on this server.");

            return;
        }

        try {
            var words = (text ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var first = words.Length > 0 ? words[0].ToLowerInvariant() : "";
            var me = Context.Character;
            var inRaid = Raids.Raids.IsRaidZone(me.Zone);
            switch (first) {
                case "":
                case "status":
                    if (!inRaid) {
                        InformSenderClient("You are not in a raid. Raids: " + string.Join(", ", Raids.Raids.All.Select(r => r.Name))
                            + ". Open one at a raid gate on the battle island of your guild grounds. " + HELP);

                        return;
                    }

                    var answer = Context.ZoneActor.Ask<ZONE_102_PROTOCOL.MSG_RAIDCONTROL>(
                        new ZONE_102_PROTOCOL.MSG_RAIDCONTROL { Kind = RaidControlKind.Status }, TimeSpan.FromSeconds(5)).Result;
                    InformSenderClient(answer?.Text ?? "The raid did not answer.");

                    return;
                case "leave":
                case "out":
                case "back":
                    if (!inRaid) {
                        InformSenderClient("You are not in a raid.");

                        return;
                    }

                    InformSenderClient("Going back to the guild grounds.");
                    Context.SessionActor.Tell(new CHARACTER_103_PROTOCOL.MSG_RAIDLEAVE());

                    return;
            }

            if (Context.Account.AuthLevel < AuthLevel.QualityAssurance) {
                InformSenderClient(HELP);

                return;
            }

            switch (first) {
                case "go":
                    Go(string.Join(' ', words.Skip(1)));

                    return;
                case "loot":
                case "drops":
                    Raids.RaidRewards.Grant(Context.SessionActor, me,
                        inRaid && Raids.RaidRewards.Has(me.Zone) ? me.Zone : Raids.Raids.All[0].Zone);

                    return;
                case "end":
                    if (InRaidOrSay(inRaid)) {
                        Context.ZoneActor.Tell(new ZONE_102_PROTOCOL.MSG_RAIDCONTROL { Kind = RaidControlKind.End });
                    }

                    return;
                case "timer":
                    if (InRaidOrSay(inRaid) && words.Length > 1 && double.TryParse(words[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes)) {
                        Context.ZoneActor.Tell(new ZONE_102_PROTOCOL.MSG_RAIDCONTROL {
                            Kind = RaidControlKind.Timer,
                            Seconds = (int) Math.Round(minutes * 60),
                        });
                        InformSenderClient(minutes > 0 ? $"The raid timer is set to {minutes} minute(s)." : "The raid timer is stopped.");
                    }

                    return;
                case "event":
                    if (InRaidOrSay(inRaid) && words.Length > 1) {
                        var name = string.Join(' ', words.Skip(1));
                        Context.ZoneActor.Tell(new ZONE_102_PROTOCOL.MSG_POSTEVENT {
                            EventName = name,
                            PlayerActor = Context.SessionActor,
                            PlayerGameObject = Context.CharacterObject,
                        });
                        InformSenderClient($"Posted {name} in the raid.");
                    }

                    return;
                case "counter":
                    if (InRaidOrSay(inRaid) && words.Length > 2 && int.TryParse(words[^1], out var value)) {
                        var counter = string.Join(' ', words.Skip(1).Take(words.Length - 2));
                        Context.ZoneActor.Tell(new ZONE_102_PROTOCOL.MSG_ZONESCRIPTRESULT {
                            Result = new ResZoneCounter { m_counterName = counter, m_action = "ZCA_Set", m_value = value },
                            PlayerGameObject = Context.CharacterObject,
                        });
                        InformSenderClient($"Set {counter} to {value}.");
                    }

                    return;
            }

            InformSenderClient(HELP);
        }
        catch (Exception ex) {
            Logger.Error("Raids | .raid failed: {0}", Logger.Args(ex));
            InformSenderClient("That did not work: " + ex.Message);
        }
    }

    private bool InRaidOrSay(bool inRaid) {
        if (!inRaid) {
            InformSenderClient("You are not in a raid.");
        }

        return inRaid;
    }

    // Straight into a fresh copy of the raid, your own (a test without the gate).
    private void Go(string name) {
        var raid = string.IsNullOrWhiteSpace(name) ? Raids.Raids.All[0] : Raids.Raids.Find(name);
        if (raid is null) {
            InformSenderClient("No raid is called that. Raids: " + string.Join(", ", Raids.Raids.All.Select(r => r.Name)) + ".");

            return;
        }

        var me = Context.Character;
        ZoneInstanceOwners.Clear(me.CharId, raid.Zone);
        InformSenderClient($"Going into {raid.Name}.");
        Context.SessionActor.Tell(new CHARACTER_103_PROTOCOL.MSG_DOTELEPORTEFFECTS());
        Context.SessionActor.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
            DestinationZone = raid.Zone,
            DestinationLocation = Raids.Raids.START_LOCATION,
            SendToClient = true,
            IsPrivate = true,
            OwnerCharId = me.CharId,
            ResetInstance = true,
        });
    }

}
