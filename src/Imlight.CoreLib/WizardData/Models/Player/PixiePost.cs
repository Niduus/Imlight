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

namespace Imlight.CoreLib.WizardData.Models.Player;

/// <summary>
/// One Pixie Post message in a wizard's mailbox (the Mail tab of the friends window). The texts are kept as the game
/// sent them (its own string bytes), in base64, so they go back to the game unchanged.
/// </summary>
[Serializable]
public class PixiePost {

    public ulong MailId { get; set; }
    // The sender's saved character ID. The game sends it back as the receiver when the player presses Reply.
    public ulong SenderCharId { get; set; }
    // The sender's packed name (the same bytes the friends list uses).
    public string SenderNameBase64 { get; set; }
    public string SubjectBase64 { get; set; }
    public string MessageBase64 { get; set; }
    // Unix seconds on the server when it was sent.
    public uint SentTime { get; set; }
    // The sender's own clock values, as the game sent them; TimeOffset goes back with the message.
    public uint TimeOffset { get; set; }
    public uint ClientTime { get; set; }
    public uint PartyName { get; set; }
    public uint PartyNameLocale { get; set; }
    public bool Read { get; set; }

}
