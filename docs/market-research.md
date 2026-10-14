# Desktop Log Viewer Market: Feature Comparison

*Researched 2026-10-14 from the vendors' official sites, GitHub pages and docs. Facts not confirmed against a primary source are labelled **(inferred)** or **(unverified)**. Version numbers are as shown on the source pages on that date.*

---

## 1. Executive summary

The market splits into three groups:

1. **Fast raw-text viewers** (klogg/glogg, BareTail, SnakeTail). They are "grep + less with a GUI". They handle huge files and tail well, but they don't understand log *structure*.
2. **Structured log analysers** (LogViewPlus, lnav, Log4View, LogExpert, Tailviewer). They parse entries into fields (timestamp, level, logger, message), so they can offer column views, level and time filters, and merging by timestamp.
3. **Adjacent tools** (VS Code extensions, Logdy, Apache Chainsaw). These cover niches: editor highlighting, a browser UI for piped logs, and log4j network receivers.

**What makes the best viewers stand out:**

- **Out-of-core indexing of huge files.** klogg reads directly from disk, handles files over 10 GB and more than 2^31 lines, and searches with multiple threads and SIMD. Viewers that load whole files into memory (Tailviewer: under 1 GB; LogViewPlus: about 30 s for 500 MB, with chunking) lose here.
- **Automatic format detection with structured parsing.** lnav has 70+ built-in formats detected automatically. LogViewPlus detects formats and generates parsers. Structured parsing is what makes level, time and field filters possible.
- **Merging many files into one timeline by timestamp.** lnav, LogViewPlus, Tailviewer and Log4View do this. It is the most important feature for distributed or rolled logs.
- **Query power beyond regex.** lnav exposes logs as SQLite virtual tables. LogViewPlus has a T-SQL-like engine with reports and dashboards. This is the clearest differentiator of the top tier.
- **Visual navigation.** Histogram/timeline views (lnav; LogViewPlus charts; Log4View message chart) and overview/marker bars (klogg, LogViewPlus, LogExpert) for errors, bookmarks and matches.
- **Remote and compressed sources without extra steps.** lnav opens `user@host:/path` over SSH and reads gz/bz2/zip/tar. LogViewPlus supports SFTP/FTP/SCP. klogg opens zip/7z/tar/gz.
- **Persistent investigation state.** Sessions or workspaces with bookmarks, comments and filters (lnav sessions with `:comment`/`:tag`; LogViewPlus workspaces and bookmark comments).
- **Rules and automation.** LogViewPlus rules and email alerts; LogExpert triggers; lnav headless mode for scripting.

**Gap a new entrant could fill:** no single tool combines klogg-class raw speed, lnav-class structure, SQL, time merging and histograms, a modern cross-platform GUI, and remote/compressed sources. LogViewPlus comes closest but is Windows-only and paid. lnav comes close but is terminal-only.

---

## 2. Per-tool profiles

### LogViewPlus (Clearcove), commercial, Windows
- **Version:** 3.2.9 is the latest in the official release notes. ComponentSource lists 3.2.x as of May 2025. **Platform:** Windows only (11/10/8.1).
- **Price:** ComponentSource discounted prices are about $44 Personal and $93 Corporate, each including one year of updates, plus a 50-licence pack. Prices at the vendor's own store may differ.
- **Formats:** automatic detection with parser generation and a Parser Configuration Wizard; custom parsers; JSON parsing (including big integers); syslog and UDP listeners; Windows Event Log; ETW; database sources (SQL Server, Oracle, MySQL, PostgreSQL, SQLite); IIS and FIX dashboards. Multi-line custom parsers are discussed in the vendor forum. Named log4j/log4net support comes from site keywords.
- **Analysis:** chained filters (AND/OR/exclude) saved as templates; regex/wildcard search with highlighting; syntax highlighting; **pretty-prints JSON/XML**; elapsed-time column; **merging by drag-and-drop, with entries sorted by date**; T-SQL-based **SQL engine, reports and dashboards** with charts; an AI chat prompt in the SQL editor (3.1.6).
- **Workflow:** bookmarks with comments; navigation bar showing errors, warnings and bookmarks; rules (auto-filter, auto-bookmark); SMTP alerts; directory monitors; workspaces; export to CSV/HTML; 7-Zip support as an add-on (3.1.18); default encoding is UTF-8 (3.1.22).
- **Remote:** SFTP, FTP, FTPS, SCP, SMB/mapped drives; can open an SSH session for a monitored SFTP file.
- **Large files:** about 30 s to open 500 MB (vendor figure); large files are split into chunks you can choose or merge.
- **Sources:** https://www.logviewplus.com/ · https://www.logviewplus.com/log-viewer.html · https://www.logviewplus.com/releasenotes.html · https://www.logviewplus.com/docs/merge_log_files.html · https://www.componentsource.com/product/logviewplus/prices

