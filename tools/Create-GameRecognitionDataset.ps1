param(
    [string]$DesktopPath = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory),
    [int]$ClassCount = 1000,
    [int]$MinimumImagesPerGame = 10
)

$ErrorActionPreference = 'Stop'

$csvPath = Join-Path $DesktopPath 'all_games_PlayStation.csv'
$imagesRoot = Join-Path $DesktopPath 'screenshots\screenshots\genres'
$datasetRoot = Join-Path $DesktopPath "PsGameTranslator-GameRecognition-$ClassCount"

if (-not (Test-Path -LiteralPath $csvPath)) {
    throw "CSV file was not found: $csvPath"
}

if (-not (Test-Path -LiteralPath $imagesRoot)) {
    throw "Screenshots folder was not found: $imagesRoot"
}

if (Test-Path -LiteralPath $datasetRoot) {
    throw "Target folder already exists and will not be overwritten: $datasetRoot"
}

function ConvertTo-SafeLabel {
    param([string]$Name)

    $value = $Name.ToLowerInvariant() -replace '[^a-z0-9]+', '_'
    $value = $value.Trim('_')

    if ([string]::IsNullOrWhiteSpace($value)) {
        return 'unnamed_game'
    }

    return $value
}

function ConvertTo-NumberOrZero {
    param([string]$Value)

    $result = 0.0
    if (-not [string]::IsNullOrWhiteSpace($Value) -and
        $Value -ne 'Missing' -and
        $Value -ne 'nan') {
        [void][double]::TryParse(
            $Value,
            [Globalization.NumberStyles]::Float,
            [Globalization.CultureInfo]::InvariantCulture,
            [ref]$result)
    }

    return $result
}

