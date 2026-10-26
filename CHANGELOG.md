# Changelog

Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) · [Semantic Versioning](https://semver.org/)

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
