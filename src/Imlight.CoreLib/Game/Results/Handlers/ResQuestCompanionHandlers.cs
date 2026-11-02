using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Quests;

namespace Imlight.CoreLib.Game.Results.Handlers;

/// <summary>
/// ResToggleAccompanyNPC: a quest companion starts or stops walking with the acting player (QuestCompanions). What walks
/// along is worked out from the player's quests, so the session only needs telling that it changed.
/// </summary>
internal sealed class ResToggleAccompanyNPCHandler : BaseResultHandler<ResToggleAccompanyNPC> {

    public override bool Execute(IResultContext context) {
        var playerRef = context.GetPlayerRef();
        if (playerRef is null || Result is null || Result.m_templateID == 0) {
            return false;
        }

        playerRef.Tell(new RefreshQuestCompanions(), Akka.Actor.ActorRefs.NoSender);

        return true;
    }

}

/// <summary>
/// ResToggleQuestEffect: a quest effect on the acting player. The ones that bring a companion along (Dyvim, Istar and
/// so on, m_accompanyID) make it walk with the player (QuestCompanions); the effect's own look is not shown.
/// </summary>
internal sealed class ResToggleQuestEffectHandler : BaseResultHandler<ResToggleQuestEffect> {

    public override bool Execute(IResultContext context) {
        var playerRef = context.GetPlayerRef();
        if (playerRef is null || Result is null) {
            return false;
        }

        if (QuestCompanions.GetEffect(Result.m_effectName) is { m_accompanyID: not 0 }) {
            playerRef.Tell(new RefreshQuestCompanions(), Akka.Actor.ActorRefs.NoSender);
        }

        return true;
    }

}