# The source repeats the same screenshot in every genre assigned to a game.
# Keep the first occurrence of each filename so no image is duplicated in a split.
$uniqueFiles = [System.Collections.Generic.Dictionary[string, string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)

Get-ChildItem -LiteralPath $imagesRoot -Recurse -File |
    Where-Object { $_.Extension -in '.jpg', '.jpeg', '.png' } |
    ForEach-Object {
        if ($_.Name -match '^gameid_(\d+)_') {
            if (-not $uniqueFiles.ContainsKey($_.Name)) {
                $uniqueFiles.Add($_.Name, $_.FullName)
            }
        }
    }

$imagesByGameId = [System.Collections.Generic.Dictionary[string, System.Collections.Generic.List[string]]]::new()

foreach ($entry in $uniqueFiles.GetEnumerator()) {
    if ($entry.Key -match '^gameid_(\d+)_') {
        $gameId = $matches[1]

        if (-not $imagesByGameId.ContainsKey($gameId)) {
            $imagesByGameId.Add($gameId, [System.Collections.Generic.List[string]]::new())
        }

        $imagesByGameId[$gameId].Add($entry.Value)
    }
}

$games = Import-Csv -LiteralPath $csvPath
$eligibleGames = foreach ($game in $games) {
    $gameId = [string]$game.id

    if ($imagesByGameId.ContainsKey($gameId) -and
        $imagesByGameId[$gameId].Count -ge $MinimumImagesPerGame) {
        [pscustomobject]@{
            GameId      = $gameId
            Name        = $game.name
            ReviewCount = ConvertTo-NumberOrZero $game.review_count
            PeoplePolled = ConvertTo-NumberOrZero $game.people_polled
            Rating      = ConvertTo-NumberOrZero $game.rating
            Genres      = $game.genres
            SourcePaths = @($imagesByGameId[$gameId] | Sort-Object)
        }
    }
}

$selectedGames = @(
    $eligibleGames |
        Sort-Object -Property @{ Expression = 'ReviewCount'; Descending = $true },
                              @{ Expression = 'PeoplePolled'; Descending = $true },
                              @{ Expression = 'Name'; Descending = $false } |
        Select-Object -First $ClassCount
)

if ($selectedGames.Count -lt $ClassCount) {
    throw "Only $($selectedGames.Count) games have at least $MinimumImagesPerGame unique screenshots."
}

New-Item -ItemType Directory -Path $datasetRoot | Out-Null
foreach ($split in 'train', 'validation', 'test', 'metadata') {
    New-Item -ItemType Directory -Path (Join-Path $datasetRoot $split) | Out-Null
}

$random = [System.Random]::new(20260713)
$manifest = [System.Collections.Generic.List[object]]::new()
$selectedGameRows = [System.Collections.Generic.List[object]]::new()

for ($classIndex = 0; $classIndex -lt $selectedGames.Count; $classIndex++) {
    $game = $selectedGames[$classIndex]
    $label = '{0:D4}_gameid_{1}_{2}' -f ($classIndex + 1), $game.GameId, (ConvertTo-SafeLabel $game.Name)
    $paths = [System.Collections.Generic.List[string]]::new()
    foreach ($path in $game.SourcePaths) {
        $paths.Add($path)
    }

    # Deterministic Fisher-Yates shuffle prevents source-file ordering from defining the split.
    for ($index = $paths.Count - 1; $index -gt 0; $index--) {
        $swapIndex = $random.Next($index + 1)
        $temporary = $paths[$index]
        $paths[$index] = $paths[$swapIndex]
        $paths[$swapIndex] = $temporary
    }

    for ($imageIndex = 0; $imageIndex -lt $paths.Count; $imageIndex++) {
        $split = if ($imageIndex -lt 8) { 'train' } elseif ($imageIndex -eq 8) { 'validation' } else { 'test' }
        $targetDirectory = Join-Path (Join-Path $datasetRoot $split) $label
        if (-not (Test-Path -LiteralPath $targetDirectory)) {
            New-Item -ItemType Directory -Path $targetDirectory | Out-Null
        }

        $extension = [IO.Path]::GetExtension($paths[$imageIndex]).ToLowerInvariant()
        $targetFileName = '{0:D3}{1}' -f ($imageIndex + 1), $extension
        $targetPath = Join-Path $targetDirectory $targetFileName
        Copy-Item -LiteralPath $paths[$imageIndex] -Destination $targetPath

        $manifest.Add([pscustomobject]@{
            split           = $split
            label           = $label
            class_index     = $classIndex
            game_id         = $game.GameId
            game_name       = $game.Name
            source_filename = [IO.Path]::GetFileName($paths[$imageIndex])
            file            = (Join-Path (Join-Path $split $label) $targetFileName)
        })
    }

    $selectedGameRows.Add([pscustomobject]@{
        class_index = $classIndex
        label = $label
        game_id = $game.GameId
        game_name = $game.Name
        screenshots = $paths.Count
        review_count = $game.ReviewCount
        people_polled = $game.PeoplePolled
        rating = $game.Rating
        genres = $game.Genres
    })
}

$manifest | Export-Csv -LiteralPath (Join-Path $datasetRoot 'metadata\manifest.csv') -NoTypeInformation -Encoding utf8
$selectedGameRows | Export-Csv -LiteralPath (Join-Path $datasetRoot 'metadata\selected_games.csv') -NoTypeInformation -Encoding utf8

$summary = [pscustomobject]@{
    dataset_name = "PsGameTranslator Game Recognition $ClassCount"
    source_csv = $csvPath
    source_images = $imagesRoot
    class_count = $selectedGames.Count
    image_count = $manifest.Count
    train_images = ($manifest | Where-Object split -eq 'train').Count
    validation_images = ($manifest | Where-Object split -eq 'validation').Count
    test_images = ($manifest | Where-Object split -eq 'test').Count
    selection = 'Top games ranked by IGDB review_count, then people_polled; at least 10 unique screenshots per game.'
    note = 'Cover images are not present in the downloaded source and are intentionally excluded from the recognition training splits.'
}

$summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $datasetRoot 'metadata\dataset_summary.json') -Encoding utf8

$readme = @"
# PsGameTranslator Game Recognition Dataset

This dataset contains $($selectedGames.Count) PlayStation games selected by IGDB review count.
Only unique gameplay screenshots are included; repeated screenshots from genre folders were removed.

- train: first 8 shuffled screenshots per game
- validation: ninth shuffled screenshot per game
- test: remaining screenshots per game

The folder name includes the stable class index, IGDB game ID, and game name.
Use metadata/manifest.csv to map every image to its label and source filename.
Cover art is deliberately excluded because the app will identify gameplay screenshots, not box art.
"@

Set-Content -LiteralPath (Join-Path $datasetRoot 'README.md') -Value $readme -Encoding utf8

Write-Host "Created: $datasetRoot"
Write-Host "Classes: $($selectedGames.Count)"
Write-Host "Images: $($manifest.Count)"
Write-Host "Train: $(($manifest | Where-Object split -eq 'train').Count)"
Write-Host "Validation: $(($manifest | Where-Object split -eq 'validation').Count)"
Write-Host "Test: $(($manifest | Where-Object split -eq 'test').Count)"
