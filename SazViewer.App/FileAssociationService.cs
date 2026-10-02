using System.Runtime.InteropServices;

namespace SazViewer.App;

internal enum FileAssociationStatus
{
    NotRegistered,
    Registered,

    /// <summary>Registered by SAZ Viewer, but for a different executable path (for example the app moved).</summary>
    Stale
}

internal sealed record FileAssociationState(FileAssociationStatus Status, string? RegisteredCommand);

/// <summary>
/// Per-user <c>.saz</c> and <c>.har</c> registration under <c>HKCU\Software\Classes</c> without administrator rights.
/// SAZ Viewer adds itself as an <em>Open with</em> candidate (ProgID, <c>OpenWithProgids</c>, and an
/// <c>Applications</c> entry) but never writes an extension default value or <c>UserChoice</c>, so an
/// existing default handler is not taken over. Marker values record which keys SAZ Viewer created so
/// <see cref="Unregister"/> removes only those.
/// </summary>
internal sealed class FileAssociationService(IRegistryStore registry, Action notifyShell)
{
    public const string ProgId = "SazViewer.Capture";
    public const string Extension = ".saz";
    public const string HarExtension = ".har";
    public const string ExecutableName = "SazViewer.App.exe";
    public const string FriendlyTypeName = "SAZ or HTTP Archive Capture";
    public const string FriendlyAppName = "SAZ Viewer";
    public const string OwnerMarker = "SazViewer.Owner";
    public const string OwnerValue = "SazViewer.App";
    public const string CreatedExtensionKeyMarker = "SazViewer.CreatedExtensionKey";
    public const string CreatedOpenWithProgidsMarker = "SazViewer.CreatedOpenWithProgids";
    public const string CreatedHarExtensionKeyMarker = "SazViewer.CreatedHarExtensionKey";
    public const string CreatedHarOpenWithProgidsMarker = "SazViewer.CreatedHarOpenWithProgids";

    public const string ProgIdKey = ProgId;
    public const string ProgIdIconKey = ProgId + @"\DefaultIcon";
    public const string ProgIdCommandKey = ProgId + @"\shell\open\command";
    public const string OpenWithProgidsKey = Extension + @"\OpenWithProgids";
    public const string HarOpenWithProgidsKey = HarExtension + @"\OpenWithProgids";
    public const string ApplicationKey = @"Applications\" + ExecutableName;
    public const string ApplicationCommandKey = ApplicationKey + @"\shell\open\command";
    public const string ApplicationSupportedTypesKey = ApplicationKey + @"\SupportedTypes";

    public static FileAssociationService ForCurrentUser() =>
        new(RegistryStore.CurrentUserClasses(), NativeMethods.NotifyAssociationsChanged);

    public static string BuildCommand(string executablePath) => $"\"{executablePath}\" \"%1\"";

    public FileAssociationState GetState(string executablePath)
    {
        if (!IsOwned(ProgIdKey))
        {
            return new FileAssociationState(FileAssociationStatus.NotRegistered, null);
        }
        var command = registry.GetValue(ProgIdCommandKey, null);
        var current = string.Equals(command, BuildCommand(executablePath), StringComparison.OrdinalIgnoreCase)
            && string.Equals(registry.GetValue(ApplicationCommandKey, null), command, StringComparison.OrdinalIgnoreCase)
            && registry.HasValue(OpenWithProgidsKey, ProgId)
            && registry.HasValue(HarOpenWithProgidsKey, ProgId)
            && registry.HasValue(ApplicationSupportedTypesKey, Extension)
            && registry.HasValue(ApplicationSupportedTypesKey, HarExtension);
        return new FileAssociationState(current ? FileAssociationStatus.Registered : FileAssociationStatus.Stale, command);
    }

