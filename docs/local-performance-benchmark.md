# Local Performance Benchmark

Updated September 28, 2026, for 0.5.1. The July 2026 run follows it.

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

## July 2026 run

Measured on .NET 10.0.9.

### Result

| Production path | Baseline mean | Optimized mean | Time change | Baseline allocation | Optimized allocation | Allocation change |
|---|---:|---:|---:|---:|---:|---:|
| 10-second audio aggregation | 164.43 us | 69.33 us | **-57.8%** | 2,625.29 KB | 625.12 KB | **-76.2%** |
| 48k-character cleanup chunking | 38.71 us | 30.47 us | **-21.3%** | 375.93 KB | 190.96 KB | **-49.2%** |
| 10-second audio serialization round trip | 71.45 us | 69.65 us | -2.5% | 1,250.11 KB | 1,250.11 KB | 0% |
| 100-rule dictionary processing | 117.97 us | 115.99 us | -1.7% | 213.80 KB | 213.80 KB | 0% |

Short-run timing has wider confidence intervals than a publication benchmark, so the allocation
results and repeated focused runs are the primary evidence. The two optimized methods produced large,
repeatable improvements; movement in the two unchanged controls is ordinary run-to-run variance.

### Changes Adopted

`AudioCaptureService.ReadAll` previously appended each provider read to a dynamically growing
`List<float>` and then copied the list into the returned array. A 160,000-sample capture allocated
about four times the 625 KB result payload. It now grows temporary storage through pooled arrays
(`ArrayPool<float>.Shared`, or for the capture service's own conversions a `CaptureScratchPool` that the idle release
empties) and materializes only the exact returned array. A unit test forces growth
past the initial one-second buffer and verifies every sample survives unchanged.

`TextCleanupService.ChunkForCleanup` previously allocated a target-sized window string for each
boundary search, then allocated substring and trim results for returned chunks. It now searches and
trims `ReadOnlySpan<char>` windows and creates each final chunk string once. All punctuation,
whitespace fallback, and hard-split tests remain unchanged.

### Changes Rejected

Audio serialization was not changed. The benchmark deliberately performs a byte-array to float-array
round trip, so its 1.25 MB allocation is the expected pair of 625 KB payloads. Production persistence
writes and reads occur separately; pooling those retained SQLite values would complicate ownership
without removing the required result allocation.

Dictionary processing was not changed. A deliberately heavy 100-rule, 500-match workload completes
in about 116 us. Replacing regex matching and candidate ordering with a custom scanner would add
behavioral risk for a sub-millisecond path and lacks benchmark evidence of user-visible value.

VAD model execution and ASR inference are intentionally excluded from this microbenchmark. They need
scenario profiling with the actual native models and representative audio. Use `dotnet-counters` or
`dotnet-trace` on a Release app session before changing VAD window handling or native inference code.

## Reproduce

```powershell
dotnet run --project tools/Scribe.Benchmarks/Scribe.Benchmarks.csproj -c Release -- --filter "*" --artifacts artifacts/performance/current --join
```

Use a narrower filter while iterating:

```powershell
dotnet run --project tools/Scribe.Benchmarks/Scribe.Benchmarks.csproj -c Release -- --filter "*ReadAllAudio*" --artifacts artifacts/performance/audio
dotnet run --project tools/Scribe.Benchmarks/Scribe.Benchmarks.csproj -c Release -- --filter "*ChunkLongTranscript*" --artifacts artifacts/performance/chunking
```

These use BenchmarkDotNet's quick ShortRun job, which is right for a first look but can report error
bars wider than the difference being measured. For any number you quote, add `--scribe-default-job`
to use BenchmarkDotNet's default job, and report Mean, Error, StdDev and Allocated:

```powershell
dotnet run --project tools/Scribe.Benchmarks/Scribe.Benchmarks.csproj -c Release -- --scribe-default-job --filter "*ReadAllAudio*" --artifacts artifacts/performance/audio
```

The benchmark job passes `/p:RuntimeIdentifier=win-x64` to both restore and build. This is required
because BenchmarkDotNet's generated .NET 10 Windows project otherwise restores only the framework
target while the Scribe project reference requests the `win-x64` target.

Raw reports from this run are under `artifacts/performance/baseline` and
`artifacts/performance/optimized`.
