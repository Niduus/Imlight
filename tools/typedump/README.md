# Type dump from a new Wizard101 program

The server's data classes (players, creatures, battles, items...) are generated from a *type dump*: the list of every
data type in `WizardGraphicalClient.exe` with its fields. When KingsIsle patches the game program and changes a type,
a server built on the old dump can no longer talk to the game. The dumps live in `data/typedump/`; setup and the
release workflow build with the newest one (by the revision number in the file name).

The Ahmarra update (r813697, client W.1.620, 2026-11-12) needed a new one: new fields on CoreObject
(`m_turnSpeedMultiplier`), CombatParticipant (`m_auraCasterSC`, `m_auraRound`), CoreObjectInfo (`m_statsTemplateID`),
GroupMember, guild open houses, keybinds and 65 new types.

## Making one (cloud, no Windows needed)

Read-only copies from the player's PC: `Bin\WizardGraphicalClient.exe`, `Bin\vcruntime140.dll`,
`Data\GameData\Root.wad`, laid out as `<dir>/Bin/...` and `<dir>/Data/GameData/Root.wad`.

1. `apt-get install dotnet-sdk-10.0`
2. Clone upstream Imcodec (github.com/Jooty/Imcodec, `main`; tested at d200e36) somewhere, then
   `git apply tools/typedump/imcodec-typedump-fixes.patch` in it. The fixes:
   - `_Init_thread_notify` is found by its bytes and made a no-op (r813697 calls a CRT-encoded pointer there, which
     is garbage when the CRT startup never ran, so every getter faulted).
   - The type-name registry getter's byte anchor is gone in r813697. When it is not found, the walker scans the
     client's data sections for the `std::map` {head, size} of types and takes the biggest.
3. `dotnet build -c Release -p:ImcodecUpstream=<that clone> tools/typedump/typedump.csproj`
   then `dotnet tools/typedump/bin/Release/net10.0/typedump.dll <dir> raw.json` (about 20 s).
4. `python3 -I tools/typedump/merge.py <previous dump> raw.json data/typedump/r<rev>_Wizard_1_<ver>.json`. It:
   - fills `SharedPointer<class X>` types the scan left empty from `class X` (5 in r813697),
   - keeps enum options the static dump can't fill (eRace and a few ints) from the previous dump,
   - renames a plain field that clashes with a `.m_full` one once the generator cuts `.m_full` off
     (`SandboxGameObjectInfo.m_templateID` -> `m_templateID_u32`; it writes by hash, so the game still matches).
5. Check: put the dump in `submodule/Imcodec/src/Imcodec.ObjectProperty/GeneratorInput/` (alone) and the game's
   `*Messages*.xml` from Root.wad in `Imcodec.MessageLayer/GeneratorInput/`, then
   `dotnet build src/Imlight.Director/Imlight.Director.csproj -c Release`. Decoding the game's Spells and ObjectData
   folders with the generated classes is a good second check (r813697: 0 failures in 18,625 spell files, 3 of
   108,810 ObjectData files).

The windows build of Unicorn (the emulator) is not in its NuGet package, so this runs in the cloud, not in Ahziael.
