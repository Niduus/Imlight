using System;
using System.Collections.Generic;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

/// <summary>
/// The daily reward wisps of the guild grounds (Beta, Sparr, Theena, Flip; Niduus, 2026-10-25). They stand where the
/// game's island Daily Reward chests unlock, and give what those chests give: once every 20 hours (the chest's
/// ResTimeStampEntry, 72000 s, kept in the same registry entry) a roll of Guild_Daily_Loot_Table_01. The game files
/// leave that table empty (its loot lives on the real servers), so spiraldb-extra/DropTables holds a stand-in.
/// </summary>
internal sealed class GuildDailyRewardComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    private const string LOOT_TABLE = "Guild_Daily_Loot_Table_01";
    private static readonly TimeSpan COOLDOWN = TimeSpan.FromSeconds(72000);

    // Wisp template -> the registry entry its island's Daily Reward chest keeps the time stamp in.
    private static readonly Dictionary<uint, string> s_registryEntries = new() {
        [1529150] = "Guild_Housing_MainIslandDaily",     // Beta
        [1529151] = "Guild_Housing_PvPIslandDaily",      // Sparr
        [1529148] = "Guild_Housing_BattleIslandDaily",   // Theena
        [1529149] = "Guild_Housing_HousingIslandDaily",  // Flip
    };

    public string ServiceName     => "GuildDailyReward";
    public string NpcIcon         => (Entity.Template as GameObjectTemplate)?.m_sIcon;
    public string NpcNameKey      => null;
    public string NpcTextKey      => null;
    public WizBangs WizBang       => WizBangs.None;
    public string StateName       => null;
    public string InteractWizBang => null;
    // "Daily Reward", the island chests' display name.
    public string DisplayKey      => "NPCs_01749207";

    private string RegistryEntry => s_registryEntries[(uint) Entity.ActiveGameObject.m_templateID];

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate go && s_registryEntries.ContainsKey((uint) go.m_templateID);

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard playerCharacter)
        => [new InteractableOption { m_serviceName = ServiceName, m_displayKey = DisplayKey, m_iconKey = "Interact" }];

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        try {
            var now = DateTimeOffset.UtcNow;
            var last = playerCharacter.GetRegistryValue(RegistryEntry);
            if (last > 0) {
                var readyAt = DateTimeOffset.FromUnixTimeSeconds((long) last) + COOLDOWN;
                if (readyAt > now) {
                    var left = readyAt - now;
                    Tell(playerActor, $"Come back for your next daily reward in {(int) left.TotalHours} h {left.Minutes} min.");

                    return;
                }
            }

            var tables = ChestLootRoller.ResolveTableNames([LOOT_TABLE], (ulong) Entity.ActiveGameObject.m_templateID);
            var results = ChestLootRoller.Roll(tables, playerActor, playerObject, playerCharacter);
            playerCharacter.SetRegistryValue(RegistryEntry, (ulong) now.ToUnixTimeSeconds());
            LootGranter.GrantAndDisplay(playerActor, playerCharacter, results);

            Logger.Information("Guild grounds | {0} took the daily reward of {1}.",
                Logger.Args(playerCharacter.CharId, (Entity.Template as GameObjectTemplate)?.m_objectName.ToString()));
        }
        catch (Exception ex) {
            Logger.Error("Guild grounds | Giving the daily reward failed: {0}", Logger.Args(ex));
        }
    }

    private static void Tell(IActorRef playerActor, string text)
        => playerActor.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = text, Modal = 0 });

}
