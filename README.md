# little_helper

A lean coding agent harness designed for small and local models (7B–27B parameters).

**Design principle:** The harness gets out of the model's way. Every layer of abstraction is a potential failure point. The model knows how to code — give it tools, stay silent, and verify the result.

**Predecessor:** [delta-code](../delta-code/) (asgard-core, Go, ~33.5K LOC) — over-engineered, 5-25% success rate on edit tasks. This is the replacement.

---

## Core Architecture

The entire system is one loop:

```
┌─────────────────────────────────────────────┐
│                                             │
│   User Prompt                               │
│       │                                     │
│       ▼                                     │
│   ┌───────────────┐                         │
│   │  Build Context │ ◄── repo map, README   │
│   └───────┬───────┘                         │
│           │                                 │
│           ▼                                 │
│   ┌───────────────┐                         │
│   │  Model Call    │ ◄── OpenAI-compat API  │
│   │  (+ tools)     │                         │
│   └───────┬───────┘                         │
│           │                                 │
│           ▼                                 │
│   ┌───────────────┐                         │
│   │  Execute Tools │ ── read/run/write      │
│   └───────┬───────┘                         │
│           │                                 │
│           ▼                                 │
│   ┌───────────────┐                         │
│   │  Observe       │ ── did files change?   │
│   └───────┬───────┘                         │
│           │                                 │
│           ▼                                 │
│      ┌────────┐                             │
│      │ Done?  │──── No ──► loop back        │
│      └───┬────┘                             │
│          │ Yes                              │
│          ▼                                  │
│   ┌───────────────┐                         │
│   │  Verify        │ ── build/test/lint     │
│   └───────┬───────┘                         │
│           │                                 │
│           ▼                                 │
│      ┌────────┐                             │
│      │ Pass?  │──── No ──► repair (up to N) │
│      └───┬────┘                             │
│          │ Yes                              │
│          ▼                                  │
│       Result                                │
│                                             │
└─────────────────────────────────────────────┘
```

**That's it.** No lanes, no ownership resolution, no prompt file loading, no tool-blind mode, no delegation hierarchy. Just: call model, execute tools, verify, done.

---

## Design Rules (Backed by Research)

These are non-negotiable. Every one addresses a failure mode found in delta-code or confirmed by agent research.

### 1. One System Message, Under 1000 Tokens

**Rule:** The system prompt is ≤1000 tokens. Period.

**Why:** "Lost in the Middle" shows 30%+ degradation on content buried in long contexts. pi-mono achieves the best results with ~1000 tokens. Delta-code sent 7 system messages before the user prompt. The model followed the wrong one and never wrote files.

**Implementation:**
```
You are a coding assistant. You have access to tools: read, run, write.
Use them to complete the task. When done, say DONE.
```

That's the entire system prompt.

### 2. 5 Tools Maximum

**Rule:** Only `read`, `run`, `write`, `search`, and `bash` (alias for run).

**Why:** Hermes loads 31 tools → 14K tokens of overhead → leaves no room for context. Small models can't reliably select from 31 tools. Delta-code had 7 tool types plus file listing extraction plus whole-file output mode — the model chose wrong constantly.

**Implementation:** 5 tools. Each has a one-sentence description. No sub-tools, no tool variants, no tool-blind mode.

### 3. No File Listing Extraction

**Rule:** The model uses `write` tool to create/edit files. Period. No parsing whole-file listings from model output.

**Why:** Delta-code tried to extract file listings from model text output, then validated them with a Go parser, then rejected them if parsing failed. This killed runs where the model's output was 99% correct but had a missing closing brace.

**Implementation:** The model says `write("path", content)`. We write it. Done.

### 4. Verify After, Not During

**Rule:** Verification runs AFTER the model says it's done. Not during. Not interleaved.

**Why:** Delta-code verified after every coder turn, then fed verification errors back as a "repair prompt." The repair prompt told the model NOT to use tools. This was the #1 cause of the "model reads files but never writes" failure.

**Implementation:** Model loops freely with tools. When it says DONE (or hits step limit), THEN run `dotnet test` or equivalent. If fail, feed error back as a single user message: "Verification failed. Error: ... Fix it." One retry only.

### 5. Stalled = 5 Repeated Observations, Not 3

**Rule:** Kill the loop after 5 identical tool outcomes, not 3.

**Why:** Delta-code killed after 3 reads with no writes. But models often need 3-4 reads to understand a codebase before editing. With 4K char truncation on reads, the model had to read the same file multiple times to see different parts. We bumped to 5 and it helped.

