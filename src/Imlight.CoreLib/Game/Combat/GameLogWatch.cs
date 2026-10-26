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
 *   this PC is about to play for this duel; the last one is the round's last spell (counted, if it is listed more than once).
 * - "Showing actor ALL" and the turn indicator end a spell. A camera cut "Perform - spell <name>." after an end line
 *   or after a cut naming another spell starts that spell (a cheat's cast follows the one before with no end line).
 *   Announcements, hanging effects and animations mean a spell is playing. Natural attacks often end with no end line.
 * The round is played when its last spell has begun, ended with an end line and the log stayed quiet 2 s. Once the
 * server's estimate has run out, also when the last spell's own time has passed with the log quiet 6 s.
 * (v0.10.433 also took 3 s of quiet with the spell's time passed as the end: a natural attack's swing has such a quiet,
 * and round 6 of Niduus's Malistaire fight at 15:25:46 cut it short.)
 * Tuning (Niduus, 2026-10-26 evening: "combat animations needs a bit of tuning"):
 * - Most of the game's step lines also carry its own clock to the hundredth ("Showing actor ALL. at 19001.40"), while
 *   the line's time is whole seconds. Matching the two clocks dates the end line to a tenth of a second, so the round
 *   ends 1.5 s after it (the whole-second times waited 2 to 3 s).
 * - After an end line only a camera cut begins a spell again. The killing blow on Malistaire (18:39:47) logged a death
 *   animation for the already-dead caster right after its end line ("... is dead, so animation (Death_01) will not be
 *   played"), which reopened the spell and left the round idle 7 s.
 * With [Battle] EndRoundWhenGameDone the round ends as soon as that happens; past the estimate it waits for it, at most
 * [Battle] WaitForGameSeconds more (0 turns the watch off). Without the log (a friend's PC, logging off, another
 * battle), the estimate alone counts, as before.
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
    // The same, when the end line's time is known to a tenth of a second from the game's own clock. In Niduus's log of
    // 2026-10-26 nothing followed the round's last end line; between spells the next cut came within 0.8 s (or 2.5 s,
    // the turn indicator moving to the other side, which only happens before the last spell).
    private const double END_QUIET_SECONDS = 1.5;
    // Seconds of quiet that end the last spell with no end line, once its own time has passed (natural attacks).
    private const double LONG_QUIET_SECONDS = 6.0;
    // Seconds of quiet after the server's time ran out that end a round whose last listed spell never began. A big summon
    // is silent up to 17 s (Katzenstein's Monster, Skeletal Dragon), so less cut rounds short.
    private const double SKIPPED_QUIET_SECONDS = 20.0;
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

    // The PC's clock, which the game's log times are in (a replay of an old log sets its own).
    internal static Func<DateTime> Clock = () => DateTime.Now;

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
    private string _lastListedLine;
    private bool _lastSpellUnsure;
    private int _listedRun;
    // The spell the last camera cut named, and whether a spell ended since.
    private string _lastCutSpell;
    private bool _endedSinceCut = true;
    // The round's last spell as the game lists it, how many times the list has it, how many of those have begun, and
    // when the last of them began (by the game's clock).
    private string _lastSpell;
    private int _lastSpellListed;
    private int _lastSpellStarts;
    private DateTime? _lastSpellStarted;
    private bool _lastSpellEnded;
    // A spell ended (or the round has not begun): the next "Perform - spell" line is a new spell's first.
    private bool _spellEnded = true;
    // The game's own clock (seconds since it started) when the last spell ended, from its end line.
    private double? _endedAtGameSeconds;
    // The PC time at the game clock's zero, as early and as late as the lines read so far allow (a line logged at
    // 12:00:05 with "at 100.40" puts it between 11:58:24.60 and 11:58:25.60).
    private DateTime? _gameZeroEarliest;
    private DateTime? _gameZeroLatest;
    // When the last spell step was logged, by the game's clock (the PC's local time, to the second).
    private DateTime _lastActivity = Clock();

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
    /// <param name="pastEstimate">
    /// Whether the server's own time for the round has run out. Before that, only a last spell that ended with its end
    /// line counts as done; after it, a long quiet does too (natural attacks end with no end line).
    /// </param>
    internal State Check(bool pastEstimate) {
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

        // The log's times are whole seconds, so a step logged at 12:00:00.9 counts as 12:00:00; a second more is waited.
        var now = Clock();
        var quiet = (now - _lastActivity).TotalSeconds;
        if (_lastSpellStarted is not { } started) {
            // A spell the game listed but skipped (a pet card it could not pay for) never begins.
            return pastEstimate && quiet >= SKIPPED_QUIET_SECONDS + 1 ? State.Done : State.Playing;
        }

        if (_spellEnded && _lastSpellEnded && (pastEstimate || !_lastSpellUnsure)) {
            if (EndedAt() is { } endedAt) {
                if ((now - endedAt).TotalSeconds >= END_QUIET_SECONDS) {
                    return State.Done;
                }
            }
            else if (quiet >= QUIET_SECONDS + 1) {
                return State.Done;
            }
        }

        // No end line: the last spell's own time has passed and the log has been quiet a while (a 3 s quiet during a
        // natural attack's swing cut it short in Niduus's log of 2026-10-26, 15:25:46).
        var given = _spellSeconds.TryGetValue(_lastSpell, out var seconds) ? seconds : 0f;
        return pastEstimate && quiet >= LONG_QUIET_SECONDS + 1 && (now - started).TotalSeconds >= given
            ? State.Done
            : State.Playing;
    }

    // The PC time the last spell ended, to about a tenth of a second (the latest the lines allow), when the game's
    // clock is known.
    private DateTime? EndedAt()
        => _endedAtGameSeconds is { } ended && _gameZeroLatest is { } latest && _gameZeroEarliest is { } earliest
           && (latest - earliest).TotalSeconds <= 0.5
            ? latest.AddSeconds(ended)
            : null;

    // Narrows when the game's clock started from a line logged at a whole second that carries the game's own time.
    private void NoteGameClock(string line, double gameSeconds) {
        if (line.Length < 17 || !DateTime.TryParseExact(line[..17], "MM/dd/yy HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var at)) {
            return;
        }

        var earliest = at.AddSeconds(-gameSeconds);
        var latest = earliest.AddSeconds(1);
        if (_gameZeroEarliest is { } e && _gameZeroLatest is { } l && earliest <= l && latest >= e) {
            _gameZeroEarliest = earliest > e ? earliest : e;
            _gameZeroLatest = latest < l ? latest : l;
        }
        else {
            // The first line, or the PC's clock moved: start over from this line.
            _gameZeroEarliest = earliest;
            _gameZeroLatest = latest;
        }
    }

    // "... Showing actor ALL. at 19001.40", "... Perform at 19096.807s.": the game's own time, if the line ends with it.
    private static double? GameSecondsOf(string line) {
        var at = line.LastIndexOf(" at ", StringComparison.Ordinal);
        if (at < 0) {
            return null;
        }

        var text = line[(at + 4)..].TrimEnd().TrimEnd('.').TrimEnd('s');
        return text.Contains('.') && double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
            out var seconds)
            ? seconds
            : null;
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
            // "... Round:4 Caster:0 Spell:Minotaur Death Sun": the list comes out before any of it plays. A boss's or a
            // cheat's cast is written twice in a row (Niduus's log of 2026-10-26, played once); three or more lines in a
            // row may be one cast or several (three Tower Shields), which the log can't tell.
            var spell = line.IndexOf(" Spell:", listed, StringComparison.Ordinal);
            _listedRun = line == _lastListedLine ? _listedRun + 1 : 1;
            if (spell >= 0 && _lastSpellStarts == 0 && _listedRun > 1) {
                _lastSpellUnsure |= _listedRun >= 3;
            }
            else if (spell >= 0 && _lastSpellStarts == 0) {
                var name = line[(spell + " Spell:".Length)..].Trim();
                if (name.Length > 0 && name != "None") {
                    _lastSpellUnsure &= name == _lastSpell;
                    _lastSpellListed = name == _lastSpell ? _lastSpellListed + 1 : 1;
                    _lastSpell = name;
                }
            }

            _lastListedLine = line;

            return;
        }

        if (!_inDuel) {
            return;
        }

        var gameSeconds = GameSecondsOf(line);
        if (gameSeconds is { } seconds) {
            NoteGameClock(line, seconds);
        }

        if (line.Contains("Showing actor ALL", StringComparison.Ordinal)
            || line.Contains("TurnIndicatorCinematicAction", StringComparison.Ordinal)) {
            // The end of a spell (the turn indicator follows it, and also comes before a spell after a pass).
            _endedSinceCut = true;
            if (!_spellEnded) {
                _spellEnded = true;
                _endedAtGameSeconds = gameSeconds;
                _lastActivity = LoggedAt(line);
                _lastSpellEnded = _lastSpellStarts >= _lastSpellListed && _lastSpellStarts > 0;
            }

            return;
        }

        if (!IsSpellPlaying(line)) {
            return;
        }

        // A spell starts with a camera cut naming it ("Perform - spell <name>."), after an end line or a cut naming
        // another spell (a cheat's cast plays right after the one before, with no end line between). After an end
        // line, nothing but a cut begins one: a dead caster's death animation can follow the end line.
        var cut = line.IndexOf("Perform - spell ", StringComparison.Ordinal);
        if (_spellEnded && cut < 0) {
            return;
        }

        _spellEnded = false;
        _endedAtGameSeconds = null;
        _lastActivity = LoggedAt(line);
        if (cut < 0) {
            return;
        }

        var named = line[(cut + "Perform - spell ".Length)..].TrimEnd().TrimEnd('.');
        var starts = named != _lastCutSpell || _endedSinceCut;
        _lastCutSpell = named;
        _endedSinceCut = false;
        if (starts && named == _lastSpell) {
            _lastSpellStarts++;
            if (_lastSpellStarts >= _lastSpellListed) {
                _lastSpellStarted ??= _lastActivity;
            }
        }
    }

    // A log line starts with its local time: "10/26/26 06:31:52 [DBGL] ...".
    private static DateTime LoggedAt(string line)
        => line.Length >= 17 && DateTime.TryParseExact(line[..17], "MM/dd/yy HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal, out var at)
            ? at
            : Clock();

    internal static bool IsSpellPlaying(string line)
        => line.Contains("Perform - spell ", StringComparison.Ordinal)
           || line.Contains("ClientAnnounceSpellTextCinematicAction", StringComparison.Ordinal)
           || line.Contains("AddHangingEffectCinematicAction: Adding", StringComparison.Ordinal)
           || (line.Contains("AnimateActorCinematicAction: Playing anim ", StringComparison.Ordinal)
               && !line.Contains("Idle", StringComparison.Ordinal));

}
