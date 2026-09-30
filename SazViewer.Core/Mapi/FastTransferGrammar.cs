using System.Collections.Immutable;

namespace SazViewer.Core;

internal enum FastTransferRootKind
{
    Unknown,
    ContentsSync,
    HierarchySync,
    State,
    FolderContent,
    MessageContent,
    AttachmentContent,
    MessageList,
    TopFolder,
}

internal enum FastTransferGrammarPhase
{
    None,
    ContentsStart,
    ContentsChanges,
    ContentsProgressTotal,
    ContentsAfterProgress,
    ContentsFullHeader,
    ContentsMessageBody,
    ContentsGroupInfo,
    ContentsPartialAwaitChange,
    ContentsPartialBody,
    ContentsDeletions,
    ContentsReadState,
    ContentsErrorInfo,
    ContentsAfterError,
    HierarchyChanges,
    HierarchyFolderChange,
    HierarchyDeletions,
    MessageListReady,
    MessageListAfterPrefix,
    MessageListAfterWarning,
    MessageListErrorInfo,
    TopFolderStart,
    TopFolderAfterPrefix,
    TopFolderProperties,
    TopFolderMessageReady,
    TopFolderMessageAfterPrefix,
    TopFolderMessageAfterWarning,
    TopFolderSubFolders,
    TopFolderWarningAmbiguous,
    TopFolderErrorInfo,
    ObjectMessageProperties,
    ObjectMessageRecipients,
    ObjectMessageAttachments,
    ObjectRecipientProperties,
    ObjectAttachmentAwaitNumber,
    ObjectAttachmentProperties,
    ObjectAttachmentAfterEmbedded,
    AwaitStateBegin,
    StateProperties,
    AwaitSyncEnd,
    Complete,
}

internal enum FastTransferProductionKind
{
    Folder,
    Message,
    Recipient,
    Attachment,
}

internal enum FastTransferProductionPhase
{
    FolderProperties,
    FolderWarningAmbiguous,
    FolderMessageReady,
    FolderMessageAfterPrefix,
    FolderMessageAfterWarning,
    FolderSubFolders,
    FolderErrorInfo,
    MessageStart,
    MessageProperties,
    MessageAfterRecipientDelimiter,
    MessageRecipients,
    MessageAfterAttachmentDelimiter,
    MessageAttachments,
    RecipientProperties,
    AttachmentAwaitNumber,
    AttachmentStart,
    AttachmentProperties,
    AttachmentAfterEmbedded,
}

internal readonly record struct FastTransferProductionFrame(
    FastTransferProductionKind Kind,
    FastTransferProductionPhase Phase,
    int SyntaxDepth,
    uint EndTag);

internal readonly record struct FastTransferGrammarState(
    FastTransferRootKind Root,
    FastTransferGrammarPhase Phase,
    string? Provenance,
    bool AmbiguityReported = false,
    ImmutableArray<FastTransferProductionFrame> ProductionStack = default)
{
    public static FastTransferGrammarState Unconfigured { get; } =
        new(FastTransferRootKind.Unknown, FastTransferGrammarPhase.None, null);

    public bool IsConfigured => Provenance is not null;

    public bool IsValidated => Root is
        FastTransferRootKind.ContentsSync or
        FastTransferRootKind.HierarchySync or
        FastTransferRootKind.State or
        FastTransferRootKind.MessageList or
        FastTransferRootKind.TopFolder or
        FastTransferRootKind.FolderContent or
        FastTransferRootKind.MessageContent or
        FastTransferRootKind.AttachmentContent;

    public bool IsComplete => Phase == FastTransferGrammarPhase.Complete;

    public static FastTransferGrammarState ForRoot(FastTransferRootKind root, string provenance)
    {
        var state = new FastTransferGrammarState(
            root,
            root switch
            {
                FastTransferRootKind.ContentsSync => FastTransferGrammarPhase.ContentsStart,
                FastTransferRootKind.HierarchySync => FastTransferGrammarPhase.HierarchyChanges,
                FastTransferRootKind.State => FastTransferGrammarPhase.AwaitStateBegin,
                FastTransferRootKind.MessageList => FastTransferGrammarPhase.MessageListReady,
                FastTransferRootKind.TopFolder => FastTransferGrammarPhase.TopFolderStart,
                FastTransferRootKind.FolderContent => FastTransferGrammarPhase.TopFolderProperties,
                FastTransferRootKind.MessageContent => FastTransferGrammarPhase.ObjectMessageProperties,
                FastTransferRootKind.AttachmentContent => FastTransferGrammarPhase.ObjectAttachmentProperties,
                _ => FastTransferGrammarPhase.None,
            },
            provenance);
        return root switch
        {
            FastTransferRootKind.FolderContent => state with
            {
                ProductionStack =
                [
                    new FastTransferProductionFrame(
                    FastTransferProductionKind.Folder,
                    FastTransferProductionPhase.FolderProperties,
                    0,
                    0),
                ],
            },
            FastTransferRootKind.MessageContent => state with
            {
                ProductionStack =
                [
                    new FastTransferProductionFrame(
                    FastTransferProductionKind.Message,
                    FastTransferProductionPhase.MessageStart,
                    0,
                    0),
                ],
            },
            FastTransferRootKind.AttachmentContent => state with
            {
                ProductionStack =
                [
                    new FastTransferProductionFrame(
                    FastTransferProductionKind.Attachment,
                    FastTransferProductionPhase.AttachmentStart,
                    0,
                    0),
                ],
            },
            _ => state,
        };
    }
}