### 6. No Truncation of Tool Output

**Rule:** File reads and command output are sent to the model in full. Context compaction handles overflow.

**Why:** Delta-code truncated file reads to 4K chars. The model literally couldn't see the line it needed to edit (at line 90 of a 940-line file). This was the #3 cause of failures.

**Implementation:** Full output, always. Context compaction replaces OLD observations with `[previous observation: read loop.go — 940 lines, no changes]`, keeping only the latest observation in full.

### 7. State Machine, Not Free-Form Loop

**Rule:** The agent loop is an explicit state machine with named states.

**Why:** StateFlow research shows FSM modeling yields 63.73% success vs 40.3% for ReAct. Explicit error states prevent "wandering" where the model loops forever. Delta-code had no explicit error state — it just kept looping until the step limit.

**States:**
```
PLANNING → EXECUTING → OBSERVING → (loop back to EXECUTING or → VERIFYING → DONE)
                                  ↘ ERROR_RECOVERY → EXECUTING (max 2 times)
```

### 8. Single File = Single Responsibility

**Rule:** Each source file does one thing. No 993-line god files.

**Why:** Delta-code had `openai_compat.go` at 993 lines, `loop.go` at 961 lines, `lanes.go` at 670 lines. Agents couldn't understand the codebase because they couldn't read the files in one turn.

**Implementation:** Each file is ≤300 lines. If it grows beyond that, split it.

---

## What We Keep from delta-code

Not everything was wrong. These concepts survive:

1. **Deterministic verification** — running `go test` / `dotnet test` after edits is genuinely valuable
2. **Git-based change detection** — `git diff --stat` to see what changed works well
3. **The archetype concept** — pre-made project templates for greenfield tasks
4. **Control service pattern** — a service that safely executes shell commands

---

## What We Drop from delta-code

Everything else:

- Lane type hierarchy (8 types → 0)
- Workspace backend abstraction (3 backends → 1: local)
- Prompt file loading (file-based overrides → hardcoded, tested prompts)
- Tool-blind mode and whole-file listing extraction
- Delegation/watchdog/stall-detection complexity
- Planner's task classification into 8+ kinds
- `{identified_files}` placeholder resolution
- Repair budget tracking, error fingerprinting, no-improvement cutoff
- The entire `autonomy/` package (28 files, ~8K LOC)
- The entire `runtime/` package (12 files, ~5K LOC)
- The entire `planner/` package (9 files, ~3K LOC)

---

## Target: 3-5K LOC

| Component | Lines | Responsibility |
|-----------|-------|----------------|
| Program.cs | ~200 | CLI + HTTP server |
| Agent.cs | ~500 | Core loop (state machine) |
| Tools.cs | ~400 | read, run, write, search implementations |
| ModelClient.cs | ~300 | OpenAI-compatible API client |
| Verifier.cs | ~200 | Build/test verification |
| Compaction.cs | ~200 | Context window management |
| Types.cs | ~200 | Records, enums, result types |
| Tests | ~800 | Unit + integration |
| **Total** | **~2800** | |

---

## Key Metrics (from delta-code audit)

These are the numbers that killed delta-code. Little_helper must not repeat them:

| Metric | delta-code | Target |
|--------|-----------|--------|
| LOC | 33,500 | 3,000-5,000 |
| System prompt tokens | ~4,000 (7 messages) | <1,000 (1 message) |
| Tools offered | 7 types + file listing | 5 tools |
| File read truncation | 4K chars | Full file |
| Stall threshold | 3 repeats | 5 repeats |
| Lane types | 8 | 0 (single loop) |
| Prompt sources | File-based (overrides hardcoded) | Hardcoded only |
| Success rate (small edits, Qwen3-14B) | 5-25% | >60% |

---

## Research Sources

Full analysis in [RESEARCH.md](RESEARCH.md) and [LANGUAGE_ANALYSIS.md](LANGUAGE_ANALYSIS.md).

Key papers:
- **ATLAS** (74.6% with 14B model via Generate→Verify→Repair)
- **StateFlow** (63.73% success with FSM, 5.8x cheaper)
- **JetBrains observation masking** (2.6% higher solve rates, 52% cheaper)
- **AutoCodeBench** (C# outperforms Go by 5-15pp across all model sizes)
- **SOLVE-Med/MATA** (small specialized models can outperform larger standalone)

---

*Built on the ashes of asgard-core. Less code, more working.*
