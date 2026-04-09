# delta-code Post-Mortem

The honest assessment after deep auditing of asgard-core (Go, ~33.5K LOC, 153 files).

## The Pipeline Problem

The runtime boils down to: receive prompt → classify → model reads/writes files → verify → return. Instead, the execution path was:

```
Request → OpenAI compat → Gateway → Runtime Engine → Planner (classify)
→ Lane compilation (150+ line specs) → Lane resolution (path sets, ownership)
→ Exploration lane (keyword matching) → Contract lane → Autonomy runner
→ Prompt loader (file overrides) → System prompt assembly (7 messages!)
→ Model call → Tool extraction → Control service → Executor → Change detection
→ Verifier → Repair loop → Reviewer gate → checkFalseComplete → Result
```

Every failure was the pipeline getting in its own way:

| Failure | Root Cause |
|---------|-----------|
| Model doesn't write files | Prompt files override hardcoded prompts with "provide explanations" |
| Model can't see target line | 4K truncation on file reads |
| Model stalls after 3 reads | Aggressive stall detection |
| Repair makes it worse | Repair prompt says "don't use tools" |
| Verification empty | Greenfield fingerprint has no package manager yet |
| Bootstrap doesn't install deps | archetype.Apply only writes files |
| Ownership resolution fails | `{identified_files}` hard-fails on empty |

**Common theme:** Each layer of abstraction introduces its own truncation, validation, and failure mode — and they compound.

## What Research Says You Should Do Instead

1. Minimal system prompts (~1000 tokens, not 7 messages)
2. Simple loop: Generate → Verify → Repair, bounded retries
3. Observation masking over summarization
4. Explicit FSM with error states, not layered abstractions
5. Tiered toolsets (5–8 essential tools, not 31)
6. Small specialists for mechanical tasks

ATLAS gets 74.6% on LiveCodeBench with a 14B model through a clean Generate → Verify → Repair pipeline. Not complex orchestration — clean infrastructure.

## What to Keep (Concepts)

- Scoped ownership (as a simple parameter, not a type hierarchy)
- Deterministic verification (build/test/lint)
- Git-based change detection
- Archetype/bootstrap concept (inlined, not separated)
- Control service pattern (safe shell execution)

## What to Drop

Everything else: lane type hierarchy, workspace backend abstraction, prompt file loading, tool-blind mode, whole-file listing extraction, delegation/watchdog/stall-detection, planner's 8+ task kinds, `{identified_files}` resolution, repair budget tracking, error fingerprinting, `autonomy/` (28 files), `runtime/` (12 files), `planner/` (9 files).

## The Target

```
Request → Classify (simple: read/edit/create) → Agent Loop
  → Model call (minimal prompt + 5 tools)
  → Execute tools
  → After N turns or model says done:
    → Verify (build/test)
    → If fail, feed error back, retry up to M times
  → Return result
```

~3K–5K LOC. One loop. One set of prompts. No lanes, no ownership resolution, no prompt file loading. Just: call model → execute tools → verify → done.
