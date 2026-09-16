using OpenQA.Selenium;
using OpenQA.Selenium.Appium;
using OpenQA.Selenium.Appium.Android;

namespace Test.Mobile.PageObjects;

/// <summary>
/// Thin interaction wrapper over the Appium driver for the TaskFlow Uno app. Centralizes selector
/// and timeout logic so screen objects stay declarative.
///
/// Uno (Skia on Android) projects each XAML element into the accessibility tree as an
/// android.view.View whose <c>content-desc</c> carries AutomationProperties.Name (AutomationId is
/// NOT exposed as resource-id here). Element text also lands in <c>content-desc</c>. So all lookups
/// match on content-desc, with the visible text as a fallback.
/// </summary>
internal sealed class MobileTaskAppDriver
{
    private readonly AppiumDriver _driver;
    private readonly TimeSpan _timeout;

    public MobileTaskAppDriver(AppiumDriver driver, TimeSpan timeout)
    {
        _driver = driver;
        _timeout = timeout;
    }

    public AppiumDriver Driver => _driver;

    internal static readonly string[] args = new[] { "statusbar", "collapse" };

    /// <summary>XPath literal that tolerates embedded single quotes via concat().</summary>
    private static string XPathLiteral(string value)
    {
        if (!value.Contains('\'')) return $"'{value}'";
        if (!value.Contains('"')) return $"\"{value}\"";
        return "concat('" + value.Replace("'", "',\"'\",'") + "')";
    }

    /// <summary>Matches an element by its accessibility label (content-desc) or visible text.</summary>
    private static By ByLabel(string label)
    {
        return MobileBy.AccessibilityId(label);
    }

    /// <summary>Matches an element whose label/text contains the given fragment.</summary>
    private static By ByLabelContains(string fragment)
    {
        var literal = XPathLiteral(fragment);
        return By.XPath($"//*[contains(@content-desc, {literal}) or contains(@text, {literal})]");
    }

    /// <summary>
    /// Matches an exact exposed value. Uno 6.7 Skia maps AutomationProperties.HelpText to Android's
    /// hint attribute because TextBox.Text is not projected to the UiAutomator text attribute.
    /// Remove the hint branch when Uno projects editable values through AccessibilityNodeInfo.Text.
    /// </summary>
    private static By ByExactValue(string value)
    {
        var literal = XPathLiteral(value);
        return By.XPath($"//*[@content-desc = {literal} or @text = {literal} or @hint = {literal}]");
    }

    private static bool IsFatalDriverError(WebDriverException ex)
    {
        var message = ex.Message;
        return message.Contains("instrumentation process not running", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Could not proxy command to the remote server", StringComparison.OrdinalIgnoreCase)
            || message.Contains("socket hang up", StringComparison.OrdinalIgnoreCase);
    }

    private AppiumElement? FirstOrNull(By by)
    {
        var matches = _driver.FindElements(by);
        return matches.Count > 0 ? (AppiumElement)matches[0] : null;
    }

    private AppiumElement WaitFor(Func<AppiumElement?> probe, string description)
    {
        var deadline = DateTimeOffset.UtcNow + _timeout;
        Exception? last = null;
        var iteration = 0;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var found = probe();
                if (found is not null)
                {
                    return found;
                }
            }
            catch (WebDriverException ex)
            {
                if (IsFatalDriverError(ex)) throw;
                last = ex;
            }

            // Self-heal: a stray gesture on the software-GPU emulator can drop the Android
            // notification shade over the app, hiding every control. Collapse it periodically so
            // the wait recovers instead of grinding to a timeout.
            if (++iteration % 4 == 0)
            {
                CollapseStatusBar();
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(400));
        }