### klogg (fork of glogg), open source (GPLv3+), Windows/macOS/Linux
- **Version:** the latest tagged release is 22.06 (June 2022). Continuous pre-release builds are published.
- **Large files:** reads directly from disk without loading files into memory; handles files over 10 GB and more than 2,147,483,647 lines; multi-threaded, SIMD-accelerated search, 2–4x faster than glogg.
- **Search:** PCRE regex, combinable with AND/OR/NOT; predefined filter patterns; search limited to part of a file; results in a separate pane; a match overview bar showing where hits and marks are.
- **Other:** follow mode (`f` / `-f`); highlighter **sets** (switchable, importable/exportable); marks (`m`, jump with `[` `]`); automatic encoding detection (uchardet: UTF-8/16, cp1251…); opens **zip/7z/tar archives and gz**; a Scratchpad that decodes base64 and makes XML/JSON human-readable; dark mode.
- **Not supported:** structured parsing or columns, merging files, time filters. Context lines and search-result export are **not documented**.
- **Sources:** https://github.com/variar/klogg · https://github.com/variar/klogg/blob/master/DOCUMENTATION.md · https://github.com/variar/klogg/releases

### glogg, open source, cross-platform, **dormant**
- **Version:** last release 1.1.4 (May 2017). The site doesn't state a licence; GPL is **(inferred)**, since klogg as a fork is GPLv3+.
- "grep + less" GUI: regex search into a filtered pane, auto-reload/follow mode, UTF-8 and ISO-8859-1, multiple files in tabs. klogg supersedes it in every respect.
- **Source:** https://glogg.bonnefon.org/

### lnav (Log File Navigator), open source (BSD-2), terminal UI on Linux/macOS/Windows/FreeBSD
- **Version:** v0.14.0 is the latest stable release on GitHub (v0.14.1-rc1 is a pre-release). The docs' "latest" pages are labelled v0.15.0.
- **Formats:** **70+ built-in formats with automatic detection** (syslog, Apache access logs, logfmt, journald, JSON-lines, and more). Custom formats are defined in JSON via regex or JSON-lines field mapping.
- **Merging and time:** **merges all files into one view ordered by timestamp**, with a colour bar showing each line's source file; tails files, follows renames and picks up new files in directories. **Histogram view** (counts over time with errors and warnings highlighted). **Timeline view** of operation spans grouped by opid/thread/tag.
- **Filtering:** regex and SQLite-expression in/out filters; `:filter-context` (context lines); `:set-min-log-level`; `:hide-lines-before/after` (time range); a filter panel (v0.14) with live preview; jump to the next or previous error.
- **SQL:** logs exposed as **SQLite virtual tables**; headless `-n -c` mode with CSV output.
- **Other:** Shift+P pretty-prints JSON/XML; gz/bz2 decompressed on the fly; zip/tgz via libarchive; **remote files via `user@host:/path` over SSH**; marks, `:tag`, `:comment` (Markdown) saved in sessions; themes; keymaps; sticky headers; log-to-source mapping (log2src).
- **Limits:** terminal UI only, with no mouse-first GUI and no native columns grid; it is aimed at power users.
- **Sources:** https://lnav.org/features · https://github.com/tstack/lnav · https://docs.lnav.org/en/latest/usage.html · https://github.com/tstack/lnav/releases

### BareTail / BareTailPro (Bare Metal Software), freeware/commercial, Win32
- **Versions:** BareTail 3.50a and BareTailPro 2.50a, both dated **2006-11-02**. Neither has been updated since.
- **BareTail:** free; a $25 registration only removes the splash screen. Real-time follow tail; files of any size, including over 2 GB, with instant seek; line highlighting by string with custom colours; tabs; Unicode/UTF-8/ANSI; network files; no installer needed.
- **BareTailPro** ($35; the demo quits after 5 minutes): adds regex and incremental search, and a "filter tail" mode that includes or excludes matches in real time. Results appear in a sortable table with capture-group columns; saved search patterns; export to file or clipboard.
- **Not supported:** parsing, merging, bookmarks, compressed files.
- **Sources:** https://www.baremetalsoft.com/baretail/ · https://www.baremetalsoft.com/baretailpro/

