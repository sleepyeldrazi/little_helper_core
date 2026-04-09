# Little Helper — C# Implementation Plan

## Overview

A lean agent harness for local models (7B–27B). General-purpose — not limited to coding, though coding tasks are a primary focus. The system is a single state-machine loop: **call model → execute tools → observe → done**. Target: ~3K LOC across 7 source files.

**Design principle:** Get out of the model's way. Give it tools, stay silent, observe the result.

---

## Architecture: One Loop

```
User Prompt → Build Context (file listing, README if present)
           → Model Call (OpenAI-compat API, 5 tools: read/run/write/search/bash)
           → Execute Tools
           → Observe (did files change? stall detection)
           → Loop until done or step limit
           → Result
```

State machine: `PLANNING → EXECUTING → OBSERVING → (loop or → DONE) ↘ ERROR_RECOVERY → EXECUTING (max 2)`

No lanes, no ownership resolution, no prompt file loading, no tool-blind mode, no delegation hierarchy. Call model, execute tools, done.

---

## Project Structure

```
little_helper/
├── little_helper.sln
├── src/
│   ├── little_helper.csproj
│   ├── Program.cs            (~200 lines) CLI + optional HTTP server
│   ├── Agent.cs              (~400 lines) Core FSM loop
│   ├── Tools.cs              (~400 lines) 5 tool implementations
│   ├── ModelClient.cs        (~300 lines) OpenAI-compatible HTTP client
│   ├── Skills.cs             (~100 lines) Skill discovery & prompt formatting
│   ├── Compaction.cs         (~200 lines) Context window management
│   └── Types.cs              (~200 lines) Records, enums, result types
├── skills/                   # Default bundled skills
│   └── verify/
│       └── SKILL.md          # Verification skill
├── tests/
│   ├── little_helper.Tests.csproj
│   ├── AgentTests.cs
│   ├── ToolsTests.cs
│   ├── ModelClientTests.cs
│   ├── SkillsTests.cs
│   └── CompactionTests.cs
└── README.md (existing)
```

**Dependencies (minimal):**
- `System.Text.Json` (built-in) — serialization
- `Microsoft.AspNetCore` — minimal API for HTTP mode (only if needed)
- `xunit` + `Moq` — testing
- `OneOf` (optional) — discriminated unions for result types

---

## Skills System

Skills are **prompt injection, not tools.** They are markdown files discovered at startup. The system prompt lists available skills by name, description, and file path. When the model decides a skill is relevant, it uses the `read` tool to load the SKILL.md into its context — hot and ready. No `invoke_skill` meta-tool needed.

This follows pi's implementation of the [Agent Skills standard](https://agentskills.io/specification) and matches how all four harnesses (opencode, hermes, forgecode, pi-mono) handle skills: progressive disclosure, on-demand loading.

### Skill Locations
```
~/.little_helper/skills/     # User-level skills
.little_helper/skills/        # Project-level skills
```

### Skill Discovery
At startup, scan skill directories. For each `SKILL.md` found, extract the `name` and `description` from frontmatter. Format into the system prompt:

```xml
<available_skills>
  <skill>
    <name>verify</name>
    <description>Run build/test commands after editing code.</description>
    <location>~/.little_helper/skills/verify/SKILL.md</location>
  </skill>
</available_skills>
```

The model reads the skill file with `read` when it wants to use it. Full skill content enters context as a tool result, not as part of the system prompt — so it doesn't count against the base system prompt token budget.

### Bundled Default Skills

The following skills ship with little_helper:

**`verify`** — Run build/test commands appropriate for the detected project type. The model reads this skill after completing code edits to determine what verification commands to run (`dotnet build`, `go test`, `npm test`, etc.). This replaces the old `Verifier.cs` — verification is now something the model *chooses to do* by reading a skill and running commands, not infrastructure baked into the core loop.

Users can add their own skills or override bundled ones by placing files in `~/.little_helper/skills/` or `.little_helper/skills/`.

### Why Skills, Not a Verifier Component

The research is clear: "Separate generator and evaluator roles. Self-evaluation is too lenient; an external evaluator is much stronger." But baking verification into the core FSM creates the exact failure mode that killed delta-code — the repair prompt told the model NOT to use tools, which was the #1 cause of "reads files but never writes" failures.

