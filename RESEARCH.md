# Coding Agent Harness Research: Conclusions

**Date:** April 9, 2026  
**Scope:** opencode, hermes, forgecode, pi-mono  
**Based on:** Repository feedback analysis + Research literature

---

## Executive Summary

This analysis synthesizes findings from four coding agent harnesses against current research on agent design, prompting, and orchestration. The goal is to identify what architectural patterns work well for local/smaller models (7B-27B parameters) and where the current approaches fall short.

**Key Finding:** The gap between frontier-optimized and local-suitable harnesses is substantial but bridgeable. Current harnesses prioritize capability over efficiency, leaving significant room for local-model-specific optimizations.

---

## What Works Well

### 1. Skills System Design

All four harnesses implement some form of skills/sub-agent system, and this pattern is consistently well-designed:

- **pi-mono**: XML-formatted skills with clear delimiters (`<available_skills>`, `<skill>`), on-demand loading prevents context bloat (`disableModelInvocation` flag)
- **Hermes**: Skills caching, progressive disclosure (Level 0: names only, Level 1: full content when needed via `skill_view()`)
- **ForgeCode**: Clean skill invocation pattern, dynamic loading via tool call
- **OpenCode**: Sub-agents use minimal TaskPrompt (~17 lines) instead of verbose CoderPrompt (~220 lines)

**Research Support:** This aligns with the "Principled Instructions" finding that structured, hierarchical information reduces cognitive load [Research-prompt.md §14]. The XML formatting specifically leverages the finding that "XML tags for complex prompts" reduce misinterpretation vs ambiguous delimiters [Research-prompt.md §12, §20].

**Why It Helps Local Models:** Specialized skills reduce the cognitive load on the main prompt. The model sees skill names/descriptions but only loads full content when explicitly invoked, keeping the working context minimal.

---

### 2. Model-Specific Prompting

Both Hermes and OpenCode implement model-aware prompting:

- **Hermes**: `TOOL_USE_ENFORCEMENT_GUIDANCE` applied conditionally based on model family (GPT, Gemini, Gemma, Grok) [hermes/REPO_FEEDBACK.md §5]
- **OpenCode**: Different prompt structures for Anthropic vs OpenAI endpoints [opencode/REPO_FEEDBACK.md §1.1]

**Research Support:** The research emphasizes that "performance swings of up to 76 accuracy points from single-character formatting differences" occur, and "format effects do not transfer across models" [Research-prompt.md §12]. Model-specific prompting is not optional—it's a reliability requirement.

**Why It Helps Local Models:** Local models often have different chat templates, instruction-following patterns, and tool-calling formats. One-size-fits-all prompts fail more often on smaller models.

---

### 3. Tool Schema Normalization

**ForgeCode** stands out with a sophisticated transformer pipeline:
- `normalize_tool_schema.rs`: Removes duplicate `description` and `title` from parameters
- `enforce_strict_schema.rs`: Adds `additionalProperties: false` for stricter JSON compliance
- `enforce_strict_tool_schema.rs`: Converts nullable enums to OpenAI-compatible format [forgecode/REPO_FEEDBACK.md §2]

**Research Support:** Schema-enforced constrained decoding "removes syntactic failures" compared to prompt-only structured output which has a "5–20% failure rate" [Research-prompt.md §17]. The finding that adding a `reasoning` field first in JSON schemas improves semantic quality applies here—simpler schemas leave more room for model reasoning.

**Why It Helps Local Models:** Simplified, strict schemas reduce parsing errors. Smaller models struggle with deeply nested or ambiguous schemas; normalization removes cognitive overhead.

---

### 4. Minimal System Prompts

**pi-mono** achieves the best results here with a deliberately minimal approach:
- System prompt: ~1000 tokens [pi/REPO_FEEDBACK.md §1]
- Clear, direct language without excessive constraints
- Task instruction at the end (document-first, query-last ordering)

**Research Support:** The "Lost in the Middle" research shows "30%+ degradation on content buried in the middle" of long contexts, and placement of documents first with query last yields "up to 30% quality improvement" [Research-prompt.md §11]. LLMLingua-2 achieves "20x compression with only ~1.5 accuracy point drop" when compressing context/documents rather than instructions [Research-prompt.md §19].

**Why It Helps Local Models:** Smaller context windows (4K-32K) mean every token counts. Minimal prompts preserve working memory for the actual task.

---

### 5. Context Compaction / Management

**pi-mono** implements sophisticated compaction:
- Structured summaries with sections: Goal, Constraints, Current State, File Operations, etc.
- File operation tracking for context awareness [pi/REPO_FEEDBACK.md §4.2]

