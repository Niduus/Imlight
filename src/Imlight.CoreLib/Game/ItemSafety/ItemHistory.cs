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
 * ITEM HISTORY
 * ========================================================================
 *
 * PURPOSE:
 * Niduus, 2026-10-28: "some items are disappearing in inventories, make safety protocols". Every item that leaves a
 * wizard for good (thrown away, sold, used up, run out, placed in a house, or found nowhere for a new item) gets a
 * record first, with a full copy of the item, in the ItemBackups collection. A record can give the item back.
 *
 * The server also appends one line per record to item-history.jsonl next to the server, which Ahziael's Home card
 * "Lost and removed items" reads. Its "Give back" button writes the record's id to item-restore-requests.txt;
 * the server marks the record and hands the item over the next time that wizard logs in or changes area.
 *
 * USAGE EXAMPLE:
 * ItemHistory.Record(wizard.CharId, item, ItemRecordKind.Removed, "sold to a shop");
 *
 * NOTE:
 * Writing a record never stops what the caller does: a record that cannot be written is logged and skipped.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Common;
using Imlight.CoreLib.Game.Bank;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.Shared.Utilities;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Databases;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents;

namespace Imlight.CoreLib.Game.ItemSafety;

/// <summary>
/// Why an item left a wizard.
/// </summary>
public enum ItemRecordKind {

    // A new item found no room anywhere and was never stored.
    Lost,
    // The item was destroyed: thrown away, sold, used up, fed, or a rental or elixir that ran out.
    Removed,
    // The item went somewhere the wizard can still reach: a house, the attic, a vault, the Bazaar.
    Moved,
    // The item check at login found the item gone with no record of why.
    Missing,
    // A record's item was given back.
    Restored,
    // Only in item-history.jsonl: Ahziael asked to give a record's item back at the next login.
    Waiting,

}

/// <summary>
/// One item that left a wizard, with a copy of it so it can be given back.
/// </summary>
public sealed class ItemBackup {

    public string Id { get; set; }
    public string BackupId { get; set; }
    public DateTime Time { get; set; }
    public ulong CharId { get; set; }
    public ulong ItemId { get; set; }
    public ulong TemplateId { get; set; }
    public string ItemName { get; set; }
    public ItemRecordKind Kind { get; set; }
    public string Reason { get; set; }
    // Null when only the template is known; giving it back then makes a new item of the template.
    public WizClientObjectItem Item { get; set; }
    public bool Restored { get; set; }
    public bool PendingRestore { get; set; }
    // On a Restored record: the record whose item was given back.
    public string Of { get; set; }

}

public static class ItemHistory {

    internal const string HISTORY_FILE = "item-history.jsonl";
    internal const string RESTORE_REQUEST_FILE = "item-restore-requests.txt";
    private const string ID_PREFIX = "ItemBackups/";

    private static readonly object s_fileLock = new();
    // Wizards with a "Give back" waiting, so the login check only asks the database for them.
    private static readonly ConcurrentDictionary<ulong, byte> s_pending = new();
    private static int s_pendingLoaded;

    private static IDocumentStore Store => PlayerDatabase.Instance.Store;

    /// <summary>
    /// Writes a record with a copy of an item that leaves a wizard. Never throws.
    /// </summary>
    /// <param name="charId">The wizard the item belonged to.</param>
    /// <param name="item">The item, as it was.</param>
    /// <param name="kind">Why it left.</param>
    /// <param name="reason">Plain words for Ahziael, or null for the code that removed it.</param>
    /// <returns>The record, or null if it could not be written.</returns>
    public static ItemBackup Record(ulong charId, WizClientObjectItem item, ItemRecordKind kind, string reason = null,
                                    [CallerMemberName] string caller = "", [CallerFilePath] string file = "") {
        if (item is null) {
            return null;
        }

        return Record(charId, item.m_globalID.Full, item.m_templateID.Full, item, kind, reason ?? Describe(caller, file));
    }

    /// <summary>
    /// Writes a record for an item known only by its ids, or with a copy when one is given. Never throws.
    /// </summary>
    public static ItemBackup Record(ulong charId, ulong itemId, ulong templateId, WizClientObjectItem item, ItemRecordKind kind, string reason,
                                    string of = null) {
        var backup = NewBackup(charId, itemId, templateId, item, kind, reason, of);
        if (!TrySave(backup) && item is not null) {
            // The copy is what failed to store: the record without it still says what was lost.
            backup.Item = null;
            TrySave(backup);
        }

        Written(backup);

        return backup;
    }

