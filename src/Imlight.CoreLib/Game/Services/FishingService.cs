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
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.Game.Fishing;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Character;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

/// <summary>
/// Fishing (missing-features list, rated 3): the fish in each zone's ponds, casting fishing spells, catching, the basket,
/// the fishing journal and fishing XP and levels. The client draws the ponds and runs the bite from its own copy of the
/// zone's FishingInfo file; the server says which fish swim there and answers the catch.
/// </summary>
internal class FishingService(SessionActor sessionActor) : MessageService(sessionActor) {

    private const string SyncTimer = "fishing-sync";
    private const string ZonedFallbackTimer = "fishing-zoned-fallback";
    private static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(5);
    // The client says MSG_CLIENTZONED once it has loaded a zone; when it does not, the fish go out after this.
    private static readonly TimeSpan ZonedFallback = TimeSpan.FromSeconds(8);

    private string _zonePath;
    private uint _zoneNameId;
    private bool _clientZoned;
    private bool _loggedZoneId;
    private bool _starterChecked;
    private int _sentVersion = -1;
    private readonly HashSet<uint> _sentFish = [];

    internal sealed record FishingSync;
    internal sealed record FishingZonedFallback;

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new FishingService(parentActor));

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_PRELOGIN))]
    private void ReceivePreLogin(ZONE_102_PROTOCOL.MSG_PRELOGIN message) {
        if (!FishingSettings.Enabled) {
            return;
        }

        try {
            Timers.Cancel(SyncTimer);
            _sentFish.Clear();
            _sentVersion = -1;
            _clientZoned = false;
            _zonePath = GetActiveWizard()?.Zone;
            _zoneNameId = StringHash.Compute(_zonePath ?? "");
            FishingData.Waters(_zonePath, out _); // Starts reading the zone's ponds while it loads.
            Timers.StartSingleTimer(ZonedFallbackTimer, new FishingZonedFallback(), ZonedFallback);
            GiveStarterSpells();
        }
        catch (Exception e) {
            Logger.Error("Fishing: zone entry failed: {0}", Logger.Args(e));
        }
    }

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_CLIENTZONED))]
    private void ReceiveClientZoned(WIZARD2_53_PROTOCOL.MSG_CLIENTZONED message) {
        if (!FishingSettings.Enabled) {
            return;
        }

        var ours = StringHash.Compute(_zonePath ?? "");
        if (!_loggedZoneId || message.ZoneNameID != ours) {
            Logger.Information("Fishing: client is in zone id {0}; zone {1} hashes to {2} ({3}).",
                Logger.Args(message.ZoneNameID, _zonePath, ours, message.ZoneNameID == ours ? "same" : "different, using the client's"));
            _loggedZoneId = true;
        }

        // The client's own name for the zone is the one its fish lists answer to.
        _zoneNameId = message.ZoneNameID;
        _clientZoned = true;
        Timers.Cancel(ZonedFallbackTimer);
        StartSync();
    }

    [InternalMessageHandler(typeof(FishingZonedFallback))]
    private void ReceiveZonedFallback(FishingZonedFallback message) {
        if (!_clientZoned) {
            StartSync();
        }
    }

    [InternalMessageHandler(typeof(FishingSync))]
    private void ReceiveSync(FishingSync message) {
        try {
            SyncFish();
        }
        catch (Exception e) {
            Logger.Error("Fishing: sending fish failed: {0}", Logger.Args(e));
            Timers.Cancel(SyncTimer);
        }
    }

    private void StartSync() {
        try {
            SyncFish();
        }
        catch (Exception e) {
            Logger.Error("Fishing: sending fish failed: {0}", Logger.Args(e));

            return;
        }

        Timers.StartPeriodicTimer(SyncTimer, new FishingSync(), SyncInterval);
    }

    /// <summary>
    /// Tells the client about fish it has not been told about. When a fish it knows is gone (someone else caught it), the
    /// whole list is sent again.
    /// </summary>
    private void SyncFish() {
        var pond = FishPonds.For(_zonePath, out var ready);
        if (!ready) {
            return; // Still reading the zone; the next tick tries again.
        }

        if (pond is null) {
            Timers.Cancel(SyncTimer);

            return;
        }

        var fish = pond.Snapshot();
        if (pond.Version == _sentVersion) {
            return;
        }

        var ids = fish.Select(f => f.Id).ToHashSet();
        var full = _sentVersion < 0 || _sentFish.Any(id => !ids.Contains(id));
        if (full) {
            _sentFish.Clear();
            SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_SETINSTANCEFISH { ZoneNameID = _zoneNameId, Data = Array.Empty<byte>() });
        }

        var added = 0;
        foreach (var live in fish.Where(f => !_sentFish.Contains(f.Id))) {
            SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_ADDINSTANCEFISH {
                ZoneNameID = _zoneNameId,
                FishTemplateID = live.TemplateId,
                Size = live.Size,
                FishID = live.Id,
            });
            _sentFish.Add(live.Id);
            added++;
        }

        if (full || added > 0) {
            Logger.Debug("Fishing: sent {0} fish of zone {1} ({2}).", Logger.Args(added, _zonePath, full ? "full list" : "new ones"));
        }

        _sentVersion = pond.Version;
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_FISHINGSPELLCAST))]
    private void ReceiveFishingSpellCast(WIZARDHOUSING_50_PROTOCOL.MSG_FISHINGSPELLCAST message) {
        try {
            var wizard = GetActiveWizard();
            var spell = FishingData.Spell(message.SpellTemplateID);
            Logger.Information("Fishing: {0} casts {1} ({2}), treasure card {3}, passport {4}, recast {5}.",
                Logger.Args(wizard.CharId, spell?.m_name.ToString() ?? "unknown spell", message.SpellTemplateID,
                    message.IsTreasureCard, message.FishingPassport, message.IsRecast));

            if (message.IsTreasureCard != 0) {
                // A fishing treasure card is used up instead of energy.
                if (wizard.SpellbookBehavior.RemoveTreasureCard(message.SpellTemplateID)) {
                    WizardCollection.RemoveTreasureCard(wizard, message.SpellTemplateID);
                }
                else {
                    Logger.Debug("Fishing: {0} has no fishing treasure card {1} to use up.", Logger.Args(wizard.CharId, message.SpellTemplateID));
                }
            }
            else if (message.IsTreasureCard == 0 && message.IsRecast == 0 && FishingSettings.UseEnergy
                     && spell is not null && spell.m_energyCost > 0 && !UseEnergy(wizard, spell.m_energyCost)) {
                InformGameClient("You don't have enough energy for that fishing spell.");

                return;
            }

            if (FishingSettings.EchoSpellCast) {
                SendToSocket(message);
            }
        }
        catch (Exception e) {
            Logger.Error("Fishing: casting failed: {0}", Logger.Args(e));
        }
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_BEGINFISHINGCAST))]
    private void ReceiveBeginFishingCast(WIZARDHOUSING_50_PROTOCOL.MSG_BEGINFISHINGCAST message) {
        try {
            var wizard = GetActiveWizard();
            Logger.Debug("Fishing: {0} casts spell {1} from ({2:0},{3:0},{4:0}) to ({5:0},{6:0},{7:0}).",
                Logger.Args(wizard.CharId, message.SpellTemplateID, message.PositionX, message.PositionY, message.PositionZ,
                    message.EndX, message.EndY, message.EndZ));

            // The others in the zone see the cast.
            ZoneBroadcast(new WIZARDHOUSING_50_PROTOCOL.MSG_SHOWFISHINGCAST {
                ObjectGID = wizard.GameObjectID,
                PositionX = message.EndX,
                PositionY = message.EndY,
                PositionZ = message.EndZ,
                Yaw = message.Yaw,
                SpellTemplateID = message.SpellTemplateID,
            });
        }
        catch (Exception e) {
            Logger.Error("Fishing: showing a cast failed: {0}", Logger.Args(e));
        }
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_ENDFISHINGCAST))]
    private void ReceiveEndFishingCast(WIZARDHOUSING_50_PROTOCOL.MSG_ENDFISHINGCAST message) {
        try {
            ZoneBroadcast(new WIZARDHOUSING_50_PROTOCOL.MSG_ENDFISHINGCAST { ObjectGID = GetActiveWizard().GameObjectID });
        }
        catch (Exception e) {
            Logger.Error("Fishing: ending a cast failed: {0}", Logger.Args(e));
        }
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_REQUESTCATCHSUCCESS))]
    private void ReceiveRequestCatchSuccess(WIZARDHOUSING_50_PROTOCOL.MSG_REQUESTCATCHSUCCESS message) {
        try {
            var wizard = GetActiveWizard();
            var known = FishPonds.For(_zonePath, out _)?.TryGet(message.FishID, out _) ?? false;
            Logger.Information("Fishing: {0} hooks fish {1} with spell {2}, passport {3} ({4}).",
                Logger.Args(wizard.CharId, message.FishID, message.SpellTemplateID, message.FishingPassport,
                    known ? "a fish the server sent" : "not a fish the server sent"));

            SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_CATCHSUCCESS { FishID = message.FishID, Assist = 0 });
        }
        catch (Exception e) {
            Logger.Error("Fishing: answering a catch failed: {0}", Logger.Args(e));
        }
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_CATCHFISH))]
    private void ReceiveCatchFish(WIZARDHOUSING_50_PROTOCOL.MSG_CATCHFISH message) {
        try {
            Catch(message.FishID, message.SpellTemplateID, message.IsTreasureCard != 0);
        }
        catch (Exception e) {
            Logger.Error("Fishing: catching fish {0} failed: {1}", Logger.Args(message.FishID, e));
        }
    }

    private void Catch(uint fishId, uint spellTemplateId, bool treasureCard) {
        var wizard = GetActiveWizard();
        var pond = FishPonds.For(_zonePath, out _);
        var spell = FishingData.Spell(spellTemplateId);
        LiveFish caught = null;
        if (pond is not null && pond.Remove(fishId, out var live)) {
            caught = live;
            _sentFish.Remove(fishId);
            _sentVersion = pond.Version;
        }

        // A fish the server never sent: the client made it, so the pond picks one of the lure's school for it.
        caught ??= pond?.StandIn(spell?.m_fishingSchoolFocus.ToString() ?? "");
        var info = caught is null ? null : FishingData.Fish(caught.TemplateId);
        if (info is null) {
            Logger.Warning("Fishing: {0} caught fish {1} in {2}, which has no fish the server knows.",
                Logger.Args(wizard.CharId, fishId, _zonePath));

            return;
        }

        var size = caught.Size;
        var smallFry = info.IsSmallFry(size);
        var whopper = info.IsWhopper(size);
        var basket = wizard.FishingBehavior ??= new ServerFishingBehavior();

        if (info.IsChest) {
            // A chest: its gold instead of a fish in the basket.
            var gold = Math.Max(info.SalePrice, 25);
            wizard.AddGold(gold);
            SendGold(wizard);
            InformGameClient($"You fished up {info.Name} with {gold} gold inside!");
        }
        else if ((basket.Basket?.Count ?? 0) >= FishingSettings.BasketSize) {
            SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_NOFISHSPACE { GameObjectID = wizard.GameObjectID });
            InformGameClient($"Your fish basket is full ({FishingSettings.BasketSize} fish). Sell some with .fishing sell.");
        }
        else {
            basket.Add(info.TemplateId, size, smallFry, whopper);
            WizardCollection.UpdateCharacterFishingBehavior(wizard);
        }

        Logger.Information("Fishing: {0} caught {1} ({2}, rank {3} {4}), size {5:0.00}, length {6:0.0}{7}{8}.",
            Logger.Args(wizard.CharId, info.Name, info.TemplateId, info.Rank, info.School, size, info.LengthOf(size),
                smallFry ? ", a small fry" : "", whopper ? ", a whopper" : ""));

        SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_DISPLAYCAUGHTFISH {
            ObjectGID = wizard.GameObjectID,
            FishTemplateID = info.TemplateId,
            Length = info.LengthOf(size),
            IsSmallFry = smallFry ? (byte) 1 : (byte) 0,
            IsWopper = whopper ? (byte) 1 : (byte) 0,
            LootData = Array.Empty<byte>(),
            Energy = 0,
            IsTournamentFish = 0,
            CatchOfTheDayBonus = 0,
        });

        // The others in the zone see the fish come out of the water.
        ZoneBroadcast(new WIZARDHOUSING_50_PROTOCOL.MSG_DISPLAYCATCHFISH {
            ObjectGID = wizard.GameObjectID,
            FishTemplateID = info.TemplateId,
            Size = size,
            SpellTemplateID = spellTemplateId,
        });

        if (!info.IsChest) {
            GiveXp(wizard, (int) Math.Round(info.Xp * FishingSettings.XpMultiplier));
        }
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_MISSFISH))]
    private void ReceiveMissFish(WIZARDHOUSING_50_PROTOCOL.MSG_MISSFISH message)
        => Logger.Debug("Fishing: missed a fish with spell {0} (treasure card {1}).", Logger.Args(message.SpellTemplateID, message.IsTreasureCard));

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_FISHESCAPED))]
    private void ReceiveFishEscaped(WIZARDHOUSING_50_PROTOCOL.MSG_FISHESCAPED message) {
        try {
            var pond = FishPonds.For(_zonePath, out _);
            var known = pond is not null && pond.Remove(message.FishID, out _);
            if (known) {
                _sentFish.Remove(message.FishID);
                _sentVersion = pond.Version;
            }

            Logger.Debug("Fishing: fish {0} escaped in {1} ({2}).", Logger.Args(message.FishID, _zonePath,
                known ? "a new one swims in later" : "not a fish the server sent"));
        }
        catch (Exception e) {
            Logger.Error("Fishing: an escaped fish failed: {0}", Logger.Args(e));
        }
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_DELETEFISH))]
    private void ReceiveDeleteFish(WIZARDHOUSING_50_PROTOCOL.MSG_DELETEFISH message) {
        try {
            var wizard = GetActiveWizard();
            var taken = wizard.FishingBehavior?.Take(message.FishTemplateID, message.Size);
            Logger.Information("Fishing: {0} throws away fish {1} size {2:0.00} ({3}).",
                Logger.Args(wizard.CharId, message.FishTemplateID, message.Size, taken is null ? "not in the basket" : "taken out of the basket"));
            if (taken is not null) {
                WizardCollection.UpdateCharacterFishingBehavior(wizard);
            }

            SendToSocket(message);
        }
        catch (Exception e) {
            Logger.Error("Fishing: throwing away a fish failed: {0}", Logger.Args(e));
        }
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_REQUESTALLFISH))]
    private void ReceiveRequestAllFish(WIZARDHOUSING_50_PROTOCOL.MSG_REQUESTALLFISH message)
        => Logger.Information("Fishing: the game asked for the fish of {0}, data {1}.", Logger.Args(message.GlobalID, Hex(message.FishData)));

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_REQUESTFISHHISTORY))]
    private void ReceiveRequestFishHistory(WIZARDHOUSING_50_PROTOCOL.MSG_REQUESTFISHHISTORY message)
        => Logger.Information("Fishing: the game asked for the fish journal of {0}, data {1}.", Logger.Args(message.GlobalID, Hex(message.FishData)));

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_SELLFISHREQUEST))]
    private void ReceiveSellFishRequest(WIZARDHOUSING_50_PROTOCOL.MSG_SELLFISHREQUEST message) {
        Logger.Information("Fishing: the game asked to sell fish, data {0}.", Logger.Args(Hex(message.FishData)));
        InformGameClient("Selling at the fish seller is not ready yet. Type .fishing sell to sell your whole basket.");
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_ADDFISHTOAQUARIUM))]
    private void ReceiveAddFishToAquarium(WIZARDHOUSING_50_PROTOCOL.MSG_ADDFISHTOAQUARIUM message) {
        Logger.Information("Fishing: the game asked to put fish {0} size {1:0.00} in aquarium {2}.",
            Logger.Args(message.FishTemplateID, message.Size, message.GlobalID));
        InformGameClient("Aquariums are not ready yet; your fish stays in your basket.");
    }

    /// <summary>Adds fishing XP and tells the client, with a level up when there is one.</summary>
    private void GiveXp(Wizard wizard, int xp) {
        if (xp <= 0) {
            return;
        }

        var stats = wizard.GameStats;
        var before = Math.Max((int) stats.m_fishingLevel, 1);
        stats.m_fishingXP += xp;
        var after = Math.Min(Math.Max(FishingData.LevelAt(stats.m_fishingXP), before), FishingData.MaxLevel);
        stats.m_fishingLevel = (byte) Math.Clamp(after, 1, 255);
        WizardCollection.UpdateCharacterGameStats(wizard);

        SendToSocket(new WIZARD2_53_PROTOCOL.MSG_UPDATEFISHINGXP {
            GlobalID = wizard.GameObjectID,
            XP = stats.m_fishingXP,
            Level = stats.m_fishingLevel,
        });

        if (after > before) {
            Logger.Information("Fishing: {0} is fishing level {1} now.", Logger.Args(wizard.CharId, after));
            ZoneBroadcast(new WIZARD2_53_PROTOCOL.MSG_FISHINGLEVELUP { GlobalID = wizard.GameObjectID, NewLevel = after }, isSelfless: false);
        }
    }

    /// <summary>Teaches the starter fishing spells to a wizard who knows none, once per session.</summary>
    private void GiveStarterSpells() {
        if (_starterChecked || !FishingSettings.StarterSpells) {
            return;
        }

        _starterChecked = true;
        var wizard = GetActiveWizard();
        if (wizard is null) {
            return;
        }

        if (wizard.GameStats is { m_fishingLevel: 0 } stats) {
            stats.m_fishingLevel = 1;
            WizardCollection.UpdateCharacterGameStats(wizard);
        }

        var known = FishingData.Spells.Any(s => wizard.SpellbookBehavior.HasSpell(s.Id));
        if (known) {
            return;
        }

        var taught = new List<string>();
        foreach (var (id, template) in FishingData.StarterSpells()) {
            var spell = SpellFactory.GetSpell(id);
            if (spell is null || !wizard.LearnSpell(spell)) {
                continue;
            }

            SendToSocket(new WIZARD_12_PROTOCOL.MSG_ADDSPELLTOBOOK { SpellID = (int) id });
            taught.Add(template.m_name.ToString());
        }

        if (taught.Count > 0) {
            Logger.Information("Fishing: taught {0} the starter fishing spells: {1}.", Logger.Args(wizard.CharId, string.Join(", ", taught)));
        }
    }

    private bool UseEnergy(Wizard wizard, int energyCost) {
        var newEnergy = wizard.PetOwnerBehavior.Energy - energyCost;
        if (newEnergy < 0) {
            return false;
        }

        wizard.UpdateEnergy(newEnergy);

        // The client adds the gear's energy to the max itself, so the level's own max is sent (as CantripService does).
        var baseStats = MagicLevelsConfig.GetPlayerLevelInfo(wizard.MagicSchoolBehavior.MagicSchool, wizard.MagicSchoolBehavior.Level);
        SendToSocket(new PET_9_PROTOCOL.MSG_PETENERGYTICK {
            GlobalID = wizard.GameObject.m_globalID,
            Energy = newEnergy,
            MaxEnergy = baseStats.m_petEnergy,
            TickTime = (int) wizard.PetOwnerBehavior.LastEnergyTickEpoch,
        });

        return true;
    }

    private void SendGold(Wizard wizard)
        => SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD {
            Gold = wizard.GameStats.m_currentGold,
            MaxGold = wizard.GameStats.m_baseGoldPouch,
        });

    private static string Hex(Imcodec.IO.ByteString data) {
        byte[] bytes = data;
        if (bytes is null || bytes.Length == 0) {
            return "(empty)";
        }

        return $"{bytes.Length} bytes {Convert.ToHexString(bytes, 0, Math.Min(bytes.Length, 256))}";
    }

}
