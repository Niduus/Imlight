/*
 * The real spells the "add an effect of your own size" cheat step (AddCharm) casts so the game draws the effect: the
 * game draws a charm, ward or bubble only from a spell it is shown being cast (it works the round out itself), so a
 * real one is cast and the server then gives its effect your size and school.
 *
 * The cast animation (Look): the game picks the casting sign and sound of a cast (FX_CastingSign_<School>,
 * FX_CastingSound_<School>) from the school of the cast spell's own template, which it looks up by the template id
 * (ClientCastEffectCinematicAction::Perform, "could not find Magic School for spellTemplateID"); nothing in the cast
 * message changes it. So a Sun or Shadow casting is a real Sun or Shadow spell of that kind cast as the lookalike.
 *
 * Each lookalike is listed with the school of the effect it puts on (its sDamageType; All for an all-school one). The
 * game uses the drawn effect on hits of that school, the server uses yours on hits of your school, so when the two
 * differ the shown one is not used up by the same hits. The Manager links this file in (Imlight.Setup.csproj) to warn.
 */

#nullable enable

using System;
using System.Collections.Generic;

namespace Imlight.CoreLib.Game.Combat;

internal static class CheatLookalikes {

    /// <summary>A lookalike spell: its name, the school of the effect it puts on, and whether it only goes on its caster.</summary>
    internal readonly record struct Look(string Spell, string EffectSchool, bool SelfOnly = false);

    private static readonly Dictionary<string, Look[]> s_plain = new(StringComparer.OrdinalIgnoreCase) {
        ["blade fire"] = [new("Fireblade", "Fire")], ["blade ice"] = [new("Iceblade", "Ice")], ["blade storm"] = [new("Stormblade", "Storm")],
        ["blade myth"] = [new("Mythblade", "Myth")], ["blade life"] = [new("Lifeblade", "Life")], ["blade death"] = [new("Deathblade", "Death")],
        ["blade shadow"] = [new("Shadowblade", "Shadow")], ["blade"] = [new("Balanceblade", "All")],
        ["trap fire"] = [new("Fire Trap", "Fire")], ["trap ice"] = [new("Ice Trap", "Ice")], ["trap storm"] = [new("Storm Trap", "Storm")],
        ["trap myth"] = [new("Myth Trap", "Myth")], ["trap life"] = [new("Life Trap", "Life")], ["trap death"] = [new("Death Trap", "Death")],
        ["trap shadow"] = [new("Shadow Trap", "Shadow")], ["trap"] = [new("Hex", "All")],
        ["shield fire"] = [new("Fire Shield", "Fire")], ["shield ice"] = [new("Snow Shield", "Ice")], ["shield storm"] = [new("Storm Shield", "Storm")],
        ["shield myth"] = [new("Myth Shield", "Myth")], ["shield life"] = [new("Life Shield", "Life")], ["shield death"] = [new("Death Shield", "Death")],
        ["shield shadow"] = [new("Shadow Shield", "Shadow")], ["shield"] = [new("Tower Shield", "All")],
        // Balance only (Niduus, 2026-10-26: "Universal blade and BALANCE blade where it's actually only for BALANCE"):
        // Balanceblade, Hex and the like work for every school, so a Balance one casts the game's Balance-only one.
        ["blade balance"] = [new("Balanceblade - Balance - Nerys", "Balance")], ["trap balance"] = [new("Attenuate - Balance", "Balance")],
        ["shield balance"] = [new("Single Balance Shield - 80", "Balance")],
        ["weakness"] = [new("Weakness", "All")],
        ["absorb"] = [new("Spirit Armor", "All")],
        // Globals (battlefield bubbles): one at a time, a new one takes the old one's place.
        ["global fire"] = [new("Wyldfire", "Fire")], ["global ice"] = [new("Balefrost", "Ice")], ["global storm"] = [new("Darkwind", "Storm")],
        ["global myth"] = [new("Blood Moon", "Myth")], ["global life"] = [new("LifeGlobal_CircleOfThorns", "Life")],
        ["global death"] = [new("DeathGlobal_Deadzone", "Death")], ["global balance"] = [new("Balance of Power", "Balance")],
        ["global shadow"] = [new("ShadowGlobal", "Shadow")], ["global"] = [new("Power PlayBOSS1", "All")],
        ["healglobal"] = [new("Sanctuary", "Life")],
        ["gloomglobal"] = [new("Doom and Gloom", "Life")],
    };

