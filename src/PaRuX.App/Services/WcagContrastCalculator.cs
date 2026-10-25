using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PaRuX.Services
{
    /// <summary>
    /// Immutable representation of an sRGB color with 8-bit channels for WCAG 2.1 compliance calculations.
    /// </summary>
    public readonly struct WcagColor : IEquatable<WcagColor>
    {
        public byte R { get; }
        public byte G { get; }
        public byte B { get; }
        public byte A { get; }

        public WcagColor(byte r, byte g, byte b, byte a = 255)
        {
            R = r;
            G = g;
            B = b;
            A = a;
        }

        public static readonly WcagColor White = new(255, 255, 255, 255);
        public static readonly WcagColor Black = new(0, 0, 0, 255);

        public static WcagColor Parse(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex))
            {
                throw new ArgumentException("Hex color cannot be null or empty.", nameof(hex));
            }

            string clean = hex.Trim().TrimStart('#');
            if (clean.Length == 3)
            {
                // #RGB -> #RRGGBB
                byte r = byte.Parse(new string(clean[0], 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                byte g = byte.Parse(new string(clean[1], 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                byte b = byte.Parse(new string(clean[2], 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return new WcagColor(r, g, b, 255);
            }
            if (clean.Length == 6)
            {
                // #RRGGBB
                byte r = byte.Parse(clean.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                byte g = byte.Parse(clean.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                byte b = byte.Parse(clean.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return new WcagColor(r, g, b, 255);
            }
            if (clean.Length == 8)
            {
                // #AARRGGBB
                byte a = byte.Parse(clean.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                byte r = byte.Parse(clean.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                byte g = byte.Parse(clean.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                byte b = byte.Parse(clean.Substring(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return new WcagColor(r, g, b, a);
            }

            throw new FormatException($"Invalid hex color format: '{hex}'. Expected #RGB, #RRGGBB, or #AARRGGBB.");
        }

        public static bool TryParse(string? hex, out WcagColor color)
        {
            if (string.IsNullOrWhiteSpace(hex))
            {
                color = default;
                return false;
            }

            try
            {
                color = Parse(hex);
                return true;
            }
            catch
            {
                color = default;
                return false;
            }
        }

        /// <summary>
        /// Alpha-composites this foreground color over the given background color.
        /// </summary>
        public WcagColor CompositeOver(WcagColor background)
        {
            if (A == 255) return this;
            if (A == 0) return background;

            double alpha = A / 255.0;
            byte r = (byte)Math.Clamp(Math.Round(alpha * R + (1.0 - alpha) * background.R), 0, 255);
            byte g = (byte)Math.Clamp(Math.Round(alpha * G + (1.0 - alpha) * background.G), 0, 255);
            byte b = (byte)Math.Clamp(Math.Round(alpha * B + (1.0 - alpha) * background.B), 0, 255);

            return new WcagColor(r, g, b, 255);
        }

        public bool Equals(WcagColor other) => R == other.R && G == other.G && B == other.B && A == other.A;
        public override bool Equals(object? obj) => obj is WcagColor other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(R, G, B, A);
        public override string ToString() => $"#{A:X2}{R:X2}{G:X2}{B:X2}";
    }

    /// <summary>
    /// Thresholds specified by WCAG 2.1 Guidelines (Success Criterion 1.4.3 Contrast Minimum,
    /// 1.4.6 Contrast Enhanced, 1.4.11 Non-text Contrast).
    /// </summary>
    public static class WcagThresholds
    {
        public const double NormalTextAA = 4.5;
        public const double LargeTextAA = 3.0;
        public const double UiComponentOrFocusAA = 3.0;
        public const double NormalTextAAA = 7.0;
        public const double LargeTextAAA = 4.5;
    }

    /// <summary>
    /// Pure calculator implementing the W3C WCAG 2.1 relative luminance and contrast ratio algorithms.
    /// </summary>
    public static class WcagContrastCalculator
    {
        /// <summary>
        /// Computes the relative luminance of a color per WCAG 2.1 specifications:
        /// L = 0.2126 * R + 0.7152 * G + 0.0722 * B where components are linearized sRGB.
        /// </summary>
        public static double CalculateLuminance(WcagColor color)
        {
            double r = LinearizeChannel(color.R / 255.0);
            double g = LinearizeChannel(color.G / 255.0);
            double b = LinearizeChannel(color.B / 255.0);

            return 0.2126 * r + 0.7152 * g + 0.0722 * b;
        }

        private static double LinearizeChannel(double channel)
        {
            if (channel <= 0.04045)
            {
                return channel / 12.92;
            }
            return Math.Pow((channel + 0.055) / 1.055, 2.4);
        }

        /// <summary>
        /// Calculates the WCAG contrast ratio between two colors: (L1 + 0.05) / (L2 + 0.05).
        /// Returns a value in the range [1.0, 21.0].
        /// Handles semi-transparent foreground colors by alpha-blending onto the background first.
        /// </summary>
        public static double CalculateContrastRatio(WcagColor foreground, WcagColor background)
        {
            WcagColor effectiveFg = foreground.CompositeOver(background);
            double l1 = CalculateLuminance(effectiveFg);
            double l2 = CalculateLuminance(background);

            double brighter = Math.Max(l1, l2);
            double darker = Math.Min(l1, l2);

            return (brighter + 0.05) / (darker + 0.05);
        }

        public static bool PassesNormalTextAA(WcagColor foreground, WcagColor background)
            => CalculateContrastRatio(foreground, background) >= WcagThresholds.NormalTextAA;

        public static bool PassesLargeTextAA(WcagColor foreground, WcagColor background)
            => CalculateContrastRatio(foreground, background) >= WcagThresholds.LargeTextAA;

        public static bool PassesUiComponentOrFocusAA(WcagColor foreground, WcagColor background)
            => CalculateContrastRatio(foreground, background) >= WcagThresholds.UiComponentOrFocusAA;

        public static bool PassesNormalTextAAA(WcagColor foreground, WcagColor background)
            => CalculateContrastRatio(foreground, background) >= WcagThresholds.NormalTextAAA;
    }

    /// <summary>
    /// Result of evaluating a specific foreground-background pair in a theme.
    /// </summary>
    public sealed class ThemeContrastItemResult
    {
        public string ThemeName { get; init; } = string.Empty;
        public string ElementRole { get; init; } = string.Empty;
        public string ForegroundKey { get; init; } = string.Empty;
        public string ForegroundHex { get; init; } = string.Empty;
        public string BackgroundKey { get; init; } = string.Empty;
        public string BackgroundHex { get; init; } = string.Empty;
        public double ContrastRatio { get; init; }
        public double RequiredThreshold { get; init; }
        public bool Passes { get; init; }

        public override string ToString() =>
            $"[{ThemeName}] {ElementRole} ({ForegroundKey} on {BackgroundKey}): {ContrastRatio:F2}:1 (Required: {RequiredThreshold:F1}:1) -> {(Passes ? "PASS" : "FAIL")}";
    }

    /// <summary>
    /// Summary of automated contrast audit over theme palettes.
    /// </summary>
    public sealed class ThemeContrastAuditReport
    {
        public List<ThemeContrastItemResult> Items { get; } = new();
        public int TotalChecks => Items.Count;
        public int PassedChecks => Items.FindAll(i => i.Passes).Count;
        public int FailedChecks => Items.FindAll(i => !i.Passes).Count;
        public bool IsAllPassing => TotalChecks > 0 && FailedChecks == 0;
        public string Status => TotalChecks == 0 ? "not evaluated" : (IsAllPassing ? "pass" : "fail");
        public string Summary => TotalChecks == 0 ? "not evaluated" : $"Checks: {TotalChecks}, Passed: {PassedChecks}, Failed: {FailedChecks}";
        public override string ToString() => TotalChecks == 0 ? "not evaluated" : Summary;
    }

    /// <summary>
    /// Validates theme colors against WCAG standards for Light and Dark themes.
    /// </summary>
    public static class ThemeContrastValidator
    {
        /// <summary>
        /// Audits a set of token dictionaries extracted from Theme.xaml.
        /// </summary>
        public static ThemeContrastAuditReport AuditPalettes(
            IReadOnlyDictionary<string, string> lightColors,
            IReadOnlyDictionary<string, string> darkColors)
        {
            var report = new ThemeContrastAuditReport();

            AuditSingleTheme("Light", lightColors, report);
            AuditSingleTheme("Dark", darkColors, report);

            return report;
        }

        private static void AuditSingleTheme(
            string themeName,
            IReadOnlyDictionary<string, string> colors,
            ThemeContrastAuditReport report)
        {
            // Standard evaluation pairs for a PaRuX theme:
            // 1. Primary Text on Surface Solid
            CheckPair(themeName, "Primary Text on Surface", "PaRuXTextPrimaryBrush", "PaRuXSurfaceSolidBrush", WcagThresholds.NormalTextAA, colors, report);
            // 2. Primary Text on Card
            CheckPair(themeName, "Primary Text on Card", "PaRuXTextPrimaryBrush", "PaRuXSurfaceCardBrush", WcagThresholds.NormalTextAA, colors, report);
            // 3. Secondary Text on Surface
            CheckPair(themeName, "Secondary Text on Surface", "PaRuXTextSecondaryBrush", "PaRuXSurfaceSolidBrush", WcagThresholds.LargeTextAA, colors, report);
            // 4. Focus Visual Primary on Surface
            CheckPair(themeName, "Focus Visual Indicator on Surface", "PaRuXFocusBrush", "PaRuXSurfaceSolidBrush", WcagThresholds.UiComponentOrFocusAA, colors, report);
            // 5. Focus Visual Primary on Card
            CheckPair(themeName, "Focus Visual Indicator on Card", "PaRuXFocusBrush", "PaRuXSurfaceCardBrush", WcagThresholds.UiComponentOrFocusAA, colors, report);
            // 6. User Bubble Text on User Bubble Background
            CheckPair(themeName, "User Chat Bubble Text", "PaRuXUserBubbleTextBrush", "PaRuXUserBubbleBrush", WcagThresholds.NormalTextAA, colors, report);
            // 7. Accent Button Text on Accent Button Background
            CheckPair(themeName, "Accent Button Foreground", "AccentButtonForeground", "AccentButtonBackground", WcagThresholds.NormalTextAA, colors, report);
        }

        private static void CheckPair(
            string themeName,
            string elementRole,
            string fgKey,
            string bgKey,
            double threshold,
            IReadOnlyDictionary<string, string> colors,
            ThemeContrastAuditReport report)
        {
            if (!colors.TryGetValue(fgKey, out string? fgHex) || !colors.TryGetValue(bgKey, out string? bgHex))
            {
                return;
            }

            if (!WcagColor.TryParse(fgHex, out WcagColor fg) || !WcagColor.TryParse(bgHex, out WcagColor bg))
            {
                return;
            }

            double ratio = WcagContrastCalculator.CalculateContrastRatio(fg, bg);
            bool passes = ratio >= threshold;

            report.Items.Add(new ThemeContrastItemResult
            {
                ThemeName = themeName,
                ElementRole = elementRole,
                ForegroundKey = fgKey,
                ForegroundHex = fgHex,
                BackgroundKey = bgKey,
                BackgroundHex = bgHex,
                ContrastRatio = ratio,
                RequiredThreshold = threshold,
                Passes = passes
            });
        }

        /// <summary>
        /// Parses Theme.xaml XDocument directly to extract color definitions per theme.
        /// </summary>
        public static Dictionary<string, string> ExtractColorsFromThemeXml(XDocument themeDoc, string themeKey)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            XElement? themeDict = null;
            foreach (XElement dict in themeDoc.Descendants())
            {
                if (dict.Name.LocalName == "ResourceDictionary" &&
                    (string?)dict.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")) == themeKey)
                {
                    themeDict = dict;
                    break;
                }
            }

            if (themeDict == null) return result;

            foreach (XElement element in themeDict.Elements())
            {
                string? key = (string?)element.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml"));
                if (string.IsNullOrWhiteSpace(key)) continue;

                if (element.Name.LocalName == "Color")
                {
                    string? value = element.Value?.Trim();
                    if (!string.IsNullOrEmpty(value) && value.StartsWith("#", StringComparison.Ordinal))
                    {
                        result[key] = value;
                    }
                }
                else if (element.Name.LocalName == "SolidColorBrush")
                {
                    string? colorAttr = (string?)element.Attribute("Color");
                    if (!string.IsNullOrEmpty(colorAttr) && colorAttr.StartsWith("#", StringComparison.Ordinal))
                    {
                        result[key] = colorAttr;
                    }
                }
            }

            return result;
        }
    }
}
