#!/bin/bash
# Convenience runner for little_helper_core
# Usage: ./run.sh "your prompt here"
#        ./run.sh skills
#        ./run.sh models
#        ./run.sh models --init
#        ./run.sh --model qwen3:8b "fix the bug"

set -e
export PATH="$HOME/.dotnet:$PATH"
cd "$(dirname "$0")"
dotnet run --project src -- "$@"