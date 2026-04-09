---
name: verify
description: Run build/test commands appropriate for the detected project type after editing code.
---

# Verify Skill

After editing code, run the appropriate verification commands for the project type.

## Detection Rules

Check for these files in the working directory to determine the project type:

| File Present | Verify Command |
|-------------|---------------|
| `*.csproj`, `*.sln` | `dotnet build` then `dotnet test` (if tests exist) |
| `go.mod` | `go build ./...` then `go test ./...` |
| `package.json` | `npm test` (or `npm run build` if no test script) |
| `Cargo.toml` | `cargo build` then `cargo test` |
| `Makefile` | `make` then `make test` |
| `pom.xml` | `mvn compile` then `mvn test` |
| `requirements.txt`, `setup.py`, `pyproject.toml` | Run tests in the project's test directory |

## Procedure

1. Detect project type from files present
2. Run the build/compile command first
3. If build succeeds, run the test command
4. Report results: pass/fail with any error output
5. If tests fail, read the error output and fix the issues

## Notes

- Only run verification after you have made code changes
- Skip verification for non-code tasks (installing packages, reading files, answering questions)
- If multiple project types are detected, run verification for each
