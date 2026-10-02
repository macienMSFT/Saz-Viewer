using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SazViewer.App.Themes;
using SazViewer.App.ViewModels;
using SazViewer.App.Views;
using SazViewer.Core;

namespace SazViewer.App.Tests;

/// <summary>Milestone 6: layout preferences, split view, theme resolution, scrub banner and pop-out inspectors.</summary>
public sealed class ShellFeatureTests
{
    [Fact]
    public void PreferencesDefaultToSplitWhenWideAndSingleWhenNarrow()
    {
        var preferences = new UiPreferences(null);

        Assert.Equal(InspectorLayout.Split, preferences.GetLayout(LayoutWidthClass.Wide));
        Assert.Equal(InspectorLayout.Single, preferences.GetLayout(LayoutWidthClass.Narrow));
        Assert.Null(preferences.Theme);
        Assert.Equal(InspectorLayoutMode.Automatic, preferences.DefaultInspectorLayout);
        Assert.Equal(SessionViewerLocation.BottomPane, preferences.SessionViewer);
        Assert.Equal(.40, preferences.BottomPaneGridFraction);
        Assert.Equal(.45, preferences.RightPaneGridFraction);
        Assert.Equal(.50, preferences.RightPaneHttpSplitFraction);
        Assert.False(preferences.HideConnectOnOpen);
        Assert.False(preferences.ScrubBannerExpanded);
        Assert.Equal(LayoutWidthClass.Narrow, UiPreferences.WidthClassFor(899.5));
        Assert.Equal(LayoutWidthClass.Wide, UiPreferences.WidthClassFor(900));
    }

    [Fact]
    public void PreferencesPersistPerWidthClassThemeAndBanner()
    {
        using var temp = new TempDirectory();
        var path = temp.File("prefs\\preferences.json");
        var first = new UiPreferences(path);
        first.SetLayout(LayoutWidthClass.Narrow, InspectorLayout.Split);
        first.Theme = "dark";
        first.ScrubBannerExpanded = true;
        first.DefaultInspectorLayout = InspectorLayoutMode.AlwaysSingle;
        first.SessionViewer = SessionViewerLocation.RightPane;
        first.BottomPaneGridFraction = .35;
        first.RightPaneGridFraction = .55;
        first.RightPaneHttpSplitFraction = .60;
        first.HideConnectOnOpen = true;

        var reloaded = new UiPreferences(path);

        Assert.Equal(InspectorLayout.Split, reloaded.GetLayout(LayoutWidthClass.Narrow));
        Assert.Equal(InspectorLayout.Split, reloaded.GetLayout(LayoutWidthClass.Wide));
        Assert.Equal("dark", reloaded.Theme);
        Assert.True(reloaded.ScrubBannerExpanded);
        Assert.Equal(InspectorLayoutMode.AlwaysSingle, reloaded.DefaultInspectorLayout);
        Assert.Equal(SessionViewerLocation.RightPane, reloaded.SessionViewer);
        Assert.Equal(.35, reloaded.BottomPaneGridFraction);
        Assert.Equal(.55, reloaded.RightPaneGridFraction);
        Assert.Equal(.60, reloaded.RightPaneHttpSplitFraction);
        Assert.True(reloaded.HideConnectOnOpen);
        Assert.False(File.Exists(path + ".tmp"));

        reloaded.SessionViewer = SessionViewerLocation.NewWindow;
        Assert.Equal(SessionViewerLocation.NewWindow, new UiPreferences(path).SessionViewer);
        reloaded.Theme = "system";
        Assert.Null(new UiPreferences(path).Theme);
    }