    /// <summary>
    /// Writes a record with a copy of each item, all in one save, so a big Sell More does not wait on a save
    /// per item (Niduus, 2026-10-28). Never throws.
    /// </summary>
    public static void RecordMany(ulong charId, IReadOnlyCollection<WizClientObjectItem> items, ItemRecordKind kind, string reason) {
        var backups = items.Where(item => item is not null)
            .Select(item => NewBackup(charId, item.m_globalID.Full, item.m_templateID.Full, item, kind, reason ?? "", null))
            .ToList();
        if (backups.Count == 0) {
            return;
        }

        try {
            using var session = Store.OpenSession();
            foreach (var backup in backups) {
                session.Store(backup, backup.Id);
            }

            session.SaveChanges();
        }
        catch (Exception ex) {
            Logger.Error("Item history: could not store {0} records at once, storing them one by one: {1}",
                Logger.Args(backups.Count, ex.Message));
            foreach (var backup in backups.Where(backup => !TrySave(backup))) {
                backup.Item = null;
                TrySave(backup);
            }
        }

        foreach (var backup in backups) {
            Written(backup);
        }
    }

    private static ItemBackup NewBackup(ulong charId, ulong itemId, ulong templateId, WizClientObjectItem item, ItemRecordKind kind,
                                        string reason, string of) {
        var backup = new ItemBackup {
            Of = of,
            BackupId = NewBackupId(),
            Time = DateTime.UtcNow,
            CharId = charId,
            ItemId = itemId,
            TemplateId = templateId,
            ItemName = NameOf(templateId),
            Kind = kind,
            Reason = reason ?? "",
            Item = item,
        };
        backup.Id = ID_PREFIX + backup.BackupId;

        return backup;
    }

    // The file line and the log line for a stored record.
    private static void Written(ItemBackup backup) {
        var (templateId, itemId, charId, kind) = (backup.TemplateId, backup.ItemId, backup.CharId, backup.Kind);
        AppendLine(backup);
        if (kind is ItemRecordKind.Lost or ItemRecordKind.Missing) {
            Logger.Warning("Item history {0}: {1} ({2}, item {3}) of {4} is {5}: {6}.",
                Logger.Args(backup.BackupId, backup.ItemName, templateId, itemId, charId, kind, backup.Reason));
        }
        else {
            Logger.Information("Item history {0}: {1} ({2}, item {3}) of {4} {5}: {6}.",
                Logger.Args(backup.BackupId, backup.ItemName, templateId, itemId, charId, kind, backup.Reason));
        }
    }

    private static bool TrySave(ItemBackup backup) {
        try {
            using var session = Store.OpenSession();
            session.Store(backup, backup.Id);
            session.SaveChanges();

            return true;
        }
        catch (Exception ex) {
            Logger.Error("Item history: could not store record {0} of item {1}: {2}",
                Logger.Args(backup.BackupId, backup.ItemId, ex.Message));

            return false;
        }
    }

    /// <summary>
    /// Finds a record by the id players and Ahziael see.
    /// </summary>
    public static ItemBackup Find(string backupId) {
        if (string.IsNullOrWhiteSpace(backupId)) {
            return null;
        }

        using var session = Store.OpenSession();

        return session.Load<ItemBackup>(ID_PREFIX + backupId.Trim());
    }

    /// <summary>
    /// The newest records of a wizard, newest first.
    /// </summary>
    public static List<ItemBackup> RecentFor(ulong charId, int count) {
        using var session = Store.OpenSession();

        return [.. session.Query<ItemBackup>()
            .Where(backup => backup.CharId == charId)
            .OrderByDescending(backup => backup.Time)
            .Take(count)];
    }

    /// <summary>
    /// The ids of items with a record of this wizard since a time, which explains why they are gone.
    /// </summary>
    internal static HashSet<ulong> RecordedSince(ulong charId, DateTime since) {
        using var session = Store.OpenSession();

        return [.. session.Query<ItemBackup>()
            .Customize(query => query.WaitForNonStaleResults(TimeSpan.FromSeconds(15)))
            .Where(backup => backup.CharId == charId && backup.Time >= since)
            .Select(backup => backup.ItemId)
            .ToList()];
    }

    // ---------------------------------------------------------------------
    // Giving items back
    // ---------------------------------------------------------------------

