The Honest Assessment

 After spending deep time in this codebase, here's the pattern:

 The runtime pipeline has ~33K LOC to do something that fundamentally boils down to:
 1. Receive a prompt
 2. Classify the task
 3. Let the model read files, then write files
 4. Verify the result
 5. Return

 But instead of that simple loop, the execution path is:

 ```
   Request → OpenAI compat → Gateway → Runtime Engine → Planner (classify)
   → Lane compilation (150+ line lane specs) → Lane resolution (path sets, ownership)
   → Exploration lane (deterministic file keyword matching) → Contract lane
   → Autonomy runner → Prompt loader (file-based, overrides hardcoded good defaults)
   → System prompt assembly (7 system messages!) → Model call → Tool extraction
   → Control service dispatch → Executor → Change detection (git + snapshot)
   → Verifier → Repair loop → Reviewer gate → checkFalseComplete → Result
 ```

 Every single failure mode I found was the pipeline getting in its own way:

 ┌────────────────────────────────┬─────────────────────────────────────────────────────────────────────┐
 │ Failure                        │ Root Cause                                                          │
 ├────────────────────────────────┼─────────────────────────────────────────────────────────────────────┤
 │ Model doesn't write files      │ Prompt files override hardcoded prompts with "provide explanations" │
 ├────────────────────────────────┼─────────────────────────────────────────────────────────────────────┤
 │ Model can't see target line    │ 4K truncation on file reads                                         │
 ├────────────────────────────────┼─────────────────────────────────────────────────────────────────────┤
 │ Model stalls after 3 reads     │ Aggressive stall detection                                          │
 ├────────────────────────────────┼─────────────────────────────────────────────────────────────────────┤
 │ Repair makes it worse          │ Repair prompt says "don't use tools"                                │
 ├────────────────────────────────┼─────────────────────────────────────────────────────────────────────┤
 │ Verification empty             │ Greenfield fingerprint has no package manager yet                   │
 ├────────────────────────────────┼─────────────────────────────────────────────────────────────────────┤
 │ Bootstrap doesn't install deps │ archetype.Apply only writes files                                   │
 ├────────────────────────────────┼─────────────────────────────────────────────────────────────────────┤
 │ Ownership resolution fails     │ {identified_files} hard-fails on empty                              │
 └────────────────────────────────┴─────────────────────────────────────────────────────────────────────┘

 The common theme: layers of abstraction that each introduce failure modes. The lane system, the contract lane, the prompt
 loader, the tool-blind mode, the delegation system, the watchdog, the progress tracker — each layer has its own truncation,
 its own validation, its own failure mode. And they interact in ways that compound.

 What the Research Tells You

 From your research findings, the strong signals are:

 1. Minimal system prompts (~1000 tokens, not 7 system messages)
 2. Simple loop: Generate → Verify → Repair, bounded retries
 3. Observation masking over summarization
 4. Explicit FSM with error states, not layered abstractions
 5. Tiered toolsets (5-8 essential tools, not 31)
 6. Small specialists for mechanical tasks

 The asgard-core codebase does the opposite of most of these: verbose prompts, complex orchestration, monolithic tool
 loading, and abstractions that obscure what's actually happening.

 My Recommendation

 Yes, start fresh. But don't throw everything away — extract the hard-won knowledge:

 ### Keep (conceptual)

 - The lane idea (scoped ownership) — but as a simple parameter, not a type hierarchy
 - Deterministic verification (build/test/lint) — this is genuinely valuable
 - Git-based change detection — works well when not fighting truncation
 - The archetype/bootstrap concept — but inline it, don't separate it
 - Archetype file templates — these are fine
 - Control service pattern (shell tool execution) — solid

 ### Drop

 - The entire lane type hierarchy (exploration_lane, tiny_cluster_lane, bootstrap_lane, verify_lane, repo_bugfix_lane,
 test_repair_lane, single_file_lane, generic-implementation) — replace with a single loop
 - The prompt loader with file-based overrides — use hardcoded, tested prompts
 - Tool-blind mode / whole-file listing extraction / Go parser validation of model output
 - The delegation/watchdog/stall-detection complexity — a simple retry counter is enough
 - The planner's task classification into 8+ task kinds with slot inference
 - The {identified_files} placeholder resolution system
 - The workspace backend abstraction (LocalBackend/TempDirBackend/DockerBackend)

 ### Target Architecture (Lean)

 ```
   Request → Classify (simple: read/edit/create) → Single Agent Loop
     → Model call with minimal system prompt + tools (read/run/write)
     → Execute tool calls
     → After N turns or model says done:
       → Verify (build/test if applicable)
       → If fail, feed error back to model, retry up to M times
     → Return result
 ```

 ~3K-5K LOC, not 33K. One loop. One set of prompts. No lane types. No ownership resolution. No prompt file loading. Just:
 call model → execute tools → verify → done.

 The key insight from your research: ATLAS gets 74.6% on LiveCodeBench with a quantized 14B model not through complex
 orchestration, but through a clean Generate → Verify → Repair pipeline. That's the target.
