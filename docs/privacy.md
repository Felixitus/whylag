# Privacy and security

## Recorded (in memory, default 10 minutes)

- System metrics: CPU, memory, disk, GPU, network throughput, TCP retransmissions, DPC/interrupt time
- Top processes by CPU, I/O, GPU and memory (**names only**)
- Name of the program in front, its frame times (PresentMon, input tracking off)
- Ping times to router and one internet address; Wi-Fi signal, link speed, "access point changed"
- Windows events: warnings/errors from System and Application, Wi-Fi and Windows Update events
- Optional helper: longest DPC/ISR time per driver file name

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

No telemetry, update checks or crash uploads.

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
