using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ThemeResourceRegressionTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData("Light", "AccentButtonForeground", "AccentButtonBackground")]
    [InlineData("Light", "AccentButtonForegroundPointerOver", "AccentButtonBackgroundPointerOver")]
    [InlineData("Light", "AccentButtonForegroundPressed", "AccentButtonBackgroundPressed")]
    [InlineData("Dark", "AccentButtonForeground", "AccentButtonBackground")]
    [InlineData("Dark", "AccentButtonForegroundPointerOver", "AccentButtonBackgroundPointerOver")]
    [InlineData("Dark", "AccentButtonForegroundPressed", "AccentButtonBackgroundPressed")]
    [InlineData("Light", "ToggleButtonForegroundChecked", "ToggleButtonBackgroundChecked")]
    [InlineData("Light", "ToggleButtonForegroundCheckedPointerOver", "ToggleButtonBackgroundCheckedPointerOver")]
    [InlineData("Light", "ToggleButtonForegroundCheckedPressed", "ToggleButtonBackgroundCheckedPressed")]
    [InlineData("Dark", "ToggleButtonForegroundChecked", "ToggleButtonBackgroundChecked")]
    [InlineData("Dark", "ToggleButtonForegroundCheckedPointerOver", "ToggleButtonBackgroundCheckedPointerOver")]
    [InlineData("Dark", "ToggleButtonForegroundCheckedPressed", "ToggleButtonBackgroundCheckedPressed")]
    public void Interactive_text_contrast_meets_normal_text_AA(string theme, string foreground, string background)
    {
        var resources = ReadTheme(theme);
        var fg = WcagColor.Parse((string?)resources[foreground].Attribute("Color") ?? throw new InvalidDataException(foreground));
        var bg = WcagColor.Parse((string?)resources[background].Attribute("Color") ?? throw new InvalidDataException(background));
        Assert.Equal(255, fg.A);
        Assert.Equal(255, bg.A);
        double ratio = WcagContrastCalculator.CalculateContrastRatio(fg, bg);
        Assert.True(ratio >= 4.5, $"{theme}: {foreground} on {background} has contrast {ratio:F2}:1; expected at least 4.5:1.");
    }

    [Fact]
    public void Light_and_dark_expose_identical_unique_keys_and_high_contrast_retains_parux_keys()
    {
        var light = ReadTheme("Light");
        var dark = ReadTheme("Dark");
        var highContrast = ReadTheme("HighContrast");
        Assert.Equal(light.Keys.OrderBy(x => x), dark.Keys.OrderBy(x => x));
        Assert.Empty(light.Keys.Where(x => x.StartsWith("PaRuX", StringComparison.Ordinal) && !highContrast.ContainsKey(x)));
    }

    private static Dictionary<string, XElement> ReadTheme(string name)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "PaRuX.sln"))) current = current.Parent;
        if (current == null) throw new DirectoryNotFoundException("PaRuX.sln was not found above the test output directory.");
        var doc = XDocument.Load(Path.Combine(current.FullName, "src", "PaRuX.App", "Styles", "Theme.xaml"));
        var dictionary = doc.Descendants().Single(x => x.Name.LocalName == "ResourceDictionary" && (string?)x.Attribute(Xaml + "Key") == name);
        // ToDictionary fails on duplicate keys, matching XAML resource semantics.
        return dictionary.Elements().Where(x => x.Attribute(Xaml + "Key") != null)
            .ToDictionary(x => (string)x.Attribute(Xaml + "Key")!, StringComparer.Ordinal);
    }
}
