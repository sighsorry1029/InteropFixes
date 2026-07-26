param (
    [Parameter(Mandatory = $true)]
    [string] $manifestFile,

    [Parameter(Mandatory = $true)]
    [string] $versionString
)

try {
    $manifest = Get-Content -LiteralPath $manifestFile -Raw
    $updated = $manifest -replace '"version_number":\s*"([^"]*)"', "`"version_number`": `"$versionString`""
    $utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($manifestFile, $updated, $utf8WithoutBom)
} catch {
    Write-Error $_.Exception.Message
    exit 1
}
