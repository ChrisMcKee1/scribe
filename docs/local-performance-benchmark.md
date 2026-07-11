# Local Performance Benchmark

Updated July 10, 2026.

This report measures local CPU and managed allocation costs in Scribe's production code. It uses a
dedicated BenchmarkDotNet 0.15.8 project, .NET 10.0.9, Release builds, out-of-process execution,
and the memory diagnoser. The benchmark project is `tools/Scribe.Benchmarks`.

## Result

| Production path | Baseline mean | Optimized mean | Time change | Baseline allocation | Optimized allocation | Allocation change |
|---|---:|---:|---:|---:|---:|---:|
| 10-second audio aggregation | 164.43 us | 69.33 us | **-57.8%** | 2,625.29 KB | 625.12 KB | **-76.2%** |
| 48k-character cleanup chunking | 38.71 us | 30.47 us | **-21.3%** | 375.93 KB | 190.96 KB | **-49.2%** |
| 10-second audio serialization round trip | 71.45 us | 69.65 us | -2.5% | 1,250.11 KB | 1,250.11 KB | 0% |
| 100-rule dictionary processing | 117.97 us | 115.99 us | -1.7% | 213.80 KB | 213.80 KB | 0% |

Short-run timing has wider confidence intervals than a publication benchmark, so the allocation
results and repeated focused runs are the primary evidence. The two optimized methods produced large,
repeatable improvements; movement in the two unchanged controls is ordinary run-to-run variance.

## Changes Adopted

`AudioCaptureService.ReadAll` previously appended each provider read to a dynamically growing
`List<float>` and then copied the list into the returned array. A 160,000-sample capture allocated
about four times the 625 KB result payload. It now grows temporary storage through
`ArrayPool<float>.Shared` and materializes only the exact returned array. A unit test forces growth
past the initial one-second buffer and verifies every sample survives unchanged.

`TextCleanupService.ChunkForCleanup` previously allocated a target-sized window string for each
boundary search, then allocated substring and trim results for returned chunks. It now searches and
trims `ReadOnlySpan<char>` windows and creates each final chunk string once. All punctuation,
whitespace fallback, and hard-split tests remain unchanged.

## Changes Rejected

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

The benchmark job passes `/p:RuntimeIdentifier=win-x64` to both restore and build. This is required
because BenchmarkDotNet's generated .NET 10 Windows project otherwise restores only the framework
target while the Scribe project reference requests the `win-x64` target.

Raw reports from this run are under `artifacts/performance/baseline` and
`artifacts/performance/optimized`.
