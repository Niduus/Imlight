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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Packs;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Events;

/// <summary>
/// The game's other events (Niduus, 2026-10-25: "Implement other events that exist in the files like the scrolls"),
/// run from the game's own event templates, Root.wad ClassProjects/*.xml (ClassProjectTemplate, 171 of them):
///   Scroll of Fortune   &lt;Year&gt;&lt;Season&gt;SOF, a season pass: always on, like the live game, the one whose
///                       m_autoStart date and m_maximumLengthInDays cover today. A point for each creature beaten;
///                       68 reward tiers claimed from its window.
///   Queued events       the Events card of the Imlight app queues them like the Deckathalon (GameEvents.cs):
///                       Side Activity, Pet Activity, Castle Tours (SpiralScene7), Wizard City Day (WC1Day) and the
///                       six Monthly Gauntlets (MonthlyGauntletEvent_001-006).
/// Each template says what gives points (m_activityList: an activity type, the points it gives, how many count a day,
/// and for creatures the adjectives one must have) and the rewards (m_rewardList: the points they need and the items
/// the window shows, m_featuredTemplateID; the loot tables they name are not in the game files, so the shown items
/// are what is given). A template's m_initialHoliday is a game registry entry that is 1 while it is on: the Wizard
/// City portals of the Monthly Gauntlets (Teleport_ME_*) ask for ME_Sinbad and the others.
/// Points, today's counts and claimed rewards are kept per event in classprojects.json next to the server.
/// The Deckathalon (TreasureTower_01_*) keeps its own points (Deckathalon.cs).
/// </summary>
internal static class ClassProjects {

    internal const string PROGRESS_FILE = "classprojects.json";
    private const string TEMPLATE_FOLDER = "ClassProjects/";

    internal static readonly bool ScrollOfFortuneOn = ConfigurationManager.GetValue("Events.ScrollOfFortune", true);

    // The Scroll of Fortune's paid rewards (CPRT_Purchased) need the scroll bought for crowns in the live game; on our
    // server everyone has it unless this is off.
    internal static readonly bool PremiumForEveryone = ConfigurationManager.GetValue("Events.ScrollOfFortunePremium", true);

    /// <summary>
    /// The events the Events card can queue, by the name it saves as the Kind, and the template each one runs. A
    /// Monthly Gauntlet's School is which gauntlet.
    /// </summary>
    internal static readonly (string Kind, string Variant, string Template)[] Queueable = [
        ("Side Activity", "", "SideActivity"),
        ("Pet Activity", "", "PetActivity7"),
        ("Castle Tours", "", "SpiralScene7"),
        ("Wizard City Day", "", "WC1Day"),
        ("Monthly Gauntlet", "Sinbad", "MonthlyGauntletEvent_001"),
        ("Monthly Gauntlet", "Spiral Cup", "MonthlyGauntletEvent_002"),
        ("Monthly Gauntlet", "Great Clock", "MonthlyGauntletEvent_003"),
        ("Monthly Gauntlet", "Grand Tourney", "MonthlyGauntletEvent_004"),
        ("Monthly Gauntlet", "Fantastic Voyage", "MonthlyGauntletEvent_005"),
        ("Monthly Gauntlet", "Accursed Play", "MonthlyGauntletEvent_006"),
    ];

    /// <summary>An event that is on: its template and when it started and ends.</summary>
    /// <param name="Ending">The event is over and stays in the window for its rewards until End + a week: no more points.</param>
    internal sealed record Running(ClassProjectTemplate Template, DateTime Start, DateTime End, string Shown, bool Ending = false) {
        internal string Name => Template.m_projectName.ToString();
        internal uint Id => StringHash.Compute(Name);
        internal string Key => $"{Name}@{Start:yyyy-MM-ddTHH:mm}";
        internal bool IsScroll => Template.m_isSeasonPass != 0 || Template.m_projectType == ClassProjectType.CPT_SeasonPass;
    }

    internal sealed class Progress {
        public string Name { get; set; } = "";
        public int Points { get; set; }
        // The day (UTC, yyyy-MM-dd) of Today: how many of each activity of the template counted that day.
        public string Day { get; set; } = "";
        public List<int> Today { get; set; } = [];
        public List<int> Claimed { get; set; } = [];
    }

