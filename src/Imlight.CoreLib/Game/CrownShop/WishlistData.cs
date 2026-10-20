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

using Imcodec.IO;

namespace Imlight.CoreLib;

/// <summary>
/// The crown shop wishlist blob the game sends and stores. Upstream's feat/crown-shop uses this type but never
/// published it, so this keeps the blob as the game sent it and leaves item removal to the game, which sends the
/// updated wishlist when the crown shop closes.
/// </summary>
public static class WishlistData {

    public static ByteString Empty => ByteString.Empty;

    public static bool TryFromByteString(ByteString data, out ByteString wishlist) {
        wishlist = data;

        return true;
    }

    public static bool TryRemoveItem(ByteString wishlist, uint templateId, out ByteString updated) {
        updated = wishlist;

        return false;
    }

}
