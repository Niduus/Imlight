/*
 * The game's tutorial tips: the NPC portraits that stack up on the right of the screen (Niduus, 2026-10-28: a new
 * wizard got about 40 at once, Ambrose, Gamma, Cyrus Drake and others, "these should be gated behind conditions").
 *
 * The game keeps the tips itself (Root.wad TutorialTips/*.ttip) and shows one when its event happens (first login,
 * zone change, quest done, level up) and its requirements pass. It can't check the quest registry: in the game's code
 * ReqHasEntry and ReqClassProjectActive always pass, so every "you finished X, now go to Y" tip showed for a wizard who
 * finished nothing. Live holds those back from the server, with the tip log of the player's TutorialLogBehavior:
 *   - MSG_UPDATETUTORIALTIPLOG carries the log: uint 0x2DEC0DED, uint version 1, ushort count, count uint tip IDs.
 *   - The game replaces its list of finished tips with it, takes down every tip on screen that is in it, and never
 *     shows a tip in it (unless the tip is "always repeatable", which the game leaves out of the log).
 *   - MSG_READTUTORIALTIP: the player read a tip, it goes in the log for good.
 * So the log we send = the tips the wizard read + the tips whose quest registry requirements fail right now. When a
 * quest finishes and a tip's requirements pass, it leaves the log and shows on its next event like on live.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.IO;
using Imlight.Common;
using Imlight.CoreLib.Game.Requirements;
using Imlight.CoreLib.Game.Requirements.Contexts;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Type = System.Type;

namespace Imlight.CoreLib.Game.Quests;

internal static class TutorialTipGate {

    private const string TIP_FOLDER = "TutorialTips/";
    private const uint LOG_MAGIC = 0x2DEC0DED;
    private const uint LOG_VERSION = 1;

    // The requirements the game counts as met without looking (its evaluate is "return true"); only the server knows.
    private static readonly HashSet<Type> s_serverOnly = [
        typeof(ReqHasEntry),
        typeof(ReqClassProjectActive),
        typeof(ReqGlobalRegistryValue),
    ];

    private enum Check { False, True, ClientDecides }

    private sealed record Tip(uint Id, string Name, RequirementList Requirements);

    private static readonly Lock s_lock = new();
    // Only the tips with a requirement the server must check, and that the game can keep in the log.
    private static List<Tip> s_tips;
    // The log last sent to each wizard, so an unchanged log is not sent again.
    private static readonly ConcurrentDictionary<ulong, uint[]> s_lastSent = new();

    /// <summary>
    /// Sends the wizard's tip log when it changed since the last one (or always, when asked to), so the game shows only
    /// the tips whose quest conditions are met.
    /// </summary>
    internal static void Refresh(Wizard wizard, CoreObject playerObj, IActorRef playerRef, Action<IMessage> send,
                                 bool always = false) {
        if (wizard is null || send is null) {
            return;
        }

        try {
            var held = new List<uint>();
            foreach (var tip in GetTips()) {
                if (IsHeldBack(tip, wizard, playerObj, playerRef)) {
                    held.Add(tip.Id);
                }
            }

            var read = wizard.TutorialTipsRead ?? [];
            var log = read.Concat(held).Distinct().OrderBy(id => id).Take(ushort.MaxValue).ToArray();
            if (!always && s_lastSent.TryGetValue(wizard.CharId, out var last) && last.SequenceEqual(log)) {
                return;
            }

            var had = s_lastSent.TryGetValue(wizard.CharId, out var before) ? before : null;
            s_lastSent[wizard.CharId] = log;
            send(new WIZARD2_53_PROTOCOL.MSG_UPDATETUTORIALTIPLOG {
                GlobalID = wizard.GameObjectID,
                TutorialTipData = new ByteString(Encode(log)),
            });

            if (had is not null) {
                var freed = had.Except(log).ToList();
                if (freed.Count > 0) {
                    Logger.Information("Tutorial tips | {0} may see {1} more now: {2}.",
                        Logger.Args(wizard.CharId, freed.Count, string.Join(", ", freed.Select(NameOf))));
                }
            }
            else {
                Logger.Debug("Tutorial tips | {0}: {1} held back until their quests are done, {2} read.",
                    Logger.Args(wizard.CharId, held.Count, read.Count));
            }
        }
        catch (Exception ex) {
            Logger.Error("Tutorial tips | Sending the tip log failed: {0}", Logger.Args(ex));
        }
    }

    /// <summary>The player read a tip: it stays in the log for good.</summary>
    internal static void MarkRead(Wizard wizard, uint tipId) {
        if (wizard is null || tipId == 0) {
            return;
        }

        wizard.TutorialTipsRead ??= [];
        if (wizard.TutorialTipsRead.Contains(tipId)) {
            return;
        }

        wizard.TutorialTipsRead.Add(tipId);
        WizardCollection.UpdateCharacterTutorialTipsRead(wizard);
    }

    private static bool IsHeldBack(Tip tip, Wizard wizard, CoreObject playerObj, IActorRef playerRef) {
        var context = new GenericRequirementContext(tip.Requirements, playerRef, playerObj, wizard);

        return Evaluate(tip.Requirements, context) == Check.False;
    }

    /// <summary>
    /// The requirement list folded like the game does (see <see cref="RequirementDispatcher.EvaluateRequirements"/>),
    /// with the requirements the game checks itself left open: a tip is held back only when the server's part alone
    /// already fails it.
    /// </summary>
    private static Check Evaluate(RequirementList list, IRequirementContext context) {
        if (list?.m_requirements is not { Count: > 0 } items) {
            return Check.True;
        }

        var result = Check.True;
        var joinOperator = Operator.ROP_AND;
        foreach (var requirement in items) {
            if (requirement is null) {
                continue;
            }

            var value = requirement is RequirementList nested ? Evaluate(nested, context)
                : s_serverOnly.Contains(requirement.GetType())
                    ? RequirementDispatcher.EvaluateIndividualRequirement(requirement, context) ? Check.True : Check.False
                    : Check.ClientDecides;
            if (requirement.m_applyNOT) {
                value = value switch { Check.True => Check.False, Check.False => Check.True, _ => value };
            }

            result = joinOperator == Operator.ROP_OR ? Or(result, value) : And(result, value);
            joinOperator = requirement.m_operator;
        }

        return result;
    }

    private static Check And(Check a, Check b)
        => a == Check.False || b == Check.False ? Check.False
            : a == Check.True && b == Check.True ? Check.True : Check.ClientDecides;

    private static Check Or(Check a, Check b)
        => a == Check.True || b == Check.True ? Check.True
            : a == Check.False && b == Check.False ? Check.False : Check.ClientDecides;

    private static byte[] Encode(uint[] log) {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(LOG_MAGIC);
        writer.Write(LOG_VERSION);
        writer.Write((ushort) log.Length);
        foreach (var id in log) {
            writer.Write(id);
        }
        writer.Flush();

        return stream.ToArray();
    }

    private static string NameOf(uint id) => GetTips().FirstOrDefault(t => t.Id == id)?.Name ?? id.ToString();

    private static List<Tip> GetTips() {
        lock (s_lock) {
            if (s_tips is not null) {
                return s_tips;
            }

            var tips = new List<Tip>();
            var all = 0;
            try {
                foreach (var (entry, data) in RootArchiveLoader.GetDirectoryStream(TIP_FOLDER)) {
                    if (data is not { } bytes) {
                        continue;
                    }

                    try {
                        if (!new BindSerializer().Deserialize(bytes.ToArray(), out TipData tip) || tip is null) {
                            continue;
                        }

                        all++;
                        if (tip.m_alwaysRepeatable || (ulong) tip.m_id == 0 || (ulong) tip.m_id > uint.MaxValue
                            || !HasServerOnly(tip.m_requirements)) {
                            continue;
                        }

                        var name = Path.GetFileNameWithoutExtension(entry.FileName ?? "");
                        tips.Add(new Tip((uint) (ulong) tip.m_id, name, tip.m_requirements));
                    }
                    catch (Exception ex) {
                        Logger.Warning("Tutorial tips | could not read {0}: {1}", Logger.Args(entry.FileName, ex.Message));
                    }
                }
            }
            catch (Exception ex) {
                Logger.Error("Tutorial tips | reading the game's tips failed: {0}", Logger.Args(ex.Message));
            }

            Logger.Information("Tutorial tips | {0} tips in the game files, {1} wait on quests only the server can check.",
                Logger.Args(all, tips.Count));
            s_tips = tips;

            return s_tips;
        }
    }

    private static bool HasServerOnly(RequirementList list)
        => list?.m_requirements?.Any(r => r is RequirementList nested ? HasServerOnly(nested)
            : r is not null && s_serverOnly.Contains(r.GetType())) == true;

}
