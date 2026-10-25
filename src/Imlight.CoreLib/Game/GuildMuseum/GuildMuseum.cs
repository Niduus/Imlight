using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Imcodec.Cryptography;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.IO;
using Imlight.Common;
using Imlight.CoreLib.Game.Commands.Protocols;
using Imlight.CoreLib.Game.Housing;
using Imlight.CoreLib.Game.Social;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.GuildMuseum;

/*
 * The Guild Museum (missing-features list, rated 4; Niduus, 2026-10-24): the two galleries behind the museum island of
 * the guild grounds, Avalon and Khrysalis. Each gallery has 22 exhibits, each exhibit a few artifacts (housing items and
 * gear of that world), and each artifact fills with essence from defeating the creature that has it (the game's own
 * "Hobgoblin Plunderer in Caer Lyon" lines). An exhibit goes up a tier when every one of its artifacts has the essence
 * of that tier (the game's m_tier1..3 numbers, added up), so its stand has four looks: empty and tiers 1 to 3.
 *
 * The live game keeps the essence for the whole guild on its chat server. Here it is kept in guild-museum.json next to
 * the server so it lasts through restarts: for the guild (Game/Social/Guilds) with [GuildMuseum] SharedByGuild, so the
 * guild fills its museum together, and for the wizard alone when they are in no guild.
 *
 * The galleries, exhibits and artifacts are read from spiraldb-extra/GuildMuseum/Galleries.json, made from a copy of
 * the game's GuildMuseum/*Gallery.xml and its text by tools/guild-museum/make-galleries.py. Each artifact names its
 * creatures (template ids, from the creature named in its "From" line); an artifact with none (the cantrip chest ones
 * and a few without a line) fills a little from any creature defeated in that world.
 *
 * Which numbers the game's museum messages use for a world and an exhibit is not known yet (IdStyle and `.museum ids`
 * switch between the likely ones). The client's own museum messages are written to the log with the style they fit.
 */
internal static class GuildMuseum {

    internal sealed class GalleryFile {
        public List<Gallery> Galleries { get; set; } = [];
    }

    internal sealed class Gallery {
        public string World { get; set; } = "";
        public string Zone { get; set; } = "";
        public uint DoorTemplateID { get; set; }
        public int LevelScale { get; set; }
        public List<Exhibit> Exhibits { get; set; } = [];
    }

    internal sealed class Exhibit {
        public string Tag { get; set; } = "";
        public string Name { get; set; } = "";
        public string NameKey { get; set; } = "";
        public List<Artifact> Artifacts { get; set; } = [];

        /// <summary>The highest tier any of its artifacts asks essence for (the stand's last look).</summary>
        internal int MaxTier => Artifacts.Count == 0 ? 0 : Artifacts.Max(a => a.MaxTier);
    }

    internal sealed class Artifact {
        public uint TemplateID { get; set; }
        public string Name { get; set; } = "";
        public List<int> Tiers { get; set; } = [];
        public string From { get; set; } = "";
        public List<uint> Creatures { get; set; } = [];

        /// <summary>The essence it needs to reach a tier (1 to 4): its tier numbers up to that one, added up.</summary>
        internal int NeededFor(int tier) => Tiers.Take(Math.Clamp(tier, 0, Tiers.Count)).Sum(t => Math.Max(t, 0));

        internal int Full => NeededFor(Tiers.Count);

        internal int MaxTier {
            get {
                for (var tier = Tiers.Count; tier > 0; tier--) {
                    if (Tiers[tier - 1] > 0) {
                        return tier;
                    }
                }

                return 0;
            }
        }
    }

    /// <summary>The numbers a world and an exhibit go by in the museum messages.</summary>
    internal enum IdStyle {
        /// <summary>StringHash of the world name and of the exhibit tag (Exhibit_A).</summary>
        Hash,
        /// <summary>Their place in the lists, from 0.</summary>
        Index,
        /// <summary>Their place in the lists, from 1.</summary>
        Index1,
        /// <summary>The world's door template id, and the exhibit's place from 0.</summary>
        Door,
    }

    internal const string GUILD_GROUNDS_ZONE = "Housing_Guild/GuildStarter_Ext";
    internal const string MUSEUM_ISLAND_LOCATION = "Target location-ToMuseum";
    internal const string GALLERY_START = "Start";
    // Where a wizard without guild grounds goes back to from a gallery.
    private const string NO_GUILD_ZONE = "WizardCity/WC_Ravenwood";

    internal const string FILE = "guild-museum.json";
    private const string FOLDER = "GuildMuseum";
    private const string GALLERIES_FILE = "Galleries.json";