By making verification a skill:
- The model decides when verification is relevant (it asked to edit code → reads verify skill; it asked to install node → skips it)
- Verification instructions enter context only when needed (no wasted tokens on non-code tasks)
- Users can customize or replace verification without touching core code
- The core loop stays tiny: call model, execute tools, loop

---

## Phase 0: Project Scaffold

**Goal:** Empty project that builds and runs.

1. Create solution + console project targeting .NET 8
2. `little_helper.csproj` — AOT-ready if possible, dependencies declared
3. `Program.cs` — stub that prints version and exits
4. `.gitignore` — standard .NET ignores (`bin/`, `obj/`, `.vs/`)
5. Verify `dotnet build` and `dotnet run` work

**Deliverable:** `dotnet run` → "little_helper v0.1.0"

---

## Phase 1: Types & Foundation

**File: `Types.cs` (~200 lines)**

Define all shared types first so everything else compiles against them.

```csharp
// --- Enums ---
enum AgentState { Planning, Executing, Observing, ErrorRecovery, Done }
enum TaskType  { Edit, Create, Explore }

// --- Records ---
record ToolCall(string Name, JsonElement Arguments);
record ToolResult(string Output, bool IsError, string? FilePath = null);
record AgentResult(bool Success, string Output, List<string> FilesChanged);
record ModelResponse(string Content, List<ToolCall> ToolCalls, int TokensUsed);
record CompactionResult(List<ChatMessage> Messages, int TokensSaved);
record SkillDef(string Name, string Description, string FilePath);

// --- Chat message types ---
record ChatMessage(string Role, string? Content = null, List<ToolCall>? ToolCalls = null, string? ToolCallId = null, ToolResult? ToolResult = null);

// --- Configuration ---
record AgentConfig(
    string ModelEndpoint,      // e.g. "http://localhost:11434/v1"
    string ModelName,          // e.g. "qwen3:14b"
    int MaxContextTokens,      // e.g. 32768
    int MaxSteps,              // e.g. 30
    int MaxRetries,            // e.g. 2
    int StallThreshold,        // e.g. 5
    string WorkingDirectory
);
```

**Key decisions:**
- Use `record` types everywhere — immutable, value equality, no struct-copy bugs
- `JsonElement` for tool arguments — parsed lazily per-tool
- `Result<T, TError>` pattern via `OneOf` or a simple custom type for error handling without exceptions
- No `Verifying` state in the FSM — verification is a skill, not a core loop state

**Deliverable:** All types compile. Tests verify record equality and pattern matching.

---

## Phase 2: Model Client

**File: `ModelClient.cs` (~300 lines)**

OpenAI-compatible HTTP client. The only I/O boundary to the LLM.

### Responsibilities
1. Send chat completions with tool definitions
2. Parse responses — extract text content AND tool calls
3. **JSON repair layer** — extract valid JSON from markdown fences, fix common LLM mistakes
4. Retry on malformed responses (up to 3 retries with prompt refinement)
5. Track token usage

### Implementation Details

```
POST /v1/chat/completions
{
  "model": "qwen3:14b",
  "messages": [...],
  "tools": [...],
  "temperature": 0.3
}
```

**Tool schema normalization** (from research):
- Remove duplicate properties
- Add `additionalProperties: false` to all objects
- Convert nullable enums properly
- Keep descriptions minimal (token budget)

**JSON repair strategy:**
1. Try `JsonDocument.Parse` directly
2. If fails, extract from markdown code fences (```json ... ```)
3. If fails, try to find first `{` / `[` and parse from there
4. If all fail, retry with refined prompt: "Your response contained invalid JSON. Please respond with valid tool calls only."

**Fuzzy tool name matching:**
- Normalize to lowercase, strip underscores/hyphens
- Match against registered tool names with Levenshtein distance ≤ 2
- Log warnings for fuzzy matches

**Deliverable:** Can send prompts to Ollama/vLLM/any OpenAI-compat endpoint and get back `ModelResponse` with parsed tool calls.

