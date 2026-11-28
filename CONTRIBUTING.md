# Contributing to Ahziael

Thanks for helping. This page covers how to find your way around, how to build, and how to send a change.
Ahziael is Niduus's fork of [Imlight](https://github.com/Revive101/Imlight). Niduus has the final say on what goes in
and when it is released.

Start with the **[folder map](FOLDER-MAP.md)**: it says what every folder is and which ones to leave alone.

## 1. The ground rules

1. **Work on this repository only: `Niduus/Imlight`.** Pull requests go into the `quality-assurance` branch here.
   Never open pull requests, issues or comments on `Revive101/Imlight` for this work. On GitHub, check that the page
   header says **Niduus / Imlight** before you click anything, and don't use GitHub's "Contribute" button on a fork,
   because it points to Revive101.
2. **Never change the game's own files.** Everything happens on the server side or in Ahziael. The newest normal game
   client must always work, with nothing patched.
3. **Don't commit game files.** Nothing copyrighted goes in the repository: no `.wad` files, no game program, no
   unpacked game data. Ahziael reads what it needs from each player's own copy of the game.
4. **No releases.** Only Niduus starts a release. Don't run the release workflows in `.github/workflows/`.
5. **Settings apply live.** Every setting in `Imlight.ini` and in Ahziael should take effect without a server restart.
   If yours can't, say why in the pull request.
6. **No Microsoft add-ons in the apps** (no Edge, WebView2 or Microsoft accounts). Ahziael draws its own window with
   Avalonia.
7. **Leave the top-level folders where they are.** Installed copies of Ahziael look for them by name when they update.
   Moving files inside a project is fine.
8. **Ask first** before changing `bots/`, `Hive mind/`, `overlay-addons/`, `.github/` or the README.

## 2. Building

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and git.

```bash
git clone --recurse-submodules https://github.com/Niduus/Imlight.git
cd Imlight
git checkout quality-assurance
```

The code generator in `submodule/Imcodec` needs two inputs that aren't in the repository (they come from the game):

1. The newest type dump from `data/typedump/` copied into `submodule/Imcodec/src/Imcodec.ObjectProperty/GeneratorInput/`.
2. The `*Messages*.xml` files from your own `Root.wad` (unpack it with `imcodec wad unpack`) copied into
   `submodule/Imcodec/src/Imcodec.MessageLayer/GeneratorInput/`.

```bash
# Ahziael and the launcher (only need the type dump)
dotnet build src/Imlight.Setup/Imlight.Setup.csproj -c Release
dotnet build src/Imlight.Launcher/Imlight.Launcher.csproj -c Release

# The server (needs both inputs)
dotnet build src/Imlight.Director/Imlight.Director.csproj -c Release
dotnet run --project src/Imlight.Director
```

Running the server by hand is covered in the [README](README.md#for-developers).

## 3. Making a change

1. Make a branch from the newest `quality-assurance`.
2. Keep one change (one fix or one feature) per pull request. Small pull requests get checked and merged faster.
3. Follow the code style of the file you are in. The full rules are in [`.agents/AGENT.md`](.agents/AGENT.md): K&R
   braces, file-scoped namespaces, `_camelCase` private fields, `var`, and `internal` by default.
4. Fix things broadly. If a bug in one quest, dungeon or world could happen in others, look for those too and fix them
   together.
5. **Add a release note.** Every change players will notice adds one small file in `docs/release-notes/`, written in
   plain words (see [how to write one](docs/release-notes/README.md)):

   ```
   New: Choose what each NPC gives
   A new tab lets you switch on or off every quest, item, recipe, card and spell an NPC hands out.
   Where: Game Rules > What NPCs Give
   Test: Open it, pick Private Stillson, turn off one of his quests, then talk to him in the game.
   ```

   Keep notes plain: no headings, bullet lists, bold or code names. A change nobody will notice (a code tidy-up, a
   guide) needs no note; ask Niduus to add its number to `tools/release-notes-skip.txt` instead.
6. **Changing quests or other records?** They live in `data/` ([guide](data/README.md)). After adding or changing
   quest files, rebuild the quest list with `python3 tools/data-index/make_quest_index.py`.
7. **Changing server code** (`src/Imlight.CoreLib`)? Running servers take most server fixes live, without a restart.
   The **Live patch check** on your pull request says whether yours can. If it says it needs a full update, that's
   fine, just mention it.

## 4. The checks on your pull request

| Check | What it does |
|---|---|
| Build check | Builds Ahziael and the launcher (and the server when the build has the game inputs). |
| Live patch check | For server code changes: can it go into a running server without a restart? |

All checks must pass before a pull request is merged.

## 5. Pull request description

Fill in the pull request template: what the change does and why, how you tested it, and whether an AI tool helped
write it (that's fine, just say so, and make sure you understand and checked what it wrote).

## 6. License

By sending a change you agree it is licensed under the same terms as the rest of the project, the
[GNU Affero General Public License v3.0](LICENSE).
