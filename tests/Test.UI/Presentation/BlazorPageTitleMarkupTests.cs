namespace Test.UI.Presentation;

/// <summary>
/// Guards the page-title contract: Routes.razor's FocusOnNavigate targets "h1", so every routed
/// page must render exactly one h1 (MudText HtmlTag="h1") carrying the stable page-title testid.
/// MudBlazor Typo sets only the CSS class - without HtmlTag the title emits an h4 and focus,
/// heading order, and smoke anchors all break silently.
/// </summary>
[TestClass]
[TestCategory("UI")]
[TestCategory("Presentation")]
public sealed class BlazorPageTitleMarkupTests
{
    [TestMethod]
    public void EveryRoutedPage_RendersExactlyOnePageTitleH1()
    {
        var pagesDir = Path.Combine(AppContext.BaseDirectory, "Presentation", "BlazorPages");
        var routedPages = Directory.GetFiles(pagesDir, "*.razor")
            .Where(f => File.ReadAllText(f).Contains("@page ", StringComparison.Ordinal))
            .ToList();

        Assert.IsTrue(routedPages.Count > 0, "No routed Blazor pages found - check the csproj Content link.");

        foreach (var page in routedPages)
        {
            var markup = File.ReadAllText(page);
            var count = CountOccurrences(markup, "HtmlTag=\"h1\"");
            Assert.AreEqual(1, count, $"{Path.GetFileName(page)} must render exactly one HtmlTag=\"h1\" page title, found {count}.");
            StringAssert.Contains(markup, "data-testid=\"page-title\"", $"{Path.GetFileName(page)} is missing the page-title testid.");
        }
    }

    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        for (var i = text.IndexOf(token, StringComparison.Ordinal); i >= 0; i = text.IndexOf(token, i + token.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
