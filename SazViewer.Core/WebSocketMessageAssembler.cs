using System.Text;

namespace SazViewer.Core;

internal static class WebSocketMessageAssembler
{
    private const int MaxRetainedMessageBytes = 1024 * 1024;
    private const int MaxPreviewCharacters = 240;
    private const int MaxHexPreviewBytes = 96;

    public static IReadOnlyList<WebSocketMessage> Assemble(
        string sessionId,
        int archiveOrder,
        IReadOnlyList<WebSocketFrame> frames)
    {
        var drafts = new List<MessageDraft>();
        var fragments = new Dictionary<string, MessageDraft>(StringComparer.Ordinal);

        foreach (var frame in frames.OrderBy(frame => frame.RecordIndex))
        {
            if (frame.Opcode is 8 or 9 or 10)
            {
                drafts.Add(MessageDraft.Single(frame, frame.Final && frame.PayloadLength <= 125));
                continue;
            }

            if (frame.Opcode == 0)
            {
                fragments.TryGetValue(frame.Direction, out var fragmented);
                if (!frame.IsDecoded || fragmented is null)
                {
                    if (fragmented is not null)
                    {
                        fragmented.AddSequenceWarning(
                            $"frame {frame.RecordIndex} could not continue the fragmented message; the sequence is incomplete");
                        drafts.Add(fragmented);
                        fragments.Remove(frame.Direction);
                    }

                    var orphan = MessageDraft.Single(frame, complete: false);
                    orphan.AddSequenceWarning(
                        frame.IsDecoded
                            ? $"frame {frame.RecordIndex} is an orphan continuation with no active fragmented message in the {frame.Direction} direction"
                            : $"frame {frame.RecordIndex} is an undecodable continuation and cannot be reassembled");
                    drafts.Add(orphan);
                    continue;
                }

                fragmented.Add(frame);
                if (frame.Final)
                {
                    fragmented.IsComplete = true;
                    drafts.Add(fragmented);
                    fragments.Remove(frame.Direction);
                }
                continue;
            }

            if (frame.Opcode is 1 or 2)
            {
                if (fragments.Remove(frame.Direction, out var unfinished))
                {
                    unfinished.AddSequenceWarning(
                        $"frame {frame.RecordIndex} starts a new {frame.Type.ToLowerInvariant()} message before the prior fragmented message finished");
                    drafts.Add(unfinished);
                }

                if (!frame.IsDecoded || frame.Final)
                {
                    drafts.Add(MessageDraft.Single(frame, frame.IsDecoded && frame.Final));
                }
                else
                {
                    fragments[frame.Direction] = MessageDraft.Single(frame, complete: false);
                }
                continue;
            }

            var unsupported = MessageDraft.Single(frame, complete: false);
            unsupported.AddSequenceWarning(
                frame.Opcode < 0
                    ? $"frame {frame.RecordIndex} could not be decoded as an RFC 6455 frame"
                    : $"frame {frame.RecordIndex} uses unsupported opcode 0x{frame.Opcode:X}");
            drafts.Add(unsupported);
        }

        foreach (var unfinished in fragments.Values)
        {
            unfinished.AddSequenceWarning("capture ended before the fragmented message received a final continuation frame");
            drafts.Add(unfinished);
        }

        return drafts
            .OrderBy(draft => draft.StartRecordIndex)
            .Select((draft, index) => draft.Build(sessionId, archiveOrder, index))
            .ToArray();
    }

    private sealed class MessageDraft
    {
        private readonly MemoryStream payload = new();
        private readonly List<string> sequenceWarnings = [];
        private long payloadLength;
        private bool payloadTruncated;

        private MessageDraft(WebSocketFrame frame, bool complete)
        {
            Type = frame.Type;
            Direction = frame.Direction;
            Timestamp = frame.Timestamp;
            StartRecordIndex = frame.RecordIndex;
            IsComplete = complete;
            Add(frame);
        }

        public string Type { get; }
        public string Direction { get; }
        public DateTimeOffset? Timestamp { get; }
        public int StartRecordIndex { get; }
        public bool IsComplete { get; set; }
        public List<WebSocketFrame> Frames { get; } = [];