/// <summary>
/// Online, capture-local validator for the MS-OXCFXICS 2.2.4.2 synchronization productions.
/// The lexical parser remains authoritative for byte boundaries; this validator only advances at
/// complete lexical-element boundaries and never scans ahead or guesses after a phase mismatch.
/// </summary>
internal static class FastTransferGrammar
{
    private const uint StartEmbed = 0x40010003;
    private const uint EndEmbed = 0x40020003;
    private const uint StartRecip = 0x40030003;
    private const uint EndToRecip = 0x40040003;
    private const uint StartTopFolder = 0x40090003;
    private const uint StartSubFolder = 0x400A0003;
    private const uint EndFolder = 0x400B0003;
    private const uint StartMessage = 0x400C0003;
    private const uint EndMessage = 0x400D0003;
    private const uint NewAttach = 0x40000003;
    private const uint EndAttach = 0x400E0003;
    private const uint StartFaiMessage = 0x40100003;
    private const uint IncrSyncChg = 0x40120003;
    private const uint IncrSyncChgPartial = 0x407D0003;
    private const uint IncrSyncDel = 0x40130003;
    private const uint IncrSyncEnd = 0x40140003;
    private const uint IncrSyncRead = 0x402F0003;
    private const uint IncrSyncStateBegin = 0x403A0003;
    private const uint IncrSyncStateEnd = 0x403B0003;
    private const uint IncrSyncProgressMode = 0x4074000B;
    private const uint IncrSyncProgressPerMessage = 0x4075000B;
    private const uint IncrSyncMessage = 0x40150003;
    private const uint IncrSyncGroupInfo = 0x407B0102;
    private const uint FxErrorInfo = 0x40180003;
    private const uint MetaTagDnPrefix = 0x4008001E;
    private const uint MetaTagEcWarning = 0x400F0003;
    private const uint MetaTagNewFxFolder = 0x40110102;
    private const uint MetaTagFxDelProp = 0x40160003;
    private const uint MetaTagIncrementalSyncMessagePartial = 0x407A0003;
    private const uint MetaTagIncrSyncGroupId = 0x407C0003;

    public static FastTransferGrammarState AdvanceProperty(
        FastTransferGrammarState state,
        uint tag,
        long offset,
        int syntaxDepth)
    {
        if (!state.IsValidated)
        {
            return state;
        }

        if (state.Root is
            FastTransferRootKind.MessageList or
            FastTransferRootKind.TopFolder or
            FastTransferRootKind.FolderContent or
            FastTransferRootKind.MessageContent or
            FastTransferRootKind.AttachmentContent)
        {
            return AdvanceObjectProperty(state, tag, offset, syntaxDepth);
        }

        return state.Phase switch
        {
            FastTransferGrammarPhase.ContentsProgressTotal or
            FastTransferGrammarPhase.ContentsAfterProgress or
            FastTransferGrammarPhase.ContentsFullHeader or
            FastTransferGrammarPhase.ContentsMessageBody or
            FastTransferGrammarPhase.ContentsGroupInfo or
            FastTransferGrammarPhase.ContentsPartialBody or
            FastTransferGrammarPhase.ContentsDeletions or
            FastTransferGrammarPhase.ContentsReadState or
            FastTransferGrammarPhase.HierarchyFolderChange or
            FastTransferGrammarPhase.HierarchyDeletions or
            FastTransferGrammarPhase.StateProperties => state,
            FastTransferGrammarPhase.ContentsErrorInfo when (tag & 0xFFFF) == 0x0102 =>
                state with { Phase = FastTransferGrammarPhase.ContentsAfterError },
            _ => Invalid(state, offset, "a property value"),
        };
    }

    public static FastTransferGrammarState AdvanceMetaProperty(
        FastTransferGrammarState state,
        uint tag,
        long offset,
        int syntaxDepth)
    {
        if (!state.IsValidated)
        {
            return state;
        }

        if (state.Root is
            FastTransferRootKind.MessageList or
            FastTransferRootKind.TopFolder or
            FastTransferRootKind.FolderContent or
            FastTransferRootKind.MessageContent or
            FastTransferRootKind.AttachmentContent)
        {
            return AdvanceObjectMetaProperty(state, tag, offset, syntaxDepth);
        }

        if (state.Phase == FastTransferGrammarPhase.ContentsGroupInfo
            && tag == MetaTagIncrSyncGroupId
            && syntaxDepth == 0)
        {
            return state with { Phase = FastTransferGrammarPhase.ContentsPartialAwaitChange };
        }

        if (state.Phase == FastTransferGrammarPhase.ContentsPartialBody
            && tag == MetaTagIncrementalSyncMessagePartial
            && syntaxDepth == 0)
        {
            return state;
        }

        if (state.Phase is FastTransferGrammarPhase.ContentsMessageBody
            or FastTransferGrammarPhase.ContentsPartialBody
            && (syntaxDepth > 0 || tag is MetaTagDnPrefix or MetaTagFxDelProp))
        {
            return state;
        }

        return Invalid(
            state,
            offset,
            FastTransferStreamLexer.MetaPropertyNames.TryGetValue(tag, out var name)
                ? name
                : $"meta-property 0x{tag:X8}");
    }

    public static FastTransferGrammarState AdvanceMarker(
        FastTransferGrammarState state,
        uint tag,
        long offset,
        int syntaxDepthBefore,
        int syntaxDepthAfter)
    {
        if (!state.IsValidated)
        {
            return state;
        }

        return state.Root switch
        {
            FastTransferRootKind.ContentsSync =>
                AdvanceContents(state, tag, offset, syntaxDepthBefore, syntaxDepthAfter),
            FastTransferRootKind.HierarchySync =>
                AdvanceHierarchy(state, tag, offset, syntaxDepthBefore, syntaxDepthAfter),
            FastTransferRootKind.State =>
                AdvanceState(state, tag, offset, syntaxDepthBefore),
            FastTransferRootKind.MessageList =>
                AdvanceMessageList(state, tag, offset, syntaxDepthBefore, syntaxDepthAfter),
            FastTransferRootKind.TopFolder =>
                AdvanceTopFolder(state, tag, offset, syntaxDepthBefore, syntaxDepthAfter),
            FastTransferRootKind.FolderContent =>
                tag == FxErrorInfo
                    ? AdvanceRecoveryError(state, offset, syntaxDepthAfter)
                    : AdvanceObjectMarker(state, tag, offset, syntaxDepthBefore, syntaxDepthAfter),
            FastTransferRootKind.MessageContent or
            FastTransferRootKind.AttachmentContent =>
                AdvanceObjectMarker(state, tag, offset, syntaxDepthBefore, syntaxDepthAfter),
            _ => state,
        };
    }