    private sealed class ProgressFile {
        public Dictionary<string, Dictionary<string, Progress>> Events { get; set; } = [];
    }

    private static readonly object s_lock = new();
    private static Dictionary<string, ClassProjectTemplate> s_templates;
    private static ProgressFile s_progress;

    #region Templates

    /// <summary>Every event template of the game, by m_projectName.</summary>
    internal static IReadOnlyDictionary<string, ClassProjectTemplate> Templates {
        get {
            if (s_templates is not null) {
                return s_templates;
            }

            lock (s_lock) {
                if (s_templates is not null) {
                    return s_templates;
                }

                var templates = new Dictionary<string, ClassProjectTemplate>(StringComparer.OrdinalIgnoreCase);
                try {
                    foreach (var (entry, data) in RootArchiveLoader.GetDirectoryStream(TEMPLATE_FOLDER)) {
                        if (data is not { } bytes) {
                            continue;
                        }

                        try {
                            if (new BindSerializer().Deserialize(bytes.ToArray(), out ClassProjectTemplate template)
                                && template is not null && !template.m_projectName.IsEmpty) {
                                templates[template.m_projectName.ToString()] = template;
                            }
                        }
                        catch (Exception ex) {
                            Logger.Warning("Events | could not read an event template: {0}", Logger.Args(ex.Message));
                        }
                    }
                }
                catch (Exception ex) {
                    Logger.Error("Events | reading the game's event templates failed: {0}", Logger.Args(ex.Message));
                }

                Logger.Information("Events | {0} event templates in the game files.", Logger.Args(templates.Count));
                s_templates = templates;

                return s_templates;
            }
        }
    }

    internal static ClassProjectTemplate TemplateOf(string name)
        => name is not null && Templates.TryGetValue(name, out var template) ? template : null;

    internal static ClassProjectTemplate TemplateOf(uint id)
        => Templates.Values.FirstOrDefault(t => StringHash.Compute(t.m_projectName.ToString()) == id);

    /// <summary>The template a queued event runs (Side Activity, Monthly Gauntlet + Sinbad, ...), or null.</summary>
    internal static string TemplateNameOf(string kind, string variant) {
        foreach (var (k, v, template) in Queueable) {
            if (string.Equals(k, kind?.Trim(), StringComparison.OrdinalIgnoreCase)
                && (v == "" || string.Equals(v.Replace(" ", ""), (variant ?? "").Replace(" ", "").Trim(), StringComparison.OrdinalIgnoreCase))) {
                return template;
            }
        }

        // Any other template by its own name, for testers editing events.json by hand.
        return TemplateOf(kind) is { } own ? own.m_projectName.ToString() : null;
    }

    /// <summary>A template's name in English (its m_projectTitle), or its own name.</summary>
    internal static string TitleOf(ClassProjectTemplate template) {
        var key = template?.m_projectTitle.ToString();
        var title = string.IsNullOrEmpty(key) ? null : Locale.GetEnglishName(key);

        return string.IsNullOrWhiteSpace(title) ? template?.m_projectName.ToString() ?? "" : title;
    }

    #endregion

    #region Which events are on

    /// <summary>The Scroll of Fortune of today: the newest one that has started, as long as it lasts, else the newest.</summary>
    internal static Running Scroll(DateTime now) {
        if (!ScrollOfFortuneOn) {
            return null;
        }

        var scrolls = Templates.Values
            .Where(t => t.m_projectName.ToString().EndsWith("SOF", StringComparison.OrdinalIgnoreCase)
                && !t.m_projectName.ToString().StartsWith("TEST", StringComparison.OrdinalIgnoreCase)
                && t.m_autoStartYear > 2000 && t.m_autoStartMonth is >= 1 and <= 12)
            .Select(t => (Template: t, Start: new DateTime(t.m_autoStartYear, t.m_autoStartMonth, Math.Clamp(t.m_autoStartDay, 1, 28), 0, 0, 0, DateTimeKind.Utc)))
            .OrderByDescending(s => s.Start)
            .ToList();
        var started = scrolls.Where(s => s.Start <= now).ToList();
        if (started.Count == 0) {
            return null;
        }

        var (template, start) = started[0];
        var end = start.AddDays(Math.Max(1, template.m_maximumLengthInDays));

        // The game files have no scroll for after the newest one ends: it then goes round again, a season at a time.
        while (end <= now) {
            start = end;
            end = start.AddDays(Math.Max(1, template.m_maximumLengthInDays));
        }

        return new Running(template, start, end, "Scroll of Fortune");
    }