---

## Phase 3: Tools

**File: `Tools.cs` (~400 lines)**

5 tools maximum. Each is a simple function: `string name → ToolResult`.

### Tool 1: `read`
```
Parameters: { path: string, offset?: int, limit?: int }
```
- Read file contents (honor offset/limit for large files)
- Return full content — **never truncate** (Rule #6)
- If file doesn't exist, return error with message

### Tool 2: `run`
```
Parameters: { command: string, timeout?: int }
```
- Execute shell command in working directory
- Capture stdout + stderr
- Default timeout 60s, configurable
- Return full output — no truncation

### Tool 3: `write`
```
Parameters: { path: string, content: string }
```
- Write content to file (create parent directories)
- No parsing, no validation, no listing extraction (Rule #3)
- Return success + bytes written

### Tool 4: `search`
```
Parameters: { pattern: string, file_type?: string }
```
- Run `grep -rn` or `ripgrep` under the hood
- Return matching lines with file paths
- Limit to 200 results to avoid flooding context

### Tool 5: `bash` (alias for `run`)
- Same as `run` — exists because some models prefer it

### Tool Registration
```csharp
// Generate OpenAI tool schemas from the tool definitions
// Each tool: name, description (under 50 tokens), JSON schema for parameters
Dictionary<string, Func<JsonElement, Task<ToolResult>>> Tools;
```

**Safety:** All tools execute relative to `AgentConfig.WorkingDirectory`. No path escape. `run`/`bash` has an allowlist or confirmation mode for destructive commands.

**Deliverable:** All 5 tools work. Tests mock the filesystem and shell.

---

## Phase 4: Agent Core (FSM Loop)

**File: `Agent.cs` (~400 lines)**

The heart of the system. Implements the state machine.

### State Transitions
```
Planning → Executing (always)
Executing → Observing (tool results ready)
Observing → Executing (if model wants to continue — has tool calls)
Observing → Done (if model says DONE / no tool calls / step limit)
Observing → ErrorRecovery (if tool execution fails)
ErrorRecovery → Executing (max 2 times, then force Done)
```

No `Verifying` state. The model handles verification by reading the verify skill and running commands — it's just more tool use in the same loop.

### Core Loop Pseudocode
```csharp
async Task<AgentResult> Run(string userPrompt, AgentConfig config)
{
    var state = AgentState.Planning;
    var messages = BuildInitialContext(userPrompt, config);
    int step = 0, errorRecoveryCount = 0;
    var lastObservations = new FixedSizeQueue<string>(capacity: config.StallThreshold);

    while (state != AgentState.Done && step < config.MaxSteps)
    {
        switch (state)
        {
            case Planning:
                // Build file listing, load skills list, add system message
                state = AgentState.Executing;
                break;

            case Executing:
                var response = await modelClient.Complete(messages, toolDefinitions);
                step++;
                messages.Add(response.ToAssistantMessage());

                if (response.ToolCalls.Count == 0)
                    state = AgentState.Done;  // Model is done talking
                else
                    state = AgentState.Observing;
                break;

            case Observing:
                foreach (var toolCall in response.ToolCalls)
                {
                    var result = await ExecuteTool(toolCall);
                    messages.Add(result.ToToolMessage(toolCall.Id));
                }

                // Stall detection: if last N observations are identical
                var observation = messages.LastContent();
                lastObservations.Push(observation);
                if (lastObservations.AllSame())
                    state = AgentState.Done;  // Stall → stop

                state = AgentState.Executing;  // Continue loop
                break;

            case ErrorRecovery:
                errorRecoveryCount++;
                if (errorRecoveryCount > config.MaxRetries)
                    state = AgentState.Done;
                else
                    state = AgentState.Executing;
                break;
        }
    }

    return new AgentResult(state == AgentState.Done, ...);
}
```

### System Prompt (Under 1000 tokens — Rule #1)
```
You are a helpful assistant. You have access to tools: read, run, write, search.
Use them to complete the task. When done, say DONE.

Working directory: {path}
{skills block}
```

Minimal. No examples, no rules, no formatting instructions. The model knows how to do things. Available skills are listed by name/description/path so the model can `read` them when relevant.

### Context Building
- Scan working directory for file listing (names + structure, not contents)
- Include README.md if present
- Keep initial context under 2000 tokens

**Deliverable:** The agent loop runs end-to-end with a real model.

---

## Phase 5: Skills

**File: `Skills.cs` (~100 lines)**

Skill discovery and formatting. Not execution — skills are just prompt injection.

### Responsibilities
1. Scan `~/.little_helper/skills/` and `.little_helper/skills/` for `SKILL.md` files
2. Parse frontmatter (name, description)
3. Format `<available_skills>` XML block for the system prompt
4. Resolve skill file paths (relative paths resolved against skill directory)

That's it. The model does the rest by using `read` to load skill content when it needs it.

**Deliverable:** Skills are discovered and listed in the system prompt. Model can read them.

---

## Phase 6: Context Compaction

**File: `Compaction.cs` (~200 lines)**

Manages the context window so it doesn't overflow.

### Strategy (from research): Observation Masking
- When total tokens approach `MaxContextTokens * 0.8`, start compacting
- Replace old tool outputs with placeholders: `[Output of read("src/Foo.cs") — 47 lines]`
- Keep all reasoning (assistant text content) intact
- Keep the system message and the last N turns fully intact
- Only compress middle turns

```csharp
CompactionResult Compact(List<ChatMessage> messages, int maxTokens)
{
    // 1. Count tokens (estimate: chars/4 for English, chars/3 for code)
    // 2. If under threshold, return as-is
    // 3. Walk from oldest observation toward newest
    // 4. Replace tool result content with summary placeholder
    // 5. Stop when under budget
    // 6. Never touch system message, user prompt, or last 3 turns
}
```

**Fallback:** If a single tool response exceeds 50% of context, truncate that ONE response (with header noting truncation). This is the only allowed truncation.

**Deliverable:** Messages stay within token budget across long agent sessions.

---

## Phase 7: CLI & HTTP Interface

**File: `Program.cs` (~200 lines)**

### CLI Mode (Primary)
```bash
# Run a task
little_helper --model qwen3:14b --endpoint http://localhost:11434/v1 \
  --dir ./my-project "Fix the null reference exception in UserService.cs"

# Run a general task
little_helper --dir / "Install Node.js 22 and verify it works"

# List available skills
little_helper skills
```

Using `System.CommandLine` or simple argument parsing.

### HTTP Mode (Optional — ASP.NET Minimal API)
```csharp
var app = WebApplication.Create(args);
app.MapPost("/task", async (TaskRequest req) => {
    var result = await agent.Run(req.Prompt, config with { WorkingDirectory = req.WorkingDir });
    return Results.Ok(result);
});
app.Run();
```

**Deliverable:** Fully functional CLI tool.

---

## Phase 8: Tests

**File: `tests/` (~800 lines)**

### Unit Tests
| Test File | What It Tests |
|-----------|--------------|
| `AgentTests.cs` | FSM state transitions, stall detection, error recovery, step limits |
| `ToolsTests.cs` | File read/write, shell execution, search, edge cases (missing files, timeouts) |
| `ModelClientTests.cs` | JSON repair, fuzzy tool matching, retry logic, token counting |
| `SkillsTests.cs` | Skill discovery, frontmatter parsing, prompt formatting |
| `CompactionTests.cs` | Token budget enforcement, observation masking, never-touch zones |

### Integration Test (one)
- Spin up a mock OpenAI server (return canned responses)
- Create a temp directory with a simple .csproj + broken code
- Run agent, verify it fixes the code and `dotnet build` passes

**Deliverable:** `dotnet test` passes green.

---

## Implementation Order & Milestones

| Phase | File(s) | Depends On | Est. Lines | Milestone |
|-------|---------|-----------|------------|-----------|
| **0** | csproj, Program.cs | — | ~30 | `dotnet run` works |
| **1** | Types.cs | — | ~200 | All types defined |
| **2** | ModelClient.cs | Types | ~300 | Can call Ollama and get parsed response |
| **3** | Tools.cs | Types | ~400 | All 5 tools work against filesystem |
| **4** | Agent.cs | Types, ModelClient, Tools | ~400 | Full FSM loop runs end-to-end |
| **5** | Skills.cs | Types | ~100 | Skills discovered and listed in prompt |
| **6** | Compaction.cs | Types | ~200 | Context stays in budget |
| **7** | Program.cs (full) | All above | ~200 | CLI works |
| **8** | tests/ | All above | ~800 | Full test suite green |
| | | | **~2630** | |

Each phase is independently testable — no "big bang" integration.

---

## Key Design Decisions Summary

| Decision | Rationale | Source |
|----------|-----------|--------|
| System prompt < 1000 tokens | Lost in the Middle: 30%+ degradation on buried content | Research Rule #1 |
| 5 tools max | Small models can't reliably select from 31 tools | Research Rule #2 |
| `write` tool writes directly | No parsing/model output extraction — delta-code's parser rejected 99% correct outputs | Research Rule #3 |
| No verification in core loop | Repair prompts caused "don't use tools" failures in delta-code; verification is now a skill | Research Rule #4 |
| Stall = 5 repeated observations | Models need 3-4 reads before editing; delta-code's threshold of 3 was too aggressive | Research Rule #5 |
| No truncation of tool output | Truncation hides the line the model needs to edit | Research Rule #6 |
| Explicit FSM with error states | 63.73% vs 40.3% for ReAct (StateFlow research) | Research Rule #7 |
| Files ≤ 300 lines | Agents can't read 993-line god files in one turn | Research Rule #8 |
| Skills = prompt injection | All four harnesses use progressive disclosure; skills are read on-demand, not tools | Research, pi-mono |
| "helpful assistant" not "coding assistant" | The tool is general-purpose — install node, edit configs, write code, anything | Design principle |

---

## What We're NOT Building

From the delta-code post-mortem — explicitly dropping:
- ~~Lane type hierarchy~~
- ~~Workspace backend abstraction~~
- ~~Prompt file loading/overriding~~
- ~~Tool-blind mode~~
- ~~Whole-file listing extraction~~
- ~~Delegation/watchdog/stall-detection agents~~
- ~~Planner's 8+ task classifications~~
- ~~`{identified_files}` placeholder resolution~~
- ~~Repair budget tracking~~
- ~~Error fingerprinting~~
- ~~Verification as core loop state~~ → replaced by skills

The entire deleted surface is replaced by: **one loop, one system message, five tools, skills for extension.**

---

## Future Features (Post-MVP)

These are planned features to implement after the core MVP is working and tested. They are documented here so the architecture doesn't preclude them, but they are NOT part of the initial build.

### 1. `--setup` Flag: Self-Bootstrapping Model Setup

A setup wizard that downloads and configures a local inference backend, then pulls a recommended model.

```
little_helper --setup
# → Detect OS, GPU, available RAM
# → Offer choice of backends: llama.cpp, ollama, vLLM, omlx
# → Install selected backend
# → Offer choice of models from a curated/tested list:
#    - Qwen3-14B (recommended for 16GB VRAM)
#    - Qwen3-8B (recommended for 8GB VRAM)
#    - etc.
# → Pull selected model
# → Verify model responds to a test prompt
# → Write config to ~/.little_helper/config.json
```

**Why:** The biggest barrier to local agent adoption is setup. Making little_helper self-bootstrapping means a user can go from zero to working agent in minutes. The curated model list ensures we only recommend models we've actually tested.

**Curated model list** will be maintained based on testing with the harness — models that score well on tool-calling reliability, JSON compliance, and instruction following at each size tier.

### 2. Sub-Agent Spawning (`spawn` tool)

A tool that spawns a new `little_helper` process with its own isolated context window, a minimal system prompt, and a specific task. Essential for local models with small context windows — the main agent can delegate subtasks without filling its own context.

```
spawn({
    task: "Refactor the auth module to use dependency injection",
    model?: "qwen3:14b",     // optional, defaults to same model
    cwd?: "./src/auth"        // optional, defaults to parent's cwd
})
```

**Sub-agent system prompt:**
```
You are a sub-agent. Execute the task and provide a clear, concise result from your work at the end.
```

**How it works:**
1. Main agent calls `spawn` tool
2. New `little_helper` process starts with `--mode json --no-session`
3. Sub-agent runs with minimal system prompt + task
4. Sub-agent's final text output is returned to the main agent as the tool result
5. Main agent continues with the sub-agent's result in context

**Design considerations:**
- Sub-agents get their own isolated context — no KV cache sharing with parent
- The sub-agent's output is just its final text response (not the full conversation)
- The main agent can spawn multiple sub-agents (parallel) or chain them sequentially
- Abort/cancellation propagates from parent to child via signals

**Research backing:** SOLVE-Med / MATA findings show "small specialized models, when orchestrated well, can outperform much larger standalone systems." For local setups, sub-agents let you decompose tasks so each piece fits in a small context window.

**Pi's implementation** (for reference): The `subagent` extension in pi spawns a separate `pi` process with `--mode json --no-session`, supports single/parallel/chain modes, and streams results back to the parent agent's tool result. We'll follow a similar pattern.

### 3. Automatic Verifier Sub-Agent (Research Topic)

**Status: Research needed. Not yet committed for implementation.**

When an agent finishes its task, before returning control to the user, an automatic verifier sub-agent could be spawned to audit the result. This is the Generate→Verify→Repair pattern from ATLAS research, implemented via sub-agent spawning.

**Proposed flow:**
```
1. Main agent completes task → says DONE
2. System automatically spawns verifier sub-agent with:
   - System prompt: "You are a verifier. You can accept or deny the agent's work."
   - Context: original task, list of changed files, agent's final response
   - Tools: read, run (so it can actually inspect files and run tests)
3. Verifier evaluates:
   → ACCEPT → user gets control back, agent's result presented
   → DENY  → verifier generates specific rejection:
     "X is broken because A. Y is missing because B. Z fails test C."
     This rejection is fed back to the main agent as a user message:
     "I found X, Y, Z problematic due to A, B, C. Re-evaluate and fix the issues."
4. Main agent loops (with a max retry budget, e.g. 2 verifier rejections)
5. If verifier still rejects after max retries, present both agent result and
   verifier objections to the user for manual review
```

**Why this is interesting:**
- **Research-backed:** "Separate generator and evaluator roles. Self-evaluation is too lenient; an external evaluator is much stronger." (Orchestration research). ATLAS achieves 74.6% on LiveCodeBench with Generate→Verify→Repair.
- **Isolated context:** The verifier runs in its own context window with a focused prompt. It doesn't carry the main agent's conversation baggage — it evaluates *results*, not *process*.
- **The verifier can actually audit:** It has `read` and `run` tools, so it can inspect changed files and run build/test commands. It's not rubber-stamping — it's auditing.
- **Repair is clean:** Rejection is a concise, specific message fed back to the main agent. Not a complex repair pipeline — just "here's what's wrong, fix it." One message.

**Design constraints (must be satisfied before implementation):**
- **Off by default.** This is opt-in via flag or config (`--verify-auto` or `auto_verify: true`). Many tasks don't benefit from verification (installing packages, reading files, answering questions).
- **Only after MVP.** Requires sub-agent spawning to be implemented first.
- **Max retry budget.** After N verifier rejections (e.g. 2), stop and present to user. Prevent infinite verify→repair loops.
- **Context isolation.** The verifier never sees the main agent's full conversation — only the original task, changed files, and the agent's final response. This prevents the verifier from being biased by the agent's reasoning process.
- **Cost awareness.** Each verification is a full model call. For local models this means time and compute. The feature should be smart about when to verify (e.g., only if files were actually written).

**Open research questions:**
- Does a verifier using the *same* model as the main agent provide meaningful evaluation, or do you need a larger/different model for this to work? (ATLAS used the same model but with different prompting.)
- What's the right balance of context to give the verifier? Too little and it can't evaluate; too much and it's just as expensive as the main agent.
- Should the verifier have access to git diff output, or just the final file states?
- How does this interact with the `verify` skill? If the main agent already read the verify skill and ran tests, is the verifier sub-agent redundant?

---

*Built on the ashes of asgard-core / delta-code. Less code, more working.*
