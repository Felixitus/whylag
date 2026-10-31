# Architecture

| Project | Target | Role |
|---|---|---|
| `WhyLag.Core` | `net10.0` | Model, recorder, spike detector, rules, patterns, reports, translations. No Windows APIs; unit-tested. |
| `WhyLag.Collectors` | `net10.0-windows` | Metric and event sources, PresentMon child process, kernel latency tracer and elevated helper. |
| `WhyLag.AI` | `net10.0` | Ollama client, model advice, prompt, `ExplanationValidator`. Optional at runtime. |
| `WhyLag.App` | `net10.0-windows` | WPF tray app (Fluent theme), hotkey, live view, reports, patterns, settings. |
| `WhyLag.Cli` | `net10.0-windows` | Same pipeline without UI, plus diagnostics. |

## Flow

1. Every second the `Recorder` builds a `Tick`; each `IMetricSource` adds metrics. `IEventSource`s push events.
2. The `SpikeDetector` triggers on stalls, game hitches, ping spikes and packet loss – never on high load alone.
3. `Recorder.Capture` saves the last three minutes as an `Incident` and runs all `IRule`s. Findings are ranked by the
   rule's own confidence and get ids (`F1…`, evidence `E1…`).
4. `PatternAnalyzer` groups findings across incidents by rule and subject and finds dominant hours and weekdays.
   `SessionTracker` turns fullscreen play into session reports; `ChangeAnalyzer` compares incident rates before and
   after driver and Windows updates (`SystemChangeReader`); `LongTermHistory` keeps per-minute process memory.
5. Only on *Explain*, `IncidentExplainer` asks Ollama (JSON schema, `keep_alive: 0`) and validates the answer.

## Principles

- **Measurements decide, the model explains.** The AI may only rephrase and rank findings; every statement cites evidence.
- **Never fatal.** Failing sources are marked degraded; a throwing rule is skipped.
- **No admin rights by default.** Elevated parts are optional, explicit and isolated in a helper process.
- **Direct APIs before performance counters** – counters are localized by name and often damaged.
- **All user-facing text is translated** via `Loc.T("key")` and `src/WhyLag.Core/Localization/<lang>.json`.

## Extending

- **Threshold rule:** add it to `src/WhyLag.Core/Rules/builtin.rules.json` ([format](rules.md)).
- **Rule with logic:** implement `IRule` in `src/WhyLag.Core/Rules/`, add texts to every language file, register it
  in `RuleEngine.CodeRules`, test a positive and a calm-data case.
- **Real case:** `whylag-cli export-fixture <id> --expect rule.id` and put the file into
  `tests/WhyLag.Core.Tests/Fixtures`; `RealCaseTests` checks every fixture.
- **Collector:** implement `IMetricSource` (fast `Collect`, background work like `PingSource`), report problems via
  `Warnings`, register in `DefaultSources`.
- **Language:** copy `en.json` to `<code>.json` and translate; a test checks keys and placeholders.

## User interface

`Theme.cs` holds the light and dark palette and replaces the Fluent theme's brushes by key; shared styles (buttons,
navigation, switch, cards, type) are in `App.xaml`. New views use those styles instead of own colors and sizes.

`WhyLag.exe --screenshots <folder> [--theme Dark|Light] [--lang en] [--size 1320x860]` renders every view to PNG with
sample data in a temporary folder, also without a visible desktop. `docs/img` is made this way.

## Packaging

| Script | Result |
|---|---|
| `build/publish.ps1` | self-contained `WhyLag.exe`, `whylag-cli.exe`, bundled PresentMon, portable ZIP |
| `build/build-installer.ps1` | Inno Setup installer from `build/installer/whylag.iss` |
| `build/winget/New-WingetManifest.ps1 -Owner <owner>` | winget manifests for a release |

A tag `v<version>` runs `.github/workflows/release.yml`: build, tests, draft release with checksums, winget manifests.

Decisions: [adr/](adr/).
