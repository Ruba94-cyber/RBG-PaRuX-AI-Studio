# PaRuX Binary Analysis / Reverse Engineering

Status: **implementation present on the current feature slice; Windows build/tool acceptance still required before DONE**.

This document is subordinate to `PARUX_EXECUTION_PLAN.md`. The canonical roadmap and release status always live there.

## Product goal

PaRuX should be able to use a strong local model as an evidence-driven reverse-engineering assistant instead of behaving like a chat model that merely explains disassembly pasted by hand.

The intended loop is:

`goal → inspect artifacts → collect structured evidence → choose next tool → inspect/disassemble/decompile → reason → verify result`

The Windows desktop remains the execution workstation. The local model can be Standard or Uncensored. Selecting an **UNCENSORED** local profile does not enable a second PaRuX content-policy layer: the Binary Analysis agent sends its evidence/tool prompt to the selected local model and does not rewrite the model into a Standard persona. Execution authority remains separate from model behavior.

## Implemented architecture

### Durable agent role

`binary-analysis` is a first-class desktop-executable orchestrator role.

- `AgentOrchestrator` recognizes binary-analysis / reverse-engineering / decompile / disassemble / PE / ELF / Mach-O / firmware intents.
- `AgentWorkerService` treats `binary-analysis` as desktop-owned, so a headless server cannot leave it stuck in `running` forever.
- `AgentBinaryAnalysisWorkerService` claims the task on Windows, streams step logs, supports cancellation, and sends the result into the existing independent verifier instead of self-declaring success.
- Workflow Builder accepts `binary-analysis` as a real executable role.
- `App.xaml.cs` starts the worker with the existing Coding / UI Verification / Computer Use workers.

### Workspace

`AppSettings.BinaryAnalysisWorkspaceRoot` is the optional dedicated analysis root. If it is empty, the worker reuses `AgentCodingWorkspaceRoot` so existing PaRuX installations do not require a migration before the new capability can work.

Every artifact path is canonicalized and rejected if it escapes the selected workspace.

### Built-in artifact inspection

`BinaryAnalysisTools` provides dependency-free structured inspection before an external reverse-engineering suite is needed:

- SHA-256 fingerprint;
- file size;
- whole-file entropy;
- PE/COFF recognition;
- ELF recognition;
- Mach-O recognition;
- x86 / x64 / ARM / ARM64 / RISC-V and common architecture mapping;
- PE entry-point RVA;
- PE image base;
- PE subsystem;
- PE timestamp;
- PE section table;
- per-section entropy;
- CLR / managed-.NET detection through the PE COM descriptor;
- bounded ASCII string extraction;
- bounded UTF-16LE string extraction;
- explicit notes when a signal is ambiguous (for example, high entropy is not automatically labelled as packing/encryption).

The original target file is never patched or overwritten by these primitives.

### External disassembler adapters

PaRuX discovers and can invoke, when already installed by the user:

- LLVM `llvm-objdump`;
- GNU `objdump`;
- Microsoft `DUMPBIN`;
- `radare2` / `r2`;
- Ghidra `analyzeHeadless` discovery.

Arguments are built by PaRuX through fixed read-only command shapes; the Binary Analysis model does not receive an arbitrary shell primitive.

### Decompiler adapters

`BinaryDecompilerTools` adds real decompilation rather than stopping at disassembly.

#### Managed .NET

When the target is detected as managed and `ilspycmd` is available, PaRuX runs ILSpy with update checks disabled and captures the C# decompilation from stdout. No source project is silently written into the inspected folder.

#### Native binaries

When Ghidra Headless is available, PaRuX:

1. creates an ephemeral temp project outside the inspected workspace;
2. imports the target into that project;
3. allows Ghidra analysis;
4. runs an embedded PaRuX post-script using Ghidra's `DecompInterface`;
5. exports bounded decompiled C/function evidence;
6. captures diagnostics;
7. deletes the temporary Ghidra project and script directory.

The target artifact itself remains unchanged.

External tools are user-managed: PaRuX may detect and use them, but does not silently install, replace or update them.

## Agent actions

The Binary Analysis agent currently exposes these structured actions to the local model:

- `list_directory`
- `analyze_binary`
- `extract_strings`
- `tool_status`
- `disassemble_binary`
- `decompile_binary`
- `done`

The model receives bounded observations after each real tool execution and chooses the next action. The loop is capped to prevent accidental infinite execution and remains cancellable through the task lifecycle.

## Uncensored-model semantics

PaRuX deliberately separates **model freedom** from **execution authority**.

When the user selects an UNCENSORED local model/profile:

- Binary Analysis does not inject a Standard-style refusal policy;
- the agent is free to reason over the evidence returned by its tools;
- PaRuX does not silently swap the requested manual model/profile merely because the task is reverse engineering;
- analysis output is not post-filtered into a different persona.

This does not turn arbitrary model text into operating-system commands. Tool calls still use explicit structured actions, canonical workspace paths, bounded output, cancellation and the existing Computer Use authorization model where desktop interaction is required. This keeps an unconstrained local model useful for research without making prompt text equivalent to unrestricted process execution.

## Verification already represented in source

Regression coverage includes:

- PE recognition, x64 architecture, CLR flag, subsystem, entry point and section parsing;
- ELF64/x86-64 recognition;
- ASCII + UTF-16LE string extraction;
- workspace traversal rejection;
- unknown-binary handling without fabricated format claims;
- decompiler workspace traversal rejection;
- truthful optional-tool status;
- explicit failure when a requested decompiler does not exist;
- `binary-analysis` orchestrator inference;
- desktop-worker watchdog coverage;
- Workflow Builder role acceptance.

## Windows acceptance gate

Do not mark this capability DONE until the development PC proves all of the following locally:

1. `dotnet restore PaRuX.sln` succeeds;
2. `dotnet build PaRuX.sln -c Release` succeeds;
3. the full `tests/PaRuX.Server.Tests` suite passes;
4. a real native PE is fingerprinted and its sections/entry point are reported correctly;
5. a real managed .NET assembly is detected as managed and ILSpy output is captured when `ilspycmd` is present;
6. LLVM/GNU/DUMPBIN/radare2 discovery is truthful for the tools actually installed;
7. Ghidra Headless discovery is truthful;
8. when Ghidra is installed, a native binary produces decompiled function evidence and the temp project is removed afterward;
9. cancelling a running binary-analysis task stops the agent and does not strand it in `running`;
10. the independent verifier receives the final result;
11. STANDARD and UNCENSORED selections both remain the profile the user selected unless existing explicit AUTO/fallback policy says otherwise;
12. Gaming Mode prevents heavy Binary Analysis execution just as it does Coding/Computer Use.

Only real Windows evidence can close these acceptance items; source inspection alone is not runtime validation.