**Research Support:** JetBrains Research found that "observation masking matched or outperformed LLM summarization in 4 of 5 configurations, at lower complexity" and achieved "2.6% higher solve rates while being 52% cheaper" with Qwen3-Coder [Research-orchestration.md §11]. The research recommends triggering compaction at 70–80% of context limit, preserving original task spec and recent N turns verbatim.

**Why It Helps Local Models:** Small context windows fill quickly. Well-designed compaction preserves essential state while removing noise.

---

### 6. Auto-Discovery of Local Endpoints

**OpenCode** and **Hermes** automatically discover local models:
- Query v1/models and api/v0/models endpoints
- Auto-configure context windows and defaults

**Why It Helps Local Models:** Reduces manual configuration burden, which is a significant barrier to local model adoption.

---

## What's Weak / Needs New Ideas

### 1. Token Overhead (Critical)

| Harness | Fixed Overhead | Impact on Local Models |
|---------|---------------|------------------------|
| **Hermes** | ~14K tokens (31 tools + system prompt) | Leaves only 20% of 4K context for actual work |
| **OpenCode** | CoderPrompt ~170 lines, bash tool ~3500 chars | Designed for frontier models; 27B+ threshold |
| **ForgeCode** | 58+ line prompts, 12+ rules | Exceeds comprehension capacity of <14B models |
| **pi-mono** | ~1000 tokens | ✅ Acceptable |

**The Problem:** Hermes explicitly acknowledges this as a "fundamental architectural constraint, not a bug" [hermes/REPO_FEEDBACK.md §1]. OpenCode's bash tool description alone is ~3500 characters with embedded git/PR workflows [opencode/REPO_FEEDBACK.md §2.2].

**Research Gap:** While the research emphasizes prompt compression (LLMLingua-2) and placement (Lost in the Middle), there's no systematic study on **dynamic prompt tiering** based on model capacity. The harnesses treat all models the same.

**New Idea Needed:** Dynamic prompt compression tiering:
- Detect or configure model size/tier
- Strip examples and reduce verbosity for smaller models
- Abbreviate tool descriptions (especially bash/edit)
- Create "essential tools only" mode for <14B models

This aligns with SOLVE-Med/MATA findings that "small specialized models, when orchestrated well, can outperform much larger standalone systems" [Research.md §12, Research-orchestration.md §5].

---

### 2. JSON Parsing / Tool Calling Reliability (Critical)

