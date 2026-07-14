# Pocket TTS Memory Growth Investigation

**Investigation date:** 2026-07-14  
**Platform:** Windows, CPython 3.12, CPU inference  
**Relevant dependency:** `pocket-tts==2.1.0`  
**Status:** Root cause identified and fixed in `sidecar/server.py` (persistent worker
threads); see [Root cause identified and fixed](#root-cause-identified-and-fixed-2026-07-14)

## Executive summary

During a long game session, the Python process hosting RadioChatter's Pocket TTS and
Whisper models grew from roughly 800 MB to more than 2 GB of resident memory. The
memory stayed high when the game was paused and no radio audio was queued.

The original working hypothesis was that overlapping `/speak` requests caused the
growth. That hypothesis was too narrow. Concurrent TTS generation is unsafe and can
amplify peak memory use, but controlled tests show that it is **not required** to
reproduce the reported growth.

The defect reproduces with all of the following constraints:

- STT completely disabled;
- no HTTP server in the most isolated test;
- one TTS model and one voice;
- a fixed input sentence;
- strictly sequential calls, with each call completing before the next starts;
- explicit Python garbage collection;
- no increase in live Python tensor count or tensor bytes; and
- no increase in the Python heap large enough to explain the process growth.

In the direct-library test, resident memory grew from 696.9 MB to 856.1 MB across 24
sequential generations. The live tensor inventory stayed exactly constant at 238
tensors and 430.6 MB. The traced Python heap stayed between 0.0 and 0.1 MB after model
initialization. Windows C-runtime heap minimization did not recover any memory.

The supported conclusion is that Pocket TTS/PyTorch retains or loses memory in native
code during repeated generation. The current measurements do not distinguish an
unreachable native allocation from a native allocator, oneDNN, or thread-local cache
that grows per call. Operationally, however, it behaves as a leak: private memory and
working set continue increasing for a fixed serialized workload and do not fall while
idle.

The most reliable application-level containment is to move Pocket TTS into a dedicated
worker process and recycle that worker after a configurable request count or memory
threshold. Whisper STT should remain in the long-lived parent so voice-command
recognition is not reloaded or interrupted when the TTS worker is recycled.

## Relevant application paths

RadioChatter keeps the TTS model and all configured voice states in one Python process:

- [`PocketTtsEngine`](../sidecar/server.py#L150) loads the model and voice states.
- [`PocketTtsEngine.speak`](../sidecar/server.py#L175) calls
  `self.model.generate_audio(state, text)` for every synthesized line.
- [`Handler.do_POST`](../sidecar/server.py#L285) serves `/speak` requests.
- [`ThreadingHTTPServer`](../sidecar/server.py#L414) allows request handlers to overlap.
- [`PocketTtsClient.Request`](../src/RadioChatter/Speech/PocketTtsClient.cs#L30) currently
  deduplicates only identical in-flight keys; it does not impose one global TTS request.
- [`RadioAudioPlayer.TryStartPendingSpeech`](../src/RadioChatter/Audio/RadioAudioPlayer.cs#L556)
  can attempt several pending audio requests during one update.
- [`VoiceCommandController`](../src/RadioChatter/Speech/VoiceCommandController.cs#L31)
  already limits STT to one push-to-talk transcription at a time.

The installed Pocket TTS 2.1.0 implementation states that `generate_audio()` and
`generate_audio_stream()` are not thread-safe. Its generation path also creates fresh
decoder and autoregressive-generation threads inside each call. The current upstream
implementation can be inspected in
[`pocket_tts/models/tts_model.py`](https://github.com/kyutai-labs/pocket-tts/blob/main/pocket_tts/models/tts_model.py).

Those concurrency facts remain correctness and peak-memory concerns, but the isolated
tests below demonstrate a separate problem within serialized generation.

## Measurement terminology

All values in this report are MiB, calculated using 1 MiB = 1,048,576 bytes.

- **Working set / RSS:** physical pages currently resident for the process. This is
  close to the number normally noticed in Task Manager.
- **Private memory:** committed memory private to the process. It may be larger than the
  resident working set and is important because it cannot be shared with other
  processes.
- **Python heap:** allocations visible to Python's `tracemalloc`. Native PyTorch tensor
  storage and backend workspaces are not generally represented here.
- **Live tensor bytes:** the sum of `nelement() * element_size()` for live Python tensor
  objects found through the garbage collector. It is useful for comparing snapshots,
  although shared tensor storage means it should not be treated as a perfect unique-byte
  total.

## Observation 1: reported game session

The game-launched sidecar used two visible Python processes:

- a small virtual-environment launcher process at approximately 24 MB; and
- the real bundled CPython process that loaded Pocket TTS and Whisper.

At the diagnostic snapshot, the real process had the following state:

| Metric | Value |
| --- | ---: |
| Working set | 2,369.5 MB |
| Private memory | 3,574.7 MB |
| Threads | 40 |
| Handles | 514 |

For the current sidecar run at the time of that snapshot, the log contained:

| Event | Count |
| --- | ---: |
| Successful or attempted `/speak` requests | 177 |
| `/speak` HTTP 500 responses | 5 |
| `/transcribe` requests | 69 |
| TTS client timeouts in the game log | 3 |

This session established the user-visible symptom but did not isolate its source. It
contained TTS, STT, HTTP, multiple voices, varied sentence lengths, and possible request
overlap.

The high memory remaining after the game was paused rules out an actively growing game
queue as the only explanation. By itself, an idle high-water mark cannot distinguish
live retained objects from native allocator retention, so two controlled tests were
performed after the game and original sidecar exited.

## Observation 2: isolated serialized HTTP test

A fresh copy of the existing sidecar was started on port 5076 with STT disabled using
`--stt-model=`. The deployed, release-payload, and workspace `server.py` files had the
same SHA-256 hash at the time of the test.

Requests were sent through one client and awaited synchronously. The next request was
not submitted until the previous response body had been read completely. Therefore,
this test had no concurrent TTS generation.

### First eight calls

| Stage | Working set | Private memory | Threads |
| --- | ---: | ---: | ---: |
| Fresh loaded sidecar | 698.6 MB | 1,910.8 MB | 38 |
| After call 1 | 763.4 MB | 1,979.9 MB | 38 |
| After call 2 | 785.8 MB | 2,015.2 MB | 38 |
| After call 3 | 792.9 MB | 2,029.4 MB | 38 |
| After call 4 | 808.7 MB | 2,022.2 MB | 38 |
| After call 5 | 827.2 MB | 2,047.2 MB | 38 |
| After call 6 | 811.9 MB | 2,052.9 MB | 38 |
| After call 7 | 819.1 MB | 2,031.8 MB | 38 |
| After call 8 | 823.5 MB | 2,052.1 MB | 38 |
| Ten seconds idle | 823.5 MB | 2,052.1 MB | 38 |

Both working set and private memory fluctuated from call to call, but neither returned
to the loaded baseline. Ten idle seconds produced no reduction.

### Extended run

The same process completed 48 serialized `/speak` calls in total. At the end:

| Metric | Fresh baseline | After 48 calls | Change |
| --- | ---: | ---: | ---: |
| Working set | 698.6 MB | 1,168.3 MB | +469.7 MB |
| Private memory | 1,910.8 MB | 2,398.8 MB | +488.0 MB |

The final thread count was 32 rather than an ever-increasing number, so the process did
not accumulate live operating-system threads. The varied sentences in this test had
different lengths and generated-audio durations, which can explain why its growth rate
was higher than the fixed-sentence direct-library test below. It does not change the
important result: serialization alone did not bound memory.

## Observation 3: direct Pocket TTS library test

The final test bypassed RadioChatter's HTTP server entirely and invoked Pocket TTS 2.1.0
directly. It loaded only the English model and the `vera` voice. Whisper was not imported
or loaded. Every generation used the same sentence and waited for completion before the
next call.

The probe recorded:

- process working set and private usage through Windows
  `GetProcessMemoryInfo`;
- live tensor count and aggregate tensor bytes after `gc.collect()`;
- Python allocations through `tracemalloc`, started after model and voice loading; and
- the result of the UCRT `_heapmin()` compaction call.

### Raw results

| Stage | Working set | Private memory | Live tensors | Tensor bytes | Python heap |
| --- | ---: | ---: | ---: | ---: | ---: |
| Baseline | 696.9 MB | 1,891.3 MB | 238 | 430.6 MB | 0.0 MB |
| Normal call 3 | 799.0 MB | 1,972.3 MB | 238 | 430.6 MB | 0.0 MB |
| Normal call 6 | 807.0 MB | 1,980.5 MB | 238 | 430.6 MB | 0.0 MB |
| Normal call 9 | 815.1 MB | 1,988.7 MB | 238 | 430.6 MB | 0.0 MB |
| Normal call 12 | 823.1 MB | 1,996.8 MB | 238 | 430.6 MB | 0.0 MB |
| After `_heapmin()` | 823.1 MB | 1,996.8 MB | 238 | 430.6 MB | 0.0 MB |
| Trimmed call 3 | 831.1 MB | 2,005.0 MB | 238 | 430.6 MB | 0.0 MB |
| Trimmed call 6 | 839.0 MB | 2,013.1 MB | 238 | 430.6 MB | 0.0 MB |
| Trimmed call 9 | 846.9 MB | 2,021.2 MB | 238 | 430.6 MB | 0.1 MB |
| Trimmed call 12 | 856.1 MB | 2,030.3 MB | 238 | 430.6 MB | 0.1 MB |

“Trimmed” calls ran `gc.collect()` and `_heapmin()` after every generation. `_heapmin()`
returned success but did not reduce either process metric. The peak traced Python heap
was only 2.0 MB throughout the run.

After the initial warm-up increase, the second half of this fixed workload added about
33.5 MB over 12 calls, or approximately 2.8 MB per generation. The exact rate should not
be assumed universal: it can depend on sentence length, generated duration, voice state,
CPU backend, and Windows/PyTorch versions. The stable tensor and Python-heap numbers are
more important than the precise rate.

## What the tests rule out

### Whisper STT as the primary source

Ruled out. The serialized HTTP test disabled STT, and the direct-library test did not
load `faster-whisper` at all. Memory still increased.

### HTTP bodies, Base64 transcription audio, or request-handler retention

Ruled out as a necessary cause. The direct-library test had no HTTP server, requests,
JSON, Base64 data, or response streams.

### Concurrent Pocket TTS generation as a necessary cause

Ruled out. Both controlled tests were strictly serialized. Concurrency remains unsafe
because Pocket TTS documents the generator as not thread-safe, and it may increase peaks
or trigger synthesis failures, but it is not the root requirement for this memory
growth.

### RadioChatter's C# audio cache

Ruled out as an explanation for Python memory. The cache lives in the Unity process,
not the Python process. The direct-library test did not run Unity or the mod.

### Accumulating live Python tensors or autograd graphs

Not supported by the evidence. The number of live tensors and their aggregate byte count
were identical at every direct-test snapshot. Pocket TTS's generation methods also use
PyTorch's no-gradient mode. This does not prove every native buffer is freed, but it
shows that ordinary Python references to tensors are not accumulating.

### A conventional Python-object leak

Not supported by the evidence. The traced Python heap stayed near zero after the model
baseline while native process memory continued to increase.

### Runaway live threads

Ruled out in the observed runs. Thread count stayed flat in the first serialized test
and was lower, not higher, at the end of the 48-call run.

### A queue that is still actively synthesizing while paused

Ruled out as the explanation for the controlled idle result. Each serialized request
had completed, the CPU work stopped, and the process metrics remained high during idle.

## Supported diagnosis

The leak or retention is below the managed Python object layer, in the native execution
path exercised by `TTSModel.generate_audio()` on Windows.

Pocket TTS performs substantial native work per call:

1. It deep-copies the selected voice/model state so the reusable state is preserved.
2. It expands transformer KV caches to the sequence length required for the text and
   estimated audio generation.
3. It creates a decoder thread and an autoregressive-generation thread.
4. Those threads execute PyTorch CPU tensor operations and the Mimi decoder.
5. Temporary tensors become unreachable by the end of the call, as supported by the
   stable live-tensor inventory.

The process nevertheless retains additional private and resident memory. Likely native
locations include PyTorch's CPU allocator, oneDNN/OpenMP workspaces, or per-thread native
state created by Pocket TTS's fresh internal worker threads. This last sentence is an
inference, not a proven allocation-stack attribution.

Distinguishing those candidates precisely would require native allocation tracing, such
as Windows Performance Recorder/Analyzer, UMDH with page-heap-compatible symbols, or a
minimal upstream reproducer instrumented inside PyTorch/Pocket TTS. It is not necessary
to establish the application impact or implement process-level containment.

## Separate concurrency defect

Although concurrency does not cause the serialized leak, RadioChatter should still stop
issuing overlapping TTS calls:

- Pocket TTS explicitly says generation is not thread-safe.
- The sidecar uses `ThreadingHTTPServer` without a TTS generation lock.
- The C# client permits different cache keys to synthesize simultaneously.
- A four-second C# timeout does not cancel Python synthesis that has already started.
- The observed game session included HTTP 500 responses and timed-out TTS requests.

Serializing the C# request path and defensively rejecting or queueing overlapping
`/speak` requests in Python remains appropriate. It should be treated as a correctness
and load-control fix, not as the complete memory fix.

## Recommended mitigation

### Preferred: dedicated recyclable TTS worker process

Keep the current HTTP service and Whisper STT model in a long-lived parent process, but
move Pocket TTS model loading and generation into a dedicated child process.

The parent should:

1. maintain a bounded, serialized TTS job queue;
2. send one `(voice, text)` job at a time to the worker;
3. receive WAV bytes or a structured error;
4. track worker request count and resident/private memory;
5. stop accepting new work for that worker once a limit is reached;
6. allow its current response to complete;
7. terminate and recreate only the TTS worker; and
8. report a temporary `loading` or `restarting` TTS state while keeping STT available.

This converts unbounded lifetime growth into a known ceiling. Process termination is the
only mechanism observed to reliably return all of the native memory.

The recycle policy should be configurable and should use both a request-count backstop
and a memory threshold. A request count alone cannot account for very different line
lengths. A memory threshold alone should include hysteresis so a newly loaded worker does
not immediately recycle on systems whose baseline is naturally higher.

Starting the replacement before stopping the old worker would reduce latency but nearly
double model memory during overlap. On memory-constrained systems, stop-then-start is the
safer default.

### Simpler fallback: recycle the entire sidecar

The existing sidecar can exit cleanly after a request-count or memory limit and rely on
`SidecarSupervisor` to restart it. This is much easier to implement, but it also unloads
and reloads Whisper, temporarily interrupts voice commands, and produces a larger
service gap. It is acceptable as an interim containment measure if a dedicated worker
is too large a change.

### Longer-term: upstream native fix

Create a minimal Pocket TTS 2.1.0 Windows reproducer from the direct-library test and
report it upstream with the constant live-tensor and Python-heap measurements. Useful
experiments for an upstream fix include:

- replacing per-generation Python threads with persistent worker threads;
- explicitly joining every internal generation thread;
- reusing bounded KV-cache and Mimi scratch buffers;
- comparing Windows against Linux with the same PyTorch build;
- comparing PyTorch CPU allocator and oneDNN configurations; and
- tracing native allocations by stack across hundreds of identical generations.

Patching private Pocket TTS internals inside RadioChatter would be fragile across package
updates, so process isolation is a safer mod-level response unless an upstream patch is
available.

## Verification plan for a mitigation

A memory fix should not be accepted based on a short functional test. The following soak
tests are recommended:

1. Load TTS and STT and record their stable post-load baseline.
2. Synthesize at least 300 identical fixed-length lines sequentially.
3. Synthesize at least 300 mixed lines across every configured voice.
4. Include short, medium, and near-maximum expected radio messages.
5. Confirm only one Pocket TTS generation executes at a time.
6. Confirm memory remains below the configured ceiling across multiple worker recycles.
7. Leave the game and sidecar idle for at least 20 minutes after a cycle.
8. Verify STT commands continue working before, during, and after a TTS-worker recycle.
9. Verify no completed response is truncated and no queued response is silently lost.
10. Verify the Unity client does not enter a timeout/retry storm while TTS is restarting.
11. Verify sidecar shutdown leaves no Python child process behind.
12. Repeat with the game paused, in the menu, and during dense combat radio traffic.

Acceptance should be based on a bounded sawtooth memory profile: memory may rise during a
worker lifetime, but it must return close to the known baseline when that worker is
recycled and must not trend upward across successive workers.

## Final conclusion

The reported long-session memory growth is reproducible and significant. It is not
caused solely by queued audio, ongoing synthesis while paused, STT, HTTP request data,
the Unity audio cache, or concurrent TTS generation. Repeated serialized Pocket TTS
generation increases native private and resident memory while live Python tensor and
heap measurements remain constant.

Concurrency control is still required for correctness, but the memory problem requires
either an upstream native fix or process-level recycling. A dedicated recyclable TTS
worker provides the best balance: it bounds memory while allowing the parent sidecar and
Whisper voice commands to remain available.

> **Superseded:** the follow-up below pinned the exact mechanism and a much smaller fix
> than process recycling. The conclusions above about *where* the memory goes remain
> correct; the recycling recommendation is no longer necessary.

## Root cause identified and fixed (2026-07-14)

### Mechanism

The leak is **per-thread native cache retention, orphaned on thread exit**. PyTorch's
Windows CPU stack (Intel MKL's fast memory manager, plus Intel OpenMP / oneDNN
per-thread state; torch 2.12.1+cpu is built with `USE_MKL=ON` and ships
`libiomp5md.dll`) caches scratch buffers **per OS thread** and never releases a
thread's buffers when that thread exits. Any torch work executed on a short-lived
thread therefore permanently leaks several MB of native memory — invisible to
`gc`/`tracemalloc`, unrecoverable by `_heapmin()`, exactly matching every observation
above.

The sidecar created up to three throwaway threads per synthesized line:

1. Pocket TTS spawns a fresh **decoder thread** inside every `generate_audio()` call
   (`pocket_tts/models/tts_model.py`, `_generate_audio_stream_short_text`);
2. it spawns a fresh **autoregressive generation thread** per call (`_generate`); and
3. `ThreadingHTTPServer` runs each `/speak` handler — which executes the text-prompt
   transformer pass, `torch.cat`, and the numpy WAV conversion — on a fresh
   **per-request handler thread**.

This also explains why the serialized HTTP test (three dying threads per call) grew
faster than the direct-library test (two dying threads per call; the probe's main
thread was persistent).

### Confirming experiments

A synthetic probe ran an identical torch workload (GEMMs + gelu, `torch.set_num_threads(1)`)
60 times per mode and measured process memory via `GetProcessMemoryInfo`:

| Mode | Private-bytes growth |
| --- | ---: |
| Same thread (inline) | **0.0 MB** |
| Fresh `threading.Thread` per iteration | **+379 MB** (+6.4 MB/iter, unbounded) |
| Fresh thread + `MKL_DISABLE_FAST_MM=1` | +116 MB (+2.0 MB/iter — MKL is the largest, not the only, per-thread cache) |
| Persistent 2-worker pool | **0.0 MB** |
| Fresh *empty* thread per iteration (no torch work) | 0.0 MB |

The same A/B against real Pocket TTS generation (English model, `vera` voice, fixed
sentence, 4 warmup + 20 measured calls):

| Mode | Private-bytes growth over 20 calls |
| --- | ---: |
| Stock pocket-tts 2.1.0 | +52.5 MB (+2.63 MB/call, matching the ~2.8 MB/call above) |
| Internal threads routed to a persistent pool | **−24 MB (no growth; fluctuation only)** |

Generation speed was unchanged (marginally faster with warm caches: 1.45s vs 1.47s
mean per call).

### The fix

`sidecar/server.py` now keeps all native inference on persistent threads:

- a `_ThreadingProxy`/`_PooledThread` shim replaces `threading.Thread` **inside
  `pocket_tts.models.tts_model` only**, so the per-call decoder and generation threads
  run on a persistent 3-worker pool instead of being created and destroyed each call;
- `/speak` executes `engine.speak` on a single persistent `TTS_EXECUTOR` worker rather
  than on the per-request HTTP handler thread — this simultaneously serializes
  generation (Pocket TTS documents `generate_audio` as not thread-safe; overlapping
  requests now queue FIFO), fixing the separate concurrency defect above; and
- `/transcribe` likewise runs on a single persistent `STT_EXECUTOR` worker.

No library files are modified; `pocket-tts==2.1.0` stays pinned. Process recycling is
not required. `MKL_DISABLE_FAST_MM` is deliberately **not** set: with persistent
threads the MKL buffer cache is bounded and beneficial.

Residual, bounded, non-growing allocations remain expected: the one-shot model-loader
thread, the HTTP handler threads (pure Python after the fix — measured leak-free), and
the fixed set of pooled worker threads.