### LogExpert, open source, Windows (.NET 10 desktop runtime)
- **Version:** v1.50.0 on GitHub releases (October; the page doesn't show the year). The licence isn't shown on the pages fetched; MIT is **(unverified)**.
- Tail mode; MDI tabs; **columnizers** (plugins that split lines into columns, including log4j XML and experimental Serilog compact) with experimental automatic columnizer selection; filter view and filter-to-tab; regex search; search-based highlighting; bookmarks; **triggers** that bookmark lines or run plugins when a search matches; combined rollover view; an SFTP plugin; plugin API; portable settings.
- New in 1.50: hide line, log overview marker bar, command-line jump to a line.
- **Sources:** https://github.com/zarunbal/LogExpert · https://github.com/zarunbal/LogExpert/releases

### Tailviewer, open source (MIT), Windows (.NET 4.7.1+)
- **Version:** v1.0.0-rc1 on GitHub releases. The repo calls itself "active", but it has many open issues. Its maintenance status is **(unverified)**.
- **Merges multiple files by timestamp**; substring/wildcard/regex filters; **log level filter**; **timestamp range filter**; highlighting by level; bookmarks; multi-line entries; plugin system for custom formats.
- Live filtering is aimed at files under 1 GB.
- **Sources:** https://github.com/Kittyfisto/Tailviewer · https://github.com/Kittyfisto/Tailviewer/releases

### SnakeTail, freeware, Windows
- **Version:** 1.9.6 (2019), according to Snapfiles. Its official project page wasn't reachable during this research; it is generally listed as open source on GitHub/SourceForge **(unverified)**.
- Tails multiple files and **Windows Event Logs** in tabs; keyword highlighting with custom colours; wildcard file paths; adjustable check interval; bookmarks; launches external tools.
- **Not supported:** parsing or merging.
- **Source:** https://www.snapfiles.com/get/snaketail.html

### Log4View (PROSA GmbH), commercial freemium, Windows
- **Version:** 2.1.1.51 on winget. Proprietary; a perpetual licence costs about $59 to $4,950 depending on tier, and a limited free version exists (per AlternativeTo).
- Built for log4net, NLog, log4j and log4cxx. Receives logs over **UDP and TCP**, from file appenders (including rolled files) and from **databases** (SQL Server, Oracle, DB2, MySQL). Reads XML and pattern layouts. **Merges sources into one view**. Logger tree with per-logger level filters; colours by logger, level or text; wildcard/regex search; message detail pane for stack traces; **message charts** (state, line, histogram).
- **Sources:** https://www.log4view.com/download-en · https://wingetly.io/apps/prosa/log4-view · https://alternativeto.net/software/log4view/about/

### Adjacent tools (brief)
- **Logdy** (Apache-2.0, v0.17.0, June 2025): a single Go binary with an **embedded web UI**. It takes input from files, stdin, sockets or a REST API, and supports real-time filtering and custom TypeScript parsers and columns. Runs on Linux, macOS and Windows. It shows demand for browser-style UIs on top of CLI pipelines. https://github.com/logdyhq/logdy-core
- **VS Code "Log File Highlighter"** (emilast): token highlighting (levels, timestamps, GUIDs, IPs, stack traces), custom regex highlights, elapsed time between selected lines, tail-follow. It doesn't filter or parse; it is popular because developers already work in the editor. https://github.com/emilast/vscode-logfile-highlighter
- **Apache Chainsaw** (v2, Java): companion to Log4j. Reads local and **SSH** text logs and log4j XMLLayout; receives TCP/UDP events and JUL; reads from databases. https://logging.apache.org/chainsaw

---

## 3. Feature matrix

Legend: ✓ = supported (verified) · ◐ = partial or limited · ✗ = not supported or not documented · ? = unknown. "(i)" means inferred.

| Feature | LogViewPlus | klogg | glogg | lnav | BareTail(Pro) | LogExpert | Tailviewer | SnakeTail | Log4View |
|---|---|---|---|---|---|---|---|---|---|
| Very large files (>2 GB, disk-indexed) | ◐ chunking | ✓ >10 GB | ◐ | ✓ | ✓ >2 GB | ? | ◐ <1 GB | ? | ◐ |
| Tail / follow | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ live receivers |
| Follows rollover / new files in directory | ✓ | ✗ | ✗ | ✓ | ✗ | ◐ rollover view | ? | ◐ wildcards | ✓ rolled files |
| Automatic format detection | ✓ | ✗ | ✗ | ✓ 70+ | ✗ | ◐ experimental | ◐ | ✗ | ◐ |
| log4j/log4net/NLog parsers | ✓ | ✗ | ✗ | ◐ custom | ✗ | ◐ log4j XML | ◐ plugin | ✗ | ✓ |
| JSON / JSON-lines logs | ✓ | ✗ | ✗ | ✓ | ✗ | ◐ Serilog (exp.) | ? | ✗ | ? |
| Syslog / Apache / W3C(IIS) | ✓ syslog, IIS | ✗ | ✗ | ✓ syslog, Apache, W3C(i) | ✗ | ◐ via columnizer | ? | ✗ | ✗ |
| Custom parser definition | ✓ wizard | ✗ | ✗ | ✓ JSON | ✗ | ✓ plugins | ✓ plugins | ✗ | ✓ patterns |
| Column / grid view | ✓ | ✗ | ✗ | ◐ TUI fields | ◐ Pro results | ✓ | ◐ | ✗ | ✓ |
| Multi-line entries (stack traces) | ✓ | ✗ | ✗ | ✓ | ✗ | ? | ✓ | ✗ | ✓ |
| Regex search | ✓ | ✓ PCRE | ✓ | ✓ | ◐ Pro only | ✓ | ✓ | ✗ | ✓ |
| Boolean AND/OR/NOT search | ✓ | ✓ | ✗ | ✓ SQL | ✗ | ? | ? | ✗ | ? |
| Match overview / marker bar | ✓ | ✓ | ✗ | ◐ | ✗ | ✓ (1.50) | ? | ✗ | ? |
| Include/exclude filters | ✓ | ◐ search pane | ◐ | ✓ | ◐ Pro | ✓ | ✓ | ✗ | ✓ |
| Level filter | ✓ | ✗ | ✗ | ✓ | ✗ | ◐ | ✓ | ✗ | ✓ per logger |
| Time-range filter | ✓ (i) | ✗ | ✗ | ✓ | ✗ | ? | ✓ | ✗ | ? |
| Context lines around hits | ? | ✗ | ✗ | ✓ | ✗ | ? | ? | ✗ | ? |
| Highlighting rules | ✓ | ✓ sets | ✓ | ✓ | ✓ | ✓ | ◐ level | ✓ | ✓ |
| Bookmarks | ✓ | ✓ marks | ✗ | ✓ | ✗ | ✓ | ✓ | ✓ | ? |
| Notes / comments on lines | ✓ | ◐ scratchpad | ✗ | ✓ :comment/:tag | ✗ | ◐ bookmark comment(i) | ✗ | ✗ | ✗ |
| Merge files by timestamp | ✓ | ✗ | ✗ | ✓ | ✗ | ✗ | ✓ | ✗ | ✓ |
| Histogram / timeline / charts | ✓ dashboards | ✗ | ✗ | ✓ | ✗ | ✗ | ✗ | ✗ | ✓ |
| Pretty-print JSON/XML | ✓ | ◐ scratchpad | ✗ | ✓ | ✗ | ✗ | ✗ | ✗ | ◐ details pane |
| SQL / query language | ✓ T-SQL-like | ✗ | ✗ | ✓ SQLite | ✗ | ✗ | ✗ | ✗ | ✗ |
| Compressed / archive files | ◐ 7-Zip add-on | ✓ zip/7z/tar/gz | ✗ | ✓ gz/bz2/zip/tar | ✗ | ✗ | ✗ | ✗ | ✗ |
| Remote (SFTP/SSH) | ✓ SFTP/FTP/SCP | ✗ | ✗ | ✓ SSH | ◐ SMB shares | ◐ SFTP plugin | ✗ | ✗ | ◐ network receivers |
| Network/DB/event-log sources | ✓ UDP, DB, EventLog, ETW | ✗ | ✗ | ◐ journald/stdin | ✗ | ✗ | ✗ | ✓ EventLog | ✓ TCP/UDP/DB |
| Encoding detection / options | ✓ UTF-8 default | ✓ auto (uchardet) | ◐ | ✓ (i) | ◐ | ✓ | ? | ? | ? |
| Export (CSV/HTML/clipboard) | ✓ | ✗ | ✗ | ✓ CSV/JSON | ◐ Pro | ◐ (i) | ? | ✗ | ? |
| Sessions / workspaces | ✓ | ◐ favourites | ✗ | ✓ | ✗ | ✓ (i) | ? | ✗ | ? |
| Alerts / triggers / rules | ✓ rules + email | ✗ | ✗ | ◐ scripting | ✗ | ✓ triggers | ✗ | ✓ keyword alerts | ? |
| Platforms | Win | Win/Mac/Linux | Win/Mac/Linux | Linux/Mac/Win/BSD (TUI) | Win | Win | Win | Win | Win |
| Licence / price | Paid (~$44–93) | GPLv3 free | free (GPL i) | BSD-2 free | Free / $25; Pro $35 | OSS (MIT?) free | MIT free | Freeware | Freemium, $59+ |
| Actively maintained (2025–26) | ✓ | ◐ continuous builds only | ✗ (2017) | ✓ | ✗ (2006) | ✓ | ◐ | ✗ (2019) | ✓ |

---

## 4. Recommended feature set for UltraLogViewer

Ranked by user value and market evidence. **Must** = parity with the best tools (users will leave without it). **Should** = what makes the top tier stand out. **Could** = extras that set us apart.

### Must have
1. **Disk-backed line indexing of huge files** (no full load into RAM, 10 GB+, more than 2^31 lines), with background indexing and progress, and scrolling while indexing runs (klogg sets the bar).
2. **Tail/follow** that handles truncation, rotation and rename, and auto-scrolls only when the user is at the bottom.
3. **Fast regex search** (multi-threaded, PCRE-class) with a hit count, next/previous navigation, a results pane linked to the source line, and case/whole-word/regex toggles.
4. **Include/exclude filters**, combinable and saved as presets.
5. **Highlighting rules** (text/regex → foreground/background), grouped into switchable, importable/exportable sets.
6. **Automatic format detection and structured parsing** for the common formats: log4j/log4net/NLog patterns, JSON-lines (Serilog/structured), syslog (RFC3164/5424), Apache/Nginx combined, W3C/IIS. Plus a user-defined regex/pattern parser.
7. **Multi-line entry grouping** (stack traces stay attached to their entry).
8. **Column/grid view** for parsed fields (show/hide/reorder columns, level colouring).
9. **Level filter and time-range filter** (powered by the parser).
10. **Bookmarks** with navigation and an overview/marker bar (bookmarks, errors, search hits).
11. **Encoding detection** (UTF-8/16 with or without BOM, legacy code pages) with a manual override.
12. **Copy/export** of selected or filtered lines (plain text, CSV).

### Should have
13. **Merging multiple files into one timeline by timestamp**, with a per-source colour stripe (lnav, LogViewPlus, Tailviewer, Log4View all have this).
14. **Histogram/timeline strip** of entry counts over time, stacked by level, with click to jump and brush to set a time filter (lnav histogram, LogViewPlus charts).
15. **Context lines** around filter hits (N before/after, adjustable).
16. **Pretty-print JSON/XML** for the selected entry in a detail pane.
17. **Compressed and archive files**: gz, bz2, zip, 7z, tar opened in place.
18. **Notes/comments on lines**, plus **sessions/workspaces** that persist files, filters, bookmarks and notes.
19. **Remote sources** over SSH/SFTP (open `user@host:/path`), plus directory monitoring for new files.
20. **Boolean search** (AND/OR/NOT) and field-scoped queries (`level:ERROR AND logger:*Db*`).
21. **Cross-platform** desktop (Windows, macOS, Linux). Only klogg (unstructured) and lnav (TUI) cover all three today, so a GUI covering all three fills a real gap.

### Could have
22. **SQL query mode** over parsed entries, with results grid and charts (lnav's SQLite tables, LogViewPlus's T-SQL engine).
23. **Rules/triggers**: auto-bookmark, auto-highlight or notify when a pattern matches during tail (LogExpert triggers, LogViewPlus rules).
24. **Live network sources**: syslog UDP/TCP listener, log4j/Serilog TCP receivers, stdin/pipe, Windows Event Log, journald.
25. **Elapsed time and delta columns**, and the duration between two selected lines (LogViewPlus, the VS Code highlighter).
26. **Compare/diff two logs**, and log-to-source jumps (lnav log2src).
27. **Headless/CLI mode** for scripted filtering and export (lnav `-n -c`).
28. **Plugin API** for custom parsers and sources (LogExpert, Tailviewer, LogViewPlus).
29. **AI assist** (explain an error cluster, draft a regex or SQL). LogViewPlus 3.1.6 already ships an AI prompt in its SQL editor.

---

*Gaps in this research: SnakeTail's and Log4View's official pages were not fetched directly; their facts come from Snapfiles, winget and AlternativeTo listings. LogExpert's licence and LogViewPlus's named parsers (log4j/NLog/Apache/W3C) were not confirmed on primary pages. LogViewPlus prices are ComponentSource's discounted figures.*
