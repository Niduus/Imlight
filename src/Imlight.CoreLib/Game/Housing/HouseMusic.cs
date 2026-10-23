/*
 * Imlight
 * Copyright (C) 2025 Revive101
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
 *
 * ========================================================================
 * HOUSING SYSTEM
 * ========================================================================
 *
 * PURPOSE:
 * The music players of a house. A music player holds musical scrolls (its playlist), the scroll it plays and how it
 * plays them. The state is the custom data of the player's blob entry, so it is stored with the placed item and every
 * client in the house reads it from the blob or from a MSG_PATCHHOUSINGMUSICOBJECT.
 *
 * USAGE EXAMPLE:
 * var player = HouseMusic.Read(placedItem);
 * player.m_playlist.Add(scrollTemplateId);
 * var custom = HouseMusic.Write(player);
 *
 * NOTE:
 * - The client's music window sends MSG_EQUIPHOUSEMUSIC for a scroll dragged from the backpack, MSG_SELECTHOUSEMUSIC
 *   for a scroll of the playlist, MSG_REMOVEHOUSEMUSIC to take one out and MSG_SELECTMUSICSTYLE for the play style
 *   (0x140f9abc0, 0x140f9be90, 0x140f9c0c0). The playlist holds scroll template ids.
 * - The client loads the custom data with a versionable binary serializer and the property mask 0x10000
 *   (ClientBlobPatcher::MSG_PatchHousingMusicObject, 0x140f1b180), which takes m_playlist, m_currentSelection and
 *   m_playStyle. The patch then tells its HousingMusicManager, which plays the music; no MSG_PLAYMUSIC is needed.
 * - A placed musical scroll needs nothing: the client plays its music itself.
 * - The play styles are the music window's LoopOneButton (0), LoopAllButton (1) and ShuffleButton (2) (0x140f99b4b).
 *   The music manager starts the stream with looping only for style 0 (0x140f7eb34). For the others it waits for
 *   MusicFinished (0x140f7ed90), so the zone music comes back while the next scroll fades in. With one scroll that is
 *   the same scroll again, so such a playlist is stored with style 0 (HouseMusic.KeepLooping).
 *
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/23/2026
 */

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Housing;

internal static class HouseMusic {

    /// <summary>
    /// The property mask of the custom data: the properties the client keeps in the blob.
    /// </summary>
    internal const uint BlobPropertyMask = 0x10000;

    /// <summary>
    /// The most scrolls one player holds. The client keeps m_musicData up to 4096 bytes, far more than this needs.
    /// </summary>
    internal const int MaxScrolls = 50;

    /// <summary>
    /// The highest play style the server stores. The music window offers three.
    /// </summary>
    internal const uint MaxPlayStyle = 15;

    /// <summary>
    /// The play style that plays the selected scroll over and over. It is the only style the client loops without a gap.
    /// </summary>
    internal const uint LoopOneStyle = 0;

    private const string PlayerBehaviorName = "HousingMusicPlayerBehavior";

    private static readonly ConcurrentDictionary<uint, bool> s_players = new();
    private static readonly ConcurrentDictionary<uint, bool> s_scrolls = new();

    /// <summary>
    /// Whether a template is a music player: furniture that carries a HousingMusicPlayerBehavior.
    /// </summary>
    internal static bool IsPlayer(uint templateId)
        => templateId != 0 && s_players.GetOrAdd(templateId, id => CoreObjectFactory.GetCoreTemplate(id) is WizItemTemplate template
            && (template.m_behaviors ?? []).Any(behavior => behavior?.m_behaviorName.ToString() == PlayerBehaviorName));

    /// <summary>
    /// Whether a template is a musical scroll: an item that carries a HousingMusicBehaviorTemplate.
    /// </summary>
    internal static bool IsScroll(uint templateId)
        => templateId != 0 && s_scrolls.GetOrAdd(templateId, id => CoreObjectFactory.GetCoreTemplate(id) is WizItemTemplate template
            && (template.m_behaviors ?? []).OfType<HousingMusicBehaviorTemplate>().Any());

    /// <summary>
    /// Reads the state of a placed music player.
    /// </summary>
    /// <param name="item">The placed player.</param>
    /// <returns>The state; an empty player when the item has no custom data or it cannot be read.</returns>
    internal static HousingMusicPlayerBehavior Read(PlacedItem item)
        => Read(item?.TemplateId ?? 0, item?.Custom);