    /// <summary>
    /// Takes the "Give back" requests Ahziael wrote and marks their records, so each item is handed over the next
    /// time its wizard logs in or changes area. Called by ManagerCommands twice a second.
    /// </summary>
    internal static void PollRestoreRequests() {
        LoadPendingOnce();
        if (!File.Exists(RESTORE_REQUEST_FILE)) {
            return;
        }

        string[] lines;
        try {
            var taken = RESTORE_REQUEST_FILE + ".running";
            File.Move(RESTORE_REQUEST_FILE, taken, overwrite: true);
            lines = File.ReadAllLines(taken);
            File.Delete(taken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Ahziael is still writing it; the next round reads it.
            return;
        }

        foreach (var id in lines.Select(line => line.Trim()).Where(line => line.Length > 0).Distinct()) {
            try {
                using var session = Store.OpenSession();
                var backup = session.Load<ItemBackup>(ID_PREFIX + id);
                if (backup is null || backup.Restored) {
                    Logger.Warning("Item history: Ahziael asked to give back record {0}, which {1}.",
                        Logger.Args(id, backup is null ? "does not exist" : "was already given back"));

                    continue;
                }

                backup.PendingRestore = true;
                session.SaveChanges();
                s_pending[backup.CharId] = 0;
                AppendWaiting(backup);
                Logger.Information("Item history: {0} ({1}) goes back to {2} the next time they log in or change area.",
                    Logger.Args(backup.ItemName, id, backup.CharId));
            }
            catch (Exception ex) {
                Logger.Error("Item history: could not mark record {0} to be given back: {1}", Logger.Args(id, ex.Message));
            }
        }
    }

    // Records marked in an earlier run still go back.
    private static void LoadPendingOnce() {
        if (System.Threading.Interlocked.Exchange(ref s_pendingLoaded, 1) == 1) {
            return;
        }

        Task.Run(() => {
            try {
                using var session = Store.OpenSession();
                foreach (var charId in session.Query<ItemBackup>().Where(backup => backup.PendingRestore).Select(backup => backup.CharId).ToList()) {
                    s_pending[charId] = 0;
                }
            }
            catch (Exception ex) {
                Logger.Error("Item history: could not read the waiting give-backs: {0}", Logger.Args(ex.Message));
            }
        });
    }

    /// <summary>
    /// Hands over the items Ahziael asked to give back to this wizard. Called while the wizard's object is built,
    /// so the game sees them in the backpack or bank it is sent.
    /// </summary>
    internal static void DeliverPending(Wizard wizard) {
        if (wizard is null || !s_pending.TryRemove(wizard.CharId, out _)) {
            return;
        }

        try {
            List<ItemBackup> waiting;
            using (var session = Store.OpenSession()) {
                waiting = [.. session.Query<ItemBackup>()
                    .Customize(query => query.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                    .Where(backup => backup.CharId == wizard.CharId && backup.PendingRestore)];
            }

            foreach (var backup in waiting) {
                TryRestore(wizard, backup, session: null, out var message);
                Logger.Information("Item history: give back of {0} to {1}: {2}", Logger.Args(backup.BackupId, wizard.CharId, message));
            }
        }
        catch (Exception ex) {
            Logger.Error("Item history: could not give back the waiting items of {0}: {1}", Logger.Args(wizard.CharId, ex.Message));
        }
    }

    /// <summary>
    /// Gives a record's item back to its wizard: the same item when it still exists unclaimed or was copied, else a
    /// new item of its template. It goes to the backpack, then the bank, then the backpack past its limit.
    /// </summary>
    /// <param name="wizard">The wizard the record belongs to.</param>
    /// <param name="backup">The record.</param>
    /// <param name="session">The wizard's session to tell the game, or null while the wizard object is being built.</param>
    /// <param name="message">What happened, in plain words.</param>
    /// <returns>True if the item was given back.</returns>
    internal static bool TryRestore(Wizard wizard, ItemBackup backup, IActorRef session, out string message) {
        if (backup.CharId != wizard.CharId) {
            message = "that record belongs to another wizard";

            return false;
        }

        if (backup.Restored) {
            message = "it was already given back";
            ClearPending(backup);

            return false;
        }

        // A house, attic, vault or Bazaar still has it, so giving it back would make a second one.
        if (backup.Kind is ItemRecordKind.Moved or ItemRecordKind.Restored) {
            message = backup.Kind == ItemRecordKind.Moved ? $"it was not lost, it was {backup.Reason}" : "that record is a give back itself";
            ClearPending(backup);

            return false;
        }

        var itemId = backup.ItemId;
        if (wizard.InventoryBehavior.HasItem(itemId) || wizard.StorageBehavior.HasItem(itemId)
            || wizard.EquipmentBehavior.EquippedItems?.Any(item => item.m_globalID.Full == itemId) == true) {
            message = "the wizard already has it";
            MarkRestored(backup);

            return false;
        }

        var item = backup.Item;
        var stored = WizardItemCollection.GetItemOfAnyOwner(itemId);
        if (stored is not null && stored.m_characterId.Full == wizard.CharId) {
            // Still stored for this wizard but in none of its lists: that is the item itself.
            item = stored;
            WizardItemCollection.RemoveItem(wizard.CharId, itemId, record: false);
        }
        else if (stored is not null && item is not null) {
            // Someone else holds an item with that id now, so the copy comes back as a new item.
            item.m_globalID = RandomGen.GenerateGUID();
        }

        if (item is null && !wizard.TryCreateItem(backup.TemplateId, out item)) {
            message = $"template {backup.TemplateId} is not an item the server can make";

            return false;
        }

        item.m_characterId = (GID) wizard.CharId;
        var placement = session is null ? ItemPlacement.Place(wizard, item) : ItemPlacement.Give(session, wizard, item);
        if (placement == Placement.Lost) {
            message = "it could not be stored";

            return false;
        }

        MarkRestored(backup);
        Record(wizard.CharId, item.m_globalID.Full, item.m_templateID.Full, null, ItemRecordKind.Restored,
            $"given back to the {placement switch {
                Placement.Bank => "bank",
                Placement.SharedBank => "shared bank",
                _ => "backpack",
            }}", of: backup.BackupId);
        message = placement switch {
            Placement.Bank => "it is in the bank",
            Placement.SharedBank => "it is in the shared bank",
            _ => "it is in the backpack",
        };

        return true;
    }

    private static void MarkRestored(ItemBackup backup) {
        backup.Restored = true;
        backup.PendingRestore = false;
        try {
            using var session = Store.OpenSession();
            var stored = session.Load<ItemBackup>(backup.Id);
            if (stored is not null) {
                stored.Restored = true;
                stored.PendingRestore = false;
                session.SaveChanges();
            }
        }
        catch (Exception ex) {
            Logger.Error("Item history: could not mark record {0} as given back: {1}", Logger.Args(backup.BackupId, ex.Message));
        }
    }

    private static void ClearPending(ItemBackup backup) {
        if (backup.PendingRestore) {
            MarkRestored(backup);
        }
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static string NewBackupId() => Guid.NewGuid().ToString("N")[..10];

    internal static string NameOf(ulong templateId) {
        try {
            if (CoreObjectFactory.GetCoreTemplate(templateId) is WizItemTemplate template) {
                string key = template.m_displayName;
                var shown = string.IsNullOrEmpty(key) ? "" : Locale.GetEnglishName(key);

                return string.IsNullOrWhiteSpace(shown) ? (string) template.m_objectName ?? $"item {templateId}" : shown;
            }
        }
        catch {
            // The id is still written.
        }

        return $"item {templateId}";
    }

    // "ReceiveTrashInventoryItem in InventoryService" when the caller gave no words.
    private static string Describe(string caller, string file)
        => $"{caller} in {Path.GetFileNameWithoutExtension(file?.Replace('\\', '/') ?? "")}";

    // Ahziael shows "waiting" on the record until the wizard logs in or changes area.
    private static void AppendWaiting(ItemBackup backup)
        => AppendLine(new ItemBackup {
            BackupId = NewBackupId(), Time = DateTime.UtcNow, CharId = backup.CharId, ItemId = backup.ItemId,
            TemplateId = backup.TemplateId, ItemName = backup.ItemName, Kind = ItemRecordKind.Waiting,
            Reason = "waiting to be given back", Of = backup.BackupId,
        });

    private static void AppendLine(ItemBackup backup) {
        try {
            var line = JsonSerializer.Serialize(new {
                Id = backup.BackupId,
                Time = backup.Time.ToString("o"),
                CharId = backup.CharId.ToString(),
                ItemId = backup.ItemId.ToString(),
                TemplateId = backup.TemplateId.ToString(),
                Name = backup.ItemName,
                Kind = backup.Kind.ToString(),
                backup.Reason,
                Copy = backup.Item is not null,
                Of = backup.Of ?? "",
            });
            lock (s_fileLock) {
                File.AppendAllText(HISTORY_FILE, line + Environment.NewLine);
            }
        }
        catch (Exception ex) {
            Logger.Error("Item history: could not write {0}: {1}", Logger.Args(HISTORY_FILE, ex.Message));
        }
    }

}
