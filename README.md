# UltraLogViewer

A fast, cross-platform desktop log viewer (Windows, macOS, Linux) that combines the raw speed of
klogg with the structure of lnav and LogViewPlus: huge files, live tail, automatic format
detection, column view, filters, highlighting, bookmarks, merged timelines and a time histogram.

The feature set comes from a comparison of the leading viewers (LogViewPlus, klogg/glogg, lnav,
BareTail, LogExpert, Tailviewer, SnakeTail, Log4View): see [docs/market-research.md](docs/market-research.md).

## Features

**Big files and live logs**
- Opens files far larger than RAM: only a byte-offset index is kept in memory. Indexing runs in a
  worker thread at roughly 100 MB/s, and rows show up while it is still running.
- Live tail: new lines appear as they are written; truncation and log rotation are detected.
  Follow mode (`Ctrl+T`, `f` or `End`) keeps the newest line in view.
- Gzip files open directly. Encodings are detected (UTF-8, UTF-8 BOM, UTF-16 LE/BE, Windows-1252)
  and can be overridden (click the encoding in the status bar).

**Understands log structure**
- Auto-detected formats: timestamped text (log4j, logback, log4net, NLog, Serilog, Spring Boot,
  Python, Monolog…), JSON lines (Serilog, pino, bunyan, ECS, Logstash), syslog RFC 3164 and
  RFC 5424, Apache/Nginx access logs, W3C/IIS, Android logcat, plain text.
- Custom formats: a regex with named groups (`timestamp`, `level`, `thread`, `logger`, `message`,
  or anything else), with a live tester.
- Multi-line entries: stack traces stay attached to their entry (`+4` in the line gutter).
- Column view with resizable columns (right-click the header to show/hide columns) and per-level colouring.

**Finding things**
- Search: plain text, whole word, match case or regex, with hit count, next/previous (`Enter`,
  `F3`, `n`), highlights in the rows and marks on the overview ruler.
- Quick filter language: `timeout OR refused -retry logger:Db level:error "exact phrase" /regex/`.
- Include/exclude filter rules (any column or a specific one), saved as presets.
- Level filter with counts, time-range filter, "bookmarks only", and context lines around matches.
- Highlight rules (text or whole row, any colour).
- Overview ruler next to the scrollbar showing warnings, errors, search hits and bookmarks.
- Timeline histogram stacked by severity: click to jump, drag to filter to a time range.
- Jump to next/previous error (`e`/`E`) or warning (`w`/`W`), go to line (`Ctrl+G`).

**Investigating**
- Merge several files into one timeline ordered by timestamp (drop them together, or
  `Ctrl+Shift+O`), with a Source column.
- Bookmarks with notes, saved per file (`Ctrl+B`, `b`, double-click; `F2`/`Shift+F2` to jump).
- Details pane with all parsed fields and pretty-printed JSON/XML.
- Select a range to see the time between the first and last row.
- Copy entries (`Ctrl+C`), export the current view as text, CSV or JSON lines (`Ctrl+E`).
- Light and dark themes, recent files, drag and drop, command-line arguments.

Press `F1` in the app for all keyboard shortcuts.

## Getting started

```bash
npm install
npm start                                   # build and launch
npm run gen:sample -- sample.log 50         # write a 50 MB sample log
node scripts/gen-sample.mjs live.log 0 --live   # keep appending to a log to try tail mode
npm run dist                                # installers via electron-builder (dmg, nsis, AppImage, deb)
```

## Native desktop app (Avalonia UI / .NET 8)

