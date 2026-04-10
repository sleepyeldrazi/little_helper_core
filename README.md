# little_helper_core

A lean agent harness for small/local/cloud models. General-purpose — coding tasks are a primary focus, but not the only one.

**Design principle:** Get out of the model's way. The model knows what to do — give it tools, stay silent, observe the result.

**Predecessor:** Internal Go agent (~33.5K LOC) — over-engineered, 5–25% success rate. This is the replacement.

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

## Source Files

```
src/                                    — Library (core agent engine)
  Agent.cs              — FSM loop: Planning→Executing→Observing→Done, stall detection
  AgentControl.cs       — Pause/resume, message injection, tool interception
  AnthropicClient.cs    — Anthropic Messages API client (claude.ai / direct)
  AnthropicStreaming.cs — Anthropic SSE streaming parser
  CodeCompressor.cs     — Code-aware compaction: preserve signatures, strip bodies
  Compaction.cs         — Context window management, observation masking
  ConfigResolver.cs     — CLI args + config file → resolved endpoint/model/key
  IAgentObserver.cs     — Observer interface for TUI integration
  IModelClient.cs       — Model client abstraction (OpenAI + Anthropic)
  JsonRepair.cs         — Extract/fix JSON from model output (code fences, prefixes, etc.)
  MessageSerializer.cs  — ChatMessage → OpenAI API JSON (includes reasoning_content)
  ModelClient.cs        — OpenAI-compatible HTTP client, JSON repair, fuzzy tool matching
  ModelConfig.cs        — Multi-provider config from ~/.little_helper/models.json
  ModelStreaming.cs     — OpenAI SSE streaming parser
  PromptBuilder.cs      — System prompt construction, skill injection, project instructions
  SessionLogger.cs      — JSONL session logs to ~/.little_helper/logs/
  ShellExecutor.cs      — Safe shell command execution via bash -c
  Skills.cs             — SkillDiscovery: SKILL.md parsing, XML formatting for prompt
  ToolSchemas.cs        — JSON schema definitions, normalization (additionalProperties: false)
  Tools.cs              — 4 tools + bash alias: read, run, write, search — with arg validation
  Types.cs              — Records: ChatMessage, ModelResponse, AgentResult, AgentConfig

cli/                                    — CLI entry point (publishes as 'little_helper')
  Program.cs            — Argument parsing, agent orchestration, output formatting
```
---

## Multi-Provider Support

Models are configured in `~/.little_helper/models.json`:

```json
{
  "default_model": "qwen3:14b",
  "providers": {
    "ollama": {
      "base_url": "http://localhost:11434/v1",
      "models": [{ "id": "qwen3:14b", "context_window": 32768 }]
    },
    "openrouter-extra": {
      "base_url": "https://openrouter.ai/api/v1",
      "api_key": "sk-or-...",
      "headers": { "HTTP-Referer": "..." },
      "models": [{ "id": "openai/gpt-oss-120b", "context_window": 128000 }]
    }
  }
}
```

Usage: `little_helper_core -m qwen3:14b "prompt"` or `little_helper_core -m openrouter-extra/openai/gpt-oss-120b "prompt"`

---

## Thinking/Reasoning Capture

Thinking models (Kimi K2.5, DeepSeek, Ollama thinking models) are fully supported:

- `message.reasoning_content` (Kimi K2.5, DeepSeek) — captured and roundtripped in conversation history
- `message.thinking` (Ollama) — captured
- `usage.completion_tokens_details.reasoning_tokens` (OpenAI o1/o3) — captured
- Thinking tokens estimated from content length when API doesn't report them
- All thinking accumulated in `ModelClient.ThinkingLog` and `AgentResult.ThinkingLog`

**Important:** Kimi K2.5 requires `reasoning_content` on ALL assistant messages when thinking mode is active. The `MessageSerializer` handles this automatically.

---

## Session Logs

Every run writes a JSONL log to `~/.little_helper/logs/`:

```
20260410_001234_k2p5.jsonl
```

