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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Battlegrounds;

/*
 * Beastmoon Hunt. In this client Beastmoon is built on the game's Battleground system: two teams (red and blue) in beast
 * forms take control points in an arena zone in real time and fight at them. It is not a board game.
 *
 * What the client tells us:
 * - a match is a BattlegroundTemplate in Root.wad's Battlegrounds/ folder (Beastmoon_PVP_01..25, Beastmoon_PVE_01..25):
 *   its zone (m_battlegroundZone), the two team starts (m_teamStartA "Red_Start", m_teamStartB "Blue_Start"), the
 *   countdown and match time, the forms it allows (m_unlockedPolymorphTags, "Life Cyclops") and its loot tables. The
 *   guild raids (Game/Raids) are BattlegroundTemplates in the same folder.
 * - a form is a WizPolymorphTemplate in ObjectData/Polymorphs/BG-Polymorph-<Beast>-<School>-0N.xml: the creature it
 *   stands for (m_mobTemplateID), the look (a TransformationEffectInfo, race Transformation_BG_Cyclops_Life) and its
 *   PolymorphBattlegroundsBehaviorTemplate (pips, hand size, battle cards).
 * - the arena's own trigger script (triggers.xml of Battlegrounds/BMH/BMH_Zone_02..05) does nearly everything else:
 *   team teleporters, flags, gates, pip and wisp spawns. It checks the player with effects nothing in the zone gives,
 *   so the server keeps them:
 *     BattlegroundsTeamAEffect   the red team (Trigger_Teleport_RedBase (1) asks for it; A is red, B is blue)
 *     BattlegroundsTeamBEffect   the blue team
 *     DeathEffect                a wizard who lost a fight (the base teleporters refuse them: WizardActorDialogue_00000836)
 *     BG-PlanningPhase           the countdown before the match (WizardActorDialogue_00000837)
 *   and ReqHasPolymorphEffect: only a wizard in a beast form may leave the base.
 *
 * Here (stage 1 of the port): [Beastmoon] Enabled; the team and these effects per wizard (ReqHasEffect and
 * ReqHasPolymorphEffect answer from them); the beast form (BeastForm.cs); the arena teleporters, whose ResTeleport
 * names no destination (BattlegroundTeleports.cs); the compass step (ResBattlegroundHandlers.cs); and `.bg` to go into
 * an arena as a beast for testing (CommandBattlegroundProtocol). Capture, points, the match and its queue come later.
 */
internal static class Battlegrounds {

    internal static bool Enabled => ConfigurationManager.GetValue("Beastmoon.Enabled", true);
    // The countdown before a match, in seconds; below 0 takes the match's own (m_countdownSeconds, 90).
    internal static int CountdownSeconds => ConfigurationManager.GetValue("Beastmoon.CountdownSeconds", -1);
    // Seconds after a match ends before everyone goes back.
    internal static int ReturnSeconds => Math.Max(5, ConfigurationManager.GetValue("Beastmoon.ReturnSeconds", 30));

    // Where a match sends everyone back to (ClassProjects/Beastmoon_PVP_Event_NN m_eventTeleportZone and Location).
    internal const string RETURN_ZONE = "WizardCity/WC_Duel_Arena";
    internal const string RETURN_LOCATION = "From_BG";
    // The event the arenas end the match on (Trigger_EndofMatch, Trigger_PostBattle), as the raids do.
    internal const string END_BATTLE_EVENT = "EVENT_Battleground_EndBattle";
    // Posted by a PvE arena when its last boss falls (Trigger_On_Boss_Death_03); nothing in the zone waits for it.
    internal const string PVE_COMPLETE_EVENT = "EVENT_PVE_Complete";

    internal const string ZONE_PREFIX = "Battlegrounds/";
    internal const string TEMPLATE_FOLDER = "Battlegrounds/";
    internal const string FORM_FOLDER = "ObjectData/Polymorphs/";
    internal const string FORM_PREFIX = "BG-Polymorph-";

    // The team starts every arena has (BattlegroundTemplate m_teamStartA / m_teamStartB, BattlegroundSigilTemplate).
    internal const string RED_START = "Red_Start";
    internal const string BLUE_START = "Blue_Start";

