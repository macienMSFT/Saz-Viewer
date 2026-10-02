using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace SazViewer.Core;

public static class AuthScrubber
{
    private const string RemovedPayloadNote = "[REDACTED:BinaryPayload] Retained bytes were removed by --scrub-auth because they could not be safely rewritten.";

    private static readonly Regex QueryParameterPattern = new(
        @"(?<prefix>[?&])(?<name>[^=&#]+)(?<equals>=)(?<value>[^&#]*)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex PlainSecretPattern = new(
        @"(?<name>\b(?:access[_-]?token|id[_-]?token|refresh[_-]?token|client[_-]?secret|client[_-]?assertion|assertion|password|passwd|pwd|api[_-]?key|apikey|aws[_-]?secret[_-]?access[_-]?key|sharedaccesskey(?:name)?|accountkey|token|secret|signature|sig|auth|authorization|code)\b)(?<separator>\s*[:=]\s*)(?<quote>[""']?)(?<value>[^""'\s,;&<>]+)(?<closing>[""']?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AuthorizationPattern = new(
        @"\b(?<scheme>Basic|Bearer|Negotiate|NTLM|Kerberos)\s+(?<value>[A-Za-z0-9+/=_\-.]{4,})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex JwtPattern = new(
        @"(?<![A-Za-z0-9_-])eyJ[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{6,}(?:\.[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{6,})?(?![A-Za-z0-9_-])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex GitHubTokenPattern = new(
        @"(?<![A-Za-z0-9_])(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})(?![A-Za-z0-9_])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SlackTokenPattern = new(
        @"(?<![A-Za-z0-9-])xox[a-z]-[A-Za-z0-9-]{10,}(?![A-Za-z0-9-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AwsAccessKeyPattern = new(
        @"(?<![A-Z0-9])(?:AKIA|ASIA|AIDA|AROA|AIPA|ANPA|ANVA|ASCA)[A-Z0-9]{12,}(?![A-Z0-9])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex GoogleApiKeyPattern = new(
        @"(?<![A-Za-z0-9_-])AIza[0-9A-Za-z_-]{20,}(?![A-Za-z0-9_-])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex PrivateKeyPattern = new(
        @"-----BEGIN(?: [A-Z0-9]+)? PRIVATE KEY-----.*?-----END(?: [A-Z0-9]+)? PRIVATE KEY-----",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex SamlAssertionPattern = new(
        @"<(?<prefix>[A-Za-z_][\w.-]*:)?Assertion\b.*?</(?:[A-Za-z_][\w.-]*:)?Assertion\s*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex WebhookPattern = new(
        @"(?<prefix>https?://(?:hooks\.slack\.com/services|(?:canary|[^/]+)\.webhook\.office\.com/webhookb2|discord(?:app)?\.com/api/webhooks)/)(?<secret>[^\s?""'<>]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex GitHubWebhookPattern = new(
        @"(?<prefix>https?://api\.github\.com/repos/[^/\s]+/[^/\s]+/hooks/)(?<secret>[^/?#\s""'<>]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Only starts a name at the first letter of an [A-Za-z0-9_-] run. Every later offset in the run reaches the same
    // run end and suffix, so it would match or fail identically; skipping them avoids quadratic backtracking on long
    // tokens (for example Bearer JWTs) without changing any match.
    private static readonly Regex ChallengeParameterPattern = new(
        @"(?<![A-Za-z][0-9_-]*)(?<name>[A-Za-z][A-Za-z0-9_-]*)\s*=\s*""?(?<value>[^"",\s]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex UrlUserInfoPattern = new(
        @"(?<prefix>(?:[A-Za-z][A-Za-z0-9+.-]*:)?//)(?<userinfo>[^/?#@\s]+)@",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static AuthScrubSummary Scrub(SazReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var context = new ScrubContext();
        context.CollectKnownSecrets(report);

        // Sessions and messages are independent and the context only accumulates commutative counts, so scrubbing
        // them in parallel produces exactly the same report as a sequential pass.
        Parallel.ForEach(report.Sessions, session =>
        {
            session.Url = context.ScrubUrl(session.Url);
            session.StatusText = context.ScrubText(session.StatusText);
            session.ContentType = context.ScrubText(session.ContentType);
            session.Request = context.ScrubMessage(session.Request);
            session.Response = context.ScrubMessage(session.Response);
            ScrubDictionary(session.Metadata, context);
            ScrubDictionary(session.Timers, context);
            ScrubList(session.Warnings, context);
        });

        Parallel.For(0, report.WebSocketMessages.Count, index =>
            report.WebSocketMessages[index] = context.ScrubWebSocket(report.WebSocketMessages[index]));

        ScrubList(report.Warnings, context);
        if (report.Mapi is not null)
        {
            var scrubbedMapi = context.ScrubMapi(report.Mapi);
            report.Mapi = scrubbedMapi;
            foreach (var session in report.Sessions)
            {
                session.Mapi = scrubbedMapi.ByHttpSessionId.GetValueOrDefault(session.Id);
            }
        }

        var summary = new AuthScrubSummary(
            context.Counts.ToImmutableSortedDictionary(StringComparer.Ordinal));
        report.AuthScrub = summary;
        return summary;
    }

    private static void ScrubDictionary(Dictionary<string, string> values, ScrubContext context)
    {
        foreach (var key in values.Keys.ToArray())
        {
            values[key] = context.IsSensitiveName(key)
                ? context.Redact(context.MarkerType(key, values[key]))
                : context.ScrubText(values[key]) ?? string.Empty;
        }
    }

    private static void ScrubList(List<string> values, ScrubContext context)
    {
        for (var index = 0; index < values.Count; index++)
        {
            values[index] = context.ScrubText(values[index]) ?? string.Empty;
        }
    }

    private sealed class ScrubContext
    {
        private (string Value, string Type)[] knownSecrets = [];

        public Dictionary<string, int> Counts { get; } = new(StringComparer.Ordinal);

        public void CollectKnownSecrets(SazReport report)
        {
            var collected = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var session in report.Sessions)
            {
                CollectUrlSecrets(session.Url, collected);
                CollectMessageSecrets(session.Request, collected);
                CollectMessageSecrets(session.Response, collected);
            }
            knownSecrets = collected
                .OrderByDescending(item => item.Key.Length)
                .Select(item => (item.Key, item.Value))
                .ToArray();
        }

        public HttpMessage? ScrubMessage(HttpMessage? message)
        {
            if (message is null)
            {
                return null;
            }

            var headers = message.Headers
                .Select(header => new HttpHeader(header.Name, ScrubHeader(header.Name, header.Value)))
                .ToArray();
            var contentType = message.Headers.FirstOrDefault(header =>
                header.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))?.Value;
            var scrubbed = new HttpMessage
            {
                StartLine = ScrubStartLine(message.StartLine),
                Body = ScrubBody(message.Body, contentType)
            };
            scrubbed.Headers.AddRange(headers);
            return scrubbed;
        }

        public string ScrubHeader(string name, string value)
        {
            if (name.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
            {
                return ScrubCookieHeader(value);
            }
            if (name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
            {
                return ScrubSetCookieHeader(value);
            }
            if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase))
            {
                return ScrubAuthorization(value);
            }
            if (name.Equals("WWW-Authenticate", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase))
            {
                return ScrubChallenge(value);
            }
            if (IsSensitiveName(name))
            {
                return Redact(MarkerType(name, value));
            }
            if (name.Equals("Location", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Referer", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Origin", StringComparison.OrdinalIgnoreCase))
            {
                return ScrubUrl(value) ?? string.Empty;
            }
            return ScrubText(value) ?? string.Empty;
        }

        public string? ScrubUrl(string? value)
        {
            if (value is null)
            {
                return null;
            }

            var scrubbedUserInfo = UrlUserInfoPattern.Replace(
                value,
                match => match.Groups["prefix"].Value + Redact("UserInfo") + "@");
            var hasAzureSas = QueryParameterPattern.Matches(scrubbedUserInfo)
                .Select(match => DecodeName(match.Groups["name"].Value))
                .Any(name => name.Equals("sig", StringComparison.OrdinalIgnoreCase))
                && (scrubbedUserInfo.Contains("blob.core.windows.net", StringComparison.OrdinalIgnoreCase)
                    || scrubbedUserInfo.Contains("dfs.core.windows.net", StringComparison.OrdinalIgnoreCase)
                    || Regex.IsMatch(scrubbedUserInfo, @"[?&](?:sv|se|sp|skoid|sktid|sr)=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            var scrubbed = QueryParameterPattern.Replace(scrubbedUserInfo, match =>
            {
                var name = DecodeName(match.Groups["name"].Value);
                var original = match.Groups["value"].Value;
                if (IsSensitiveQueryName(name))
                {
                    var type = hasAzureSas && name.Equals("sig", StringComparison.OrdinalIgnoreCase)
                        ? "AzureSAS"
                        : MarkerType(name, DecodeValue(original));
                    return match.Groups["prefix"].Value + match.Groups["name"].Value + "=" + Redact(type);
                }
                var cleaned = ScrubText(DecodeValue(original)) ?? string.Empty;
                return cleaned == DecodeValue(original)
                    ? match.Value
                    : match.Groups["prefix"].Value + match.Groups["name"].Value + "=" + cleaned;
            });
            scrubbed = WebhookPattern.Replace(
                scrubbed,
                match => match.Groups["prefix"].Value + Redact("Webhook"));
            scrubbed = GitHubWebhookPattern.Replace(
                scrubbed,
                match => match.Groups["prefix"].Value + Redact("Webhook"));
            return ScrubText(scrubbed);
        }

        public string? ScrubText(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            var scrubbed = ApplyKnownSecrets(value);
            scrubbed = PrivateKeyPattern.Replace(scrubbed, _ => Redact("PrivateKey"));
            scrubbed = SamlAssertionPattern.Replace(scrubbed, _ => Redact("SAML"));
            scrubbed = PlainSecretPattern.Replace(scrubbed, match =>
            {
                if (match.Groups["value"].Value.StartsWith("[REDACTED:", StringComparison.Ordinal))
                {
                    return match.Value;
                }
                var type = MarkerType(match.Groups["name"].Value, match.Groups["value"].Value);
                var quote = match.Groups["quote"].Value;
                var closing = match.Groups["closing"].Value;
                if (closing.Length == 0 && quote.Length > 0)
                {
                    closing = quote;
                }
                return match.Groups["name"].Value
                    + match.Groups["separator"].Value
                    + quote
                    + Redact(type)
                    + closing;
            });
            scrubbed = AuthorizationPattern.Replace(scrubbed, match =>
                match.Groups["scheme"].Value + " " + Redact(SchemeType(match.Groups["scheme"].Value)));
            scrubbed = JwtPattern.Replace(scrubbed, _ => Redact("JWT"));
            scrubbed = GitHubTokenPattern.Replace(scrubbed, _ => Redact("GitHubToken"));
            scrubbed = SlackTokenPattern.Replace(scrubbed, _ => Redact("SlackToken"));
            scrubbed = AwsAccessKeyPattern.Replace(scrubbed, _ => Redact("AWS"));
            scrubbed = GoogleApiKeyPattern.Replace(scrubbed, _ => Redact("APIKey"));
            scrubbed = WebhookPattern.Replace(
                scrubbed,
                match => match.Groups["prefix"].Value + Redact("Webhook"));
            return GitHubWebhookPattern.Replace(
                scrubbed,
                match => match.Groups["prefix"].Value + Redact("Webhook"));
        }

        public WebSocketMessage ScrubWebSocket(WebSocketMessage message)
        {
            string? text = null;
            byte[] payload;
            var removed = false;
            if (message.IsBinary)
            {
                payload = [];
                removed = !message.Payload.IsEmpty || message.PayloadLength > 0;
                if (removed)
                {
                    Record("BinaryPayload");
                }
            }
            else if (message.Text is not null
                     && TryScrubBodyText(message.Text, "application/json", out var scrubbedText))
            {
                text = scrubbedText;
                payload = Encoding.UTF8.GetBytes(text);
            }
            else if (message.Text is null
                     && TryDecodeText(message.Payload.Span, null, out var decoded, out _)
                     && TryScrubBodyText(decoded, null, out scrubbedText))
            {
                text = scrubbedText;
                payload = Encoding.UTF8.GetBytes(text);
            }
            else
            {
                payload = [];
                removed = !message.Payload.IsEmpty || message.Text is not null;
                if (removed)
                {
                    Record("BinaryPayload");
                }
            }

            var scrubbed = new WebSocketMessage
            {
                SessionId = message.SessionId,
                MessageIndex = message.MessageIndex,
                RecordIndex = message.RecordIndex,
                Timestamp = message.Timestamp,
                Direction = message.Direction,
                Type = message.Type,
                PayloadLength = message.PayloadLength,
                Preview = removed
                    ? RemovedPayloadNote
                    : Compact(text ?? ScrubText(message.Preview) ?? string.Empty, 240),
                IsBinary = message.IsBinary,
                IsDecoded = message.IsDecoded,
                IsComplete = message.IsComplete,
                IsFragmented = message.IsFragmented,
                IsPayloadTruncated = message.IsPayloadTruncated || removed,
                Text = text,
                Warning = AppendNote(ScrubText(message.Warning), removed ? RemovedPayloadNote : null),
                Payload = payload,
                SourceOrder = message.SourceOrder
            };
            scrubbed.Frames.AddRange(message.Frames.Select(frame => new WebSocketFrame
            {
                RecordIndex = frame.RecordIndex,
                FiddlerId = frame.FiddlerId,
                BitFlags = frame.BitFlags,
                Timestamp = frame.Timestamp,
                Direction = frame.Direction,
                Opcode = frame.Opcode,
                Type = frame.Type,
                Final = frame.Final,
                Masked = frame.Masked,
                PayloadLength = frame.PayloadLength,
                CapturedPayloadLength = frame.CapturedPayloadLength,
                IsDecoded = frame.IsDecoded,
                IsPayloadTruncated = frame.IsPayloadTruncated || !frame.Payload.IsEmpty,
                Warning = AppendNote(ScrubText(frame.Warning), frame.Payload.IsEmpty ? null : RemovedPayloadNote),
                Payload = ReadOnlyMemory<byte>.Empty
            }));
            return scrubbed;
        }

        public MapiCapture ScrubMapi(MapiCapture capture)
        {
            var sessions = capture.Sessions.Select(session =>
            {
                var request = ScrubMapiParse(session.Request);
                var response = ScrubMapiParse(session.Response);
                return session with
                {
                    RequestType = ScrubText(session.RequestType) ?? string.Empty,
                    ResponseCode = ScrubText(session.ResponseCode),
                    Request = request,
                    Response = response,
                    Warnings = session.Warnings.Select(warning => ScrubText(warning) ?? string.Empty).ToImmutableArray()
                };
            }).ToImmutableArray();
            return new MapiCapture(
                sessions,
                sessions.ToImmutableDictionary(session => session.HttpSessionId, StringComparer.Ordinal),
                capture.Coverage);
        }

        public bool IsSensitiveName(string value)
        {
            var normalized = new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
            return normalized.Contains("authorization", StringComparison.Ordinal)
                || normalized.Contains("credential", StringComparison.Ordinal)
                || normalized.Contains("password", StringComparison.Ordinal)
                || normalized.Contains("passwd", StringComparison.Ordinal)
                || normalized.Contains("secret", StringComparison.Ordinal)
                || normalized.Contains("token", StringComparison.Ordinal)
                || normalized.Contains("signature", StringComparison.Ordinal)
                || normalized.Contains("apikey", StringComparison.Ordinal)
                || normalized.Contains("subscriptionkey", StringComparison.Ordinal)
                || normalized.Contains("functionskey", StringComparison.Ordinal)
                || normalized.Contains("accountkey", StringComparison.Ordinal)
                || normalized.Contains("sharedaccesskey", StringComparison.Ordinal)
                || normalized.Contains("sessionkey", StringComparison.Ordinal)
                || normalized.Contains("csrf", StringComparison.Ordinal)
                || normalized.Contains("xsrf", StringComparison.Ordinal)
                || normalized is "auth" or "sig" or "pwd" or "key" or "code" or "assertion" or "clientassertion";
        }

        public string MarkerType(string name, string value)
        {
            if (JwtPattern.IsMatch(value))
            {
                return "JWT";
            }
            if (GitHubTokenPattern.IsMatch(value))
            {
                return "GitHubToken";
            }
            if (SlackTokenPattern.IsMatch(value))
            {
                return "SlackToken";
            }
            if (AwsAccessKeyPattern.IsMatch(value))
            {
                return "AWS";
            }

            var normalized = new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
            if (normalized.Contains("cookie", StringComparison.Ordinal))
            {
                return "Cookie";
            }
            if (normalized.Contains("password", StringComparison.Ordinal) || normalized.Contains("passwd", StringComparison.Ordinal) || normalized == "pwd")
            {
                return "Password";
            }
            if (normalized.Contains("signature", StringComparison.Ordinal) || normalized == "sig")
            {
                return "Signature";
            }
            if (normalized.Contains("csrf", StringComparison.Ordinal) || normalized.Contains("xsrf", StringComparison.Ordinal))
            {
                return "CSRF";
            }
            if (normalized.Contains("apikey", StringComparison.Ordinal)
                || normalized.Contains("subscriptionkey", StringComparison.Ordinal)
                || normalized.Contains("functionskey", StringComparison.Ordinal)
                || normalized == "key")
            {
                return "APIKey";
            }
            if (normalized.Contains("accountkey", StringComparison.Ordinal)
                || normalized.Contains("sharedaccesskey", StringComparison.Ordinal))
            {
                return "AzureSAS";
            }
            if (normalized.Contains("assertion", StringComparison.Ordinal))
            {
                return "SAML";
            }
            if (normalized == "code")
            {
                return "OAuthCode";
            }
            if (normalized.Contains("session", StringComparison.Ordinal))
            {
                return "Session";
            }
            if (normalized.Contains("token", StringComparison.Ordinal))
            {
                return "Token";
            }
            if (normalized.Contains("auth", StringComparison.Ordinal) || normalized.Contains("credential", StringComparison.Ordinal))
            {
                return "Auth";
            }
            return "Secret";
        }

        public string Redact(string type)
        {
            Record(type);
            return $"[REDACTED:{type}]";
        }

        private string ApplyKnownSecrets(string value)
        {
            foreach (var (secret, type) in knownSecrets)
            {
                var index = value.IndexOf(secret, StringComparison.Ordinal);
                if (index < 0)
                {
                    continue;
                }

                var occurrences = 0;
                while (index >= 0)
                {
                    occurrences++;
                    index = value.IndexOf(secret, index + secret.Length, StringComparison.Ordinal);
                }
                for (var count = 0; count < occurrences; count++)
                {
                    Record(type);
                }
                value = value.Replace(secret, $"[REDACTED:{type}]", StringComparison.Ordinal);
            }
            return value;
        }

        private void CollectMessageSecrets(
            HttpMessage? message,
            Dictionary<string, string> collected)
        {
            if (message is null)
            {
                return;
            }

            foreach (var header in message.Headers)
            {
                if (header.Name.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var segment in header.Value.Split(';'))
                    {
                        var equals = segment.IndexOf('=');
                        if (equals >= 0)
                        {
                            AddKnownSecret(collected, segment[(equals + 1)..].Trim().Trim('"'), "Cookie");
                        }
                    }
                }
                else if (header.Name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (Match match in Regex.Matches(
                                 header.Value,
                                 @"(?:^|,(?=\s*[^=;,\s]+=))\s*[^=;,\s]+=(?<value>[^;,]*)",
                                 RegexOptions.CultureInvariant))
                    {
                        AddKnownSecret(collected, match.Groups["value"].Value.Trim().Trim('"'), "Cookie");
                    }
                }
                else if (header.Name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                         || header.Name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase))
                {
                    var separator = header.Value.IndexOfAny([' ', '\t']);
                    var scheme = separator < 0 ? "Auth" : header.Value[..separator];
                    var remainder = separator < 0 ? header.Value : header.Value[(separator + 1)..].Trim();
                    AddKnownSecret(collected, remainder, SchemeType(scheme));
                    CollectChallengeSecrets(remainder, collected);
                }
                else if (header.Name.Equals("WWW-Authenticate", StringComparison.OrdinalIgnoreCase)
                         || header.Name.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase))
                {
                    CollectChallengeSecrets(header.Value, collected);
                }
                else if (IsSensitiveName(header.Name))
                {
                    AddKnownSecret(collected, header.Value, MarkerType(header.Name, header.Value));
                }
            }
        }

