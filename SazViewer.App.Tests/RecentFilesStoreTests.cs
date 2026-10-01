namespace SazViewer.App.Tests;

public sealed class RecentFilesStoreTests
{
    [Fact]
    public void MissingStoreIsEmptyAndAddPersistsMostRecentFirst()
    {
        using var temp = new TempDirectory();
        var storePath = temp.File(@"nested\recent.json");
        var store = new RecentFilesStore(storePath);
        Assert.Empty(store.Paths);

        store.Add(@"C:\captures\a.saz");
        store.Add(@"C:\captures\b.saz");

        Assert.Equal([@"C:\captures\b.saz", @"C:\captures\a.saz"], store.Paths);
        Assert.Equal(store.Paths, new RecentFilesStore(storePath).Paths);
        Assert.False(File.Exists(storePath + ".tmp"));
    }

    [Fact]
    public void ReAddingMovesToTopCaseInsensitively()
    {
        using var temp = new TempDirectory();
        var store = new RecentFilesStore(temp.File("recent.json"));
        store.Add(@"C:\captures\a.saz");
        store.Add(@"C:\captures\b.saz");
        store.Add(@"c:\CAPTURES\A.saz");

        Assert.Equal([@"c:\CAPTURES\A.saz", @"C:\captures\b.saz"], store.Paths);
    }

    [Fact]
    public void CapacityIsEnforced()
    {
        using var temp = new TempDirectory();
        var store = new RecentFilesStore(temp.File("recent.json"));
        for (var index = 0; index < 15; index++)
        {
            store.Add($@"C:\captures\{index}.saz");
        }

        Assert.Equal(RecentFilesStore.DefaultCapacity, store.Paths.Count);
        Assert.Equal(@"C:\captures\14.saz", store.Paths[0]);
        Assert.Equal(@"C:\captures\5.saz", store.Paths[^1]);
    }

    [Fact]
    public void RemoveAndClearPersist()
    {
        using var temp = new TempDirectory();
        var storePath = temp.File("recent.json");
        var store = new RecentFilesStore(storePath);
        store.Add(@"C:\captures\a.saz");
        store.Add(@"C:\captures\b.saz");

        Assert.True(store.Remove(@"C:\CAPTURES\a.saz"));
        Assert.False(store.Remove(@"C:\captures\missing.saz"));
        Assert.False(store.Remove("relative.saz"));
        Assert.Equal([@"C:\captures\b.saz"], new RecentFilesStore(storePath).Paths);

        store.Clear();
        Assert.Empty(store.Paths);
        Assert.Empty(new RecentFilesStore(storePath).Paths);
    }

    [Fact]
    public void StoresPathsOnly()
    {
        using var temp = new TempDirectory();
        var storePath = temp.File("recent.json");
        new RecentFilesStore(storePath).Add(@"C:\captures\a.saz");

        var json = File.ReadAllText(storePath);
        Assert.Contains(@"C:\\captures\\a.saz", json);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"Files\": 42}")]
    [InlineData("")]
    public void CorruptStoreIsTreatedAsEmpty(string content)
    {
        using var temp = new TempDirectory();
        var storePath = temp.File("recent.json");
        File.WriteAllText(storePath, content);

        var store = new RecentFilesStore(storePath);
        Assert.Empty(store.Paths);
        store.Add(@"C:\captures\a.saz");
        Assert.Equal([@"C:\captures\a.saz"], new RecentFilesStore(storePath).Paths);
    }

    [Fact]
    public void InvalidAndDuplicateEntriesAreIgnoredOnLoad()
    {
        using var temp = new TempDirectory();
        var storePath = temp.File("recent.json");
        File.WriteAllText(
            storePath,
            """{"Version":1,"Files":["relative.saz","","C:\\a.saz","c:\\A.saz",null,"C:\\b.saz"]}""");

        Assert.Equal([@"C:\a.saz", @"C:\b.saz"], new RecentFilesStore(storePath).Paths);
    }

    [Fact]
    public void AddRejectsRelativePaths()
    {
        using var temp = new TempDirectory();
        Assert.Throws<ArgumentException>(() => new RecentFilesStore(temp.File("recent.json")).Add("relative.saz"));
    }

    [Fact]
    public void UnwritableStoreDoesNotThrow()
    {
        using var temp = new TempDirectory();
        var blocker = temp.File("blocker");
        File.WriteAllText(blocker, "");
        var store = new RecentFilesStore(Path.Combine(blocker, "recent.json"));

        store.Add(@"C:\captures\a.saz");
        Assert.Equal([@"C:\captures\a.saz"], store.Paths);
    }
}
