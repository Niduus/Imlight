using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Results.Handlers;

/// <summary>
/// ResAddEncounter: starts an encounter (a quest of the EncounterTemplate kind, such as EM-AERI-J-ENC-001) for the acting
/// player. The game's quests, zone triggers and objects start encounters this way instead of an NPC offering them; nothing
/// ran it before, so the encounters the game's own quest files add (2026-11-28) could never start. The quest service
/// decides whether it may start (QuestService "encounter").
/// </summary>
internal sealed class ResAddEncounterHandler : BaseResultHandler<ResAddEncounter> {

    public override bool Execute(IResultContext context) {
        var playerRef = context.GetPlayerRef();
        var name = EncounterName(Result);
        if (playerRef is null || string.IsNullOrEmpty(name)) {
            return false;
        }

        playerRef.Tell(new CHARACTER_103_PROTOCOL.MSG_QUESTCOMMAND { Action = "encounter", QuestName = name }, ActorRefs.NoSender);

        return true;
    }

    // The game's files call the field m_encounterName; the generated type may spell it otherwise, so it is looked up.
    private static string EncounterName(ResAddEncounter result) {
        if (result is null) {
            return null;
        }

        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase;
        var type = result.GetType();
        var value = (object) type.GetField("m_encounterName", flags)?.GetValue(result)
            ?? type.GetProperty("m_encounterName", flags)?.GetValue(result);
        return value?.ToString();
    }

}