    public static bool CanComplete(FastTransferGrammarState state) =>
        state.IsComplete
        || state.Root == FastTransferRootKind.MessageList
        && Frames(state).IsEmpty
        && state.Phase is
            FastTransferGrammarPhase.MessageListReady or
            FastTransferGrammarPhase.MessageListAfterPrefix or
            FastTransferGrammarPhase.MessageListAfterWarning
        || CanCompleteObjectContent(state);

    public static FastTransferGrammarState Finalize(FastTransferGrammarState state) =>
        CanComplete(state)
            ? state with { Phase = FastTransferGrammarPhase.Complete }
            : state;

    private static FastTransferGrammarState AdvanceContents(
        FastTransferGrammarState state,
        uint tag,
        long offset,
        int depthBefore,
        int depthAfter)
    {
        if (tag == FxErrorInfo
            && state.Phase is not FastTransferGrammarPhase.Complete
                and not FastTransferGrammarPhase.ContentsErrorInfo)
        {
            return state with { Phase = FastTransferGrammarPhase.ContentsErrorInfo };
        }

        if (state.Phase is FastTransferGrammarPhase.ContentsMessageBody
            or FastTransferGrammarPhase.ContentsPartialBody)
        {
            if (IsMessageChildMarker(tag)
                && (depthBefore > 0
                    || (depthBefore == 0 && tag is StartRecip or NewAttach)
                    || (depthAfter == 0 && tag is EndToRecip or EndAttach)))
            {
                return state;
            }

            if (depthBefore > 0)
            {
                return Invalid(state, offset, Marker(tag));
            }

            state = state with { Phase = FastTransferGrammarPhase.ContentsChanges };
        }
        else if (depthBefore > 0 && tag != IncrSyncStateEnd)
        {
            return Invalid(state, offset, Marker(tag));
        }

        return state.Phase switch
        {
            FastTransferGrammarPhase.ContentsStart when tag == IncrSyncProgressMode =>
                state with { Phase = FastTransferGrammarPhase.ContentsProgressTotal },
            FastTransferGrammarPhase.ContentsStart =>
                AdvanceContents(state with { Phase = FastTransferGrammarPhase.ContentsChanges }, tag, offset, depthBefore, depthAfter),

            FastTransferGrammarPhase.ContentsProgressTotal when tag is
                IncrSyncProgressPerMessage or IncrSyncChg or IncrSyncGroupInfo or
                IncrSyncDel or IncrSyncRead or IncrSyncStateBegin =>
                AdvanceContents(state with { Phase = FastTransferGrammarPhase.ContentsChanges }, tag, offset, depthBefore, depthAfter),
            FastTransferGrammarPhase.ContentsAfterError =>
                AdvanceContents(state with { Phase = FastTransferGrammarPhase.ContentsChanges }, tag, offset, depthBefore, depthAfter),

            FastTransferGrammarPhase.ContentsChanges when tag == IncrSyncProgressPerMessage =>
                state with { Phase = FastTransferGrammarPhase.ContentsAfterProgress },
            FastTransferGrammarPhase.ContentsChanges when tag == IncrSyncChg =>
                state with { Phase = FastTransferGrammarPhase.ContentsFullHeader },
            FastTransferGrammarPhase.ContentsChanges when tag == IncrSyncGroupInfo =>
                state with { Phase = FastTransferGrammarPhase.ContentsGroupInfo },
            FastTransferGrammarPhase.ContentsChanges when tag == IncrSyncDel =>
                state with { Phase = FastTransferGrammarPhase.ContentsDeletions },
            FastTransferGrammarPhase.ContentsChanges when tag == IncrSyncRead =>
                state with { Phase = FastTransferGrammarPhase.ContentsReadState },
            FastTransferGrammarPhase.ContentsChanges when tag == IncrSyncStateBegin =>
                state with { Phase = FastTransferGrammarPhase.StateProperties },

            FastTransferGrammarPhase.ContentsAfterProgress when tag == IncrSyncChg =>
                state with { Phase = FastTransferGrammarPhase.ContentsFullHeader },
            FastTransferGrammarPhase.ContentsAfterProgress when tag == IncrSyncGroupInfo =>
                state with { Phase = FastTransferGrammarPhase.ContentsGroupInfo },

            FastTransferGrammarPhase.ContentsFullHeader when tag == IncrSyncMessage =>
                state with { Phase = FastTransferGrammarPhase.ContentsMessageBody },
            FastTransferGrammarPhase.ContentsPartialAwaitChange when tag == IncrSyncChgPartial =>
                state with { Phase = FastTransferGrammarPhase.ContentsPartialBody },

            FastTransferGrammarPhase.ContentsDeletions when tag == IncrSyncRead =>
                state with { Phase = FastTransferGrammarPhase.ContentsReadState },
            FastTransferGrammarPhase.ContentsDeletions when tag == IncrSyncStateBegin =>
                state with { Phase = FastTransferGrammarPhase.StateProperties },
            FastTransferGrammarPhase.ContentsReadState when tag == IncrSyncStateBegin =>
                state with { Phase = FastTransferGrammarPhase.StateProperties },
            FastTransferGrammarPhase.StateProperties when tag == IncrSyncStateEnd && depthBefore == 1 =>
                state with { Phase = FastTransferGrammarPhase.AwaitSyncEnd },
            FastTransferGrammarPhase.AwaitSyncEnd when tag == IncrSyncEnd =>
                state with { Phase = FastTransferGrammarPhase.Complete },

            _ => Invalid(state, offset, Marker(tag)),
        };
    }

