# Changelog

Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) · [Semantic Versioning](https://semver.org/)

## [0.1.6] - 2026-11-14

Result of a full code review.

### Fixed

- When two or more sources failed at once, only one of them was tried again every minute; the others stayed off
  until WhyLag restarted. Now every failed source is tried again.
- Automatic detection kept watching while a capture was pending, instead of missing ten seconds after each spike.
- A program could be recorded under the name of another one that had the same process id earlier.
- Changed rule files in the rules folder apply without a restart; a rule that fails is written to the log.
- Values in a hand-edited `config.json` are brought into the allowed range when WhyLag starts.
- A broken HWiNFO shared-memory block no longer marks the temperature source as failed.
- `whylag-cli record` no longer stops the frame-time recording of the running app; `whylag-cli list` is much faster
  and shows the same cause as the app.
- Saving a report or the provider report shows a message when the file cannot be written.

## [0.1.5] - 2026-11-13

Second security review and new features, see [PROJECT_AUDIT.md](PROJECT_AUDIT.md).

### Added

- *Keep* an incident with the pin in its report: kept incidents are never deleted to make room for new ones.
- Sessions are compared with your usual for the same game: *worse than usual*, *as usual* or *better than usual*, with
  the usual fps, 1 % low and hitches per hour from up to ten earlier sessions.
- The overview shows the incidents of the last seven days as one bar per day, and how that compares with the week
  before.
- *This was the cause* / *This was not the cause* on every finding. What you rule out no longer counts as the cause
  in the list and in recurring patterns; bug reports contain the answers per rule.
- Your own markers on the patterns page ("graphics driver updated"): WhyLag shows the lag reports in the week before
  and after.
- The copy button offers the full report, a short summary for chats and forums, or the top of the report as an image;
  the export also saves that image as PNG.

### Changed

- Category colours can be told apart, also with a colour-vision deficiency, and have their own steps for the light and
  the dark theme. GPU and memory used to look the same.

### Security

- The elevated helper no longer reads the settings file before it starts.
- WhyLag checks that the helper's pipe is served by the helper it started.
- The update installer and PresentMon stay open for reading from the checksum until they run, so the checked file
  cannot be swapped in between.
- AI answers that contain links are removed, so text from program names or events can never become a link to open.
- The PresentMon download has a size limit; the Inno Setup version used for releases is pinned.

## [0.1.4] - 2026-11-01

Result of a full review, see [PROJECT_AUDIT.md](PROJECT_AUDIT.md).

### Added

- *Report for provider* on the patterns page: moments in which the line was slow while the router was fast.
- Keyboard: `Ctrl+1…4` for the pages, `Ctrl+F` for the search.
- Windows high-contrast colors are respected.

### Changed

- Network spikes start no automatic incident while nobody is at the PC and no game is in front.
- When the history is full, automatic incidents are removed first, so personal reports stay.
- Live values, sessions, findings and patterns are compact tables instead of separate cards.
- The incident list and patterns load in the background and about 13 times faster.
- Temperature tools are looked for every 30 seconds while none is running.
- Browsers, chat and video programs no longer count as gaming sessions.

### Fixed

- An error in the recording loop no longer ends the recording; automatic captures no longer run on that loop.
- Waking from sleep is no longer treated as a frozen PC.
- A ping target given as host name works when the network comes up after WhyLag started.
- Router discovery no longer freezes the window and retries.
- Failed sources are tried again every minute.
- Memory no longer grows while recording is paused with PresentMon running.
- A note is saved when the window closes; broken session files no longer break the page; day names update at midnight.
- Errors outside the window are logged, the log rotates, hotkey and update errors are translated.

### Security

- PresentMon only starts when its name and SHA-256 match the known release.
- The elevated permission command escapes the account name and is passed encoded.
- GitHub Actions pinned to commit hashes, tag names no longer expanded in scripts, Dependabot enabled.
- HTML exports have a content security policy; anonymisation covers profile paths.
- `whylag-cli explain` needs `--allow-remote` for a non-local Ollama; update downloads have a size limit.

## [0.1.3] - 2026-10-31

### Changed

- New interface: navigation on the left, own light and dark palette independent of the Windows accent color,
  consistent buttons, switches, type and spacing.
- *Incidents* shows list and report side by side and replaces the separate table and sidebar list; search, category
  filter and bulk delete moved into the list (sorting by column is gone).
- *Overview* shows today's numbers, the last minute and the latest incidents; the how-to only appears while there are
  no incidents yet.
- Report actions moved to the top of the report; settings are split into pages (*General*, *Detection*, *Frame
  times*, *Network*, *Local AI*, *Data*) with switches.
- An automatic incident no longer pulls the window away from the page you are looking at.
- The update notice is visible on every page.

### Added

- `--screenshots` renders all views to PNG files with sample data.

## [0.1.2] - 2026-10-29

### Added

- *Today* card on the live page: number of incidents today, how many were detected automatically, the most frequent
  cause and the count of the last 7 days.
- Setting to keep automatic detection for freezes and game hitches but not for ping spikes and packet loss
  (*Settings → General*). The *Today* card suggests it when a line produces many network incidents.
- Navigation on the left of the settings window.

### Changed

- The incident list in the sidebar is grouped by day (*Today*, *Yesterday*, date); the open page is highlighted.
- One *Export…* button saves the report as HTML or Markdown.
- The settings window checks the ping target and accepts only http(s) addresses for Ollama.

### Security

- The elevated driver latency helper no longer writes a marker file to `C:\ProgramData` (a folder standard users can
  write to, so the file could have been redirected by a link); the marker is kept under `HKLM`. The helper refuses
  pipe names it did not get from WhyLag and stops if another process already owns the pipe.
- Router discovery (UPnP) only talks to the device that answered, only on the local network, without following
  redirects and without DTD processing.
- Updates and release links are only accepted when they point into this repository on github.com over https;
  links are only opened when they are web links.
- Incident ids are checked before they are used as file names; the HTML export encodes every value.

## [0.1.1] - 2026-10-28

### Added

- Updates from inside the app: *Update now* downloads the installer of the new release, verifies its SHA-256
  against the release's checksum file, installs it silently and restarts WhyLag; optional automatic installation
  when WhyLag starts. The update check also runs every few hours while WhyLag stays open.
- Links to the source code on GitHub and to Buy Me a Coffee in the sidebar.

### Changed

- Starting WhyLag again (Start menu, desktop icon) brings up the window of the running instance instead of a
  message box.

## [0.1.0] - 2026-10-26

### Added

- Rolling in-memory recorder without admin rights: CPU, disk, memory, GPU, per-process load, network, ping,
  TCP retransmissions, Wi-Fi, DPC/interrupt time, foreground program, Windows events; fallbacks for damaged counters.
- Frame times via bundled Intel PresentMon 2.6.0 (checksum-verified) with one-click permission setup.
- Optional elevated helper that measures DPC/ISR duration per driver via the kernel trace.
- 16 rules with evidence, automatic detection of stalls, game hitches and network spikes.
- Recurring-pattern view with typical hour and weekday.
- Incident table (search, category filter, bulk delete), Windows events per incident, personal notes.
- "Ignore hitches in …" per program, tray option to pause automatic detection for an hour, keyboard shortcuts.
- Optional local AI explanations via Ollama with evidence validation; cloud models refused.
- WPF Fluent UI (light/dark) with live overview, timeline, KPI tiles; English, German, French and Spanish.
- Per-user installer, portable ZIP, winget manifest generator, CI and release workflows.
- Gaming session reports (average and 1 % low fps, hitches, most frequent causes) with a notification afterwards.
- Driver and Windows update comparison: incident rate in the week before and after each update.
- Rules for slow memory growth over hours, high CPU/GPU temperature (HWiNFO or LibreHardwareMonitor) and traffic
  from other devices in the household (router counters via UPnP, optional).
- Threshold rules as JSON (built-in plus your own in `%LOCALAPPDATA%\WhyLag\rules`), `whylag-cli rules`.
- Real recorded cases as regression fixtures, `whylag-cli export-fixture` and `check-fixture`.
- Overlay window, HTML export with timeline, notification modes (summary, all, hotkey only), theme setting,
  remembered window size, daily update check (can be turned off), bug report ZIP.
- Italian, Polish and Portuguese.
- Release workflow prepared for SignPath code signing; project page under `docs/`.

### Changed

- Automatic frame hitch detection only for the fullscreen program in front; browsers, chat and wallpaper apps never
  trigger it.
