using System.Text;
using SazViewer.App.Model;
using SazViewer.App.ViewModels;
using SazViewer.Core;

namespace SazViewer.App.Tests;

public sealed class AdvancedFilterTests
{
    [Fact]
    public void AndBindsTighterThanOrAndParenthesesOverridePrecedence()
    {
        var row = Row(status: 200, method: "GET", url: "https://example.test/plain");
        var result = Field("result");
        var method = Field("method");
        var url = Field("url");
        var precedence = Definition(
            Rule(result, AdvancedFilterOperator.Equals, "200", join: AdvancedFilterJoin.Or),
            Rule(method, AdvancedFilterOperator.Equals, "POST", join: AdvancedFilterJoin.And),
            Rule(url, AdvancedFilterOperator.Contains, "needle"));
        var parenthesized = Definition(
            Rule(result, AdvancedFilterOperator.Equals, "200", open: 1, join: AdvancedFilterJoin.Or),
            Rule(method, AdvancedFilterOperator.Equals, "POST", close: 1, join: AdvancedFilterJoin.And),
            Rule(url, AdvancedFilterOperator.Contains, "needle"));

        Assert.True(Matches(precedence, row));
        Assert.False(Matches(parenthesized, row));
    }

    [Fact]
    public void NestedParenthesesAndDisabledRowsAreHandledAsAbsent()
    {
        var row = Row(status: 204, method: "GET", url: "https://example.test/value");
        var definition = Definition(
            Rule(Field("result"), AdvancedFilterOperator.Between, "200-299", open: 2, join: AdvancedFilterJoin.And),
            Rule(Field("method"), AdvancedFilterOperator.Equals, "POST", enabled: false, open: 99, close: -10, join: AdvancedFilterJoin.Or),
            Rule(Field("method"), AdvancedFilterOperator.IsOneOf, "GET, HEAD", close: 1, join: AdvancedFilterJoin.And),
            Rule(Field("url"), AdvancedFilterOperator.EndsWith, "/value", close: 1));

        Assert.True(Matches(definition, row));
    }

    [Theory]
    [InlineData("Equals", "AlphaBeta", true)]
    [InlineData("NotEquals", "other", true)]
    [InlineData("Contains", "PHAB", true)]
    [InlineData("NotContains", "missing", true)]
    [InlineData("IsIn", "one|AlphaBeta|two", true)]
    [InlineData("IsNotIn", "one|two", true)]
    [InlineData("IsOneOf", "one, AlphaBeta, two", true)]
    [InlineData("IsNotOneOf", "one,two", true)]
    [InlineData("StartsWith", "alpha", true)]
    [InlineData("DoesNotStartWith", "beta", true)]
    [InlineData("EndsWith", "BETA", true)]
    [InlineData("DoesNotEndWith", "alpha", true)]
    [InlineData("MatchesRegex", "^alpha.*beta$", true)]
    public void StringOperatorsUseCaseInsensitiveSemanticsByDefault(
        string operatorName,
        string value,
        bool expected)
    {
        var filterOperator = Enum.Parse<AdvancedFilterOperator>(operatorName);
        var row = Row(headers: [new("X-Test", "AlphaBeta")]);
        var field = AdvancedFilterField.ForColumn(
            SessionColumnCatalog.CreateCustom(SessionColumnSetting.RequestHeaderKind, "X-Test"));

        Assert.Equal(expected, Matches(Definition(Rule(field, filterOperator, value)), row));
    }

    [Fact]
    public void CaseSensitiveToggleAndEmptyOperatorsWork()
    {
        var populated = Row(headers: [new("X-Test", "Alpha")]);
        var blank = Row();
        var field = AdvancedFilterField.ForColumn(
            SessionColumnCatalog.CreateCustom(SessionColumnSetting.RequestHeaderKind, "X-Test"));

        Assert.False(Matches(Definition(Rule(field, AdvancedFilterOperator.Equals, "alpha", caseSensitive: true)), populated));
        Assert.True(Matches(Definition(Rule(field, AdvancedFilterOperator.IsEmpty, "")), blank));
        Assert.True(Matches(Definition(Rule(field, AdvancedFilterOperator.IsNotEmpty, "")), populated));
        Assert.False(Matches(
            Definition(Rule(Field("time"), AdvancedFilterOperator.GreaterThan, "2026-01-01")),
            blank));
    }

