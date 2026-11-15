/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imlight.Common;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Quests;

/// <summary>
/// The three storm obelisks of the Tomb of Storms in Krokotopia (KT_ScarabHubOblelisk 1-3). In the original game a wizard
/// lights all three before the way on into the Well of Spirits opens (Niduus, 2026-11-15: "to go to the temple of storms
/// you're suppose to interact with the 3 pillars"). The client's files hold the obelisks but not the lock, so it is kept
/// here: each wizard's own lighting is remembered on the wizard, and the Tomb of Storms' way into the Well of Spirits
/// takes only a wizard who lit all three, or one already past it (finished "De-Cipher the Ahnics", or has been inside), so no
/// wizard who is already there is ever sent back (Niduus: "Ultimus wouldn't really count towards this check since he's
/// at where he needs to be").
/// </summary>
internal static class StormObelisks {

    internal const string CRYPT_ZONE = "Krokotopia/KT_Hub_Crypt";
    internal const string ENTRANCE_TRIGGER = "Teleport location (Krok_Hub StormCrypt Entrance)";
    private const string INSIDE_FOLDER = "Krokotopia/KT_Tomb/";
    // "De-Cipher the Ahnics": its steps are inside the Well of Spirits, so a wizard who finished it has been in. ("Tomb of
    // Storms" itself finishes before the obelisks, by talking: Ultimus finished it in 37 seconds, 2026-11-14.)
    private const string PAST_GATE_QUEST = "KT-CRYHub-C01-001";
    private const string INSIDE_KEY = "IMLIGHT-KT-WELL-OF-SPIRITS-VISITED";

    /// <summary>The obelisks: template, the registry entry that says a wizard lit it, and where it stands.</summary>
    internal static readonly IReadOnlyList<(ulong Template, string Key, float X, float Y, int Number)> All = [
        (87872, "IMLIGHT-KT-STORM-OBELISK-1", -3187f, -2920f, 1),
        (87873, "IMLIGHT-KT-STORM-OBELISK-2", -4648f, -2902f, 2),
        (87874, "IMLIGHT-KT-STORM-OBELISK-3", -4274f, -1231f, 3),
    ];

    internal static bool IsObelisk(ulong template) => All.Any(o => o.Template == template);

    internal static bool HasLit(Wizard wizard, ulong template)
        => wizard is not null && All.FirstOrDefault(o => o.Template == template) is { Key: { } key } && wizard.HasRegistryValue(key);

    /// <summary>The wizard lit this obelisk (remembered on the wizard, so it holds in every copy of the tomb).</summary>
    internal static void Lit(Wizard wizard, ulong template) {
        if (wizard is null || All.FirstOrDefault(o => o.Template == template) is not { Key: { } key } || wizard.HasRegistryValue(key)) {
            return;
        }

        wizard.SetRegistryValue(key, 1);
        Logger.Information("Wizard {0} lit storm obelisk {1} in the Tomb of Storms ({2} of 3).",
            Logger.Args(wizard.CharId, template, All.Count(o => wizard.HasRegistryValue(o.Key))));
    }

    // The obelisks light for a wizard who finished "Back to Balance" (their own requirement, KT-SPHUB-C01-005_Complete).
    private const string BACK_TO_BALANCE_QUEST = "KT-SPHUB-C01-005";

    internal static bool MayLight(Wizard wizard) => wizard is not null && wizard.HasCompletedQuest(BACK_TO_BALANCE_QUEST);

    /// <summary>The obelisks this wizard has not lit yet, in order.</summary>
    internal static IEnumerable<(ulong Template, string Key, float X, float Y, int Number)> Unlit(Wizard wizard)
        => wizard is null ? [] : All.Where(o => !wizard.HasRegistryValue(o.Key));

    /// <summary>True when the way into the Well of Spirits takes this wizard.</summary>
    internal static bool MayEnter(Wizard wizard) {
        if (wizard is null) {
            return true;
        }

        return All.All(o => wizard.HasRegistryValue(o.Key))
               || wizard.HasRegistryValue(INSIDE_KEY)
               || wizard.HasCompletedQuest(PAST_GATE_QUEST);
    }

    /// <summary>True when the way into the Well of Spirits would be shut for this wizard (it still has obelisks to light).</summary>
    internal static bool Shut(Wizard wizard) => !MayEnter(wizard);

    internal static bool IsInside(string zonePath)
        => zonePath is not null && zonePath.StartsWith(INSIDE_FOLDER, StringComparison.OrdinalIgnoreCase);

    internal static bool IsCrypt(string zonePath) => string.Equals(zonePath, CRYPT_ZONE, StringComparison.OrdinalIgnoreCase);

    /// <summary>A wizard in the Well of Spirits or its tombs is past the obelisks for good (however it got there).</summary>
    internal static void BeenInside(Wizard wizard, string zonePath) {
        if (wizard is null || !IsInside(zonePath) || wizard.HasRegistryValue(INSIDE_KEY)) {
            return;
        }

        wizard.SetRegistryValue(INSIDE_KEY, 1);
    }
}
