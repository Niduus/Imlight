/*
 * Waiting for the game to finish a round's animations (Niduus, 2026-10-26: "Animation timing is still becoming a huge
 * issue. Sometimes turns just immediately rush").
 *
 * The game never tells the server it has played a round; the server waits its own estimate of the round's animations.
 * The game works each round out itself and plays steps the estimate does not count (a charm going on each ally, a
 * multi-hit spell's hits, deaths mid-spell), so in his Malistaire fight (06:33-06:37) rounds 4, 5 and 6 ended 8 to 30 s
 * before the game was done, and the rest of the round's spells were cut off.
 *
 * The server runs on the same PC as the game, so it reads the game's log (WizardClient.log) while a round plays:
 * - "ResolveCombatRound. Duel ID:<duel> Round:<n> Caster:<slot> Spell:<name>" lists, in order, the spells the game on
 *   this PC is about to play for this duel; the last one is the round's last spell.
 * - "Perform - spell <name>." (camera cuts), announcements, hanging effects and animations mean a spell is playing.
 * - "Showing actor ALL" (and the turn indicator after it) ends most spells; natural attacks end with no such line.
 * When the estimate runs out, the round waits on while the game has not reached its last spell, and then until that
 * spell is over: the log quiet for 2 s after it ended, or after the seconds the server gave that spell, or quiet for
 * 6 s. At most [Battle] WaitForGameSeconds more (0 turns this off). The estimate is still the least a round waits.
 * Without the log (a friend's PC, logging off, another battle), nothing changes. Checked against his log of 2026-10-26:
 * rounds 4, 5 and 6 wait on; the others end within 0 to 2 s of the estimate, as before.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Combat;

internal sealed class GameLogWatch {

    // Seconds the game's log stays quiet after a spell ends before the round counts as played. The game starts the next
    // spell within a second of ending one (71 spells in Niduus's log of 2026-10-26).
    private const double QUIET_SECONDS = 2.0;
    // Seconds of quiet that end the last spell even with no end line and no time for it (a summon can be silent ~7 s).
    private const double LONG_QUIET_SECONDS = 6.0;
    // The most of the log read at once (a round writes about 1 MB).
    private const int MAX_READ_BYTES = 16 * 1024 * 1024;

    // [Battle] WaitForGameSeconds: the most seconds a round waits on after its estimate while the game still plays it.
    internal static float MaxExtraSeconds => Math.Clamp(ConfigurationManager.GetValue("Battle.WaitForGameSeconds", 60f), 0f, 300f);

    // [Battle] GameLogPath: the game's WizardClient.log; empty finds it in the usual Wizard101 folders.
    private static string LogPath {
        get {
            var configured = ConfigurationManager.GetValue("Battle.GameLogPath", "");
            if (!string.IsNullOrWhiteSpace(configured)) {
                return configured.Trim().Trim('"');
            }

            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            foreach (var folder in new[] {
                         Path.Combine(programData, "KingsIsle Entertainment", "Wizard101"),
                         Path.Combine(programFilesX86, "Steam", "steamapps", "common", "Wizard101"),
                         Path.Combine(programFiles, "Steam", "steamapps", "common", "Wizard101"),
                         Path.Combine(programFilesX86, "KingsIsle Entertainment", "Wizard101"),
                         Path.Combine(programFiles, "KingsIsle Entertainment", "Wizard101"),
                     }) {
                var log = Path.Combine(folder, "Bin", "WizardClient.log");
                if (File.Exists(log)) {
                    return log;
                }
            }

            return null;
        }
    }

    internal enum State {
        // The game on this PC is not playing this duel (or its log can't be read).
        NotWatching,
        Playing,
        Done,
    }

    private readonly string _path;
    private readonly string _duelLine;
    // The seconds the server gave each of the round's spells, by name.
    private readonly IReadOnlyDictionary<string, float> _spellSeconds;
    private long _offset;
    private string _partLine = "";
    private bool _inDuel;
    // The round's last spell as the game lists it, and when the game began playing it (by the game's clock).
    private string _lastSpell;
    private DateTime? _lastSpellStarted;
    private bool _lastSpellEnded;
    private bool _spellEnded = true;
    // When the last spell step was logged, by the game's clock (the PC's local time, to the second).
    private DateTime _lastActivity = DateTime.Now;

    private GameLogWatch(string path, ulong duelId, long offset, IReadOnlyDictionary<string, float> spellSeconds) {
        _path = path;
        _duelLine = $"ResolveCombatRound. Duel ID:{duelId} ";
        _offset = offset;
        _spellSeconds = spellSeconds;
    }

    /// <summary>Starts watching the game's log from its end, as a round's actions go out. Null without a log.</summary>
    internal static GameLogWatch Start(ulong duelId, IReadOnlyDictionary<string, float> spellSeconds) {
        try {
            if (MaxExtraSeconds <= 0 || LogPath is not { } path || !File.Exists(path)) {
                return null;
            }

            return new GameLogWatch(path, duelId, new FileInfo(path).Length, new Dictionary<string, float>(spellSeconds));
        }
        catch (Exception e) {
            Logger.Debug("Game log watch could not start: {0}", Logger.Args(e.Message));

            return null;
        }
    }

    /// <summary>Reads what the game has logged since the last look and says whether it is still playing the round.</summary>
    internal State Check() {
        try {
            ReadNewLines();
        }
        catch (Exception e) {
            Logger.Debug("Game log watch could not read {0}: {1}", Logger.Args(_path, e.Message));

            return State.NotWatching;
        }

        if (!_inDuel || _lastSpell is null) {
            return State.NotWatching;
        }

        if (_lastSpellStarted is not { } started) {
            return State.Playing;
        }

        // The log's times are whole seconds, so a step logged at 12:00:00.9 counts as 12:00:00; a second more is waited.
        var now = DateTime.Now;
        var quiet = (now - _lastActivity).TotalSeconds;
        var given = _spellSeconds.TryGetValue(_lastSpell, out var seconds) ? seconds : float.MaxValue;
        var done = (quiet >= QUIET_SECONDS + 1 && ((_spellEnded && _lastSpellEnded) || (now - started).TotalSeconds >= given))
                   || quiet >= LONG_QUIET_SECONDS + 1;

        return done ? State.Done : State.Playing;
    }

    private void ReadNewLines() {
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < _offset) {
            // The game was restarted and began a new log.
            _offset = 0;
            _partLine = "";
        }

        if (stream.Length - _offset > MAX_READ_BYTES) {
            _offset = stream.Length - MAX_READ_BYTES;
            _partLine = "";
        }

        if (stream.Length == _offset) {
            return;
        }

        stream.Seek(_offset, SeekOrigin.Begin);
        var buffer = new byte[stream.Length - _offset];
        var read = 0;
        while (read < buffer.Length) {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n <= 0) {
                break;
            }

            read += n;
        }

        _offset += read;
        var text = _partLine + Encoding.UTF8.GetString(buffer, 0, read);
        var lastBreak = text.LastIndexOf('\n');
        if (lastBreak < 0) {
            _partLine = text;

            return;
        }

        _partLine = text[(lastBreak + 1)..];
        foreach (var line in text[..lastBreak].Split('\n')) {
            Note(line);
        }
    }

    private void Note(string line) {
        var listed = line.IndexOf(_duelLine, StringComparison.Ordinal);
        if (listed >= 0) {
            _inDuel = true;
            // "... Round:4 Caster:0 Spell:Minotaur Death Sun": the list comes out before any of it plays.
            var spell = line.IndexOf(" Spell:", listed, StringComparison.Ordinal);
            if (spell >= 0 && _lastSpellStarted is null) {
                var name = line[(spell + " Spell:".Length)..].Trim();
                if (name.Length > 0 && name != "None") {
                    _lastSpell = name;
                }
            }

            return;
        }

        if (!_inDuel) {
            return;
        }

        if (line.Contains("Showing actor ALL", StringComparison.Ordinal)
            || line.Contains("TurnIndicatorCinematicAction", StringComparison.Ordinal)) {
            // The end of a spell (the turn indicator follows it).
            if (!_spellEnded) {
                _spellEnded = true;
                _lastActivity = LoggedAt(line);
            }

            _lastSpellEnded = _lastSpellStarted is not null;

            return;
        }

        if (!IsSpellPlaying(line)) {
            return;
        }

        _spellEnded = false;
        _lastActivity = LoggedAt(line);
        if (_lastSpellStarted is null && _lastSpell is not null
            && line.Contains($"Perform - spell {_lastSpell}.", StringComparison.Ordinal)) {
            _lastSpellStarted = _lastActivity;
        }
    }

    // A log line starts with its local time: "10/26/26 06:31:52 [DBGL] ...".
    private static DateTime LoggedAt(string line)
        => line.Length >= 17 && DateTime.TryParseExact(line[..17], "MM/dd/yy HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal, out var at)
            ? at
            : DateTime.Now;

    internal static bool IsSpellPlaying(string line)
        => line.Contains("Perform - spell ", StringComparison.Ordinal)
           || line.Contains("ClientAnnounceSpellTextCinematicAction", StringComparison.Ordinal)
           || line.Contains("AddHangingEffectCinematicAction: Adding", StringComparison.Ordinal)
           || (line.Contains("AnimateActorCinematicAction: Playing anim ", StringComparison.Ordinal)
               && !line.Contains("Idle", StringComparison.Ordinal));

}
