using System;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AnsweringModelLabelFormatterTests
{
    [Fact]
    public void Null_or_empty_route_formats_clean_auto_default()
    {
        AnsweringModelDisplayInfo infoNull = AnsweringModelLabelFormatter.Format(null);
        Assert.False(infoNull.HasRoute);
        Assert.Equal("Auto", infoNull.DisplayName);
        Assert.Equal("Auto", infoNull.RoleOrMode);
        Assert.Equal("Model: Auto", infoNull.BadgeText);
        Assert.Contains("No message has been answered yet", infoNull.OneLineReason, StringComparison.Ordinal);
        Assert.Contains("Note: Router estimates reflect selection heuristics", infoNull.TooltipText, StringComparison.Ordinal);
        Assert.DoesNotContain("guarantee", infoNull.AccessibleName, StringComparison.OrdinalIgnoreCase);

        var emptyRoute = new ChatModelRouteInfo { SelectedModel = "" };
        AnsweringModelDisplayInfo infoEmpty = AnsweringModelLabelFormatter.Format(emptyRoute);
        Assert.False(infoEmpty.HasRoute);
        Assert.Equal("Auto", infoEmpty.DisplayName);
        Assert.Equal("Model: Auto", infoEmpty.BadgeText);
    }

    [Fact]
    public void Auto_profile_responsive_formats_role_and_latency_reason()
    {
        var route = new ChatModelRouteInfo
        {
            SelectedModel = "qwen2.5-coder-7b.gguf",
            Mode = "auto",
            AutoProfile = "responsive"
        };

        AnsweringModelDisplayInfo info = AnsweringModelLabelFormatter.Format(route);
        Assert.True(info.HasRoute);
        Assert.Equal("qwen2.5-coder-7b.gguf", info.ModelName);
        Assert.Equal("qwen2.5-coder-7b", info.DisplayName);
        Assert.Equal("Auto · Responsive", info.RoleOrMode);
        Assert.Equal("Model: qwen2.5-coder-7b (Auto · Responsive)", info.BadgeText);
        Assert.Contains("lower latency", info.OneLineReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Auto_profile_max_intelligence_formats_role_and_capability_reason()
    {
        var route = new ChatModelRouteInfo
        {
            SelectedModel = "deepseek-coder-v2-16b.gguf",
            Mode = "auto",
            AutoProfile = "max_intelligence"
        };

        AnsweringModelDisplayInfo info = AnsweringModelLabelFormatter.Format(route);
        Assert.True(info.HasRoute);
        Assert.Equal("deepseek-coder-v2-16b", info.DisplayName);
        Assert.Equal("Auto · Max intelligence", info.RoleOrMode);
        Assert.Equal("Model: deepseek-coder-v2-16b (Auto · Max intelligence)", info.BadgeText);
        Assert.Contains("reasoning capability", info.OneLineReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Auto_profile_custom_formats_role_and_custom_reason()
    {
        var route = new ChatModelRouteInfo
        {
            SelectedModel = "mistral-7b.gguf",
            Mode = "auto",
            AutoProfile = "custom"
        };

        AnsweringModelDisplayInfo info = AnsweringModelLabelFormatter.Format(route);
        Assert.True(info.HasRoute);
        Assert.Equal("Auto · Custom", info.RoleOrMode);
        Assert.Contains("custom auto-tuning", info.OneLineReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Auto_profile_balanced_formats_role_and_balanced_reason()
    {
        var route = new ChatModelRouteInfo
        {
            SelectedModel = "llama-3.2-3b.gguf",
            Mode = "auto",
            AutoProfile = "balanced"
        };

        AnsweringModelDisplayInfo info = AnsweringModelLabelFormatter.Format(route);
        Assert.True(info.HasRoute);
        Assert.Equal("Auto · Balanced", info.RoleOrMode);
        Assert.Contains("balanced", info.OneLineReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Manual_lock_formats_manual_role_and_user_preference_reason()
    {
        var route = new ChatModelRouteInfo
        {
            SelectedModel = "locked-model.gguf",
            Mode = "manual",
            ManualLock = true
        };

        AnsweringModelDisplayInfo info = AnsweringModelLabelFormatter.Format(route);
        Assert.True(info.HasRoute);
        Assert.Equal("Manual", info.RoleOrMode);
        Assert.Equal("Model: locked-model (Manual)", info.BadgeText);
        Assert.Contains("Manual lock", info.OneLineReason, StringComparison.Ordinal);
        Assert.Contains("user preference", info.OneLineReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Fallback_to_standard_is_explicit_in_reason()
    {
        var route = new ChatModelRouteInfo
        {
            SelectedModel = "standard-fallback.gguf",
            Mode = "auto",
            FellBackToStandard = true
        };

        AnsweringModelDisplayInfo info = AnsweringModelLabelFormatter.Format(route);
        Assert.True(info.HasRoute);
        Assert.Contains("Standard role used because the requested role was unavailable", info.OneLineReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_reason_from_core_is_sanitized_single_line_without_raw_newlines()
    {
        var route = new ChatModelRouteInfo
        {
            SelectedModel = "phi-3-mini.gguf",
            Mode = "auto",
            Reason = "Selected by router because:\r\n  - Complexity was high\n  - Fast token budget satisfied."
        };

        AnsweringModelDisplayInfo info = AnsweringModelLabelFormatter.Format(route);
        Assert.True(info.HasRoute);
        Assert.DoesNotContain("\r", info.OneLineReason, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", info.OneLineReason, StringComparison.Ordinal);
        Assert.Contains("Complexity was high", info.OneLineReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Path_sanitization_removes_directories_and_control_characters()
    {
        var route = new ChatModelRouteInfo
        {
            SelectedModel = "C:\\sensitive\\local\\models\\my-model.gguf",
            Mode = "auto"
        };

        AnsweringModelDisplayInfo info = AnsweringModelLabelFormatter.Format(route);
        Assert.Equal("my-model.gguf", info.ModelName);
        Assert.Equal("my-model", info.DisplayName);
        Assert.DoesNotContain("sensitive", info.BadgeText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\", info.BadgeText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Gguf_and_bin_extensions_are_stripped_from_display_name_preserving_model_name()
    {
        Assert.Equal("model-a", AnsweringModelLabelFormatter.FormatModelDisplayName("model-a.gguf"));
        Assert.Equal("model-b", AnsweringModelLabelFormatter.FormatModelDisplayName("model-b.bin"));
        Assert.Equal("model-c", AnsweringModelLabelFormatter.FormatModelDisplayName("model-c"));
        Assert.Equal("Configured model", AnsweringModelLabelFormatter.FormatModelDisplayName(null));
        Assert.Equal("Configured model", AnsweringModelLabelFormatter.FormatModelDisplayName("   "));
    }

    [Fact]
    public void Tooltip_includes_router_heuristic_disclaimer_and_never_claims_quality()
    {
        var route = new ChatModelRouteInfo
        {
            SelectedModel = "llama-3-8b.gguf",
            Mode = "auto",
            AutoProfile = "balanced",
            SelectedIntelligence = 0.85,
            TokensPerSecond = 28.3
        };

        AnsweringModelDisplayInfo info = AnsweringModelLabelFormatter.Format(route);
        Assert.Contains("Note: Router estimates reflect selection heuristics and do not guarantee answer quality.", info.TooltipText, StringComparison.Ordinal);
        Assert.Contains("Estimated capability fit: 85%", info.TooltipText, StringComparison.Ordinal);
        Assert.Contains("Benchmark speed: 28.3 tok/s", info.TooltipText, StringComparison.Ordinal);
        // Verified absence of false quality claims:
        Assert.DoesNotContain("best quality", info.TooltipText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("guaranteed accuracy", info.TooltipText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Accessible_name_provides_screen_reader_context()
    {
        var route = new ChatModelRouteInfo
        {
            SelectedModel = "qwen-coder.gguf",
            Mode = "auto",
            AutoProfile = "responsive"
        };

        AnsweringModelDisplayInfo info = AnsweringModelLabelFormatter.Format(route);
        Assert.StartsWith("Answering model qwen-coder, role Auto · Responsive.", info.AccessibleName, StringComparison.Ordinal);
        Assert.Contains("Route reason:", info.AccessibleName, StringComparison.Ordinal);
    }

    [Fact]
    public void Long_reason_is_truncated_cleanly_with_ellipsis()
    {
        string longReason = new string('A', 200);
        var route = new ChatModelRouteInfo
        {
            SelectedModel = "model.gguf",
            Reason = longReason
        };

        AnsweringModelDisplayInfo info = AnsweringModelLabelFormatter.Format(route);
        Assert.True(info.OneLineReason.Length <= 140);
        Assert.EndsWith("…", info.OneLineReason, StringComparison.Ordinal);
    }
}
