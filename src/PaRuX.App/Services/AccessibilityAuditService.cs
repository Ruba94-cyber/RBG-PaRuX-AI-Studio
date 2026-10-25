using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace PaRuX.Services
{
    public sealed class AccessibilityControlViolation
    {
        public string FilePath { get; init; } = string.Empty;
        public string ControlType { get; init; } = string.Empty;
        public string? ControlName { get; init; }
        public string? AutomationId { get; init; }
        public string Issue { get; init; } = string.Empty;
        public string Recommendation { get; init; } = string.Empty;

        public override string ToString() =>
            $"[{FilePath}] {ControlType} (Name='{ControlName}', AutoId='{AutomationId}'): {Issue} -> {Recommendation}";
    }

    public sealed class AccessibilityAuditResult
    {
        public int TotalInteractiveControls { get; init; }
        public int PassedControls { get; init; }
        public IReadOnlyList<AccessibilityControlViolation> Violations { get; init; } = Array.Empty<AccessibilityControlViolation>();
        public bool IsSuccess => TotalInteractiveControls > 0 && Violations.Count == 0;
        public string Status => TotalInteractiveControls == 0 ? "not evaluated" : (IsSuccess ? "pass" : "fail");
        public string Summary => TotalInteractiveControls == 0 ? "not evaluated" : $"Passed: {PassedControls}/{TotalInteractiveControls}, Violations: {Violations.Count}";
        public override string ToString() => TotalInteractiveControls == 0 ? "not evaluated" : Summary;
    }

    /// <summary>
    /// Pure XAML static analyzer for accessibility compliance (names, focus order, icon-only buttons).
    /// </summary>
    public static class AccessibilityAuditor
    {
        private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
        private static readonly HashSet<string> InteractiveControlNames = new(StringComparer.Ordinal)
        {
            "Button",
            "ToggleButton",
            "RepeatButton",
            "TextBox",
            "PasswordBox",
            "ComboBox",
            "ToggleSwitch",
            "Slider",
            "HyperlinkButton"
        };

        public static AccessibilityAuditResult AuditXaml(string xamlContent, string filePath = "unknown.xaml")
        {
            if (string.IsNullOrWhiteSpace(xamlContent))
            {
                return new AccessibilityAuditResult();
            }

            XDocument doc = XDocument.Parse(xamlContent);
            return AuditXamlDocument(doc, filePath);
        }

        public static AccessibilityAuditResult AuditXamlDocument(XDocument doc, string filePath = "unknown.xaml")
        {
            var violations = new List<AccessibilityControlViolation>();
            int total = 0;
            int passed = 0;

            foreach (XElement element in doc.Descendants())
            {
                string localName = element.Name.LocalName;
                if (!InteractiveControlNames.Contains(localName))
                {
                    continue;
                }

                total++;
                string? controlName = (string?)element.Attribute(XamlNamespace + "Name") ?? (string?)element.Attribute("Name");
                string? autoId = (string?)element.Attribute("AutomationProperties.AutomationId");
                string? autoName = (string?)element.Attribute("AutomationProperties.Name");
                string? isTabStop = (string?)element.Attribute("IsTabStop");
                string? tabIndexStr = (string?)element.Attribute("TabIndex");

                bool hasViolation = false;

                // 1. Focus order check: TabIndex must not be negative unless IsTabStop is explicitly "False"
                if (!string.IsNullOrWhiteSpace(tabIndexStr) && int.TryParse(tabIndexStr, out int tabIndex))
                {
                    if (tabIndex < 0 && !string.Equals(isTabStop, "False", StringComparison.OrdinalIgnoreCase))
                    {
                        violations.Add(new AccessibilityControlViolation
                        {
                            FilePath = filePath,
                            ControlType = localName,
                            ControlName = controlName,
                            AutomationId = autoId,
                            Issue = $"Negative TabIndex ({tabIndex}) without IsTabStop='False'.",
                            Recommendation = "Set IsTabStop='False' if control is not keyboard-focusable, or use non-negative TabIndex."
                        });
                        hasViolation = true;
                    }
                }

                // 2. Accessible name check for icon-only buttons
                if (localName is "Button" or "ToggleButton" or "RepeatButton")
                {
                    bool isIconOnly = IsIconOnlyButton(element);
                    if (isIconOnly && string.IsNullOrWhiteSpace(autoName))
                    {
                        violations.Add(new AccessibilityControlViolation
                        {
                            FilePath = filePath,
                            ControlType = localName,
                            ControlName = controlName,
                            AutomationId = autoId,
                            Issue = "Icon-only button has no accessible name (AutomationProperties.Name is missing).",
                            Recommendation = "Add AutomationProperties.Name attribute with a plain-language description of the action."
                        });
                        hasViolation = true;
                    }
                }

                if (!hasViolation)
                {
                    passed++;
                }
            }

            return new AccessibilityAuditResult
            {
                TotalInteractiveControls = total,
                PassedControls = passed,
                Violations = violations
            };
        }

        private static bool IsIconOnlyButton(XElement buttonElement)
        {
            string? content = (string?)buttonElement.Attribute("Content");

            // If it has visible string content that is not a glyph code
            if (!string.IsNullOrWhiteSpace(content) && !content.StartsWith("&#x", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Check children
            var children = buttonElement.Elements().ToList();
            if (children.Count == 0 && string.IsNullOrWhiteSpace(content))
            {
                return true;
            }

            // Check if only an icon control (FontIcon, SymbolIcon, PathIcon)
            if (children.Count == 1)
            {
                string childName = children[0].Name.LocalName;
                if (childName is "FontIcon" or "SymbolIcon" or "PathIcon" or "BitmapIcon" or "Image")
                {
                    return true;
                }
            }

            // Check if child has any TextBlock with text
            bool hasTextChild = buttonElement.Descendants()
                .Any(c => c.Name.LocalName == "TextBlock" &&
                          (!string.IsNullOrWhiteSpace((string?)c.Attribute("Text")) || !string.IsNullOrWhiteSpace(c.Value)));

            if (!hasTextChild && children.Count > 0 &&
                buttonElement.Descendants().Any(c => c.Name.LocalName is "FontIcon" or "SymbolIcon" or "PathIcon"))
            {
                return true;
            }

            return false;
        }
    }
}
