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
 * Castle Magic: the objects (levers, buttons, orbs, timers, detectors...) whose spells the owner sets by casting Castle
 * Magic treasure cards onto them, and the players in a house, whose clients share what the magic does.
 *
 * USAGE EXAMPLE:
 * if (HouseCastleMagic.TrySetAction(stored, message.ActionNumber, spell, target, x, y, z, out var data, out var refusal)) {
 *     // store PlacedItem.EncodeCustom(data) on the object and send MSG_PATCHCASTLEMAGIC with data
 * }
 *
 * NOTE:
 * - The client runs Castle Magic itself. The server keeps each object's spells and passes messages between the
 *   players in the house.
 * - An object's spells are the custom data of its blob entry, which the client copies into CastleMagicBehavior's
 *   m_castleMagicData when it builds the object (ClientHousingManager, 0x140f33688) and when MSG_PATCHCASTLEMAGIC comes
 *   (ClientBlobPatcher::MSG_PatchCastleMagic, 0x140f1d970: the entry must already be a custom one, at the blob's version + 1).
 * - The data is what CastleMagicBehaviorBase::OnPostLoad (0x141e25dc0) reads: 0xACE1FACE, version 1, a 16-bit count and
 *   per action the spell template, the target template, the target's X and Y as two 16-bit sign and magnitude whole
 *   numbers (X high), and Z as a float (CastleMagicAction, 0x141e255c0). An action's number is its place in the list.
 * - The client sends MSG_CASTLEMAGICCHANGEACTION with every field of the action, also when it changes only the spell
 *   or only the target (0x140eb14f0, 0x140eb1800, 0x140eb1ba0), so the server stores what it sends.
 * - Castle Magic spells are treasure cards (school CastleMagic). Casting one onto an object does not use it up.
 * - MSG_CASTLEMAGICREQUESTRANDOM is answered with 64 random 32-bit numbers (0x140ed65f0 reads 64). Every client in a house
 *   gets the same ones, so random magic (random reflectors) goes the same way for everyone.
 * - MSG_HOUSINGZONEPLAYER tells the players already in a house about one who arrived; the client that runs the magic
 *   answers with MSG_CASTLEMAGICCURRENTSTATE for the new player (0x140eca630), which the server passes on.
 *
 * TODO:
 * - MSG_HOUSINGZONEPLAYERLIST (players already in the house, a serialized CastleMagicPlayersInZone) is not sent yet.
 * - MSG_CASTLEMAGICREQUESTUSE, ALLOWMOUNTS, ALLOWPVP and PVPSTATE are only logged.
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/24/2026
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Housing;

/// <summary>
/// One spell set on a Castle Magic object.
/// </summary>
/// <param name="SpellTemplateId">The Castle Magic spell, or 0 for an empty action.</param>
/// <param name="TargetTemplateId">The template of the object the spell is aimed at, or 0.</param>
/// <param name="X">The target's X, as the client keeps it: a whole number.</param>
/// <param name="Y">The target's Y, as the client keeps it: a whole number.</param>
/// <param name="Z">The target's Z.</param>
internal readonly record struct CastleMagicAction(uint SpellTemplateId, uint TargetTemplateId, float X, float Y, float Z);

/// <summary>
/// A player in a house, as the house's Castle Magic sees them.
/// </summary>
/// <param name="Session">The player's session, which sends a message to their client.</param>
/// <param name="Wizard">The character.</param>
/// <param name="GameObject">The character's game object in the house zone.</param>
internal sealed record CastleMagicVisitor(IActorRef Session, Wizard Wizard, CoreObject GameObject) {

    /// <summary>
    /// The player's game object global id, which the client names players by.
    /// </summary>
    internal ulong GameObjectGid => Wizard.GameObjectID;

    /// <summary>
    /// The player's mobile id, which a server teleport moves.
    /// </summary>
    internal ushort MobileId => GameObject?.m_nMobileID ?? 0;

    /// <summary>
    /// Whether the players already in the house were told about this one.
    /// </summary>
    internal bool Announced { get; set; }

}

internal static class HouseCastleMagic {

    internal const uint Identifier = 0xACE1FACE;
    internal const uint Version = 1;

    // The client reads 64 numbers from the answer to MSG_CASTLEMAGICREQUESTRANDOM.
    internal const int RandomNumberCount = 64;

    // An object whose template does not say how many actions it has gets this many.
    private const int DefaultActionCount = 16;
    private const int MaxActionCount = 64;
    private const int MaxCoordinate = 0x7FFF;
    private const uint NegativeBit = 0x8000;

    private static readonly ConcurrentDictionary<ulong, ConcurrentDictionary<ulong, CastleMagicVisitor>> s_visitors = new();
    private static readonly ConcurrentDictionary<ulong, byte[]> s_randomNumbers = new();
    private static readonly ConcurrentDictionary<uint, bool> s_isObject = new();
    private static readonly Lazy<IReadOnlyDictionary<uint, uint>> s_spellsByHash = new(BuildSpellHashes);
    private static readonly Lazy<IReadOnlyList<uint>> s_objectTemplates = new(() => FindTemplates(IsObject));
    private static readonly Lazy<IReadOnlyList<uint>> s_spellTemplates = new(() => FindTemplates(id => IsSpell(id)));

    /// <summary>
    /// The data of an object with no spells yet.
    /// </summary>
    internal static byte[] Empty => Encode([]);

    /// <summary>
    /// Every Castle Magic object template that can be placed in a house.
    /// </summary>
    internal static IReadOnlyList<uint> ObjectTemplates => s_objectTemplates.Value;

    /// <summary>
    /// Every Castle Magic spell template.
    /// </summary>
    internal static IReadOnlyList<uint> SpellTemplates => s_spellTemplates.Value;

    /// <summary>
    /// Checks whether a template is a Castle Magic object.
    /// </summary>
    /// <param name="templateId">An item template id.</param>
    /// <returns><c>true</c> when the template carries a CastleMagicBehavior.</returns>
    internal static bool IsObject(uint templateId)
        => s_isObject.GetOrAdd(templateId, id => CoreObjectFactory.GetCoreTemplate(id) is WizItemTemplate template
            && (template.m_behaviors ?? []).Any(behavior => behavior is CastleMagicBehaviorTemplate
                || behavior?.m_behaviorName.ToString() == "CastleMagicBehavior"));

    /// <summary>
    /// Checks whether a spell id the client sends names a Castle Magic spell, as a template id or as the hash of its name.
    /// </summary>
    /// <param name="spellId">The id.</param>
    /// <param name="templateId">The spell's template id when this returns <c>true</c>.</param>
    /// <returns><c>true</c> for a Castle Magic spell.</returns>
    internal static bool IsSpell(uint spellId, out uint templateId) {
        templateId = spellId;
        if (CoreObjectFactory.GetCoreTemplate(spellId) is CastleMagicSpellTemplate) {
            return true;
        }

        return s_spellsByHash.Value.TryGetValue(spellId, out templateId);
    }

    private static bool IsSpell(uint templateId) => CoreObjectFactory.GetCoreTemplate(templateId) is CastleMagicSpellTemplate;

    /// <summary>
    /// Finds how many actions an object has.
    /// </summary>
    /// <param name="templateId">The object's template id.</param>
    /// <returns>The template's number of actions, or a default when it does not say.</returns>
    internal static int ActionCountOf(uint templateId) {
        var behavior = CoreObjectFactory.GetCoreTemplate(templateId) is WizItemTemplate template
            ? (template.m_behaviors ?? []).OfType<CastleMagicBehaviorTemplate>().FirstOrDefault()
            : null;

        return behavior is { m_numberOfActions: > 0 and <= MaxActionCount } ? behavior.m_numberOfActions : DefaultActionCount;
    }

    /// <summary>
    /// Reads the spells of a placed object.
    /// </summary>
    /// <param name="item">The placed object.</param>
    /// <returns>Its actions; none when it has no data or the data is not Castle Magic data.</returns>
    internal static List<CastleMagicAction> Read(PlacedItem item) => Decode(PlacedItem.DecodeCustom(item?.Custom));

    /// <summary>
    /// Sets one action of an object.
    /// </summary>
    /// <param name="stored">The placed object.</param>
    /// <param name="actionNumber">The action's number, its place in the list.</param>
    /// <param name="action">The new action.</param>
    /// <param name="data">The object's new data when this returns <c>true</c>.</param>
    /// <param name="refusal">Why the action cannot be set, when this returns <c>false</c>.</param>
    /// <returns><c>true</c> when the action is set.</returns>
    internal static bool TrySetAction(PlacedItem stored, uint actionNumber, CastleMagicAction action, out byte[] data, out string refusal) {
        data = [];
        refusal = null;
        var count = ActionCountOf(stored.TemplateId);
        if (actionNumber >= count) {
            refusal = $"action {actionNumber} of an object with {count} action(s)";

            return false;
        }

        var actions = Read(stored);
        while (actions.Count < count) {
            actions.Add(default);
        }

        actions[(int) actionNumber] = action with { X = Whole(action.X), Y = Whole(action.Y) };
        data = Encode(actions);

        return true;
    }

    /// <summary>
    /// Writes a list of actions the way the client reads it.
    /// </summary>
    internal static byte[] Encode(IReadOnlyList<CastleMagicAction> actions) {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Identifier);
        writer.Write(Version);
        writer.Write((ushort) actions.Count);
        foreach (var action in actions) {
            writer.Write(action.SpellTemplateId);
            writer.Write(action.TargetTemplateId);
            writer.Write((Pack(action.X) << 16) | Pack(action.Y));
            writer.Write(action.Z);
        }

        writer.Flush();

        return stream.ToArray();
    }

    /// <summary>
    /// Reads a list of actions the way the client does.
    /// </summary>
    internal static List<CastleMagicAction> Decode(byte[] data) {
        var actions = new List<CastleMagicAction>();
        if (data is null || data.Length < 10) {
            return actions;
        }

        try {
            using var reader = new BinaryReader(new MemoryStream(data));
            if (reader.ReadUInt32() != Identifier || reader.ReadUInt32() != Version) {
                return actions;
            }

            var count = reader.ReadUInt16();
            for (var index = 0; index < count; index++) {
                var spell = reader.ReadUInt32();
                var target = reader.ReadUInt32();
                var xy = reader.ReadUInt32();
                var z = reader.ReadSingle();
                actions.Add(new CastleMagicAction(spell, target, Unpack(xy >> 16), Unpack(xy & 0xFFFF), z));
            }
        } catch (EndOfStreamException) {
            // A cut off list keeps the actions read so far, as the client's reader does.
        }

        return actions;
    }

    /// <summary>
    /// Counts the actions of an object that hold a spell.
    /// </summary>
    internal static int CountSpells(IEnumerable<CastleMagicAction> actions) => actions.Count(action => action.SpellTemplateId != 0);

    /// <summary>
    /// Counts a player into a house.
    /// </summary>
    /// <param name="lotGid">The house's lot.</param>
    /// <param name="visitor">The player.</param>
    internal static void Enter(ulong lotGid, CastleMagicVisitor visitor)
        => s_visitors.GetOrAdd(lotGid, _ => new ConcurrentDictionary<ulong, CastleMagicVisitor>())[visitor.GameObjectGid] = visitor;

    /// <summary>
    /// Counts a player out of a house.
    /// </summary>
    /// <param name="lotGid">The house's lot.</param>
    /// <param name="gameObjectGid">The player's game object global id.</param>
    /// <returns>The player when they were in the house, otherwise <c>null</c>.</returns>
    internal static CastleMagicVisitor Leave(ulong lotGid, ulong gameObjectGid) {
        if (!s_visitors.TryGetValue(lotGid, out var visitors) || !visitors.TryRemove(gameObjectGid, out var visitor)) {
            return null;
        }

        // An empty house forgets its random numbers, so the next visit draws new ones.
        if (visitors.IsEmpty) {
            s_randomNumbers.TryRemove(lotGid, out _);
        }

        return visitor;
    }

    /// <summary>
    /// Finds a player in a house by the global id the client names them by.
    /// </summary>
    internal static CastleMagicVisitor Find(ulong lotGid, ulong gameObjectGid)
        => s_visitors.TryGetValue(lotGid, out var visitors) && visitors.TryGetValue(gameObjectGid, out var visitor) ? visitor : null;

    /// <summary>
    /// Lists the players in a house but one.
    /// </summary>
    internal static IReadOnlyList<CastleMagicVisitor> Others(ulong lotGid, ulong gameObjectGid)
        => s_visitors.TryGetValue(lotGid, out var visitors)
            ? [.. visitors.Values.Where(visitor => visitor.GameObjectGid != gameObjectGid)]
            : [];

    /// <summary>
    /// Gives the house's pool of random numbers, drawing it the first time.
    /// </summary>
    /// <param name="lotGid">The house's lot.</param>
    /// <returns>64 random 32-bit numbers, little-endian.</returns>
    internal static byte[] RandomNumbers(ulong lotGid)
        => s_randomNumbers.GetOrAdd(lotGid, _ => RandomNumberGenerator.GetBytes(RandomNumberCount * sizeof(uint)));

    private static uint Pack(float value) {
        var whole = (int) Math.Clamp(MathF.Round(value), -MaxCoordinate, MaxCoordinate);

        return whole < 0 ? (uint) -whole | NegativeBit : (uint) whole;
    }

    private static float Unpack(uint packed)
        => (packed & NegativeBit) != 0 ? -(float) (packed & ~NegativeBit & 0xFFFF) : packed;

    private static float Whole(float value) => Unpack(Pack(value));

    private static IReadOnlyDictionary<uint, uint> BuildSpellHashes() {
        var hashes = new Dictionary<uint, uint>();
        foreach (var templateId in SpellTemplates) {
            if (CoreObjectFactory.GetCoreTemplate(templateId) is CastleMagicSpellTemplate spell) {
                string name = spell.m_name;
                if (!string.IsNullOrEmpty(name)) {
                    hashes.TryAdd(StringHash.Compute(name), templateId);
                }
            }
        }

        return hashes;
    }

    private static IReadOnlyList<uint> FindTemplates(Func<uint, bool> isWanted) {
        var found = new List<uint>();
        foreach (var location in CoreObjectFactory.TemplateManifest?.m_serializedTemplates ?? []) {
            string path = location?.m_filename;
            if (path is null || !path.Contains("CastleMagic", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            try {
                if (isWanted(location.m_id)) {
                    found.Add(location.m_id);
                }
            } catch (Exception exception) {
                Logger.Debug("Castle Magic: template {TemplateId} ({Path}) could not be read: {Reason}",
                    Logger.Args(location.m_id, path, exception.Message));
            }
        }

        found.Sort();

        return found;
    }

}