        throw new TimeoutException($"Timed out waiting for {description} after {_timeout.TotalSeconds:0}s.", last);
    }

    /// <summary>Waits for and returns the element with the given accessibility label, scrolling it into view if needed.</summary>
    public AppiumElement Element(string label) =>
        WaitFor(
            () => FirstOrNull(ByLabel(label)) ?? FirstOrNull(ByLabelContains(label)) ?? ScrollIntoView(label),
            $"element '{label}'");

    public AppiumElement EnabledElement(string label) =>
        WaitFor(
            () =>
            {
                var element = FirstOrNull(ByLabel(label)) ?? FirstOrNull(ByLabelContains(label)) ?? ScrollIntoView(label);
                return element is not null && element.Enabled ? element : null;
            },
            $"enabled element '{label}'");

    /// <summary>
    /// Brings an off-screen control into view with bounded swipes inside the content area.
    /// Deliberately avoids UiScrollable/scrollIntoView and any swipe near the top edge: a downward
    /// gesture from the status-bar region pulls down the Android notification shade and covers the
    /// app. Tries both directions because Uno may preserve the form's prior scroll position between
    /// screens, so the requested field can be above or below the current viewport.
    /// </summary>
    private AppiumElement? ScrollIntoView(string label)
    {
        if (_driver is not AndroidDriver) return null;

        foreach (var direction in new[] { "down", "up" })
        {
            for (var i = 0; i < 3; i++)
            {
                if (!SafeSwipe(direction)) return null;
                var found = FirstOrNull(ByLabel(label)) ?? FirstOrNull(ByLabelContains(label));
                if (found is not null) return found;
            }
        }

        return null;
    }

    /// <summary>Swipes within a safe content rectangle, never touching the top status-bar zone.</summary>
    private bool SafeSwipe(string direction)
    {
        try
        {
            var size = _driver.Manage().Window.Size;
            _driver.ExecuteScript("mobile: swipeGesture", new Dictionary<string, object>
            {
                ["left"] = (int)(size.Width * 0.1),
                ["top"] = (int)(size.Height * 0.35),    // start well below the status bar
                ["width"] = (int)(size.Width * 0.8),
                ["height"] = (int)(size.Height * 0.45),
                ["direction"] = direction,
                ["percent"] = 0.75
            });
            return true;
        }
        catch (WebDriverException)
        {
            return false;
        }
    }

    /// <summary>Collapses the Android notification shade if a stray gesture opened it.</summary>
    public void CollapseStatusBar()
    {
        try
        {
            _driver.ExecuteScript("mobile: shell", new Dictionary<string, object>
            {
                ["command"] = "cmd",
                ["args"] = args
            });
        }
        catch (WebDriverException) { /* best-effort */ }
    }

    /// <summary>Taps the element with the given accessibility label.</summary>
    public void Tap(string label) => Element(label).Click();

    public void TapEnabled(string label) => EnabledElement(label).Click();

    /// <summary>Taps an element identified by exact label/text (alias of Tap for readability).</summary>
    public void TapText(string text) => Tap(text);

    /// <summary>Focuses the field then types. Falls back to adb input when the view rejects SendKeys.</summary>
    public void Type(string label, string value)
    {
        var element = Element(label);
        element.Click();
        try
        {
            try { element.Clear(); }
            catch (WebDriverException) { /* unfocused Uno inputs may reject Clear; ignore */ }
            element.SendKeys(value);
        }
        catch (WebDriverException)
        {
            // Uno Skia inputs surface as android.view.View, which uiautomator2 cannot SendKeys to.
            // The field is focused from the tap above, so route characters through the IME via adb.
            AdbInputText(value);
        }
    }

    /// <summary>Sends text to the focused field through the Android IME (adb input text).</summary>
    private void AdbInputText(string value)
    {
        // uiautomator2 exposes a shell escape hatch; "input text" targets the focused editor.
        var escaped = value.Replace(" ", "%s");
        _driver.ExecuteScript("mobile: shell", new Dictionary<string, object>
        {
            ["command"] = "input",
            ["args"] = new[] { "text", escaped }
        });
    }

    /// <summary>
    /// Best-effort ComboBox selection. Opens the (Android Spinner) combo and taps the option if it
    /// surfaces within the short window. Returns false instead of throwing when the native dropdown
    /// cannot be driven (Uno Skia Spinner popups are not always exposed to uiautomator2), so callers
    /// can treat priority/status as optional decoration on the core CRUD flow.
    /// </summary>
    public bool TrySelectFromCombo(string comboLabel, string optionText, TimeSpan optionTimeout)
    {
        try { Element(comboLabel).Click(); }
        catch (WebDriverException) { return false; }
        catch (TimeoutException) { return false; }

        var deadline = DateTimeOffset.UtcNow + optionTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var option = FirstOrNull(ByLabel(optionText));
            if (option is not null)
            {
                option.Click();
                return true;
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(300));
        }

        // Do NOT press the Android Back button to dismiss: Uno intercepts Back for page
        // navigation, so it would leave the form. Leave any open dropdown as-is and return;
        // the next field interaction re-focuses the form.
        return false;
    }

    /// <summary>Best-effort type: find within a short window (scrolling), type, return success. Never throws.</summary>
    public bool TryType(string label, string value, TimeSpan timeout)
    {
        var element = WaitForOptional(label, timeout);
        if (element is null) return false;
        try
        {
            element.Click();
            try { element.Clear(); } catch (WebDriverException) { }
            element.SendKeys(value);
            return true;
        }
        catch (WebDriverException)
        {
            try { AdbInputText(value); return true; }
            catch (WebDriverException) { return false; }
        }
    }

    /// <summary>Best-effort tap: find within a short window (scrolling), tap, return success. Never throws.</summary>
    public bool TryTap(string label, TimeSpan timeout)
    {
        var element = WaitForOptional(label, timeout);
        if (element is null) return false;
        try { element.Click(); return true; }
        catch (WebDriverException) { return false; }
    }

    /// <summary>Returns the element if it appears within the timeout (scrolling), else null. Never throws.</summary>
    private AppiumElement? WaitForOptional(string label, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var found = FirstOrNull(ByLabel(label)) ?? FirstOrNull(ByLabelContains(label)) ?? ScrollIntoView(label);
                if (found is not null) return found;
            }
            catch (WebDriverException ex)
            {
                if (IsFatalDriverError(ex)) throw;
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(300));
        }

        return null;
    }

    public bool HasText(string text) =>
        _driver.FindElements(ByLabel(text)).Count > 0 || _driver.FindElements(ByExactValue(text)).Count > 0;

    public void WaitForText(string text) =>
        WaitFor(() => FirstOrNull(ByLabel(text)), $"text '{text}'");

    public void WaitForTextGone(string text)
    {
        var deadline = DateTimeOffset.UtcNow + _timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_driver.FindElements(ByLabel(text)).Count == 0)
            {
                return;
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(400));
        }

        throw new TimeoutException($"Timed out waiting for text '{text}' to disappear after {_timeout.TotalSeconds:0}s.");
    }

    /// <summary>Best-effort hide of the soft keyboard so action buttons are not obscured.</summary>
    public void HideKeyboard()
    {
        try
        {
            if (_driver is AndroidDriver android && android.IsKeyboardShown())
            {
                android.HideKeyboard();
            }
        }
        catch (WebDriverException) { /* keyboard state is best-effort */ }
    }
}
