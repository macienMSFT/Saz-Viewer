using SazViewer.Core;

namespace SazViewer.Tests;

public sealed class BodyFormatterTests
{
    private readonly BodyFormatter formatter = new();

    [Fact]
    public void DetectsAndPrettyPrintsJsonWhenContentTypeIsWrong()
    {
        const string raw = """{"name":"value","items":[1,true,null]}""";

        var result = formatter.Format(TextBody(raw), "text/plain");

        Assert.Equal(BodyFormat.Json, result.Format);
        Assert.Equal("JSON", result.Label);
        Assert.Contains("Detected and parsed as JSON", result.Status, StringComparison.Ordinal);
        Assert.Contains("\n", result.Formatted, StringComparison.Ordinal);
        Assert.Contains("\"items\": [", result.Formatted, StringComparison.Ordinal);
        Assert.Equal(raw, result.Raw);
        Assert.True(result.CanToggle);
    }

    [Fact]
    public void DetectsAndPrettyPrintsXmlWithoutContentType()
    {
        const string raw = """<root><item key="a">value</item></root>""";

        var result = formatter.Format(TextBody(raw), null);

        Assert.Equal(BodyFormat.Xml, result.Format);
        Assert.Equal("XML", result.Label);
        Assert.Contains("Detected and parsed as XML", result.Status, StringComparison.Ordinal);
        Assert.Contains("\n", result.Formatted, StringComparison.Ordinal);
        Assert.Contains("<item key=\"a\">value</item>", result.Formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void FallsBackToOriginalTextForMalformedStructuredBodies()
    {
        const string malformedJson = """{"broken":]""";
        const string malformedXml = "<root><broken></root>";

        var json = formatter.Format(TextBody(malformedJson), "application/json");
        var xml = formatter.Format(TextBody(malformedXml), "application/xml");

        Assert.Equal(BodyFormat.Text, json.Format);
        Assert.Equal("Text (invalid JSON)", json.Label);
        Assert.Equal(malformedJson, json.Formatted);
        Assert.Contains("parsing failed", json.Status, StringComparison.Ordinal);
        Assert.Equal(BodyFormat.Text, xml.Format);
        Assert.Equal("Text (invalid XML)", xml.Label);
        Assert.Equal(malformedXml, xml.Formatted);
        Assert.Contains("secure parsing failed", xml.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void DoesNotParseTruncatedStructuredPreview()
    {
        var result = formatter.Format(
            TextBody("""{"partial":{"value":1""", truncated: true),
            "application/json");

        Assert.Equal(BodyFormat.Text, result.Format);
        Assert.Equal("JSON preview (truncated)", result.Label);
        Assert.Contains("formatting was skipped", result.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void KeepsBinaryHexPreviewUnchanged()
    {
        var body = new BodyPreview
        {
            Length = 4,
            IsBinary = true,
            Preview = "00000000  00 FF 01 02"
        };

        var result = formatter.Format(body, "application/json");

        Assert.Equal(BodyFormat.Binary, result.Format);
        Assert.Equal(body.Preview, result.Formatted);
        Assert.False(result.CanToggle);
    }

    [Fact]
    public void FormatsJsonAfterAUnicodeBomWhilePreservingRawText()
    {
        const string raw = "\uFEFF{\"value\":1}";

        var result = formatter.Format(TextBody(raw), "application/json; charset=utf-8");

        Assert.Equal(BodyFormat.Json, result.Format);
        Assert.Equal(raw, result.Raw);
        Assert.DoesNotContain("\uFEFF", result.Formatted, StringComparison.Ordinal);
        Assert.Contains("\"value\": 1", result.Formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsXmlDocumentTypeDeclarations()
    {
        const string raw = "<!DOCTYPE root [<!ENTITY x SYSTEM \"file:///windows/win.ini\">]><root>&x;</root>";

        var result = formatter.Format(TextBody(raw), "application/xml");

        Assert.Equal(BodyFormat.Text, result.Format);
        Assert.Equal(raw, result.Raw);
        Assert.Contains("secure parsing failed", result.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void FallsBackToRawWhenStructuredFormattingWouldAmplifyOutput()
    {
        var json = new string('[', 64) + "0" + new string(']', 64);
        var xml = string.Concat(Enumerable.Range(0, 70).Select(index => $"<n{index}>"))
            + "value"
            + string.Concat(Enumerable.Range(0, 70).Reverse().Select(index => $"</n{index}>"));

        var jsonResult = formatter.Format(TextBody(json), "application/json");
        var xmlResult = formatter.Format(TextBody(xml), "application/xml");

        Assert.Equal(BodyFormat.Json, jsonResult.Format);
        Assert.False(jsonResult.CanToggle);
        Assert.Equal(json, jsonResult.Formatted);
        Assert.Contains("formatting expansion limits", jsonResult.Status, StringComparison.Ordinal);
        Assert.Equal(BodyFormat.Xml, xmlResult.Format);
        Assert.False(xmlResult.CanToggle);
        Assert.Equal(xml, xmlResult.Formatted);
        Assert.Contains("safe formatting depth", xmlResult.Status, StringComparison.Ordinal);
    }

    private static BodyPreview TextBody(string preview, bool truncated = false) =>
        new()
        {
            Length = preview.Length,
            IsTruncated = truncated,
            Preview = preview
        };
}
