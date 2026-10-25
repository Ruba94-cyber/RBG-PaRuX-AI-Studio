# PaRuX coding-agent dogfood harness

The dogfood harness lets a local coding agent work in a disposable clone of PaRuX. A separate acceptance script checks the resulting Git changes, builds one selected project, and runs only a specifically named test filter. A PASS means those checks passed; it does not accept or merge the change.

## Run one task

1. Create an allowed scratch directory and a prompt file. For example, `D:\fixtures` can be the allowed root and `D:\fixtures\dogfood-01\prompt-source.md` can hold the task. The allowed root must already exist. Use a fresh scratch directory for each task.
2. Create the copy and its external prompt file:

   ```powershell
   $scratch = 'D:\fixtures\dogfood-01'
   .\tools\dogfood\Start-DogfoodCopy.ps1 `
     -ScratchRoot $scratch `
     -AllowedRoot 'D:\fixtures' `
     -TaskPromptPath (Join-Path $scratch 'prompt-source.md')
   ```

   The script clones the current checkout locally, checks out the supplied `-BaseCommit` or the local `main` commit resolved at start, removes the clone's `origin`, and stores `task.md` and `base-commit.txt` in `$scratch\.dogfood`. The working copy is `$scratch\repo`.
3. Choose the local agent command explicitly. The JSON array is an executable followed by argument strings; `{prompt}` is replaced with the prompt-file path. No command or agent is selected by default, and the command is started without a shell parser:

   ```powershell
   $agent = '["local-agent.exe","code","--prompt","{prompt}"]'
   .\tools\dogfood\Invoke-DogfoodAgent.ps1 `
     -CopyPath (Join-Path $scratch 'repo') `
     -PromptPath (Join-Path $scratch '.dogfood\task.md') `
     -AgentCommandTemplate $agent `
     -TimeoutSeconds 900
   ```

   `run.json` records UTC start and end times, the process exit code, timeout status, and a launch or termination failure when one occurs. Standard output and error are captured in `.dogfood`.
4. Run independent acceptance with an explicit, targeted test filter:

   ```powershell
   $base = (Get-Content (Join-Path $scratch '.dogfood\base-commit.txt') -Raw).Trim()
   .\tools\dogfood\Test-DogfoodAcceptance.ps1 `
     -CopyPath (Join-Path $scratch 'repo') `
     -BaseCommit $base `
     -BuildProjectPath 'tests/PaRuX.Server.Tests/PaRuX.Server.Tests.csproj' `
     -TestProjectPath 'tests/PaRuX.Server.Tests/PaRuX.Server.Tests.csproj' `
     -TestFilter 'FullyQualifiedName=PaRuX.Server.Tests.DogfoodHarnessContractTests.Acceptance_script_selftest_exercises_the_harness_refusal_and_verdict_contracts'
   ```

   Acceptance writes `verdict.json` and prints `PASS` or `FAIL` with the causes. It checks tracked and untracked changes against the base commit, limits paths to `src/`, `tests/`, and `docs/`, rejects licence and notice changes and secret-like added content, builds the selected project with `--no-restore`, then runs the selected test project with the required filter. The filter must use exact `FullyQualifiedName=` equality with a dotted test method name; substring, namespace-wide, and full-suite filters are refused. A successful test command must report at least one passed test. A refusal before build/test leaves both gates marked `not-run` in the verdict.
5. Or run the same three stages through `Invoke-DogfoodTask.ps1`, passing the scratch root, allowed root, prompt source, explicit agent template, timeout, build and test project paths, and a targeted filter. It stops at the first failing stage. It does not commit, push, merge, or adopt changes.
6. Inspect the entire diff and the verdict yourself before adopting anything. The harness is evidence about its listed checks; it does not prove that the change is correct or good.

## Refusal meanings

- Copy creation refuses an omitted or relative scratch root, a root outside the explicitly supplied allowed root, a path that resolves through a reparse point, a destination inside the primary checkout, drive or profile roots, and a non-empty copy target. Choose a new scratch directory beneath the allowed root.
- Agent and acceptance stages also refuse a copy path that overlaps the checkout containing the harness. This protects direct stage invocations as well as the single entry point.
- Agent and acceptance stages refuse a redirected `.dogfood` sidecar or an output path that traverses a reparse point or overlaps the primary checkout. This prevents their prompt, logs, run record, or verdict from following a pre-existing junction or symbolic link.
- Agent execution refuses a missing explicit command, a malformed command template, a missing `{prompt}` placeholder, a prompt inside the repository, or an invalid timeout. An agent timeout or non-zero exit is recorded and stops the orchestrated task.
- Acceptance refuses a missing base commit, project path outside the copy, missing or broad test filter, changed paths outside the three allowed areas, licence or notice changes, and secret-like additions. The filename check covers `LICENSE*`, `NOTICE*`, `COPYING*`, third-party notice names, and paths containing `licen`. The content checks include private-key blocks, common hosted-model and API tokens, credential assignments, AWS access keys, and GitHub tokens. Build failure prevents the targeted test step from running; either verification failure produces a FAIL verdict.

## Limits and cleanup

- A local model's coding quality is not measured by this harness. The agent has a hard timeout, and the harness runs one task per scratch directory at a time.
- The scripts write their own prompt, logs, and verdict only in `.dogfood`, and start the agent with the copy as its working directory. PowerShell cannot confine an arbitrary executable's filesystem or network access by changing its working directory. Use a trusted local agent configured for the disposable copy; use operating-system isolation when the executable itself is not trusted.
- The owner reviews every diff before adoption. PASS proves only that the scoped path, content, build, and targeted-test checks ran successfully. It does not prove the code is good.
- To discard a task, confirm the resolved scratch path is the intended disposable directory, then remove both the copy and its sibling `.dogfood` directory. For the example above:

  ```powershell
  Remove-Item -LiteralPath 'D:\fixtures\dogfood-01\repo' -Recurse -Force
  Remove-Item -LiteralPath 'D:\fixtures\dogfood-01\.dogfood' -Recurse -Force
  ```

  Keep or remove `prompt-source.md` separately according to its owner.

## Self-tests

Each script supports `-SelfTest`. Self-tests use temporary Git repositories and fake local agents or verification runners; they do not clone this checkout, contact a network, or use a model. Run them under both installed hosts when available:

```powershell
pwsh -NoProfile -NonInteractive -File tools/dogfood/Start-DogfoodCopy.ps1 -SelfTest
pwsh -NoProfile -NonInteractive -File tools/dogfood/Invoke-DogfoodAgent.ps1 -SelfTest
pwsh -NoProfile -NonInteractive -File tools/dogfood/Test-DogfoodAcceptance.ps1 -SelfTest
pwsh -NoProfile -NonInteractive -File tools/dogfood/Invoke-DogfoodTask.ps1 -SelfTest
```

For Windows PowerShell 5.1, run the same four commands with `powershell` in place of `pwsh`.