    private static FastTransferGrammarState AdvanceHierarchy(
        FastTransferGrammarState state,
        uint tag,
        long offset,
        int depthBefore,
        int depthAfter)
    {
        _ = depthAfter;
        if (depthBefore > 0 && tag != IncrSyncStateEnd)
        {
            return Invalid(state, offset, Marker(tag));
        }

        return state.Phase switch
        {
            FastTransferGrammarPhase.HierarchyChanges when tag == IncrSyncChg =>
                state with { Phase = FastTransferGrammarPhase.HierarchyFolderChange },
            FastTransferGrammarPhase.HierarchyChanges when tag == IncrSyncDel =>
                state with { Phase = FastTransferGrammarPhase.HierarchyDeletions },
            FastTransferGrammarPhase.HierarchyChanges when tag == IncrSyncStateBegin =>
                state with { Phase = FastTransferGrammarPhase.StateProperties },

            FastTransferGrammarPhase.HierarchyFolderChange when tag == IncrSyncChg =>
                state,
            FastTransferGrammarPhase.HierarchyFolderChange when tag == IncrSyncDel =>
                state with { Phase = FastTransferGrammarPhase.HierarchyDeletions },
            FastTransferGrammarPhase.HierarchyFolderChange when tag == IncrSyncStateBegin =>
                state with { Phase = FastTransferGrammarPhase.StateProperties },

            FastTransferGrammarPhase.HierarchyDeletions when tag == IncrSyncStateBegin =>
                state with { Phase = FastTransferGrammarPhase.StateProperties },
            FastTransferGrammarPhase.StateProperties when tag == IncrSyncStateEnd && depthBefore == 1 =>
                state with { Phase = FastTransferGrammarPhase.AwaitSyncEnd },
            FastTransferGrammarPhase.AwaitSyncEnd when tag == IncrSyncEnd =>
                state with { Phase = FastTransferGrammarPhase.Complete },

            _ => Invalid(state, offset, Marker(tag)),
        };
    }

    private static FastTransferGrammarState AdvanceState(
        FastTransferGrammarState state,
        uint tag,
        long offset,
        int depthBefore) =>
        state.Phase switch
        {
            FastTransferGrammarPhase.AwaitStateBegin when tag == IncrSyncStateBegin && depthBefore == 0 =>
                state with { Phase = FastTransferGrammarPhase.StateProperties },
            FastTransferGrammarPhase.StateProperties when tag == IncrSyncStateEnd && depthBefore == 1 =>
                state with { Phase = FastTransferGrammarPhase.Complete },
            _ => Invalid(state, offset, Marker(tag)),
        };

    private static FastTransferGrammarState AdvanceMessageList(
        FastTransferGrammarState state,
        uint tag,
        long offset,
        int depthBefore,
        int depthAfter)
    {
        if (tag == FxErrorInfo)
        {
            return AdvanceRecoveryError(state, offset, depthAfter);
        }

        if (!Frames(state).IsEmpty)
        {
            return AdvanceObjectMarker(state, tag, offset, depthBefore, depthAfter);
        }

        return (state.Phase, tag, depthBefore, depthAfter) switch
        {
            (FastTransferGrammarPhase.MessageListReady, StartMessage or StartFaiMessage, 0, 1) or
            (FastTransferGrammarPhase.MessageListAfterPrefix, StartMessage or StartFaiMessage, 0, 1) or
            (FastTransferGrammarPhase.MessageListAfterWarning, StartMessage or StartFaiMessage, 0, 1) =>
                Push(
                    state,
                    new FastTransferProductionFrame(
                        FastTransferProductionKind.Message,
                        FastTransferProductionPhase.MessageStart,
                        depthAfter,
                        EndMessage)),
            _ => Invalid(state, offset, Marker(tag)),
        };
    }

    private static FastTransferGrammarState AdvanceTopFolder(
        FastTransferGrammarState state,
        uint tag,
        long offset,
        int depthBefore,
        int depthAfter)
    {
        if (tag == FxErrorInfo)
        {
            return AdvanceRecoveryError(state, offset, depthAfter);
        }

        if (!Frames(state).IsEmpty)
        {
            return AdvanceObjectMarker(state, tag, offset, depthBefore, depthAfter);
        }

        return (state.Phase, tag, depthBefore, depthAfter) switch
        {
            (FastTransferGrammarPhase.TopFolderStart, StartTopFolder, 0, 1) or
            (FastTransferGrammarPhase.TopFolderAfterPrefix, StartTopFolder, 0, 1) =>
                Push(
                    state,
                    new FastTransferProductionFrame(
                        FastTransferProductionKind.Folder,
                        FastTransferProductionPhase.FolderProperties,
                        depthAfter,
                        EndFolder)),
            _ => Invalid(state, offset, Marker(tag)),
        };
    }

    public static int? RecoverySyntaxDepth(FastTransferGrammarState state)
    {
        if (!state.IsValidated || state.IsComplete)
        {
            return null;
        }

        if (state.Root == FastTransferRootKind.ContentsSync
            && state.Phase != FastTransferGrammarPhase.ContentsErrorInfo)
        {
            return 0;
        }

        if (state.Root == FastTransferRootKind.MessageList)
        {
            return state.Phase == FastTransferGrammarPhase.MessageListErrorInfo
                ? null
                : 0;
        }

        if (state.Root is not (
            FastTransferRootKind.TopFolder or
            FastTransferRootKind.FolderContent))
        {
            return null;
        }

        var frames = Frames(state);
        for (var index = frames.Length - 1; index >= 0; index--)
        {
            var frame = frames[index];
            if (frame.Kind != FastTransferProductionKind.Folder)
            {
                continue;
            }

            var insideMessage = frames
                .Skip(index + 1)
                .Any(candidate => candidate.Kind == FastTransferProductionKind.Message);
            var atMessageList = frame.Phase is
                FastTransferProductionPhase.FolderProperties or
                FastTransferProductionPhase.FolderWarningAmbiguous or
                FastTransferProductionPhase.FolderMessageReady or
                FastTransferProductionPhase.FolderMessageAfterPrefix or
                FastTransferProductionPhase.FolderMessageAfterWarning;
            if (insideMessage || atMessageList)
            {
                return frame.SyntaxDepth;
            }
        }

        return null;
    }