    internal const string TEAM_A_EFFECT = "BattlegroundsTeamAEffect";
    internal const string TEAM_B_EFFECT = "BattlegroundsTeamBEffect";
    internal const string DEATH_EFFECT = "DeathEffect";
    internal const string PLANNING_EFFECT = "BG-PlanningPhase";

    // The effects the arena scripts ask about that the server gives. ReqHasEffect answers these for real; any other
    // name it does not know keeps passing, as it did before (Requirements/Handlers/ReqUntrackedHandlers.cs).
    private static readonly HashSet<string> s_trackedEffects = new(StringComparer.OrdinalIgnoreCase) {
        TEAM_A_EFFECT, TEAM_B_EFFECT, DEATH_EFFECT, PLANNING_EFFECT,
    };

    internal enum Team : byte { None = 0, Red = 1, Blue = 2 }

    /// <summary>A wizard's part in a battleground: their team, the effects the arena asks about, and their form.</summary>
    internal sealed class Player {

        internal Team Team;
        internal readonly HashSet<string> Effects = new(StringComparer.OrdinalIgnoreCase);
        internal uint FormTemplateId;
        internal string FormName;
        internal BeastForm.Look FormLook = BeastForm.Look.Both;
        // The beast's health between battles (BeastCombat); 0 or less is full health.
        internal int BeastHealth;
        // The match the wizard went in for (`.bg enter`); the arena plays it (Zone.Battleground.cs).
        internal string MatchName;

    }

    private static readonly ConcurrentDictionary<ulong, Player> s_players = new();

    internal static bool IsBattlegroundZone(string zonePath)
        => zonePath?.StartsWith(ZONE_PREFIX, StringComparison.OrdinalIgnoreCase) == true
           && !Raids.Raids.IsRaidZone(zonePath);

    internal static Player Of(ulong charId) => s_players.TryGetValue(charId, out var player) ? player : null;

    internal static Player GetOrAdd(ulong charId) => s_players.GetOrAdd(charId, _ => new Player());

    /// <summary>Puts a wizard on a team: the team's effect on, the other team's off.</summary>
    internal static void SetTeam(ulong charId, Team team) {
        var player = GetOrAdd(charId);
        lock (player) {
            player.Team = team;
            player.Effects.Remove(TEAM_A_EFFECT);
            player.Effects.Remove(TEAM_B_EFFECT);
            if (team != Team.None) {
                player.Effects.Add(team == Team.Red ? TEAM_A_EFFECT : TEAM_B_EFFECT);
            }
        }
    }

    internal static void SetEffect(ulong charId, string effectName, bool on) {
        var player = GetOrAdd(charId);
        lock (player) {
            if (on) {
                player.Effects.Add(effectName);
            }
            else {
                player.Effects.Remove(effectName);
            }
        }
    }

    internal static bool IsTracked(string effectName) => effectName is not null && s_trackedEffects.Contains(effectName);

    /// <summary>Whether the wizard has a battleground effect the server gives (see the header).</summary>
    internal static bool HasEffect(ulong charId, string effectName) {
        var player = Of(charId);
        if (player is null) {
            return false;
        }

        lock (player) {
            return player.Effects.Contains(effectName);
        }
    }

    /// <summary>
    /// After a zone change the wizard's effects were rebuilt from the gear (CharacterHelper.RecalculateGameStats): a
    /// wizard still in a battleground gets their form back; one who left it is themselves again and off any team.
    /// </summary>
    internal static void AfterZoneChange(Wizard wizard) {
        if (wizard is null || Of(wizard.CharId) is not { } player) {
            return;
        }

        if (!IsBattlegroundZone(wizard.Zone)) {
            Forget(wizard.CharId);
            Logger.Information("Beastmoon | {0} left the battleground; their team and form ended.", Logger.Args(wizard.CharId));

            return;
        }

        if (player.FormTemplateId != 0) {
            BeastForm.AddEffects(wizard, player);
        }

        AddTeamEffect(wizard, player);
    }

    // Marks the team effect among the wizard's effects.
    internal static readonly uint TeamSlotId = Imcodec.Cryptography.StringHash.Compute("BeastmoonTeam");

