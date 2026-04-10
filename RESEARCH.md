# Agent Harness Research

**Date:** April 9, 2026 | **Scope:** opencode, hermes, forgecode, pi-mono | **Target:** Local models 7B–27B

---

## What Works (Across All Four Harnesses)

### Skills / Progressive Disclosure
All four harnesses implement skills loaded on-demand, not eagerly. pi-mono uses XML delimiters with on-demand loading; Hermes caches and loads full content only when invoked. This aligns with "Principled Instructions" finding that structured, hierarchical information reduces cognitive load. For local models: keep working context minimal, load skill content only when explicitly invoked.

### Model-Specific Prompting
Hermes and OpenCode adjust prompts per model family. Research confirms "performance swings of up to 76 accuracy points from single-character formatting differences" and "format effects do not transfer across models." One-size-fits-all prompts fail more often on smaller models.

### Tool Schema Normalization
ForgeCode normalizes schemas (remove duplicates, add `additionalProperties: false`, convert nullable enums). Research: schema-enforced constrained decoding "removes syntactic failures" vs prompt-only's "5–20% failure rate." Simpler schemas leave more room for model reasoning.

### Minimal System Prompts
pi-mono's ~1000 tokens outperforms all others. "Lost in the Middle" shows 30%+ degradation on buried content. Document-first, query-last ordering yields "up to 30% quality improvement." For 4K–32K context windows, every token counts.

### Context Compaction via Observation Masking
JetBrains Research: observation masking (replace old tool outputs with placeholders, keep reasoning) matched or outperformed LLM summarization in 4/5 configurations, at 2.6% higher solve rates and 52% cheaper. Key insight: LLM summarization caused agents to run ~15% longer trajectories because summaries gave false confidence. **Default to observation masking. Only use LLM summarization as fallback for oversized single responses.**

---

## What's Broken (Critical for Local Models)

### Token Overhead
| Harness | Fixed Overhead | Impact |
|---------|---------------|--------|
| Hermes | ~14K tokens (31 tools) | Leaves 20% of 4K context for work |
| OpenCode | ~170 lines prompt + 3500-char bash tool | Frontier-only |
| ForgeCode | 58+ line prompts, 12+ rules | Exceeds <14B capacity |
| pi-mono | ~1000 tokens | ✅ Acceptable |

**Needed:** Dynamic prompt compression tiering — detect model size, strip examples/reduce verbosity for smaller models, abbreviate tool descriptions, create "essential tools only" mode for <14B.

