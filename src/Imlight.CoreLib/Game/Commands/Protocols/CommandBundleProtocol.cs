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
using System.Text;
using System.Text.RegularExpressions;
using Akka.Actor;
using Imcodec.ObjectProperty;
using Imcodec.Types;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.bundle list [words]` lists the game's bundles; `.bundle <name>` puts every tier of that bundle in your gift box,
/// one gift each, so you redeem the tier you want (Justin, 2026-10-21).
/// </summary>
internal class CommandBundleProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    private const int NAMES_PER_MESSAGE = 40;

    private const uint GIFT_PROPERTY_FLAGS = 31;

    private static readonly ObjectSerializer s_giftSerializer = new(Versionable: true, Behaviors: SerializerFlags.None);

    [Command("bundle")]
    [Alias("bundles")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void BundleCommand([Remainder] string text = "") {
        text = (text ?? "").Trim();
        var families = BundleCatalog.Families;
        if (families.Count == 0) {
            InformSenderClient("No bundles were found in the game files.");

            return;
        }

        if (text.Length == 0 || text.Equals("list", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("list ", StringComparison.OrdinalIgnoreCase)) {
            var filter = text.Length > 4 ? text[5..].Trim() : "";
            SendList(families.Where(f => filter.Length == 0 || f.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList(), filter);

            return;
        }

        var matches = BundleCatalog.Find(text);
        if (matches.Count == 0) {
            InformSenderClient($"No bundle is called \"{text}\". Type .bundle list to see them all.");

            return;
        }

        if (matches.Count > 1) {
            InformSenderClient($"More than one bundle matches \"{text}\". Type the full name of one of these:");
            SendList(matches, text);

            return;
        }

        Send(matches[0]);
    }

    private void SendList(List<BundleFamily> families, string filter) {
        if (families.Count == 0) {
            InformSenderClient($"No bundle name has \"{filter}\" in it.");

            return;
        }

        InformSenderClient($"{families.Count} bundles. Type .bundle <name> to get one in your gift box:");
        for (var start = 0; start < families.Count; start += NAMES_PER_MESSAGE) {
            InformSenderClient(string.Join("; ", families.Skip(start).Take(NAMES_PER_MESSAGE)
                .Select(f => $"{f.Name} ({f.Gifts.Count})")));
        }
    }

    private void Send(BundleFamily family) {
        var wizard = Context.Character;
        var accountId = wizard.AccountId;
        var now = (ulong) DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sent = 0;

        foreach (var templateId in family.Gifts) {
            var redemption = new CrownShopGiftRedemption {
                m_giverAccountID = accountId,
                m_itemCount = 1,
                m_itemId = templateId,
                m_itemTransactionId = Guid.NewGuid().ToString("N"),
            };

            if (!s_giftSerializer.Serialize(redemption, GIFT_PROPERTY_FLAGS, out var serialized)) {
                Logger.Error("Bundle: could not serialize the gift for template {0}.", Logger.Args(templateId));
                continue;
            }

            var mail = new Mail {
                m_recipientId = accountId,
                m_senderId = (GID) wizard.CharId,
                m_messageData = Encoding.Latin1.GetString((byte[]) serialized),
                m_expireDuration = 0,
                m_messageType = 2,
                // One second apart, so the gift box keeps them in tier order.
                m_timeStamp = now + (ulong) sent,
                m_mailId = RandomMailId(),
            };

            // Saved here; the session adds it to the open account and tells the client.
            if (!AccountCollection.AddGiftMail(accountId, mail)) {
                Logger.Error("Bundle: could not save the gift for template {0}.", Logger.Args(templateId));
                continue;
            }

            Context.SessionActor.Tell(new SERVICE_101_PROTOCOL.MSG_DELIVER_GIFT { Mail = mail }, ActorRefs.NoSender);
            sent++;
        }

        Logger.Information("Bundle: \"{0}\" sent {1} gifts to account {2}.", Logger.Args(family.Name, sent, accountId));
        InformSenderClient(sent == 0
            ? $"Could not send {family.Name}."
            : $"Sent {family.Name} to your gift box: {sent} gifts, lowest tier first. Open the gift box and claim the tier you want.");
    }

    private static ulong RandomMailId() {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        Random.Shared.NextBytes(bytes);

        return BitConverter.ToUInt64(bytes);
    }

}

/// <summary>
/// One name in `.bundle list`: a set's bundles (all its tiers, gear and wand) or one shop bundle with its level
/// versions ("Sultan's Attire", "Sultan's Attire 10+", ...).
/// </summary>
internal sealed record BundleFamily(string Name, List<ulong> Gifts);

/// <summary>
/// Reads every ItemBundleTemplate in the game files once and groups them by name.
/// </summary>
internal static class BundleCatalog {

    private static readonly Regex s_setPrefix = new(@"^(.+?)-Bundle-", RegexOptions.Compiled);

    private static readonly Regex s_levelSuffix = new(@"\s+\d+\+$", RegexOptions.Compiled);

    private static readonly object s_lock = new();

    private static List<BundleFamily> s_families;

    public static List<BundleFamily> Families {
        get {
            lock (s_lock) {
                return s_families ??= Build();
            }
        }
    }

    /// <summary>
    /// The exact name, else the only family whose name starts with or has the text, else every one that has it.
    /// A template id or a set prefix (GS7) works too.
    /// </summary>
    public static List<BundleFamily> Find(string text) {
        var families = Families;
        var exact = families.Where(f => f.Name.Equals(text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0) {
            return exact;
        }

        if (ulong.TryParse(text, out var id)) {
            return families.Where(f => f.Gifts.Contains(id)).Take(1).ToList();
        }

        if (s_byPrefix.TryGetValue(text, out var byPrefix)) {
            return [byPrefix];
        }

        var starts = families.Where(f => f.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (starts.Count == 1) {
            return starts;
        }

        var contains = families.Where(f => f.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();

        return contains.Count == 1 || starts.Count == 0 ? contains : starts;
    }

    private static readonly Dictionary<string, BundleFamily> s_byPrefix = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Entry(ulong Id, string ObjectName, string Name, ItemBundleTemplate Template);

    private static List<BundleFamily> Build() {
        var bundles = new Dictionary<ulong, Entry>();
        foreach (var location in CoreObjectFactory.TemplateManifest.m_serializedTemplates) {
            string path = location?.m_filename;
            if (path is null || !path.Contains("Bundle", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            try {
                if (CoreObjectFactory.LoadTemplateFile(path) is ItemBundleTemplate bundle) {
                    string displayKey = bundle.m_displayName;
                    string objectName = bundle.m_objectName;
                    objectName ??= "";
                    var name = string.IsNullOrEmpty(displayKey) ? "" : Locale.GetEnglishName(displayKey);
                    bundles[location.m_id] = new(location.m_id, objectName, string.IsNullOrEmpty(name) ? objectName : name, bundle);
                }
            }
            catch (Exception ex) {
                Logger.Debug("Bundle list skipped {0}. {1}", Logger.Args(path, ex.Message));
            }
        }

        // Only bundles that no other bundle holds; a held tier comes along with its master.
        var held = bundles.Values.SelectMany(b => Contents(b.Template)).ToHashSet();
        var groups = new Dictionary<string, (List<Entry> Members, string Prefix)>(StringComparer.OrdinalIgnoreCase);
        var prefixNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var set in bundles.Values.Where(b => s_setPrefix.IsMatch(b.ObjectName)).GroupBy(b => s_setPrefix.Match(b.ObjectName).Groups[1].Value)) {
            var main = set.FirstOrDefault(b => b.ObjectName.EndsWith("-Bundle-Gear-Master", StringComparison.OrdinalIgnoreCase))
                ?? set.OrderBy(b => b.Id).First();
            prefixNames[set.Key] = main.Name;
        }

        foreach (var entry in bundles.Values.Where(b => !held.Contains(b.Id)).OrderBy(b => b.Id)) {
            var match = s_setPrefix.Match(entry.ObjectName);
            var prefix = match.Success ? match.Groups[1].Value : null;
            var name = prefix is not null ? prefixNames[prefix] : s_levelSuffix.Replace(entry.Name, "");
            if (string.IsNullOrWhiteSpace(name)) {
                continue;
            }

            if (!groups.TryGetValue(name, out var group)) {
                groups[name] = group = ([], prefix);
            }

            group.Members.Add(entry);
        }

        var families = new List<BundleFamily>();
        foreach (var (name, group) in groups) {
            var gifts = new List<ulong>();
            // Gear before wands, then lowest level first.
            foreach (var entry in group.Members.OrderBy(m => m.ObjectName.Contains("-Wand-", StringComparison.OrdinalIgnoreCase))) {
                AddGifts(entry.Id, bundles, gifts, 0);
            }

            if (gifts.Count == 0) {
                continue;
            }

            var family = new BundleFamily(name, gifts.Distinct().ToList());
            families.Add(family);
            if (group.Prefix is not null) {
                s_byPrefix[group.Prefix] = family;
            }
        }

        Logger.Information("Bundle list: {0} bundles in {1} names.", Logger.Args(bundles.Count, families.Count));

        return [.. families.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// A choose-one bundle (a set's master) becomes one gift per choice; any other bundle is one gift.
    /// </summary>
    private static void AddGifts(ulong id, Dictionary<ulong, Entry> bundles, List<ulong> gifts, int depth) {
        if (bundles.TryGetValue(id, out var entry) && depth < 4 && (int) entry.Template.m_acquireCondition == 1) {
            foreach (var inner in Contents(entry.Template)) {
                AddGifts(inner, bundles, gifts, depth + 1);
            }

            return;
        }

        gifts.Add(id);
    }

    private static IEnumerable<ulong> Contents(ItemBundleTemplate bundle)
        => (bundle.m_bundleItems ?? []).Concat(bundle.m_bundleFreeItems ?? []).Select(g => (ulong) g);

}