    /// <summary>
    /// The team effect as a real game effect on the wizard. The game knows BattlegroundsTeamAEffect and
    /// BattlegroundsTeamBEffect (GameEffectData/WizardEffects.xml: public NamedEffectTemplates whose visual effects are
    /// AddBattlegroundsTeamAEffect / RemoveBattlegroundsTeamAEffect), and its battleground code looks for them on the
    /// wizard. Puts it on without telling anyone and returns it (or null when there is nothing to change); the zone
    /// change sends it with the wizard, and BeastForm.ShowTeam sends a change made in the zone.
    /// </summary>
    internal static GameEffectBase AddTeamEffect(Wizard wizard, Player player) {
        if (wizard?.GameEffects is null || player is null) {
            return null;
        }

        var name = player.Team switch { Team.Red => TEAM_A_EFFECT, Team.Blue => TEAM_B_EFFECT, _ => null };
        var nameId = name is null ? 0u : Imcodec.Cryptography.StringHash.Compute(name);
        if (wizard.GameEffects.Exists(e => e?.m_itemSlotID == TeamSlotId && e.m_effectNameID == nameId)) {
            return null;
        }

        wizard.GameEffects.RemoveAll(e => e?.m_itemSlotID == TeamSlotId);
        if (name is null) {
            return null;
        }

        var effect = new NamedEffect {
            m_effectNameID = nameId,
            m_itemSlotID = TeamSlotId,
            m_internalID = wizard.NextEffectId(),
        };
        wizard.GameEffects.Add(effect);

        return effect;
    }

    /// <summary>
    /// The behaviors a wizard built in an arena carries that the player template (ObjectData/PlayerObject.xml) lacks;
    /// the live game adds them in battlegrounds:
    /// - the Battle Book (ClientBattleBookBehavior): the beast's battle cards (m_spellList) and how many it may carry
    ///   (m_rating, a PolymorphRatingInfo). The Beast Form Select window finds it among the wizard's behaviors when it
    ///   opens, and the game crashes without it (BGPolymorphSelectWindow; seen 2026-11-14). BattleCards keeps it current.
    /// - [Beastmoon] PlayerBehavior (testing, off by default): BGPlayerBehavior, which follows the wizard's effects
    ///   (ClientBGPlayerBehavior::OnGameEffectModified, MSG_BGPlayerSync). The match window opens without it.
    /// </summary>
    internal static void AddPlayerBehavior(CoreObject playerObject, Wizard wizard) {
        if (!Enabled || playerObject is null || wizard is null || !IsBattlegroundZone(wizard.Zone)) {
            return;
        }

        playerObject.m_inactiveBehaviors ??= [];
        if (!playerObject.m_inactiveBehaviors.Any(b => b is ClientBattleBookBehavior)) {
            playerObject.m_inactiveBehaviors.Add(BattleCards.BattleBook(wizard));
        }

        if (ConfigurationManager.GetValue("Beastmoon.PlayerBehavior", false)
            && !playerObject.m_inactiveBehaviors.Any(b => b is ClientBGPlayerBehavior)) {
            playerObject.m_inactiveBehaviors.Add(new ClientBGPlayerBehavior {
                m_behaviorTemplateNameID = Imcodec.Cryptography.StringHash.Compute("BGPlayerBehavior"),
            });
        }
    }

    /// <summary>
    /// The battleground event of a match: the ClassProjectTemplate (CPT_Battleground) whose m_extendedTemplateName is the
    /// match, Beastmoon_PVE_Event_01 for Beastmoon_PVE_01. Its id is the string hash of its name. 0 when there is none.
    /// </summary>
    internal static uint EventProjectId(string matchName) {
        if (string.IsNullOrEmpty(matchName)) {
            return 0;
        }

        var project = Events.ClassProjects.Templates.Values.FirstOrDefault(t => t is not null
            && t.m_projectType == ClassProjectType.CPT_Battleground
            && string.Equals(t.m_extendedTemplateName.ToString(), matchName, StringComparison.OrdinalIgnoreCase));

        return project is null ? 0 : Imcodec.Cryptography.StringHash.Compute(project.m_projectName.ToString());
    }