    // With a cast animation picked: the game's own spells of that school and kind (one effect each).
    private static readonly Dictionary<string, Look[]> s_cast = new(StringComparer.OrdinalIgnoreCase) {
        ["blade sun"] = [new("Sun Blade Sun", "Sun", true), new("Dragonblade TC-01", "All")],
        ["blade shadow"] = [new("Shadowblade", "Shadow")],
        ["blade moon"] = [new("Moon Blade - Moon", "Moon", true)],
        ["blade star"] = [new("Star Blade - Star", "Star", true)],
        ["trap shadow"] = [new("Shadow Trap", "Shadow")],
        ["shield sun"] = [new("Tower Shield-01", "All")],
        ["shield shadow"] = [new("Shadow Shield", "Shadow")],
        ["shield star"] = [new("Balance Immune MOB ONLY Star", "Balance")],
        ["weakness shadow"] = [new("Weakness - 75 Protected Shadow", "All")],
        ["absorb sun"] = [new("Fire Armor - Amulet Protected", "All", true)],
        ["absorb shadow"] = [new("Tung Ak Armor - Protected", "All", true)],
        ["absorb star"] = [new("Frozen Armor-01-phase1", "All")],
        ["absorb fire"] = [new("Fire Armor", "All", true), new("Spirit Armor", "All")],
        ["absorb myth"] = [new("Myth Armor", "All", true), new("Spirit Armor", "All")],
        ["global shadow"] = [new("ShadowGlobal", "Shadow")],
        ["gloomglobal moon"] = [new("Doom and Gloom Moon", "Life")],
        ["gloomglobal shadow"] = [new("Healing To Damage Global - The Prophet", "Shadow")],
    };

    private static readonly HashSet<string> s_schools = new(StringComparer.OrdinalIgnoreCase) { "fire", "ice", "storm", "myth", "life", "death", "balance" };

    private static string KindOf(string? kind) => (kind ?? "").Trim().ToLowerInvariant() is { Length: > 0 } k ? k : "blade";

    /// <summary>A battlefield bubble (Global: more damage; HealGlobal: more healing; GloomGlobal: less healing).</summary>
    internal static bool IsGlobal(string? kind) => KindOf(kind) is "global" or "healglobal" or "gloomglobal";

    /// <summary>
    /// The plain lookalike of a kind and school: that school's blade, trap, shield or global, or the all-school one
    /// (Balanceblade, Hex, Tower Shield, Weakness, Spirit Armor, an all-school bubble). Null for DoT and HoT.
    /// </summary>
    internal static Look? Plain(string? kind, string? school) {
        var k = KindOf(kind);
        var s = (school ?? "").Trim();
        if (k is "healglobal" or "gloomglobal" or "weakness" or "absorb") {
            return s_plain[k][0];
        }

        return s_plain.TryGetValue($"{k} {s}", out var picks) ? picks[0] : s_plain.TryGetValue(k, out var any) ? any[0] : null;
    }

    /// <summary>
    /// The lookalike to cast for a kind and school with a cast animation ("" or Normal: the plain one). Null when the
    /// game has no spell of that kind for that animation (the plain one is cast then).
    /// </summary>
    internal static Look? WithCast(string? kind, string? school, string? look, bool onCaster) {
        var k = KindOf(kind);
        var l = (look ?? "").Trim().ToLowerInvariant();
        if (l is "" or "normal") {
            return Plain(kind, school);
        }

        if (s_cast.TryGetValue($"{k} {l}", out var picks)) {
            foreach (var pick in picks) {
                if (!pick.SelfOnly || onCaster) {
                    return pick;
                }
            }
        }

        // One of the seven schools: that school's own blade, trap, shield or global (Spirit Armor for any absorb).
        // The Balance cast of a universal (or another school's) blade, trap or shield is still the universal one, whose
        // casting is Balance's (Balanceblade, Hex); only a Balance one casts the Balance-only spell.
        if (l == "balance" && k is "blade" or "trap" or "shield" && !(school ?? "").Trim().Equals("balance", StringComparison.OrdinalIgnoreCase)) {
            return Plain(k, "");
        }

        return s_schools.Contains(l) && k is "blade" or "trap" or "shield" or "absorb" or "global" ? Plain(k, l) : null;
    }

    /// <summary>The lookalike that is cast in the end: the one for the animation, else the plain one.</summary>
    internal static Look? Picked(string? kind, string? school, string? look, bool onCaster)
        => WithCast(kind, school, look, onCaster) ?? Plain(kind, school);

    /// <summary>The school of the effect a lookalike spell of these tables puts on, or null for a spell not in them.</summary>
    internal static string? EffectSchoolOf(string? spell) {
        foreach (var table in new[] { s_plain, s_cast }) {
            foreach (var picks in table.Values) {
                foreach (var pick in picks) {
                    if (pick.Spell.Equals(spell?.Trim(), StringComparison.OrdinalIgnoreCase)) {
                        return pick.EffectSchool;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>"" or All: every school.</summary>
    internal static bool SameSchool(string? a, string? b) {
        static string N(string? s) => string.IsNullOrWhiteSpace(s) || s.Trim().Equals("All", StringComparison.OrdinalIgnoreCase) ? "all" : s.Trim().ToLowerInvariant();

        return N(a) == N(b);
    }

    /// <summary>How a kind is named in a sentence ("blade", "global").</summary>
    internal static string KindWord(string? kind) => KindOf(kind) switch {
        "healglobal" => "healing global", "gloomglobal" => "less-healing global", var k => k,
    };
}
