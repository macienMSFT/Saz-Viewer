using Microsoft.Win32;

namespace SazViewer.App.Tests;

/// <summary>In-memory registry: keys are case-insensitive paths; each key maps value names ("" = default) to strings.</summary>
internal sealed class FakeRegistryStore : IRegistryStore
{
    public Dictionary<string, Dictionary<string, string>> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool KeyExists(string subKey) => Keys.ContainsKey(subKey);

    public string? GetValue(string subKey, string? name) =>
        Keys.TryGetValue(subKey, out var values) && values.TryGetValue(name ?? "", out var value) ? value : null;

    public bool HasValue(string subKey, string name) => Keys.TryGetValue(subKey, out var values) && values.ContainsKey(name);

    public void SetValue(string subKey, string? name, string value)
    {
        var parts = subKey.Split('\\');
        for (var i = 1; i <= parts.Length; i++)
        {
            var path = string.Join('\\', parts[..i]);
            if (!Keys.ContainsKey(path))
            {
                Keys[path] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }
        Keys[subKey][name ?? ""] = value;
    }

    public void DeleteValue(string subKey, string name)
    {
        if (Keys.TryGetValue(subKey, out var values))
        {
            values.Remove(name);
        }
    }

    public bool IsEmpty(string subKey) =>
        Keys.TryGetValue(subKey, out var values) && values.Count == 0 && !Keys.Keys.Any(k => IsChild(k, subKey));

    public void DeleteKey(string subKey)
    {
        if (Keys.Keys.Any(k => IsChild(k, subKey)))
        {
            throw new InvalidOperationException("Key has sub-keys.");
        }
        Keys.Remove(subKey);
    }

    public void DeleteTree(string subKey)
    {
        foreach (var key in Keys.Keys.Where(k => k.Equals(subKey, StringComparison.OrdinalIgnoreCase) || IsChild(k, subKey)).ToList())
        {
            Keys.Remove(key);
        }
    }

    private static bool IsChild(string key, string parent) => key.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase);
}

public sealed class FileAssociationServiceTests
{
    private const string Exe = @"C:\Program Files\SAZ Viewer\SazViewer.App.exe";
    private const string Command = "\"" + Exe + "\" \"%1\"";

    private readonly FakeRegistryStore registry = new();
    private int notifications;

    private FileAssociationService CreateService() => new(registry, () => notifications++);

    [Fact]
    public void RegisterWritesPerUserLayoutWithoutTakingOverDefault()
    {
        var service = CreateService();
        service.Register(Exe);

        Assert.Equal("Fiddler Session Archive", registry.GetValue("SazViewer.Capture", null));
        Assert.Equal("Fiddler Session Archive", registry.GetValue("SazViewer.Capture", "FriendlyTypeName"));
        Assert.Equal("\"" + Exe + "\",0", registry.GetValue(@"SazViewer.Capture\DefaultIcon", null));
        Assert.Equal(Command, registry.GetValue(@"SazViewer.Capture\shell\open\command", null));
        Assert.Equal("", registry.GetValue(@".saz\OpenWithProgids", "SazViewer.Capture"));
        Assert.Equal("SAZ Viewer", registry.GetValue(@"Applications\SazViewer.App.exe", "FriendlyAppName"));
        Assert.Equal(Command, registry.GetValue(@"Applications\SazViewer.App.exe\shell\open\command", null));
        Assert.Equal("", registry.GetValue(@"Applications\SazViewer.App.exe\SupportedTypes", ".saz"));

        // The default handler and the user's choice are never written.
        Assert.False(registry.HasValue(".saz", ""));
        Assert.False(registry.KeyExists(@".saz\UserChoice"));
        Assert.Equal(1, notifications);
        Assert.Equal(new FileAssociationState(FileAssociationStatus.Registered, Command), service.GetState(Exe));
    }

    [Fact]
    public void UnregisterRemovesEverythingItCreated()
    {
        var service = CreateService();
        service.Register(Exe);
        service.Unregister();

        AssertClean();
        Assert.Equal(2, notifications);
        Assert.Equal(FileAssociationStatus.NotRegistered, service.GetState(Exe).Status);
    }

    [Fact]
    public void RegisterIsIdempotentAndUnregisterStillCleansUp()
    {
        var service = CreateService();
        service.Register(Exe);
        var snapshot = Snapshot();
        service.Register(Exe);
        Assert.Equal(snapshot, Snapshot());

        service.Unregister();
        AssertClean();
    }

