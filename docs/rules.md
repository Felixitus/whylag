# JSON rules

Simple threshold checks can be written as JSON instead of C#. WhyLag loads the built-in ones from
`src/WhyLag.Core/Rules/builtin.rules.json` and your own from `%LOCALAPPDATA%\WhyLag\rules\*.json` (restart WhyLag
after a change). `whylag-cli rules` lists all loaded rules and reports broken files.

```json
[
  {
    "id": "custom.vram",
    "category": "Gpu",
    "when": [ { "metric": "gpu.vram.used.mb", "above": 7500 } ],
    "minSeconds": 2,
    "confidence": 0.5,
    "title": { "en": "Video memory nearly full ({0} MB)", "de": "Grafikspeicher fast voll ({0} MB)" },
    "summary": { "en": "Textures no longer fit into video memory and are streamed from RAM." },
    "evidence": [
      { "text": { "en": "Peak {0} MB, {1} s above the limit" }, "args": [ "peak:gpu.vram.used.mb", "seconds" ] }
    ],
    "checks": [ { "en": "Lower the texture quality." } ]
  }
]
```

| Field | Meaning |
|---|---|
| `id` | Unique rule id, shown in reports and used for patterns. |
| `category` | `Cpu`, `Disk`, `Memory`, `Gpu`, `Network`, `System`, `Background`, `Events` or `Frames`. |
| `when` | Conditions that must all hold in the same second: `metric` with `above` and/or `below` (inclusive). |
| `minSeconds` | How many seconds of the analysed window must match (default 2). |
| `peak` | Metric that picks the worst second; defaults to the first condition's metric. |
| `confidence` | Rule match from 0 to 1 (default 0.5). `boost` raises it when `any` of its conditions holds. |
| `title`, `summary` | `{0}` is the peak value. |
| `evidence` | Lines with arguments: `peak:<metric>` (value in the worst second), `max:<metric>`, `baseline:<metric>` (median before the problem; the line is left out when there is no baseline) and `seconds`. |
| `checks` | What the user can try. |

Texts are either an object with one string per language code (`en` is the fallback) or a translation key from
`src/WhyLag.Core/Localization`. Comments and trailing commas are allowed.

Metric names are listed in `MetricKeys` in [src/WhyLag.Core/Model.cs](../src/WhyLag.Core/Model.cs), for example
`cpu.total`, `disk.latency.ms`, `gpu.3d`, `ping.target.ms`, `frames.max.ms`, `temp.gpu` and `wifi.rssi`.