    internal static bool Enabled => ConfigurationManager.GetValue("GuildMuseum.Enabled", true);
    internal static bool CommandOn => ConfigurationManager.GetValue("GuildMuseum.Command", true);
    internal static bool ChatOn => ConfigurationManager.GetValue("GuildMuseum.ChatLines", true);
    private static int s_perKill => Math.Max(0, ConfigurationManager.GetValue("GuildMuseum.EssencePerKill", 10));
    private static bool s_sharedByGuild => ConfigurationManager.GetValue("GuildMuseum.SharedByGuild", true);
    private static int s_perWorldKill => Math.Max(0, ConfigurationManager.GetValue("GuildMuseum.EssencePerWorldKill", 2));

    private static IdStyle s_idStyle = Enum.TryParse<IdStyle>(ConfigurationManager.GetValue("GuildMuseum.IdStyle", "Hash"), true, out var style)
        ? style
        : IdStyle.Hash;

    private static readonly JsonSerializerOptions s_options = new() {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
    };

    private static readonly object s_lock = new();
    private static List<Gallery> s_galleries;
    private static Dictionary<uint, (Gallery Gallery, Exhibit Exhibit, Artifact Artifact)> s_artifacts;
    private static Dictionary<uint, List<Artifact>> s_byCreature;
    // Essence by wizard (character id) and artifact template id.
    private static Dictionary<string, Dictionary<string, int>> s_essence;

    internal static IdStyle Ids {
        get => s_idStyle;
        set => s_idStyle = value;
    }

    internal static IReadOnlyList<Gallery> Galleries {
        get {
            Load();

            return s_galleries;
        }
    }

    #region Galleries

