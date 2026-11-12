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
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.Game.MonsterMagic;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.Results.Handlers;

internal sealed class ResLearnSpellHandler : BaseResultHandler<ResLearnSpell> {

    private const float QUERY_WIZARD_TIMEOUT_SECONDS = 5.0f;

    public override bool Execute(IResultContext context) {
        // Context does not ship with a wizard reference, so we need to query for it.
        var queryWizardMsg = new CHARACTER_103_PROTOCOL.MSG_QUERYACTIVEWIZARD();
        var queryTimeout = TimeSpan.FromSeconds(QUERY_WIZARD_TIMEOUT_SECONDS);
        var queryResponse = context
            .GetPlayerRef()
            .Ask<CHARACTER_103_PROTOCOL.MSG_CHARACTER>(queryWizardMsg, queryTimeout).Result;
        if (queryResponse == null) {
            Logger.Error("ResLearnSpell handler failed to retrieve character data within {0} seconds.",
                Logger.Args(QUERY_WIZARD_TIMEOUT_SECONDS));

            return false;
        }

        var wizard = queryResponse.Wizard;

        // A spell that is already known is fine; the learn is idempotent.
        var outcome = SpellTeacher.TryTeach(context.GetPlayerRef(), wizard, Result.m_templateID);

        // Learning an Extract spell (Know Thine Enemy gives Extract Undead) opens Monstrology at once, so the spell
        // book's Monstrology tab works without logging out first.
        if (outcome != SpellTeachResult.Failed
            && CoreObjectFactory.GetCoreTemplate(Result.m_templateID) is SpellTemplate learned
            && Monstrology.IsExtract(learned) && Monstrology.Open(wizard)) {
            WizardCollection.UpdateCharacterMonsterTome(wizard);
            context.GetPlayerRef().Tell(new WIZARD2_53_PROTOCOL.MSG_UPDATEMONSTERMAGICXP {
                GlobalID = wizard.GameObjectID,
                XP = wizard.GameStats.m_monsterMagicXP,
                Level = Monstrology.LevelOf(wizard),
            });
            Logger.Information("Monstrology: opened for {0} by learning {1}.", Logger.Args(wizard.CharId, (string) learned.m_name));
        }

        return outcome != SpellTeachResult.Failed;
    }

}
