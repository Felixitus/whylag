# WhyLag project audit

Audit of version 0.1.3, 2026-11-01. Changes from this audit are listed in [CHANGELOG.md](CHANGELOG.md) under *0.1.4*.
Everything marked *verified* was run; everything else says what it is based on.

## 1. Executive summary

WhyLag was in better shape than a 0.1 project usually is: clear layering, no secrets in the repository, no vulnerable
packages, sensible privacy defaults. The serious problems were not in the obvious places. The worst one was
reliability, not security: **any exception in the one-second loop ended the recording for good while the app kept
showing "Recording"**. For a tool whose whole value is having recorded the moment, that was the most important fix.

Security had one finding of real weight (an elevated process writing into a folder other users can prepare) and it was
fixed in 0.1.2. This audit added fixes for the supply chain and for code started from user-writable folders. The main
remaining risk is that releases are **not code-signed**, so updates are protected against corruption, not against a
compromised GitHub account. Signing cannot be solved in code alone (section 10).

Real data from the development PC shaped several decisions: 69 of 81 incidents were automatic, many at night with
nobody at the PC, and they were pushing personal reports out of the history. Browsers and Discord were being counted as
gaming sessions.

## 2. Architecture

| Layer | Role | Assessment |
|---|---|---|
| `WhyLag.Core` | model, recorder, rules, reports, translations | Good: no Windows APIs, well tested |
| `WhyLag.Collectors` | metric and event sources, elevated helper | Good separation; previously no tests at all |
| `WhyLag.AI` | Ollama client, validator | Good: measurements decide, the model only explains |
| `WhyLag.App` | WPF tray app | `MainWindow.xaml.cs` is large (about 700 lines) and has no view models |
| `WhyLag.Cli` | diagnostics | Fine |

There is no database, no server, no login and no web surface. Storage is one JSON file per incident in
`%LOCALAPPDATA%\WhyLag`, which is simple and inspectable, and fine for hundreds of files.

**Decision taken:** no SQLite index and no MVVM rewrite. Both would be large changes without a user-visible gain at the
current size; a lightweight read path (section 6) solved the actual problem.

## 3. Security audit

Not applicable, because the surface does not exist: authentication, sessions, passwords, rate limiting, SQL injection,
CSRF, CORS, HTTP headers, file uploads, containers. WhyLag opens no listening port except the elevated helper's named
pipe. A search found no hardcoded credentials or keys in the repository.

`dotnet list package --vulnerable --include-transitive`: no known vulnerable packages (*verified*).