    /// <summary>
    /// Reads the state of a music player from its custom data.
    /// </summary>
    /// <param name="templateId">The player's template, for the log.</param>
    /// <param name="custom">The stored custom data.</param>
    /// <returns>The state; an empty player when there is no custom data or it cannot be read.</returns>
    internal static HousingMusicPlayerBehavior Read(uint templateId, string custom) {
        var data = PlacedItem.DecodeCustom(custom);
        if (data.Length > 0) {
            try {
                if (NewSerializer().Deserialize<HousingMusicPlayerBehavior>(data, BlobPropertyMask, out var stored) && stored is not null) {
                    stored.m_playlist ??= [];

                    return stored;
                }
            } catch (System.Exception exception) {
                Logger.Error("Music: the data of music player {TemplateId} could not be read: {Reason}",
                    Logger.Args(templateId, exception.Message));
            }

            Logger.Warning("Music: the data of music player {TemplateId} could not be read; it starts empty.", Logger.Args(templateId));
        }

        return new HousingMusicPlayerBehavior { m_playlist = [] };
    }

    /// <summary>
    /// The scrolls a placed music player holds.
    /// </summary>
    /// <param name="item">The placed player, or an item that is not one.</param>
    /// <returns>The scroll template ids; none for an item without custom data.</returns>
    internal static IReadOnlyList<uint> Scrolls(PlacedItem item)
        => item is null ? [] : Scrolls(item.TemplateId, item.Custom);

    /// <summary>
    /// The scrolls a music player holds, from its custom data.
    /// </summary>
    /// <param name="templateId">The item's template.</param>
    /// <param name="custom">The stored custom data.</param>
    /// <returns>The scroll template ids; none for an item that is not a music player or has no custom data.</returns>
    internal static IReadOnlyList<uint> Scrolls(uint templateId, string custom)
        => custom is null || !IsPlayer(templateId) ? [] : Read(templateId, custom).m_playlist;

    /// <summary>
    /// Creates the backpack items of the scrolls a music player gives back. The items are not added to the backpack.
    /// </summary>
    /// <param name="wizard">The character who gets them.</param>
    /// <param name="scrolls">The scroll template ids.</param>
    /// <param name="items">The new items when this returns <c>true</c>.</param>
    /// <returns><c>false</c> when a scroll template is not an item any more.</returns>
    internal static bool TryCreateScrolls(Wizard wizard, IReadOnlyList<uint> scrolls, out List<WizClientObjectItem> items) {
        items = [];
        foreach (var scroll in scrolls) {
            if (HousingItems.CreateItem(wizard, scroll) is not { } item) {
                Logger.Error("Music: musical scroll {TemplateId} of a music player of character {CharId} could not be created.",
                    Logger.Args(scroll, wizard.CharId));

                return false;
            }

            items.Add(item);
        }

        return true;
    }

    /// <summary>
    /// Writes the state of a music player as the custom data of its blob entry.
    /// </summary>
    /// <param name="player">The state.</param>
    /// <param name="data">The bytes the client reads when this returns <c>true</c>.</param>
    /// <returns><c>false</c> when the state cannot be serialized.</returns>
    internal static bool TryWrite(HousingMusicPlayerBehavior player, out byte[] data) {
        data = [];
        try {
            if (!NewSerializer().Serialize(player, BlobPropertyMask, out var serialized)) {
                return false;
            }

            data = serialized;

            return data.Length > 0;
        } catch (System.Exception exception) {
            Logger.Error("Music: a music player could not be serialized: {Reason}", Logger.Args(exception.Message));

            return false;
        }
    }

    /// <summary>
    /// Gives a playlist of one scroll the loop-one style. In the other styles the client lets the zone music play for a
    /// moment each time the scroll ends before it starts it again (Niduus, 2026-10-23).
    /// </summary>
    /// <param name="player">The state, changed in place.</param>
    /// <returns><c>true</c> when the style was changed.</returns>
    internal static bool KeepLooping(HousingMusicPlayerBehavior player) {
        if (player.m_playStyle == LoopOneStyle || (player.m_playlist?.Count ?? 0) > 1) {
            return false;
        }

        player.m_playStyle = LoopOneStyle;

        return true;
    }

    /// <summary>
    /// Describes a player's state for the log.
    /// </summary>
    internal static string Describe(HousingMusicPlayerBehavior player)
        => $"playlist [{string.Join(", ", player.m_playlist ?? [])}], selection {player.m_currentSelection}, style {player.m_playStyle}";

    private static ObjectSerializer NewSerializer()
        => new(Versionable: true, Behaviors: SerializerFlags.None);

}
