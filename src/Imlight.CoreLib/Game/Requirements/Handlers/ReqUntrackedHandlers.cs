using System.Collections.Concurrent;
using Imcodec.Cryptography;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Requirements.Handlers;

/// <summary>
/// Requirement classes the server does not track (effects, transformations, the tutorial, placeholders whose
/// purpose is unknown). They passed while their class decoded as null, so they keep passing, except transformation
/// checks, which answer false. ReqHasEffect and ReqHasPolymorphEffect answer for real where the server knows the answer. Each class logs
/// once so a trigger that depends on one can be found.
/// </summary>
internal static class UntrackedRequirement {

    private static readonly ConcurrentDictionary<string, byte> s_logged = new();

    public static bool Pass(string className) => Answer(className, true);

    public static bool Answer(string className, bool result) {
        if (s_logged.TryAdd(className, 0)) {
            Logger.Information("Requirement {0} is not tracked by the server; it always answers {1}.", Logger.Args(className, result));
        }

        return result;
    }

}

internal sealed class ReqUnknown0FF838ACHandler : BaseRequirementHandler<ReqUnknown0FF838AC> {

    public override bool Evaluate(IRequirementContext context) => UntrackedRequirement.Pass(nameof(ReqUnknown0FF838AC));

}

internal sealed class ReqUnknown14CA64DEHandler : BaseRequirementHandler<ReqUnknown14CA64DE> {

    public override bool Evaluate(IRequirementContext context) => UntrackedRequirement.Pass(nameof(ReqUnknown14CA64DE));

}

internal sealed class ReqTutorialStageHandler : BaseRequirementHandler<ReqTutorialStage> {

    public override bool Evaluate(IRequirementContext context) => UntrackedRequirement.Pass(nameof(ReqTutorialStage));

}

/// <summary>
/// ReqHasEffect (m_effectName): the wizard has the named effect. An effect the wizard holds (Wizard.GameEffects, by its
/// name's hash) answers true; the Beastmoon arenas' effects, which the server gives (team, DeathEffect, BG-PlanningPhase:
/// Game/Battlegrounds/Battlegrounds.cs), answer for real. Any other name keeps passing, as it did while untracked, so
/// content that relied on that is unchanged.
/// </summary>
internal sealed class ReqHasEffectHandler : BaseRequirementHandler<ReqHasEffect> {

    public override bool Evaluate(IRequirementContext context) {
        var name = Requirement?.m_effectName;
        var wizard = context.GetWizard();
        if (!string.IsNullOrEmpty(name) && wizard is not null) {
            var nameId = StringHash.Compute(name);
            if (wizard.GameEffects?.Exists(e => e?.m_effectNameID == nameId) == true) {
                return true;
            }

            if (Battlegrounds.Battlegrounds.IsTracked(name)) {
                return Battlegrounds.Battlegrounds.HasEffect(wizard.CharId, name);
            }
        }

        return UntrackedRequirement.Pass(nameof(ReqHasEffect));
    }

}

internal sealed class ReqHasPolymorphEffectHandler : BaseRequirementHandler<ReqHasPolymorphEffect> {

    // A polymorph outside battle is a Beastmoon form (Game/Battlegrounds/BeastForm.cs); the arenas' base teleporters
    // let only a wizard in one out. Nothing else gives one, so everyone else still answers false.
    public override bool Evaluate(IRequirementContext context) => Battlegrounds.BeastForm.HasPolymorph(context.GetWizard());

}

internal sealed class ReqUnknown50E82908Handler : BaseRequirementHandler<ReqUnknown50E82908> {

    public override bool Evaluate(IRequirementContext context) => UntrackedRequirement.Pass(nameof(ReqUnknown50E82908));

}

internal sealed class ReqUnknown02D1D615Handler : BaseRequirementHandler<ReqUnknown02D1D615> {

    public override bool Evaluate(IRequirementContext context) => UntrackedRequirement.Pass(nameof(ReqUnknown02D1D615));

}

internal sealed class ReqHasTransformationHandler : BaseRequirementHandler<ReqHasTransformation> {

    public override bool Evaluate(IRequirementContext context) => UntrackedRequirement.Answer(nameof(ReqHasTransformation), false);

}

internal sealed class ReqUnknown2C948ACCHandler : BaseRequirementHandler<ReqUnknown2C948ACC> {

    public override bool Evaluate(IRequirementContext context) => UntrackedRequirement.Pass(nameof(ReqUnknown2C948ACC));

}

internal sealed class ReqUnknown3569555FHandler : BaseRequirementHandler<ReqUnknown3569555F> {

    public override bool Evaluate(IRequirementContext context) => UntrackedRequirement.Pass(nameof(ReqUnknown3569555F));

}

internal sealed class ReqUnknown6A164DC9Handler : BaseRequirementHandler<ReqUnknown6A164DC9> {

    public override bool Evaluate(IRequirementContext context) => UntrackedRequirement.Pass(nameof(ReqUnknown6A164DC9));

}

internal sealed class ReqUnknown001CD4F2Handler : BaseRequirementHandler<ReqUnknown001CD4F2> {

    public override bool Evaluate(IRequirementContext context) => UntrackedRequirement.Pass(nameof(ReqUnknown001CD4F2));

}