| # | Vulnerability | Severity | Location | Impact | Solution | Fixed |
|---|---|---|---|---|---|---|
| S1 | Elevated helper wrote and deleted a marker file in `C:\ProgramData\WhyLag`, a location standard users can prepare | **High** | `LatencyHelper.cs` | On a shared PC another user could redirect the file with a link and have it overwritten or deleted as administrator | Marker moved to `HKLM\SOFTWARE\WhyLag` | Yes, 0.1.2 (*verified* on the real PC) |
| S2 | Any file named `PresentMon-*-x64.exe` in the user's tools folder was started without a check | **Medium** | `PresentMonLocator.cs` | Code dropped there runs on every start, elevated if WhyLag was started as administrator | Exact name and SHA-256 are checked before every start | Yes (*tested*) |
| S3 | Elevated PowerShell command contained the account name without escaping | **Medium** | `PresentMonLocator.cs`, `SettingsWindow.xaml.cs` | A name with a quote breaks out of the string; the command runs elevated | Quotes doubled, command passed as `-EncodedCommand` | Yes (*tested*) |
| S4 | CI actions referenced by movable tags; tag name expanded inside scripts in the release job, which has `contents: write` | **Medium** | `.github/workflows/*.yml` | A compromised action or a crafted tag could alter release files | Actions pinned to commit hashes, tag passed through the environment, Dependabot added | Yes (workflow run pending) |
| S5 | Router discovery followed any URL from any device on the network, with DTD processing, redirects and no size limit | **Medium** | `RouterSource.cs` | A device on the LAN could make WhyLag request other hosts (SSRF) or send entity bombs | Only the answering local device, no redirects, no DTD, 256 KB limit | Yes, 0.1.2 (*tested* in this audit) |
| S6 | Update links from the API were not checked; `OpenUrl` shell-executed any string | **Medium** | `Updates.cs`, `App.xaml.cs` | A manipulated response could open or download something else | Only https links into this repository; only http(s) opened | Yes, 0.1.2 (*tested*) |
| S7 | Helper accepted arbitrary pipe names and spun in a loop when the name was taken | **Low** | `LatencyHelper.cs` | Fake data for WhyLag, wasted CPU in an elevated process | Name format checked, `FirstPipeInstance`, bounded retries | Yes, 0.1.2 (*tested*) |
| S8 | Incident ids from JSON files were used as paths | **Low** | `IncidentStore.cs` | A crafted incident file could delete another `.json` file | Ids validated | Yes, 0.1.2 (*tested*) |
| S9 | Session id with the program name used as file name | **Low** | `Sessions.cs` | Unexpected file names | Sanitised | Yes (*tested*) |
| S10 | Anonymisation missed other accounts and short names in paths | **Low** | `ReportBuilder.cs` | Name in a shared report | Profile paths are replaced | Yes (*tested*) |
| S11 | HTML export had no content security policy; some ids unencoded | **Low** | `HtmlReport.cs` | Text from a shared incident file in a browser | All values encoded, CSP blocks scripts and remote content | Yes (*tested*) |
| S12 | CLI sent findings to a remote Ollama with only a note | **Low** | `Cli/Program.cs` | Data leaves the PC without consent | Needs `--allow-remote` | Yes |
| S13 | Update download without size limit | **Low** | `Maintenance.cs` | Disk filled by a bad response | 400 MB limit | Yes |
| S14 | Error log grew without limit | **Low** | `App.xaml.cs` | Disk use | Rotates at 1 MB | Yes |
| S15 | Updates are unsigned; the checksum comes from the same release as the installer | **Medium** | Release process | A compromised GitHub account reaches everyone with automatic updates | SignPath workflow is prepared; needs the project owner to apply. Automatic install stays off by default | **No** |
| S16 | Per-user install: the elevated helper starts from a folder the user can write to | **Medium** | Design | Malware running as the user could replace `WhyLag.exe` and wait for the next admin approval | Inherent to per-user apps. Options in section 10 | **No** |
| S17 | Client side of the helper pipe does not verify the server | **Low** | `LatencyHelper.cs` | A same-user process could feed fake driver numbers; no privilege gain | Accepted | **No** |

Reviewed without findings: the AI path (cloud models refused, remote address needs confirmation, output shown as text
and validated, nothing executed), PresentMon download (pinned hash), named events (same session only), Wi-Fi data
(names never stored).

## 4. Bugs found and fixed

| # | Problem | Severity | Fix |
|---|---|---|---|
| B1 | An exception in the loop (disk full, listener error) stopped recording silently | **Critical** | Each second is guarded; errors go to the log (*tested*) |
| B2 | Automatic capture ran on the recording thread, so rules and disk delayed the measurements and a failure hit B1 | **High** | Runs in the background (*tested*) |
| B3 | Waking from sleep would have read as a frozen PC | **High** | Gaps over 30 s are treated as sleep (*tested* with a simulated gap, not with real sleep) |
| B4 | Hostname ping target failed for the whole session if DNS was not up at sign-in, and DNS blocked the UI | **High** | Resolved in the background with retries |
| B5 | Router discovery froze the UI for 3 s at start and on every settings change | **Medium** | Background, with retries |
| B6 | A source failing five times stayed off until restart | **Medium** | Tried again every minute (*tested*) |
| B7 | PresentMon's frame queue grew without limit while recording was paused | **Medium** | Bounded |
| B8 | Browsers and Discord counted as gaming sessions (seen in real data) | **Medium** | Filtered, also in existing files (*tested*) |
| B9 | Automatic incidents pushed personal reports out of the history within days | **Medium** | Automatic ones are removed first (*tested*) |
| B10 | A note typed just before closing the window was lost | **Medium** | Saved on close |
| B11 | Unhandled errors outside the UI thread were never logged | **Medium** | Logged |
| B12 | "Today" stayed "Today" after midnight in a long-running window | **Low** | Relabelled |
| B13 | Broken or locked session files crashed the sessions page | **Low** | Skipped (*tested*) |
| B14 | Frame statistics leaked from one session into the next | **Low** | Reset |
| B15 | Hotkey and update errors were English in every language | **Low** | Translated |

**Open:** on 2026-11-01 WhyLag was not running after a reboot although the autostart entry was present and enabled.
There was no log and no crash event, so the cause is unknown; the user may have closed it. B11 makes a repeat visible.

## 5. Dead and unnecessary code removed

- Separate incident table and sidebar list (one list now), and their row type.
- `SessionStore.Delete`, unused.
- Status keys and summary keys no longer used.

