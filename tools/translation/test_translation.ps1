#Requires -Version 5.1
<#
.SYNOPSIS
    Sends a test translation request to the local OPUS-MT server and prints the response.

.EXAMPLE
    .\test_translation.ps1
    .\test_translation.ps1 -Text "A strange feeling of unease washes over you."
#>

param(
    [string]$Text = "More marks of the dragon's fury.",
    [string]$Url  = "http://127.0.0.1:8770/translate"
)

$body = @{
    text           = $Text
    sourceLanguage = "en"
    targetLanguage = "tr"
} | ConvertTo-Json -Compress

Write-Host ""
Write-Host "Sending to: $Url" -ForegroundColor Cyan
Write-Host "Text:       $Text" -ForegroundColor Cyan
Write-Host ""

try {
    $response = Invoke-RestMethod `
        -Uri $Url `
        -Method POST `
        -ContentType "application/json; charset=utf-8" `
        -Body ([System.Text.Encoding]::UTF8.GetBytes($body)) `
        -TimeoutSec 60

    if ($response.success) {
        Write-Host "Translation : $($response.translation)" -ForegroundColor Green
        Write-Host "Provider    : $($response.provider)"    -ForegroundColor Gray
        Write-Host "Duration    : $($response.durationMs) ms" -ForegroundColor Gray
    } else {
        Write-Host "Translation FAILED: $($response.error)" -ForegroundColor Red
    }
} catch {
    Write-Host "Request failed: $_" -ForegroundColor Red
    Write-Host ""
    Write-Host "Is the server running?" -ForegroundColor Yellow
    Write-Host "  .\tools\translation\start_translation_server.ps1" -ForegroundColor White
}
Write-Host ""