| Harness | Issue |
|---------|-------|
| **OpenCode** | No JSON repair layer; relies entirely on SDK/provider; "NO resilience for JSON repair/relaxation" [opencode/REPO_FEEDBACK.md §3.2] |
| **Hermes** | llama-server returns `dict` instead of JSON string → crashes on `.strip()` (Issue #1071) [hermes/REPO_FEEDBACK.md §2] |
| **ForgeCode** | XML tool wrapper `<forge_tool_call>` confuses local models (Qwen3.5 specifically) [forgecode/REPO_FEEDBACK.md §2] |
| **pi-mono** | ~1.6 retries per prompt for JSON compliance [pi/REPO_FEEDBACK.md §1] |

**Research Support:** The research notes that "schema-enforced constrained decoding removes syntactic failures" but this requires API-level support [Research-prompt.md §17]. For local models without this support, we're in a gap.

**New Idea Needed:** A JSON extraction/repair layer with:
- Extract JSON blocks from text output using regex/fenced code blocks
- Fuzzy tool name matching (Levenshtein distance) against registered tools
- Parameter type coercion (string→number, etc.)
- Retry loops with prompt refinement on parse failure
- Tool fallback: route to bash for simple file operations when structured calls fail

This aligns with StateFlow findings that "error handling as a named state is critical" and "removing the explicit Error state caused a 5% success rate decline" [Research-orchestration.md §12].

---

### 3. Monolithic Toolsets (High Priority)

**Hermes**: All 31 tools loaded eagerly; no granularity for resource constraints [hermes/REPO_FEEDBACK.md §9]  
**OpenCode**: 11 core tools all presented together; no subset selection [opencode/REPO_FEEDBACK.md §2.1]

**Research Support:** The research explicitly recommends routing "grep/read/run/simple classification to cheaper lanes" and reserving "expensive models for hard reasoning" [Research.md §12, Research-orchestration.md §5]. Difficulty-Aware Agentic Orchestration (DAAO) shows "a variational autoencoder estimating query difficulty + a cost/performance-aware router gives near-frontier quality at significantly lower cost" [Research-orchestration.md §6].

**New Idea Needed:** Tiered toolsets based on model capability:
- `minimal`: 5-8 essential tools (terminal, file, read, write, patch, search)
- `standard`: 15-20 tools for 14B+ models
- `full`: All tools for frontier models

This should be configurable, not hardcoded. The harness should detect or allow users to specify model tier and adjust tool availability accordingly.

---

### 4. Context Window Mismanagement (High Priority)

**OpenCode**: 4K default fallback is "too small for verbose prompts" [opencode/REPO_FEEDBACK.md §5.2]  
**Hermes**: Manual sync required between Ollama `num_ctx` and Hermes config; users report "context exceeded your setting" errors [hermes/REPO_FEEDBACK.md §4]

**Research Support:** The research recommends "set a hard token budget before each agent turn" and "trigger compaction when projected input exceeds 70–80% of the context limit" [Research-orchestration.md §11].

**New Idea Needed:** Automatic context negotiation:
- Query the model's actual context window from the endpoint
- Dynamically adjust tool availability and prompt size
- Warn users when context configuration mismatches are detected
- Set 32K default for local models (not 4K)

---

### 5. KV Cache / Session Instability (Medium Priority)

**OpenCode**: KV cache invalidated when sub-agent spawns (no reuse between parent/child) [opencode/REPO_FEEDBACK.md §4.2]  
**pi-mono**: Session hangs after extended use (Issue #2422); no health monitoring [pi/REPO_FEEDBACK.md §3]

**Research Support:** The StateFlow research emphasizes that "error handling as a named state is critical" [Research-orchestration.md §12]. The retry/fallback/circuit breaker pattern is recommended: "After 2 consecutive failures on the same action, force a planning reset" [Research-orchestration.md §15].

**New Idea Needed:** Session health monitoring with:
- Periodic heartbeat/ping to detect hung sessions
- Automatic recovery with state preservation
- Circuit breaker pattern for systematic degradation
- Graceful shutdown handlers

---

### 6. Multiple System Messages (ForgeCode Specific)

**ForgeCode** generates two separate system messages (`static_block` + `non_static_block`) which breaks Qwen3.5 and models with strict chat templates (Issue #2894) [forgecode/REPO_FEEDBACK.md §1].

**New Idea Needed:** Combine into single system message or make second message optional via config.

---

## Strong Signals from Research for Local/Smaller Models

Based on the research literature, here are high-confidence patterns that should be implemented in harnesses targeting local models:

### 1. Difficulty-Based Model Routing (Strong Signal)

**Source:** DAAO (Difficulty-Aware Agentic Orchestration) [Research-orchestration.md §6]

**Finding:** A variational autoencoder estimating query difficulty + a cost/performance-aware router gives near-frontier quality at significantly lower cost.

**Application:** Before dispatching to a model, classify task difficulty:
- Simple classification → 7B model
- Code generation → 14B model
- Complex synthesis → 27B+ model

This is the principled version of the "route to cheap specialists" heuristic.

---

### 2. Generate → Verify → Repair Pattern (Strong Signal)

**Source:** ATLAS [Research.md §12, Research-orchestration.md §6]

**Finding:** ATLAS achieves 74.6% on LiveCodeBench using a quantized 14B Qwen model on a single consumer GPU (16GB VRAM) via a three-phase pipeline:
1. **Generate**: PlanSearch extracts constraints, produces diverse candidates; Budget Forcing controls token spend
2. **Verify**: "Geometric Lens" scores candidates with energy field (87.8% selection accuracy) + sandboxed execution
3. **Repair**: Self-generated test cases + iterative refinement via PR-CoT

This doubles baseline pass rate (38% → 74.6%) entirely through infrastructure, not model scale.

**Application:** Implement external verification for coding tasks:
- Generate multiple candidate solutions
- Score candidates with a lightweight verifier (could be smaller model + tests)
- Repair failing candidates with targeted refinement

---

### 3. Observation Masking Over Summarization (Strong Signal)

**Source:** JetBrains Research [Research-orchestration.md §11]

**Finding:** Observation masking (replace old tool outputs with placeholders, keep reasoning chain) matched or outperformed LLM summarization in 4 of 5 configurations. With Qwen3-Coder 480B, masking achieved 2.6% *higher* solve rates while being 52% cheaper.

**Key Insight:** LLM summarization paradoxically caused agents to run ~15% longer trajectories because summaries gave false confidence to keep going.

**Application:** Default to observation masking for context compaction. Only use LLM summarization as a fallback for single oversized responses.

---

### 4. Small Specialists for Mechanical Subproblems (Strong Signal)

**Source:** SOLVE-Med / MATA [Research.md §12, Research-orchestration.md §5]

**Finding:** Small specialized models, when orchestrated well, can outperform or match much larger standalone systems.

**Application:** Route mechanical tasks to small models:
- grep/read/run → 4B model
- simple classification → 7B model
- code generation → 14B+ model
- synthesis/integration → 27B+ model

Reserve expensive models for hard reasoning or integration steps.

---

### 5. State Machine Modeling with Explicit Error States (Strong Signal)

**Source:** StateFlow [Research-orchestration.md §12]

**Finding:** Modeling tasks as finite state machines (FSM) with explicit states yielded 63.73% success on SQL tasks vs 40.3% for ReAct, at 5.8x lower cost. Removing the explicit Error state caused a 5% success rate decline.

**Recommended Minimum States:**
- `PLANNING`
- `EXECUTING`
- `OBSERVING`
- `ERROR_RECOVERY`
- `DONE`

**Application:** Model agent loops as explicit FSMs with named error states and deterministic transitions in code (not in LLM prompts).

---

### 6. Bounded Loops with Fixed Retry Budgets (Strong Signal)

**Source:** Portkey/Maxim on retries, fallbacks, circuit breakers [Research-orchestration.md §15]

**Finding:** Three patterns form the production resilience stack:
- **Retries**: Exponential backoff + jitter, max 3 attempts
- **Fallbacks**: Switch to alternate model/provider
- **Circuit breakers**: Remove endpoint from routing when failure rate exceeds threshold

**Application:**
- Define per-task max-retry budgets (not just per-call)
- After 2 consecutive tool failures on the same action, force planning reset
- Return structured error observations to agent, not crashes

---

### 7. XML Structure Over Free-Form Text (Strong Signal)

**Source:** Anthropic Prompting Best Practices, POSIX Prompt Sensitivity Index [Research-prompt.md §12, §20]

**Finding:** XML tags for structure reduce brittleness. "Prompt-only structured output has a 5–20% failure rate." Adding even one few-shot example dramatically reduces prompt sensitivity.

**Application:**
- Use XML tags for complex prompts: `<instructions>`, `<context>`, `<examples>`, `<input>`
- Use `<example>` tags with 3–5 diverse examples focused on output format
- Keep examples short; they're for format alignment, not reasoning

---

### 8. Episodic Memory with Structured Metadata (Strong Signal)

**Source:** A-MEM, Episodic Memory research [Research-orchestration.md §9-10]

**Finding:** A Zettelkasten-style memory network (structured notes with attributes, keywords, tags) doubled complex reasoning performance vs flat vector store baselines at lower token cost.

**Application:**
- Build two layers: short-term in-context working buffer + persistent episodic store
- Enrich every stored memory with metadata at write time (task context, success/failure, timestamps, tags)
- After task completion, abstract successful patterns from episode trace into reusable rules

---

## Summary Table: Harness Suitability for Local Models

| Harness | Token Overhead | Tool Reliability | Context Mgmt | Overall |
|---------|---------------|------------------|--------------|---------|
| **pi-mono** | ✅ ~1000 tokens | ⚠️ Needs retry layer | ✅ Sophisticated | **Best suited** |
| **Hermes** | ❌ ~14K tokens | ⚠️ Bug #1071 | ⚠️ Manual config | Needs tiering |
| **ForgeCode** | ⚠️ Complex prompts | ⚠️ XML issues | ⚠️ 4K default | Fix #2894 first |
| **OpenCode** | ❌ Verbose | ❌ No JSON repair | ⚠️ 4K default | Needs compression |

---

## Recommendations Priority

### Immediate (High Impact, Low Effort)
1. **Fix Hermes llama-server bug** (#1071): Type-check arguments before `.strip()`
2. **Fix ForgeCode multiple system messages** (#2894): Combine into single message
3. **Set 32K default context** for local models in all harnesses

### Short-term (High Impact, Medium Effort)
4. **Implement JSON extraction/repair layer** with fuzzy tool matching
5. **Create tiered toolsets**: minimal (5-8 tools), standard (15-20), full (all)
6. **Add session health monitoring** with heartbeat and circuit breakers

### Medium-term (High Impact, High Effort)
7. **Dynamic prompt compression** based on model tier
8. **Implement generate → verify → repair** pipeline for coding tasks
9. **Difficulty-based model routing** for multi-model deployments
10. **Observation masking** as default compaction strategy

---

## References

### Repository Feedback
- opencode/REPO_FEEDBACK.md
- hermes/REPO_FEEDBACK.md
- forgecode/REPO_FEEDBACK.md
- pi/REPO_FEEDBACK.md

### Research Literature
- Research.md: Core agent systems research (SOLVE-Med, MATA, ATLAS, SWE-agent, Agentless)
- Research-prompt.md: Prompt design and single-agent strategies (Lost in the Middle, POSIX, Principled Instructions, LLMLingua)
- Research-orchestration.md: Multi-agent design, memory, context management (StateFlow, A-MEM, JetBrains context research)

---

*Analysis conducted April 9, 2026. Strong conclusions backed by multiple verified reports and research citations; recommendations prioritized by impact/effort ratio.*
