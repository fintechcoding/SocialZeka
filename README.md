# SocialZeka

A Windows app that automatically records WhatsApp and Telegram voice calls, transcribes
them in Turkish, and then analyzes them with a local AI model. **No conversation content
ever leaves the machine.**

SocialZeka is forked from VoiceTranscript (5 Sept 2026). The recording and transcription
core is shared; on top of it sits a "social intelligence" coaching layer. Assemblies,
namespaces, and `VoiceTranscript.exe` deliberately keep the old name; the user-facing name,
install identity, release name, and data folder (`%LOCALAPPDATA%\SocialZeka.Data`) are new.

[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
![Platform: Windows](https://img.shields.io/badge/platform-Windows-blue.svg)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4.svg)
![Status: complete](https://img.shields.io/badge/status-complete-success.svg)

---

## Core idea

> What goes out of the microphone is **us**; what comes out of the speaker is the **other party**.

The microphone and the audio the app sends to the speaker are recorded as **two separate
streams**. This means who said what is not guessed — it is **known for certain**, with no
need for a speaker-diarization model at all. That both frees up room in 6 GB of VRAM and
stays correct even when two people talk at the same time, which is exactly where
diarization models fail most.

Group calls break this simplicity (several people on the other side mix into one stream),
so group calls are recorded **as audio only** — they are not transcribed or analyzed.

---

## What it does

| | |
|---|---|
| **Records** | Automatically when a call starts, or manually with one button. Two separate streams, aligned on a single timeline. Live level meters for both streams while recording. |
| **Transcribes** | After the call ends, on the GPU or in the cloud, with word-level timestamps. Multiple cloud services are tried in order. |
| **Analyzes** | Who promised what, which figure changed, which question went unanswered. |
| **Shows** | **The Ledger**: unkept promises, changed numbers, and unanswered questions across every contact, on one screen. |
| **Plays back** | A mirrored waveform of both streams, click-to-play, with the playing line highlighted in the transcript. |
| **Searches** | Full-text search across the entire archive, with contact / date / speaker filters and highlighted matches. |
| **Reports status** | Capture, model, queue, disk, and services on one page — each with the button that fixes it. |
| **Exports** | Obsidian markdown (local), backup, export-everything. Optional Notion (cloud, off by default). |

The analysis layer **does not pass judgment; it shows evidence.** Every finding carries a
verbatim quote and a clickable timestamp, so you listen and decide for yourself.

### Seeing it work right away

Without waiting for a first call: **Overview → Load sample conversations.** Three calls
spread across six weeks appear — a price changes twice, one promise slips past its date,
the same question goes unanswered twice. They are written into the same tables real data
uses, and removed with a single click.

---

## Status

**Complete.** All seven phases are written; two await measurement on the target machine.

| Phase | Content | Status |
|---|---|---|
| 0 | Solution skeleton, timeline alignment, Turkish text normalization | ✅ |
| 1 | Python worker (multi-engine), C# worker host, end-to-end transcription | ✅ |
| 2 | Call detection: WASAPI session state, state machine, hysteresis | ✅ |
| 3 | Audio capture: device loopback, process loopback, echo suppression | ✅ code · ⏳ measured on target |
| 4 | UI: contact list, transcript, click-to-play, ledger, settings | ✅ |
| 5 | Analysis: quote verification, commitment/contradiction ledger, scam patterns | ✅ |
| 6 | Obsidian export, Turkish full-text search | ✅ |
| 7 | Target-machine install, GPU benchmarks, Turkish accuracy evaluation | ⏳ on target |

**Tests:** 250 C# + 27 Python, all passing. Three are integration tests that start a real
Python process.

---

## Install

### On the machine that records (an RTX 4050 notebook)

Run `dist\VoiceTranscript-Setup-1.0.0.exe`. It needs no administrator rights and installs
itself under `%LOCALAPPDATA%\Programs\VoiceTranscript`.

On first launch the app shows a **setup wizard**: it checks whether Python is present and
installs it if not, installs the pinned Whisper packages, **verifies rather than assumes**
that the GPU is actually reachable, tests audio capture, and downloads the model files.
Each step has a single button and a one-line explanation of why it is needed.

If you would rather not install any of that: pick the cloud option under **Settings →
Transcription**. Then neither Python nor a model is required — in exchange, the call's
audio is uploaded to the service you chose.

If you prefer the command line to the wizard, `setup.ps1` does the same job:

```powershell
powershell -ExecutionPolicy Bypass -File setup.ps1
```

### On a development machine (no GPU, no sound card)

```powershell
powershell -ExecutionPolicy Bypass -File test.ps1
```

This runs the C# and Python suites together. `dotnet test` is deliberately not used: when
the .NET 10 SDK invokes the xUnit v3 module through Microsoft.Testing.Platform for the
`net10.0-windows` target it reports "Zero tests ran," whereas running the same module
directly finds every test.

```powershell
python -m vt_worker probe                           # what runs on this machine?
```

To generate a synthetic two-stream test call (identical to what a real recording produces,
plus a reference file that knows who said what and when):

```powershell
powershell -File tools/synth_utterances.ps1 -ScriptPath tools/testcall.json -OutDir .work/utt
python tools/make_test_call.py --utterances .work/utt --script tools/testcall.json --out .work/call
```

The transcription integration test downloads Whisper weights, so it is skipped by default:

```powershell
$env:VT_RUN_ASR_TESTS = "1"; powershell -ExecutionPolicy Bypass -File test.ps1
```

### Building a distribution package

```powershell
powershell -ExecutionPolicy Bypass -File publish.ps1
```

This produces the `dist\VoiceTranscript` folder (a self-contained publish that needs no
.NET), and, if Inno Setup is installed, the `dist\VoiceTranscript-Setup-1.0.0.exe`
installer. If the compiler is missing, the command tells you:
`winget install JRSoftware.InnoSetup`.

Python, the model weights, and the pip dependencies are **deliberately not bundled**. The
wizard downloads them on first launch; bundling would blow the 65 MB installer up into
gigabytes and freeze versions the wizard can otherwise keep current.

---

## Layout

```
src/VoiceTranscript.Core/      net10.0             pure logic, fully testable
  Audio/TimelineWriter.cs        aligns the two streams (see below)
  Audio/CallRecorder.cs          two WAVs, one shared time anchor
  Audio/FileAudioSource.cs       development without audio hardware
  Detection/CallDetector.cs      call state machine (pure, with hysteresis)
  Text/TurkishText.cs            Turkish search normalization
  Analysis/QuoteVerifier.cs      guard that blocks fabricated quotes
  Analysis/DeterministicChecks.cs the ledger's real value — no LLM, just arithmetic
  Analysis/AnalysisPipeline.cs   extract → verify → compare → ledger
  Storage/                       SQLite schema + repository, FTS5 Turkish search
  Export/ObsidianExporter.cs     markdown output
  Asr/ + Llm/                    selectable engines, with their measured numbers

src/VoiceTranscript.Capture/   net10.0-windows     WASAPI
  WasapiCaptureBackend.cs        device loopback + microphone + echo suppression
  ProcessLoopbackCaptureBackend.cs per-application capture (optional)
  AudioSessionWatcher.cs         session sampling for call detection
  TargetProcesses.cs             WhatsApp/Telegram process-tree resolution

src/VoiceTranscript.Worker/    net10.0-windows     Python supervision
  JobObject.cs                   kills the worker even if the app is force-closed
  PythonWorkerHost.cs            launch, read progress, classify errors

src/VoiceTranscript.App/       WPF (.NET 10)       the UI
worker/                        Python              Whisper + CUDA
tools/                         development helpers
tests/                         C# tests
```

---

## Why it is built this way

This section answers the places where, reading the code, you might ask "why is this so
complicated?"

### Timeline alignment — the project's quietest trap

WASAPI loopback **produces no packets while nothing is playing**. This is not in Microsoft's
docs; it is in NAudio's. If you append packets back to back, a one-hour call in which the
other party speaks half the time produces a loopback file that is **half an hour short**,
and every speaker attribution after the first silence is wrong.

`TimelineWriter` places each packet by its QPC timestamp and fills the gaps with silence.
It also **anchors both streams to a single shared start**: anchoring each stream to its own
first packet was shifting every timestamp for whichever side started talking late. Both are
pinned down by tests.

### Whisper's segment boundaries are unreliable; its word timestamps are reliable

In a real run, two separate sentences with 6.4 seconds of silence between them merged into
a single 13-second segment, and the price inside it was timestamped **11 seconds before** it
was actually spoken. That breaks "click a quote and hear that moment" — i.e. the entire
verifiability of the analysis layer.

`segmentation.py` re-splits segments at the real pauses in the word timestamps, then merges
adjacent pieces from the same speaker. On the reference call the result is **9 segments = 9
real conversational turns**, with an average timestamp error of **0.07 seconds**.

### Quote verification is mandatory

Every quote the model produces is checked to actually appear in the text; if it does not,
the record is **rejected**. The product presents everything as "this person said this, at
this moment, click to hear it." A model that fabricates quotes turns that into fake evidence
about a real person — someone's friend, supplier, or family. A system that fabricates quotes
about people is worse than no system at all.

The comparison is lenient in presentation and strict in content: Turkish spelling
differences and punctuation are ignored (the model may have written "yapacagim" for
"yapacağım"), but different words are rejected.

### Why there is no "lie detection"

An LLM cannot detect lies from a conversation transcript. In *Beyond Liars' Bench*
(arXiv:2607.20479), even white-box probes with access to the speaker's internal activations
average 0.45–0.51 AUROC across conditions (chance = 0.50), dropping to **0.12–0.14** — worse
than random — when the type of lie changes. With a human speaker that access does not exist
at all.

The base-rate math is even clearer: with a generous 82% sensitivity/specificity and a
realistic 5% prevalence, **81% of "this person is lying" flags are wrong** — about the user's
own family and colleagues.

Instead, what is **countable** is computed: what was promised, how many times a date was
recorded, how many times a price changed, which direct question went unanswered. All of it
is arithmetic, all of it explainable, none of it capable of hallucinating. The model only
finds and quotes; the verdict stays in code.

Two Turkish-specific traps are handled too: phrases like `"bakarız"`, `"inşallah"`, and
`"bir ara"` count as polite refusals and are not recorded as commitments. And if the audio
is unclear, the numbers in that segment are excluded from automatic contradiction detection
— a misheard amount would otherwise produce a fake price contradiction about a real person.

### CUDA setup: cuDNN is no longer needed

CTranslate2 4.6.3 (Jan 2026) dropped the cuDNN dependency. The only NVIDIA DLL required is
`cublas64_12.dll`. **Both the faster-whisper README and the CTranslate2 docs still tell you
to install cuDNN 9 — both are stale.** The common online advice to "downgrade to
ctranslate2==4.4.0" now works in reverse: that version brings back a hard cuDNN 8 dependency.

This was verified by running `setup.ps1`: **no cuDNN** appears among the installed packages.

On Windows, `pip install nvidia-cublas-cu12` alone is also not enough; since Python 3.8 the
Windows loader does not search `PATH` for C-extension dependencies. `sitecustomize.py` solves
this with `os.add_dll_directory()`. The `LD_LIBRARY_PATH` fix in the READMEs is Linux-only and
**silently does nothing** on Windows — which is why the error is usually misdiagnosed as
"CUDA is broken."

### Turkish search fails silently

SQLite FTS5's `unicode61` tokenizer applies standard Unicode lowercasing, which is wrong for
Turkish's dotted and dotless i. A search for `ışık` will not find `IŞIK` — and **raises no
error**, it just returns empty, so you assume there is no data. Both the index and the query
are put through the same normalization. Because Turkish is agglutinative, prefix queries are
used: a search for `kitap` also reaches `kitabı`, `kitaptan`, and `kitabımı`.

### Why device loopback is the default

Per-process capture takes only WhatsApp's audio and leaves background music out — cleaner on
paper. But on the target machine's exact build (26200) that virtual device rejects the format
negotiation (`E_NOTIMPL`), always reports device position 0, and its QPC values are a packet
counter rather than a clock. So **alignment cannot be verified**. It has also been reported to
return a buffer of the correct length but filled entirely with zeros on some VoIP clients —
silence is indistinguishable from success, the worst possible failure mode for a recorder.

Device loopback has none of these problems. Its cost — capturing whatever is playing at that
moment — stays theoretical, because recording only runs when a call is detected. Process
loopback can be enabled in settings, and is tested with real audio before it is turned on.

### One job per worker process

The only reliable mechanism that returns VRAM fully to the driver is process exit. `del model`
+ `gc.collect()` + `torch.cuda.empty_cache()` does not work with CTranslate2 — it does not use
torch and has its own caching allocator. The same decision also roots out the problem of the
CUDA context being invalidated after sleep.

`JobObject` then guarantees the Python process is never orphaned even if the app is force-killed
from Task Manager — otherwise a ghost process with no visible cause would keep eating the user's
VRAM.

### Which model, and why

The selection screen shows **measured Turkish error rates**, because choosing by name is
misleading: the most popular Turkish Whisper adaptation
(`selimc/whisper-large-v3-turbo-turkish`) makes almost **twice** the errors of the plain model
it is based on (20.71 vs 12.17 WER).

The default `large-v3-turbo` costs only +0.24 WER over `large-v3` in Turkish, in exchange for
~3× the speed and 44% less VRAM. Dropping to `medium` means a 37% relative error increase — and
turbo uses less VRAM anyway, so dropping there for VRAM's sake makes no sense.

On the LLM side, Turkish-specialized models are not used: three independent 2026 sources
(Cetvel/EACL, TurkBench, TUDUM) show them lagging general-purpose multilingual models. The
default `Qwen3.5-4B Q6_K` fits in 6 GB with a 32k-token context and has the tokenizer that
spends the fewest tokens per word in Turkish (~55% below the LLaMA family).

---

## Two machines

This repo was developed on a machine **with no NVIDIA GPU and no audio hardware**; the target
machine is an RTX 4050 notebook with 6 GB of VRAM. That is why the audio source sits behind an
interface, the tests run on synthetic WAV files, and everything GPU-dependent has a CPU
counterpart.

**Two measurements that must be done on the target machine** — neither can be learned from
documentation:

1. **Record a call of at least one hour and verify the two WAV files come out the same length.**
   If they diverge, speaker attribution after the first silence is not trustworthy.

2. **Manually correct five real Turkish calls and compute the error rate.** Below 15% is usable;
   markedly above means another model should be tried.

The window list and accessibility tree should also be dumped during one real WhatsApp and one
real Telegram call — the one open question about WhatsApp's call window is closed by that single
experiment.

---

## Known open risks

| Risk | Mitigation |
|---|---|
| Qwen3.5-4B's Turkish quality is unmeasured (benchmarks predate the model) | TR-MMLU + manual evaluation on target; fallback `Trendyol-LLM-8B-T1` |
| Two-stream sync over 60 minutes | QPC-anchored buffer + mandatory beep test |
| WhatsApp call-window structure unknown | Fallback already exists: one-touch tagging after the call, then remembered |
| Echo if no headphones are used | Windows echo suppression + warning when detected |
| `faster-whisper` has had no commits in 9 months | Versions pinned; whisper.cpp engine ready as an alternative |
| Process loopback is broken on build 26200 | Not the default; device loopback is the fallback |

---

## Documentation

| Document | For |
|---|---|
| [docs/MIMARI.md](docs/MIMARI.md) | What is where and **why it is there**. Every decision is annotated with what it prevents. |
| [docs/GELISTIRME.md](docs/GELISTIRME.md) | Build, test, the two-machine constraint, reporting bugs. |
| [docs/ISLEM-GUNLUGU.md](docs/ISLEM-GUNLUGU.md) | What was done, why, and how it was verified — in date order. |
| [PRODUCT.md](PRODUCT.md) | Product rationale: what it is and what it is not. |

Before changing anything, look at the **"Places to check before changing"** table at the end of
`docs/MIMARI.md`. Most of the decisions here guard against things that break *silently*.

---

## When something goes wrong

The app writes what it does to disk:

```
%LocalAppData%\VoiceTranscript.Data\logst-YYYY-MM-DD.log
```

**Status → Log → Copy log** puts the last three days on the clipboard. The log contains **no
conversation text, no contact names, and no API keys** — it is written to be shared.

---

## Responsibility

This is a call recorder. The legal status of recording the other party's voice varies by
country and situation; some places require the consent of both parties. Deciding what gets
recorded and who it is shared with is the responsibility of whoever runs the app.

The app has taken a side on this: a banner stays at the top of the screen while recording is in
progress, automatic recording can be turned off with one click, and content does not leave the
machine.

---

## License

Released under the [MIT License](LICENSE).
