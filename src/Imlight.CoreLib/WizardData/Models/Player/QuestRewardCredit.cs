using System;
using System.Collections.Generic;

namespace Imlight.CoreLib.WizardData.Models.Player;

/// <summary>
/// What a quest gives for sure (Game/Quests/QuestRewardCatchUp): its first reward, or the best one a wizard got.
/// </summary>
public sealed class QuestRewardCredit {
    public int Gold { get; set; }
    public int Xp { get; set; }
    public int TrainingPoints { get; set; }
    // Item template id (or a pick-one table's "pick:id|id|...") and how many.
    public Dictionary<string, int> Items { get; set; } = new(StringComparer.Ordinal);
    public List<uint> Spells { get; set; } = [];
}