    /// <summary>
    /// The queue's word that the wizard's match is ready, sent before they go in. The game keeps its ProjectID as the
    /// battleground event it plays (ClientClassProjectComponent::BattlegroundQueueUpdate; only outside an arena), and
    /// when the arena loads it builds the match window (BattlegroundWindow.gui: the clock, the points, the control
    /// points, the Battle Coins) from that event's match (ClientBattlegroundManager::OnHUDWindowShown). Without it the
    /// window never opens. Null when the match has no event.
    /// </summary>
    internal static Imcodec.MessageLayer.Generated.WIZARD2_53_PROTOCOL.MSG_BATTLEGROUNDQUEUEUPDATE QueueMessage(Wizard wizard, string matchName) {
        var projectId = EventProjectId(matchName);
        if (projectId == 0 || wizard is null) {
            return null;
        }

        return new() {
            ProjectID = projectId,
            PlayerGID = wizard.GameObjectID,
            Waiting = 0,
            Cancel = 0,
            Kicked = "",
            Ready = 0,
            TimeLeft = 0,
            DisableCrossPlay = 0,
        };
    }

    // Weeks of the PvE event rotation count from this Monday.
    private static readonly DateTime s_rotationStart = new(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The Beastmoon PvE event on now, with its start and end, or (null, ...). The game's event window lists it and
    /// offers its Join button (MSG_BATTLEGROUNDQUEUEPLAYER, Game/Services/BattlegroundService.cs); its match is the
    /// template's m_extendedTemplateName. [Beastmoon] PvEEvent: "rotate" (the default) goes through the game's
    /// Beastmoon_PVE_Event_01 to _25 a week each, from Monday; a template name keeps that one; "off" has none.
    /// </summary>
    internal static (ClassProjectTemplate Template, DateTime Start, DateTime End) PvEEvent(DateTime now) {
        var setting = ConfigurationManager.GetValue("Beastmoon.PvEEvent", "rotate")?.Trim() ?? "";
        if (!Enabled || setting.Length == 0 || setting.Equals("off", StringComparison.OrdinalIgnoreCase)) {
            return (null, default, default);
        }

        var weeks = (int) Math.Floor((now - s_rotationStart).TotalDays / 7);
        var start = s_rotationStart.AddDays(weeks * 7);
        var end = start.AddDays(7);
        if (!setting.Equals("rotate", StringComparison.OrdinalIgnoreCase)) {
            var chosen = Events.ClassProjects.TemplateOf(setting);
            if (chosen is null) {
                Logger.Warning("Beastmoon | [Beastmoon] PvEEvent names {0}, which is no event in the game files.", Logger.Args(setting));
            }

            return (chosen, start, end);
        }

        var events = Events.ClassProjects.Templates.Values
            .Where(t => t is not null && t.m_projectType == ClassProjectType.CPT_Battleground
                && System.Text.RegularExpressions.Regex.IsMatch(t.m_projectName.ToString(), @"^Beastmoon_PVE_Event_\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                && Template(t.m_extendedTemplateName.ToString()) is not null)
            .OrderBy(t => t.m_projectName.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToList();

        return events.Count == 0 ? (null, default, default) : (events[((weeks % events.Count) + events.Count) % events.Count], start, end);
    }

    /// <summary>
    /// Whether a wizard who logged out inside a Beastmoon arena logs in at the pavilion (WC_Duel_Arena, From_BG): the
    /// match they were in is gone, and the arena would put them in a new one with no team.
    /// </summary>
    internal static bool LogsInAtPavilion(Wizard wizard) => Enabled && IsBattlegroundZone(wizard?.Zone);

    /// <summary>A new login starts outside any battleground.</summary>
    internal static void Forget(ulong charId) => s_players.TryRemove(charId, out _);

    // ---------------------------------------------------------------------------------------------------------------
    // Templates, read from the player's own Root.wad.

    private static readonly ConcurrentDictionary<string, BattlegroundTemplate> s_templates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A match template by name ("Beastmoon_PVP_02"), or null.</summary>
    internal static BattlegroundTemplate Template(string name) {
        if (string.IsNullOrWhiteSpace(name)) {
            return null;
        }

        name = name.Trim();
        if (s_templates.TryGetValue(name, out var cached)) {
            return cached;
        }

        var template = RootArchiveLoader.GetFile<BattlegroundTemplate>(TEMPLATE_FOLDER + name + ".xml");
        if (template is not null) {
            s_templates[name] = template;
        }

        return template;
    }

    /// <summary>
    /// The match an arena plays: the one a wizard in it went in for, else the first Beastmoon match set in that zone.
    /// </summary>
    internal static (string Name, BattlegroundTemplate Template) MatchFor(string zonePath, string wanted) {
        foreach (var name in new[] { wanted }.Concat(BeastmoonTemplateNames())) {
            if (Template(name) is { } template
                && string.Equals((string) template.m_battlegroundZone, zonePath, StringComparison.OrdinalIgnoreCase)) {
                return (name, template);
            }
        }

        return (null, null);
    }

    /// <summary>The Beastmoon match templates in Root.wad (Beastmoon_PVP_01, ...), by name.</summary>
    internal static List<string> BeastmoonTemplateNames()
        => [.. RootFiles(TEMPLATE_FOLDER + "Beastmoon_")
            .Select(f => f[TEMPLATE_FOLDER.Length..^".xml".Length])
            .Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The beast form file names in Root.wad (BG-Polymorph-Cyclops-Life-01, ...).</summary>
    internal static List<string> FormNames()
        => [.. RootFiles(FORM_FOLDER + FORM_PREFIX)
            .Select(f => f[FORM_FOLDER.Length..^".xml".Length])
            .Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>A beast form by its file name ("BG-Polymorph-Cyclops-Life-01", or just "Cyclops-Life-01"), or null.</summary>
    internal static WizPolymorphTemplate Form(string name, out string fileName) {
        fileName = null;
        if (string.IsNullOrWhiteSpace(name)) {
            return null;
        }

        name = name.Trim();
        if (!name.StartsWith(FORM_PREFIX, StringComparison.OrdinalIgnoreCase)) {
            name = FORM_PREFIX + name;
        }

        // A form named without its rank is its first rank.
        if (!char.IsDigit(name[^1])) {
            name += "-01";
        }

        var form = RootArchiveLoader.GetFile<WizPolymorphTemplate>(FORM_FOLDER + name + ".xml");
        if (form is not null) {
            fileName = name;
        }

        return form;
    }

    private static Dictionary<uint, string> s_formsById;

    /// <summary>
    /// A beast form by its template ID (what the game's Beast Form Select window asks about), or null. The forms are
    /// read once, the first time one is asked for by ID.
    /// </summary>
    internal static WizPolymorphTemplate FormById(uint templateId, out string fileName) {
        fileName = null;
        var forms = s_formsById;
        if (forms is null) {
            forms = [];
            foreach (var name in FormNames()) {
                if (Form(name, out var file) is { } form && form.m_templateID != 0) {
                    forms[form.m_templateID] = file;
                }
            }

            s_formsById = forms;
            Logger.Information("Beastmoon | read {0} beast forms.", Logger.Args(forms.Count));
        }

        return forms.TryGetValue(templateId, out var found) ? Form(found, out fileName) : null;
    }

    /// <summary>"Cyclops-Life" of BG-Polymorph-Cyclops-Life-03: the beast and school, the same at every tier.</summary>
    internal static string FormKey(string fileName) {
        var rest = fileName?.StartsWith(FORM_PREFIX, StringComparison.OrdinalIgnoreCase) == true ? fileName[FORM_PREFIX.Length..] : fileName;
        var dash = rest?.LastIndexOf('-') ?? -1;

        return dash > 0 && int.TryParse(rest[(dash + 1)..], out _) ? rest[..dash] : rest;
    }

    /// <summary>The tier of a form, 3 for BG-Polymorph-Cyclops-Life-03 (the forms' -01 to -05).</summary>
    internal static int FormTier(string fileName) {
        var dash = fileName?.LastIndexOf('-') ?? -1;

        return dash > 0 && int.TryParse(fileName[(dash + 1)..], out var tier) ? tier : 1;
    }

    /// <summary>The beast of a form, "Cyclops" of BG-Polymorph-Cyclops-Life-03: the statue's m_polymorphCategory.</summary>
    internal static string FormCategory(string fileName) {
        var key = FormKey(fileName);
        var dash = key?.IndexOf('-') ?? -1;

        return dash > 0 ? key[..dash] : key;
    }

    /// <summary>The match tag of a form, "Life Cyclops" (BattlegroundTemplate m_unlockedPolymorphTags).</summary>
    internal static string FormTag(string fileName) {
        var key = FormKey(fileName);
        var dash = key?.IndexOf('-') ?? -1;

        return dash > 0 ? $"{key[(dash + 1)..]} {key[..dash]}" : key;
    }

    private static IEnumerable<string> RootFiles(string prefix) {
        var wad = RootArchiveLoader.GetRootWad();
        if (wad is null) {
            return [];
        }

        return wad.Files.Keys
            .Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

}
