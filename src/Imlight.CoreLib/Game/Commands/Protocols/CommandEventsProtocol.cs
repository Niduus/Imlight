using System;
using System.Collections.Generic;
using System.Linq;
using Imlight.Common;
using Imlight.CoreLib.Game.Events;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.events` for the game's events (Game/Events/ClassProjects.cs): the events on, your points in each and the rewards
/// waiting, and `.events claim`, which claims every reward you have the points for (the same as the window's claim
/// buttons).
/// </summary>
internal class CommandEventsProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("events")]
    [Alias("scroll", "event")]
    private void EventsCommand([Remainder] string text = "") {
        try {
            var me = Context.Character;
            var first = (text ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() ?? "";
            if (first is "claim" or "rewards") {
                var claimed = ClassProjects.ClaimAll(Context.SessionActor, me);
                InformSenderClient(claimed.Count == 0 ? "You have no rewards waiting." : string.Join("<br>", claimed));

                return;
            }

            var lines = new List<string>();
            foreach (var running in ClassProjects.Deckathalons().Concat(ClassProjects.Active())) {
                var points = ClassProjects.PointsOf(running, me.CharId);
                var rewards = running.Template.m_rewardList ?? [];
                var next = rewards.FirstOrDefault(r => r is not null && r.m_goalCount > points);
                var waiting = ClassProjects.Unclaimed(running, me.CharId).Count;
                var when = running.Ending
                    ? $"ended, rewards can be claimed for {Math.Max(0, (running.End.AddDays(GameEvents.ENDED_DAYS) - DateTime.UtcNow).Days)}d more"
                    : $"{(running.End - DateTime.UtcNow).Days}d left";
                lines.Add($"{running.Shown} ({when}): {points} points"
                    + (next is null ? ", every reward reached" : $", next reward at {next.m_goalCount}")
                    + (waiting > 0 ? $", {waiting} reward{(waiting == 1 ? "" : "s")} to claim (.events claim)." : "."));
            }

            InformSenderClient(lines.Count == 0 ? "No events are on." : string.Join("<br>", lines));
        }
        catch (Exception ex) {
            Logger.Error("Events | .events {0} failed: {1}", Logger.Args(text, ex));
            InformSenderClient("The events command did not work. The server log has the details.");
        }
    }

}
