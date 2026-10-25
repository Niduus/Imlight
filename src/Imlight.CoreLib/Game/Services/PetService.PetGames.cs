/*
 * Imlight
 * Copyright (C) 2026 Revive101
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
 *
 * ========================================================================
 * PET SERVICE: PET GAMES YOU PLAY
 * ========================================================================
 *
 * PURPOSE:
 * The Play button of the pet game kiosks (Niduus, 2026-10-24: "Pet games you actually play", instead of
 * only skipping them). The kiosk's MSG_PETGAMEJOIN sends the player to the game's room
 * (PetGameZones); there the game is opened the way the hatch window is (MSG_PETGAMEINIT, the game's
 * MSG_PETGAMEREADY, MSG_PETGAMESTART), played, and ended with MSG_PETGAMEEND and the same results
 * and feed window as a skip. Closing the window sends the player back to the kiosk.
 *
 * USAGE EXAMPLE:
 * Part of PetService; the kiosk's Play button comes to ReceivePetGameJoin.
 *
 * NOTE:
 * Read from the game's code (WizardGraphicalClient, PetGameBase and PetGameDance, 2026-10-24):
 * - MSG_PETGAMESTART puts the window in its playing state, which at once sends MSG_PETGAMEDATA with
 *   Data = one byte 0. The results window's buttons send MSG_PETGAMEDATA too: 3 = done (closes),
 *   4 + 8 bytes = feed this snack; 1 and 2 (four-byte numbers) are the test Win and Lose buttons.
 *   Closing the window sends MSG_PETGAMEENDING with no fields.
 * - MSG_PETGAMEEND's Data is a PetGameEndData; the window shows it as the results.
 * - Dance: 5 rounds (imgRound0..4). Each round the server sends MSG_PETGAMEDANCE with Moves, one letter
 *   per arrow: a = up, b = right, c = down, d = left. The game shows them, the player repeats them
 *   (the game marks each press right or wrong and stops at the first wrong one, or when the time,
 *   moves + 3 seconds, runs out), and 1.5 s later the game sends MSG_PETGAMEDANCE back with the
 *   player's moves in the same letters. The server judges them and sends the next round or the end.
 *
 * TODO:
 * - Untested in game. The round lengths (3 + level + round) and the rewards are guesses.
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/24/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

/// <summary>
/// The timers of a pet game being played (see PetService.PetGames).
/// </summary>
internal enum PetGameTimerKind : byte {
    // Open the game's window, once the game has had a moment to load the room.
    Init,
    // The game did not ask for the first round; send it anyway.
    FirstRound,
    // Too long in the room; back to the kiosk.
    Timeout,
    // The game is over; back to the kiosk.
    Return,
}

internal sealed class MSG_PETGAMEPLAYTIMER : IServerMessage {

    public byte MessageOrder { get; } = 200;
    public byte ServiceID { get; } = 103;

    public PetGameTimerKind Kind;
    public string Reason;

}

internal partial class PetService {

    private const string PLAY_INIT_TIMER = "petGameInit";
    private const string PLAY_FIRST_ROUND_TIMER = "petGameFirstRound";
    private const string PLAY_TIMEOUT_TIMER = "petGameTimeout";
    private const string PLAY_RETURN_TIMER = "petGameReturn";
    private static readonly TimeSpan PLAY_INIT_DELAY = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PLAY_INIT_RETRY = TimeSpan.FromSeconds(3);
    private const int PLAY_INIT_TRIES = 5;
    private static readonly TimeSpan PLAY_FIRST_ROUND_WAIT = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PLAY_RETURN_DELAY = TimeSpan.FromSeconds(1);

    // The results window's MSG_PETGAMEDATA commands (first byte).
    private const byte DATA_STARTED = 0;
    private const byte DATA_TEST_WIN = 1;
    private const byte DATA_TEST_LOSE = 2;
    private const byte DATA_DONE = 3;
    private const byte DATA_FEED = 4;

    // The obstacle course's commands: the seed (server), the game's position samples, and its finish:
    // elapsed seconds, penalty seconds and bonus seconds (three 32-bit numbers).
    private const byte COURSE_SEED = 0x21;
    private const byte COURSE_SAMPLES = 0x22;
    private const byte COURSE_SAMPLES_FLUSH = 0x23;
    private const byte COURSE_DONE = 0x24;

    private const int DANCE_ROUNDS = 5;
    private const string DANCE_LETTERS = "abcd";
    private static readonly string[] s_danceArrowNames = ["up", "right", "down", "left"];

    // The game being played in this session's room; null anywhere else.
    private PetGameVisit _play;
    private bool _playReady;
    private bool _playStarted;
    private bool _playOver;
    private bool _playLeaving;
    private int _playInitTries;
    private int _danceRound;
    private string _danceMoves;

    private static readonly Random s_playRandom = new();

    /// <summary>
    /// The kiosk's Play button. Answered with yes for the games the server can run, and the player goes to the game's
    /// room; any other game gets the old "not in yet" note.
    /// </summary>
    private void JoinPetGame(string game, string trackText) {
        var wizard = GetActiveWizard();
        var track = int.TryParse(trackText?.Trim(), out var n) ? Math.Max(0, n) : 0;
        Logger.Information("Pet game join: game {0}, track {1}.", Logger.Args(game, trackText));

        string zone = null;
        string why = null;
        var playable = PetGameZones.Enabled && PetGameZones.TryGetZone(game, track, out zone, out why);
        if (!playable) {
            if (why is not null) {
                Logger.Information("Pet game join: {0} cannot be played: {1}.", Logger.Args(game, why));
            }

            SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEJOINRSP { Game = game, Success = 0 });
            InformGameClient(PetGameZones.Enabled
                ? $"This pet game cannot be played here yet; use Skip. Games you can play: {string.Join(", ", PetGameZones.PlayableGames.Select(GameLabel))}."
                : "Playing pet games is turned off ([Pets] PlayGames); use Skip.");

            return;
        }

        if (PetProgress.GetBehavior(PetCare.GetWornPet(wizard)) is null) {
            SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEJOINRSP { Game = game, Success = 0 });
            InformGameClient("You need to wear a pet to play a pet game.");

            return;
        }

        SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEJOINRSP { Game = game, Success = 1 });

        // Play again from the results window: already in the room, so the game starts over here.
        if (_play is not null && string.Equals(zone, wizard.Zone, StringComparison.OrdinalIgnoreCase)) {
            _play = _play with { Track = track };
            PetGameZones.Begin(_play);
            Logger.Information("Pet game: {0} plays {1} again (level {2}).", Logger.Args(wizard.CharId, game, track));
            StartPlay();

            return;
        }

        var returnLocation = wizard.Location is { X: 0, Y: 0, Z: 0 }
            ? PetGameZones.LOCATION
            : Util.GetCompactStringFromVector(wizard.Location, wizard.Orientation);
        // From one game room to another (play again at another level): back to the kiosk afterwards, not this room.
        var returnZone = wizard.Zone;
        if (_play is not null && PetGameZones.IsPetGameZone(wizard.Zone)) {
            returnZone = _play.ReturnZone;
            returnLocation = _play.ReturnLocation;
            ForgetPlay();
        }

        var visit = new PetGameVisit(wizard.CharId, game, track, zone, returnZone, returnLocation);
        PetGameZones.Begin(visit);
        Logger.Information("Pet game: sending {0} to {1} to play {2} (level {3}); they come back to {4} at {5}.",
            Logger.Args(wizard.CharId, zone, game, track, returnZone, returnLocation));
        Teleport(zone, doTeleportEffects: true, makePrivate: true, destinationLocation: PetGameZones.LOCATION);
    }

    /// <summary>
    /// Every arrival in a zone: in a pet game room the game starts; anywhere else a trip is over.
    /// </summary>
    private void OnPetGameRoomAttach(Wizard wizard) {
        var hasVisit = PetGameZones.TryGet(wizard.CharId, out var visit);
        if (!PetGameZones.IsPetGameZone(wizard.Zone)) {
            if (hasVisit) {
                Logger.Information("Pet game: {0} arrived in {1}, not a pet game room; the trip to {2} is over.",
                    Logger.Args(wizard.CharId, wizard.Zone, visit.Zone));
                PetGameZones.End(wizard.CharId);
            }

            return;
        }

        SessionActor.ClientMessageLogPrefix = "Pet game room";
        if (!hasVisit || !string.Equals(visit.Zone, wizard.Zone, StringComparison.OrdinalIgnoreCase)) {
            // A login or a teleport here by hand: nothing to play, so back to the Pet Pavilion.
            Logger.Information("Pet game: {0} arrived in {1} with no game to play; sending them to the Pet Pavilion.",
                Logger.Args(wizard.CharId, wizard.Zone));
            _play = new PetGameVisit(wizard.CharId, "none", 0, wizard.Zone, PetGameZones.PET_PARK_ZONE, PetGameZones.LOCATION);
            SchedulePlayReturn("no game to play", PLAY_RETURN_DELAY);

            return;
        }

        _play = visit;
        _playLeaving = false;
        Logger.Information("Pet game: {0} arrived in {1} to play {2} (level {3}), {4:0.0} s after pressing Play.",
            Logger.Args(wizard.CharId, wizard.Zone, visit.Game, visit.Track, (DateTimeOffset.UtcNow - visit.Started).TotalSeconds));
        Timers.StartSingleTimer(PLAY_TIMEOUT_TIMER, new MSG_PETGAMEPLAYTIMER { Kind = PetGameTimerKind.Timeout }, PetGameZones.Timeout);
        StartPlay();
    }

    private void StartPlay() {
        _playReady = false;
        _playStarted = false;
        _playOver = false;
        _playInitTries = 0;
        _danceRound = 0;
        _danceMoves = null;
        Timers.Cancel(PLAY_FIRST_ROUND_TIMER);
        // The game is still loading the room when it attaches; a message sent now may be lost.
        Timers.StartSingleTimer(PLAY_INIT_TIMER, new MSG_PETGAMEPLAYTIMER { Kind = PetGameTimerKind.Init }, PLAY_INIT_DELAY);
    }

    [MessageHandler(typeof(MSG_PETGAMEPLAYTIMER))]
    private void ReceivePetGamePlayTimer(MSG_PETGAMEPLAYTIMER message) {
        if (_play is null) {
            return;
        }

        try {
            switch (message.Kind) {
                case PetGameTimerKind.Init:
                    SendPlayInit();
                    break;
                case PetGameTimerKind.FirstRound:
                    if (_playStarted && !_playOver && _danceMoves is null) {
                        Logger.Information("Pet game: the game did not ask for the first round; sending it.");
                        SendNextRound();
                    }

                    break;
                case PetGameTimerKind.Timeout:
                    Logger.Information("Pet game: {0} has been in {1} for {2}; time is up.",
                        Logger.Args(_play.CharId, _play.Zone, PetGameZones.Timeout));
                    InformGameClient("Time is up in the pet game room.");
                    ReturnFromPetGame("time is up");
                    break;
                case PetGameTimerKind.Return:
                    ReturnFromPetGame(message.Reason);
                    break;
            }
        }
        catch (Exception ex) {
            Logger.Error("Pet game: timer {0} failed. {1}", Logger.Args(message.Kind, ex));
        }
    }

    // The game answers with MSG_PETGAMEREADY once it has the room's game object; until then the message is dropped,
    // so it goes again a few times.
    private void SendPlayInit() {
        if (_play is null || _playReady) {
            return;
        }

        if (++_playInitTries < PLAY_INIT_TRIES) {
            Timers.StartSingleTimer(PLAY_INIT_TIMER, new MSG_PETGAMEPLAYTIMER { Kind = PetGameTimerKind.Init }, PLAY_INIT_RETRY);
        }

        var data = InitData(_play.Game);
        if (data.Length == 0) {
            // The game makes the game's window from this data and crashes without it.
            Timers.Cancel(PLAY_INIT_TIMER);
            Logger.Error("Pet game: no game info for {0}; sending the player back instead.", Logger.Args(_play.Game));
            SchedulePlayReturn("no game info", PLAY_RETURN_DELAY);

            return;
        }

        Logger.Information("Pet game: sending MSG_PETGAMEINIT Game={0} Track={1} ({2} bytes, try {3}).",
            Logger.Args(_play.Game, _play.Track, data.Length, _playInitTries));
        SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEINIT {
            Game = _play.Game,
            Data = data,
            MinLevel = 0,
            Track = (byte) Math.Clamp(_play.Track, 0, byte.MaxValue),
        });
    }

    /// <summary>
    /// Every game's window is made from the game's PetGames.xml entry sent here (the game reads it into the
    /// PetGameInfo it keeps, and Dance reads its level's scene from it, so an empty one crashes the game); the
    /// obstacle course also reads its settings (pet size, gravity, falling penalty, the time of each level) from it.
    /// </summary>
    private static Imcodec.IO.ByteString InitData(string gameName) {
        if (Imlight.CoreLib.Game.Pet.PetGameConfig.FindGame(gameName) is not { } game) {
            Logger.Error("Pet game: {0} is not in PetGames.xml.", Logger.Args(gameName));

            return "";
        }

        var serializer = new Imcodec.ObjectProperty.ObjectSerializer(Versionable: false, Behaviors: Imcodec.ObjectProperty.SerializerFlags.None);
        if (!serializer.Serialize(game, Imcodec.ObjectProperty.PropertyFlags.Prop_Public, out var data)) {
            Logger.Error("Pet game: could not write the game info of {0}.", Logger.Args(gameName));

            return "";
        }

        return data;
    }

    private static bool IsObstacleCourse(string gameName)
        => string.Equals(gameName, "PetGameObstacleCourse", StringComparison.Ordinal);

    [MessageHandler(typeof(PET_9_PROTOCOL.MSG_PETGAMEREADY))]
    private void ReceivePetGameReady(PET_9_PROTOCOL.MSG_PETGAMEREADY message) {
        if (_play is null || _playReady) {
            return;
        }

        _playReady = true;
        _playStarted = true;
        Timers.Cancel(PLAY_INIT_TIMER);
        Logger.Information("Pet game: the game is ready; sending MSG_PETGAMESTART Game={0}.", Logger.Args(_play.Game));
        SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMESTART { Game = _play.Game, Data = "" });
        if (IsObstacleCourse(_play.Game)) {
            // The only way the course shows its obstacles: one group per section, picked from this seed.
            var seed = (uint) s_playRandom.Next();
            var bytes = new byte[5];
            bytes[0] = COURSE_SEED;
            BitConverter.GetBytes(seed).CopyTo(bytes, 1);
            Logger.Information("Pet game: obstacle course seed {0}.", Logger.Args(seed));
            SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEDATA { Game = _play.Game, Data = bytes });

            return;
        }

        Timers.StartSingleTimer(PLAY_FIRST_ROUND_TIMER, new MSG_PETGAMEPLAYTIMER { Kind = PetGameTimerKind.FirstRound }, PLAY_FIRST_ROUND_WAIT);
    }

    [MessageHandler(typeof(PET_9_PROTOCOL.MSG_PETGAMEDATA))]
    private void ReceivePetGameData(PET_9_PROTOCOL.MSG_PETGAMEDATA message) {
        if (_play is null) {
            return;
        }

        try {
            byte[] data = message.Data;
            var command = data is { Length: > 0 } ? data[0] : (byte) 255;
            if (command is COURSE_SAMPLES or COURSE_SAMPLES_FLUSH) {
                return;
            }

            Logger.Information("Pet game: the game sent MSG_PETGAMEDATA Game={0} Data={1}.",
                Logger.Args(message.Game, data is null ? "none" : Convert.ToHexString(data)));

            switch (command) {
                case DATA_STARTED when !_playOver:
                    Timers.Cancel(PLAY_FIRST_ROUND_TIMER);
                    if (_danceMoves is null) {
                        SendNextRound();
                    }

                    break;
                case DATA_TEST_WIN when !_playOver:
                    EndPlay(1.0f, "the Win button");
                    break;
                case DATA_TEST_LOSE when !_playOver:
                    EndPlay(0.0f, "the Lose button");
                    break;
                case DATA_DONE:
                    SchedulePlayReturn("the player is done", PLAY_RETURN_DELAY);
                    break;
                case DATA_FEED:
                    FeedAfterPlay(data);
                    break;
                case COURSE_DONE when !_playOver && data.Length >= 13:
                    FinishCourse(BitConverter.ToInt32(data, 1), BitConverter.ToInt32(data, 5), BitConverter.ToInt32(data, 9));
                    break;
            }
        }
        catch (Exception ex) {
            Logger.Error("Pet game: MSG_PETGAMEDATA failed. {0}", Logger.Args(ex));
        }
    }

    [MessageHandler(typeof(PET_9_PROTOCOL.MSG_PETGAMEENDING))]
    private void ReceivePetGameEnding(PET_9_PROTOCOL.MSG_PETGAMEENDING message) {
        if (_play is null) {
            return;
        }

        Logger.Information("Pet game: the game closed the window (MSG_PETGAMEENDING).");
        Timers.Cancel(PLAY_RETURN_TIMER);
        _playLeaving = false;
        SchedulePlayReturn("the player closed the game", PLAY_RETURN_DELAY);
    }

    #region Dance

    private void SendNextRound() {
        if (_play is null || _playOver) {
            return;
        }

        if (!string.Equals(_play.Game, "PetGameDance", StringComparison.OrdinalIgnoreCase)) {
            return;
        }

        var length = DanceMoveCount(_play.Track, _danceRound);
        var moves = new StringBuilder(length);
        for (var i = 0; i < length; i++) {
            moves.Append(DANCE_LETTERS[s_playRandom.Next(DANCE_LETTERS.Length)]);
        }

        _danceMoves = moves.ToString();
        Logger.Information("Pet game: dance round {0} of {1}: {2} ({3}).",
            Logger.Args(_danceRound + 1, DANCE_ROUNDS, _danceMoves, DescribeMoves(_danceMoves)));
        SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEDANCE { Moves = _danceMoves });
    }

    /// <summary>
    /// How many arrows a round has: 3 in the first round of the first level, one more each round and each level
    /// (a guess; [Pets] DanceFirstMoves and DanceMaxMoves change it).
    /// </summary>
    private static int DanceMoveCount(int track, int round) {
        var first = Math.Max(1, ConfigurationManager.GetValue("Pets.DanceFirstMoves", 3));
        var max = Math.Max(first, ConfigurationManager.GetValue("Pets.DanceMaxMoves", 12));

        return Math.Clamp(first + Math.Clamp(track, 0, 10) + round, first, max);
    }

    [MessageHandler(typeof(PET_9_PROTOCOL.MSG_PETGAMEDANCE))]
    private void ReceivePetGameDance(PET_9_PROTOCOL.MSG_PETGAMEDANCE message) {
        if (_play is null || _playOver || _danceMoves is null) {
            return;
        }

        try {
            var played = message.Moves.ToString() ?? "";
            var right = string.Equals(played, _danceMoves, StringComparison.Ordinal);
            Logger.Information("Pet game: dance round {0}: asked {1}, played {2} ({3}): {4}.",
                Logger.Args(_danceRound + 1, _danceMoves, played, DescribeMoves(played), right ? "right" : "wrong"));
            _danceMoves = null;
            if (!right) {
                EndPlay((float) _danceRound / DANCE_ROUNDS, $"a wrong move in round {_danceRound + 1}");

                return;
            }

            _danceRound++;
            if (_danceRound >= DANCE_ROUNDS) {
                EndPlay(1.0f, "all rounds danced");

                return;
            }

            SendNextRound();
        }
        catch (Exception ex) {
            Logger.Error("Pet game: MSG_PETGAMEDANCE failed. {0}", Logger.Args(ex));
        }
    }

    private static string DescribeMoves(string moves)
        => string.Join(" ", (moves ?? "").Select(c => c - 'a' is >= 0 and < 4 ? s_danceArrowNames[c - 'a'] : $"?{(int) c}"));

    #endregion

    #region Obstacle course

    /// <summary>
    /// The course's finish line, or its clock running out: time left = the level's time - elapsed + bonus - penalty
    /// (as the game counts it). Finishing scores from 0.5 (just in time) to 1 (half the time or more left); running
    /// out of time scores 0.2.
    /// </summary>
    private void FinishCourse(int elapsed, int penalty, int bonus) {
        var info = Imlight.CoreLib.Game.Pet.PetGameConfig.FindGame(_play.Game) as PetObstacleCourseGameInfo;
        var times = info?.m_trackTimes;
        var limit = times is { Count: > 0 } ? times[Math.Clamp(_play.Track, 0, times.Count - 1)] : 0;
        var left = limit - elapsed + bonus - penalty;
        float score;
        if (limit <= 0) {
            score = 1f;
        }
        else if (left <= 0) {
            score = 0.2f;
        }
        else {
            score = Math.Min(1f, 0.5f + (float) left / limit);
        }

        EndPlay(score, $"the course: {elapsed} s, penalty {penalty} s, bonus {bonus} s, level time {limit} s, {left} s left");
    }

    #endregion

    /// <summary>
    /// The end of a game: the pet gets its experience and the track's stats, and the game is sent the results window
    /// with the feed step after it, as after a skip.
    /// </summary>
    /// <param name="score">0 to 1: how much of the game was done.</param>
    private void EndPlay(float score, string why) {
        if (_play is null || _playOver) {
            return;
        }

        _playOver = true;
        Timers.Cancel(PLAY_FIRST_ROUND_TIMER);
        var wizard = GetActiveWizard();
        var pet = PetCare.GetWornPet(wizard);
        var behavior = PetProgress.GetBehavior(pet);
        if (behavior is null) {
            InformGameClient("You need to wear a pet to train it.");
            SchedulePlayReturn("no pet", PLAY_RETURN_DELAY);

            return;
        }

        score = Math.Clamp(score, 0f, 1f);
        PetProgress.EnsureInitialized(pet);
        var growth = new PetProgress.PetGrowth();
        var game = Imlight.CoreLib.Game.Pet.PetGameConfig.FindGame(_play.Game);
        var cost = Imlight.CoreLib.Game.Pet.PetGameConfig.TryGetEnergyCost(_play.Game, behavior.m_level, out var energy) ? energy : 2;
        var xp = PlayedGameXp(cost, score);
        PetProgress.AddXp(pet, xp, growth);

        PetStatModificationSet chosen = null;
        var tracks = game?.m_trackChoices;
        if (tracks is { Count: > 0 } && score > 0f) {
            chosen = tracks[_play.Track >= 0 && _play.Track < tracks.Count ? _play.Track : s_playRandom.Next(tracks.Count)];
            PetProgress.ApplyStatChanges(pet, chosen?.m_modifications, growth);
        }

        Logger.Information("Pet game: {0} ended ({1}), score {2:0.00}: {3} XP, stats from track {4}.",
            Logger.Args(_play.Game, why, score, xp, chosen is null ? "none" : _play.Track));

        // The results window first: level-up windows sent while it opens may not show.
        if (TryWriteEndData(_play.Game, growth.XpGained, WithActualChanges(chosen, growth), out var data, score)) {
            SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEEND { Game = _play.Game, Data = data });
        }

        _skipAwaitingFeed = pet.m_globalID.Full;
        PetCare.Finish(wizard, pet, growth, SendToSocket);
        if (score > 0f) {
            Imlight.CoreLib.Game.Events.ClassProjects.Record(SessionActor.ActorRef, wizard, Imcodec.ObjectProperty.TypeCache.ClassProjectActivityType.CPT_WinPetGame);
        }
        InformGameClient(PetCare.Summary(pet, growth));
        ResummonAfterLevelUp(pet, growth);
    }

    /// <summary>
    /// A finished game gives half again what a skip gives (twice the energy cost); a game stopped early gives its part
    /// of a skip's. [Pets] PlayedGameXpPercent changes the finished game's share.
    /// </summary>
    private static uint PlayedGameXp(int energyCost, float score) {
        var skipXp = Math.Max(2, energyCost * 2);
        if (score >= 1f) {
            var percent = Math.Max(1, ConfigurationManager.GetValue("Pets.PlayedGameXpPercent", 150));

            return (uint) Math.Max(1, (int) Math.Round(skipXp * percent / 100.0));
        }

        return (uint) Math.Max(1, (int) Math.Round(skipXp * score));
    }

    /// <summary>
    /// The feed button of the results window after a played game: Data = 4, then the snack (8 bytes).
    /// </summary>
    private void FeedAfterPlay(byte[] data) {
        var wizard = GetActiveWizard();
        var pet = PetCare.GetWornPet(wizard);
        var snack = FindSnackIn(wizard, data?.Skip(1).ToArray());
        if (pet is null || snack is null) {
            Logger.Warning("Pet game: the feed after the game named no snack of {0} ({1}).",
                Logger.Args(wizard?.CharId, data is null ? "none" : Convert.ToHexString(data)));
            SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMESNACKFEEDFAILED());

            return;
        }

        var snackTemplateId = snack.m_templateID;
        var held = new List<IMessage>();
        if (PetCare.Feed(wizard, pet, snackTemplateId, 1, held.Add, out var fed) is not null) {
            SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMESNACKFEEDFAILED());
            held.ForEach(SendToSocket);

            return;
        }

        _skipAwaitingFeed = 0;
        var snackMods = (CoreObjectFactory.GetCoreTemplate(snackTemplateId) as PetSnackItemTemplate)?.m_statModifierSet;
        SendHeldThenFeedResult(held, _play?.Game ?? "", fed.XpGained, WithActualChanges(snackMods, fed));
        InformGameClient(PetCare.Summary(pet, fed));
        ResummonAfterLevelUp(pet, fed);
    }

    private void SchedulePlayReturn(string reason, TimeSpan delay) {
        if (_play is null || _playLeaving) {
            return;
        }

        _playLeaving = true;
        Logger.Information("Pet game: {0} goes back to {1} in {2} s ({3}).",
            Logger.Args(_play.CharId, _play.ReturnZone, delay.TotalSeconds, reason));
        Timers.StartSingleTimer(PLAY_RETURN_TIMER, new MSG_PETGAMEPLAYTIMER { Kind = PetGameTimerKind.Return, Reason = reason }, delay);
    }

    private void ReturnFromPetGame(string reason) {
        var visit = _play;
        if (visit is null) {
            return;
        }

        Logger.Information("Pet game: sending {0} back to {1} at {2} ({3}).",
            Logger.Args(visit.CharId, visit.ReturnZone, visit.ReturnLocation, reason));
        ForgetPlay();
        Teleport(visit.ReturnZone, doTeleportEffects: true, destinationLocation: visit.ReturnLocation);
    }

    private void ForgetPlay() {
        Timers.Cancel(PLAY_INIT_TIMER);
        Timers.Cancel(PLAY_FIRST_ROUND_TIMER);
        Timers.Cancel(PLAY_TIMEOUT_TIMER);
        Timers.Cancel(PLAY_RETURN_TIMER);
        if (_play is not null) {
            PetGameZones.End(_play.CharId);
        }

        SessionActor.ClientMessageLogPrefix = null;
        _play = null;
        _playReady = false;
        _playStarted = false;
        _playOver = false;
        _playLeaving = false;
        _danceMoves = null;
    }

    private static string GameLabel(string game)
        => game.StartsWith("PetGame", StringComparison.Ordinal) ? game["PetGame".Length..] : game;

}
