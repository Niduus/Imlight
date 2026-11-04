/*
 * What the game is shown of each cheat step (Niduus, 2026-11-04: "automated checks to cheats for player and boss ...
 * intended actions instead of open actions"). The game draws only what it is shown cast, so the server checks every
 * step and casts a real spell for what a step changes with no spell (CombatResolver.OpenChanges.cs). This list says,
 * in words for the cheat editor and Boss Test, which steps that check shows and which it can't. The Manager links this
 * file in (Imlight.Setup.csproj); the server's check does not read it.
 */

#nullable enable

using System;

namespace Imlight.CoreLib.Game.Combat;

internal static class CheatDisplay {

    internal enum Shown {
        /// <summary>Shown by a real spell cast for it automatically.</summary>
        Auto,
        /// <summary>The game can't be shown it: it keeps showing something else until the battle moves on.</summary>
        NotShown,
    }

    /// <summary>
    /// What the game sees of one Do step (its server Do name and Kind), or null when nothing needs saying (a real
    /// cast, words, a step that changes nothing on screen).
    /// </summary>
    internal static (Shown How, string Note)? Check(string? doName, string? kind = null) {
        var k = (kind ?? "").Trim().ToLowerInvariant();
        switch ((doName ?? "").Trim().ToLowerInvariant()) {
            case "removeeffects" or "removecharms" or "removewards" or "removeauras":
                return (Shown.Auto, "Shown in game: the server casts a real removal spell for it, so the effects disappear on screen too.");
            case "stealeffects":
                return (Shown.Auto, "Shown in game: a removal spell takes them off the target and the spells they came from go on the thief.");
            case "addeffects":
                return (Shown.Auto, "Shown in game: the spell is cast for real when it only puts charms and wards on.");
            case "addcharm" when k is "dot" or "hot":
                return (Shown.Auto, $"Shown in game with a real {(k == "dot" ? "damage" : "heal")} over time spell, unless the game would use up a "
                                    + $"{(k == "dot" ? "blade, trap or shield" : "heal boost")} the server keeps; then it ticks on the server only "
                                    + "(the health ring catches up later).");
            case "heal":
                return (Shown.Auto, "Shown in game with a real heal of that size, unless the game would use up a heal boost the server keeps.");
            case "drain":
                return (Shown.NotShown, "The target's lost health is not shown in game (no spell is cast), only the drainer's heal. "
                                        + "To hurt wizards, cast a real spell instead (Steal Health...).");
            case "damage":
                return (Shown.NotShown, "The game is not shown this health loss (no spell is cast), so the health ring won't move with it and the game "
                                        + "and the server can disagree about who is defeated. To hurt wizards, cast a real spell instead.");
            case "healthto" or "swaphealth" or "maxhealth":
                return (Shown.NotShown, "Only a health gain is shown in game (with a real heal). A health loss is not, so the game can show more "
                                        + "health than the server.");
            case "detonate":
                return (Shown.NotShown, "The effects coming off are shown, but the damage they deal is not (no spell is cast for it). "
                                        + "To hurt wizards, cast a real spell instead.");
            case "revive":
                return (Shown.NotShown, "The game can't be shown someone getting back up with no spell: it keeps them shown as defeated.");
            case "scaleeffects":
                return (Shown.NotShown, "Server only: the game keeps showing the old size, but hits count the new one.");
            case "effectsschool":
                return (Shown.NotShown, "Server only: the game keeps showing the old school, but hits count the new one.");
            default:
                return null;
        }
    }

}
