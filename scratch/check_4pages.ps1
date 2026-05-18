$original = Get-Content 'out\4pages.original.raw.json' | ConvertFrom-Json
Write-Host "Original controls:"
$original.controls.name

Write-Host "Rebuilt controls:"
$rebuilt = Get-Content 'out\4pages.rebuilt.raw.json' | ConvertFrom-Json
$rebuilt.controls.name