### JSON Parsing / Tool Calling Reliability
- OpenCode: No JSON repair layer at all
- Hermes: llama-server returns dict instead of JSON string → crashes (Issue #1071)
- ForgeCode: XML `<forge_tool_call>` confuses Qwen3.5 (Issue #2894)
- pi-mono: ~1.6 retries per prompt for compliance

**Needed:** JSON extraction/repair layer — extract from text/fenced blocks, fuzzy tool name matching, parameter type coercion, retry with prompt refinement. StateFlow confirms "error handling as a named state is critical; removing it caused 5% success decline."

### Context Window Mismanagement
OpenCode falls back to 4K (too small). Hermes requires manual sync between Ollama `num_ctx` and config. **Needed:** Auto-query endpoint's context window, dynamically adjust prompt/tool size, default 32K for local models.

### Monolithic Toolsets
Hermes loads all 31 tools eagerly. Research recommends routing mechanical tasks to cheaper models. **Needed:** Tiered toolsets — minimal (5–8 tools for <14B), standard (15–20 for 14B+), full (all for frontier).

---

## Strong Research Signals (High Confidence)

### 1. Generate → Verify → Repair (ATLAS)
74.6% on LiveCodeBench with a quantized 14B Qwen on a single consumer GPU (16GB VRAM). Three-phase pipeline: PlanSearch generates diverse candidates → "Geometric Lens" scores + sandboxed execution → self-generated test cases + iterative refinement. Doubles baseline pass rate (38% → 74.6%) entirely through infrastructure, not model scale.

### 2. FSM Over ReAct (StateFlow)
63.73% success with explicit state machines vs 40.3% for ReAct, at 5.8x lower cost. Minimum states: `PLANNING`, `EXECUTING`, `OBSERVING`, `ERROR_RECOVERY`, `DONE`. Transitions are deterministic in code, not in LLM prompts.

### 3. Observation Masking Over Summarization (JetBrains)
Covered above. Default strategy for context compaction.

### 4. Small Specialists for Mechanical Tasks (SOLVE-Med / MATA)
Small specialized models, orchestrated well, can outperform larger standalone systems. Route by difficulty: grep/read → 4B, classification → 7B, code generation → 14B+, synthesis → 27B+.

### 5. Bounded Loops with Fixed Retry Budgets
Per-task max-retry budgets (not just per-call). After 2 consecutive tool failures on same action → force planning reset. Exponential backoff + jitter, circuit breaker when failure rate exceeds threshold.

### 6. XML Structure Over Free-Form Text
XML tags reduce brittleness. "Prompt-only structured output has 5–20% failure rate." Add 3–5 short examples focused on output format (not reasoning).

---

## Harness Suitability for Local Models

| Harness | Token Overhead | Tool Reliability | Context Mgmt | Verdict |
|---------|---------------|------------------|--------------|---------|
| **pi-mono** | ✅ ~1K tokens | ⚠️ Needs retry | ✅ Sophisticated | **Best suited** |
| **Hermes** | ❌ ~14K tokens | ⚠️ Bug #1071 | ⚠️ Manual | Needs tiering |
| **ForgeCode** | ⚠️ Complex | ⚠️ XML issues | ⚠️ 4K default | Fix #2894 first |
| **OpenCode** | ❌ Verbose | ❌ No JSON repair | ⚠️ 4K default | Needs compression |

---

## Recommendations Priority

**Immediate (high impact, low effort):**
1. Fix Hermes llama-server bug (#1071) — type-check before `.strip()`
2. Fix ForgeCode multiple system messages (#2894) — combine into single message
3. Set 32K default context for local models

**Short-term (high impact, medium effort):**
4. JSON extraction/repair layer with fuzzy tool matching
5. Tiered toolsets: minimal (5–8), standard (15–20), full (all)
6. Session health monitoring with heartbeat + circuit breakers

**Medium-term (high impact, high effort):**
7. Dynamic prompt compression based on model tier
8. Generate → verify → repair pipeline
9. Difficulty-based model routing
10. Observation masking as default compaction

---

## References

- **Harness feedback:** [opencode/REPO_FEEDBACK.md](https://github.com/sleepyeldrazi/coding-harness-feedback/blob/main/opencode/REPO_FEEDBACK.md), [hermes/REPO_FEEDBACK.md](https://github.com/sleepyeldrazi/coding-harness-feedback/blob/main/hermes/REPO_FEEDBACK.md), [forgecode/REPO_FEEDBACK.md](https://github.com/sleepyeldrazi/coding-harness-feedback/blob/main/forgecode/REPO_FEEDBACK.md), [pi/REPO_FEEDBACK.md](https://github.com/sleepyeldrazi/coding-harness-feedback/blob/main/pi/REPO_FEEDBACK.md)
- **Research:** [Research.md](https://github.com/sleepyeldrazi/coding-harness-feedback/blob/main/Research.md) (SOLVE-Med, MATA, ATLAS, SWE-agent, Agentless), [Research-prompt.md](https://github.com/sleepyeldrazi/coding-harness-feedback/blob/main/Research-prompt.md) (Lost in the Middle, POSIX, Principled Instructions, LLMLingua), [Research-orchestration.md](https://github.com/sleepyeldrazi/coding-harness-feedback/blob/main/Research-orchestration.md) (StateFlow, A-MEM, JetBrains context research)

*Analysis April 9, 2026. Conclusions backed by multiple verified reports and research citations.*