        private void CollectUrlSecrets(string? url, Dictionary<string, string> collected)
        {
            if (string.IsNullOrEmpty(url))
            {
                return;
            }

            foreach (Match match in UrlUserInfoPattern.Matches(url))
            {
                AddKnownSecret(collected, match.Groups["userinfo"].Value, "UserInfo");
            }
            foreach (Match match in QueryParameterPattern.Matches(url))
            {
                var name = DecodeName(match.Groups["name"].Value);
                if (!IsSensitiveQueryName(name))
                {
                    continue;
                }
                var raw = match.Groups["value"].Value;
                var decoded = DecodeValue(raw);
                var type = MarkerType(name, decoded);
                AddKnownSecret(collected, raw, type);
                AddKnownSecret(collected, decoded, type);
            }
        }

        private void CollectChallengeSecrets(
            string value,
            Dictionary<string, string> collected)
        {
            foreach (Match match in ChallengeParameterPattern.Matches(value))
            {
                var name = match.Groups["name"].Value;
                if (IsSensitiveName(name)
                    || name.Equals("nonce", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("opaque", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("response", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("credential", StringComparison.OrdinalIgnoreCase))
                {
                    var secret = match.Groups["value"].Value;
                    AddKnownSecret(collected, secret, MarkerType(name, secret));
                }
            }
        }

        private static void AddKnownSecret(
            Dictionary<string, string> collected,
            string value,
            string type)
        {
            if (value.Length >= 8 && !value.StartsWith("[REDACTED:", StringComparison.Ordinal))
            {
                collected.TryAdd(value, type);
            }
        }

        private BodyPreview ScrubBody(BodyPreview body, string? contentType)
        {
            var source = !body.DecodedBytes.IsEmpty ? body.DecodedBytes : body.CapturedBytes;
            if (source.IsEmpty && body.Preview.Length == 0)
            {
                return body;
            }

            string text;
            Encoding encoding;
            if (body.IsBinary && !IsTextualMediaType(contentType))
            {
                Record("BinaryPayload");
                return RemovedBody(body);
            }
            if (!source.IsEmpty && TryDecodeText(source.Span, body.Charset, out var decoded, out var detectedEncoding))
            {
                text = decoded;
                encoding = detectedEncoding;
            }
            else if (source.IsEmpty && !body.IsBinary)
            {
                text = body.Preview;
                encoding = Encoding.UTF8;
            }
            else
            {
                Record("BinaryPayload");
                return RemovedBody(body);
            }

            if (!TryScrubBodyText(text, contentType, out var scrubbedText))
            {
                Record("MalformedPayload");
                return RemovedBody(body);
            }
            byte[] scrubbedBytes;
            try
            {
                scrubbedBytes = encoding.GetBytes(scrubbedText);
            }
            catch (EncoderFallbackException)
            {
                scrubbedBytes = Encoding.UTF8.GetBytes(scrubbedText);
                encoding = Encoding.UTF8;
            }

            var retained = scrubbedBytes.AsMemory(0, Math.Min(scrubbedBytes.Length, HttpMessageParser.MaxBodyPreview));
            var encodedBody = body.RemovedEncodings.Count > 0 || body.SourceIsDecoded;
            var captured = encodedBody ? ReadOnlyMemory<byte>.Empty : retained;
            var decodedBytes = retained;
            var note = body.SourceIsDecoded
                ? "Original wire bytes were unavailable; only the scrubbed decoded representation is retained."
                : encodedBody
                ? "Captured pre-decode bytes were removed by --scrub-auth; only the scrubbed decoded representation is retained."
                : null;
            return new BodyPreview
            {
                Length = body.IsTruncated ? body.Length : scrubbedBytes.Length,
                CapturedLength = body.CapturedLength,
                IsBinary = false,
                IsTruncated = body.IsTruncated || scrubbedBytes.Length > retained.Length,
                Charset = encoding.WebName,
                Preview = scrubbedText[..Math.Min(scrubbedText.Length, HttpMessageParser.MaxBodyPreview)],
                CapturedBytesPreview = encodedBody ? RemovedPayloadNote : null,
                CapturedBytesPreviewTruncated = encodedBody,
                RemovedEncodings = body.RemovedEncodings,
                SourceIsDecoded = body.SourceIsDecoded,
                DecodingStatus = AppendNote(ScrubText(body.DecodingStatus), note),
                CapturedBytes = captured,
                DecodedBytes = decodedBytes,
                NormalizedBytes = ReadOnlyMemory<byte>.Empty
            };
        }

        private BodyPreview RemovedBody(BodyPreview body) => new()
        {
            Length = 0,
            CapturedLength = body.CapturedLength,
            IsBinary = true,
            IsTruncated = true,
            Charset = null,
            Preview = RemovedPayloadNote,
            CapturedBytesPreview = RemovedPayloadNote,
            CapturedBytesPreviewTruncated = true,
            RemovedEncodings = body.RemovedEncodings,
            SourceIsDecoded = body.SourceIsDecoded,
            DecodingStatus = AppendNote(ScrubText(body.DecodingStatus), RemovedPayloadNote),
            CapturedBytes = ReadOnlyMemory<byte>.Empty,
            DecodedBytes = ReadOnlyMemory<byte>.Empty,
            NormalizedBytes = ReadOnlyMemory<byte>.Empty
        };

        private bool TryScrubBodyText(string text, string? contentType, out string scrubbed)
        {
            var mediaType = contentType?.Split(';', 2)[0].Trim().ToLowerInvariant() ?? string.Empty;
            if (mediaType.Contains("json", StringComparison.Ordinal))
            {
                return TryScrubJson(text, out scrubbed);
            }
            if (mediaType.Contains("xml", StringComparison.Ordinal))
            {
                return TryScrubXml(text, out scrubbed);
            }
            if (mediaType == "application/x-www-form-urlencoded")
            {
                scrubbed = ScrubForm(text);
                return true;
            }
            if (mediaType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
            {
                return TryScrubMultipart(text, contentType ?? string.Empty, out scrubbed);
            }
            if (TryScrubJson(text, out var sniffedJson))
            {
                scrubbed = sniffedJson;
                return true;
            }
            if (LooksLikeJson(text))
            {
                scrubbed = string.Empty;
                return false;
            }
            if (!mediaType.Contains("html", StringComparison.Ordinal)
                && LooksLikeXml(text))
            {
                return TryScrubXml(text, out scrubbed);
            }
            scrubbed = ScrubText(text) ?? string.Empty;
            return true;
        }

        private bool TryScrubMultipart(string text, string contentType, out string scrubbed)
        {
            var boundaryMatch = Regex.Match(
                contentType,
                @"(?:^|;)\s*boundary\s*=\s*(?:""(?<quoted>(?:\\.|[^""])*)""|(?<token>[^;\s]+))",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var boundary = boundaryMatch.Groups["quoted"].Success
                ? boundaryMatch.Groups["quoted"].Value.Replace("\\\"", "\"", StringComparison.Ordinal)
                : boundaryMatch.Groups["token"].Value;
            if (boundary.Length is 0 or > 200 || boundary.IndexOfAny(['\r', '\n']) >= 0)
            {
                scrubbed = string.Empty;
                return false;
            }

            var delimiter = "--" + boundary;
            var first = FindBoundary(text, delimiter, 0);
            if (first < 0)
            {
                scrubbed = string.Empty;
                return false;
            }

            var output = new StringBuilder(text.Length);
            output.Append(ScrubText(text[..first]));
            var position = first;
            while (position >= 0)
            {
                output.Append(delimiter);
                position += delimiter.Length;
                if (text.AsSpan(position).StartsWith("--", StringComparison.Ordinal))
                {
                    position += 2;
                    output.Append("--").Append(ScrubText(text[position..]));
                    scrubbed = output.ToString();
                    return true;
                }

                var newlineLength = text.AsSpan(position).StartsWith("\r\n", StringComparison.Ordinal)
                    ? 2
                    : text.AsSpan(position).StartsWith("\n", StringComparison.Ordinal)
                        ? 1
                        : 0;
                if (newlineLength == 0)
                {
                    scrubbed = string.Empty;
                    return false;
                }
                output.Append(text, position, newlineLength);
                position += newlineLength;

                var next = FindBoundary(text, delimiter, position);
                if (next < 0)
                {
                    scrubbed = string.Empty;
                    return false;
                }
                var part = text[position..next];
                var headerEnd = part.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                var separatorLength = 4;
                if (headerEnd < 0)
                {
                    headerEnd = part.IndexOf("\n\n", StringComparison.Ordinal);
                    separatorLength = 2;
                }
                if (headerEnd < 0)
                {
                    scrubbed = string.Empty;
                    return false;
                }

                var trailing = part.EndsWith("\r\n", StringComparison.Ordinal)
                    ? "\r\n"
                    : part.EndsWith('\n')
                        ? "\n"
                        : string.Empty;
                var bodyEnd = part.Length - trailing.Length;
                output.Append(ScrubText(part[..(headerEnd + separatorLength)]));
                if (bodyEnd < headerEnd + separatorLength)
                {
                    scrubbed = string.Empty;
                    return false;
                }
                output.Append(Redact("Multipart")).Append(trailing);
                position = next;
            }

            scrubbed = string.Empty;
            return false;
        }

        private static int FindBoundary(string text, string delimiter, int start)
        {
            var index = text.IndexOf(delimiter, start, StringComparison.Ordinal);
            while (index >= 0 && index > 0 && text[index - 1] != '\n')
            {
                index = text.IndexOf(delimiter, index + delimiter.Length, StringComparison.Ordinal);
            }
            return index;
        }

        private bool TryScrubJson(string text, out string scrubbed)
        {
            try
            {
                using var document = JsonDocument.Parse(
                    text,
                    new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 64 });
                using var output = new MemoryStream();
                using (var writer = new Utf8JsonWriter(output))
                {
                    WriteJson(writer, document.RootElement, null);
                }
                scrubbed = Encoding.UTF8.GetString(output.ToArray());
                return true;
            }
            catch (JsonException)
            {
                scrubbed = string.Empty;
                return false;
            }
        }

        private void WriteJson(Utf8JsonWriter writer, JsonElement element, string? propertyName)
        {
            if (propertyName is not null && IsSensitiveName(propertyName))
            {
                writer.WriteStringValue(Redact(MarkerType(propertyName, element.ToString())));
                return;
            }

            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var property in element.EnumerateObject())
                    {
                        writer.WritePropertyName(property.Name);
                        WriteJson(writer, property.Value, property.Name);
                    }
                    writer.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray())
                    {
                        WriteJson(writer, item, propertyName);
                    }
                    writer.WriteEndArray();
                    break;
                case JsonValueKind.String:
                    writer.WriteStringValue(ScrubText(element.GetString()));
                    break;
                default:
                    element.WriteTo(writer);
                    break;
            }
        }

        private bool TryScrubXml(string text, out string scrubbed)
        {
            try
            {
                using var reader = XmlReader.Create(
                    new StringReader(text),
                    new XmlReaderSettings
                    {
                        DtdProcessing = DtdProcessing.Prohibit,
                        XmlResolver = null,
                        MaxCharactersInDocument = HttpMessageParser.MaxBodyPreview
                    });
                var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
                foreach (var element in document.Descendants().ToArray())
                {
                    if (IsSensitiveName(element.Name.LocalName)
                        || element.Name.LocalName.Equals("Assertion", StringComparison.OrdinalIgnoreCase))
                    {
                        element.RemoveNodes();
                        element.Add(new XText(Redact(MarkerType(element.Name.LocalName, element.Value))));
                        continue;
                    }
                    foreach (var attribute in element.Attributes().ToArray())
                    {
                        attribute.Value = IsSensitiveName(attribute.Name.LocalName)
                            ? Redact(MarkerType(attribute.Name.LocalName, attribute.Value))
                            : ScrubText(attribute.Value) ?? string.Empty;
                    }
                    foreach (var node in element.Nodes().OfType<XText>().ToArray())
                    {
                        node.Value = ScrubText(node.Value) ?? string.Empty;
                    }
                }
                scrubbed = document.ToString(SaveOptions.DisableFormatting);
                return true;
            }
            catch (XmlException)
            {
                scrubbed = string.Empty;
                return false;
            }
        }

        private string ScrubForm(string text)
        {
            var parts = text.Split('&');
            for (var index = 0; index < parts.Length; index++)
            {
                var equals = parts[index].IndexOf('=');
                var rawName = equals < 0 ? parts[index] : parts[index][..equals];
                var rawValue = equals < 0 ? string.Empty : parts[index][(equals + 1)..];
                var name = DecodeName(rawName);
                var value = DecodeValue(rawValue);
                var scrubbed = IsSensitiveQueryName(name)
                    ? Redact(MarkerType(name, value))
                    : ScrubText(value) ?? string.Empty;
                parts[index] = equals < 0 ? rawName : rawName + "=" + scrubbed;
            }
            return string.Join('&', parts);
        }

        private string ScrubCookieHeader(string value)
        {
            var segments = value.Split(';');
            for (var index = 0; index < segments.Length; index++)
            {
                var equals = segments[index].IndexOf('=');
                if (equals < 0)
                {
                    continue;
                }
                segments[index] = segments[index][..(equals + 1)] + Redact("Cookie");
            }
            return string.Join(';', segments);
        }

        private string ScrubSetCookieHeader(string value)
        {
            return Regex.Replace(
                value,
                @"(?<prefix>^|,(?=\s*[^=;,\s]+=))(?<space>\s*)(?<name>[^=;,\s]+)=(?<value>[^;,]*)",
                match => match.Groups["prefix"].Value
                    + match.Groups["space"].Value
                    + match.Groups["name"].Value
                    + "="
                    + Redact("Cookie"),
                RegexOptions.CultureInvariant);
        }

        private string ScrubAuthorization(string value)
        {
            var separator = value.IndexOfAny([' ', '\t']);
            if (separator < 0)
            {
                return Redact("Auth");
            }
            var scheme = separator < 0 ? value : value[..separator];
            var remainder = separator < 0 ? string.Empty : value[(separator + 1)..].TrimStart();
            if (scheme.Equals("Digest", StringComparison.OrdinalIgnoreCase)
                || scheme.StartsWith("AWS4-", StringComparison.OrdinalIgnoreCase))
            {
                return scheme + (remainder.Length == 0 ? string.Empty : " " + ScrubChallengeParameters(remainder));
            }
            if (scheme.Equals("SharedKey", StringComparison.OrdinalIgnoreCase)
                || scheme.Equals("SharedAccessSignature", StringComparison.OrdinalIgnoreCase))
            {
                return scheme + " " + Redact("AzureSAS");
            }
            return scheme + (remainder.Length == 0 ? string.Empty : " " + Redact(SchemeType(scheme)));
        }

        private string ScrubChallenge(string value)
        {
            var separator = value.IndexOfAny([' ', '\t']);
            if (separator < 0)
            {
                return ScrubText(value) ?? string.Empty;
            }
            var scheme = value[..separator];
            var parameters = value[(separator + 1)..].TrimStart();
            if (!parameters.Contains('='))
            {
                return scheme + " " + Redact(SchemeType(scheme));
            }
            return scheme + " " + ScrubChallengeParameters(parameters);
        }

        private string ScrubChallengeParameters(string value)
        {
            return Regex.Replace(
                value,
                @"(?<name>[A-Za-z][A-Za-z0-9_-]*)\s*=\s*(?<quote>""?)(?<value>[^"",\s]+)(?<closing>""?)",
                match =>
                {
                    var name = match.Groups["name"].Value;
                    var sensitive = IsSensitiveName(name)
                        || name.Equals("nonce", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("opaque", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("response", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("credential", StringComparison.OrdinalIgnoreCase);
                    var quote = match.Groups["quote"].Value;
                    if (!sensitive)
                    {
                        var cleaned = ScrubText(match.Groups["value"].Value) ?? string.Empty;
                        return cleaned == match.Groups["value"].Value
                            ? match.Value
                            : name + "=" + quote + cleaned + quote;
                    }
                    return name + "=" + quote + Redact(MarkerType(name, match.Groups["value"].Value)) + quote;
                },
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private string ScrubStartLine(string value)
        {
            var firstSpace = value.IndexOf(' ');
            var lastSpace = value.LastIndexOf(' ');
            if (firstSpace > 0 && lastSpace > firstSpace)
            {
                var target = value[(firstSpace + 1)..lastSpace];
                return value[..(firstSpace + 1)] + ScrubUrl(target) + value[lastSpace..];
            }
            return ScrubText(value) ?? string.Empty;
        }

        private MapiMessageParse? ScrubMapiParse(MapiMessageParse? parse)
        {
            if (parse is null)
            {
                return null;
            }
            return parse with
            {
                Root = ScrubMapiNode(parse.Root, sensitiveAncestor: false),
                Warnings = parse.Warnings.Select(warning => ScrubText(warning) ?? string.Empty).ToImmutableArray()
            };
        }

        private MapiNode ScrubMapiNode(MapiNode node, bool sensitiveAncestor)
        {
            var sensitive = sensitiveAncestor || IsSensitiveName(node.Name);
            var name = ScrubText(node.Name) ?? string.Empty;
            string? value = node.Value;
            if (node.Kind == MapiNodeKind.Raw && value is not null)
            {
                value = Redact("BinaryPayload");
            }
            else if (value is not null)
            {
                value = sensitive
                    ? Redact(MarkerType(node.Name, value))
                    : ScrubText(value);
            }
            return node with
            {
                Name = name,
                Value = value,
                Children = node.Children.Select(child => ScrubMapiNode(child, sensitive)).ToImmutableArray()
            };
        }

        private void Record(string type)
        {
            lock (Counts)
            {
                Counts[type] = Counts.GetValueOrDefault(type) + 1;
            }
        }

        private bool IsSensitiveQueryName(string name) =>
            IsSensitiveName(name)
            || name.Equals("access_token", StringComparison.OrdinalIgnoreCase)
            || name.Equals("id_token", StringComparison.OrdinalIgnoreCase)
            || name.Equals("refresh_token", StringComparison.OrdinalIgnoreCase)
            || name.Equals("client_secret", StringComparison.OrdinalIgnoreCase)
            || name.Equals("api_key", StringComparison.OrdinalIgnoreCase)
            || name.Equals("apikey", StringComparison.OrdinalIgnoreCase);

        private static string SchemeType(string scheme) => scheme.ToLowerInvariant() switch
        {
            "basic" => "Basic",
            "bearer" => "Bearer",
            "ntlm" => "NTLM",
            "negotiate" => "Negotiate",
            "kerberos" => "Kerberos",
            _ => "Auth"
        };

        private static bool TryDecodeText(
            ReadOnlySpan<byte> bytes,
            string? charset,
            out string text,
            out Encoding encoding)
        {
            encoding = charset?.ToLowerInvariant() switch
            {
                "utf-16" or "utf-16le" or "unicode" => new UnicodeEncoding(false, true, true),
                "utf-16be" => new UnicodeEncoding(true, true, true),
                "us-ascii" or "ascii" => Encoding.GetEncoding(
                    Encoding.ASCII.CodePage,
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback),
                "iso-8859-1" or "latin1" => Encoding.Latin1,
                _ => new UTF8Encoding(false, true)
            };
            try
            {
                text = encoding.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                text = string.Empty;
                return false;
            }
            var controls = text.Count(character => char.IsControl(character)
                && character is not ('\r' or '\n' or '\t' or '\f'));
            return controls <= Math.Max(2, text.Length / 100);
        }

        private static bool LooksLikeXml(string value)
        {
            var trimmed = value.AsSpan().TrimStart();
            return trimmed.StartsWith("<", StringComparison.Ordinal)
                && trimmed.Contains(">", StringComparison.Ordinal);
        }

        private static bool LooksLikeJson(string value)
        {
            var trimmed = value.AsSpan().TrimStart();
            return trimmed.StartsWith("{", StringComparison.Ordinal)
                || trimmed.StartsWith("[", StringComparison.Ordinal);
        }

        private static bool IsTextualMediaType(string? contentType)
        {
            var mediaType = contentType?.Split(';', 2)[0].Trim() ?? string.Empty;
            return mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                || mediaType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase)
                || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
                || mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase)
                || mediaType.Contains("javascript", StringComparison.OrdinalIgnoreCase)
                || mediaType.Contains("graphql", StringComparison.OrdinalIgnoreCase)
                || mediaType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);
        }

        private static string DecodeName(string value) => DecodeValue(value.Replace('+', ' '));

        private static string DecodeValue(string value)
        {
            try
            {
                return Uri.UnescapeDataString(value.Replace('+', ' '));
            }
            catch (UriFormatException)
            {
                return value;
            }
        }

        private static string? AppendNote(string? value, string? note)
        {
            if (string.IsNullOrEmpty(note))
            {
                return value;
            }
            return string.IsNullOrWhiteSpace(value) ? note : value + " " + note;
        }

        private static string Compact(string value, int limit)
        {
            var compact = Regex.Replace(value, @"\s+", " ").Trim();
            return compact.Length <= limit ? compact : compact[..(limit - 1)] + "\u2026";
        }
    }
}
