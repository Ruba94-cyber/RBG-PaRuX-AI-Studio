# Fair response-speed comparisons

The local benchmark scripts send the same system, developer and user messages, sampling
parameters, reasoning-effort setting, tool-free request shape and output limit to every selected
provider. They report submit-to-first-visible and submit-to-last-visible latency separately,
plus exact token throughput only when stream usage proves it is comparable. Twenty measured rounds
are a hard minimum, not a guarantee of significance; the gate also requires a paired 95% bootstrap
lower confidence bound on median TTFT or median last-visible improvement to meet the configured
minimum gain (default 2%, minimum configurable gain 0.1%). Allowed regression tolerance is a
separate setting capped at 5%, so setting it to zero still cannot turn a tie into a speed win.
Confidence bounds are compared at full precision and only rounded for display. A confidence
interval that is too wide is inconclusive and must not be presented as a speed win.

## Parity manifest

`AssertProvider` in `scripts/benchmark-reference-stream.ps1` now requires a fresh operator-attested
manifest. The harness re-hashes every listed local model artifact, confirms that API model IDs match
the benchmark cases, and fails closed if providers differ in artifact bytes, chat-template hash,
normalized runtime-settings hash, context, parallelism, warm-up state or warm-up count. A run with
fewer than 20 rounds is smoke-only and cannot support the speed assertion. Context, concurrency and
warm-up values must be JSON integer numbers (not strings or fractions). Manifest bytes and model
artifact hashes are checked again after measurement; a change invalidates the evidence. Paths are
not copied to the generated report.

Example shape (replace the placeholders with evidence from the actual active providers):

```json
{
  "schemaVersion": 1,
  "verifiedBy": "local-operator",
  "verifiedUtc": "2026-09-26T12:00:00Z",
  "providers": {
    "parux": {
      "apiModelId": "parux-main",
      "artifactPath": "C:\\models\\same-model.gguf",
      "artifactSha256": "<64 hexadecimal SHA-256 characters>",
      "runtimeSettingsSha256": "<64 hex chars for canonical comparable runtime settings>",
      "chatTemplateSha256": "<64 hex chars for the active chat template>",
      "contextTokens": 8192,
      "parallel": 1,
      "warmStateConfirmed": true,
      "warmupRequests": 2,
      "verificationNote": "How the active model and settings were inspected"
    },
    "competitor": {
      "apiModelId": "model-alias",
      "artifactPath": "D:\\models\\same-model.gguf",
      "artifactSha256": "<same SHA-256 as PaRuX>",
      "runtimeSettingsSha256": "<same canonical settings SHA-256 as PaRuX>",
      "chatTemplateSha256": "<same chat-template SHA-256 as PaRuX>",
      "contextTokens": 8192,
      "parallel": 1,
      "warmStateConfirmed": true,
      "warmupRequests": 2,
      "verificationNote": "How the active model and settings were inspected"
    }
  }
}
```

`runtimeSettingsSha256` should be computed from a canonical representation of the comparable
effective inference settings (for example context, batch, KV precision, GPU offload, concurrency
and other settings held constant for this experiment), not from provider-specific runtime identity.
`chatTemplateSha256` covers the effective prompt template, which the shared API cannot expose
uniformly. The benchmark verifies that listed files match their declared hashes, but the common
OpenAI-compatible API cannot independently prove that a third-party process loaded the listed
file; the generated report marks the manifest **operator-attested** and calls out that limitation.
If the active model/template/settings cannot be inspected, do not create a manifest or claim a
competitive speed win.

The direct harness can then be run with `-ParityManifestPath <manifest>` and
`-AssertProvider parux -Repetitions 20`. The combined product comparison wrapper accepts
`-ChatParityManifestPath <manifest>` and records the same evidence status. By default it still
records diagnostic timings without treating them as a winner.

The speed gate is not a quality gate or a universal best-in-class verdict. Before describing PaRuX as faster or best-in-class, separately
review the saved outputs against a predeclared quality floor and verify successful-output parity,
reliability and resources. A failed, truncated or materially worse answer never wins on latency.
First-request latency is not labelled cold unless model/runtime unload and cold-state evidence were
independently established; cold and warm results must be reported separately when both matter.
