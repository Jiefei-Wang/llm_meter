# LLM Meter

A lightweight Windows tray widget that monitors local LLM inference servers in real time.
Small frameless desktop widgets show tokens/s, context usage, queue depth, active requests,
and time-to-first-token — per backend, with honest labeling of where every number came from.

## Supported backends

| Backend | Detection | Metrics source |
|---|---|---|
| **vLLM** | `/metrics` (`vllm:*` names) | Prometheus counters/gauges/histograms |
| **llama.cpp server** | `/metrics` (`llamacpp:*`), `/slots`, `/props` | Prometheus + native `/slots` API |
| **LM Studio** | `/api/v0/models` | REST v0 (+ v1 fallback) |
| **Ollama** | `/api/version`, `/api/ps`, `/api/tags` | Native API |
| **Generic OpenAI-compatible** | `/v1/models` shape | Counters only (no token telemetry) |

## Discovery

* Probes known ports on localhost (8000, 8080, 1234, 11434 by default).
* Enumerates Windows TCP listeners via `GetExtendedTcpTable` and fingerprints processes
  likely to be inference servers (server/llama/vllm/ollama/koboldcpp etc.).
* Detects WSL distros and probes listeners inside them via `wsl.exe`
  (UTF-16 output decoded automatically; `localhost` forwarding assumed).

## Honest metrics

Every displayed value carries its provenance:

* **Exact** — read directly from the backend (native APIs like `/slots` or `/api/ps`).
* **Approximate** — derived (counter deltas over scrape intervals, EMA-smoothed rates,
  histogram-sum TTFT estimates). Tooltips explain the derivation.
* **Unavailable** — the backend simply doesn't expose it; shown as `-`, never guessed.

Rates never go negative across counter resets; stale counters decay to a real zero;
TTFT windows mix exact single-request deltas with weighted batch averages.

## Usage

* Run `LLMMeter.exe` — a tray icon appears; widgets open for each discovered backend.
* **Drag** to move · **Ctrl+wheel** or **Ctrl+N/+/-** to scale · double-click the expand
  button to toggle the request list.
* Right-click the header or tray icon for the full menu (add manual endpoint,
  theme, topmost, close widget).
* Config lives next to the exe as `LLMMeter.json` (atomic writes; corrupt files are
  backed up as `.broken` and replaced with defaults).

## Build

Requires .NET 8 SDK (Windows):

```sh
dotnet test tests\LLMMeter.Tests\LLMMeter.Tests.csproj     # unit tests
dotnet publish src\LLMMeter\LLMMeter.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true -o publish
```

Output: a single ~68 MB self-contained `LLMMeter.exe`.

## Privacy

The app only issues HTTP GETs to localhost endpoints you discovered or configured.
No prompts, completions, or tokens ever leave your machine; nothing is sent anywhere.
