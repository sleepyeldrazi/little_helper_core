# little_helper

A lean agent harness for small/local models (7B–27B parameters). General-purpose — coding tasks are a primary focus, but not the only one.

**Design principle:** Get out of the model's way. The model knows what to do — give it tools, stay silent, observe the result.

**Predecessor:** [delta-code](../delta-code/) (asgard-core, Go, ~33.5K LOC) — over-engineered, 5–25% success rate. This is the replacement.

---

## Architecture: One Loop

```
User Prompt → Build Context (file listing, README if present)
           → Model Call (OpenAI-compat API, tools: read/run/write/search)
           → Execute Tools
           → Observe (did files change? stall detection)
           → Loop until done or step limit
           → Result
```

No lanes, no ownership resolution, no prompt file loading, no tool-blind mode, no delegation hierarchy. Call model, execute tools, done.

---

## Design Rules (Non-Negotiable)

Each rule addresses a failure mode found in delta-code or confirmed by agent research.

### 1. One System Message, Under 1000 Tokens

"Lost in the Middle" shows 30%+ degradation on buried content. pi-mono's best results come from ~1000 tokens. Delta-code sent 7 system messages — the model followed the wrong one.

```
You are a helpful assistant. You have access to tools: read, run, write, search.
Use them to complete the task. When done, say DONE.
```

### 2. 5 Tools Maximum

`read`, `run`, `write`, `search`, `bash` (alias). Hermes loads 31 tools → 14K tokens overhead → no room for context. Small models can't reliably select from 31 tools.

### 3. No File Listing Extraction

The model uses `write("path", content)`. We write it. Done. Delta-code tried to parse file listings from model text output then validate with a Go parser — it rejected outputs that were 99% correct but had a missing closing brace.

### 4. Verification via Skills, Not Core Loop

Verification is handled through skills, not baked into the agent loop. Delta-code verified every turn, then fed errors back as a "repair prompt" that told the model NOT to use tools — the #1 cause of "reads files but never writes" failures. The model reads a `verify` skill when it wants to run build/test commands. The core loop knows nothing about verification — it just calls the model and executes tools.

### 5. Stall = 5 Repeated Observations

Kill the loop after 5 identical tool outcomes. Models often need 3–4 reads to understand a codebase before editing. Delta-code killed after 3 — too aggressive.

### 6. No Truncation of Tool Output

Full file reads, full command output. Context compaction handles overflow. Delta-code truncated to 4K chars — the model literally couldn't see the line it needed to edit. #3 cause of failures.

### 7. State Machine, Not Free-Form Loop

StateFlow research: FSM yields 63.73% success vs 40.3% for ReAct. Explicit error states prevent infinite wandering.

```
PLANNING → EXECUTING → OBSERVING → (loop or → DONE)
                                ↘ ERROR_RECOVERY → EXECUTING (max 2)
```

### 8. Files ≤ 300 Lines, Single Responsibility

Delta-code had 993-line god files. Agents couldn't read them in one turn. If a file grows past 300 lines, split it.

---

## Keep from delta-code

1. **Skills as extension mechanism** — on-demand prompt injection for specialized tasks (verification, etc.)
2. **Git-based change detection** — `git diff --stat`
3. **Archetype concept** — pre-made project templates for greenfield tasks
4. **Control service pattern** — safe shell command execution

---

## Drop from delta-code

Lane type hierarchy (8→0), workspace backend abstraction (3→1), prompt file loading, tool-blind mode, whole-file listing extraction, delegation/watchdog/stall-detection, planner's 8+ task classifications, `{identified_files}` placeholder resolution, repair budget tracking, error fingerprinting, the entire `autonomy/` (28 files), `runtime/` (12 files), and `planner/` (9 files) packages.

---

## Target: 3–5K LOC, C#/.NET

See [LANGUAGE_ANALYSIS.md](LANGUAGE_ANALYSIS.md) for the full argument. Summary: AutoCodeBench shows C# outscores Go by 5–15pp across all model sizes. For agent-maintained code, that's the deciding factor.

| Component | Lines | Responsibility |
|-----------|-------|----------------|
| Program.cs | ~200 | CLI + HTTP server |
| Agent.cs | ~400 | Core loop (state machine) |
| Tools.cs | ~400 | read, run, write, search |
| ModelClient.cs | ~300 | OpenAI-compatible API client |
| Skills.cs | ~100 | Skill discovery & prompt formatting |
| Compaction.cs | ~200 | Context window management |
| Types.cs | ~200 | Records, enums, result types |
| Tests | ~800 | Unit + integration |
| **Total** | **~2600** | |

---

## delta-code Post-Mortem (Why It Failed)

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

The common theme: layers of abstraction that each introduce failure modes, interacting in ways that compound.

---

## Research Sources

See [RESEARCH.md](RESEARCH.md) for full synthesis. Key papers:

- **ATLAS** — 74.6% with 14B model via Generate→Verify→Repair
- **StateFlow** — 63.73% success with FSM, 5.8x cheaper
- **JetBrains observation masking** — 2.6% higher solve rates, 52% cheaper
- **AutoCodeBench** — C# outperforms Go by 5–15pp across all model sizes

---

*Built on the ashes of asgard-core. Less code, more working.*
