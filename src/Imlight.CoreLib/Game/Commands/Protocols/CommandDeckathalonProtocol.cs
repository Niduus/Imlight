using System;
using System.Linq;
using Imlight.Common;
using Imlight.CoreLib.Game.Events;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.deckathalon` for the Deckathalon (Niduus, 2026-10-24; Game/Events/Deckathalon.cs): the event that is on and your
/// best floor, climbing the tower, leaving it, and the best wizards of the event. The server's testers can climb any
/// school's tower at any time (`.deckathalon start ice`).
/// </summary>
internal class CommandDeckathalonProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("deckathalon")]
    [Alias("deck", "tower")]
    private void DeckathalonCommand([Remainder] string text = "") {
        try {
            var words = (text ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var first = words.Length > 0 ? words[0].ToLowerInvariant() : "";
            var me = Context.Character;
            var tester = Context.Account.AuthLevel >= AuthLevel.QualityAssurance;
            switch (first) {
                case "":
                case "status":
                    InformSenderClient(Status(me, tester));

                    return;
                case "top":
                case "best":
                case "scores":
                case "leaderboard":
                    InformSenderClient(TopList(words.Skip(1).FirstOrDefault()));

                    return;
                case "leave":
                case "out":
                case "quit":
                    InformSenderClient(Deckathalon.Leave(Context.SessionActor, me));

                    return;
                case "start":
                case "enter":
                case "climb":
                case "go":
                    Start(me, words.Skip(1).FirstOrDefault() ?? "", tester);

                    return;
            }

            // `.deckathalon fire` is `.deckathalon start fire`.
            if (Deckathalon.SchoolName(first) is not null) {
                Start(me, first, tester);

                return;
            }

            InformSenderClient("Use .deckathalon, .deckathalon start, .deckathalon leave or .deckathalon top.");
        }
        catch (Exception ex) {
            Logger.Error("Deckathalon | .deckathalon {0} failed: {1}", Logger.Args(text, ex));
            InformSenderClient("The Deckathalon command did not work. The server log has the details.");
        }
    }

    /// <summary>
    /// `.credits` shows your Deckathalon credits, `.credits 10` gives you 10 more and `.credits -5` takes 5 away
    /// (Niduus, 2026-10-25: "add a .credits commands so I can give myself credits").
    /// </summary>
    [Command("credits")]
    [Alias("credit")]
    private void CreditsCommand([Remainder] string text = "") {
        try {
            var me = Context.Character;
            var word = (text ?? "").Trim();
            if (Deckathalon.CurrentEvent is null) {
                InformSenderClient("No Deckathalon is on right now. The Events card of the Imlight app starts one.");

                return;
            }

            if (word.Length == 0) {
                InformSenderClient($"You have {Deckathalon.CreditsOf(me)} Deckathalon credits. Type .credits 10 to get 10 more.");

                return;
            }

            if (!int.TryParse(word, out var amount)) {
                InformSenderClient("Type .credits and a number, such as .credits 10 (or .credits -5 to take some away).");

                return;
            }

            if (Context.Account.AuthLevel < AuthLevel.QualityAssurance) {
                InformSenderClient("Only the server's testers can give credits.");

                return;
            }

            var now = Deckathalon.AddCredits(me, amount);
            InformSenderClient(now is null ? "No Deckathalon is on right now." : $"You now have {now} Deckathalon credits.");
        }
        catch (Exception ex) {
            Logger.Error("Deckathalon | .credits {0} failed: {1}", Logger.Args(text, ex));
            InformSenderClient("The credits command did not work. The server log has the details.");
        }
    }

    private void Start(Wizard me, string school, bool tester) {
        Deckathalon.TryStart(Context.SessionActor, me, school, tester, out var reply);
        InformSenderClient(reply);
    }

    private static string Status(Wizard me, bool tester) {
        var lines = new System.Collections.Generic.List<string>();
        var current = Deckathalon.CurrentEvent;
        lines.Add(current is null
            ? "No Deckathalon is on right now. The Events card of the Imlight app starts one."
            : $"The {current.School} Deckathalon is on{GameEvents.TimeLeft(current)}. Type .deckathalon start to climb it.");
        if (Deckathalon.RunOf(me.CharId) is { } run) {
            lines.Add($"You are on floor {run.Floor} of the {run.Tower.School} tower with {run.Points} points (.deckathalon leave to stop).");
        }

        if (current is not null && Deckathalon.PlaceOf(current.Key, me.CharId) is ({ } score, var place)) {
            lines.Add($"Your best: floor {score.BestFloor}, {score.Points} points, place {place}.");
        }

        if (tester) {
            lines.Add("As a tester you can climb any tower at any time: .deckathalon start fire (ice, storm, myth, life, death, balance).");
        }

        return string.Join("<br>", lines);
    }

    private static string TopList(string school) {
        var current = Deckathalon.CurrentEvent;
        var key = Deckathalon.SchoolName(school) is { } picked && (current is null || current.School != picked)
            ? $"Practice-{picked}"
            : current?.Key;
        if (key is null) {
            return "No Deckathalon is on right now. .deckathalon top fire shows the practice climbs of the Fire tower.";
        }

        var top = Deckathalon.Top(key, 10);
        if (top.Count == 0) {
            return "Nobody has cleared a floor yet.";
        }

        return string.Join("<br>", top.Select((s, i) => $"{i + 1}. {s.Name}: floor {s.BestFloor}, {s.Points} points"));
    }

}
