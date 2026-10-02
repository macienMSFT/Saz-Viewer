using System.Globalization;
using System.Text;
using SazViewer.App.Model;

namespace SazViewer.App.ViewModels;

/// <summary>
/// The HexView tab (the report's <c>createHexView</c> / <c>renderHexView</c>): offsets, 16-byte rows and an
/// ASCII gutter over the retained Captured or Decoded body bytes, limited to the report's 1,024-byte prefix.
/// </summary>
internal sealed class HexViewModel : TabContentViewModel
{
    public const int MaxBytes = 1024;
    public const string HeaderRow = "Offset    00 01 02 03 04 05 06 07  08 09 0A 0B 0C 0D 0E 0F  ASCII";

    private readonly MessageContent content;
    private bool isDecoded;
    private string text = "";
    private TextDocumentViewModel document = null!;
    private string status = "";

    public HexViewModel(MessageContent content)
    {
        this.content = content;
        CanShowDecoded = content.Decoded is not null;
        CanShowCaptured = !content.Body.SourceIsDecoded;
        isDecoded = content.Body.SourceIsDecoded && CanShowDecoded;
        RemovedEncodings = content.HasRemovedEncodings ? string.Join(" -> ", content.Body.RemovedEncodings) : null;
        Render();
    }

    public bool CanShowDecoded { get; }
    public bool CanShowCaptured { get; }

    /// <summary>"a -> b" when content codings were removed (shown as the decode status line).</summary>
    public string? RemovedEncodings { get; }

    public string? RemovedEncodingsText => RemovedEncodings is null ? null : $"Removed encodings: {RemovedEncodings}";

    public static IReadOnlyList<string> SourceOptions { get; } = ["Captured", "Decoded"];

    public string SelectedSource
    {
        get => isDecoded ? "Decoded" : "Captured";
        set => IsDecoded = value == "Decoded";
    }

    public bool IsDecoded
    {
        get => isDecoded;
        set
        {
            var decoded = value && CanShowDecoded;
            if (decoded == isDecoded)
            {
                return;
            }
            isDecoded = decoded;
            Render();
            OnPropertyChanged(nameof(IsDecoded));
            OnPropertyChanged(nameof(IsCaptured));
            OnPropertyChanged(nameof(SelectedSource));
            OnPropertyChanged(nameof(SourceLabel));
            OnPropertyChanged(nameof(DumpAccessibleName));
            OnPropertyChanged(nameof(CopyDescription));
            OnPropertyChanged(nameof(Document));
            OnPropertyChanged(nameof(Status));
            RaiseSearchTargetChanged();
        }
    }

    public bool IsCaptured
    {
        get => !isDecoded;
        set => IsDecoded = !value || !CanShowCaptured;
    }

    public string SourceLabel => $"{SelectedSource} body bytes";

    public string DumpAccessibleName => $"{SelectedSource} body byte hex dump";

    public TextDocumentViewModel Document => document;

    public string Text => text;

    public string Status => status;

    public bool IsTruncated { get; private set; }

    public override string? CopyDescription => $"{SelectedSource.ToLowerInvariant()} body hex view";

    public override ISearchableView SearchTarget => document.Document;

    public override CopyResult GetCopyText() => CopyResult.Bounded(text);

    /// <summary>Port of the report's <c>formatHexDump</c> (en-US digit grouping).</summary>
    public static string Format(ReadOnlySpan<byte> bytes, string source, long total, string? removed)
    {
        var retained = bytes.Length;
        var lines = new StringBuilder()
            .Append(source).Append(" body bytes\n")
            .Append(Count(total)).Append(" total; ").Append(Count(retained)).Append(" retained")
            .Append(retained < total ? " (truncated)" : "").Append('\n');
        if (!string.IsNullOrEmpty(removed))
        {
            lines.Append("Removed encodings: ").Append(removed).Append('\n');
        }
        lines.Append('\n').Append(HeaderRow);
        for (var offset = 0; offset < bytes.Length; offset += 16)
        {
            var count = Math.Min(16, bytes.Length - offset);
            lines.Append('\n').Append(offset.ToString("X8", CultureInfo.InvariantCulture)).Append("  ");
            AppendHexGroup(lines, bytes.Slice(offset, Math.Min(8, count)));
            lines.Append("  ");
            AppendHexGroup(lines, count > 8 ? bytes.Slice(offset + 8, count - 8) : []);
            lines.Append("  |");
            for (var index = 0; index < 16; index++)
            {
                lines.Append(index >= count ? ' ' : bytes[offset + index] is >= 32 and <= 126 ? (char)bytes[offset + index] : '.');
            }
            lines.Append('|');
        }
        if (retained < total)
        {
            lines.Append("\n\n[HexView truncated: ").Append(Count(retained)).Append(" of ").Append(Count(total)).Append(" bytes retained.]");
        }
        return lines.ToString();
    }

    private static void AppendHexGroup(StringBuilder lines, ReadOnlySpan<byte> group)
    {
        var start = lines.Length;
        for (var index = 0; index < group.Length; index++)
        {
            if (index > 0)
            {
                lines.Append(' ');
            }
            lines.Append(group[index].ToString("X2", CultureInfo.InvariantCulture));
        }
        lines.Append(' ', 23 - (lines.Length - start));
    }

    private static string Count(long value) => value.ToString("N0", CultureInfo.GetCultureInfo("en-US"));

    private void Render()
    {
        var source = isDecoded ? content.BodyBytes : content.Captured;
        var bytes = source.Span[..Math.Min(source.Length, MaxBytes)];
        long total = isDecoded ? content.Body.Length : content.Body.CapturedLength;
        text = Format(bytes, SelectedSource, total, isDecoded ? RemovedEncodings : null);
        IsTruncated = bytes.Length < total;
        status = IsTruncated
            ? $"Showing a truncated retained prefix ({Count(bytes.Length)} of {Count(total)} bytes)."
            : $"Showing all {Count(total)} bytes.";
        document = new TextDocumentViewModel(LineDocument.FromText(text), GetCopyText);
    }
}
