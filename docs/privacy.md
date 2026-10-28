# Privacy and security

## Recorded (in memory, default 10 minutes)

- System metrics: CPU, memory, disk, GPU, network throughput, TCP retransmissions, DPC/interrupt time
- Top processes by CPU, I/O, GPU and memory (**names only**)
- Name of the program in front, its frame times (PresentMon, input tracking off)
- Ping times to router and one internet address; Wi-Fi signal, link speed, "access point changed"
- Windows events: warnings/errors from System and Application, Wi-Fi and Windows Update events
- Optional helper: longest DPC/ISR time per driver file name
- Private memory of the largest processes once per minute for the last 24 hours (names only, in memory)
- Optional: CPU/GPU temperatures read from HWiNFO or LibreHardwareMonitor running on this PC
- Optional: total internet traffic counters of your router (UPnP)

Gaming session summaries (program name, fps, hitches, causes) are saved to `%LOCALAPPDATA%\WhyLag\sessions`.
The update comparison reads driver and Windows Update events from the local event log when the patterns view opens.

**Never recorded:** window titles, command lines, keystrokes, screen content, URLs, packet content, file names,
Wi-Fi network names or access-point addresses (compared in memory only).

When an incident is captured, the last three minutes are saved to `%LOCALAPPDATA%\WhyLag\incidents\<id>.json`.
Copied and exported reports remove user and computer names.

## Connections

| Connection | When |
|---|---|
| ICMP ping to the default gateway and `1.1.1.1` (configurable) | once per second, can be turned off |
| Ollama (`127.0.0.1:11434` by default) | only on *Explain*, *Test connection* or a confirmed model download |
| github.com | only on a confirmed PresentMon download (not needed with the installer) |
| api.github.com | once a day to read the latest WhyLag version number; can be turned off in *Settings* |
| github.com | when you click *Update now* (or automatic updates are on): downloads the installer and `SHA256SUMS.txt` of the new release |
| Your router (SSDP multicast and UPnP on the local network) | only when *router traffic* is enabled |

No telemetry or crash uploads. The bug report ZIP (*Settings → Data*) is only created on request and stays on your
PC until you attach it somewhere yourself; it contains system info, settings without the Ollama address, the last
500 log lines and the latest incident report, with user and computer name removed.

## Local AI

Off by default. Receives only findings and evidence, never raw data. Asks before sending to a non-local address,
refuses Ollama cloud models, validates every answer and never triggers actions.

## Permissions

WhyLag runs as a normal user. Elevated parts are optional and explicit:

- **Frame times** need the *Performance Log Users* group (SID `S-1-5-32-559`); WhyLag adds you only after you click
  *Allow* and approve the admin prompt.
- **Driver latency helper** runs elevated only while WhyLag runs. It reads the kernel trace for DPC/ISR timings, sends
  only these numbers through a named pipe that only your account may open, accepts no commands and exits with WhyLag.

Child processes are bound to WhyLag with a job object. Autostart uses the per-user `Run` key.

## Deleting data

Delete one incident in the report, all in *Settings → Data*, or remove `%LOCALAPPDATA%\WhyLag`.

Security issues: see [SECURITY.md](../SECURITY.md).
