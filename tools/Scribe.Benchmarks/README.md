# Scribe.Benchmarks

BenchmarkDotNet micro-benchmarks for Scribe's hot paths, plus a `--soak` mode that runs the
shipping dictation tail repeatedly at normal priority. Nothing here touches a microphone, the
clipboard or keyboard input, and every input is synthetic.

```powershell
# All micro-benchmarks (Release is required by BenchmarkDotNet). Run from inside the repository:
# BenchmarkDotNet looks for the project file from the current directory. --artifacts <folder>
# sends its results and logs somewhere other than .\BenchmarkDotNet.Artifacts.
dotnet run -c Release --project tools/Scribe.Benchmarks

# One class, with tighter confidence intervals than the default ShortRun job
dotnet run -c Release --project tools/Scribe.Benchmarks -- --filter *ProcessDetailedBenchmarks* --iterationCount 12

# The soak (defaults shown); it writes only to its own %TEMP% folder
dotnet run -c Release --project tools/Scribe.Benchmarks -- --soak --cycles 300 --capture-seconds 8 --report-every 25
```

## What the micro-benchmarks cover

| Class | Measures |
| --- | --- |
| `HotPathBenchmarks` | cleanup chunking, `AudioCaptureService.ReadAll`, a 100-rule dictionary pass, history audio serialization |
| `ProcessDetailedBenchmarks` | `TextPostProcessor.ProcessDetailed` for short and long text, a 20-rule and an every-library (about 1,500 rule) dictionary, a source identical to the text or a raw transcript behind cleaned text, and snippets on or off. The `FullRescan` baseline arm is the original algorithm, which normalizes and scans the source a second time even when it is the text itself; `ProcessDetailed` is what ships and reuses that work when the source is identical. Both return identical results (pinned by `PostProcessorSourcePassTests`). |
| `CaptureAssemblyBenchmarks` | allocations of assembling a capture from synthetic 48 kHz stereo float packets and converting it to 16 kHz mono. `FreshReservation` is the former shape (a new 30-second `MemoryStream` per capture); `ReusedBuffer` is the shipping `CaptureBufferPool` path. At 40 s both arms outgrow the reservation, and the pool deliberately drops a grown buffer rather than keep it. |

## Which architecture actually ran

BenchmarkDotNet builds a generated project and runs the result in a separate process through
`dotnet`. In 0.15.8 that is the `dotnet` found on `PATH` unless the toolchain names one
(`DotNetCliCommandExecutor.BuildStartInfo` and `GetDefaultDotNetCliPath`). The generated project's
`PlatformTarget` is the job platform (`CsProjGenerator` fills `$PLATFORM$` in
`Templates/CsProj.txt`), which defaults to the architecture of the process running BenchmarkDotNet
(`EnvironmentResolver` registers `RuntimeInformation.GetCurrentPlatform`). The hardcoded `win-x64`
RuntimeIdentifier therefore did not prove what executed: an x64 host emulated on an Arm64 PC built
and ran x64 through whatever `dotnet` `PATH` resolved, and an Arm64-native host paired `win-x64`
with an `ARM64` PlatformTarget, a combination the SDK rejects (NETSDK1032).

`Program.cs` now derives all three from the process running the tool: the RuntimeIdentifier
(`win-x64` or `win-arm64`), the job platform (`x64` or `ARM64`), and the `dotnet.exe` that belongs
to the running runtime, which BenchmarkDotNet then uses to build and to run each benchmark. It
prints the host's process and OS architecture, whether Windows is emulating it, and its priority
at startup and again with the results. Each benchmark process also reports its own environment
(a module initializer in `ExecutionEnvironment.cs`, so every benchmark class is covered without
opting in), shown in the `ExecEnv` column; that column, not the host line, is the evidence of what
was measured. A host running emulated (an x64 SDK on an Arm64 PC) is flagged: the benchmarks then
run emulated too, which is not what a native install executes.

## Priority: these are not coexistence measurements

BenchmarkDotNet 0.15.8 raises every out-of-process benchmark to **High** priority:
`DotNetCliExecutor.Execute` calls `ProcessExtensions.EnsureHighPriority` right after starting the
process. The in-process toolchains raise the process to High and the benchmark thread to Highest for
the duration of the run. The `ExecEnv` column shows the priority the benchmark process observed.

So a BenchmarkDotNet number is the cost of the code with the scheduler on its side. It says nothing
about how Scribe behaves at Normal priority while the user's other applications compete for the
same cores. Use `--soak`, which runs in this process at the priority it was started with, or the
thread sweep in `tools/Scribe.AsrCheck`, for that.

## Power plan: left as the machine has it

By default BenchmarkDotNet 0.15.8 also switches the **whole machine** to the High performance power
plan for the duration of a run and switches it back afterwards (`PowerManagementApplier`, whose
default comes from `EnvironmentResolver`). That changes clock and core-parking behavior for every
application, not just the benchmark, and a user's PC normally runs Balanced. This tool's job opts out
with `DontEnforcePowerPlan()`, so benchmarks measure under whatever plan is active and never change
it. The active plan is printed as `power=` in every environment line, the host's and each benchmark
process's (`ExecEnv` column), so a result always says which plan it was measured under.

## The soak

Each cycle is one synthetic dictation through shipping code:

1. 10 ms device-format packets appended through `AudioCaptureService.AppendChunk` into a
   `CaptureBufferPool` recording, the same members the WASAPI callback uses;
2. `AudioCaptureService.ConvertCapture`, the conversion `Stop` uses (signal analysis, downmix,
   resample to 16 kHz mono);
3. `TextPostProcessor.ProcessDetailed` with the every-library dictionary and snippets, alternating
   short and long text and identical and raw sources;
4. `HistoryRepository.Add` with the audio into a throwaway SQLite database in a unique
   `%TEMP%\scribe-soak-<guid>` folder, deleted at the end (a first Ctrl+C stops after the current
   cycle and still deletes it), pruned every `--prune-every` cycles to the newest `--keep-rows`
   entries through `PruneOlderThan`. Nothing is written to the working directory.

Every `--report-every` cycles it prints p50/p95/max per stage, CPU and allocated bytes per cycle,
private bytes, working set, handles, threads, GC collections and pause time for the window, managed
memory still live after a full collection, and the capture buffer the pool retains. The closing
summary gives the change and least-squares trend from the second window on (the first carries JIT and
cold-cache cost), so steady growth in private bytes, live memory, handles or threads stands out.

Options: `--dictionary small`, `--no-snippets`, `--no-audio-history`, `--release-buffers-every N`
(exercises `CaptureBufferPool.ReleaseRetained`, what an idle release would call), `--per-cycle`
(CSV line per cycle). Output is numbers and enum names only: no text, user paths or device names;
the database folder is shown only as `%TEMP%\scribe-soak-<guid>`.
