# Local Performance Benchmark

Updated October 23, 2026, for 0.5.13, with speech recognition, `MAI-Transcribe-2` and Copilot+ PC NPU measurements.

This report measures local CPU and managed allocation costs in Scribe's production code. It uses a
dedicated BenchmarkDotNet 0.15.8 project, Release builds, out-of-process execution, and the memory
diagnoser. The benchmark project is `tools/Scribe.Benchmarks`.

## 0.5.1 against 0.5.0

Release 0.5.0 (`10c9a0b`) and 0.5.1 were built and run back to back in one sitting on one PC: AMD Ryzen 9
9900X (12 cores, 24 logical), 95.4 GB RAM, Windows 11 25H2 (10.0.26200), .NET SDK 10.0.401 and runtime
10.0.12, High performance power plan (BenchmarkDotNet processes at High priority, the soak and the scenario
suite at Normal). Times are BenchmarkDotNet's default job (capped at 40 iterations and 12 warmups), mean and
error; allocations are exact. The PC was shared: a control benchmark whose code is identical in both builds
moved 20.8%, so treat a time difference smaller than that as noise.

| Production path | 0.5.0 | 0.5.1 | Time | Allocation |
|---|---:|---:|---:|---:|
| Capture of 25 s at 48 kHz stereo (`CaptureAssemblyBenchmarks`) | 19.40 ± 0.71 ms, 39.29 MB | 17.74 ± 1.07 ms, 1.91 MB | -8.6% | **-95.1%** |
| Capture of 40 s | 35.27 ± 1.02 ms, 119.18 MB | 32.22 ± 0.69 ms, 24.82 MB | -8.6% | **-79.2%** |
| Dictionary and every word pack, short dictation, snippets on (`ProcessDetailed`) | 454.2 ± 11.8 us, 921.65 KB | 220.6 ± 7.0 us, 10.02 KB | **-51.4%** | **-98.9%** |
| The same, long dictation (about 550 words) | 1.055 ± 0.086 ms, 1.06 MB | 0.770 ± 0.040 ms, 134.58 KB | **-27.0%** | **-87.6%** |
| 100-rule dictionary processing (`ProcessDictionary`) | 225.2 ± 13.6 us, 285.03 KB | 129.7 ± 8.1 us, 154.39 KB | **-42.4%** | **-45.8%** |
| Word packs' first status, 1,549 terms (`LibraryCompositionBenchmarks`) | 792.1 ± 44.7 us, 2.24 MB | 666.3 ± 48.0 us, 1.16 MB | -15.9% | **-48.1%** |
| Sorting a 10,000-term word pack (`LibrarySearchBenchmarks`) | 8.127 ± 0.605 ms, 578.91 KB | 6.894 ± 0.330 ms, 165.27 KB | -15.2% | **-71.5%** |

End to end:

- **Soak** (100 dictations of 8 s at 48 kHz stereo, a large dictionary, snippets and audio history on,
  `--soak`): 9.19 MB allocated per dictation in 0.5.0, 1.18 MB in 0.5.1 (-87.2%). After the first 25
  dictations 0.5.0 ran 7 to 11 generation 2 collections per 25 dictations and 0.5.1 none; private bytes at
  the window ends swung between 64 and 137 MB in 0.5.0 and stayed between 69 and 79 MB in 0.5.1.
- **Full scenario suite** (real speech through the production recognizer, `--scenarios`): the recognized
  and final text are identical in all 59 scenarios that produce text, and the pipeline allocates 269.4 MB
  against 361.8 MB (-25.5%).
- **Idle release** (`--idle-release`, six fresh processes each): 0.5.1's Aggressive collection leaves
  54.1 MiB private bytes against 212.6 MiB for 0.5.0's Forced collection (`ForcedIdleGc`), for a longer
  one-off pause, 18.9 ms against 6.6 ms, taken while nobody dictates.

The same sitting also ran 0.5.1 on the .NET 11 release candidate (runtime 11.0.0-rc.1). Allocations were
the same; most times moved within the noise above, and the one difference that repeated was a slower long
dictation with snippets on (+20.8%, then +24.0%). Scribe stays on .NET 10, a long-term support release.

### Speech recognition examples