    private static FastTransferGrammarState AdvanceObjectProperty(
        FastTransferGrammarState state,
        uint tag,
        long offset,
        int syntaxDepth)
    {
        var frames = Frames(state);
        if (frames.IsEmpty)
        {
            if (state.Root == FastTransferRootKind.MessageList
                && state.Phase == FastTransferGrammarPhase.MessageListErrorInfo
                && syntaxDepth == 0
                && (tag & 0xFFFF) == 0x0102)
            {
                return state with { Phase = FastTransferGrammarPhase.MessageListReady };
            }

            return Invalid(state, offset, "a property value");
        }

        var frame = frames[^1];
        if (syntaxDepth != frame.SyntaxDepth)
        {
            return Invalid(state, offset, "a property value at an unexpected nesting depth");
        }

        return frame.Kind switch
        {
            FastTransferProductionKind.Folder when frame.Phase is
                FastTransferProductionPhase.FolderProperties or
                FastTransferProductionPhase.FolderWarningAmbiguous =>
                ReplaceTop(state, frame with { Phase = FastTransferProductionPhase.FolderProperties }),

            FastTransferProductionKind.Folder when frame.Phase == FastTransferProductionPhase.FolderErrorInfo
                && (tag & 0xFFFF) == 0x0102 =>
                ReplaceTop(state, frame with { Phase = FastTransferProductionPhase.FolderMessageReady }),

            FastTransferProductionKind.Message when frame.Phase is
                FastTransferProductionPhase.MessageStart or
                FastTransferProductionPhase.MessageProperties =>
                ReplaceTop(state, frame with { Phase = FastTransferProductionPhase.MessageProperties }),

            FastTransferProductionKind.Recipient =>
                state,

            FastTransferProductionKind.Attachment when frame.Phase ==
                FastTransferProductionPhase.AttachmentAwaitNumber
                && tag == 0x0E210003 =>
                ReplaceTop(state, frame with { Phase = FastTransferProductionPhase.AttachmentStart }),

            FastTransferProductionKind.Attachment when frame.Phase is
                FastTransferProductionPhase.AttachmentStart or
                FastTransferProductionPhase.AttachmentProperties =>
                ReplaceTop(state, frame with { Phase = FastTransferProductionPhase.AttachmentProperties }),

            _ => Invalid(state, offset, "a property value"),
        };
    }

    private static FastTransferGrammarState AdvanceObjectMetaProperty(
        FastTransferGrammarState state,
        uint tag,
        long offset,
        int syntaxDepth)
    {
        var frames = Frames(state);
        if (frames.IsEmpty)
        {
            if (state.Root == FastTransferRootKind.MessageList)
            {
                return (state.Phase, tag, syntaxDepth) switch
                {
                    (FastTransferGrammarPhase.MessageListReady, MetaTagDnPrefix, 0) =>
                        state with { Phase = FastTransferGrammarPhase.MessageListAfterPrefix },
                    (FastTransferGrammarPhase.MessageListAfterPrefix, MetaTagDnPrefix, 0) or
                    (FastTransferGrammarPhase.MessageListAfterWarning, MetaTagDnPrefix, 0) =>
                        state with { Phase = FastTransferGrammarPhase.MessageListAfterPrefix },
                    (FastTransferGrammarPhase.MessageListReady, MetaTagEcWarning, 0) or
                    (FastTransferGrammarPhase.MessageListAfterPrefix, MetaTagEcWarning, 0) =>
                        state with { Phase = FastTransferGrammarPhase.MessageListAfterWarning },
                    (FastTransferGrammarPhase.MessageListAfterWarning, MetaTagEcWarning, 0) =>
                        state,
                    _ => Invalid(state, offset, MetaProperty(tag)),
                };
            }

            return (state.Phase, tag, syntaxDepth) switch
            {
                (FastTransferGrammarPhase.TopFolderStart, MetaTagDnPrefix, 0) =>
                    state with { Phase = FastTransferGrammarPhase.TopFolderAfterPrefix },
                _ => Invalid(state, offset, MetaProperty(tag)),
            };
        }

        var frame = frames[^1];
        if (syntaxDepth != frame.SyntaxDepth)
        {
            return Invalid(state, offset, $"{MetaProperty(tag)} at an unexpected nesting depth");
        }

        return frame.Kind switch
        {
            FastTransferProductionKind.Folder =>
                AdvanceFolderMetaProperty(state, frame, tag, offset),
            FastTransferProductionKind.Message =>
                AdvanceMessageMetaProperty(state, frame, tag, offset),
            FastTransferProductionKind.Attachment when
                frame.Phase == FastTransferProductionPhase.AttachmentStart
                && tag == MetaTagDnPrefix =>
                ReplaceTop(state, frame with { Phase = FastTransferProductionPhase.AttachmentProperties }),
            _ => Invalid(state, offset, MetaProperty(tag)),
        };
    }

    private static FastTransferGrammarState AdvanceFolderMetaProperty(
        FastTransferGrammarState state,
        FastTransferProductionFrame frame,
        uint tag,
        long offset) =>
        (frame.Phase, tag) switch
        {
            (FastTransferProductionPhase.FolderProperties, MetaTagDnPrefix) or
            (FastTransferProductionPhase.FolderMessageReady, MetaTagDnPrefix) or
            (FastTransferProductionPhase.FolderWarningAmbiguous, MetaTagDnPrefix) or
            (FastTransferProductionPhase.FolderMessageAfterPrefix, MetaTagDnPrefix) or
            (FastTransferProductionPhase.FolderMessageAfterWarning, MetaTagDnPrefix) =>
                ReplaceTop(state, frame with { Phase = FastTransferProductionPhase.FolderMessageAfterPrefix }),

            (FastTransferProductionPhase.FolderProperties, MetaTagEcWarning) =>
                ReplaceTop(state, frame with { Phase = FastTransferProductionPhase.FolderWarningAmbiguous }),

            (FastTransferProductionPhase.FolderWarningAmbiguous, MetaTagEcWarning) =>
                state,

            (FastTransferProductionPhase.FolderMessageReady, MetaTagEcWarning) or
            (FastTransferProductionPhase.FolderMessageAfterPrefix, MetaTagEcWarning) =>
                ReplaceTop(state, frame with { Phase = FastTransferProductionPhase.FolderMessageAfterWarning }),

            (FastTransferProductionPhase.FolderMessageAfterWarning, MetaTagEcWarning) =>
                state,

            (FastTransferProductionPhase.FolderProperties, MetaTagNewFxFolder) or
            (FastTransferProductionPhase.FolderWarningAmbiguous, MetaTagNewFxFolder) =>
                ReplaceTop(state, frame with { Phase = FastTransferProductionPhase.FolderSubFolders }),

            _ => Invalid(state, offset, MetaProperty(tag)),
        };

