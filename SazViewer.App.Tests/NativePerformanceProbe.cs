using System.Diagnostics;
using System.Windows.Controls;
using SazViewer.App.ViewModels;
using SazViewer.App.Views;
using Xunit.Abstractions;

namespace SazViewer.App.Tests;

/// <summary>
/// Local performance probe for a real capture; skipped unless <c>SAZVIEWER_PERF_CAPTURE</c> names a plain .saz file.
/// </summary>
public sealed class NativePerformanceProbe(ITestOutputHelper output)
{
    [Fact]
    public async Task MeasureOpenAndSessionLoad()
    {
        var path = Environment.GetEnvironmentVariable("SAZVIEWER_PERF_CAPTURE");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return;
        }

        var total = Stopwatch.StartNew();
        var document = ReportBuilder.Build(path, false, new QueuePasswordProvider());
        output.WriteLine($"parse: {total.ElapsedMilliseconds} ms");
        var warm = Stopwatch.StartNew();
        var eager = new SazViewer.Core.SazParser().Parse(path);
        output.WriteLine($"eager parse (CLI path): {warm.ElapsedMilliseconds} ms");
        warm.Restart();
        var deferred = new SazViewer.Core.SazParser { DeferBodyDecoding = true }.Parse(path);
        output.WriteLine($"deferred parse: {warm.ElapsedMilliseconds} ms");
        warm.Restart();
        SazViewer.Core.SazParser.CompleteDeferred(deferred);
        output.WriteLine($"complete deferred: {warm.ElapsedMilliseconds} ms");
        Assert.Equal(eager.Warnings, deferred.Warnings);
        await document.StartDeferredWork();
        Assert.Equal(eager.Warnings.Count, document.WarningCount);
        total.Restart();
        StaRunner.Run(() =>
        {
            var step = Stopwatch.StartNew();
            var model = new CaptureViewModel(document.Report);
            output.WriteLine($"view-model: {step.ElapsedMilliseconds} ms");
            step.Restart();
            var view = new CaptureView { DataContext = model };
            var window = NativeViewSmokeTests.Host(view);
            output.WriteLine($"grid rendered: {step.ElapsedMilliseconds} ms (total {total.ElapsedMilliseconds} ms)");
            try
            {
                var grid = (DataGrid)view.FindName("SessionGrid");
                var worst = 0L;
                var sum = 0L;
                var count = Math.Min(grid.Items.Count, 300);
                for (var i = 0; i < count; i++)
                {
                    step.Restart();
                    grid.SelectedIndex = i;
                    StaRunner.DoEvents();
                    worst = Math.Max(worst, step.ElapsedMilliseconds);
                    sum += step.ElapsedMilliseconds;
                }
                output.WriteLine($"session open (incl. render): avg {sum / Math.Max(count, 1)} ms, worst {worst} ms over {count}");
                GC.Collect();
                output.WriteLine($"managed heap: {GC.GetTotalMemory(true) / (1024 * 1024)} MiB");
                step.Restart();
                var second = NativeViewSmokeTests.Host(new CaptureView { DataContext = new CaptureViewModel(document.Report) });
                output.WriteLine($"second grid rendered (warm): {step.ElapsedMilliseconds} ms");
                second.Close();
            }
            finally
            {
                window.Close();
            }
        });
    }
}

