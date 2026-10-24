# ADR 0001: .NET 10 with WPF for v0.1

- Status: accepted
- Date: 2026-10-23

## Context

WhyLag needs deep Windows access (PDH, event log, IOCTLs, global hotkeys, tray) and a desktop UI. It should be easy
for contributors to build and must run without admin rights.

## Decision

- C# on .NET 10 (LTS). Windows-specific code is isolated in `WhyLag.Collectors` and `WhyLag.App`; the core and AI
  projects target plain `net10.0` and are tested on any OS.
- WPF for the UI. No local web server, so no open port and no HTTP attack surface. The UI uses WPF's built-in
  Fluent theme (`ThemeMode="System"`, still marked experimental in .NET 10) for a Windows 11 look with light/dark
  mode, instead of a third-party UI library.
- Incidents as JSON files instead of a database: transparent, easy to share and delete. SQLite can be added later
  when history views need queries.

## Consequences

- Windows only for now; that matches the problem domain.
- A web-based UI (e.g. Blazor Hybrid) remains possible later without touching core logic.