    private static FastTransferGrammarState AdvanceMessageMetaProperty(
        FastTransferGrammarState state,
        FastTransferProductionFrame frame,
        uint tag,
        long offset) =>
        (frame.Phase, tag) switch
        {
            (FastTransferProductionPhase.MessageStart, MetaTagDnPrefix) =>
                ReplaceTop(state, frame with { Phase = FastTransferProductionPhase.MessageProperties }),

            (FastTransferProductionPhase.MessageStart, MetaTagFxDelProp) or
            (FastTransferProductionPhase.MessageProperties, MetaTagFxDelProp) =>
                ReplaceTop(state, frame with { Phase = FastTransferProductionPhase.MessageAfterRecipientDelimiter }),

            (FastTransferProductionPhase.MessageAfterRecipientDelimiter, MetaTagFxDelProp) or
            (FastTransferProductionPhase.MessageRecipients, MetaTagFxDelProp) =>
                ReplaceTop(state, frame with { Phase = FastTransferProductionPhase.MessageAfterAttachmentDelimiter }),

            _ => Invalid(state, offset, MetaProperty(tag)),
        };

    private static FastTransferGrammarState AdvanceObjectMarker(
        FastTransferGrammarState state,
        uint tag,
        long offset,
        int depthBefore,
        int depthAfter)
    {
        var frame = Frames(state)[^1];
        return frame.Kind switch
        {
            FastTransferProductionKind.Folder =>
                AdvanceFolderMarker(state, frame, tag, offset, depthBefore, depthAfter),
            FastTransferProductionKind.Message =>
                AdvanceMessageMarker(state, frame, tag, offset, depthBefore, depthAfter),
            FastTransferProductionKind.Recipient =>
                AdvanceRecipientMarker(state, frame, tag, offset, depthBefore, depthAfter),
            FastTransferProductionKind.Attachment =>
                AdvanceAttachmentMarker(state, frame, tag, offset, depthBefore, depthAfter),
            _ => Invalid(state, offset, Marker(tag)),
        };
    }

    private static FastTransferGrammarState AdvanceFolderMarker(
        FastTransferGrammarState state,
        FastTransferProductionFrame frame,
        uint tag,
        long offset,
        int depthBefore,
        int depthAfter)
    {
        if (frame.EndTag != 0
            && tag == frame.EndTag
            && depthBefore == frame.SyntaxDepth
            && depthAfter == frame.SyntaxDepth - 1
            && frame.Phase is
                FastTransferProductionPhase.FolderProperties or
                FastTransferProductionPhase.FolderWarningAmbiguous or
                FastTransferProductionPhase.FolderMessageReady or
                FastTransferProductionPhase.FolderMessageAfterPrefix or
                FastTransferProductionPhase.FolderMessageAfterWarning or
                FastTransferProductionPhase.FolderSubFolders)
        {
            return Pop(state, FastTransferProductionKind.Folder);
        }

        if (tag is StartMessage or StartFaiMessage
            && depthBefore == frame.SyntaxDepth
            && depthAfter == frame.SyntaxDepth + 1
            && frame.Phase is
                FastTransferProductionPhase.FolderProperties or
                FastTransferProductionPhase.FolderWarningAmbiguous or
                FastTransferProductionPhase.FolderMessageReady or
                FastTransferProductionPhase.FolderMessageAfterPrefix or
                FastTransferProductionPhase.FolderMessageAfterWarning)
        {
            state = ReplaceTop(
                state,
                frame with { Phase = FastTransferProductionPhase.FolderMessageReady });
            return Push(
                state,
                new FastTransferProductionFrame(
                    FastTransferProductionKind.Message,
                    FastTransferProductionPhase.MessageStart,
                    depthAfter,
                    EndMessage));
        }

        if (tag == StartSubFolder
            && depthBefore == frame.SyntaxDepth
            && depthAfter == frame.SyntaxDepth + 1
            && frame.Phase is
                FastTransferProductionPhase.FolderProperties or
                FastTransferProductionPhase.FolderWarningAmbiguous or
                FastTransferProductionPhase.FolderMessageReady or
                FastTransferProductionPhase.FolderMessageAfterPrefix or
                FastTransferProductionPhase.FolderMessageAfterWarning or
                FastTransferProductionPhase.FolderSubFolders)
        {
            state = ReplaceTop(
                state,
                frame with { Phase = FastTransferProductionPhase.FolderSubFolders });
            return Push(
                state,
                new FastTransferProductionFrame(
                    FastTransferProductionKind.Folder,
                    FastTransferProductionPhase.FolderProperties,
                    depthAfter,
                    EndFolder));
        }

        return Invalid(state, offset, Marker(tag));
    }