One JSON object per line:
- `session_start` — model, working directory, timestamp
- `step` — tokens, thinking content, tool call count, response preview
- `tool` — name, args summary, result preview, file path, duration_ms
- `session_end` — success/fail, total tokens, thinking tokens, files changed, duration

```bash
# Inspect a session
cat ~/.little_helper/logs/*.jsonl | jq 'select(.type=="tool") | {tool, args, duration_ms}'
```

---

## Anthropic API

Providers with `"api_type": "anthropic"` are fully supported. Add an Anthropic provider to your `models.json`:

```json
{
  "providers": {
    "anthropic": {
      "base_url": "https://api.anthropic.com",
      "api_key": "sk-ant-...",
      "api_type": "anthropic",
      "models": [
        { "id": "claude-sonnet-4-6", "context_window": 1000000 },
        { "id": "claude-haiku-4-5", "context_window": 200000 }
      ]
    }
  }
}
```

Usage: `little_helper -m anthropic/claude-sonnet-4-6 "prompt"`

Key differences handled by `AnthropicClient`:
- `POST /v1/messages` instead of `/v1/chat/completions`
- Content blocks (`text`, `thinking`, `tool_use`, `tool_result`) instead of flat messages
- `x-api-key` + `anthropic-version` headers instead of `Authorization: Bearer`
- Tool schemas use `input_schema` instead of `parameters` (no `function` wrapper)
- System prompt is a top-level `system` field, not a message
- SSE streaming uses typed events (`message_start`, `content_block_start`, `content_block_delta`, etc.)

---

## Design Rules (Non-Negotiable)

Each rule addresses a failure mode found in the predecessor or confirmed by agent research.

### 1. One System Message, Under 1000 Tokens

"Lost in the Middle" shows 30%+ degradation on buried content.

### 2. 5 Tools Maximum

`read`, `run`, `write`, `search`, `bash` (alias). Small models can't reliably select from more.

### 3. No File Listing Extraction

The model uses `write("path", content)`. We write it. Done.

### 4. Verification via Skills, Not Core Loop

The `verify` skill handles build/test. The core loop knows nothing about verification.

### 5. Stall = 5 Repeated Observations

Kill the loop after 5 identical tool outcomes.

### 6. No Truncation of Tool Output

Full file reads, full command output. Context compaction handles overflow.
Partial reads (with offset/limit) include explicit markers telling the model
how many lines remain and how to read them.

### 7. State Machine, Not Free-Form Loop

StateFlow research: FSM yields 63.73% success vs 40.3% for ReAct.

```
PLANNING → EXECUTING → OBSERVING → (loop or → DONE)
                              ↘ ERROR_RECOVERY → EXECUTING (max 2)
```

### 8. Files ≤ 300 Lines, Single Responsibility

If a file grows past 300 lines, split it.

---

## CLI Usage

```
little_helper_core [options] <prompt>
little_helper_core models [--init]
little_helper_core skills

Options:
  -m, --model <model>              Model name or provider/model
  -e, --endpoint <url>             Override endpoint URL
  -d, --dir <path>                 Working directory
  -c, --context <tokens>           Max context tokens
  -s, --max-steps <n>              Maximum agent steps [default: 30]
  -b, --block-destructive          Block destructive commands
  -t, --temperature <temp>         Sampling temperature

Examples:
  little_helper_core "Fix the null reference in UserService.cs"
  little_helper_core -m k2p5 "Explain the architecture"
  little_helper_core -m openrouter-extra/openai/gpt-oss-120b "Write tests"
  echo "prompt" | little_helper_core -m qwen3:8b
```

---

## TUI Integration