    /// <summary>Creates or refreshes the per-user registration for <paramref name="executablePath"/>.</summary>
    public void Register(string executablePath)
    {
        ValidateExecutablePath(executablePath);
        var owned = IsOwned(ProgIdKey);
        var saz = RegistrationFor(
            Extension,
            OpenWithProgidsKey,
            CreatedExtensionKeyMarker,
            CreatedOpenWithProgidsMarker,
            owned);
        var har = RegistrationFor(
            HarExtension,
            HarOpenWithProgidsKey,
            CreatedHarExtensionKeyMarker,
            CreatedHarOpenWithProgidsMarker,
            owned);
        var command = BuildCommand(executablePath);
        var icon = $"\"{executablePath}\",0";

        registry.SetValue(ProgIdKey, null, FriendlyTypeName);
        registry.SetValue(ProgIdKey, "FriendlyTypeName", FriendlyTypeName);
        registry.SetValue(ProgIdKey, OwnerMarker, OwnerValue);
        SetOrClearMarker(ProgIdKey, saz.CreatedExtensionMarker, saz.CreatedExtension);
        SetOrClearMarker(ProgIdKey, saz.CreatedOpenWithMarker, saz.CreatedOpenWith);
        SetOrClearMarker(ProgIdKey, har.CreatedExtensionMarker, har.CreatedExtension);
        SetOrClearMarker(ProgIdKey, har.CreatedOpenWithMarker, har.CreatedOpenWith);
        registry.SetValue(ProgIdIconKey, null, icon);
        registry.SetValue(ProgIdCommandKey, null, command);

        registry.SetValue(OpenWithProgidsKey, ProgId, "");
        registry.SetValue(HarOpenWithProgidsKey, ProgId, "");

        registry.SetValue(ApplicationKey, "FriendlyAppName", FriendlyAppName);
        registry.SetValue(ApplicationKey, OwnerMarker, OwnerValue);
        registry.SetValue(ApplicationKey + @"\DefaultIcon", null, icon);
        registry.SetValue(ApplicationCommandKey, null, command);
        registry.SetValue(ApplicationSupportedTypesKey, Extension, "");
        registry.SetValue(ApplicationSupportedTypesKey, HarExtension, "");

        notifyShell();
    }

    /// <summary>Removes only what <see cref="Register"/> created; foreign extension values are kept.</summary>
    public void Unregister()
    {
        if (IsOwned(ProgIdKey))
        {
            RemoveExtensionRegistration(
                Extension,
                OpenWithProgidsKey,
                CreatedExtensionKeyMarker,
                CreatedOpenWithProgidsMarker);
            RemoveExtensionRegistration(
                HarExtension,
                HarOpenWithProgidsKey,
                CreatedHarExtensionKeyMarker,
                CreatedHarOpenWithProgidsMarker);
            registry.DeleteTree(ProgIdKey);
        }
        if (IsOwned(ApplicationKey))
        {
            registry.DeleteTree(ApplicationKey);
        }
        notifyShell();
    }

    private bool IsOwned(string key) =>
        string.Equals(registry.GetValue(key, OwnerMarker), OwnerValue, StringComparison.Ordinal);

    private ExtensionRegistration RegistrationFor(
        string extension,
        string openWithKey,
        string createdExtensionMarker,
        string createdOpenWithMarker,
        bool owned) =>
        new(
            (owned && registry.HasValue(ProgIdKey, createdExtensionMarker)) || !registry.KeyExists(extension),
            (owned && registry.HasValue(ProgIdKey, createdOpenWithMarker)) || !registry.KeyExists(openWithKey),
            createdExtensionMarker,
            createdOpenWithMarker);

    private void RemoveExtensionRegistration(
        string extension,
        string openWithKey,
        string createdExtensionMarker,
        string createdOpenWithMarker)
    {
        var createdExtension = registry.HasValue(ProgIdKey, createdExtensionMarker);
        var createdOpenWith = registry.HasValue(ProgIdKey, createdOpenWithMarker);
        registry.DeleteValue(openWithKey, ProgId);
        if (createdOpenWith && registry.IsEmpty(openWithKey))
        {
            registry.DeleteKey(openWithKey);
        }
        if (createdExtension && registry.IsEmpty(extension))
        {
            registry.DeleteKey(extension);
        }
    }

    private void SetOrClearMarker(string key, string marker, bool value)
    {
        if (value)
        {
            registry.SetValue(key, marker, "1");
        }
        else
        {
            registry.DeleteValue(key, marker);
        }
    }

    private static void ValidateExecutablePath(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)
            || !Path.IsPathFullyQualified(executablePath)
            || executablePath.Contains('"')
            || !executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The executable path must be a fully qualified .exe path.", nameof(executablePath));
        }
    }

    private sealed record ExtensionRegistration(
        bool CreatedExtension,
        bool CreatedOpenWith,
        string CreatedExtensionMarker,
        string CreatedOpenWithMarker);

    private static class NativeMethods
    {
        private const int ShcneAssocChanged = 0x08000000;
        private const uint ShcnfIdList = 0x0000;

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

        public static void NotifyAssociationsChanged() => SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero);
    }
}
