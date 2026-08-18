namespace Test.UI.Presentation;

/// <summary>Guards the Skia-compatible navigation host and FeedView exception data context.</summary>
[TestClass]
[TestCategory("UI")]
[TestCategory("Presentation")]
public sealed class UnoNavigationAndErrorMarkupTests
{
    /// <summary>Verifies nested route views use a content host rather than an empty visibility region.</summary>
    [TestMethod]
    public void MainPage_NestedRoutesUseContentControlNavigator()
    {
        var markup = ReadMarkup("MainPage.xaml");

        StringAssert.Contains(markup, "<ContentControl Grid.Row=\"1\"");
        StringAssert.Contains(markup, "x:Name=\"RootContent\"");
        Assert.IsFalse(markup.Contains("Region.Navigator=\"Visibility\"", StringComparison.Ordinal));
    }

    /// <summary>Verifies FeedView error templates bind to the exception itself.</summary>
    [TestMethod]
    [DataRow("CategoryTreePage.xaml")]
    [DataRow("TagManagementPage.xaml")]
    public void FeedView_ErrorTemplateBindsDirectlyToExceptionMessage(string fileName)
    {
        var markup = ReadMarkup(fileName);

        StringAssert.Contains(markup, "<uer:FeedView.ErrorTemplate>");
        StringAssert.Contains(markup, "Text=\"{Binding Message}\"");
        Assert.IsFalse(markup.Contains("{Binding Error", StringComparison.Ordinal));
    }

    private static string ReadMarkup(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Presentation", fileName));
}
