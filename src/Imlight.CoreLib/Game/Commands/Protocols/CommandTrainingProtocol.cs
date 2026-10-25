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

using Imlight.Common;
using Imcodec.MessageLayer.Generated;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// Lets every player give themselves training points, if [Trainers] PlayersCanGainTrainingPoints is on.
/// </summary>
internal class CommandTrainingProtocol : CommandProtocol {

    private const int MAX_PER_USE = 100;

    private static bool s_enabled => ConfigurationManager.GetValue("Trainers.PlayersCanGainTrainingPoints", true);

    internal override string Group { get; set; } = "";

    [Command("trainingpoints")]
    [Alias("gaintp")]
    private void GainTrainingPointsCommand(string amount) {
        if (!s_enabled) {
            InformSenderClient("This server has turned this command off.");

            return;
        }

        if (!int.TryParse(amount, out var points) || points < 1 || points > MAX_PER_USE) {
            InformSenderClient($"Type a number from 1 to {MAX_PER_USE}, such as .gaintp 5");

            return;
        }

        var newTrainingPoints = Context.Character.MagicSchoolBehavior.TrainingPoints + points;
        Context.Character.UpdateTrainingPoints(newTrainingPoints);

        var networkMessage = new WIZARD_12_PROTOCOL.MSG_UPDATETRAINING() {
            TrainingPoints = newTrainingPoints
        };
        Context.SessionActor.Tell(networkMessage, null);

        InformSenderClient($"You gained {points} training points. You now have {newTrainingPoints}.");
    }

}
