/*
 * Cheats outside battle (Niduus, 2026-10-29: "expand more on blocks again"; the cheat ideas list's "cheats outside
 * battle"). Two Whens go off with no battle at all:
 *   LogIn       a wizard comes into the game (the first zone after choosing the wizard).
 *   ZoneEnter   a wizard comes into a zone whose name has one of Zone's words (commas between; "" any zone), the
 *               first zone after logging in too.
 * Whose: a player cheat goes off for the wizards who have it (its "who has it" Ifs, as in a battle). A creature's cheat
 * (its BossCheats file) goes off for every wizard who comes in, as a greeting from that creature: "it" is the creature
 * and "the player" the wizard. A few seconds after the zone is loaded, so the game shows it.
 * The Ifs that can be looked at with no battle: the "who has it" ones, Var (saved variables), Quest, Badge, Level and
 * School of the wizard; with any other If the cheat does not go off. Chance, Once (once a login) and Off count.
 * The Dos that work: Say (Text, in chat), Announce (box, chat or both), Reward, SetVar (Saved / SavedAll variables) and
 * Sound. Any other Do is left out, with a line in the log.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Common;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.Combat;

internal static class OutsideCheats {

    // How long after the zone is loaded the cheats go off, so the game is ready to show them.
    private static readonly TimeSpan s_after = TimeSpan.FromSeconds(4);

    // Wizards (by character id) that logged in and have not yet come into their first zone.
    private static readonly ConcurrentDictionary<ulong, bool> s_justLoggedIn = new();

    // The Once cheats each wizard has had this login: "p3" (player cheat 3) or "c<template>:3".
    private static readonly ConcurrentDictionary<ulong, ConcurrentDictionary<string, bool>> s_used = new();

    /// <summary>A wizard was chosen to play (WizardService): the next zone they come into is their log in.</summary>
    internal static void NoteLogIn(ulong charId) {
        s_justLoggedIn[charId] = true;
        s_used.TryRemove(charId, out _);
    }

    /// <summary>A wizard came into a zone (ZoneService, after the zone added them).</summary>
    internal static void ZoneEntered(Wizard wizard, IActorRef actor) {
        if (wizard is null || actor is null) {
            return;
        }

        var loggedIn = s_justLoggedIn.TryRemove(wizard.CharId, out _);
        var zone = wizard.Zone ?? "";
        _ = Task.Delay(s_after).ContinueWith(_ => {
            try {
                Run(wizard, actor, zone, loggedIn);
            }
            catch (Exception e) {
                Logger.Warning("Cheats outside battle failed for {0}: {1}", Logger.Args(wizard.CharId, e.Message));
            }
        }, TaskScheduler.Default);
    }

    private static void Run(Wizard wizard, IActorRef actor, string zone, bool loggedIn) {
        var mine = PlayerCheats.ForWizard(wizard);
        for (var i = 0; i < mine.Count; i++) {
            TryCheat(wizard, actor, zone, loggedIn, mine[i], $"p{i + 1}", null);
        }

        foreach (var (template, cheats) in BossCheats.All()) {
            for (var i = 0; i < cheats.Count; i++) {
                if (cheats[i] is { Off: false } c && (c.Is("LogIn") || c.Is("ZoneEnter"))) {
                    TryCheat(wizard, actor, zone, loggedIn, c, $"c{template}:{i + 1}", template);
                }
            }
        }
    }

    private static void TryCheat(Wizard wizard, IActorRef actor, string zone, bool loggedIn, BossCheats.BossCheat cheat, string key,
                                 ulong? creature) {
        if (cheat is null || cheat.Off) {
            return;
        }

        var due = (cheat.Is("LogIn") && loggedIn) || (cheat.Is("ZoneEnter") && ZoneFits(zone, cheat.Zone));
        if (!due || !IfsHold(wizard, cheat, creature)) {
            return;
        }

        var used = s_used.GetOrAdd(wizard.CharId, _ => new());
        if (cheat.Once && used.ContainsKey(key)) {
            return;
        }

        if (cheat.Chance < 100 && Random.Shared.Next(100) >= Math.Max(0, cheat.Chance)) {
            return;
        }

        used[key] = true;
        var whose = creature is { } id ? BossCheats.NameOf(id) : "player cheat";
        Logger.Information("Cheat outside battle | {0} cheat {1} for {2} ({3}, {4}).",
            Logger.Args(whose, key, wizard.PlayerNameBehavior?.GetWizardName() ?? wizard.CharId.ToString(), loggedIn ? "log in" : "zone", zone));
        foreach (var step in cheat.Steps().Where(s => s is not null && !s.Off)) {
            RunStep(wizard, actor, step, creature, whose);
        }
    }

    private static bool ZoneFits(string zone, string words) {
        var wanted = Words(words);

        return wanted.Count == 0 || wanted.Any(w => zone.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> Words(string text)
        => [.. (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static bool IfsHold(Wizard wizard, BossCheats.BossCheat cheat, ulong? creature) {
        var groups = new Dictionary<int, bool>();
        foreach (var c in (cheat.Conditions ?? []).Where(c => c is not null && !string.IsNullOrWhiteSpace(c.If))) {
            var holds = Holds(wizard, c, creature) != c.Not;
            if (c.Any <= 0) {
                if (!holds) {
                    return false;
                }
            }
            else {
                groups[c.Any] = groups.GetValueOrDefault(c.Any) || holds;
            }
        }

        return groups.Values.All(g => g);
    }

    private static bool Holds(Wizard wizard, BossCheats.CheatCondition c, ulong? creature) {
        // About someone else (an enemy, a teammate): no one else is there outside a battle, so a player cheat's If about
        // anyone but itself does not hold. A creature's Ifs are all about the wizard who came in.
        var aboutIt = (c.Who ?? "").Trim().ToLowerInvariant() is "" or "self" or "me";
        if (PlayerCheats.WizardIs(c, wizard.CharId, wizard, wizard.CharId) is { } isWizard) {
            return isWizard;
        }

        if (!aboutIt && creature is null) {
            return false;
        }

        switch ((c.If ?? "").Trim().ToLowerInvariant()) {
            case "var":
                return Compare(GetVar(wizard, creature, c.Var, c.Scope), c.Op,
                    (c.Var2 ?? "").Trim().Length > 0 ? GetVar(wizard, creature, c.Var2, c.Scope2) : c.Value);
            case "quest":
                return Words(c.Name).Any(wizard.HasCompletedQuest);
            case "badge":
                return Words(c.Name).Any(b => uint.TryParse(b, out var id) && wizard.BadgeBehavior?.Earned?.ContainsKey(id) == true);
            case "level":
                return Compare(wizard.MagicSchoolBehavior?.Level ?? 0, c.Op, c.Value);
            case "school":
                return wizard.MagicSchoolBehavior is { } school
                       && school.MagicSchool.ToString().Contains((c.School ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
            default:
                return false;
        }
    }

    private static bool Compare(double value, string op, double than) => (op ?? "").Trim() switch {
        "<" => value < than,
        "<=" => value <= than,
        ">" => value > than,
        "=" or "==" => Math.Abs(value - than) < 0.0001,
        "!=" or "<>" => Math.Abs(value - than) >= 0.0001,
        _ => value >= than,
    };

    // A saved variable: SavedAll everyone's, else (Saved, or a battle's, which outside a battle is the saved one) its own:
    // the wizard's for a player cheat, the creature's for a creature's.
    private static string VarKey(Wizard wizard, ulong? creature, string name, string scope) {
        name = (name ?? "").Trim().ToLowerInvariant();

        return (scope ?? "").Trim().Equals("SavedAll", StringComparison.OrdinalIgnoreCase) ? $"all:{name}"
            : creature is { } id ? $"c{id}:{name}" : $"w{wizard.CharId}:{name}";
    }

    private static int GetVar(Wizard wizard, ulong? creature, string name, string scope)
        => string.IsNullOrWhiteSpace(name) ? 0 : SavedCheatVars.Get(VarKey(wizard, creature, name, scope));

    private static string Fill(string text, Wizard wizard, ulong? creature, string whose) {
        text = (text ?? "").Trim();
        if (!text.Contains('{')) {
            return text;
        }

        text = text.Replace("{player}", wizard.PlayerNameBehavior?.GetWizardName() ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{boss}", creature is null ? wizard.PlayerNameBehavior?.GetWizardName() ?? "" : whose, StringComparison.OrdinalIgnoreCase)
            .Replace("{round}", "0", StringComparison.OrdinalIgnoreCase);

        return System.Text.RegularExpressions.Regex.Replace(text, @"\{([A-Za-z0-9_ ]+)\}", m => {
            var name = m.Groups[1].Value.Trim().ToLowerInvariant();
            var own = SavedCheatVars.Get(VarKey(wizard, creature, name, "Saved"));
            var all = SavedCheatVars.Get($"all:{name}");

            return own != 0 ? own.ToString() : all != 0 ? all.ToString() : m.Value;
        });
    }

    private static void RunStep(Wizard wizard, IActorRef actor, BossCheats.CheatAction step, ulong? creature, string whose) {
        switch ((step.Do ?? "").Trim().ToLowerInvariant()) {
            case "say":
            case "mychat": {
                var text = Fill(step.Text, wizard, creature, whose);
                if (text.Length > 0) {
                    actor.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE {
                        Message = ServerMessages.Own(creature is null ? text : $"{whose}: {text}"), Modal = 0,
                    });
                }

                break;
            }
            case "announce": {
                var text = Fill(step.Text, wizard, creature, whose);
                if (text.Length == 0) {
                    break;
                }

                var style = (step.Style ?? "").Trim().ToLowerInvariant();
                if (style is "" or "box" or "both") {
                    actor.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = ManagerCommands.AnnouncementBox(text), Modal = 1 });
                }

                if (style is "chat" or "both") {
                    actor.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = ManagerCommands.AnnouncementChat(text), Modal = 0 });
                }

                break;
            }
            case "reward": {
                var result = new DropTableResult {
                    GoldAmount = Math.Max(0, step.Gold), ExperienceAmount = Math.Max(0, step.Xp), TrainingPoints = Math.Max(0, step.Tp),
                    Items = [.. Words(step.Items).Select(i => new DropItemResult { ItemId = i, Quantity = Math.Max(1, step.Count) })],
                };
                LootGranter.Grant(actor, wizard, result);
                var got = string.Join(", ", new[] {
                    step.Gold > 0 ? $"{step.Gold} gold" : "", step.Xp > 0 ? $"{step.Xp} experience" : "", step.Tp > 0 ? $"{step.Tp} training points" : "",
                    result.Items.Count > 0 ? $"{result.Items.Count} item(s)" : "",
                }.Where(t => t.Length > 0));
                if (got.Length > 0) {
                    actor.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = $"Reward: {got}.", Modal = 0 });
                }

                break;
            }
            case "setvar": {
                if (string.IsNullOrWhiteSpace(step.Var)) {
                    break;
                }

                var key = VarKey(wizard, creature, step.Var, step.Scope);
                var now = SavedCheatVars.Get(key);
                var value = (step.Op ?? "").Trim().ToLowerInvariant() switch {
                    "add" or "+" => now + step.Amount,
                    "subtract" or "-" => now - step.Amount,
                    "multiply" or "*" => now * step.Amount,
                    "random" => Random.Shared.Next(Math.Min(0, step.Amount), Math.Max(0, step.Amount) + 1),
                    _ => step.Amount,
                };
                SavedCheatVars.Set(key, value);

                break;
            }
            case "sound": {
                var sound = (step.Text ?? "").Trim();
                if (sound.Length == 0) {
                    break;
                }

                if (System.IO.Path.HasExtension(sound)) {
                    actor.Tell(new GAME_5_PROTOCOL.MSG_PLAYSOUND { SoundID = new GID(), SoundFilename = sound });
                }
                else if (uint.TryParse(sound, out var id)
                         || CoreObjectFactory.TryGetCoreTemplateID(x => x.m_filename.ToString().Contains(sound, StringComparison.OrdinalIgnoreCase), out id)) {
                    actor.Tell(new GAME_5_PROTOCOL.MSG_PLAYSOUND { SoundID = new GID(id), SoundFilename = "" });
                }

                break;
            }
            default:
                Logger.Information("Cheat outside battle | {0}: \"{1}\" only works in a battle; left out.", Logger.Args(whose, step.Do));
                break;
        }
    }
}
