#!/usr/bin/env bash
set -euo pipefail

# Usage: bash create_ollama_model.sh /path/to/llama-ps-game-recognizer/adapter
ADAPTER_DIR="${1:?Pass the adapter directory produced by training}"
test -f "$ADAPTER_DIR/adapter_config.json" || { echo "adapter_config.json not found" >&2; exit 1; }

cat > "$ADAPTER_DIR/Modelfile" <<'EOF'
FROM llama3.2-vision:11b-instruct-fp16
ADAPTER .
PARAMETER temperature 0
PARAMETER num_predict 32
SYSTEM """Identify the exact video game shown in a screenshot. Reply only with the game's real title. If the exact game cannot be identified confidently, reply Unknown."""
EOF

ollama pull llama3.2-vision:11b-instruct-fp16
(cd "$ADAPTER_DIR" && ollama create llama-ps-game-recognizer:latest -f Modelfile)
echo "Created Ollama model: llama-ps-game-recognizer:latest"
