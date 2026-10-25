namespace PaRuX.Contracts;

/// <summary>
/// Plain-language explanation and next step for the bounded machine reason codes that Candidate evaluation
/// and comparison can return. Pure text: it never changes a decision. Unknown codes are returned humanised.
/// </summary>
public static class CandidateReasonText
{
    public static string Explain(string? reasonCode)
    {
        if (string.IsNullOrWhiteSpace(reasonCode)) return string.Empty;
        string code = reasonCode.Trim();
        if (code == "active_model_proof_stale" || code.StartsWith("incumbent_", StringComparison.Ordinal))
            return "The model currently running has no verified identity for this comparison"
                + (code.StartsWith("incumbent_", StringComparison.Ordinal)
                    ? " (" + Humanise(code["incumbent_".Length..]) + ")" : string.Empty)
                + ". Only a model started through an exact, evidence-scoped load is recorded as the reference."
                + " To fix: force the reference model for the Standard / Balanced scope, send one chat message so it loads,"
                + " then compare again. Note that forcing creates a manual choice for that scope, which blocks"
                + " automatic recommendation there until you set the scope back to Automatic.";
        switch (code)
        {
            case "active_winner_unverified_kept":
                return "The current recommendation could not be re-checked right now (a temporary read problem)."
                    + " It was kept unchanged and nothing was verified or committed; it will be checked again later.";
            case "recommendation_changed_during_load":
                return "The recommendation changed while the model was being prepared, so the older choice was not loaded."
                    + " The ordinary automatic choice was used instead.";
            case "manual_pin_role_override":
                return "Automatic recommendation is blocked because a model is assigned to this role."
                    + " Assignments made before this version recorded who made them are treated as yours, even if"
                    + " they were created by automatic assignment. To release it, clear the role assignment in"
                    + " Settings (or reassign the default); automatic assignment never replaces one you made.";
            case "manual_pin_chat_manual":
                return "Automatic recommendation is blocked because Chat is set to a specific model."
                    + " To allow it, choose an Auto option in the Chat model selector.";
            case "manual_pin_scoped":
                return "Automatic recommendation is blocked because this task type is forced or preferred to one model."
                    + " To allow it, set that scope back to Automatic.";
            case "manual_pin_unreadable":
                return "Automatic recommendation is blocked because a saved model-selection setting could not be read"
                    + " (it may be corrupt or locked). To fix: re-save your Chat model choice (select Auto), then retry;"
                    + " your saved choice is never overwritten automatically.";
        }
        switch (code)
        {
            case "consent_required": return "Confirm the change to continue; nothing was changed.";
            case "stale_generation": return "The recommendation state changed since you looked. Refresh and try again; nothing was changed.";
            case "pending_unreconciled": return "A previous change did not finish. PaRuX will settle it on its own; try again afterwards.";
            case "transition_in_progress": return "Another model change is already running. Try again when it finishes.";
            case "gaming_active": return "A game is running, so local AI is paused and no model is changed. Try again after the game.";
            case "server_stopping": return "PaRuX is shutting down, so no model was changed.";
            case "store_unreadable": return "The saved recommendation state could not be read, so nothing was changed.";
            case "intent_rejected": return "The evidence or your settings changed while the change was being prepared, so nothing was changed.";
            case "commit_rejected": return "A final check failed (evidence, settings or the running model changed), so the new model was not adopted and the previous one was restored where possible.";
            case "challenger_load_failed": return "The new model could not be loaded safely, so it was not adopted and the previous model was restored where possible.";
            case "challenger_unhealthy": return "The new model started but did not respond in time, so it was not adopted and the previous model was restored where possible.";
            case "cancelled": return "The change was cancelled before it completed; the previous model was restored where possible.";
            case "no_previous_winner": return "There is no earlier recommended model to go back to.";
            case "rollback_rejected": return "Going back is not possible right now (the earlier model or the settings it was approved under changed); nothing was changed.";
            case "incumbent_not_admissible": return "The model that is running now cannot serve as the comparison reference (it was not started under the evaluated launch). See how to fix this in the comparison result.";
            case "unsupported_scope": return "Only the Chat / Standard / Balanced scope can be recommended.";
            case "licence_evidence_missing": return "No record of the licence this model was downloaded under is stored, so it cannot be adopted. Download it again through Model Hub and review its licence; nothing was changed.";
            case "licence_evidence_changed": return "The stored licence record no longer matches this model's revision or file, so it cannot be adopted. Download the current revision again and review its licence; nothing was changed.";
            case "evidence_environment_unavailable": return "The test results do not say how or where they were measured, so they cannot be reused. Run the evaluation again.";
            case "evaluation_launch_profile_not_actual": return "The test results were measured with a simplified test setup, not the way Automatic mode really starts the model, so they cannot be reused. Run the evaluation again.";
            case "quality_performance_launch_mismatch": return "The quality and speed tests were run with different launch settings, so they cannot be combined. Run the evaluation again.";
            case "launch_model_mismatch": return "The model that is running is not the file the test results belong to. Load the evaluated file, or evaluate the running one.";
            case "launch_configuration_mismatch": return "The model now starts with different settings (context size, slots or reasoning) than when it was tested, so the old results are stale. Run the evaluation again.";
            case "backend_mismatch": return "The graphics backend (CUDA or Vulkan) differs from the one used when this model was tested, so the old results are stale. Run the evaluation again.";
            case "hardware_changed": return "The graphics hardware, driver or system changed since this model was tested, so the old results are stale. Run the evaluation again.";
            case "runtime_changed": return "The runtime version changed since this model was tested, so the old results are stale. Run the evaluation again.";
            case "runtime_dependencies_changed": return "Files the runtime depends on changed since this model was tested, so the old results are stale. Run the evaluation again.";
        }
        switch (code.StartsWith("proof_", StringComparison.Ordinal) ? code["proof_".Length..] : code)
        {
            case "candidate_does_not_meet_reference_margin":
                return "The new model is not clearly faster than the one you use now (it must beat it by the required margin), so nothing was changed.";
            case "quality_below_reliability_floor":
                return "The new model did not answer the quality checks reliably enough, so it was not adopted even if it is faster. Nothing was changed.";
            case "candidate_quality_below_incumbent":
                return "The new model is faster but answers the quality checks less reliably than the one you use now, so it was not adopted. Nothing was changed.";
            case "quality_insufficient_runs":
                return "The new model has not been through enough complete quality runs yet. Run the evaluation again; nothing was changed.";
        }
        if (code.StartsWith("proof_", StringComparison.Ordinal))
            return "The evidence does not qualify this model (" + Humanise(code["proof_".Length..]) + "), so nothing was changed.";
        if (code == "request_parity_execution_profile_differs")
            return "This model's saved execution profile changes the reasoning settings that Automatic mode would use,"
                + " so measuring it would not match real chat. To fix: reset this model's execution profile to the default"
                + " (or accept that it cannot be evaluated), then run the evaluation again.";
        if (code == "request_parity_profile_unavailable")
            return "The execution profile could not be read, so the evaluation was not run. Check the data folder and retry.";
        if (code == "actual_launch_unavailable" || code.StartsWith("launch_descriptor_", StringComparison.Ordinal))
            return "The exact way this model would be launched could not be described, so evidence cannot be tied to"
                + " Automatic mode (" + Humanise(code) + "). The model is not rejected; it is just not measured.";
        if (code == "backend_executable_mismatch" || code == "host_attestation_unavailable")
            return "The runtime or hardware identity could not be verified (" + Humanise(code)
                + "), so no evidence was recorded for it.";
        return Humanise(code);
    }

    private static string Humanise(string code) => code.Replace('_', ' ');
}
