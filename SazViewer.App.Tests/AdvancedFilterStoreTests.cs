using SazViewer.App.Model;

namespace SazViewer.App.Tests;

public sealed class AdvancedFilterStoreTests
{
    [Fact]
    public void SavedFiltersRoundTripOverwriteDeleteAndPreserveStrictSchema()
    {
        using var temp = new TempDirectory();
        var path = temp.File("filters.json");
        var store = new AdvancedFilterStore(path);
        store.SaveAs("Errors", Definition("url", AdvancedFilterOperator.Contains, "error"));
        store.SaveAs("errors", Definition("url", AdvancedFilterOperator.Contains, "failure"));

        var reloaded = new AdvancedFilterStore(path);
        var saved = Assert.Single(reloaded.Filters);
        Assert.Equal("errors", saved.Name);
        Assert.Equal("failure", Assert.Single(saved.Definition.Rules).Value);
        Assert.Contains("\"Version\": 1", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.True(reloaded.Delete("ERRORS"));
        Assert.Empty(new AdvancedFilterStore(path).Filters);
    }

    [Fact]
    public void ExportedFilterImportsIntoAnotherStore()
    {
        using var temp = new TempDirectory();
        var source = new AdvancedFilterStore(temp.File("source.json"));
        source.SaveAs("Large responses", Definition("response-size", AdvancedFilterOperator.GreaterThan, "1 MB"));
        var export = temp.File("large-response.sazfilter.json");
        source.Export(export, Assert.Single(source.Filters));

        var destination = new AdvancedFilterStore(temp.File("destination.json"));
        Assert.Equal(1, destination.Import(export));

        var imported = Assert.Single(destination.Filters);
        Assert.Equal("Large responses", imported.Name);
        Assert.Equal(AdvancedFilterOperator.GreaterThan, imported.Definition.Rules[0].Operator);
    }

    [Fact]
    public void ImportRejectsUnknownPropertiesInvalidEnumsAndInvalidRulesWithoutChangingStore()
    {
        using var temp = new TempDirectory();
        var store = new AdvancedFilterStore(temp.File("filters.json"));
        store.SaveAs("Existing", Definition("url", AdvancedFilterOperator.Contains, "safe"));

        var unknown = temp.File("unknown.json");
        File.WriteAllText(unknown, """{"Version":1,"Filters":[],"Execute":"never"}""");
        Assert.Throws<InvalidDataException>(() => store.Import(unknown));

        var invalidEnum = temp.File("invalid-enum.json");
        File.WriteAllText(invalidEnum,
            """
            {
              "Version": 1,
              "Filters": [{
                "Name": "Bad",
                "Definition": {
                  "Rules": [{
                    "Enabled": true,
                    "OpenParentheses": 0,
                    "Field": { "Kind": "Column", "Column": { "Id": "url", "Kind": "builtin", "Source": "url", "Header": "", "Visible": true } },
                    "Operator": "RunCode",
                    "Value": "x",
                    "CloseParentheses": 0,
                    "Join": "And",
                    "CaseSensitive": false
                  }]
                }
              }]
            }
            """);
        Assert.Throws<InvalidDataException>(() => store.Import(invalidEnum));

        Assert.Equal("Existing", Assert.Single(store.Filters).Name);
    }

    [Fact]
    public void CorruptPersistentStoreFallsBackToNoSavedFilters()
    {
        using var temp = new TempDirectory();
        var path = temp.File("filters.json");
        File.WriteAllText(path, "{broken");

        Assert.Empty(new AdvancedFilterStore(path).Filters);
    }

    private static AdvancedFilterDefinition Definition(
        string columnId,
        AdvancedFilterOperator filterOperator,
        string value)
    {
        var field = AdvancedFilterField.ForColumn(
            Assert.Single(SessionColumnCatalog.BuiltIns, column => column.Id == columnId).Setting);
        return new([new(true, 0, field, filterOperator, value, 0, AdvancedFilterJoin.And, false)]);
    }
}
