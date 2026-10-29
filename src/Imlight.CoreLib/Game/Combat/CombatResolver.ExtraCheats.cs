/*
 * More Dos from the cheat ideas list (Niduus, 2026-10-28: "approved", the list "Cheat ideas the server could do"). Each
 * works in a player's cheats and a creature's alike.
 *
 *   ConvertEffects  Count (0: all) of Target's (Self) effects of Kind (and EffectSchool, Spell) come off, and each comes back
 *                   as one of To (Blade, Weakness, Shield, Trap, Absorb) of the same size and school, drawn by the game as
 *                   "add an effect of your own size" draws one. "Turn every trap on me into a blade." ToMe: the new ones go on
 *                   itself instead: "take every Death shield off the enemy, gain a Death blade of each one's size".
 *   CopyEffects     Count (0: all) of Target's (Caster) effects of Kind are copied onto it (Self), the same size and school;
 *                   Target keeps them.
 *   ScaleEffects    Target's (Self) effects of Kind get Amount percent of their size (200: double, 50: half). Only the
 *                   server knows: the game keeps showing the old number, but hits count the new one.
 *   EffectsSchool   Target's (Self) effects of Kind become of School ("" or All: universal). Server only, as above.
 *   ProtectEffects  Target's (Self) effects of Kind can't be removed (Kind Off: they can again).
 *   Damage          Target (Caster) loses Amount health (Percent: a share of its most health), never below 1.
 *   Drain           Target (Caster) loses Amount health (Percent: of its most) and it gets as much back.
 *   Revive          A fallen one on its side (Target AllAllies: all of them, Self: itself) gets up with Amount health
 *                   (Percent: of its most).
 *   Detonate        Target's (Caster) damage/heal over time and bombs of Kind (empty: all) go off at once with all the
 *                   rounds they have left (Amount percent of it; 100 if 0), and are gone.
 *   HitShare        Kind Thorns (who hits Target takes Amount % back, never below 1), Lifesteal (Target heals Amount % of
 *                   what it deals) or HealFromHits (Amount % of each hit heals Target instead), for Rounds (0: the battle).
 * Ifs: Cards (Kind Hand, Deck, Treasure, TreasureInHand) and LastHit (Kind Taken or Dealt): how much the last hit did.
 *
 * Whens after each move (the move's spell is "the spell that set it off"; "the attacker" the one who moved, "the one hit"
 * the one it happened to). RemovedFrom picks whose ("" itself, AnyAlly, AnyEnemy, Anyone), RemovedKind and RemovedSpell
 * which effects, as for EffectRemoved:
 *   EffectAdded     a charm, ward, aura or over-time is put on (after the spell lands, unlike EnemyCasts).
 *   EffectUsed      one is used up by a hit or a heal (a shield popped, a blade spent), not taken off by a spell.
 *   Healed          its health went up.         Stunned: it was stunned by the move.
 *   CardPlayed      the mover played a card of RemovedKind (Treasure, Item, Deck; "" any), of RemovedSpell if named.
 *   Summoned        someone came into the battle during the move (not there before it).
 *   OverTimeTick    a damage or heal over time (or a bomb) ticked on someone (RemovedKind DoT / HoT).
 * Countdown: a number shown once a round, counting down from Amount (Text with {n}; Style Chat, Box, Both).
 * Sound: a game sound (template ID or file name, Text) played for the wizards in the battle (you, in a player cheat).
 * StealCard: Count cards (random, or of Spell) of Target's (Caster) hand go into its own next hand.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.WizardData.Models.World;
using Imlight.CoreLib.Game.Tools;
using Imcodec.Types;

namespace Imlight.CoreLib.Game.Combat;

public partial class CombatResolver {

    private bool TryExtraCheatSteps(CombatDuelSubCircle boss, BossCheats.BossCheat cheat, BossCheats.CheatAction step,
                                    CombatDuelSubCircle trigger, CombatDuelSubCircle receiver, string bossName,
                                    CombatActionListObj combatActionList, float cinematicTimeSoFar, bool custom, out float cinematicTime) {
        cinematicTime = 0;
        List<CombatDuelSubCircle> On(string fallback) => StepTargets(boss, step.Target.Length > 0 ? step.Target : fallback, null, trigger, receiver);
        var kinds = (step.Kind ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool Fits(SpellEffect e) => e is not null && (kinds.Length == 0 || kinds.Any(k => IsKind(e, k)))
                                    && EffectSchoolIs(e, step.EffectSchool) && FromSpell(e, step.Spell);
        var duel = _duel.m_duelID.Full;

        switch (step.Do.ToLowerInvariant()) {
            case "converteffects":
            case "copyeffects": {
                var copying = step.Is("CopyEffects");
                var redo = new List<BossCheats.CheatAction>();
                foreach (var target in On(copying ? "Caster" : "Self").Where(t => t?._hangingEffects is not null && t.IsAlive)) {
                    var picked = target._hangingEffects.Where(e => Fits(e) && (copying || !e.m_protected) && SizedKind(e) is not null)
                        .Reverse().Take(step.Count > 0 ? step.Count : int.MaxValue).ToList();
                    if (!copying) {
                        // The game is shown them coming off too, or it keeps drawing them (Niduus, 2026-10-29: "the convert
                        // effect doesn't remove the target convert").
                        cinematicTime += ShowRemoval(boss, cheat, step, target, picked, trigger, receiver, bossName, combatActionList,
                            cinematicTimeSoFar + cinematicTime, custom);
                        var gone = picked.ToHashSet(ReferenceEqualityComparer.Instance);
                        target._hangingEffects.RemoveAll(gone.Contains);
                        _clearedCount += gone.Count;
                    }

                    foreach (var e in picked) {
                        var school = ((string) e.m_sDamageType ?? "").Trim();
                        redo.Add(new BossCheats.CheatAction {
                            Do = "AddCharm",
                            Amount = Math.Abs(e.m_effectParam),
                            School = school.Equals("All", StringComparison.OrdinalIgnoreCase) ? "" : school,
                            Kind = copying ? SizedKind(e) : (step.To ?? "").Trim().Length > 0 ? step.To.Trim() : "Blade",
                            Target = copying || step.ToMe ? $"slot:{boss.SlotIndex}" : $"slot:{target.SlotIndex}",
                            Silent = step.Silent,
                            Tag = step.Tag,
                        });
                    }

                    Logger.Information("Duel {0} | Slot {1} | {2}: {3} {4} effects of slot {5}{6}.",
                        Logger.Args(duel, boss.SlotIndex, bossName, copying ? "copies" : "converts", picked.Count, target.SlotIndex,
                            copying ? "" : $" into {step.To}"));
                }

                if (redo.Count > 0) {
                    cinematicTime += RunCheatStepsInOrder(boss, cheat, redo, trigger, receiver, bossName, custom, combatActionList,
                        cinematicTimeSoFar + cinematicTime);
                }

                return true;
            }
            case "scaleeffects": {
                var percent = step.Amount;
                foreach (var target in On("Self").Where(t => t?._hangingEffects is not null)) {
                    var hanging = target._hangingEffects;
                    var scaled = 0;
                    for (var i = 0; i < hanging.Count; i++) {
                        if (Fits(hanging[i])) {
                            hanging[i] = hanging[i] with {
                                m_effectParam = (int) Math.Round(hanging[i].m_effectParam * percent / 100.0),
                                m_paramPerRound = (int) Math.Round(hanging[i].m_paramPerRound * percent / 100.0),
                            };
                            scaled++;
                        }
                    }

                    Logger.Information("Duel {0} | Slot {1} | {2}: {3} effects of slot {4} are now {5}% of their size.",
                        Logger.Args(duel, boss.SlotIndex, bossName, scaled, target.SlotIndex, percent));
                }

                return true;
            }
            case "effectsschool": {
                var school = (step.School ?? "").Trim();
                school = school.Length == 0 || school.Equals("Universal", StringComparison.OrdinalIgnoreCase) ? "All" : school;
                foreach (var target in On("Self").Where(t => t?._hangingEffects is not null)) {
                    var hanging = target._hangingEffects;
                    for (var i = 0; i < hanging.Count; i++) {
                        if (Fits(hanging[i])) {
                            hanging[i] = hanging[i] with { m_sDamageType = school };
                        }
                    }
                }

                Logger.Information("Duel {0} | {1}: effects now of {2}.", Logger.Args(duel, bossName, school));

                return true;
            }
            case "protecteffects": {
                var off = (step.To ?? "").Trim().Equals("Off", StringComparison.OrdinalIgnoreCase);
                foreach (var target in On("Self").Where(t => t?._hangingEffects is not null)) {
                    var hanging = target._hangingEffects;
                    for (var i = 0; i < hanging.Count; i++) {
                        if (Fits(hanging[i])) {
                            hanging[i] = hanging[i] with { m_protected = !off };
                        }
                    }
                }

                return true;
            }
            case "damage":
            case "drain": {
                var drained = 0;
                foreach (var target in On("Caster").Where(t => t?.ParticipantGameStats is not null && t.IsAlive)) {
                    var now = target.ParticipantGameStats.m_currentHitpoints;
                    var amount = Math.Max(0, step.Percent ? MostHealth(target) * step.Amount / 100 : step.Amount);
                    var taken = Math.Min(amount, Math.Max(0, now - 1));
                    if (taken > 0) {
                        SetHealthTo(target, now - taken);
                        drained += taken;
                    }

                    Logger.Information("Duel {0} | Slot {1} | {2}: slot {3} loses {4} health.",
                        Logger.Args(duel, boss.SlotIndex, bossName, target.SlotIndex, taken));
                }

                if (step.Is("Drain") && drained > 0) {
                    HealFromCheat(boss, drained);
                }

                return true;
            }
            case "spellrule": {
                // Kind Hit / Fizzle / Crit / NoCrit for Target's (Self) next Count spells (0: the rest of the battle).
                var rule = (step.Kind ?? "").Trim();
                var left = step.Count > 0 ? step.Count : -1;
                foreach (var target in On("Self").Where(t => t is not null && t.IsAlive)) {
                    if (rule.Equals("Hit", StringComparison.OrdinalIgnoreCase) || rule.Equals("Fizzle", StringComparison.OrdinalIgnoreCase)) {
                        (target.CheatHitRule, target.CheatHitLeft) = (char.ToUpperInvariant(rule[0]) + rule[1..].ToLowerInvariant(), left);
                    }
                    else if (rule.Equals("Crit", StringComparison.OrdinalIgnoreCase)) {
                        (target.CheatCritRule, target.CheatCritLeft) = ("Crit", left);
                    }
                    else if (rule.Equals("NoCrit", StringComparison.OrdinalIgnoreCase)) {
                        (target.CheatCritRule, target.CheatCritLeft) = ("NoCrit", left);
                    }
                    else if (rule.Equals("Off", StringComparison.OrdinalIgnoreCase)) {
                        (target.CheatHitRule, target.CheatHitLeft, target.CheatCritRule, target.CheatCritLeft) = ("", 0, "", 0);
                    }

                    Logger.Information("Duel {0} | Slot {1} | {2}: slot {3}'s next {4} spells: {5}.",
                        Logger.Args(duel, boss.SlotIndex, bossName, target.SlotIndex, left < 0 ? "all" : left.ToString(), rule));
                }

                return true;
            }
            case "reward": {
                // Target's wizards (every wizard in the battle, or you in a player cheat) get Gold, Xp, Tp and Items at once,
                // as a drop would give them, and a chat line saying so.
                var items = Words(step.Items).Select(i => new DropItemResult { ItemId = i, Quantity = Math.Max(1, step.Count) }).ToList();
                foreach (var target in On(boss.IsHumanPlayer ? "Self" : "AllPlayers").Where(t => t?._wizard is not null && t.ParticipantActor is not null)) {
                    var result = new DropTableResult {
                        GoldAmount = Math.Max(0, step.Gold), ExperienceAmount = Math.Max(0, step.Xp), TrainingPoints = Math.Max(0, step.Tp),
                        Items = [.. items.Select(i => new DropItemResult { ItemId = i.ItemId, Quantity = i.Quantity })],
                    };
                    LootGranter.Grant(target.ParticipantActor, target._wizard, result);
                    var got = string.Join(", ", new[] {
                        step.Gold > 0 ? $"{step.Gold} gold" : "", step.Xp > 0 ? $"{step.Xp} experience" : "", step.Tp > 0 ? $"{step.Tp} training points" : "",
                        result.Items.Count > 0 ? $"{result.Items.Count} item(s)" : "",
                    }.Where(t => t.Length > 0));
                    if (got.Length > 0) {
                        target.ParticipantActor.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = $"Reward: {got}.", Modal = 0 });
                    }

                    Logger.Information("Duel {0} | Slot {1} | {2}: rewards slot {3} with {4}.", Logger.Args(duel, boss.SlotIndex, bossName, target.SlotIndex, got));
                }

                return true;
            }
            case "noflee":
                // Target (everyone) can't flee for the rest of the battle (Kind Off: can again).
                foreach (var target in On("Everyone").Where(t => t is not null)) {
                    target.CheatNoFlee = !(step.Kind ?? "").Trim().Equals("Off", StringComparison.OrdinalIgnoreCase);
                }

                return true;
            case "handsize":
                // Target's (Self) hand is refilled up to Amount cards (0-7) for the next Rounds hands (0: the rest of the battle).
                foreach (var target in On("Self").Where(t => t?._combatDeck is not null && t.IsAlive)) {
                    target._combatDeck.CheatHandSize = Math.Clamp(step.Amount, 0, 7);
                    target._combatDeck.CheatHandRounds = step.Rounds > 0 ? step.Rounds : -1;
                    Logger.Information("Duel {0} | Slot {1} | {2}: slot {3}'s hand size is {4}.",
                        Logger.Args(duel, boss.SlotIndex, bossName, target.SlotIndex, target._combatDeck.CheatHandSize));
                }

                return true;
            case "revive": {
                // Fallen ones on its side (Target: "" one at random, AllAllies all of them, Self itself) get up with Amount
                // health (Percent: a share of their most).
                var fallen = boss._duelActor.ActiveSubCircles
                    .Where(c => c is { Occupied: true, AddedToDuel: true, IsAlive: false } && c.ParticipantGameStats is not null && c.OccupiedTeam == boss.OccupiedTeam).ToList();
                var who = (step.Target ?? "").Trim().ToLowerInvariant();
                var picked = who switch {
                    "self" or "me" => fallen.Where(c => c == boss).ToList(),
                    "allallies" or "allies" => fallen,
                    _ => fallen.Count > 0 ? [fallen[Random.Shared.Next(fallen.Count)]] : [],
                };
                foreach (var target in picked) {
                    var most = Math.Max(1, MostHealth(target));
                    var health = Math.Clamp(step.Percent ? most * step.Amount / 100 : step.Amount, 1, most);
                    target.SetHealthTo(health);
                    Logger.Information("Duel {0} | Slot {1} | {2}: slot {3} gets up with {4} health.",
                        Logger.Args(duel, boss.SlotIndex, bossName, target.SlotIndex, health));
                }

                return true;
            }
            case "detonate": {
                // Target's (Caster) damage and heal over time (and bombs) of Kind go off at once with all their rounds
                // left, Amount percent of it (100 if 0), and are gone.
                foreach (var target in On("Caster").Where(t => t?._hangingEffects is not null && t.IsAlive)) {
                    var overTime = target._hangingEffects.Where(e => e is not null && CombatRequirements.IsOverTime(e) && Fits(e))
                        .Reverse().Take(step.Count > 0 ? step.Count : int.MaxValue).ToList();
                    var share = step.Amount > 0 ? step.Amount : 100;
                    foreach (var e in overTime) {
                        var left = e.m_effectType == kSpellEffects.kDeferredDamage ? e.m_paramPerRound : e.m_paramPerRound * Math.Max(1, e.m_numRounds);
                        left = left * share / 100;
                        if (e.m_effectType == kSpellEffects.kHealOverTime) {
                            HealFromCheat(target, left);
                        }
                        else if (left > 0) {
                            CombatEffectApplicator.TickDamage(target, e, left);
                            target.SyncParticipantHealth();
                        }
                    }

                    CombatCharms.RemoveExact(target._hangingEffects, overTime);
                    Logger.Information("Duel {0} | Slot {1} | {2}: {3} over-time effects on slot {4} go off at once.",
                        Logger.Args(duel, boss.SlotIndex, bossName, overTime.Count, target.SlotIndex));
                }

                return true;
            }
            case "hitshare": {
                // Kind Thorns / Lifesteal / HealFromHits on Target (Self): Amount percent for Rounds rounds (0: the rest of
                // the battle); 0 percent takes it off.
                var kind = (step.Kind ?? "").Trim();
                if (kind.Length == 0) {
                    return true;
                }

                var until = step.Rounds > 0 ? (int) _duel.m_roundNum + step.Rounds - 1 : 0;
                foreach (var target in On("Self").Where(t => t is not null)) {
                    if (step.Amount == 0) {
                        target.CheatHitShares.Remove(kind);
                    }
                    else {
                        target.CheatHitShares[kind] = (step.Amount, until);
                    }

                    Logger.Information("Duel {0} | Slot {1} | {2}: slot {3} {4} {5}%.", Logger.Args(duel, boss.SlotIndex, bossName, target.SlotIndex, kind, step.Amount));
                }

                return true;
            }
            case "stealcard": {
                // Count random cards (or of Spell / Kind) of Target's (Caster) hand leave it at the next hand and go into
                // its own (yours, in a player cheat) next hand.
                foreach (var target in On("Caster").Where(t => t?._combatDeck?.LastGivenHand is not null && t.IsAlive && t != boss)) {
                    var cards = target._combatDeck.LastGivenHand.Where(c => c is not null && (step.Spell.Length == 0 || MatchesCard(c, step.Spell))).ToList();
                    for (var n = 0; n < Math.Clamp(step.Count, 1, 7) && cards.Count > 0; n++) {
                        var card = cards[Random.Shared.Next(cards.Count)];
                        cards.Remove(card);
                        target.CheatHandRules.Add(new CombatDuelSubCircle.CheatHandRule("Discard", 1, card.m_templateID.ToString(), "", 0, false));
                        boss.CheatHandRules.Add(new CombatDuelSubCircle.CheatHandRule("Add", 1, "", "", card.m_templateID, false));
                        Logger.Information("Duel {0} | Slot {1} | {2}: steals card {3} from slot {4}.",
                            Logger.Args(duel, boss.SlotIndex, bossName, card.m_templateID, target.SlotIndex));
                    }
                }

                return true;
            }
            case "countdown": {
                // A number counting down once a round, shown with Text ({n} is the number) as Style (Chat, Box, Both) to the
                // wizards in the battle (you alone, in a player cheat): Amount now, one less each round, gone after 1.
                var start = Math.Clamp(step.Amount, 1, 99);
                var text = (step.Text ?? "").Trim().Length > 0 ? step.Text.Trim() : "{n}";
                _countdowns.Add(new Countdown(boss, text, step.Style ?? "", start));
                ShowCountdown(_countdowns[^1]);

                return true;
            }
            case "givecheat":
                return GiveCheat(boss, step, On("MyMinions"), bossName);
            case "sound": {
                // A game sound (its template ID, or a sound file name the game knows) played for Target's wizards (every
                // wizard in the battle; you, in a player cheat).
                var sound = (step.Text ?? "").Trim();
                if (sound.Length == 0) {
                    return true;
                }

                // As the game's own sound results take it: a file (with its extension), a template ID, or part of a sound
                // template's file name.
                GAME_5_PROTOCOL.MSG_PLAYSOUND message;
                if (System.IO.Path.HasExtension(sound)) {
                    message = new GAME_5_PROTOCOL.MSG_PLAYSOUND { SoundID = new GID(), SoundFilename = sound };
                }
                else if (uint.TryParse(sound, out var id)
                         || CoreObjectFactory.TryGetCoreTemplateID(x => x.m_filename.ToString().Contains(sound, StringComparison.OrdinalIgnoreCase), out id)) {
                    message = new GAME_5_PROTOCOL.MSG_PLAYSOUND { SoundID = new GID(id), SoundFilename = "" };
                }
                else {
                    Logger.Warning("Duel {0} | {1}: no sound \"{2}\" was found.", Logger.Args(duel, bossName, sound));

                    return true;
                }
                var fallback = boss.IsHumanPlayer ? "Self" : "Everyone";
                var to = On(fallback);
                foreach (var wizard in to.Where(t => t is { IsHumanPlayer: true, ParticipantActor: not null }).Distinct()) {
                    wizard.ParticipantActor.Tell(message);
                }

                Logger.Information("Duel {0} | Slot {1} | {2}: plays sound {3}.", Logger.Args(duel, boss.SlotIndex, bossName, sound));

                return true;
            }
            default:
                return false;
        }
    }

    // ---- Countdown: a number shown once a round, counting down. ----
    private sealed class Countdown(CombatDuelSubCircle owner, string text, string style, int left) {
        internal CombatDuelSubCircle Owner { get; } = owner;
        internal string Text { get; } = text;
        internal string Style { get; } = style;
        internal int Left { get; set; } = left;
    }

    private readonly List<Countdown> _countdowns = [];

    private void CountdownsAtRoundStart() {
        foreach (var countdown in _countdowns.ToList()) {
            countdown.Left--;
            if (countdown.Left <= 0 || countdown.Owner is not { Occupied: true }) {
                _countdowns.Remove(countdown);
                continue;
            }

            ShowCountdown(countdown);
        }
    }

    private void ShowCountdown(Countdown countdown) {
        var text = AnnounceText(countdown.Text.Replace("{n}", countdown.Left.ToString(), StringComparison.OrdinalIgnoreCase), countdown.Owner, null);
        var style = countdown.Style.Trim().ToLowerInvariant();
        var box = style is "box" or "both";
        var chat = style is "" or "chat" or "both";
        var wizards = countdown.Owner.IsHumanPlayer
            ? [countdown.Owner]
            : _subCircles.Where(c => c is { Occupied: true, IsHumanPlayer: true }).ToList();
        foreach (var wizard in wizards.Where(w => w.ParticipantActor is not null)) {
            if (box) {
                wizard.ParticipantActor.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = ManagerCommands.AnnouncementBox(text), Modal = 1 });
            }

            if (chat) {
                wizard.ParticipantActor.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = text, Modal = 0 });
            }
        }
    }

    // ---- When a damage or heal over time (or a bomb) ticks: OverTimeTick, on RemovedFrom ("" itself, AnyAlly, AnyEnemy,
    // Anyone), RemovedKind DoT / HoT ("" either), RemovedSpell. "The one hit" and "the attacker" are the one it ticked on. ----
    private readonly List<SpellEffect> _ticked = [];

    private float OverTimeTickCheats(CombatDuelSubCircle ticked, CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        var cinematicTime = 0.0f;
        if (_ticked.Count == 0 || ticked is null) {
            return 0;
        }

        var ticks = _ticked.ToList();
        foreach (var (boss, cheats) in BossesWithCheats().ToList()) {
            for (var i = 0; i < cheats.Count; i++) {
                var cheat = cheats[i];
                if (!cheat.Is("OverTimeTick") || !InPhase(boss, cheat)) {
                    continue;
                }

                var from = (cheat.RemovedFrom ?? "").Trim().ToLowerInvariant();
                var fits = from switch {
                    "anyally" => ticked.OccupiedTeam == boss.OccupiedTeam,
                    "anyenemy" => ticked.OccupiedTeam != boss.OccupiedTeam,
                    "anyone" => true,
                    _ => ticked == boss,
                };
                var kinds = (cheat.RemovedKind ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var tick = ticks.FirstOrDefault(e => (kinds.Length == 0 || kinds.Any(k => IsKind(e, k))) && FromSpell(e, cheat.RemovedSpell));
                if (!fits || tick is null) {
                    continue;
                }

                _cheatSpellId = tick.m_spellTemplateID;
                cinematicTime += TryBossCheat(boss, i, cheat, ticked, ticked, $"an over-time effect ticked on slot {ticked.SlotIndex}",
                    combatActionList, cinematicTimeSoFar + cinematicTime);
                _cheatSpellId = 0;
            }
        }

        return cinematicTime;
    }

    // A card in a hand is one of these spells (names or numbers, commas between).
    private static bool MatchesCard(Spell card, string names) {
        var template = CoreObjectFactory.GetCoreTemplate(card.m_templateID) as SpellTemplate;
        string name = template?.m_name;
        string key = template?.m_displayName;
        var shown = string.IsNullOrEmpty(key) ? "" : Locale.GetEnglishName(key);

        return Words(names).Any(n => SpellNameFamily.IsSpellId(n, out var id) ? card.m_templateID == id : SpellNameFamily.AnyInFamily(n, name, shown));
    }

    // How many cards: Hand in the hand, Deck left in the deck (with the hand), Treasure treasure cards left.
    private static int CardsOf(CombatDuelSubCircle who, string kind) {
        if (who?._combatDeck is not { } deck) {
            return 0;
        }

        return (kind ?? "").Trim().ToLowerInvariant() switch {
            "deck" => deck.RemainingCardCount,
            "treasure" => deck.VaultRemainingCount,
            "treasureinhand" => deck.TreasureCardsInHand,
            _ => deck.LastGivenHand?.Count ?? 0,
        };
    }

    // Everyone's health and stun before the move, for Healed and Stunned.
    private Dictionary<CombatDuelSubCircle, (int Health, int Stunned)> _statsBeforeMove;

    private static bool IsAfterMoveWhen(BossCheats.BossCheat c)
        => c.Is("EffectAdded") || c.Is("EffectUsed") || c.Is("Healed") || c.Is("Stunned") || c.Is("CardPlayed") || c.Is("Summoned");

    // The kind of card a move played: Treasure, Item or Deck (a card from the deck).
    private static bool CardIs(Spell card, string kind) => (kind ?? "").Trim().ToLowerInvariant() switch {
        "" => true,
        "treasure" => card.m_treasureCard,
        "item" => card.m_itemCard,
        "deck" => !card.m_treasureCard && !card.m_itemCard,
        _ => false,
    };

    private void NoteStatsBeforeMove()
        => _statsBeforeMove = _subCircles.Where(c => c is not null && c.Occupied && c.ParticipantGameStats is not null)
            .ToDictionary(c => c, c => (c.ParticipantGameStats.m_currentHitpoints, c.CombatParticipant?.m_stunned ?? 0));

    // The effects on someone now that were not before the move (two alike count as two).
    private List<SpellEffect> AddedTo(CombatDuelSubCircle who) {
        if (_effectsBeforeMove is null || !_effectsBeforeMove.TryGetValue(who, out var before)) {
            return [];
        }

        var had = before.ToList();
        var added = new List<SpellEffect>();
        foreach (var e in (who._hangingEffects ?? []).Where(e => e is not null)) {
            var i = had.IndexOf(e);
            if (i >= 0) {
                had.RemoveAt(i);
            }
            else {
                added.Add(e);
            }
        }

        return added;
    }

    private float AfterMoveWhenCheats(CombatDuelSubCircle mover, List<(CombatAction Action, SpellTemplate Spell)> landed, bool removingSpell,
                                      CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        var cinematicTime = 0.0f;
        if (_effectsBeforeMove is null) {
            return 0;
        }

        var spell = landed.FirstOrDefault(m => m.Spell is not null);
        foreach (var (boss, cheats) in BossesWithCheats().ToList()) {
            for (var i = 0; i < cheats.Count; i++) {
                var cheat = cheats[i];
                if (!InPhase(boss, cheat) || cheat.Interrupts || !IsAfterMoveWhen(cheat)) {
                    continue;
                }

                if (cheat.Is("CardPlayed") && mover is not null && cheat.RemovedFrom is not ("Anyone" or "AnyAlly" or "AnyEnemy") && mover != boss) {
                    continue;
                }

                var kinds = (cheat.RemovedKind ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                bool Fits(SpellEffect e) => (kinds.Length == 0 || kinds.Any(k => IsKind(e, k))) && FromSpell(e, cheat.RemovedSpell);
                var from = (cheat.RemovedFrom ?? "").Trim().ToLowerInvariant();
                var who = from switch {
                    "anyally" => _subCircles.Where(c => c is not null && c.Occupied && c.OccupiedTeam == boss.OccupiedTeam),
                    "anyenemy" => _subCircles.Where(c => c is not null && c.Occupied && c.OccupiedTeam != boss.OccupiedTeam),
                    "anyone" => _subCircles.Where(c => c is not null && c.Occupied),
                    _ => [boss],
                };
                CombatDuelSubCircle hit = null;
                var what = "";
                foreach (var c in who) {
                    var stats = _statsBeforeMove?.GetValueOrDefault(c) ?? default;
                    if (cheat.Is("EffectAdded") && AddedTo(c).Count(Fits) is > 0 and var added) {
                        (hit, what) = (c, $"{added} effects put on slot {c.SlotIndex}");
                    }
                    else if (cheat.Is("EffectUsed") && !removingSpell && GoneFrom(c).Count(Fits) is > 0 and var used) {
                        (hit, what) = (c, $"{used} effects of slot {c.SlotIndex} used up");
                    }
                    else if (cheat.Is("Healed") && _statsBeforeMove is not null && c.ParticipantGameStats?.m_currentHitpoints > stats.Health && stats.Health > 0) {
                        (hit, what) = (c, $"slot {c.SlotIndex} healed");
                    }
                    else if (cheat.Is("Stunned") && _statsBeforeMove is not null && stats.Stunned <= 0 && (c.CombatParticipant?.m_stunned ?? 0) > 0) {
                        (hit, what) = (c, $"slot {c.SlotIndex} stunned");
                    }
                    else if (cheat.Is("CardPlayed") && c == mover && spell.Spell is not null && spell.Action.m_spell is { } card && CardIs(card, cheat.RemovedKind)
                             && (string.IsNullOrWhiteSpace(cheat.RemovedSpell) || MatchesCard(card, cheat.RemovedSpell))) {
                        (hit, what) = (c, $"slot {c.SlotIndex} played a {(card.m_treasureCard ? "treasure" : card.m_itemCard ? "item" : "deck")} card");
                    }
                    else if (cheat.Is("Summoned") && _statsBeforeMove is not null && !_statsBeforeMove.ContainsKey(c) && c.IsAlive) {
                        (hit, what) = (c, $"slot {c.SlotIndex} came into the battle");
                    }

                    if (hit is not null) {
                        break;
                    }
                }

                if (hit is null) {
                    continue;
                }

                _cheatSpellId = spell.Spell is not null ? spell.Action.m_spell.m_templateID : 0;
                _cheatCard = spell.Spell is not null ? spell.Action.m_spell : null;
                cinematicTime += TryBossCheat(boss, i, cheat, mover, hit, what, combatActionList, cinematicTimeSoFar + cinematicTime);
                _cheatSpellId = 0;
                _cheatCard = null;
            }
        }

        return cinematicTime;
    }

    // ---- Ifs (CreativeCheats' condition switch) ----
    //   Stat     Who's Kind (Damage, Resist, Pierce, Critical, Block, Accuracy) for School ("" all) from gear and stats, Op Value
    //            (percent; rating for Critical and Block).
    //   Status   Who is Kind: Stunned, Confused, Polymorphed (a form), Shadowed (a shadow creature or form).
    //   Time     the server PC's time: Kind Hour, Month, Day (of the month) Op Value; Weekday: one of Name (Monday, ...).
    //   Wears    Who (a wizard) has an item on (gear, pet, mount) whose id or name holds one of Name (commas).
    //   Quest    Who (a wizard) has finished one of the quests Name (commas).   Badge: has one of the badges Name (ids).

    private static List<string> Words(string text)
        => [.. (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static double StatOf(CombatDuelSubCircle who, string kind, string school) {
        if (who?.ParticipantGameStats is not { } stats) {
            return 0;
        }

        var row = (kind ?? "").Trim();
        school = (school ?? "").Trim();
        if (row.Equals("Accuracy", StringComparison.OrdinalIgnoreCase)) {
            var index = MagicSchools.All.FirstOrDefault(s => ((string) s.m_schoolName).Equals(school, StringComparison.OrdinalIgnoreCase))?.m_schoolIndex;
            var bySchool = index is { } i && stats.m_accBonusPercent is { } list && i < list.Count ? list[i] : 0f;

            return CombatEffectApplicator.AsFraction(bySchool + stats.m_accBonusPercentAll) * 100f;
        }

        return BossOverrides.GameValue(stats, row.Length > 0 ? char.ToUpperInvariant(row[0]) + row[1..].ToLowerInvariant() : "Damage", school);
    }

    private static bool HasCheatStatus(CombatDuelSubCircle who, string kind) => (kind ?? "").Trim().ToLowerInvariant() switch {
        "stunned" => who?.CombatParticipant?.m_stunned > 0,
        "confused" => who?.CombatParticipant?.m_confused > 0,
        "polymorphed" => who?.CombatParticipant?.m_polymorphSpellTemplateID is > 0,
        _ => false,
    };

    private static bool TimeHolds(BossCheats.CheatCondition c) {
        var now = DateTime.Now;
        switch ((c.Kind ?? "").Trim().ToLowerInvariant()) {
            case "weekday":
                return Words(c.Name).Any(d => now.DayOfWeek.ToString().StartsWith(d, StringComparison.OrdinalIgnoreCase));
            case "month":
                return Compare(now.Month, c.Op, c.Value);
            case "day":
                return Compare(now.Day, c.Op, c.Value);
            default:
                return Compare(now.Hour, c.Op, c.Value);
        }
    }

    private static bool Wears(CombatDuelSubCircle who, string names) {
        var items = who?._wizard?.EquipmentBehavior?.EquippedItems;
        var words = Words(names);
        if (items is null || words.Count == 0) {
            return false;
        }

        foreach (var item in items.Where(i => i is not null)) {
            var id = item.m_templateID.Full.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var template = CoreObjectFactory.GetCoreTemplate(item.m_templateID.Full) as GameObjectTemplate;
            string objectName = template?.m_objectName ?? "";
            string key = template?.m_displayName ?? "";
            var shown = key.Length > 0 ? Locale.GetEnglishName(key) ?? "" : "";
            if (words.Any(w => w == id || objectName.Contains(w, StringComparison.OrdinalIgnoreCase) || shown.Contains(w, StringComparison.OrdinalIgnoreCase))) {
                return true;
            }
        }

        return false;
    }

    // What "add an effect of your own size" calls an effect: Blade, Weakness, Shield, Trap or Absorb; null for any other.
    // The game works the round out itself and draws only what it is shown cast: effects the server takes off with no spell
    // stay on in the game. So a real removal spell (Enfeeble-like for blades, Pierce-like for shields...) is cast on the
    // target for each kind taken, told to take that many, and the server then keeps exactly its own pick off. The game
    // takes the newest of that kind, so when newer ones of another school sit on top it may show others gone.
    private float ShowRemoval(CombatDuelSubCircle boss, BossCheats.BossCheat cheat, BossCheats.CheatAction step, CombatDuelSubCircle target,
                              List<SpellEffect> picked, CombatDuelSubCircle trigger, CombatDuelSubCircle receiver, string bossName,
                              CombatActionListObj combatActionList, float cinematicTimeSoFar, bool custom) {
        if (picked.Count == 0 || combatActionList is null) {
            return 0;
        }

        var before = target._hangingEffects.ToList();
        var cinematicTime = 0.0f;
        var onEnemy = target.OccupiedTeam != boss.OccupiedTeam;
        foreach (var group in picked.GroupBy(e => (Charm: s_charmEffects.Contains(e.m_effectType), Good: CombatRequirements.IsBeneficial(e)))) {
            var look = RemovalLook(group.Key.Charm ? kSpellEffects.kRemoveCharm : kSpellEffects.kRemoveWard, group.Key.Good, onEnemy, target == boss);
            if (look is null) {
                Logger.Information("Duel {0} | {1}: no game spell takes off {2} {3}s on that side, so the game keeps showing them.",
                    Logger.Args(_duel.m_duelID.Full, bossName, group.Key.Good ? "good" : "bad", group.Key.Charm ? "charm" : "ward"));
                continue;
            }

            var sentBefore = combatActionList.m_actionList.Count;
            cinematicTime += CheatCast(boss, cheat, new BossCheats.CheatAction { Do = "Cast", Spell = look.Value.Name, Target = $"slot:{target.SlotIndex}", Silent = step.Silent },
                trigger, receiver, bossName, [], combatActionList, cinematicTimeSoFar + cinematicTime, custom);
            for (var i = sentBefore; i < combatActionList.m_actionList.Count; i++) {
                if (combatActionList.m_actionList[i]?.m_spell is { } sent) {
                    combatActionList.m_actionList[i].m_spell = sent with {
                        m_paramOverrides = [new SpellEffectParamOverride { m_effectIndex = look.Value.Index, m_paramValue = group.Count() }],
                    };
                }
            }
        }

        // The server's own pick, whatever the cast took.
        target._hangingEffects.Clear();
        target._hangingEffects.AddRange(before);

        return cinematicTime;
    }

    // The game's removal spells of one effect (kRemoveCharm / kRemoveWard) on one target, by whether they take good or bad
    // ones and whether they go on an enemy or a teammate: the shortest name of each.
    private static readonly Lazy<List<(string Name, int Index, SpellEffect Effect)>> s_removalLooks = new(() => {
        try {
            return [.. SpellFactory.AllSpellTemplates()
                .Where(t => t.Template.m_effects is [{ } e] && e.GetType() == typeof(SpellEffect)
                            && e.m_effectType is kSpellEffects.kRemoveCharm or kSpellEffects.kRemoveWard
                            && e.m_effectTarget is kEffectTarget.kEnemySingle or kEffectTarget.kFriendlySingle or kEffectTarget.kFriendlySingleNotMe
                            && e.m_disposition is kHangingDisposition.kBeneficial or kHangingDisposition.kHarmful
                            && SpellFactory.Exists((string) t.Template.m_name))
                .Select(t => ((string) t.Template.m_name, 0, t.Template.m_effects[0]))
                .OrderBy(t => t.Item1.Length)];
        }
        catch (Exception e) {
            Logger.Error("Boss cheats | The removal spells could not be listed: {0}", Logger.Args(e));

            return [];
        }
    });

    private static (string Name, int Index)? RemovalLook(kSpellEffects type, bool good, bool onEnemy, bool onSelf) {
        var disposition = good ? kHangingDisposition.kBeneficial : kHangingDisposition.kHarmful;
        foreach (var (name, index, effect) in s_removalLooks.Value) {
            if (effect.m_effectType == type && effect.m_disposition == disposition
                && (effect.m_effectTarget == kEffectTarget.kEnemySingle) == onEnemy
                && !(onSelf && effect.m_effectTarget == kEffectTarget.kFriendlySingleNotMe)) {
                return (name, index);
            }
        }

        return null;
    }

    private static string SizedKind(SpellEffect e) {
        if (e.m_effectType == kSpellEffects.kAbsorbDamage) {
            return "Absorb";
        }

        foreach (var kind in new[] { "Blade", "Weakness", "Shield", "Trap" }) {
            if (IsKind(e, kind)) {
                return kind;
            }
        }

        return null;
    }

}
