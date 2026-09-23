using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace Scribe.Benchmarks;

/// <summary>
/// Shows, per benchmark, the environment the benchmark process reported about itself. The host's
/// own architecture and priority say nothing reliable about the child: BenchmarkDotNet starts a
/// separate process and raises it to High priority right after starting it.
/// </summary>
internal sealed class ExecutionEnvironmentColumn : IColumn
{
    public string Id => nameof(ExecutionEnvironmentColumn);

    public string ColumnName => "ExecEnv";

    public bool AlwaysShow => true;

    public ColumnCategory Category => ColumnCategory.Job;

    public int PriorityInCategory => 100;

    public bool IsNumeric => false;

    public UnitType UnitType => UnitType.Dimensionless;

    public string Legend =>
        "Architecture, emulation and priority reported by the benchmark process itself (not the host)";

    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

    public bool IsAvailable(Summary summary) => true;

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase) =>
        FindMarker(summary[benchmarkCase]) ?? "not reported";

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style) =>
        GetValue(summary, benchmarkCase);

    public override string ToString() => ColumnName;

    /// <summary>The compact environment from the marker line, or null when the process wrote none.</summary>
    internal static string? FindMarker(BenchmarkReport? report)
    {
        if (report is null)
        {
            return null;
        }

        foreach (var result in report.ExecuteResults)
        {
            foreach (var line in result.StandardOutput)
            {
                if (!line.StartsWith(ExecutionEnvironment.MarkerPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var body = line[ExecutionEnvironment.MarkerPrefix.Length..];
                var separator = body.IndexOf('|', StringComparison.Ordinal);
                return (separator >= 0 ? body[..separator] : body).Trim();
            }
        }

        return null;
    }
}