The TUI ([little_helper_tui](https://github.com/sleepyeldrazi/little_helper_tui)) references core as a git submodule and wraps it with adapter classes:

```
little_helper_tui/
  core/                  <- git submodule (read-only, bump on core release)
  src/
    Adapters/            <- TUI-specific wrappers around core types
      AgentRunner.cs     <- wraps Agent with events, pause/resume, streaming
      SessionManager.cs  <- loads JSONL logs, resume/branch conversations
      ModelPool.cs       <- multi-model switching, arena mode
    TUI.cs               <- terminal frontend
```

Adapters are TUI-owned. When an adapter needs something core doesn't expose (e.g. `Agent.ToolExecuted` event, or `Agent.Pause()`), that becomes a core feature request. Flow:

1. TUI adapter hits a wall
2. Add extension point to core, push
3. Bump submodule pointer in TUI
4. Adapter works

Core stays clean — no TUI dependencies.

---

## Build & Test

```bash
dotnet build                    # 0 warnings, 0 errors
dotnet test                     # 75 passed, 2 skipped (integration)

dotnet publish src -c Release -r linux-x64 \
  --self-contained true -p:PublishSingleFile=true
```

---

## Research Sources

See [RESEARCH.md](RESEARCH.md) for full synthesis. Key papers:

- **ATLAS** — 74.6% with 14B model via Generate→Verify→Repair
- **StateFlow** — 63.73% success with FSM, 5.8x cheaper
- **JetBrains observation masking** — 2.6% higher solve rates, 52% cheaper
- **AutoCodeBench** — C# outperforms Go by 5–15pp across all model sizes

---

## Batch Scripting

Instead of a separate `script` tool, batch scripting is encouraged via a system prompt instruction in `PromptBuilder`. The model is told:

> When you need multiple pieces of information or need to perform several operations,
> write a short Python or shell script and run it with the `run` tool. One script call
> is better than many separate tool calls — it saves round-trips and keeps context clean.

This avoids adding a 6th tool (violating Rule #2) while achieving the same context efficiency gains. The model already has `run` — it just needs a nudge to use it for batch operations.

---

## Dynamic Prompt Compression

The system prompt and tool descriptions adjust based on the model's context window:

| Context Window | Model Tier | Prompt | README | Tool Descriptions | Batch Hint |
|----------------|-----------|--------|--------|-------------------|------------|
| < 8K | Tiny | 3 principles | Skip | Abbreviated | Skip |
| 8K–16K | Small | 6 principles | Skip | Abbreviated | Skip |
| >= 16K | Standard | 6 principles | Full | Standard | Include |

Context window is auto-detected from the endpoint:
- **OpenAI-compat**: queries `GET /models` for `context_length` or `max_model_len`
- **Ollama**: queries `POST /api/show` for `{arch}.context_length`
- **Anthropic**: known model table (all Claude models = 200K)
- **Fallback**: uses config value from `models.json`

Auto-detection is silent — if the query fails, the config value is used without error.

---

## Tool-Call Validation

All tool calls are validated before execution. Catches malformed arguments from unreliable models:

- Missing required fields (`path` for read, `command` for run, etc.)
- Wrong argument types (string where number expected, etc.)
- Empty/whitespace arguments

Returns a clear error message to the model so it can retry with correct arguments.

---

## Project Instructions (AGENTS.md)

The prompt builder auto-detects project-level instruction files in the working directory:
`AGENTS.md`, `CLAUDE.md`, `.cursorrules` (first match wins).

Injected into context after the README, truncated to fit model tier:
- Tiny models: skipped
- Small models: first 1,500 chars
- Standard models: first 4,000 chars

---

## Code-Aware Compaction

When context exceeds 80% of the window, observation masking kicks in. For code files,
compaction preserves structure instead of replacing with generic placeholders:

```
Before: [Output of previous operation on Agent.cs — 245 lines]

After:  File: Agent.cs (245 lines)
        1|using System;
        2|using System.Text.Json;
        3|
        4|namespace LittleHelper {
        5|
        6|public class Agent {
        8|  public Agent(IModelClient model, ...)
        12|  public async Task<AgentResult> Run(...)
        18|  private AgentState PlanAction(...)
        25|  private async Task<ToolResult> ExecuteTools(...)
        32|  private string Observe(...)

        [Code compressed: showing signatures and declarations only.
         Use read tool to see full implementations.]
```

Keeps: imports, class/method/enum declarations, signatures, fields.
Strips: method bodies, blank lines, comments.
Capped at 60 preserved lines per file.
Non-code files use the original generic placeholder.

---

*Less code, more working.*
