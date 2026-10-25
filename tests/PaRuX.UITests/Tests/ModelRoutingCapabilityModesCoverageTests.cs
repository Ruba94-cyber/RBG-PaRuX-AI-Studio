using System;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

[Collection(UiTestCollection.Name)]
public sealed class ModelRoutingCapabilityModesCoverageTests
{
    [Theory, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "FlaUI")]
    [InlineData("image.generate")]
    [InlineData("image.edit")]
    [InlineData("video.generate")]
    public void Media_capability_row_exposes_mode_model_and_trace(string capability)
    {
        string testName = nameof(Media_capability_row_exposes_mode_model_and_trace) + "_" + capability;
        using var app = new UiTestApplication(testName, modelRoutingFixture: "empty");
        try
        {
            var main = new MainWindowPage(app.MainWindow);
            main.EnableAdvanced();
            main.Click("Navigation_ModelRouting_Item", ControlType.ListItem);

            AutomationElement mode = UiWait.ById(app.MainWindow, "routing.cap." + capability + ".mode", ControlType.ComboBox);
            AutomationElement model = UiWait.ById(app.MainWindow, "routing.cap." + capability + ".model", ControlType.ComboBox);
            AutomationElement trace = UiWait.ById(app.MainWindow, "routing.cap." + capability + ".trace", ControlType.Text);

            // Automatic is the default: the model picker is disabled and the trace explains the automatic behaviour.
            Assert.False(model.IsEnabled);
            // The trace is filled by the async status refresh; an empty TextBlock exposes no Name property at all.
            string traceText = ReadNameWhenPresent(trace);
            Assert.False(string.IsNullOrWhiteSpace(traceText), "The trace line stayed empty after the status refresh.");
            Assert.NotNull(mode);
        }
        catch (Exception error)
        {
            app.CaptureFailure(testName, error);
            throw;
        }
    }

    private static string ReadNameWhenPresent(AutomationElement element)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            try
            {
                string name = element.Properties.Name.ValueOrDefault ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(name) || DateTime.UtcNow > deadline) return name;
            }
            catch (FlaUI.Core.Exceptions.PropertyNotSupportedException)
            {
                if (DateTime.UtcNow > deadline) return string.Empty;
            }
            System.Threading.Thread.Sleep(200);
        }
    }
}
