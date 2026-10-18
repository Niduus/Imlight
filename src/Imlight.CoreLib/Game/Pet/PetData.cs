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

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.Cryptography;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Pet;

/// <summary>
/// The pet talents and the talent effects from Root.wad: TalentData/*.xml and GameEffectData/PetTalentEffects.xml.
/// Loaded the first time a pet needs them, so a missing file only switches talents off instead of stopping the server.
/// </summary>
internal static class PetData {

    private const string TALENT_DIRECTORY = "TalentData/";
    private const string TALENT_EFFECTS_FILE = "GameEffectData/PetTalentEffects.xml";

    private static readonly object s_lock = new();
    private static bool s_loaded;
    private static readonly Dictionary<string, PetTalentBaseTemplate> s_talentsByName = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<uint, PetTalentBaseTemplate> s_talentsById = [];
    private static readonly Dictionary<string, PetBoostPlayerStatEffectTemplate> s_effects = new(StringComparer.OrdinalIgnoreCase);

    internal static uint TalentId(string talentName) => StringHash.Compute(talentName ?? "");

    internal static PetTalentBaseTemplate GetTalent(string talentName) {
        Load();

        return talentName is not null && s_talentsByName.TryGetValue(talentName, out var talent) ? talent : null;
    }

    internal static PetTalentBaseTemplate GetTalent(uint talentId) {
        Load();

        return s_talentsById.GetValueOrDefault(talentId);
    }

    internal static IEnumerable<PetTalentBaseTemplate> AllTalents {
        get {
            Load();

            return s_talentsByName.Values;
        }
    }

    internal static PetBoostPlayerStatEffectTemplate GetTalentEffect(string effectName) {
        Load();

        return effectName is not null && s_effects.TryGetValue(effectName, out var effect) ? effect : null;
    }

    private static void Load() {
        if (s_loaded) {
            return;
        }

        lock (s_lock) {
            if (s_loaded) {
                return;
            }

            s_loaded = true;
            try {
                var serializer = new BindSerializer();
                foreach (var (file, data) in RootArchiveLoader.GetDirectoryStream(TALENT_DIRECTORY)) {
                    if (data is null || !serializer.Deserialize<PetTalentBaseTemplate>(data.Value.ToArray(), out var talent)
                        || string.IsNullOrEmpty(talent?.m_talentName)) {
                        continue;
                    }

                    s_talentsByName[talent.m_talentName] = talent;
                    s_talentsById[TalentId(talent.m_talentName)] = talent;
                }

                var effects = RootArchiveLoader.GetFile<GameEffectTemplateList>(TALENT_EFFECTS_FILE);
                foreach (var effect in effects?.m_effectTemplates?.OfType<PetBoostPlayerStatEffectTemplate>() ?? []) {
                    if (!string.IsNullOrEmpty(effect.m_effectName)) {
                        s_effects[effect.m_effectName] = effect;
                    }
                }

                Logger.Information("Loaded {0} pet talents and {1} pet talent effects.",
                    Logger.Args(s_talentsByName.Count, s_effects.Count));
            }
            catch (Exception e) {
                Logger.Error("Could not load the pet talents: {0}", Logger.Args(e));
            }
        }
    }

}