    /// <summary>
    /// The events of the game's templates in the event window: the Scroll of Fortune and the queued events that are
    /// on, then those that ended in the last week (Ending), whose rewards can still be claimed.
    /// </summary>
    internal static List<Running> Active() {
        var now = DateTime.UtcNow;
        var active = new List<Running>();
        if (Scroll(now) is { } scroll) {
            active.Add(scroll);

            // Last season's scroll, for a week after it ended.
            if (Scroll(now - TimeSpan.FromDays(GameEvents.ENDED_DAYS)) is { } last && last.Id != scroll.Id) {
                active.Add(last with { Ending = true });
            }
        }

        foreach (var current in GameEvents.Running.Where(e => !e.IsDeckathalon)) {
            if (TemplateOf(TemplateNameOf(current.Kind, current.School)) is { } template) {
                active.Add(new Running(template, current.Start, current.End, current.Name));
            }
        }

        foreach (var ended in GameEvents.Ended.Where(e => !e.IsDeckathalon)) {
            if (TemplateOf(TemplateNameOf(ended.Kind, ended.School)) is { } template && !active.Any(a => a.Id == StringHash.Compute(template.m_projectName.ToString()))) {
                active.Add(new Running(template, ended.Start, ended.End, ended.Name, Ending: true));
            }
        }

        return active;
    }

    internal static Running ActiveOf(uint projectId) => Active().FirstOrDefault(r => r.Id == projectId);

