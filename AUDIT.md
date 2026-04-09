# Little Helper — Phase 0-7 Audit

**Date:** April 9, 2026 | **Build:** 0 warnings, 0 errors | **LOC:** 2,003 across 12 source files

---

## Phase-by-Phase Status

| Phase | File(s) | Lines | Status | Issues |
|-------|---------|-------|--------|--------|
| 0: Scaffold | csproj, .gitignore, .sln | — | PASS | 0 |
| 1: Types | Types.cs | 117 | PASS | 0 |
| 2: ModelClient | ModelClient.cs + JsonRepair.cs + MessageSerializer.cs | 238 + 86 + 50 | PASS | 0 (all fixed) |
| 3: Tools | Tools.cs + ShellExecutor.cs + ToolSchemas.cs | 205 + 112 + 144 | PASS | 0 (all fixed) |
| 4: Agent Core | Agent.cs + PromptBuilder.cs | 223 + 137 | PASS | 0 (all fixed) |
| 5: Skills | Skills.cs | 144 | PASS | 0 |
| 6: Compaction | Compaction.cs | 288 | PASS | 0 (all fixed) |
| 7: CLI | Program.cs | 259 | PASS | 0 |

---

## Files ≤ 300 Lines (Rule #8)

All source files are now under 300 lines:

| File | Lines |
|------|-------|
| MessageSerializer.cs | 50 |
| JsonRepair.cs | 86 |
| ShellExecutor.cs | 112 |
| Types.cs | 117 |
| PromptBuilder.cs | 137 |
| Skills.cs | 144 |
| ToolSchemas.cs | 144 |
| Tools.cs | 205 |
| Agent.cs | 223 |
| ModelClient.cs | 238 |
| Program.cs | 259 |
| Compaction.cs | 288 |
| **Total** | **2,003** |

---

## Critical Issues — All Fixed

### C1. FIX: Error recovery now resets on success
**Was:** `errorRecoveryCount` incremented on every tool error and never reset. After `MaxRetries` (default 2) total errors across the entire session, all subsequent errors forced `Done`.

**Fix:** Reset `errorRecoveryCount = 0` after a successful observation (no errors). Also added explicit force-Done with log message when max retries are exhausted during an error, instead of silently falling through.

### C2. FIX: Files split to comply with Rule #8
**Was:** Tools.cs (454 lines) and ModelClient.cs (380 lines) exceeded the 300-line rule.

**Fix:** Extracted three new files:
- `JsonRepair.cs` — JSON repair and Levenshtein distance (86 lines)
- `MessageSerializer.cs` — ChatMessage-to-API serialization (50 lines)
- `ShellExecutor.cs` — Shell execution helpers, stdin-pipe and bash -c modes (112 lines)
- `ToolSchemas.cs` — Tool schema definitions and recursive normalization (144 lines)

All source files now under 300 lines.

### C3. FIX: Recursive `additionalProperties: false` on tool schemas
**Was:** `NormalizeToolSchema()` only set `additionalProperties: false` at the top level. Nested property objects (like `{ "type": "string", "description": "..." }`) didn't receive it.

**Fix:** Rewrote `ToolSchemas.NormalizeObject()` to recursively walk all nested objects. Any nested object with `"type": "object"` gets `additionalProperties: false` applied. Top-level schemas always get it regardless.

### C4. FIX: Cancellation propagates immediately during model retries
**Was:** `SendRequest()` caught all exceptions including `OperationCanceledException`, returning `null` and triggering retries. With 3 retries * 5-min timeout = 15 minutes before Ctrl+C actually stops.

**Fix:** Added `catch (OperationCanceledException) { throw; }` as the first catch block in `SendRequest()`, before the generic `catch (Exception)`. Cancellation now propagates immediately.

---

## Moderate Issues — All Fixed

### M1. FIX: Retry hints appended to last user message, not as system messages
**Was:** `AppendRetryHint()` appended a `ChatMessage.System(...)` on each retry, violating Rule #1 (one system message under 1000 tokens).

**Fix:** New `AppendRetryHintToLastUserMessage()` method finds the last user message and appends the hint as `[hint text]`. This keeps the system prompt as a single message and places corrections where the model is most attentive (near the end, per "Lost in the Middle" research). If no user message exists, adds a new one.

### M2. FIX: Shell injection mitigated via stdin pipe in Run()
**Was:** `Run()` embedded the command in `bash -c "{command}"` with fragile double-quote escaping. A model-generated command containing `$()`, backticks, or escaped quotes could inject unintended operations.

**Fix:** `Run()` now uses `bash -s` (read commands from stdin). The command is written to `process.StandardInput` and the pipe is closed. This completely avoids string interpolation in shell arguments. `SearchAsync()` still uses `bash -c` for its internally-constructed grep/rg commands, but those are built from escaped arguments, not arbitrary model input.

### M3. FIX: Consistent logging to Console.Error
**Was:** `Compaction.cs` extension method logged to `Console.WriteLine` while all other code used `Console.Error.WriteLine`.

**Fix:** Changed to `Console.Error.WriteLine` in the `CompactIfNeeded` extension method.

---

## Low Priority Issues (Not Fixed, Documented)

### L1. Silent `{}` fallback for unparseable JSON arguments
When all JSON repair strategies fail, `RepairJson` returns `{}`. The tool will fail with "missing required parameter" which triggers error recovery. Acceptable behavior — the model gets a natural signal that something was wrong.

### L2. No binary file detection in `read` tool
`File.ReadAllLines()` on a binary file could throw or produce garbage. Minor — local models rarely request binary files.

### L3. Fragile YAML frontmatter parser
Simple `key: value` splitting in Skills.cs. Works for current SKILL.md format. Multi-line descriptions or values with colons would need a proper YAML parser.

### L4. `System.CommandLine` beta dependency
Pre-release package. Stable enough for MVP.

### L5. No HTTP server mode
Marked as optional in the plan. Not implemented for MVP.

### L6. Ripgrep detection spawns bash process
`_isRipgrepAvailable` uses `which rg` via Process.Start. Cached correctly, so only runs once.

### L7. `TaskType` enum not implemented
Correctly omitted — the FSM doesn't use task classification per the "one loop" design.

### L8. `OneOf` / `Result<T,TError>` not used
Current `ToolResult.IsError` boolean flag works fine for MVP.

---

## Design Rule Conformance

| Rule | Status | Notes |
|------|--------|-------|
| #1: System prompt < 1000 tokens | PASS | PromptBuilder generates concise prompts; retry hints appended to user message, not system |
| #2: 5 tools max | PASS | read, run, write, search, bash |
| #3: No listing extraction | PASS | `write` tool writes directly |
| #4: Verification via skills | PASS | No Verifying state, verify skill exists |
| #5: Stall = 5 repeated observations | PASS | Circular buffer, threshold = 5 |
| #6: No truncation of tool output | PASS | Read returns full content; only compaction truncates |
| #7: Explicit FSM | PASS | 5 states with defined transitions; error counter resets on success |
| #8: Files ≤ 300 lines | PASS | All 12 source files under 300 lines |

---

*Audit complete. All critical and moderate issues fixed. Phase 8 (Tests) is next.*