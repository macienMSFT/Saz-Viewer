using Microsoft.Win32;

namespace SazViewer.App;

/// <summary>
/// Minimal string-valued registry surface rooted at a fixed key (in production
/// <c>HKCU\Software\Classes</c>). Sub-key paths are relative and backslash-separated; a null or empty
/// value name addresses the key's default value.
/// </summary>
internal interface IRegistryStore
{
    bool KeyExists(string subKey);

    string? GetValue(string subKey, string? name);

    bool HasValue(string subKey, string name);

    void SetValue(string subKey, string? name, string value);

    void DeleteValue(string subKey, string name);

    /// <summary>True when the key has no values (including a default value) and no sub-keys.</summary>
    bool IsEmpty(string subKey);

    void DeleteKey(string subKey);

    void DeleteTree(string subKey);
}

/// <summary><see cref="IRegistryStore"/> backed by the real registry under a root key.</summary>
internal sealed class RegistryStore(RegistryKey hive, string rootPath) : IRegistryStore
{
    public static RegistryStore CurrentUserClasses() => new(Registry.CurrentUser, @"Software\Classes");

    public bool KeyExists(string subKey)
    {
        using var key = Open(subKey, writable: false);
        return key is not null;
    }

    public string? GetValue(string subKey, string? name)
    {
        using var key = Open(subKey, writable: false);
        return key?.GetValue(name ?? "") as string;
    }

    public bool HasValue(string subKey, string name)
    {
        using var key = Open(subKey, writable: false);
        return key is not null && key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    public void SetValue(string subKey, string? name, string value)
    {
        using var key = hive.CreateSubKey(Combine(subKey), writable: true);
        key.SetValue(name ?? "", value, RegistryValueKind.String);
    }

    public void DeleteValue(string subKey, string name)
    {
        using var key = Open(subKey, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    public bool IsEmpty(string subKey)
    {
        using var key = Open(subKey, writable: false);
        return key is not null && key.ValueCount == 0 && key.SubKeyCount == 0;
    }

    public void DeleteKey(string subKey) => hive.DeleteSubKey(Combine(subKey), throwOnMissingSubKey: false);

    public void DeleteTree(string subKey) => hive.DeleteSubKeyTree(Combine(subKey), throwOnMissingSubKey: false);

    private RegistryKey? Open(string subKey, bool writable) => hive.OpenSubKey(Combine(subKey), writable);

    private string Combine(string subKey) => string.IsNullOrEmpty(subKey) ? rootPath : rootPath + "\\" + subKey;
}
