using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

/// <summary>
/// Adds per-code columns to a corpus benchmark, where one operation encodes or decodes every code of a version range.
/// Mean and Allocated are per operation, so without these a whole range reads as if one code took that long.
/// </summary>
public abstract class CorpusCodesConfig : ManualConfig
{
    protected CorpusCodesConfig(Func<BenchmarkCase, int> codesPerOperation)
    {
        AddColumn(new PerCodeColumn("Mean/code", "Mean divided by the codes one operation handles", 0, codesPerOperation,
            (summary, benchmarkCase) => summary[benchmarkCase]?.ResultStatistics?.Mean, FormatTime));
        AddColumn(new PerCodeColumn("Alloc/code", "Allocated divided by the codes one operation handles", 1, codesPerOperation,
            (summary, benchmarkCase) => summary[benchmarkCase]?.GcStats.GetBytesAllocatedPerOperation(benchmarkCase), FormatBytes));
    }

    protected static CorpusVersionRange RangeOf(BenchmarkCase benchmarkCase) => (CorpusVersionRange)benchmarkCase.Parameters["Range"];

    private static string FormatTime(double nanoseconds) => nanoseconds < 1_000 ? $"{nanoseconds:N1} ns" : $"{nanoseconds / 1_000:N2} us";

    private static string FormatBytes(double bytes) => $"{bytes:N0} B";

    private sealed class PerCodeColumn(
        string name,
        string legend,
        int priority,
        Func<BenchmarkCase, int> codesPerOperation,
        Func<Summary, BenchmarkCase, double?> perOperation,
        Func<double, string> format) : IColumn
    {
        public string Id => nameof(PerCodeColumn) + "." + name;
        public string ColumnName => name;
        public string Legend => legend;
        public bool AlwaysShow => true;
        public ColumnCategory Category => ColumnCategory.Custom;
        public int PriorityInCategory => priority;
        public bool IsNumeric => true;
        public UnitType UnitType => UnitType.Dimensionless;

        public bool IsAvailable(Summary summary) => true;
        public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;
        public string GetValue(Summary summary, BenchmarkCase benchmarkCase) => GetValue(summary, benchmarkCase, SummaryStyle.Default);

        public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
        {
            var value = perOperation(summary, benchmarkCase);
            return value is { } total ? format(total / codesPerOperation(benchmarkCase)) : "NA";
        }
    }
}
