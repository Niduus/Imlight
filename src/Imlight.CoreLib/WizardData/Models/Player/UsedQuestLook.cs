namespace Imlight.CoreLib.WizardData.Models.Player;

/// <summary>
/// A quest object that took its used look for this wizard (the Crystal Ball shattered, the bubble popped), so the quest
/// safety guards can bring it back when a later step needs the same object again (Game/Quests/QuestSafety).
/// </summary>
public sealed class UsedQuestLook {
    public string Zone { get; set; }
    public string Tag { get; set; }
    public ulong TemplateId { get; set; }
    public string State { get; set; }
}
