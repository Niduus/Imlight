using System;
using System.Linq;
using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Game.LevelScaling;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.levelscale` for the Scales of Knowledge's Level Scaling Elixir (Game/LevelScaling/LevelScales.cs): says whether
/// one is running, and `.levelscale off` ends it. For testing, `.levelscale 70` starts one without the scales or badge.
/// </summary>
internal class CommandLevelScaleProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    private const string HELP = ".levelscale says whether level scaling is on. .levelscale off ends it. "
        + "Testing: .levelscale 70 starts a Level Scaling Elixir of that level (20 to 200, in tens).";

    [Command("levelscale")]
    [Alias("levelscaling", "scales")]
    private void LevelScaleCommand([Remainder] string text = "") {
        try {
            var word = (text ?? "").Trim().ToLowerInvariant();
            var me = Context.Character;
            switch (word) {
                case "":
                case "status":
                    var level = LevelScales.ActiveLevel(me);
                    if (level == 0) {
                        InformSenderClient("Level scaling is off. Use a Scales of Knowledge to turn it on. " + HELP);

                        return;
                    }

                    var left = me.EquipmentBehavior.GetActiveElixirs()
                        .Where(active => LevelScales.ScalingEffectOf(ItemHelper.GetItemTemplate(active.Item)) is not null)
                        .Select(active => TimeSpan.FromSeconds((double) active.Slot.RemainingSeconds))
                        .FirstOrDefault();
                    InformSenderClient($"Level scaling to level {level} is on, for {(int) left.TotalHours} h {left.Minutes} min more. "
                        + "In battles against creatures you fight with the stats of that level. .levelscale off ends it.");

                    return;
                case "off":
                case "stop":
                case "end":
                    Context.SessionActor.Tell(new CHARACTER_103_PROTOCOL.MSG_APPLYLEVELSCALING { ElixirTemplateId = 0 });

                    return;
            }

            if (Context.Account.AuthLevel < AuthLevel.QualityAssurance) {
                InformSenderClient(HELP);

                return;
            }

            var elixir = int.TryParse(word, out var wanted) ? LevelScales.ElixirFor(wanted) : 0;
            if (elixir == 0) {
                InformSenderClient("There is no Level Scaling Elixir of that level. " + HELP);

                return;
            }

            Context.SessionActor.Tell(new CHARACTER_103_PROTOCOL.MSG_APPLYLEVELSCALING { ElixirTemplateId = elixir });
            InformSenderClient($"Level scaling to level {wanted} for 2 hours. .levelscale off ends it.");
        }
        catch (Exception e) {
            Logger.Error("Command levelscale failed: {0}", Logger.Args(e));
            InformSenderClient("Level scaling did not work. See the server log.");
        }
    }

}
