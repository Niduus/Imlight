using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.CoreObject;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Quests;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Services;

/// <summary>Works the quest companions out again once the quest change behind a <see cref="RefreshQuestCompanions"/> is in.</summary>
internal sealed record RefreshQuestCompanionsNow;

/// <summary>
/// Quest companions (QuestCompanions): the NPCs a quest has walk beside the wizard. Each is a zone entity made from its
/// "*-Accompany-*" template with the template's leash tied to the wizard, like a summoned pet, and goes when the wizard
/// leaves the area (SummonedPetComponent). They are worked out again at every area change and whenever a quest starts,
/// finishes or turns one on or off.
/// </summary>
internal partial class EquipmentService {

    // A quest's start results run before the quest is on the wizard's list, so the check waits a moment.
    private static readonly TimeSpan s_companionDelay = TimeSpan.FromSeconds(1.5);

    // Companion template id -> the entity walking with the wizard in this area.
    private readonly Dictionary<ulong, ulong> _companions = [];
    private bool _companionCheckPending;

    [InternalMessageHandler(typeof(RefreshQuestCompanions))]
    private void ReceiveRefreshQuestCompanions(RefreshQuestCompanions message) => ScheduleCompanionCheck();

    [InternalMessageHandler(typeof(RefreshQuestCompanionsNow))]
    private void ReceiveRefreshQuestCompanionsNow(RefreshQuestCompanionsNow message) {
        _companionCheckPending = false;
        try {
            UpdateCompanions();
        }
        catch (Exception ex) {
            Logger.Error("Error while updating the quest companions: {0} {1}", Logger.Args(ex.Message, ex.StackTrace));
        }
    }

    /// <summary>A new area: the companions of the old one went with it, and the ones wanted here come in a moment.</summary>
    private void QuestCompanionsAfterZoneChange() {
        _companions.Clear();
        ScheduleCompanionCheck();
    }

    private void ScheduleCompanionCheck() {
        if (_companionCheckPending) {
            return;
        }

        _companionCheckPending = true;
        Context.System.Scheduler.ScheduleTellOnce(s_companionDelay, Self, new RefreshQuestCompanionsNow(), Self);
    }

    private void UpdateCompanions() {
        var wizard = GetActiveWizard();
        var playerObj = GetActiveGameObject();
        var zoneActor = SessionActor.GetZoneActor();
        if (wizard is null || playerObj is null || zoneActor is null) {
            return;
        }

        var wanted = QuestCompanions.Wanted(wizard);
        foreach (var (templateId, entityId) in _companions.Where(c => !wanted.Contains(c.Key)).ToList()) {
            zoneActor.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
                Messages = [new ZONE_102_PROTOCOL.MSG_DISMISSPET { PetGlobalId = entityId }],
                Targets = ZoneBroadcastTarget.Objects,
            }, Self);
            _companions.Remove(templateId);
            Logger.Information("Quest companion {0} stopped walking with {1}.", Logger.Args(templateId, wizard.CharId));
        }

        foreach (var templateId in wanted.Where(id => !_companions.ContainsKey(id))) {
            var companion = QuestCompanions.Get(templateId);
            if (companion is null) {
                continue;
            }

            var info = new CoreObjectInfo {
                m_templateID = templateId,
                m_fScale = 1.0f,
                m_zoneTag = "QuestCompanion",
                m_location = playerObj.m_location,
                m_orientation = playerObj.m_orientation,
            };
            var entity = CoreObjectFactory.FinalizeCoreObject(info, companion.Template);
            if (entity is null) {
                continue;
            }

            // The template's leash slot holds the leash tied to this wizard (the client lines behaviors up by slot).
            entity = CoreObjectFactory.InitializeCoreObjectBehaviors(entity, companion.Template);
            while (entity.m_inactiveBehaviors.Count <= companion.LeashSlot) {
                entity.m_inactiveBehaviors.Add(null);
            }

            entity.m_inactiveBehaviors[companion.LeashSlot] = QuestCompanions.MakeLeash(companion, playerObj.m_globalID);

            // The companion templates are pet templates, sent like a summoned pet (ZoneEntity.BuildClientPet).
            if (entity is WizClientPet pet) {
                pet.m_leashed = true;
            }

            zoneActor.Tell(new ZONE_102_PROTOCOL.MSG_SPAWNENTITY {
                CoreObject = entity,
                Template = companion.Template,
                Requester = SessionActor.ActorRef,
            }, Self);
            _companions[templateId] = entity.m_globalID.Full;
            Logger.Information("Quest companion {0} ({1}) walks with {2} in {3}.",
                Logger.Args(templateId, entity.m_debugName, wizard.CharId, wizard.Zone));
        }
    }

}
