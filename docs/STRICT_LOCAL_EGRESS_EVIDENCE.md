# Strict-local egress evidence

PaRuX core inference must stay on this machine. Evidence comes in three layers; only the first two are
automated today.

## 1. Code-level denial (automated tests)

- `CloudFallbackTests` and `StrictLocalInferenceIntegrationTests`: restored cloud credentials never arm
  inference, and a ready-runtime failure sends no request to the configured fallback endpoint (a loopback
  observer counts zero connections).
- `ManagedListenerOwnershipTests` and the connected-peer attestation tests: a prompt is sent only to a
  loopback connection owned by the recorded PaRuX-launched runtime.

## 2. Non-elevated connection sampling

`scripts/sample-process-egress.ps1` polls the TCP/UDP tables for chosen processes and their descendants and
records any connection whose remote endpoint is not loopback.

```powershell
# prove the detector (loopback-only child passes; a child dialing 192.0.2.1 is flagged)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\sample-process-egress.ps1 -SelfTest

# sample a running Core during a chat/agent run
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\sample-process-egress.ps1 `
  -ProcessName PaRuX.Server -DurationSeconds 120 -OutFile .ai\artifacts\egress.json
```

Limits (stated in the result's `method` field): this is polling, not a capture. Connections shorter than the
effective sample period (about 0.4 s because the table query itself is slow) and individual UDP datagrams are
invisible; UDP only reveals the local bind. A `LOOPBACK_ONLY_AT_SAMPLED_INSTANTS` verdict is evidence, not proof.

## 3. Elevated packet capture (not yet run; needs the user to open an elevated shell)

1. Elevated PowerShell. Start capture: `pktmon filter remove; pktmon start --capture --pkt-size 0 -f C:\path\egress.etl`
   (or `netsh trace start capture=yes tracefile=C:\path\egress.etl report=disabled`).
2. Run the same chat/agent workload against PaRuX Core with no internet-enabled tools.
3. Stop: `pktmon stop` (or `netsh trace stop`); convert: `pktmon etl2txt egress.etl -o egress.txt`.
4. Accept only if every packet attributed to the PaRuX runtime/server process has loopback source and
   destination. Name resolution by other processes is out of scope and must be listed, not hidden.
5. Optional hard proof: with the user's consent, add a Windows Firewall outbound block rule for the PaRuX
   server and runtime executables, rerun the workload, and show chat/agent still complete; remove the rule.
