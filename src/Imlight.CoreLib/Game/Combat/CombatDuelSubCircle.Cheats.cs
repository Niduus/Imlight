/*
 * What cheats change on one fighter, for players and creatures alike (Niduus, 2026-10-26: "if possible, add it": hand
 * tricks, extra turns, pip gain and a school change; CombatResolver.CreativeCheats.cs has the Do steps that set these).
 *
 * Hand: the hand tricks wait for the fighter's next hand, as the server draws it (a wizard's at the start of the next
 * planning phase, so the game shows it then; a creature's when its AI picks its move, so it picks from it). Add puts a card
 * in first (in place of a random card when the hand is full), Shuffle puts the hand back in the deck first (DeckToo: the
 * used cards too), then the hand is drawn, then Lock keeps its cards out of it (back into the deck, the hand filling up from
 * the other cards) for the next Hands hands, and Discard takes cards out of it, used up.
 * Pips: PipGain pips a round (0: none) and PowerPipChance percent of them power pips, up to and with round PipUntil.
 * School: it counts as of School up to and with round SchoolUntil, for the cheats' school Ifs, spell requirements, school
 * pips, mastery and archmastery; with SchoolStats its boosts, resists and other school stats of its own school and the new
 * one trade places.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Combat;

public partial class CombatDuelSubCircle {

    /// <summary>A hand trick waiting for the next hand: Add, Shuffle, Discard or Lock.</summary>
    internal sealed record CheatHandRule(string Kind, int Count, string Spells, string Category, uint Card, bool DeckToo) {
        // Lock: how many more hands it keeps the cards out of.
        internal int Hands { get; set; }
    }

    internal List<CheatHandRule> CheatHandRules { get; } = [];

    internal int CheatPipGain { get; set; } = -1;
    internal int CheatPowerPipChance { get; set; } = -1;
    internal int CheatPipUntil { get; set; }

    internal string CheatSchool { get; set; } = "";
    internal int CheatSchoolUntil { get; set; }
    internal bool CheatSchoolStats { get; set; }

    private int CheatRound => (int) (_duelActor?.Duel?.m_roundNum ?? 0);

    private bool CheatPipsActive => (CheatPipGain >= 0 || CheatPowerPipChance >= 0) && CheatRound <= CheatPipUntil;

    /// <summary>The school a cheat makes it count as now, or null.</summary>
    internal MagicSchool? CheatSchoolNow
        => CheatSchool is { Length: > 0 } && CheatRound <= CheatSchoolUntil && Enum.TryParse<MagicSchool>(CheatSchool, true, out var s) && s != MagicSchool.None
            ? s : null;

    /// <summary>Its own school, as the battle knows it (a wizard's, or a creature's primary school).</summary>
    // As the cheats' school Ifs have read it: the stats' school, else the participant's primary school.
    internal MagicSchool OwnSchool => ParticipantGameStats?.MagicSchool is { } school and not MagicSchool.None ? school
        : CombatParticipant is { m_primaryMagicSchoolID: not 0 } participant ? (MagicSchool) participant.m_primaryMagicSchoolID
        : (MagicSchool) (ParticipantGameStats?.m_schoolID ?? 0);

    /// <summary>The school it counts as: a cheat's, else its own.</summary>
    internal MagicSchool SchoolNow => CheatSchoolNow ?? OwnSchool;

    // GetStatBySchool: with SchoolStats, its own school's stats and the new school's trade places.
    private string StatSchool(string magicSchool) {
        if (!CheatSchoolStats || CheatSchoolNow is not { } now) {
            return magicSchool;
        }

        var own = OwnSchool.ToString();

        return magicSchool.Equals(own, StringComparison.OrdinalIgnoreCase) ? now.ToString()
            : magicSchool.Equals(now.ToString(), StringComparison.OrdinalIgnoreCase) ? own : magicSchool;
    }

    // The pips a round and whether one is a power pip, as a cheat says, or null to go as usual.
    private int? CheatPipGains() => CheatPipsActive && CheatPipGain >= 0 ? CheatPipGain : null;

    private bool? CheatPowerPip() => CheatPipsActive && CheatPowerPipChance >= 0 ? Random.Shared.Next(100) < CheatPowerPipChance : null;

    // Whether a card in hand is one a hand trick names: by spell (names or IDs, any version) and kind of card.
    private static bool CardFits(Spell card, string spells, string category) => card is not null && CardFits(card.m_templateID, spells, category);

    private static bool CardFits(uint templateId, string spells, string category) {
        if (string.IsNullOrWhiteSpace(spells) && string.IsNullOrWhiteSpace(category)) {
            return true;
        }

        if (CoreObjectFactory.GetCoreTemplate(templateId) is not SpellTemplate template) {
            return false;
        }

        string name = template.m_name;
        string key = template.m_displayName;
        var nameOk = string.IsNullOrWhiteSpace(spells) || SpellNameFamily.IsOneOf(
            spells.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), true, name,
            string.IsNullOrEmpty(key) ? "" : Locale.GetEnglishName(key), templateId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        return nameOk && SpellNameFamily.CategoryMatches(category, CombatResolver.CategoriesOf(template));
    }

    // Add and Shuffle, before the hand is drawn.
    private void CheatHandBeforeDraw() {
        if (CheatHandRules.Count == 0 || _combatDeck is null) {
            return;
        }

        foreach (var rule in CheatHandRules.Where(r => r.Kind == "Shuffle").ToList()) {
            _combatDeck.ReturnHandToDeck();
            if (rule.DeckToo) {
                _combatDeck.Reshuffle();
            }

            CheatHandRules.Remove(rule);
            Logger.Information("Slot {0} | A cheat shuffles its hand back into its deck{1}.", Logger.Args(SlotIndex, rule.DeckToo ? ", used cards too" : ""));
        }

        foreach (var rule in CheatHandRules.Where(r => r.Kind == "Add").ToList()) {
            CheatHandRules.Remove(rule);
            for (var i = 0; i < Math.Max(1, rule.Count); i++) {
                if (SpellFactory.GetSpell(rule.Card) is not { } card) {
                    break;
                }

                // A full hand: the card takes the place of a random one.
                if (_combatDeck.HandRoom <= 0 && _combatDeck.LastGivenHand.Count > 0) {
                    _combatDeck.Discard(_combatDeck.LastGivenHand[Random.Shared.Next(_combatDeck.LastGivenHand.Count)]);
                }

                _combatDeck.AddExtraCardToHand(card);
            }

            Logger.Information("Slot {0} | A cheat puts {1} x{2} in its hand.", Logger.Args(SlotIndex, rule.Card, Math.Max(1, rule.Count)));
        }
    }

    // Discard and Lock, once the hand is drawn.
    private void CheatHandAfterDraw() {
        if (CheatHandRules.Count == 0 || _combatDeck is null) {
            return;
        }

        // Lock first: the locked cards go back into the deck, and the hand fills up again from the cards not locked.
        var locks = CheatHandRules.Where(r => r.Kind == "Lock").ToList();
        if (locks.Count > 0) {
            var gone = _combatDeck.LastGivenHand.Where(c => locks.Any(r => CardFits(c, r.Spells, r.Category))).ToList();
            foreach (var card in gone) {
                _combatDeck.ReturnCardToDeck(card);
            }

            _combatDeck.FillHandFrom(id => !locks.Any(r => CardFits(id, r.Spells, r.Category)));
            Logger.Information("Slot {0} | A cheat keeps {1} cards out of its hand.", Logger.Args(SlotIndex, gone.Count));
            foreach (var rule in locks.Where(r => --r.Hands <= 0)) {
                CheatHandRules.Remove(rule);
            }
        }

        // Discard: the cards are used up, and the hand stays smaller until the next one.
        foreach (var rule in CheatHandRules.Where(r => r.Kind == "Discard").ToList()) {
            var fits = _combatDeck.LastGivenHand.Where(c => CardFits(c, rule.Spells, rule.Category)).OrderBy(_ => Random.Shared.Next()).ToList();
            var gone = rule.Count <= 0 ? fits : [.. fits.Take(rule.Count)];
            foreach (var card in gone) {
                _combatDeck.DropFromHand(card);
            }

            Logger.Information("Slot {0} | A cheat discards {1} cards of its hand.", Logger.Args(SlotIndex, gone.Count));
            CheatHandRules.Remove(rule);
        }
    }

}
