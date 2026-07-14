[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$AdapterDir
)

$ErrorActionPreference = "Stop"
$adapter = (Resolve-Path -LiteralPath $AdapterDir).Path
if (-not (Test-Path -LiteralPath (Join-Path $adapter "adapter_config.json"))) {
    throw "adapter_config.json was not found in $adapter"
}

$modelfile = Join-Path $adapter "Modelfile"
@'
FROM llama3.2-vision:11b-instruct-fp16
ADAPTER .
PARAMETER temperature 0
PARAMETER num_predict 32
SYSTEM """Identify the exact video game shown in a screenshot. Reply only with the game's real title. If the exact game cannot be identified confidently, reply Unknown."""
'@ | Set-Content -LiteralPath $modelfile -Encoding utf8

ollama pull llama3.2-vision:11b-instruct-fp16
Push-Location $adapter
try {
    ollama create llama-ps-game-recognizer:latest -f Modelfile
}
finally {
    Pop-Location
}

Write-Host "Created Ollama model: llama-ps-game-recognizer:latest" -ForegroundColor Green