    private static void Load() {
        if (s_galleries is not null) {
            return;
        }

        lock (s_lock) {
            if (s_galleries is not null) {
                return;
            }

            var galleries = new List<Gallery>();
            var file = Path.Combine(SpiralDB.ExtraPath, FOLDER, GALLERIES_FILE);
            try {
                if (File.Exists(file)) {
                    galleries = JsonSerializer.Deserialize<GalleryFile>(File.ReadAllText(file), s_options)?.Galleries ?? [];
                }
                else {
                    Logger.Warning("Guild Museum | {0} is missing, so the museum has no galleries.", Logger.Args(file));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) {
                Logger.Error("Guild Museum | Could not read {0}: {1}", Logger.Args(file, ex.Message));
            }

            var artifacts = new Dictionary<uint, (Gallery, Exhibit, Artifact)>();
            var byCreature = new Dictionary<uint, List<Artifact>>();
            foreach (var gallery in galleries) {
                foreach (var exhibit in gallery.Exhibits) {
                    foreach (var artifact in exhibit.Artifacts) {
                        // The same item in two exhibits is one artifact; the first exhibit names it.
                        artifacts.TryAdd(artifact.TemplateID, (gallery, exhibit, artifact));
                        foreach (var creature in artifact.Creatures) {
                            if (!byCreature.TryGetValue(creature, out var list)) {
                                byCreature[creature] = list = [];
                            }

                            if (!list.Any(a => a.TemplateID == artifact.TemplateID)) {
                                list.Add(artifact);
                            }
                        }
                    }
                }
            }

            s_artifacts = artifacts;
            s_byCreature = byCreature;
            s_galleries = galleries;
            Logger.Information("Guild Museum | {0} galleries, {1} exhibits, {2} artifacts.",
                Logger.Args(galleries.Count, galleries.Sum(g => g.Exhibits.Count), artifacts.Count));
        }
    }

    internal static Gallery GalleryOf(string world)
        => Galleries.FirstOrDefault(g => string.Equals(g.World, world, StringComparison.OrdinalIgnoreCase));

    internal static Gallery GalleryInZone(string zone)
        => string.IsNullOrEmpty(zone) ? null : Galleries.FirstOrDefault(g => string.Equals(g.Zone, zone, StringComparison.OrdinalIgnoreCase));

    internal static Exhibit ExhibitOf(Gallery gallery, string tag)
        => gallery?.Exhibits.FirstOrDefault(e => string.Equals(e.Tag, tag, StringComparison.OrdinalIgnoreCase));

    /// <summary>Finds a gallery by a word a player types: avalon, khrysalis, av, kr.</summary>
    internal static Gallery FindGallery(string word) {
        word = (word ?? "").Trim();
        if (word.Length == 0) {
            return null;
        }

        return Galleries.FirstOrDefault(g => g.World.StartsWith(word, StringComparison.OrdinalIgnoreCase))
            ?? Galleries.FirstOrDefault(g => word.Equals("kr", StringComparison.OrdinalIgnoreCase) && g.World.StartsWith("Kh", StringComparison.OrdinalIgnoreCase));
    }

    internal static uint WorldId(Gallery gallery, IdStyle? style = null) {
        var index = Galleries.ToList().IndexOf(gallery);

        return (style ?? s_idStyle) switch {
            IdStyle.Index => (uint) Math.Max(index, 0),
            IdStyle.Index1 => (uint) Math.Max(index, 0) + 1,
            IdStyle.Door => gallery.DoorTemplateID,
            _ => StringHash.Compute(gallery.World),
        };
    }

    internal static uint ExhibitId(Gallery gallery, Exhibit exhibit, IdStyle? style = null) {
        var index = gallery.Exhibits.IndexOf(exhibit);

        return (style ?? s_idStyle) switch {
            IdStyle.Index or IdStyle.Door => (uint) Math.Max(index, 0),
            IdStyle.Index1 => (uint) Math.Max(index, 0) + 1,
            _ => StringHash.Compute(exhibit.Tag),
        };
    }

    /// <summary>
    /// Names what a world and exhibit number from the game mean, and which style they fit, for the log. The client's own
    /// numbers tell which IdStyle the museum messages use.
    /// </summary>
    internal static string DescribeIds(uint worldId, uint exhibitId) {
        var fits = new List<string>();
        foreach (var style in Enum.GetValues<IdStyle>()) {
            foreach (var gallery in Galleries) {
                if (WorldId(gallery, style) != worldId) {
                    continue;
                }

                var exhibit = gallery.Exhibits.FirstOrDefault(e => ExhibitId(gallery, e, style) == exhibitId);
                fits.Add(exhibit is null
                    ? $"{style}: world {gallery.World}"
                    : $"{style}: {gallery.World} {exhibit.Tag} ({exhibit.Name})");
            }
        }

        return fits.Count == 0 ? "fits no style" : string.Join("; ", fits);
    }

    #endregion

    #region Essence

    private static Dictionary<string, Dictionary<string, int>> Store() {
        if (s_essence is not null) {
            return s_essence;
        }

        try {
            s_essence = File.Exists(FILE)
                ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(File.ReadAllText(FILE), s_options)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) {
            Logger.Error("Guild Museum | Could not read {0}, starting empty: {1}", Logger.Args(FILE, ex.Message));
        }

        s_essence ??= [];

        return s_essence;
    }

    private static void Save() {
        try {
            var temp = $"{FILE}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(s_essence, s_options));
            File.Move(temp, FILE, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Error("Guild Museum | Could not save {0}: {1}", Logger.Args(FILE, ex.Message));
        }
    }

    private static string Key(ulong value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Whose essence a wizard adds to and sees: their guild's ("g" and its id) with [GuildMuseum] SharedByGuild, as in
    /// the live game, otherwise their own.
    /// </summary>
    private static string OwnerKey(ulong charId)
        => s_sharedByGuild && Guilds.For(charId) is { } guild ? "g" + Key(guild.Id) : Key(charId);

    internal static int EssenceOf(ulong charId, Artifact artifact) {
        lock (s_lock) {
            return Store().TryGetValue(OwnerKey(charId), out var mine) && mine.TryGetValue(Key(artifact.TemplateID), out var essence)
                ? Math.Clamp(essence, 0, artifact.Full)
                : 0;
        }
    }

    /// <summary>The tier an exhibit reached for a wizard: 0 (empty) up to its last tier.</summary>
    internal static int TierOf(ulong charId, Exhibit exhibit) {
        var max = exhibit.MaxTier;
        for (var tier = max; tier > 0; tier--) {
            if (exhibit.Artifacts.All(a => EssenceOf(charId, a) >= a.NeededFor(tier))) {
                return tier;
            }
        }

        return 0;
    }

    /// <summary>
    /// Adds the essence of a won battle: each defeated creature fills the artifacts it has, and any creature defeated in
    /// a gallery's world fills that gallery's artifacts that name no creature. Returns the lines to show the wizard.
    /// </summary>
    internal static List<string> OnCombatWin(ulong charId, string zone, IReadOnlyCollection<ulong> mobTemplateIds) {
        var lines = new List<string>();
        if (!Enabled || mobTemplateIds is null || mobTemplateIds.Count == 0) {
            return lines;
        }

        Load();
        var gains = new Dictionary<uint, int>();
        foreach (var mob in mobTemplateIds) {
            if (mob <= uint.MaxValue && s_byCreature.TryGetValue((uint) mob, out var artifacts)) {
                foreach (var artifact in artifacts) {
                    gains[artifact.TemplateID] = gains.GetValueOrDefault(artifact.TemplateID) + s_perKill;
                }
            }
        }

        var world = (zone ?? "").Split('/')[0];
        var worldGallery = GalleryOf(world);
        var worldKills = worldGallery is null ? 0 : mobTemplateIds.Count;
        if (worldKills > 0 && s_perWorldKill > 0) {
            foreach (var artifact in worldGallery.Exhibits.SelectMany(e => e.Artifacts).Where(a => a.Creatures.Count == 0)) {
                gains[artifact.TemplateID] = gains.GetValueOrDefault(artifact.TemplateID) + s_perWorldKill * worldKills;
            }
        }

        if (gains.Count == 0) {
            return lines;
        }

        var tiersBefore = new Dictionary<Exhibit, int>();
        var changed = new List<(Artifact Artifact, int Gained, int Now)>();
        lock (s_lock) {
            foreach (var (templateId, _) in gains) {
                var exhibit = s_artifacts[templateId].Exhibit;
                tiersBefore.TryAdd(exhibit, TierOf(charId, exhibit));
            }

            var store = Store();
            var owner = OwnerKey(charId);
            if (!store.TryGetValue(owner, out var mine)) {
                store[owner] = mine = [];
            }

            foreach (var (templateId, gain) in gains) {
                var artifact = s_artifacts[templateId].Artifact;
                var before = Math.Clamp(mine.GetValueOrDefault(Key(templateId)), 0, artifact.Full);
                var now = Math.Min(artifact.Full, before + gain);
                if (now > before) {
                    mine[Key(templateId)] = now;
                    changed.Add((artifact, now - before, now));
                }
            }

            if (changed.Count > 0) {
                Save();
            }
        }

        if (changed.Count == 0) {
            return lines;
        }

        Logger.Information("Guild Museum | {0} gained essence for {1} artifacts: {2}.",
            Logger.Args(charId, changed.Count, string.Join(", ", changed.Select(c => $"{c.Artifact.Name} +{c.Gained} ({c.Now}/{c.Artifact.Full})"))));

        // A few named lines, the creatures' own artifacts first; a battle that filled many at once (the world's share)
        // gets one line for the rest.
        changed = [.. changed.OrderByDescending(c => c.Artifact.Creatures.Count > 0)];
        foreach (var (artifact, gained, now) in changed.Take(4)) {
            lines.Add($"Museum: +{gained} {artifact.Name} essence ({now}/{artifact.Full}).");
        }

        if (changed.Count > 4) {
            lines.Add($"Museum: and essence for {changed.Count - 4} more artifacts.");
        }

        foreach (var (exhibit, before) in tiersBefore) {
            var after = TierOf(charId, exhibit);
            if (after > before) {
                lines.Add(after >= exhibit.MaxTier
                    ? $"Museum: the {exhibit.Name} is complete!"
                    : $"Museum: the {exhibit.Name} reached tier {after} of {exhibit.MaxTier}!");
            }
        }

        return lines;
    }

    /// <summary>Sets every artifact of a gallery (or all) to what a tier needs, for testing. Returns how many changed.</summary>
    internal static int SetTier(ulong charId, Gallery gallery, int tier) {
        var count = 0;
        lock (s_lock) {
            var store = Store();
            var owner = OwnerKey(charId);
            if (!store.TryGetValue(owner, out var mine)) {
                store[owner] = mine = [];
            }

            foreach (var g in gallery is null ? Galleries : [gallery]) {
                foreach (var artifact in g.Exhibits.SelectMany(e => e.Artifacts)) {
                    mine[Key(artifact.TemplateID)] = artifact.NeededFor(tier);
                    count++;
                }
            }

            Save();
        }

        return count;
    }

    #endregion

    #region What the game is sent

    /// <summary>
    /// The essence list the curator window is given (GuildGalleryEssenceList): the artifacts of the exhibit (or of the
    /// whole gallery for the podium and the curator) and the essence each has.
    /// </summary>
    internal static bool TryWriteEssenceList(ulong charId, Gallery gallery, Exhibit exhibit, out ByteString data) {
        var artifacts = (exhibit is null ? gallery.Exhibits.SelectMany(e => e.Artifacts) : exhibit.Artifacts)
            .DistinctBy(a => a.TemplateID)
            .ToList();
        var list = new GuildGalleryEssenceList {
            m_templateList = [.. artifacts.Select(a => a.TemplateID)],
            m_essenceList = [.. artifacts.Select(a => EssenceOf(charId, a))],
        };

        var serializer = new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.None);
        if (serializer.Serialize(list, PropertyFlags.Prop_Public, out data)) {
            return true;
        }

        Logger.Error("Guild Museum | Could not write the essence list of {0}.", Logger.Args(gallery.World));

        return false;
    }

    /// <summary>The exhibit states of a gallery for a wizard, as the museum messages number them.</summary>
    internal static IEnumerable<(uint WorldId, uint ExhibitId, uint State, Exhibit Exhibit)> StatesOf(ulong charId, Gallery gallery) {
        var worldId = WorldId(gallery);
        foreach (var exhibit in gallery.Exhibits) {
            yield return (worldId, ExhibitId(gallery, exhibit), (uint) TierOf(charId, exhibit), exhibit);
        }
    }

    #endregion

    #region Words

    /// <summary>The lines a wizard is shown at an exhibit stand: its tier and each artifact's essence and source.</summary>
    internal static List<string> ExhibitLines(ulong charId, Exhibit exhibit) {
        var tier = TierOf(charId, exhibit);
        var lines = new List<string> {
            tier >= exhibit.MaxTier && tier > 0
                ? $"{exhibit.Name}: complete (tier {tier} of {exhibit.MaxTier})."
                : $"{exhibit.Name}: tier {tier} of {exhibit.MaxTier}.",
        };
        var next = Math.Min(tier + 1, exhibit.MaxTier);
        foreach (var artifact in exhibit.Artifacts) {
            var essence = EssenceOf(charId, artifact);
            var goal = artifact.NeededFor(next);
            var from = string.IsNullOrEmpty(artifact.From)
                ? "any creature in this world"
                : artifact.From;
            lines.Add(essence >= artifact.Full
                ? $"- {artifact.Name}: full ({essence}). From: {from}."
                : $"- {artifact.Name}: {essence}/{Math.Max(goal, essence)} for tier {next}. From: {from}.");
        }

        return lines;
    }

    /// <summary>A line for a whole gallery: how many exhibits are started and complete.</summary>
    internal static string GalleryLine(ulong charId, Gallery gallery) {
        var tiers = gallery.Exhibits.Select(e => (Tier: TierOf(charId, e), Max: e.MaxTier)).ToList();
        var complete = tiers.Count(t => t.Max > 0 && t.Tier >= t.Max);
        var started = tiers.Count(t => t.Tier > 0);

        return $"{gallery.World} Gallery: {complete} of {gallery.Exhibits.Count} exhibits complete, {started} with a tier.";
    }

    #endregion

    #region Travel

    /// <summary>The transfer a gallery's door (or `.museum avalon`) gives a wizard: the gallery's start.</summary>
    internal static ZONE_102_PROTOCOL.MSG_ZONETRANSFER EntryTransfer(Gallery gallery, Wizard wizard) => new() {
        DestinationZone = gallery.Zone,
        DestinationLocation = GALLERY_START,
        SendToClient = true,
        OwnerCharId = wizard?.CharId ?? 0,
    };

    /// <summary>
    /// The transfer a gallery's way out gives a wizard: the museum island of their guild's grounds (the leader's), or
    /// of their own guild grounds, or Ravenwood for a wizard with neither.
    /// </summary>
    internal static ZONE_102_PROTOCOL.MSG_ZONETRANSFER ReturnTransfer(Wizard wizard) {
        if (wizard is not null && Guilds.For(wizard.CharId) is { } guild
            && CommandGuildProtocol.TryGetGuildHouseEntry(wizard, guild, out var entry, out _)) {
            return new() {
                DestinationZone = entry.Zone,
                DestinationLocation = MUSEUM_ISLAND_LOCATION,
                SendToClient = true,
                IsPrivate = true,
                OwnerCharId = entry.OwnerCharId,
                HouseEntry = entry,
            };
        }

        var ownGrounds = wizard?.HouseLots?.Keys.Any(id => HouseTemplates.TryGet(id, out var house)
            && string.Equals(house.LotZone, GUILD_GROUNDS_ZONE, StringComparison.OrdinalIgnoreCase)) == true;

        return new() {
            DestinationZone = ownGrounds ? GUILD_GROUNDS_ZONE : NO_GUILD_ZONE,
            DestinationLocation = ownGrounds ? MUSEUM_ISLAND_LOCATION : GALLERY_START,
            SendToClient = true,
            OwnerCharId = wizard?.CharId ?? 0,
        };
    }

    #endregion

}