    [Fact]
    public void NumericTimeSizeAndStatusValuesUseTypedComparison()
    {
        var row = Row(status: 204);
        row.Session.Timestamp = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        row.Session.ResponseBytes = 2 * 1024 * 1024;
        row.Session.ElapsedMilliseconds = 1_500;

        Assert.True(Matches(Definition(Rule(Field("result"), AdvancedFilterOperator.Between, "200-299")), row));
        Assert.True(Matches(Definition(Rule(Field("response-size"), AdvancedFilterOperator.GreaterThanOrEqual, "2 MB")), row));
        Assert.True(Matches(Definition(Rule(Field("elapsed"), AdvancedFilterOperator.LessThan, "2 s")), row));
        Assert.True(Matches(Definition(Rule(Field("time"), AdvancedFilterOperator.GreaterThan, "2026-10-02 11:59:59Z")), row));
    }

    [Theory]
    [InlineData("Equals", "204", true)]
    [InlineData("NotEquals", "200", true)]
    [InlineData("IsOneOf", "200, 204, 206", true)]
    [InlineData("IsNotOneOf", "200, 201", true)]
    [InlineData("LessThan", "300", true)]
    [InlineData("LessThanOrEqual", "204", true)]
    [InlineData("GreaterThan", "200", true)]
    [InlineData("GreaterThanOrEqual", "204", true)]
    [InlineData("Between", "200-299", true)]
    public void NumericOperatorsCompareTypedValues(string operatorName, string value, bool expected)
    {
        var filterOperator = Enum.Parse<AdvancedFilterOperator>(operatorName);

        Assert.Equal(expected, Matches(
            Definition(Rule(Field("result"), filterOperator, value)),
            Row(status: 204)));
    }

    [Fact]
    public void BodyFieldsUseTheBoundedPayloadCache()
    {
        var row = Row();
        row.Session.Request = Message("POST / HTTP/1.1", "request-secret");
        row.Session.Response = Message("HTTP/1.1 200 OK", "response-value");
        var cache = new PayloadSearchCache(1024 * 1024);

        Assert.True(Matches(
            Definition(Rule(AdvancedFilterField.RequestBody, AdvancedFilterOperator.Contains, "SECRET")),
            row,
            cache));
        Assert.True(Matches(
            Definition(Rule(AdvancedFilterField.ResponseBody, AdvancedFilterOperator.Equals, "response-value")),
            row,
            cache));
        Assert.True(cache.CacheHits > 0);
    }

    [Fact]
    public void BooleanExpressionShortCircuitsBeforeUnneededBodyDecoding()
    {
        var row = Row(status: 200);
        var decoded = 0;
        row.Session.Request = new HttpMessage(
            "GET / HTTP/1.1",
            () => EmptyBody(),
            _ =>
            {
                decoded++;
                return EmptyBody();
            });
        var falseAndBody = Definition(
            Rule(Field("result"), AdvancedFilterOperator.Equals, "404", join: AdvancedFilterJoin.And),
            Rule(AdvancedFilterField.RequestBody, AdvancedFilterOperator.Contains, "x"));
        var trueOrBody = Definition(
            Rule(Field("result"), AdvancedFilterOperator.Equals, "200", join: AdvancedFilterJoin.Or),
            Rule(AdvancedFilterField.RequestBody, AdvancedFilterOperator.Contains, "x"));

        Assert.False(Matches(falseAndBody, row));
        Assert.True(Matches(trueOrBody, row));
        Assert.Equal(0, decoded);
    }

