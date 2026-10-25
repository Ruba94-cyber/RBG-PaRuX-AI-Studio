using System;

namespace PaRuX.Services
{
    /// <summary>What the "Set up automatically" card shows for one state. Pure, so tests can pin it.</summary>
    public sealed record AutoSetupViewModel(
        string Headline,
        string Detail,
        string PrimaryLabel,
        bool PrimaryEnabled,
        bool ShowCancel,
        bool ShowRetry,
        bool ShowSkip,
        bool ShowProgress);

    public static class OnboardingAutoSetupPresenter
    {
        public static AutoSetupViewModel Present(AutoSetupState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            string message = state.Message ?? string.Empty;
            string runtime = string.IsNullOrWhiteSpace(state.RuntimeNote) ? string.Empty : " " + state.RuntimeNote;

            return state.Phase switch
            {
                AutoSetupPhase.NotStarted => new AutoSetupViewModel(
                    "Set up PaRuX in one step",
                    "PaRuX will check this PC, keep your text runtime, pick a model that fits, ask you to review its licence, "
                    + "download it, assign roles and verify a local chat answer. Nothing downloads before the licence review.",
                    "Set up automatically", true, false, false, false, false),
                AutoSetupPhase.CheckingHardware or AutoSetupPhase.CheckingRuntime or AutoSetupPhase.SelectingModel
                    or AutoSetupPhase.AssigningRoles or AutoSetupPhase.VerifyingChat => new AutoSetupViewModel(
                    StepTitle(state.Phase), message + runtime, "Setting up...", false, true, false, false, true),
                AutoSetupPhase.Downloading => new AutoSetupViewModel(
                    "Downloading " + (state.ModelTitle ?? "the model"),
                    message + runtime, "Setting up...", false, true, false, false, true),
                AutoSetupPhase.Ready => new AutoSetupViewModel(
                    "PaRuX is ready", message, "Set up again", true, false, false, false, false),
                AutoSetupPhase.NoModelFits => new AutoSetupViewModel(
                    "No recommended model fits this PC", message, "Try again", true, false, false, true, false),
                AutoSetupPhase.Failed => new AutoSetupViewModel(
                    "Automatic setup stopped", message, "Set up automatically", false, false, true, true, false),
                AutoSetupPhase.Cancelled => new AutoSetupViewModel(
                    "Automatic setup cancelled", message, "Set up automatically", true, false, false, true, false),
                AutoSetupPhase.Skipped => new AutoSetupViewModel(
                    "Automatic setup skipped", message, "Set up automatically", true, false, false, false, false),
                _ => new AutoSetupViewModel("Set up PaRuX", message, "Set up automatically", true, false, false, false, false)
            };
        }

        private static string StepTitle(AutoSetupPhase phase) => phase switch
        {
            AutoSetupPhase.CheckingHardware => "Step 1 of 5: reading this PC",
            AutoSetupPhase.CheckingRuntime => "Step 2 of 5: text runtime",
            AutoSetupPhase.SelectingModel => "Step 3 of 5: choosing a model",
            AutoSetupPhase.AssigningRoles => "Step 4 of 5: assigning roles",
            AutoSetupPhase.VerifyingChat => "Step 5 of 5: verifying local chat",
            _ => "Setting up"
        };
    }
}