The [`dotnet/`](dotnet) folder holds a second, native build of the same viewer: the engine ported
to C# (`UltraLogViewer.Core`) and an Avalonia UI (`UltraLogViewer.Desktop`) with the same features
and keyboard shortcuts as the Electron app. It needs the [.NET 8 SDK](https://dotnet.microsoft.com/download).

```bash
cd dotnet
dotnet run --project src/UltraLogViewer.Desktop -- path/to/app.log   # build and launch
dotnet test                                                          # engine + headless UI tests
dotnet publish src/UltraLogViewer.Desktop -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
#   other runtimes: linux-x64, osx-arm64, osx-x64
```

```
dotnet/src/UltraLogViewer.Core/      engine: FileSource (index, tail, gzip/UTF-16), formats, LogDocument
                                     (merge, filters, search, histogram, export), quick-filter language
dotnet/src/UltraLogViewer.Desktop/   Avalonia app: MainWindow, Sidebar, custom-drawn virtual LogGrid,
                                     HistogramView, dialogs, settings (JSON in the user's app-data folder)
dotnet/tests/                        xUnit tests; the UI tests run the real window headless (Skia) and
                                     save screenshots (set ULV_SCREENSHOTS to choose the folder)
```

The grid draws only the rows on screen and fetches them from the engine in the background, so it
stays smooth with millions of entries and shows rows while a large file is still being indexed.
On the same 300 MB / 2.5 M entry file: full index ~4 s, rule filter ~1.3 s, regex search ~1.1 s.

### Windows installer

[`installer/UltraLogViewer.iss`](installer/UltraLogViewer.iss) is an [Inno Setup 6](https://jrsoftware.org/isinfo.php)
script that packages the self-contained win-x64 build into `UltraLogViewer-Setup-<version>-x64.exe`
(about 33 MB). It installs per user without admin rights (or for all users, chosen on the first
page), adds a Start menu shortcut, an optional desktop shortcut, "Open with UltraLogViewer" for
.log/.txt/.gz files and on the right-click menu, an optional default .log association, and an
uninstaller.

On Windows, double-click **`build-installer.cmd`** in the repo root. It installs Inno Setup with
winget if it is missing, publishes the app, builds the installer and opens `release/installer/`.
It needs the [.NET 8 SDK](https://dotnet.microsoft.com/download). From a terminal:

```powershell
.\build-installer.cmd                               # Avalonia app -> release/installer/
.\build-installer.cmd -App Electron                 # same installer for the Electron app
.\build-installer.cmd -Version 1.0.0                # override the version
```

The [Windows installer workflow](.github/workflows/installer.yml) builds it on every change to the
app or installer and uploads the setup .exe as an artifact; pushing a `v*` tag also attaches it to
a GitHub release.

## Development

| Command | What it does |
|---|---|
| `npm run build` / `npm run watch` | Bundle main, preload, engine worker and renderer with esbuild |
| `npm run typecheck` | TypeScript, strict |
| `npm test` | Unit tests for the engine (vitest) |
| `npm run test:e2e` | Launches the real app with Playwright and drives the UI; screenshots in `e2e/out/` (use `xvfb-run` on headless Linux) |

### Architecture

```
src/core/       engine, no Electron dependency (unit-tested)
  fileSource.ts   line/entry index of one file, tail refresh, gzip/UTF-16 handling
  formats.ts      format definitions and auto-detection
  document.ts     a tab: one or more sources, merged timeline, filters, search, histogram, export
  query.ts        search patterns and the quick-filter language
src/main/       Electron main process; engine.ts runs in a worker thread (engineWorker.ts)
src/preload/    the small API the renderer may use
src/renderer/   UI: virtual grid, sidebar, timeline, details pane (plain TypeScript, no framework)
```

The renderer never holds the file: it asks the engine for the rows on screen. Filtering and search
run per source in file order (sequential reads) and produce bitmaps that are then walked in view
order, so merged timelines stay fast too.

### Performance (300 MB, 2.4 M entries, one core of a cloud VM)

| Operation | Time |
|---|---|
| First rows on screen | ~1.6 s |
| Full index | ~3–5 s |
| Text filter over all entries | ~1.1 s |
| Regex search | ~0.7 s |
| Fetching a screen of rows | ~2 ms |

## Roadmap

Ideas from the research that are not in this version yet: SQL queries over parsed entries, remote
files over SSH/SFTP, zip/bz2/7z archives, network receivers (syslog, log4j TCP), rules/alerts on
tail, saved workspaces, and multi-threaded search.
