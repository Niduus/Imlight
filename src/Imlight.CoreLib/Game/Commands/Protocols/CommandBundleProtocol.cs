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
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.Types;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.bundle list [words]` lists the game's bundles; `.bundle <name>` puts every tier of that bundle in your gift box,
/// one gift each, so you redeem the tier you want (Niduus, 2026-10-21).
/// </summary>
internal class CommandBundleProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    // Names in the notice when several bundles match.
    private const int MAX_NAMES_SHOWN = 5;


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
            // The list lives in the Imlight app now (Niduus, 2026-10-21: scrap the chat list, search in the app).
            InformSenderClient("The bundle list is in the Imlight app: Lookup tab, pick Bundle, and search.");

            return;
        }

        if (text.Equals("clear", StringComparison.OrdinalIgnoreCase)) {
            Clear();

            return;
        }

        var matches = BundleCatalog.Find(text);
        if (matches.Count == 0) {
            InformSenderClient($"No bundle is called \"{text}\". Type .bundle list to see them all.");

            return;
        }

        if (matches.Count > 1) {
            var names = string.Join(", ", matches.Take(MAX_NAMES_SHOWN).Select(f => f.Name));
            var more = matches.Count > MAX_NAMES_SHOWN ? $" and {matches.Count - MAX_NAMES_SHOWN} more" : "";
            InformSenderClient($"More than one bundle matches \"{text}\": {names}{more}. Type the full name, or search in the Imlight app's Lookup tab.");

            return;
        }

        Send(matches[0]);
    }

    // Empties the gift button list (Niduus, 2026-10-21: older .bundle gifts put every tier there one by one).
    private void Clear() {
        var account = Context.Character.Account;
        if (account is null) {
            return;
        }

        var gifts = account.ClaimGifts.ToList();
        foreach (var gift in gifts) {
            AccountCollection.RemoveClaimGift(account.AccountId, gift.GiftId);
        }

        account.ClaimGifts.Clear();
        Logger.Information("Bundle: cleared {0} gifts of account {1}.", Logger.Args(gifts.Count, account.AccountId));
        InformSenderClient($"Removed {gifts.Count} gifts from your gift button.");
    }

    private void Send(BundleFamily family) {
        var account = Context.Character.Account;
        if (account is null) {
            InformSenderClient($"Could not send {family.Name}.");

            return;
        }

        // The game's gift button list: a set's master bundle opens a picker there, so the player claims the tier
        // they want (Niduus, 2026-10-21: the friend gift box was the wrong one and claiming crashed the game).
        foreach (var templateId in family.Gifts) {
            GiftingService.AddClaimGift(account, templateId);
        }

        // The game shows the gift button only when Gift is exactly 1 (GameClient::MSG_NotifyGift); it is a flag, not a count.
        Context.SessionActor.Tell(new GAME_5_PROTOCOL.MSG_NOTIFY_GIFT { Gift = 1 }, ActorRefs.NoSender);
        Logger.Information("Bundle: \"{0}\" sent {1} gifts to account {2}.", Logger.Args(family.Name, family.Gifts.Count, account.AccountId));
        InformSenderClient($"Sent {family.Name} to your gifts: click the gift button, claim it and pick the tier you want.");
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

    // AcquireConditionType.kChooseOne: the game shows its "choose one" window when the gift is claimed.
    private const int CHOOSE_ONE = 1;

    private static List<BundleFamily> s_families;

    // Things the game sold with a set that its bundle files do not list (the game data does not link houses, mounts
    // and pets to sets). Keyed by the set's object name prefix; each is one more gift.
    private static readonly Dictionary<string, ulong[]> s_extras = new(StringComparer.OrdinalIgnoreCase) {
        // Spooky Carnival (Niduus, 2026-10-21): Scarousel, 4-Passenger Creepy Clown Car (PERM), Booloon.
        ["DM2"] = [1421671, 1254268, 1254264],
    };

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
            // A set's pick-one bundles (gear and wand) open the game's tier picker, so they are the only gifts; the
            // set's loose tier bundles would each be another entry in the gift list (Niduus, 2026-10-21).
            var members = group.Members.Where(m => (int) m.Template.m_acquireCondition == CHOOSE_ONE).ToList();
            if (members.Count == 0) {
                members = group.Members;
            }

            // Gear before wands, then lowest level first.
            foreach (var entry in members.OrderBy(m => m.ObjectName.Contains("-Wand-", StringComparison.OrdinalIgnoreCase))) {
                AddGifts(entry.Id, bundles, gifts, 0);
            }

            if (group.Prefix is not null && s_extras.TryGetValue(group.Prefix, out var extras)) {
                gifts.AddRange(extras);
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
    /// Each bundle is one gift; the game lets the player pick inside a choose-one bundle (a set's master).
    /// </summary>
    private static void AddGifts(ulong id, Dictionary<ulong, Entry> bundles, List<ulong> gifts, int depth)
        => gifts.Add(id);

    private static IEnumerable<ulong> Contents(ItemBundleTemplate bundle)
        => (bundle.m_bundleItems ?? []).Concat(bundle.m_bundleFreeItems ?? []).Select(g => (ulong) g);

}
