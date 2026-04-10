# Language Choice: C# (.NET)

**Context:** `little_helper` will be primarily maintained by AI agents. The language agents write most correctly should win.

## The Data: AutoCodeBench (Tencent Hunyuan, Aug 2025)

30+ models across 20 languages, 3,920 code generation problems. Average Pass@1 (Reasoning Mode):

| Model | Python | Java | JS | **C#** | Go | **Avg** |
|-------|--------|------|-----|--------|------|---------|
| Claude Opus 4 | 40.3 | 55.9 | 38.6 | **51.6** | 37.2 | 52.4 |
| o3-high | 40.8 | 53.2 | 40.8 | **49.5** | 22.0 | 51.1 |
| DeepSeek-R1 | 38.8 | 52.7 | 35.9 | **46.8** | 38.7 | 50.2 |
| Qwen3-32B | 37.8 | 39.9 | 32.6 | **39.4** | 36.1 | 41.7 |
| **Qwen3-14B** | 37.8 | 35.1 | 30.4 | **36.2** | 30.4 | 37.6 |
| Qwen3-8B | 28.1 | 21.8 | 28.3 | **27.1** | 29.3 | 28.5 |

(Shell omitted — problems are trivially simple `sed`/`awk`/`grep`. C++ omitted — not in scope.)

### Key Takeaways

1. **C# is the strongest practical language across almost all models.** For Qwen3-14B (our target), C# scores 36.2% — higher than Go (30.4%), JS (30.4%), and competitive with Python (37.8%).
2. **Go is weak for LLMs.** Even o3-high gets only 22.0% on Go vs 49.5% on C#.
3. **Python is not best for small models.** C# and Java often outperform it on complex multi-logical problems. Verbose, structured languages with clear patterns give LLMs reliable scaffolding.

---

## Why Not the Others

- **TypeScript:** JSON-native, good LLM training data, but structurally typed (unreliable refactoring), runtime type erasure, npm dependency hell, ad-hoc error handling.
- **Python:** Fastest prototyping, most training data, but no runtime type safety, chaotic dependency management, async bolted on, unreliable refactoring.
- **Go:** Already have the codebase, but worst LLM performance per the data, verbose `if err != nil` ceremony wastes agent tokens, no algebraic data types, weak generics — and the architecture already got over-complex in Go.
- **Rust:** Best type safety, but compile times kill iteration speed, borrow checker is hard for agents, overkill for a tool harness.

---

## Why C# Specifically

For a 3–5K LOC agent harness, these language features matter:

- **`record` types** — eliminate struct-copy bugs found in the predecessor's data types
- **Pattern matching with exhaustiveness** — prevent "unhandled lane type" bugs
- **`Result<T, TError>`** (via `OneOf` or custom) — eliminate `if err != nil` ceremony
- **`async`/`await`** — far superior to Go's goroutine-plus-channels for this use case
- **`dotnet test` with fixtures** — more composable than Go's table-driven tests
- **ASP.NET Minimal APIs** — HTTP server in ~20 lines
- **Source generators** — auto-generate serialization, reduce boilerplate

For the specific model we're targeting (Qwen3-14B class), C# scores 36.2% vs Go's 30.4% — a **19% relative improvement in code correctness**. For an agent-maintained project, that's the deciding factor.

### The "Enterprise" Myth

.NET 8+ is cross-platform (Linux-first), AOT-compilable (fast startup), has a great CLI, VS Code + C# Dev Kit is excellent, and is MIT-licensed. We'd use `dotnet new console`, `System.Text.Json` (built-in), `Microsoft.AspNetCore` (minimal API), `xunit`, and `OneOf`. No Visual Studio, no MVC, no solution file.

---

## Target Layout

```
little_helper/
├── src/
│   ├── Program.cs          # CLI + HTTP server
│   ├── Agent.cs             # Core loop: model → tool exec → observe → repeat
│   ├── Tools.cs             # read, run, write, search
│   ├── ModelClient.cs       # OpenAI-compatible HTTP client
│   ├── Skills.cs            # Skill discovery & prompt formatting
│   ├── Compaction.cs        # Context window management
│   └── Types.cs             # Records, enums, result types
├── skills/
│   └── verify/SKILL.md      # Bundled verification skill
├── tests/
│   ├── AgentTests.cs
│   ├── ToolsTests.cs
│   ├── SkillsTests.cs
│   └── CompactionTests.cs
└── little_helper.csproj
```

---

## References

- AutoCodeBench: https://arxiv.org/abs/2508.09101
- Agent harness research: [RESEARCH.md](RESEARCH.md)
- Harness feedback: [coding-harness-feedback](https://github.com/sleepyeldrazi/coding-harness-feedback)
