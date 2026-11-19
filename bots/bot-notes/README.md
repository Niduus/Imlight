# Bot notes

A change to the bots (anything in this bots folder: Ultimus, the TCO7, the Quest Team, the Bot Players) adds its note
here instead of docs/release-notes. Bot changes then go out as a bot update by themselves as soon as they are merged (Publish bot update runs on every merge that touches bots/), with
these notes, and stay out of Ahziael's own patch notes (Niduus, 2026-11-19).

Write each note exactly like a release note (see docs/release-notes/README.md): a "Fixed:", "New:" or "Changed:" line,
two to four plain sentences, then Where: and Test: lines.

A bot fix that also changes the server's own code (src/Imlight.CoreLib, src/Imlight.Common) in the same pull request is
marked as needing that server change: servers built before it keep their bots until their next full update.
