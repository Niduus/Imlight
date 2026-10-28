/*
 * Imlight
 * Copyright (C) 2026 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 */

/*
 * ========================================================================
 * LEVEL SCALES
 * ========================================================================
 *
 * PURPOSE:
 * The Scales of Knowledge (an object in Aquila, Empyrea, Polaris, Mirage, Karamelle, Lemuria, Novus and Marleybone)
 * and the Level Scaling Elixirs they give, as in the live game. Once the wizard has the area's badge, "Apply Level
 * Scaling (LV 70)" gives a two hour Level Scaling Elixir, taking off any other one first, and "Level Scaling 101" reads
 * the scales' explanation. While the elixir runs, the wizard fights creatures with stats lowered to its level
 * (CombatDuelSubCircle, the same scaling a PvP league uses). Throwing the elixir away ends it.
 *
 * USAGE EXAMPLE:
 * if (LevelScales.TryGet(templateName, out var scales)) { }   // a Scales of Knowledge
 * var effect = LevelScales.ActiveEffect(wizard);             // "LevelScaled_70" while one runs, else null
 *
 * NOTE:
 * The scales' interact options are InteractOptionTemplate with requirements and results the server does not read with
 * the template, so what each one needs and gives is listed here, read from the game files on 2026-10-28 (Root.wad,
 * ObjectData/<world>/*-SCALING.xml). Each option needs a badge (ReqHasBadge): the apply option's and the "101" option's.
 * Their results: ResAddElixir of the elixir, ResDisplayText of the "Applied" line, a ResRemoveElixir of every other
 * Level Scaling Elixir; the "101" option shows an ActorDialog (LevelScaling_00000017 to _00000020).
 * In battle the wizard fights with a scaled copy of their stats (like a PvP duel or the Deckathalon), starting at the
 * same share of health as outside; the battle's damage stays with the copy.
 *
 * TODO:
 * Live gives some drops only to a scaled wizard (or one at the area's level); drop tables here do not check that.
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/28/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Badges;
using Imlight.CoreLib.Game.PvP.Data;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.LevelScaling;

internal static class LevelScales {

    /// <summary>The scales' button picture.</summary>
    internal const string ICON = "GUI/QuestButtons/Art_Use_LevelScales.dds";

    /// <summary>"Level Scaling 101", the option that explains the scales.</summary>
    internal const string INFO_KEY = "LevelScaling_00000015";

    /// <summary>
    /// One Scales of Knowledge: the badge its apply option needs, the badge its "101" option needs, the elixir it gives
    /// (its file name, such as Elixir-DownScaling-70), and the game's text for "Apply Level Scaling (LV 70)" and
    /// "Level Scaling (LV 70) Applied".
    /// </summary>
    internal sealed record Scales(string Badge, string InfoBadge, string Elixir, string ApplyKey, string AppliedKey);

    // By the scales' template name.
    private static readonly Dictionary<string, Scales> s_scales = new(StringComparer.OrdinalIgnoreCase) {
        ["MB-SCOT-SCALING"] = new("KillMallistaire", "FinishMS", "Elixir-DownScaling-40", "LevelScaling_00000035", "LevelScaling_00000000"),
        ["AQ-Olympus-SCALING"] = new("FinishMS", "FinishMS", "Elixir-DownScaling-30", "LevelScaling_00000002", "LevelScaling_00000001"),
        ["AQ-Atlantea-SCALING"] = new("FinishAV", "DefeatMorganthe", "Elixir-DownScaling-70", "LevelScaling_00000008", "LevelScaling_00000007"),
        ["AQ-Tartarus-SCALING"] = new("DefeatMorganthe", "DefeatMorganthe", "Elixir-DownScaling-90", "LevelScaling_00000012", "LevelScaling_00000011"),
        ["AQ-Sisyphus-SCALING"] = new("DefeatMorganthe", "DefeatMorganthe", "Elixir-DownScaling-90", "LevelScaling_00000012", "LevelScaling_00000011"),
        ["PL-SCALING"] = new("Mirage-Complete-Mainline-1", "Mirage-Complete-Mainline-1", "Elixir-DownScaling-110", "LevelScaling_00000027", "LevelScaling_00000028"),
        ["MR-SCALING"] = new("Empyrea2-Complete-Mainline-1", "Empyrea2-Complete-Mainline-1", "Elixir-DownScaling-120", "LevelScaling_00000025", "LevelScaling_00000026"),
        ["EM-SCALING"] = new("FinishKMMainline", "FinishKMMainline", "Elixir-DownScaling-130", "LevelScaling_00000024", "LevelScaling_00000023"),
        ["KM-SCALING"] = new("FinishAR-PostLM-MAIN-001", "FinishAR-PostLM-MAIN-001", "Elixir-DownScaling-140", "LevelScaling_00000029", "LevelScaling_00000030"),
        ["LM-SCALING"] = new("FinishNV-CONA-MAIN-005", "FinishNV-CONA-MAIN-005", "Elixir-DownScaling-150", "LevelScaling_00000031", "LevelScaling_00000032"),
        ["NV-SCALING"] = new("FinishAR-PostWL-MAIN-001", "FinishAR-PostWL-MAIN-001", "Elixir-DownScaling-160", "LevelScaling_00000033", "LevelScaling_00000034"),
    };

    internal static bool TryGet(string templateName, out Scales scales) {
        scales = null;

        return !string.IsNullOrEmpty(templateName) && s_scales.TryGetValue(templateName, out scales);
    }

    /// <summary>The Level Scaling Elixir's template id for a level (Elixir-DownScaling-70 for 70), or 0.</summary>
    internal static uint ElixirFor(int level) => IdOf($"Elixir-DownScaling-{level}");

    /// <summary>The template id of the elixir the scales give, or 0.</summary>
    internal static uint ElixirId(Scales scales) => IdOf(scales.Elixir);

    private static uint IdOf(string fileName) => (uint) Math.Min(CoreObjectFactory.IdOfFileName(fileName), uint.MaxValue);

    /// <summary>Whether the wizard has earned the badge (a badge the catalog does not know counts as not earned).</summary>
    internal static bool HasBadge(Wizard wizard, string badgeName)
        => wizard?.BadgeBehavior is not null
           && BadgeCatalog.Current.TryGetByName(badgeName, out var definition)
           && BadgeView.IsComplete(wizard.BadgeBehavior, definition);

    /// <summary>The level scaling effect an item template gives (LevelScaled_70), or null when it gives none.</summary>
    internal static string ScalingEffectOf(WizItemTemplate template)
        => template?.m_equipEffects?
            .Select(effect => effect is null ? null : $"{effect.m_effectName}")
            .FirstOrDefault(name => LevelScaledEffectFactory.TryGetTemplate(name, out var scaled) && scaled.m_scaleToLevel > 0);

    /// <summary>The level scaling effect of the Level Scaling Elixir the wizard has running, or null.</summary>
    internal static string ActiveEffect(Wizard wizard) {
        if (wizard?.EquipmentBehavior is null) {
            return null;
        }

        foreach (var (slot, item) in wizard.EquipmentBehavior.GetActiveElixirs()) {
            if (slot.RemainingSeconds == 0) {
                continue;
            }

            if (ScalingEffectOf(ItemHelper.GetItemTemplate(item)) is { } effect) {
                return effect;
            }
        }

        return null;
    }

    /// <summary>The level the wizard's running Level Scaling Elixir scales to, or 0.</summary>
    internal static int ActiveLevel(Wizard wizard)
        => ActiveEffect(wizard) is { } effect && LevelScaledEffectFactory.TryGetTemplate(effect, out var scaled) ? scaled.m_scaleToLevel : 0;

}