    [Fact]
    public void ExistingPreferencesMigrateWithoutLosingFields()
    {
        using var temp = new TempDirectory();
        var path = temp.File("preferences.json");
        File.WriteAllText(path,
            """
            {
              "Version": 1,
              "Theme": "dark",
              "HttpLayoutWide": "single",
              "HttpLayoutNarrow": "split",
              "ScrubBanner": "expanded"
            }
            """);

        var preferences = new UiPreferences(path);
        preferences.SessionViewer = SessionViewerLocation.NewWindow;

        var migrated = new UiPreferences(path);
        Assert.Equal("dark", migrated.Theme);
        Assert.Equal(InspectorLayout.Single, migrated.GetLayout(LayoutWidthClass.Wide));
        Assert.Equal(InspectorLayout.Split, migrated.GetLayout(LayoutWidthClass.Narrow));
        Assert.True(migrated.ScrubBannerExpanded);
        Assert.Equal(InspectorLayoutMode.Automatic, migrated.DefaultInspectorLayout);
        Assert.Equal(SessionViewerLocation.NewWindow, migrated.SessionViewer);
        Assert.Contains("\"Version\": 2", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void CorruptPreferencesFallBackToDefaults()
    {
        using var temp = new TempDirectory();
        var path = temp.File("preferences.json");
        File.WriteAllText(path, "{ not json");

        var preferences = new UiPreferences(path);

        Assert.Equal(InspectorLayout.Split, preferences.GetLayout(LayoutWidthClass.Wide));
        File.WriteAllText(path, "{\"Theme\":\"purple\",\"HttpLayoutWide\":\"diagonal\"}");
        preferences = new UiPreferences(path);
        Assert.Null(preferences.Theme);
        Assert.Equal(InspectorLayout.Split, preferences.GetLayout(LayoutWidthClass.Wide));
    }

    [Theory]
    [InlineData(null, false, "Dark", "Dark")]
    [InlineData(null, false, "Light", "Light")]
    [InlineData("light", false, "Dark", "Light")]
    [InlineData("dark", false, "Light", "Dark")]
    [InlineData("dark", true, "Light", "HighContrast")]
    [InlineData(null, true, "Dark", "HighContrast")]
    public void ThemeResolvesHighContrastThenChoiceThenSystem(string? preference, bool highContrast, string system, string expected) =>
        Assert.Equal(Enum.Parse<AppTheme>(expected), ThemeManager.Resolve(preference, highContrast, () => Enum.Parse<AppTheme>(system)));

    [Fact]
    public void HighContrastPaletteDefinesEveryPaletteKey()
    {
        StaRunner.Run(() =>
        {
            var light = new ResourceDictionary { Source = ThemeManager.PaletteUri(AppTheme.Light) };
            var dark = new ResourceDictionary { Source = ThemeManager.PaletteUri(AppTheme.Dark) };
            var contrast = ThemeManager.HighContrastPalette();
            var lightKeys = light.Keys.Cast<object>().Select(key => key.ToString()).Order().ToArray();

            Assert.Equal(lightKeys, dark.Keys.Cast<object>().Select(key => key.ToString()).Order().ToArray());
            Assert.Equal(lightKeys, contrast.Keys.Cast<object>().Select(key => key.ToString()).Order().ToArray());
        });
    }

    [Fact]
    public void LayoutToggleSwitchesAndPersistsPerWidthClass()
    {
        var preferences = new UiPreferences(null);
        var model = MediaCaptures.Open(out _, preferences);
        var inspector = model.Inspector;
        inspector.Load(model.Sessions.VisibleRows[0]);

        Assert.True(inspector.IsSplit);
        Assert.False(inspector.IsSingle);
        Assert.Equal("Single view", inspector.LayoutToggleLabel);

        inspector.ToggleLayoutCommand.Execute(null);
        Assert.True(inspector.IsSingle);
        Assert.Equal("Split view", inspector.LayoutToggleLabel);
        Assert.Equal(InspectorLayout.Single, preferences.GetLayout(LayoutWidthClass.Wide));

        // The narrow class keeps its own (default single) layout; toggling there doesn't touch the wide one.
        inspector.WidthClass = LayoutWidthClass.Narrow;
        Assert.True(inspector.IsNarrow);
        Assert.True(inspector.IsSingle);
        inspector.ToggleLayoutCommand.Execute(null);
        Assert.Equal(InspectorLayout.Split, preferences.GetLayout(LayoutWidthClass.Narrow));
        Assert.Equal(InspectorLayout.Single, preferences.GetLayout(LayoutWidthClass.Wide));
        inspector.WidthClass = LayoutWidthClass.Wide;
        Assert.True(inspector.IsSingle);
    }

    [Fact]
    public void ForcedLayoutAppliesImmediatelyAndToggleIsTemporary()
    {
        var preferences = new UiPreferences(null)
        {
            DefaultInspectorLayout = InspectorLayoutMode.AlwaysSplit
        };
        var model = MediaCaptures.Open(out _, preferences);
        var inspector = model.Inspector;
        inspector.WidthClass = LayoutWidthClass.Narrow;
        inspector.Load(model.Sessions.VisibleRows[0]);

        Assert.True(inspector.IsSplit);
        inspector.ToggleLayoutCommand.Execute(null);
        Assert.True(inspector.IsSingle);
        Assert.Equal(InspectorLayoutMode.AlwaysSplit, preferences.DefaultInspectorLayout);
        Assert.Equal(InspectorLayout.Single, preferences.GetLayout(LayoutWidthClass.Narrow));

        inspector.WidthClass = LayoutWidthClass.Wide;
        Assert.True(inspector.IsSingle);
        preferences.DefaultInspectorLayout = InspectorLayoutMode.AlwaysSingle;
        model.ApplyPreferences();
        Assert.True(inspector.IsSingle);
        preferences.DefaultInspectorLayout = InspectorLayoutMode.AlwaysSplit;
        model.ApplyPreferences();
        Assert.True(inspector.IsSplit);
    }

    [Fact]
    public void HideConnectPreferenceOnlySetsNewCaptureInitialState()
    {
        var preferences = new UiPreferences(null) { HideConnectOnOpen = true };
        var first = new CaptureViewModel(NativeCaptures.Mixed(), new FakeClipboard(), preferences);
        Assert.True(first.Sessions.HideConnect);

        first.Sessions.HideConnect = false;
        preferences.HideConnectOnOpen = true;
        first.ApplyPreferences();
        Assert.False(first.Sessions.HideConnect);

        var second = new CaptureViewModel(NativeCaptures.Mixed(), new FakeClipboard(), preferences);
        Assert.True(second.Sessions.HideConnect);
    }

    [Fact]
    public void SingleViewRestoresPreviousSideAndResetsTheHiddenPane()
    {
        var preferences = new UiPreferences(null);
        preferences.SetLayout(LayoutWidthClass.Wide, InspectorLayout.Single);
        var model = MediaCaptures.Open(out _, preferences);
        var inspector = model.Inspector;
        inspector.Load(model.Sessions.VisibleRows[3]);
        inspector.SelectedSide = InspectorViewModel.ResponseSide;

        inspector.ToggleLayoutCommand.Execute(null);
        Assert.True(inspector.IsSplit);
        // In split view both sides are visible: revealing in Request survives selecting the Response side.
        Assert.True(inspector.Request!.Select("auth"));
        var auth = (AuthViewModel)inspector.Request.Tab("auth").Content!;
        auth.IsRevealed = true;
        inspector.SelectedSide = InspectorViewModel.RequestSide;
        inspector.SelectedSide = InspectorViewModel.ResponseSide;
        Assert.True(auth.IsRevealed);

        inspector.ToggleLayoutCommand.Execute(null);
        Assert.True(inspector.IsSingle);
        Assert.Equal(InspectorViewModel.ResponseSide, inspector.SelectedSide);
        Assert.False(auth.IsRevealed);
    }

    [Fact]
    public void SplitPanesSearchIndependently()
    {
        var model = new CaptureViewModel(NativeCaptures.Mixed(), new FakeClipboard(), new UiPreferences(null));
        var inspector = model.Inspector;
        inspector.Load(model.Sessions.VisibleRows[0]);
        Assert.True(inspector.IsSplit);
        inspector.Request!.Select("raw");
        inspector.Response!.Select("raw");

        inspector.RequestSearch.Query = "HTTP";
        inspector.RequestSearch.Run();
        inspector.ResponseSearch.Query = "zzz-no-match";
        inspector.ResponseSearch.Run();

        Assert.True(inspector.RequestSearch.MatchCount > 0);
        Assert.Equal(0, inspector.ResponseSearch.MatchCount);

        // Changing one pane's view resets only that pane's search.
        inspector.Response.Select("headers");
        Assert.True(inspector.RequestSearch.MatchCount > 0);
        Assert.Equal("", inspector.ResponseSearch.Query);
    }

    [Fact]
    public void ScrubBannerSummarizesCountsAndRemembersExpansion()
    {
        var preferences = new UiPreferences(null);
        var banner = new ScrubBannerViewModel(
            new AuthScrubSummary(new Dictionary<string, int> { ["Authorization"] = 1, ["Cookie"] = 1200 }), preferences);

        Assert.Equal("Credentials scrubbed: 1,201 replacements", banner.Summary);
        Assert.Equal(["Authorization: 1", "Cookie: 1,200"], banner.Counts);
        Assert.False(banner.IsExpanded);
        Assert.Equal("Show credential scrub details", banner.ToggleLabel);

        banner.ToggleCommand.Execute(null);

        Assert.True(banner.IsExpanded);
        Assert.Equal("Hide credential scrub details", banner.ToggleLabel);
        Assert.True(preferences.ScrubBannerExpanded);
        Assert.True(new ScrubBannerViewModel(new AuthScrubSummary(new Dictionary<string, int> { ["Cookie"] = 1 }), preferences).IsExpanded);
        Assert.Equal("Credentials scrubbed: 1 replacement",
            new ScrubBannerViewModel(new AuthScrubSummary(new Dictionary<string, int> { ["Cookie"] = 1 }), preferences).Summary);
    }

    [Fact]
    public void ScrubbedCaptureShowsRedactedValuesAndBanner()
    {
        using var temp = new TempDirectory();
        var capture = TestCaptures.WritePlain(temp.File("c.saz"));
        var plain = new CaptureViewModel(ReportBuilder.Build(capture, false, new QueuePasswordProvider()).Report, new FakeClipboard(), new UiPreferences(null));
        var scrubbed = new CaptureViewModel(ReportBuilder.Build(capture, true, new QueuePasswordProvider()).Report, new FakeClipboard(), new UiPreferences(null));

        Assert.Null(plain.ScrubBanner);
        Assert.NotNull(scrubbed.ScrubBanner);
        Assert.StartsWith("Credentials scrubbed: ", scrubbed.ScrubBanner!.Summary, StringComparison.Ordinal);

        scrubbed.Inspector.Load(scrubbed.Sessions.VisibleRows[0]);
        Assert.True(scrubbed.Inspector.Request!.Select("headers"));
        var headers = scrubbed.Inspector.Request.Tab("headers").Content!;
        var text = string.Join("\n", ((TextDocumentViewModel)headers).Document.AllRows.Select(line => line.Text));
        Assert.Contains("[REDACTED:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("topsecretcookie", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PopOutInspectorNavigatesWithoutMovingGridSelection()
    {
        var model = new CaptureViewModel(NativeCaptures.Mixed(), new FakeClipboard(), new UiPreferences(null));
        var first = model.Sessions.VisibleRows[0];
        SessionRowRequested? requested = null;
        model.Inspector.PopOutRequested += (_, row) => requested = new SessionRowRequested(row);
        model.Inspector.Load(first);
        Assert.True(model.Inspector.CanPopOut);
        Assert.True(model.Inspector.PopOutCommand.CanExecute(null));

        model.Inspector.PopOutCommand.Execute(null);
        Assert.Same(first, requested?.Row);

        var popOut = model.CreatePopOutInspector(first);
        Assert.False(popOut.CanPopOut);
        Assert.False(popOut.PopOutCommand.CanExecute(null));
        popOut.Navigate(1);

        Assert.Same(model.Sessions.VisibleRows[1], popOut.Row);
        Assert.Same(first, model.Sessions.SelectedRow);
        Assert.Equal("2 of " + model.Sessions.VisibleRows.Count, popOut.Position);
    }

    [Fact]
    public void InspectorViewShowsSplitPanesAndStacksThemWhenNarrow()
    {
        using var temp = new TempDirectory();
        var document = ReportBuilder.Build(TestCaptures.WritePlain(temp.File("c.saz")), false, new QueuePasswordProvider());
        StaRunner.Run(() =>
        {
            var model = new CaptureViewModel(document.Report, new FakeClipboard(), new UiPreferences(null));
            var view = new CaptureView { DataContext = model };
            var window = NativeViewSmokeTests.Host(view);
            try
            {
                model.Inspector.Load(model.Sessions.VisibleRows[1]);
                StaRunner.DoEvents();
                var inspector = view.Inspector!;
                var split = (Grid)inspector.FindName("SplitBody");
                var single = (FrameworkElement)inspector.FindName("HttpBody");
                var splitter = (GridSplitter)inspector.FindName("PaneSplitter");
                var response = (FrameworkElement)inspector.FindName("ResponseSplitPane");
                Assert.True(split.IsVisible);
                Assert.False(single.IsVisible);
                Assert.True(splitter.IsVisible);
                Assert.Equal(2, Grid.GetColumn(response));

                window.Width = 700;
                StaRunner.DoEvents();
                Assert.True(model.Inspector.IsNarrow);
                // Narrow defaults to single view; split there stacks the panes without a splitter.
                Assert.True(single.IsVisible);
                model.Inspector.ToggleLayoutCommand.Execute(null);
                StaRunner.DoEvents();
                Assert.True(split.IsVisible);
                Assert.False(splitter.IsVisible);
                Assert.Equal(0, Grid.GetColumn(response));
                Assert.Equal(2, Grid.GetRow(response));
            }
            finally
            {
                window.Close();
                StaRunner.DoEvents();
            }
        });
    }

    [Fact]
    public void RightPaneSwitchesOuterSplitterAndUsesRememberedStackedHttpSplit()
    {
        using var temp = new TempDirectory();
        var document = ReportBuilder.Build(TestCaptures.WritePlain(temp.File("right-pane.saz")), false, new QueuePasswordProvider());
        var preferences = new UiPreferences(null) { SessionViewer = SessionViewerLocation.RightPane };
        StaRunner.Run(() =>
        {
            var model = new CaptureViewModel(document.Report, new FakeClipboard(), preferences);
            var view = new CaptureView { DataContext = model };
            var window = NativeViewSmokeTests.Host(view);
            try
            {
                model.Inspector.Load(model.Sessions.VisibleRows[0]);
                StaRunner.DoEvents();

                var grid = (DataGrid)view.FindName("SessionGrid");
                var host = (Border)view.FindName("InspectorHost");
                var outerSplitter = (GridSplitter)view.FindName("InspectorSplitter");
                var gridColumn = (ColumnDefinition)view.FindName("GridColumnDefinition");
                var inspectorColumn = (ColumnDefinition)view.FindName("InspectorColumnDefinition");
                var inspector = Assert.IsType<InspectorView>(view.Inspector);
                var innerSplitter = (GridSplitter)inspector.FindName("PaneSplitter");
                var response = (FrameworkElement)inspector.FindName("ResponseSplitPane");
                var requestRow = (RowDefinition)inspector.FindName("SplitFirstRow");
                var responseRow = (RowDefinition)inspector.FindName("SplitSecondRow");

                Assert.True(host.IsVisible);
                Assert.Equal(2, Grid.GetColumn(host));
                Assert.Equal(1, Grid.GetColumnSpan(grid));
                Assert.Equal(GridResizeDirection.Columns, outerSplitter.ResizeDirection);
                Assert.Equal("Resize session grid and right inspector", AutomationProperties.GetName(outerSplitter));
                Assert.True(inspector.IsRightPane);
                Assert.Equal(GridResizeDirection.Rows, innerSplitter.ResizeDirection);
                Assert.True(innerSplitter.IsVisible);
                Assert.Equal(2, Grid.GetRow(response));
                Assert.Equal(0, Grid.GetColumn(response));
                Assert.Equal(3, Grid.GetColumnSpan(response));
                Assert.InRange(requestRow.ActualHeight / (requestRow.ActualHeight + responseRow.ActualHeight), .48, .52);

                model.Inspector.ToggleLayoutCommand.Execute(null);
                StaRunner.DoEvents();
                Assert.True(model.Inspector.IsSingle);
                model.Inspector.ToggleLayoutCommand.Execute(null);
                StaRunner.DoEvents();
                Assert.True(model.Inspector.IsSplit);
                Assert.Equal(2, Grid.GetRow(response));

                gridColumn.Width = new GridLength(2, GridUnitType.Star);
                inspectorColumn.Width = new GridLength(1, GridUnitType.Star);
                view.UpdateLayout();
                outerSplitter.RaiseEvent(new DragCompletedEventArgs(0, 0, false)
                {
                    RoutedEvent = Thumb.DragCompletedEvent
                });
                Assert.InRange(preferences.RightPaneGridFraction, .60, .70);

                requestRow.Height = new GridLength(3, GridUnitType.Star);
                responseRow.Height = new GridLength(2, GridUnitType.Star);
                inspector.UpdateLayout();
                innerSplitter.RaiseEvent(new DragCompletedEventArgs(0, 0, false)
                {
                    RoutedEvent = Thumb.DragCompletedEvent
                });
                Assert.InRange(preferences.RightPaneHttpSplitFraction, .55, .65);

                preferences.SessionViewer = SessionViewerLocation.BottomPane;
                model.ApplyPreferences();
                StaRunner.DoEvents();
                Assert.Equal(GridResizeDirection.Rows, outerSplitter.ResizeDirection);
                Assert.Equal(3, Grid.GetRow(host));
                Assert.False(inspector.IsRightPane);
                Assert.Equal(GridResizeDirection.Columns, innerSplitter.ResizeDirection);
                Assert.Equal(2, Grid.GetColumn(response));
                var gridRow = (RowDefinition)view.FindName("GridRowDefinition");
                var inspectorRow = (RowDefinition)view.FindName("InspectorRowDefinition");
                gridRow.Height = new GridLength(1, GridUnitType.Star);
                inspectorRow.Height = new GridLength(2, GridUnitType.Star);
                view.UpdateLayout();
                outerSplitter.RaiseEvent(new DragCompletedEventArgs(0, 0, false)
                {
                    RoutedEvent = Thumb.DragCompletedEvent
                });
                Assert.InRange(preferences.BottomPaneGridFraction, .30, .37);

                preferences.SessionViewer = SessionViewerLocation.RightPane;
                model.ApplyPreferences();
                StaRunner.DoEvents();
                Assert.InRange(requestRow.ActualHeight / (requestRow.ActualHeight + responseRow.ActualHeight), .55, .65);
            }
            finally
            {
                window.Close();
                StaRunner.DoEvents();
            }
        });
    }

    private sealed record SessionRowRequested(Model.SessionRow Row);
}
