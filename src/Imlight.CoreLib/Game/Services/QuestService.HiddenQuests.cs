using Imcodec.CoreObject;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.Services;

/*
 * The quest book's "Hide Quest" button. The game keeps the list of hidden quests in the wizard's HiddenQuestsBehavior
 * (m_hiddenQuestsData) and sends the whole new list in MSG_UPDATEUSERHIDDENQUESTS each time a quest is hidden or shown
 * again. The server saves it on the wizard and puts it back in the wizard's object (WizardObjectLoader
 * SetHiddenQuestsBehavior), which the game gets again on every loading screen and at login. Before this the list was
 * never saved, so a hidden quest came back after any zone change (Charter the Dragon in Kembaalung, 2026-11-04).
 */
internal partial class QuestService {

    [MessageHandler(typeof(QUEST_MESSAGES_52_PROTOCOL.MSG_UPDATEUSERHIDDENQUESTS))]
    private void ReceiveUpdateUserHiddenQuests(QUEST_MESSAGES_52_PROTOCOL.MSG_UPDATEUSERHIDDENQUESTS message) {
        var wizard = GetActiveWizard();
        if (wizard is null) {
            return;
        }

        var data = message.Data;
        wizard.HiddenQuestsData = data;
        WizardCollection.UpdateCharacterHiddenQuests(wizard);

        // The object the wizard has now, so a zone change that reuses it carries the new list too.
        if (wizard.GameObject is not null) {
            WizardObjectLoader.SetHiddenQuestsBehavior(wizard.GameObject, wizard);
        }

        Logger.Information("Saved the hidden quests list for character {Id} ({Bytes} bytes).",
            Logger.Args(wizard.CharId, data.IsEmpty ? 0 : ((byte[]) data).Length));
    }
}
