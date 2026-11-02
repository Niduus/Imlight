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
*/

using System;
using System.Text;
using Imcodec.IO;

namespace Imlight.CoreLib.Game.Social;

/// <summary>
/// The game puts a wide string inside the narrow Message of nearby and friend chat (MSG_REQUESTRADIALCHAT,
/// MSG_RADIALCHAT, MSG_REQUESTRADIALFRIENDCHAT): a 2-byte character count, then the text as UTF-16. Seen in the server
/// log 2026-11-02, where a typed line read "$\0Y\0O\0U\0...". Taken as plain text, those bytes put a "$" and a zero
/// between every letter, and a line sent on that way (channel chat, or the "..." stand-in as plain bytes) left the
/// other player's chat box stuck until they logged out and back in.
/// </summary>
internal static class RadialChatText {

    /// <summary>True when the bytes are a character count followed by exactly that many UTF-16 characters.</summary>
    internal static bool IsWide(ByteString message) {
        var bytes = (byte[]) message;
        if (bytes is null || bytes.Length < 2) {
            return false;
        }

        var count = bytes[0] | (bytes[1] << 8);

        return bytes.Length == 2 + count * 2;
    }

    /// <summary>The text the player typed, without the count and the zeros.</summary>
    internal static string Text(ByteString message) {
        var bytes = (byte[]) message;
        if (bytes is null || bytes.Length == 0) {
            return string.Empty;
        }

        var text = IsWide(message)
            ? Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2)
            : Encoding.UTF8.GetString(bytes);

        return text.Replace("\0", string.Empty);
    }

    /// <summary>Text to send back in the same form the player's message came in.</summary>
    internal static ByteString Like(ByteString original, string text) {
        text ??= string.Empty;
        if (!IsWide(original)) {
            return (ByteString) text;
        }

        var wide = Encoding.Unicode.GetBytes(text);
        var bytes = new byte[2 + wide.Length];
        bytes[0] = (byte) (text.Length & 0xFF);
        bytes[1] = (byte) ((text.Length >> 8) & 0xFF);
        Buffer.BlockCopy(wide, 0, bytes, 2, wide.Length);

        return bytes;
    }
}