    /// <summary>Whether an event is on now (not one that has ended and only waits for its rewards).</summary>
    internal static bool IsActive(string projectName)
        => !string.IsNullOrEmpty(projectName)
           && Active().Any(r => !r.Ending && string.Equals(r.Name, projectName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The game registry value of a queueable event's holiday entry (m_initialHoliday, e.g. ME_Sinbad): 1 while its
    /// event is on, else 0; null for an entry none of them names (the battlegrounds' entries stay the server's own).
    /// </summary>
    internal static float? HolidayValue(string entry) {
        if (string.IsNullOrEmpty(entry)
            || !Queueable.Any(q => string.Equals(TemplateOf(q.Template)?.m_initialHoliday.ToString(), entry, StringComparison.OrdinalIgnoreCase))) {
            return null;
        }

        return Active().Any(r => !r.Ending && string.Equals(r.Template.m_initialHoliday.ToString(), entry, StringComparison.OrdinalIgnoreCase)) ? 1 : 0;
    }

    #endregion

    #region Points

    /// <summary>
    /// Something a wizard did that events count (ClassProjectActivityType): every event on with that activity gives its
    /// points, up to the activity's count a day, and shows the game's "+points" toast. For a beaten creature,
    /// adjectives are the creature's (an activity with adjectives counts only creatures that have one of them).
    /// </summary>
    internal static void Record(IActorRef client, Wizard wizard, ClassProjectActivityType type, int times = 1, IReadOnlyCollection<string> adjectives = null) {
        if (wizard is null || times <= 0) {
            return;
        }

        try {
            foreach (var running in Active().Where(r => !r.Ending)) {
                RecordIn(client, wizard, running, type, times, adjectives);
            }
        }
        catch (Exception ex) {
            Logger.Error("Events | counting {0} for {1} failed: {2}", Logger.Args(type, wizard.CharId, ex));
        }
    }

    /// <summary>A won battle: each creature beaten counts for the events that count creatures.</summary>
    internal static void CreaturesBeaten(IActorRef client, Wizard wizard, IEnumerable<ulong> templateIds) {
        foreach (var id in templateIds ?? []) {
            var adjectives = (CoreObjectFactory.GetCoreTemplate((uint) id) as GameObjectTemplate)?.m_adjectiveList?
                .Select(a => a.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
            Record(client, wizard, ClassProjectActivityType.CPT_DefeatMob, 1, adjectives);
        }
    }

    private static void RecordIn(IActorRef client, Wizard wizard, Running running, ClassProjectActivityType type, int times,
        IReadOnlyCollection<string> adjectives) {
        var activities = running.Template.m_activityList ?? [];
        // The Double Scroll of Fortune Points member benefit doubles the points of the Scroll of Fortune.
        var factor = running.Template.m_projectName.ToString().EndsWith("SOF", StringComparison.OrdinalIgnoreCase)
            && Tools.MemberBenefits.IsOn(Tools.MemberBenefits.DOUBLE_SOF) ? 2 : 1;
        var gained = 0;
        Progress progress = null;
        for (var i = 0; i < activities.Count; i++) {
            var activity = activities[i];
            if (activity is null || activity.m_activityType != type || !Matches(activity, adjectives)) {
                continue;
            }

            progress ??= ProgressOf(running.Key, wizard);
            lock (s_lock) {
                NewDay(progress, activities.Count);
                var count = times;
                if (activity.m_activityLimit > 0) {
                    count = Math.Min(count, activity.m_activityLimit - progress.Today[i]);
                }

                if (count <= 0) {
                    continue;
                }

                progress.Today[i] += count;
                var points = count * Math.Max(1, activity.m_activityValue) * factor;
                progress.Points += points;
                gained += points;
            }
        }

        if (gained <= 0) {
            return;
        }

        Save();
        client?.Tell(new WIZARD2_53_PROTOCOL.MSG_UPDATECLASSPROJECTSTATUS {
            Status = 1,
            ProjectID = running.Id,
            Count = (uint) Math.Max(0, progress.Points),
            Points = (uint) gained,
            HasUnclaimedReward = (byte) (Unclaimed(running, progress).Count > 0 ? 1 : 0),
        });
    }

    // An activity that names adjectives counts only creatures with one of them (PoPFinalBoss: a Monthly Gauntlet's boss).
    private static bool Matches(ClassProjectActivity activity, IReadOnlyCollection<string> adjectives) {
        var wanted = activity.m_adjectiveList?.Select(a => a.ToString()).Where(a => !string.IsNullOrEmpty(a)).ToList() ?? [];

        return wanted.Count == 0 || (adjectives is not null && wanted.Any(adjectives.Contains));
    }

    private static void NewDay(Progress progress, int activities) {
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        if (progress.Day != today) {
            progress.Day = today;
            progress.Today = [];
        }

        while (progress.Today.Count < activities) {
            progress.Today.Add(0);
        }
    }

    /// <summary>A wizard's points and counts in an event, or null before they have any.</summary>
    internal static Progress Find(Running running, ulong charId) {
        lock (s_lock) {
            return File().Events.TryGetValue(running.Key, out var byWizard) && byWizard.TryGetValue(charId.ToString(), out var progress)
                ? progress
                : null;
        }
    }

    /// <summary>Today's count of each activity of the event, for the window's "count/limit".</summary>
    internal static List<int> TodayOf(Running running, Progress progress) {
        lock (s_lock) {
            return progress is not null && progress.Day == DateTime.UtcNow.ToString("yyyy-MM-dd") ? [.. progress.Today] : [];
        }
    }

    /// <summary>The best wizards of an event.</summary>
    internal static List<Progress> Top(Running running, int count) {
        lock (s_lock) {
            return File().Events.TryGetValue(running.Key, out var byWizard)
                ? [.. byWizard.Values.Where(p => p.Points > 0).OrderByDescending(p => p.Points).Take(count)]
                : [];
        }
    }

    #endregion

    #region Rewards

    /// <summary>The reward tiers a wizard has the points for and has not claimed yet.</summary>
    internal static List<int> Unclaimed(Running running, Progress progress) {
        var rewards = running.Template.m_rewardList ?? [];
        var ready = new List<int>();
        if (progress is null) {
            return ready;
        }

        lock (s_lock) {
            for (var i = 0; i < rewards.Count; i++) {
                if (rewards[i] is { } reward && progress.Points >= reward.m_goalCount && !progress.Claimed.Contains(i) && Allowed(reward)) {
                    ready.Add(i);
                }
            }
        }

        return ready;
    }

    private static bool Allowed(ClassProjectReward reward)
        => reward.m_rewardType != ClassProjectRewardType.CPRT_Purchased || PremiumForEveryone;

    /// <summary>
    /// The window's claim button (MSG_CLASSPROJECTCLAIMREWARD): gives the reward tier's items once, when the wizard has
    /// its points. Returns what to tell them.
    /// </summary>
    internal static string Claim(IActorRef session, Wizard wizard, uint projectId, int index) {
        if (ActiveOf(projectId) is not { } running) {
            return "That event is not on.";
        }

        var rewards = running.Template.m_rewardList ?? [];
        if (index < 0 || index >= rewards.Count || rewards[index] is not { } reward) {
            return "There is no such reward.";
        }

        var progress = ProgressOf(running.Key, wizard);
        lock (s_lock) {
            if (progress.Claimed.Contains(index)) {
                return "You already have that reward.";
            }

            if (progress.Points < reward.m_goalCount) {
                return $"That reward needs {reward.m_goalCount} points; you have {progress.Points}.";
            }

            if (!Allowed(reward)) {
                return "That reward is for the bought scroll.";
            }

            progress.Claimed.Add(index);
        }

        Save();
        var given = Give(session, wizard, reward);
        Logger.Information("Events | {0} claimed reward {1} of {2}: {3}", Logger.Args(wizard.CharId, index + 1, running.Name, string.Join(", ", given)));

        return given.Count == 0
            ? $"{running.Shown}: reward {index + 1} claimed."
            : $"{running.Shown}: reward {index + 1} claimed: {string.Join(", ", given)}.";
    }

    /// <summary>Claims every reward a wizard has the points for, in every event on (the .events claim command).</summary>
    internal static List<string> ClaimAll(IActorRef session, Wizard wizard) {
        var lines = new List<string>();
        foreach (var running in Active()) {
            foreach (var index in Unclaimed(running, Find(running, wizard.CharId))) {
                lines.Add(Claim(session, wizard, running.Id, index));
            }
        }

        return lines;
    }

    // The items the window shows for the reward: gold, packs (opened), bundles (their items) and items.
    private static List<string> Give(IActorRef session, Wizard wizard, ClassProjectReward reward) {
        var given = new List<string>();
        foreach (var id in reward.m_featuredTemplateID ?? []) {
            try {
                GiveTemplate(session, wizard, id, 0, given);
            }
            catch (Exception ex) {
                Logger.Error("Events | could not give {0} to {1}: {2}", Logger.Args(id, wizard.CharId, ex));
            }
        }

        return given;
    }

    private static void GiveTemplate(IActorRef session, Wizard wizard, ulong id, int depth, List<string> given) {
        var template = CoreObjectFactory.GetCoreTemplate((uint) id);
        switch (template) {
            case ItemBundleTemplate bundle when depth < 4:
                foreach (ulong inner in bundle.m_bundleItems.Concat(bundle.m_bundleFreeItems ?? [])) {
                    GiveTemplate(session, wizard, inner, depth + 1, given);
                }

                return;
            case BoosterPackTemplate:
                PackManager.OpenPack(session, wizard, (uint) id);
                given.Add(NameOf(template, id));

                return;
            case GoldAmountTemplate gold:
                wizard.AddGold(gold.m_goldAmount);
                session.Tell(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD {
                    Gold = wizard.GameStats.m_currentGold,
                    MaxGold = wizard.GameStats.m_baseGoldPouch,
                });
                given.Add($"{gold.m_goldAmount:N0} gold");

                return;
            case LunariAmountTemplate lunari:
                wizard.AddLunari(lunari.m_lunariAmount);
                session.Tell(new WIZARD2_53_PROTOCOL.MSG_UPDATEEVENTCURRENCY1 {
                    EventCurrency1 = wizard.GameStats.m_currentEventCurrency1,
                    MaxEventCurrency1 = wizard.GameStats.m_baseEventCurrency1Pouch,
                });
                given.Add($"{lunari.m_lunariAmount} Lunari");

                return;
            case null or CurrencyTemplate or TourneyTokensAmountTemplate:
                Logger.Warning("Events | reward item {0} is not something the server can give; skipped.", Logger.Args(id));

                return;
        }

        if (!wizard.AddItemToInventory(id, out WizClientObjectItem item)) {
            Logger.Warning("Events | could not add {0} to the backpack of {1}.", Logger.Args(id, wizard.CharId));

            return;
        }

        ItemSync.SendAdded(session, wizard, item);
        given.Add(NameOf(template, id));
    }

    private static string NameOf(CoreTemplate template, ulong id) {
        var key = (template as WizItemTemplate)?.m_displayName.ToString();
        var name = string.IsNullOrEmpty(key) ? null : Locale.GetEnglishName(key);

        return string.IsNullOrWhiteSpace(name) ? $"item {id}" : name;
    }

    /// <summary>
    /// The claimed rewards as the window reads them (read from the client's reward window, 2026-10-25): a reward is
    /// reached when its m_goalCount is at most ClassProjectPlayer.m_totalProgress; for a season pass (CPT_SeasonPass,
    /// the Scroll of Fortune) it is claimed when bit i%8 of byte i/8 of m_extendedData is set, for any other event when
    /// bit i of m_flags is (the first 16 rewards). The client marks a claim itself when it sends it.
    /// </summary>
    internal static List<byte> ClaimedBits(Progress progress, out ushort flags) {
        flags = 0;
        var bits = new List<byte>();
        if (progress is null) {
            return bits;
        }

        lock (s_lock) {
            foreach (var index in progress.Claimed.Where(i => i >= 0)) {
                while (bits.Count <= index / 8) {
                    bits.Add(0);
                }

                bits[index / 8] |= (byte) (1 << (index % 8));
                if (index < 16) {
                    flags |= (ushort) (1 << index);
                }
            }
        }

        return bits;
    }

    #endregion

    #region The file

    private static ProgressFile File() {
        lock (s_lock) {
            if (s_progress is not null) {
                return s_progress;
            }

            try {
                s_progress = System.IO.File.Exists(PROGRESS_FILE)
                    ? JsonSerializer.Deserialize<ProgressFile>(System.IO.File.ReadAllText(PROGRESS_FILE)) ?? new ProgressFile()
                    : new ProgressFile();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
                Logger.Warning("Events | could not read {0} ({1}); starting with no points.", Logger.Args(PROGRESS_FILE, ex.Message));
                s_progress = new ProgressFile();
            }

            s_progress.Events ??= [];

            return s_progress;
        }
    }

    private static Progress ProgressOf(string key, Wizard wizard) {
        var file = File();
        lock (s_lock) {
            if (!file.Events.TryGetValue(key, out var byWizard)) {
                byWizard = [];
                file.Events[key] = byWizard;
            }

            var id = wizard.CharId.ToString();
            if (!byWizard.TryGetValue(id, out var progress)) {
                progress = new Progress();
                byWizard[id] = progress;
            }

            progress.Name = wizard.PlayerNameBehavior?.GetWizardName() ?? progress.Name;
            progress.Today ??= [];
            progress.Claimed ??= [];

            return progress;
        }
    }

    private static void Save() {
        try {
            string json;
            lock (s_lock) {
                json = JsonSerializer.Serialize(File(), new JsonSerializerOptions { WriteIndented = true });
            }

            var temp = $"{PROGRESS_FILE}.{Guid.NewGuid():N}.tmp";
            System.IO.File.WriteAllText(temp, json);
            System.IO.File.Move(temp, PROGRESS_FILE, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Warning("Events | could not save {0}: {1}", Logger.Args(PROGRESS_FILE, ex.Message));
        }
    }

    #endregion

}
