using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Services;

namespace Imlight.CoreLib.Game.Results.Handlers;

/// <summary>
/// ResEquipItem: puts an item on the acting player. Only pets are put on (Secret Strife of Pets puts the school's pet on).
/// </summary>
internal sealed class ResEquipItemHandler : BaseResultHandler<ResEquipItem> {

    public override bool Execute(IResultContext context) {
        var playerRef = context.GetPlayerRef();
        if (playerRef is null || Result is null || Result.m_templateID == 0
            || !string.Equals(Result.m_categoryName, "Pet", System.StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        playerRef.Tell(new WearQuestPet { TemplateId = Result.m_templateID }, Akka.Actor.ActorRefs.NoSender);

        return true;
    }

}

/// <summary>
/// ResForcePlayerAsPet: the acting player wears the pet and takes its look, as with the Play as Pet button.
/// </summary>
internal sealed class ResForcePlayerAsPetHandler : BaseResultHandler<ResForcePlayerAsPet> {

    public override bool Execute(IResultContext context) {
        var playerRef = context.GetPlayerRef();
        if (playerRef is null || Result is null || Result.m_petTemplate == 0) {
            return false;
        }

        playerRef.Tell(new BecomeQuestPet { TemplateId = Result.m_petTemplate }, Akka.Actor.ActorRefs.NoSender);

        return true;
    }

}
