/*
 * "YOU CAME BY A SECRET PATH"
 *
 * Northguard (Grizzleheim/GH_MainHub) has an entry trigger, "Trigger TeleportWarning", in which Haggerty Greycap greets
 * a wizard with "You have arrived in Grizzleheim by a secret path, and as such, no one is ready to speak to you." It
 * asks only whether the wizard has Baldur Goldpaws' Wizard City errands (WC-GRZ-PRE-001 "Bear Market" or
 * WC-GRZ-PRE-002), and this server counts a finished quest as had too (ReqHasQuestHandler). So a wizard who ever did
 * Bear Market heard it on every visit, deep into the Grizzleheim story (a player's report, 2026-11-19: "This message
 * keeps appearing whenever you enter Northguard. Come on Haggerty everyone already knows about me since then").
 *
 * The greeting is meant for a wizard who came before being sent: it now plays only while one of those errands is still
 * open in the quest book and the wizard has not yet started the Grizzleheim story (Merle's letter, WC-GRZ-C01-001, or
 * any Grizzleheim main quest). The game files are not changed.
 */

using System;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Supervisors;

internal sealed partial class ZoneTriggerSupervisor {

    private const string NORTHGUARD = "Grizzleheim/GH_MainHub";
    private const string SECRET_PATH_WARNING = "Trigger TeleportWarning";

    // The errands the greeting asks about, and the quests that mean Grizzleheim already knows the wizard.
    private static readonly string[] s_secretPathErrands = ["WC-GRZ-PRE-001", "WC-GRZ-PRE-002"];
    private static readonly string[] s_grizzleheimStoryStarts = ["WC-GRZ-C01-001", "WC-GRZ-C01-002", "WC-GRZ-C01-004", "WC-GRZ-C01-005"];

    /// <summary>
    /// Whether this trigger is Haggerty's "secret path" greeting and the wizard should not hear it: none of Baldur's
    /// errands is open any more, or Grizzleheim's story has already begun for them.
    /// </summary>
    private bool IsStaleSecretPathWarning(Trigger trigger, ZONE_102_PROTOCOL.MSG_POSTEVENT message) {
        if (!string.Equals(trigger?.m_triggerName, SECRET_PATH_WARNING, StringComparison.Ordinal)
            || !string.Equals(Zone.ZonePath, NORTHGUARD, StringComparison.OrdinalIgnoreCase)
            || message.PlayerActor is null) {
            return false;
        }

        try {
            if (Core.ZoneTrigger.QueryWizard(message.PlayerActor) is not { } wizard) {
                return false;
            }

            var errandOpen = s_secretPathErrands.Any(wizard.HasQuest);
            return !errandOpen || KnownInGrizzleheim(wizard);
        }
        catch (Exception) {
            return false;
        }
    }

    private static bool KnownInGrizzleheim(Wizard wizard)
        => s_grizzleheimStoryStarts.Any(q => wizard.HasQuest(q) || wizard.HasCompletedQuest(q))
           || wizard.QuestBehavior.CurrentQuestInstances.Any(q => q?.QuestName?.StartsWith("GH-", StringComparison.OrdinalIgnoreCase) == true);

}
