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
using System.Linq;
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.Math;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Combat;

/// <summary>
/// Health, mana and gold wisps that drop where a battle was won ([Battle] BattleWisps).
/// </summary>
internal static class BattleDrops {

    internal static readonly bool Enabled = ConfigurationManager.GetValue("Battle.BattleWisps", true);

    // Marks a dropped wisp (as its zone tag) so it can expire if nobody picks it up.
    internal const string DROP_TAG = "BattleDrop";
    internal static readonly uint DropTagId = StringHash.Compute(DROP_TAG);

    private const float MIN_SCATTER = 60f;
    private const float MAX_SCATTER = 140f;

    private static readonly Lazy<uint> s_healthWisp = new(() => FindWisp("health", "wc_"));
    private static readonly Lazy<uint> s_manaWisp = new(() => FindWisp("mana", "uw_"));
    private static readonly Lazy<uint> s_goldWisp = new(() => FindWisp("gold", null));

    /// <summary>
    /// The wisp template whose file name names the kind, preferring one that is not the starter world's.
    /// </summary>
    private static uint FindWisp(string kind, string starterPrefix) {
        var candidates = CoreObjectFactory.TemplateManifest?.m_serializedTemplates?
            .Where(t => t?.m_filename is not null)
            .Select(t => (t.m_id, Name: ((string) t.m_filename).Replace('\\', '/').Split('/').Last().ToLowerInvariant()))
            .Where(t => t.Name.Contains("wisp") && t.Name.Contains(kind))
            .OrderBy(t => starterPrefix is not null && t.Name.StartsWith(starterPrefix) ? 1 : 0)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .ToList() ?? [];

        foreach (var (id, name) in candidates) {
            if (CoreObjectFactory.GetCoreTemplate(id) is GameObjectTemplate) {
                Logger.Information("Battle {0} wisp: {1} ({2}).", Logger.Args(kind, name, id));

                return id;
            }
        }

        Logger.Warning("No {0} wisp template was found, so battles drop none.", Logger.Args(kind));

        return 0;
    }

    /// <summary>
    /// Drops one health, one mana and one gold wisp around <paramref name="location"/>.
    /// </summary>
    internal static void Spawn(IActorRef zoneRef, Vector3 location) {
        if (!Enabled || zoneRef is null) {
            return;
        }

        foreach (var templateId in new[] { s_healthWisp.Value, s_manaWisp.Value, s_goldWisp.Value }) {
            if (templateId == 0 || CoreObjectFactory.GetCoreTemplate(templateId) is not GameObjectTemplate template) {
                continue;
            }

            var angle = Random.Shared.NextSingle() * MathF.Tau;
            var distance = MIN_SCATTER + (Random.Shared.NextSingle() * (MAX_SCATTER - MIN_SCATTER));
            var info = new CoreObjectInfo {
                m_templateID = templateId,
                m_fScale = 1.0f,
                m_zoneTag = DROP_TAG,
                m_location = new Vector3(
                    location.X + (MathF.Cos(angle) * distance),
                    location.Y + (MathF.Sin(angle) * distance),
                    location.Z),
            };

            var wisp = CoreObjectFactory.FinalizeCoreObject(info, template);
            if (wisp is null) {
                continue;
            }

            wisp = CoreObjectFactory.InitializeCoreObjectBehaviors(wisp, template);
            zoneRef.Tell(new ZONE_102_PROTOCOL.MSG_SPAWNENTITY { CoreObject = wisp, Template = template });
        }
    }

}