    private static FastTransferGrammarState AdvanceMessageMarker(
        FastTransferGrammarState state,
        FastTransferProductionFrame frame,
        uint tag,
        long offset,
        int depthBefore,
        int depthAfter)
    {
        if (frame.EndTag != 0
            && tag == frame.EndTag
            && depthBefore == frame.SyntaxDepth
            && depthAfter == frame.SyntaxDepth - 1
            && frame.Phase is
                FastTransferProductionPhase.MessageStart or
                FastTransferProductionPhase.MessageProperties or
                FastTransferProductionPhase.MessageAfterRecipientDelimiter or
                FastTransferProductionPhase.MessageRecipients or
                FastTransferProductionPhase.MessageAfterAttachmentDelimiter or
                FastTransferProductionPhase.MessageAttachments)
        {
            return Pop(state, FastTransferProductionKind.Message);
        }

        if (tag == StartRecip
            && depthBefore == frame.SyntaxDepth
            && depthAfter == frame.SyntaxDepth + 1
            && frame.Phase is
                FastTransferProductionPhase.MessageStart or
                FastTransferProductionPhase.MessageProperties or
                FastTransferProductionPhase.MessageAfterRecipientDelimiter or
                FastTransferProductionPhase.MessageRecipients)
        {
            state = ReplaceTop(
                state,
                frame with { Phase = FastTransferProductionPhase.MessageRecipients });
            return Push(
                state,
                new FastTransferProductionFrame(
                    FastTransferProductionKind.Recipient,
                    FastTransferProductionPhase.RecipientProperties,
                    depthAfter,
                    EndToRecip));
        }

        if (tag == NewAttach
            && depthBefore == frame.SyntaxDepth
            && depthAfter == frame.SyntaxDepth + 1
            && frame.Phase is
                FastTransferProductionPhase.MessageStart or
                FastTransferProductionPhase.MessageProperties or
                FastTransferProductionPhase.MessageAfterRecipientDelimiter or
                FastTransferProductionPhase.MessageRecipients or
                FastTransferProductionPhase.MessageAfterAttachmentDelimiter or
                FastTransferProductionPhase.MessageAttachments)
        {
            state = ReplaceTop(
                state,
                frame with { Phase = FastTransferProductionPhase.MessageAttachments });
            return Push(
                state,
                new FastTransferProductionFrame(
                    FastTransferProductionKind.Attachment,
                    FastTransferProductionPhase.AttachmentAwaitNumber,
                    depthAfter,
                    EndAttach));
        }

        return Invalid(state, offset, Marker(tag));
    }

    private static FastTransferGrammarState AdvanceRecipientMarker(
        FastTransferGrammarState state,
        FastTransferProductionFrame frame,
        uint tag,
        long offset,
        int depthBefore,
        int depthAfter) =>
        tag == EndToRecip
        && depthBefore == frame.SyntaxDepth
        && depthAfter == frame.SyntaxDepth - 1
            ? Pop(state, FastTransferProductionKind.Recipient)
            : Invalid(state, offset, Marker(tag));

    private static FastTransferGrammarState AdvanceAttachmentMarker(
        FastTransferGrammarState state,
        FastTransferProductionFrame frame,
        uint tag,
        long offset,
        int depthBefore,
        int depthAfter)
    {
        if (frame.EndTag != 0
            && tag == frame.EndTag
            && depthBefore == frame.SyntaxDepth
            && depthAfter == frame.SyntaxDepth - 1
            && frame.Phase is
                FastTransferProductionPhase.AttachmentStart or
                FastTransferProductionPhase.AttachmentProperties or
                FastTransferProductionPhase.AttachmentAfterEmbedded)
        {
            return Pop(state, FastTransferProductionKind.Attachment);
        }

        if (tag == StartEmbed
            && depthBefore == frame.SyntaxDepth
            && depthAfter == frame.SyntaxDepth + 1
            && frame.Phase is
                FastTransferProductionPhase.AttachmentStart or
                FastTransferProductionPhase.AttachmentProperties)
        {
            state = ReplaceTop(
                state,
                frame with { Phase = FastTransferProductionPhase.AttachmentAfterEmbedded });
            return Push(
                state,
                new FastTransferProductionFrame(
                    FastTransferProductionKind.Message,
                    FastTransferProductionPhase.MessageStart,
                    depthAfter,
                    EndEmbed));
        }

        return Invalid(state, offset, Marker(tag));
    }

    private static FastTransferGrammarState AdvanceRecoveryError(
        FastTransferGrammarState state,
        long offset,
        int retainedSyntaxDepth)
    {
        if (state.Root == FastTransferRootKind.MessageList && retainedSyntaxDepth == 0)
        {
            if (Frames(state).IsEmpty
                && state.Phase is not (
                    FastTransferGrammarPhase.MessageListReady or
                    FastTransferGrammarPhase.MessageListAfterPrefix or
                    FastTransferGrammarPhase.MessageListAfterWarning))
            {
                return Invalid(state, offset, Marker(FxErrorInfo));
            }

            return state with
            {
                Phase = FastTransferGrammarPhase.MessageListErrorInfo,
                ProductionStack = ImmutableArray<FastTransferProductionFrame>.Empty,
            };
        }

        var frames = Frames(state);
        var folderIndex = -1;
        for (var index = frames.Length - 1; index >= 0; index--)
        {
            if (frames[index].Kind == FastTransferProductionKind.Folder
                && frames[index].SyntaxDepth == retainedSyntaxDepth)
            {
                folderIndex = index;
                break;
            }
        }

        if (folderIndex < 0)
        {
            return Invalid(state, offset, Marker(FxErrorInfo));
        }

        var folder = frames[folderIndex];
        var insideMessage = frames
            .Skip(folderIndex + 1)
            .Any(candidate => candidate.Kind == FastTransferProductionKind.Message);
        var atMessageList = folder.Phase is
            FastTransferProductionPhase.FolderProperties or
            FastTransferProductionPhase.FolderWarningAmbiguous or
            FastTransferProductionPhase.FolderMessageReady or
            FastTransferProductionPhase.FolderMessageAfterPrefix or
            FastTransferProductionPhase.FolderMessageAfterWarning;
        if (!insideMessage && !atMessageList)
        {
            return Invalid(state, offset, Marker(FxErrorInfo));
        }

        frames = frames.RemoveRange(folderIndex + 1, frames.Length - folderIndex - 1);
        frames = frames.SetItem(
            folderIndex,
            frames[folderIndex] with { Phase = FastTransferProductionPhase.FolderErrorInfo });
        return WithFrames(state, frames);
    }

    private static ImmutableArray<FastTransferProductionFrame> Frames(
        FastTransferGrammarState state) =>
        state.ProductionStack.IsDefault
            ? ImmutableArray<FastTransferProductionFrame>.Empty
            : state.ProductionStack;

    private static FastTransferGrammarState Push(
        FastTransferGrammarState state,
        FastTransferProductionFrame frame) =>
        WithFrames(state, Frames(state).Add(frame));

    private static FastTransferGrammarState ReplaceTop(
        FastTransferGrammarState state,
        FastTransferProductionFrame frame)
    {
        var frames = Frames(state);
        return WithFrames(state, frames.SetItem(frames.Length - 1, frame));
    }

