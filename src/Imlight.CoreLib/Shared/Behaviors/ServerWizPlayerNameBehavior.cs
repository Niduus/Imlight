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
using System.Linq;
using System.Text;
using Imcodec.IO;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Shared.Resources;
using System.Text.Json.Serialization;

namespace Imlight.CoreLib.Shared.Behaviors;

[Serializable]
public class ServerWizPlayerNameBehavior : IClientBehaviorProvider<ClientWizPlayerNameBehavior> {

    private const string FemaleSourcePrefix = "80";
    private const string MaleSourcePrefix = "82";

    [JsonIgnore] public bool NoTransfer { get; set; } = false;

    public uint NameIndices;
    public bool UseRank;
    public eGender Gender;
    public eRace Race;
    public ByteString BadgeTitle;
    public uint ChatPermissions;
    public uint PvpIconId;
    public uint LocaleId;
    public bool FriendlyPlayer;
    public bool Volunteer;
    public uint GuildName;
    public WideByteString NameOverride;

    public string GetWizardName() {
        if (NameOverride.Length > 0) {
            return NameOverride.ToString();
        }

        var actualName = WizardNameBank.GetEnglishName(NameIndices, Gender);
        
        return actualName;
    }

    public string GetWizardNameAsByteHexString() {
        // A custom name is sent as itself, so chat, friends and groups show it rather than the name the keys make
        // (Niduus, 2026-10-21: chat said "Mycin LotusKey" for Walter Blanco).
        if (TryGetOverrideNameBytes(out var overrideBytes)) {
            return string.Join(" ", overrideBytes.Select(x => x.ToString("X2")));
        }

        // Drop the MSB from input, then convert it to a hex string.
        var raw = (NameIndices & 0x7FFFFFFF).ToString("X8");
        var sb = new StringBuilder(raw);
        for (int i = sb.Length - 2; i >= 0; i -= 2) {
            sb.Insert(i, ' ');
        }

        var tail = sb.ToString().TrimStart();

        // Replace the first 2 characters depending on gender.
        var newMsb = Gender == eGender.Female ? FemaleSourcePrefix : MaleSourcePrefix;
        tail = newMsb + tail[2..];

        return tail;
    }

    /// <summary>
    /// The game's packed-name form of NameOverride (read from the client's name codec, WizardNameCodec::UnpackName):
    /// the first byte's bits, lowest first, are 1 (an override, not name keys), 1 for 8-bit characters or 0 for
    /// UTF-16, the byte length (5 bits) and a closing 1; the name's bytes follow. 8-bit takes up to 31 characters,
    /// UTF-16 up to 15; a longer name falls back to the name keys.
    /// </summary>
    private bool TryGetOverrideNameBytes(out byte[] bytes) {
        bytes = null;
        var name = NameOverride.Length > 0 ? NameOverride.ToString() : null;
        if (string.IsNullOrEmpty(name)) {
            return false;
        }

        var ascii = name.All(c => c >= 0x20 && c < 0x7F);
        var body = ascii ? Encoding.ASCII.GetBytes(name) : Encoding.Unicode.GetBytes(name);
        if (body.Length > 31) {
            return false;
        }

        bytes = new byte[body.Length + 1];
        bytes[0] = (byte) (0x81 | (ascii ? 0x02 : 0) | (body.Length << 2));
        body.CopyTo(bytes, 1);

        return true;
    }

    public ClientWizPlayerNameBehavior GetClientBehaviorInstance() {
        // If the name override is set, we want to add the friendly player icon to the name
        // since the client won't do it automatically anymore.
        var nameOverride = new WideByteString();
        if (NameOverride.Length > 0 && FriendlyPlayer) {
            nameOverride = $"<image;FriendlyPlayer> {NameOverride} <image;FriendlyPlayer>";
        }
        else {
            nameOverride = NameOverride;
        }

        return new ClientWizPlayerNameBehavior {
            m_nameKeys = NameIndices,
            m_useRank = UseRank,
            m_eGender = Gender,
            m_eRace = Race,
            m_badgeTitle = BadgeTitle,
            m_chatPermissions = ChatPermissions,
            m_pvpIconID = PvpIconId,
            m_localeID = LocaleId,
            m_friendlyPlayer = FriendlyPlayer,
            m_volunteer = Volunteer,
            m_guildName = GuildName,
            m_wsNameOverride = nameOverride
        };
    }
    
}
