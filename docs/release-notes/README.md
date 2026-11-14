# Release notes

Every change that players or Niduus will notice adds one small file here, named after its branch or topic
(`side-bar-search-patch-notes.md`). The release writes these into the release's "What's new" list
(`tools/release-notes.ps1`), and Ahziael's What's New page and pop-up show them: each note as New, Fixed or Changed,
with a "Show me" button that opens the tab named on its Where line, and its How to test steps.

A change without a file here still shows up, as its pull request's title.

## How to write one

One note per change, notes split by an empty line:

```
New: Choose what each NPC gives
A new tab lets you switch on or off every quest, item, recipe, card and spell an NPC hands out.
Where: Game Rules > What NPCs Give
Test: Open it, pick Private Stillson, turn off one of his quests, then talk to him in the game.
```

- The first line starts with `New:`, `Fixed:` or `Changed:`, then a short title in plain words.
- The next lines say what it does or what was wrong, in plain words a player understands. No code names.
- `Where:` (optional) is the page and tab, as the side bar shows them, split by `>`. The last part must be a tab's name
  for the Show me button to appear.
- `Test:` (optional) is how to try it, in one or two sentences.
- Never write Niduus's real first name.

## Keep it simple

Niduus, 2026-11-14: the patch notes "seem to become more... cryptic". Each note becomes one line in the release and in
Ahziael's What's New, so write it to read well as that one line:

- One change per note. A big change is several notes, each with its own `New:`, `Fixed:` or `Changed:` title line.
- Two to four plain sentences: what was wrong or what is new, and what happens now. Leave out inner workings, file
  names, setting names and numbers a player doesn't need.
- No `#` headings, no `-` bullet lists, no numbered steps, no `**bold**`. Put each note's own `Where:` and `Test:`
  right under it, not in a block shared by several notes.
- `Test:` is one or two sentences a player can follow.