    private static FastTransferGrammarState Pop(
        FastTransferGrammarState state,
        FastTransferProductionKind expected)
    {
        var frames = Frames(state);
        if (frames.IsEmpty || frames[^1].Kind != expected)
        {
            throw new InvalidOperationException("FastTransfer production stack is inconsistent.");
        }

        frames = frames.RemoveAt(frames.Length - 1);
        if (frames.IsEmpty)
        {
            return state.Root switch
            {
                FastTransferRootKind.MessageList => state with
                {
                    Phase = FastTransferGrammarPhase.MessageListReady,
                    ProductionStack = frames,
                },
                FastTransferRootKind.TopFolder when expected == FastTransferProductionKind.Folder =>
                    state with
                    {
                        Phase = FastTransferGrammarPhase.Complete,
                        ProductionStack = frames,
                    },
                _ => throw new InvalidOperationException("FastTransfer root production ended unexpectedly."),
            };
        }

        return WithFrames(state, frames);
    }

    private static FastTransferGrammarState WithFrames(
        FastTransferGrammarState state,
        ImmutableArray<FastTransferProductionFrame> frames) =>
        state with
        {
            ProductionStack = frames,
            Phase = frames.IsEmpty ? state.Phase : DisplayPhase(frames[^1]),
        };

    private static FastTransferGrammarPhase DisplayPhase(FastTransferProductionFrame frame) =>
        frame.Phase switch
        {
            FastTransferProductionPhase.FolderProperties => FastTransferGrammarPhase.TopFolderProperties,
            FastTransferProductionPhase.FolderWarningAmbiguous => FastTransferGrammarPhase.TopFolderWarningAmbiguous,
            FastTransferProductionPhase.FolderMessageReady => FastTransferGrammarPhase.TopFolderMessageReady,
            FastTransferProductionPhase.FolderMessageAfterPrefix => FastTransferGrammarPhase.TopFolderMessageAfterPrefix,
            FastTransferProductionPhase.FolderMessageAfterWarning => FastTransferGrammarPhase.TopFolderMessageAfterWarning,
            FastTransferProductionPhase.FolderSubFolders => FastTransferGrammarPhase.TopFolderSubFolders,
            FastTransferProductionPhase.FolderErrorInfo => FastTransferGrammarPhase.TopFolderErrorInfo,
            FastTransferProductionPhase.MessageStart or
            FastTransferProductionPhase.MessageProperties or
            FastTransferProductionPhase.MessageAfterRecipientDelimiter =>
                FastTransferGrammarPhase.ObjectMessageProperties,
            FastTransferProductionPhase.MessageRecipients or
            FastTransferProductionPhase.MessageAfterAttachmentDelimiter =>
                FastTransferGrammarPhase.ObjectMessageRecipients,
            FastTransferProductionPhase.MessageAttachments =>
                FastTransferGrammarPhase.ObjectMessageAttachments,
            FastTransferProductionPhase.RecipientProperties =>
                FastTransferGrammarPhase.ObjectRecipientProperties,
            FastTransferProductionPhase.AttachmentAwaitNumber =>
                FastTransferGrammarPhase.ObjectAttachmentAwaitNumber,
            FastTransferProductionPhase.AttachmentStart or
            FastTransferProductionPhase.AttachmentProperties =>
                FastTransferGrammarPhase.ObjectAttachmentProperties,
            FastTransferProductionPhase.AttachmentAfterEmbedded =>
                FastTransferGrammarPhase.ObjectAttachmentAfterEmbedded,
            _ => throw new InvalidOperationException("Unknown FastTransfer production phase."),
        };

    private static bool CanCompleteObjectContent(FastTransferGrammarState state)
    {
        var frames = Frames(state);
        if (frames.Length != 1 || frames[0].SyntaxDepth != 0 || frames[0].EndTag != 0)
        {
            return false;
        }

        var frame = frames[0];
        return state.Root switch
        {
            FastTransferRootKind.FolderContent when
                frame.Kind == FastTransferProductionKind.Folder
                && frame.Phase is
                    FastTransferProductionPhase.FolderProperties or
                    FastTransferProductionPhase.FolderWarningAmbiguous or
                    FastTransferProductionPhase.FolderMessageReady or
                    FastTransferProductionPhase.FolderMessageAfterPrefix or
                    FastTransferProductionPhase.FolderMessageAfterWarning or
                    FastTransferProductionPhase.FolderSubFolders => true,
            FastTransferRootKind.MessageContent when
                frame.Kind == FastTransferProductionKind.Message
                && frame.Phase is
                    FastTransferProductionPhase.MessageStart or
                    FastTransferProductionPhase.MessageProperties or
                    FastTransferProductionPhase.MessageAfterRecipientDelimiter or
                    FastTransferProductionPhase.MessageRecipients or
                    FastTransferProductionPhase.MessageAfterAttachmentDelimiter or
                    FastTransferProductionPhase.MessageAttachments => true,
            FastTransferRootKind.AttachmentContent when
                frame.Kind == FastTransferProductionKind.Attachment
                && frame.Phase is
                    FastTransferProductionPhase.AttachmentStart or
                    FastTransferProductionPhase.AttachmentProperties or
                    FastTransferProductionPhase.AttachmentAfterEmbedded => true,
            _ => false,
        };
    }

    private static bool IsMessageChildMarker(uint tag) => tag is
        StartRecip or EndToRecip or NewAttach or EndAttach or StartEmbed or EndEmbed;

    private static FastTransferGrammarState Invalid(
        FastTransferGrammarState state,
        long offset,
        string actual)
    {
        throw new MapiParseException(
            offset,
            $"{state.Root} grammar phase {state.Phase} does not permit {actual}");
    }

    private static string Marker(uint tag) =>
        FastTransferStreamLexer.MarkerNames.TryGetValue(tag, out var name)
            ? name
            : $"marker 0x{tag:X8}";

    private static string MetaProperty(uint tag) =>
        FastTransferStreamLexer.MetaPropertyNames.TryGetValue(tag, out var name)
            ? name
            : $"meta-property 0x{tag:X8}";
}