## 6. Performance

Measured on the development PC with 81 real incidents (36 MB):

| Measure | Before | After |
|---|---|---|
| Reading the incident list | 600 ms on the UI thread, 240 MB allocated | **45 ms** in the background, **5 MB** |
| Temperature probing without HWiNFO or LibreHardwareMonitor | WMI query every 2 s, file probe every 1 s | every 30 s |
| Live page while minimized | updated every second | paused |
| Background CPU (window closed) | 1.04 % of one core, with driver helper | 0.73 % of one core, without helper (not a like-for-like comparison) |

Memory in the tray is 74 MB private after a fresh start. It was 117 MB after the window had been open; WhyLag now
releases memory when the window closes (effect not measured).

## 7. UI/UX

The previous design had already moved away from the stock look, but much of it was still a generic dashboard: every
value in its own rounded card. Changed:

- Live values are **one table**: name, value, last minute, note. More readable and comparable, and it fits eight
  metrics on a screen without scrolling.
- Sessions are a real table with aligned numbers. Findings and patterns share one surface with rules between them.
- Corners reduced to 4 px; no gradients, shadows or animation.
- Loading and empty states for list and patterns.
- Windows high-contrast colors are respected.

Kept on purpose: left navigation, list next to report, settings pages, palette.

## 8. Features

**Implemented**

| Feature | Why |
|---|---|
| Network spikes are ignored while nobody is at the PC and no game is in front | Most automatic incidents on the development PC were noise of this kind |
| *Report for provider* | A dated list of moments in which the line was slow while the router was fast. Turns "my internet is bad" into evidence |
| Personal reports survive the automatic ones | See B9 |
| `Ctrl+1…4`, `Ctrl+F` | Faster navigation |

**Recommended, not implemented**

| Feature | Value | How | Effort | Priority |
|---|---|---|---|---|
| Compare two sessions of a game | Answers "did the driver update help?" | Session statistics exist; add a comparison view | Medium | High |
| Per-game baseline | "Worse than your usual Deadlock" is more useful than absolute numbers | Rolling statistics per game in session store | Medium | High |
| Keep incidents with a note | A note means the user cares | Add a `pinned` list next to the incidents, honoured by `Prune` | Low | High |
| Group repeated incidents | 20× the same cause should be one row | Group by rule within an hour | Medium | Medium |
| Mark a finding as right or wrong | Tunes rules with the user's real experience | Store feedback, use as an export for rule work | Medium | Medium |
| Sharper per-process disk attribution | Names the file activity behind a disk hitch | ETW in the helper; privacy needs thought (file names are not recorded today) | High | Low |

## 9. Tests

| Project | Tests | Result |
|---|---|---|
| `WhyLag.Core.Tests` | 109 (83 before) | passed |
| `WhyLag.Collectors.Tests` | 36 (new project) | passed |
| `WhyLag.AI.Tests` | 31 | passed |
| **Total** | **176** | **0 failed** |

New: recorder under failure, sleep, retry; storage; idle detection; sessions; anonymisation; XSS; provider report;
router, helper and PresentMon safety. CI gains a step that renders every view.

Manually verified on the development PC: all pages with real data, no entries in the error log, every view rendered in
dark and light and at minimum window size, elevated helper starts and uses the new marker.

**Not verified:** real sleep and wake, high-contrast mode, mouse and hover behaviour (the screen was locked), measurement
data through the helper pipe, the save dialog of the provider report (its content was generated from the real incidents:
36 moments on 5 days), the changed CI workflows (not pushed).

## 10. Remaining risks and technical debt

1. **Code signing (S15, S16).** The most valuable next step. It removes SmartScreen warnings and lets the updater verify
   the publisher. The workflow is ready; the application to SignPath is the owner's decision.
2. **Elevated helper from a user-writable folder (S16).** Options: a signed helper, or an optional machine-wide install
   of only the helper in `Program Files`. The second is safer but needs admin rights at install time, against the
   project's "no admin" principle, so nothing was changed. Turning the helper on should remain a deliberate choice.
3. **UAC prompt at every sign-in with the helper on.** Trains users to click yes. A scheduled task could avoid it but
   would be a permanently elevated path. Left as is.
4. **`MainWindow.xaml.cs` size.** Split by page when it next grows. No UI automation tests beyond rendering.
5. **Hand-maintained binary offsets** in `WifiSource` and the HWiNFO reader can break with OS or tool updates; both fail
   softly.
6. **Rule thresholds** are heuristics tuned on few machines. More real fixtures would help more than more rules.
