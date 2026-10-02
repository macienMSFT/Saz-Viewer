using System.Windows;

namespace SazViewer.App.ViewModels;

/// <summary>Result of preparing copy text: either the text or a user-facing error.</summary>
internal readonly record struct CopyResult(string? Text, string? Error)
{
    public const string LimitError = "Copy source exceeds the 1 MiB safety limit.";

    public static CopyResult Of(string? text, string error = LimitError) =>
        text is null ? new CopyResult(null, error) : new CopyResult(text, null);

    public static CopyResult Bounded(string text) =>
        text.Length > Model.MessageContent.MaxCopyCharacters ? new CopyResult(null, LimitError) : new CopyResult(text, null);

    public static CopyResult Fail(string error) => new(null, error);
}

/// <summary>Clipboard abstraction so copy behaviour is testable.</summary>
internal interface IClipboardService
{
    bool TrySetText(string text);
}

internal sealed class WpfClipboardService : IClipboardService
{
    public static readonly WpfClipboardService Instance = new();

    public bool TrySetText(string text)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), true);
                return true;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                Thread.Sleep(30);
            }
        }
        return false;
    }
}
