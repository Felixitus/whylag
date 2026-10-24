# Changelog

Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) · [Semantic Versioning](https://semver.org/)

## [0.1.0] - unreleased

### Added

- Rolling in-memory recorder without admin rights: CPU, disk, memory, GPU, per-process load, network, ping,
  TCP retransmissions, Wi-Fi, DPC/interrupt time, foreground program, Windows events; fallbacks for damaged counters.
- Frame times via bundled Intel PresentMon 2.6.0 (checksum-verified) with one-click permission setup.
- Optional elevated helper that measures DPC/ISR duration per driver via the kernel trace.
- 16 rules with evidence, automatic detection of stalls, game hitches and network spikes.
- Recurring-pattern view with typical hour and weekday.
- Optional local AI explanations via Ollama with evidence validation; cloud models refused.
- WPF Fluent UI (light/dark) with live overview, timeline, KPI tiles; English and German.
- Per-user installer, portable ZIP, winget manifest generator, CI and release workflows.
