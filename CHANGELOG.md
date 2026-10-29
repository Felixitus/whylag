# Changelog

Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) · [Semantic Versioning](https://semver.org/)

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