The README quotes these. They come from one quick run of the scenario suite on the same PC on September
29, 2026, started at 01:17 local time (06:17 UTC): a Release build of 0.5.1 (`9cd88748`), .NET 10.0.12,
Normal priority, eight speech recognition threads (the automatic choice on this CPU), Parakeet TDT 0.6b
v3 (int8) through sherpa-onnx 1.13.8, and the generated English speech committed under
`tests/fixtures/speech`. The model loaded once, in 2,083.8 ms, before the scenarios. Each time below is
speech recognition alone (the report's `decodeMs`), after silence trimming, which is timed separately;
neither includes model loading, dictionary processing, AI cleanup or typing. It is one run on a shared
PC, so treat the times as examples rather than a distribution.

```powershell
dotnet run --project tools/Scribe.AsrCheck -c Release -- --scenarios --quick
```

| Sample | Audio | Silence trimming | Speech recognition |
|---|---:|---:|---:|
| `A-clean-sentence` | 4.00 s | 15.5 ms | 112.0 ms |
| `A-clean-dict-github-copilot` | 3.99 s | 12.5 ms | 118.5 ms |
| `A-clean-dict-kubernetes` | 4.15 s | 15.8 ms | 121.4 ms |
| `A-clean-greeting` | 4.16 s | 18.2 ms | 128.0 ms |
| `A-clean-dict-scribe` | 3.97 s | 13.5 ms | 133.9 ms |
| `A-clean-long-passage` | 23.26 s | 74.2 ms | 639.3 ms |

### October 23, 2026 verification (`Scribe.AsrCheck --scenarios --quick` and `MaiTranscribeBenchmarks`)

Re-measured on October 23, 2026 on the same AMD Ryzen 9 9900X desktop (Windows 11 build 26300,
.NET 10.0.13, Release build):

- **`Scribe.AsrCheck -c Release -- --scenarios --quick`**: 49 scenarios, **44 passed, 0 failed**,
  5 reported only; **201 checks, 0 failed** in 26.1 s total. Parakeet TDT 0.6B v3 decoded the 4.0 s
  `A-clean-sentence` in **119 ms** (RTF 0.04), `A-clean-dict-github-copilot` in **117 ms** (RTF
  0.04), `A-clean-long-passage` (23.3 s) in **638 ms** (RTF 0.03), and 20 rapid short dictations in
  a **96 ms median / 128 ms P95** end-to-end loop with zero memory growth (1,517 MB private before
  and after).
- **Live Microsoft Foundry Speech (`MAI-Transcribe-2` / Fast Transcription, South Central US)**:
  Across all 14 committed WAV fixtures (`1.92 s` to `23.26 s`) with `phraseList` vocabulary biasing,
  round-trip HTTPS recognition completed in a **710 ms median (P50)** and **1,571 ms P95** (mean
  760.9 ms, RTF 0.07 to 0.36).
- **`MaiTranscribeBenchmarks` (`--scribe-default-job`, BenchmarkDotNet 0.15.8)**:

| Method | Mean | Error | StdDev | Median | Allocated |
|---|---:|---:|---:|---:|---:|
| `EncodeWav4Seconds` (`MaiWavEncoder.EncodePcm16Wav`, 4 s @ 16 kHz) | 66.34 us | 1.326 us | 3.673 us | 66.78 us | 125.09 KB |
| `EncodeWav23Seconds` (`MaiWavEncoder.EncodePcm16Wav`, 23 s @ 16 kHz) | 463.91 us | 9.252 us | 17.378 us | 460.99 us | 718.93 KB |
| `BuildDefinition500Phrases` (`MaiTranscribeClient.BuildDefinitionJson`, 500 terms) | 11.63 us | 0.533 us | 1.571 us | 12.06 us | 55.20 KB |

## What this does not measure

VAD model execution and speech recognition are not in the BenchmarkDotNet microbenchmarks; the speech
samples above come from the scenario suite instead. Judging a change to VAD window handling or native
inference needs scenario profiling with the actual native models and representative audio: use
`dotnet-counters` or `dotnet-trace` on a Release app session first.

## Reproduce

Run these from inside the repository, in Release. Every filter below names a benchmark class from the first
table; `--filter "*"` runs them all, and `--artifacts <folder>` puts BenchmarkDotNet's output elsewhere.

```powershell
dotnet run --project tools/Scribe.Benchmarks/Scribe.Benchmarks.csproj -c Release -- --filter "*CaptureAssemblyBenchmarks*" --artifacts artifacts/performance/capture
dotnet run --project tools/Scribe.Benchmarks/Scribe.Benchmarks.csproj -c Release -- --filter "*ProcessDetailedBenchmarks*" --artifacts artifacts/performance/dictionary
dotnet run --project tools/Scribe.Benchmarks/Scribe.Benchmarks.csproj -c Release -- --filter "*LibraryCompositionBenchmarks*" --artifacts artifacts/performance/word-packs
```

These use BenchmarkDotNet's quick ShortRun job, which is right for a first look but can report error
bars wider than the difference being measured. For any number you quote, add `--scribe-default-job`
to use BenchmarkDotNet's default job, and report Mean, Error, StdDev and Allocated:

```powershell
dotnet run --project tools/Scribe.Benchmarks/Scribe.Benchmarks.csproj -c Release -- --scribe-default-job --filter "*CaptureAssemblyBenchmarks*" --artifacts artifacts/performance/capture
```

The end-to-end figures come from the soak, the idle release check (once as shipped, once with
`ForcedIdleGc`, 0.5.0's collection) and the scenario suite:

```powershell
dotnet run -c Release --project tools/Scribe.Benchmarks -- --soak --cycles 100
dotnet run -c Release --project tools/Scribe.Benchmarks -- --idle-release
dotnet run -c Release --project tools/Scribe.Benchmarks -- --idle-release --flags ForcedIdleGc
dotnet run --project tools/Scribe.AsrCheck -c Release -- --scenarios
```

The benchmark job passes `/p:RuntimeIdentifier=<host RID>` (`win-x64` or `win-arm64`, matching the host
process) to both restore and build. This is required because BenchmarkDotNet's generated .NET 10 Windows
project otherwise restores only the framework target while the Scribe project reference requests the
RID-specific target.