        public static MessageDraft Single(WebSocketFrame frame, bool complete) => new(frame, complete);

        public void Add(WebSocketFrame frame)
        {
            Frames.Add(frame);
            try
            {
                payloadLength = checked(payloadLength + Math.Max(0, frame.PayloadLength));
            }
            catch (OverflowException)
            {
                payloadLength = long.MaxValue;
                payloadTruncated = true;
                AddSequenceWarning("logical payload length exceeds the supported range");
            }

            var available = frame.Payload.Span;
            var remaining = MaxRetainedMessageBytes - checked((int)payload.Length);
            if (remaining > 0)
            {
                payload.Write(available[..Math.Min(remaining, available.Length)]);
            }
            payloadTruncated |= frame.IsPayloadTruncated || available.Length > remaining;
        }

        public void AddSequenceWarning(string warning)
        {
            if (!sequenceWarnings.Contains(warning, StringComparer.Ordinal))
            {
                sequenceWarnings.Add(warning);
            }
        }

        public WebSocketMessage Build(string sessionId, int archiveOrder, int messageIndex)
        {
            var retained = payload.ToArray();
            var warnings = new List<string>();
            foreach (var frame in Frames)
            {
                if (!string.IsNullOrWhiteSpace(frame.Warning))
                {
                    warnings.Add($"Frame {frame.RecordIndex}: {frame.Warning}");
                }
            }
            warnings.AddRange(sequenceWarnings);
            if (payloadTruncated)
            {
                warnings.Add($"Logical payload retained for display/copy is limited to {retained.Length:N0} bytes.");
            }

            string? text = null;
            if (Type == "Text" && IsComplete && Frames.All(frame => frame.IsDecoded))
            {
                try
                {
                    var encoding = new UTF8Encoding(false, true);
                    if (payloadTruncated)
                    {
                        var characters = new char[encoding.GetMaxCharCount(retained.Length)];
                        encoding.GetDecoder().Convert(
                            retained,
                            characters,
                            flush: false,
                            out _,
                            out var charactersUsed,
                            out _);
                        text = new string(characters, 0, charactersUsed);
                    }
                    else
                    {
                        text = encoding.GetString(retained);
                    }
                }
                catch (DecoderFallbackException)
                {
                    warnings.Add("Reassembled text message is not valid UTF-8; Text and JSON views are unavailable.");
                }
            }

            var preview = text is not null
                ? BoundPreview(text)
                : retained.Length == 0
                    ? "(empty payload)"
                    : HttpMessageParser.HexPreview(retained.AsSpan(0, Math.Min(retained.Length, MaxHexPreviewBytes))).TrimEnd();
            if (retained.Length > MaxHexPreviewBytes && text is null)
            {
                preview += "\n[preview truncated]";
            }

            var message = new WebSocketMessage
            {
                SessionId = sessionId,
                MessageIndex = messageIndex,
                RecordIndex = StartRecordIndex,
                Timestamp = Timestamp,
                Direction = Direction,
                Type = Type,
                PayloadLength = payloadLength,
                Preview = preview,
                IsBinary = Type != "Text",
                IsDecoded = IsComplete && Frames.All(frame => frame.IsDecoded),
                IsComplete = IsComplete,
                IsFragmented = Frames.Count > 1 || Frames[0].Opcode == 0 || !Frames[0].Final,
                IsPayloadTruncated = payloadTruncated,
                Text = text,
                Warning = warnings.Count == 0 ? null : string.Join("; ", warnings),
                Payload = retained,
                SourceOrder = ((long)archiveOrder << 32) | (uint)StartRecordIndex
            };
            message.Frames.AddRange(Frames.Select(MetadataOnly));
            return message;
        }

        private static WebSocketFrame MetadataOnly(WebSocketFrame frame) =>
            new()
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
                IsPayloadTruncated = frame.IsPayloadTruncated,
                Warning = frame.Warning
            };

        private static string BoundPreview(string text) =>
            text.Length <= MaxPreviewCharacters
                ? text
                : text[..MaxPreviewCharacters] + "\n[preview truncated]";
    }
}
