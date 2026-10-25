namespace PaRuX.Services
{
    /// <summary>What a coding-task card may claim about a completed task.</summary>
    public sealed record CodingVerificationPresentation(bool IsVerified, string Badge, string ApplyWarning)
    {
        public const string VerifiedBadge = "Completed and verified";
        public const string UnverifiedBadge = "Completed, not verified";
        public const string FailedBadge = "Verification failed";
        public const string UnconfirmedBadge = "Completed, verification not confirmed";

        /// <summary>
        /// Maps Core's VerificationStatus to what the UI may say. Only "pass" (and the legacy fixture
        /// value "passed") is verified. "unverified" (no executed check), "fail", "inconclusive", a
        /// missing value or anything unknown must never be shown as verified.
        /// </summary>
        public static CodingVerificationPresentation For(string? verificationStatus)
        {
            string value = (verificationStatus ?? string.Empty).Trim().ToLowerInvariant();
            return value switch
            {
                "pass" or "passed" => new CodingVerificationPresentation(true, VerifiedBadge, string.Empty),
                "unverified" => new CodingVerificationPresentation(false, UnverifiedBadge,
                    "WARNING: no check (build/test) was run for this change. The model's opinion is not a verification. Review the diff carefully.\n\n"),
                "fail" or "failed" => new CodingVerificationPresentation(false, FailedBadge,
                    "WARNING: an executed check failed.\n\n"),
                _ => new CodingVerificationPresentation(false, UnconfirmedBadge,
                    "WARNING: verification of this change is not confirmed.\n\n")
            };
        }
    }
}
