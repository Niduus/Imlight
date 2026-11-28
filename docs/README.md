# Docs

| Folder | What it is |
|---|---|
| [`release-notes/`](release-notes) | One small note file for each change players or Niduus will notice. Releases and Ahziael's What's New page are built from them. Every pull request adds one ([how to write one](release-notes/README.md)). |
| [`docs/`](docs) | The developer book: how the server works inside (actors, message handling, login, zones, combat, the game's network formats). It is built with VitePress and published to GitHub Pages on every change to `quality-assurance`. |

The book's pages are in `docs/docs/modules/`. To read it on your own computer:

```bash
cd docs
npm ci
npm run docs:dev
```

Old release notes stay in `release-notes/` on purpose: the release step finds each note through the change that added
it, so please leave old notes where they are rather than moving or tidying them.