    [Fact]
    public void InvalidValuesAndUnbalancedParenthesesIdentifyTheOffendingRule()
    {
        var invalidSize = AdvancedFilterCompiler.Compile(
            Definition(Rule(Field("response-size"), AdvancedFilterOperator.GreaterThan, "many")));
        var unbalanced = AdvancedFilterCompiler.Compile(
            Definition(Rule(Field("url"), AdvancedFilterOperator.Contains, "x", open: 1)));
        var invalidRegex = AdvancedFilterCompiler.Compile(
            Definition(Rule(Field("url"), AdvancedFilterOperator.MatchesRegex, "(")));

        Assert.Equal(0, Assert.Single(invalidSize.Errors).RuleIndex);
        Assert.Contains("size", invalidSize.Errors[0].Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, Assert.Single(unbalanced.Errors).RuleIndex);
        Assert.Contains("parenthesis", unbalanced.Errors[0].Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, Assert.Single(invalidRegex.Errors).RuleIndex);
        Assert.Contains("regular expression", invalidRegex.Errors[0].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CatastrophicRegexIsBoundedByTheMatchTimeout()
    {
        var row = Row(headers: [new("X-Test", new string('a', 250_000) + "!")]);
        var field = AdvancedFilterField.ForColumn(
            SessionColumnCatalog.CreateCustom(SessionColumnSetting.RequestHeaderKind, "X-Test"));
        var compilation = AdvancedFilterCompiler.Compile(
            Definition(Rule(field, AdvancedFilterOperator.MatchesRegex, "^(a|aa)+$")));

        var exception = Assert.Throws<AdvancedFilterRuntimeException>(
            () => compilation.Filter!.Matches(row, new PayloadSearchCache(), CancellationToken.None));

        Assert.Equal(0, exception.RuleIndex);
        Assert.Contains("100", exception.Message, StringComparison.Ordinal);
    }

    private static bool Matches(
        AdvancedFilterDefinition definition,
        SessionRow row,
        PayloadSearchCache? cache = null)
    {
        var compilation = AdvancedFilterCompiler.Compile(definition);
        Assert.Empty(compilation.Errors);
        return compilation.Filter!.Matches(row, cache ?? new PayloadSearchCache(), CancellationToken.None);
    }

    private static AdvancedFilterDefinition Definition(params AdvancedFilterRule[] rules) => new(rules);

    private static AdvancedFilterRule Rule(
        AdvancedFilterField field,
        AdvancedFilterOperator filterOperator,
        string value,
        bool enabled = true,
        int open = 0,
        int close = 0,
        AdvancedFilterJoin join = AdvancedFilterJoin.And,
        bool caseSensitive = false) =>
        new(enabled, open, field, filterOperator, value, close, join, caseSensitive);

    private static AdvancedFilterField Field(string id) =>
        AdvancedFilterField.ForColumn(Assert.Single(SessionColumnCatalog.BuiltIns, column => column.Id == id).Setting);

    private static SessionRow Row(
        int status = 200,
        string method = "GET",
        string url = "https://example.test/",
        IReadOnlyList<HttpHeader>? headers = null)
    {
        var request = Message($"{method} {url} HTTP/1.1", "");
        if (headers is not null)
        {
            request.Headers.AddRange(headers);
        }
        var session = new HttpSession
        {
            Id = Guid.NewGuid().ToString("N"),
            Method = method,
            Url = url,
            StatusCode = status,
            Request = request,
            RequestBytes = 100,
            ResponseBytes = 200
        };
        session.Response = Message($"HTTP/1.1 {status} Test", "");
        return new SessionRow(session, 0, []);
    }

    private static HttpMessage Message(string startLine, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        return new HttpMessage
        {
            StartLine = startLine,
            Body = new BodyPreview
            {
                Length = bytes.Length,
                CapturedLength = bytes.Length,
                Preview = body,
                DecodedBytes = bytes,
                CapturedBytes = bytes,
                Charset = "utf-8"
            }
        };
    }

    private static BodyPreview EmptyBody() => new()
    {
        Preview = "",
        Length = 0,
        CapturedLength = 0
    };
}
