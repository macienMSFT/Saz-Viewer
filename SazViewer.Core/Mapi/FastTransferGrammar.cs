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
    AwaitStateBegin,
    StateProperties,
    AwaitSyncEnd,
    Complete,
}

internal readonly record struct FastTransferGrammarState(
    FastTransferRootKind Root,
    FastTransferGrammarPhase Phase,
    string? Provenance,
    bool AmbiguityReported = false)
{
    public static FastTransferGrammarState Unconfigured { get; } =
        new(FastTransferRootKind.Unknown, FastTransferGrammarPhase.None, null);

    public bool IsConfigured => Provenance is not null;

    public bool IsValidated => Root is
        FastTransferRootKind.ContentsSync or
        FastTransferRootKind.HierarchySync or
        FastTransferRootKind.State;

    public bool IsComplete => Phase == FastTransferGrammarPhase.Complete;

    public static FastTransferGrammarState ForRoot(FastTransferRootKind root, string provenance) =>
        new(
            root,
            root switch
            {
                FastTransferRootKind.ContentsSync => FastTransferGrammarPhase.ContentsStart,
                FastTransferRootKind.HierarchySync => FastTransferGrammarPhase.HierarchyChanges,
                FastTransferRootKind.State => FastTransferGrammarPhase.AwaitStateBegin,
                _ => FastTransferGrammarPhase.None,
            },
            provenance);
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
    private const uint NewAttach = 0x40000003;
    private const uint EndAttach = 0x400E0003;
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
    private const uint MetaTagFxDelProp = 0x40160003;
    private const uint MetaTagIncrementalSyncMessagePartial = 0x407A0003;
    private const uint MetaTagIncrSyncGroupId = 0x407C0003;

    public static FastTransferGrammarState AdvanceProperty(
        FastTransferGrammarState state,
        uint tag,
        long offset)
    {
        if (!state.IsValidated)
        {
            return state;
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
            _ => state,
        };
    }

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
}
