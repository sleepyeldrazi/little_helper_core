---
name: script
description: Combine multiple operations into one script call instead of many separate tool calls. Load this skill for complex multi-step data processing or when you need to chain several commands together.
---

# Script Skill

When you need multiple pieces of information or need to perform several operations,
write a short Python or shell script and run it with the bash tool.

One script call is better than many separate tool calls — it saves round-trips and
keeps context clean.

## When to Use

- Combining grep + wc + sort into one pipeline
- Processing data through multiple transformation steps
- Running a sequence of dependent commands where each needs the previous output
- Installing dependencies and running a verification in one shot

## When NOT to Use

- Simple single commands (just use bash directly)
- When you need to read the output of one step before deciding the next
- Debugging — keep steps separate so you can see what failed

## Examples

Instead of 3 separate search calls:
```bash
bash: grep -r "TODO" src/ | wc -l && grep -r "FIXME" src/ | wc -l && grep -r "HACK" src/ | wc -l
```

Do one script:
```bash
bash: python3 -c "
import subprocess
for pattern in ['TODO', 'FIXME', 'HACK']:
    result = subprocess.run(['grep', '-r', pattern, 'src/'], capture_output=True, text=True)
    count = len(result.stdout.strip().split('\n')) if result.stdout.strip() else 0
    print(f'{pattern}: {count}')
"
```

Or use shell directly:
```bash
bash: for p in TODO FIXME HACK; do echo "$p: $(grep -rc "$p" src/ | awk -F: '{s+=$2} END {print s}')"; done
```