    [Fact]
    public void ExistingExtensionKeyAndOtherHandlersArePreserved()
    {
        registry.SetValue(".saz", null, "Fiddler.ArchiveZip");
        registry.SetValue(".saz", "Content Type", "application/x-fiddler");
        registry.SetValue(@".saz\OpenWithProgids", "Fiddler.ArchiveZip", "");
        registry.SetValue(@"Fiddler.ArchiveZip\shell\open\command", null, "fiddler.exe \"%1\"");
        var before = Snapshot();

        var service = CreateService();
        service.Register(Exe);
        Assert.Equal("Fiddler.ArchiveZip", registry.GetValue(".saz", null));
        Assert.Equal("", registry.GetValue(@".saz\OpenWithProgids", "Fiddler.ArchiveZip"));

        service.Unregister();
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void PreexistingEmptyExtensionKeyIsNotDeleted()
    {
        registry.SetValue(@".saz\OpenWithProgids", "Other", "");
        registry.DeleteValue(@".saz\OpenWithProgids", "Other");

        var service = CreateService();
        service.Register(Exe);
        service.Unregister();

        Assert.True(registry.KeyExists(".saz"));
        Assert.True(registry.KeyExists(@".saz\OpenWithProgids"));
    }

    [Fact]
    public void ForeignKeysWithOurNamesAreNotTouchedByUnregister()
    {
        registry.SetValue(@"SazViewer.Capture\shell\open\command", null, "someone-else.exe \"%1\"");
        registry.SetValue(@"Applications\SazViewer.App.exe\shell\open\command", null, "someone-else.exe \"%1\"");
        var before = Snapshot();

        var service = CreateService();
        Assert.Equal(FileAssociationStatus.NotRegistered, service.GetState(Exe).Status);
        service.Unregister();
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void MovedExecutableIsReportedAsStaleAndReRegisterFixesIt()
    {
        var service = CreateService();
        service.Register(@"D:\old\SazViewer.App.exe");

        var state = service.GetState(Exe);
        Assert.Equal(FileAssociationStatus.Stale, state.Status);
        Assert.Equal("\"D:\\old\\SazViewer.App.exe\" \"%1\"", state.RegisteredCommand);

        service.Register(Exe);
        Assert.Equal(FileAssociationStatus.Registered, service.GetState(Exe).Status);
        service.Unregister();
        AssertClean();
    }

    [Fact]
    public void PartiallyRemovedRegistrationIsStale()
    {
        var service = CreateService();
        service.Register(Exe);
        registry.DeleteValue(@".saz\OpenWithProgids", "SazViewer.Capture");
        Assert.Equal(FileAssociationStatus.Stale, service.GetState(Exe).Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("SazViewer.App.exe")]
    [InlineData(@"C:\app\SazViewer.App.dll")]
    [InlineData("C:\\app\\\"evil\".exe")]
    public void InvalidExecutablePathsAreRejected(string path)
    {
        Assert.Throws<ArgumentException>(() => CreateService().Register(path));
        AssertClean();
        Assert.Equal(0, notifications);
    }

    [Fact]
    public void RealRegistryStoreRoundTripsUnderIsolatedTestKey()
    {
        var root = @"Software\SazViewer.Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            var service = new FileAssociationService(new RegistryStore(Registry.CurrentUser, root), () => { });
            service.Register(Exe);
            using (var key = Registry.CurrentUser.OpenSubKey(root + @"\SazViewer.Capture\shell\open\command"))
            {
                Assert.Equal(Command, key?.GetValue(""));
            }
            Assert.Equal(FileAssociationStatus.Registered, service.GetState(Exe).Status);

            service.Unregister();
            using var rootKey = Registry.CurrentUser.OpenSubKey(root);
            Assert.NotNull(rootKey);
            Assert.All(rootKey.GetSubKeyNames(), name => Assert.Equal("Applications", name));
            using var applications = rootKey.OpenSubKey("Applications");
            Assert.True(applications is null || applications.SubKeyCount == 0);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
            using var parent = Registry.CurrentUser.OpenSubKey(@"Software\SazViewer.Tests", writable: true);
            if (parent is not null && parent.SubKeyCount == 0 && parent.ValueCount == 0)
            {
                Registry.CurrentUser.DeleteSubKey(@"Software\SazViewer.Tests", throwOnMissingSubKey: false);
            }
        }
    }

    /// <summary>Nothing of ours is left; the shared, value-less <c>Applications</c> parent may remain.</summary>
    private void AssertClean() =>
        Assert.All(registry.Keys, key =>
        {
            Assert.Equal("Applications", key.Key);
            Assert.Empty(key.Value);
        });

    private string Snapshot() => string.Join("\n", registry.Keys
        .Where(k => !(k.Key == "Applications" && k.Value.Count == 0))
        .OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
        .Select(k => k.Key + "=" + string.Join(";", k.Value.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase).Select(v => v.Key + ":" + v.Value))));
}
