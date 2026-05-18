$orig = Get-Content out\userformallcontrol.raw.json -Raw | ConvertFrom-Json
$rebuilt = Get-Content out\tabstrip-test.raw.json -Raw | ConvertFrom-Json

Write-Host "=== Root Entry/i17/o (TabStrip o stream) validation stats ==="
$origVal = $orig.ParserValidation.ObjectStreamValidations.PSObject.Properties | Where-Object { $_.Name -eq "Root Entry/i17/o" }
$rebuiltVal = $rebuilt.ParserValidation.ObjectStreamValidations.PSObject.Properties | Where-Object { $_.Name -eq "Root Entry/i17/o" }

if ($origVal) {
    Write-Host "Original length: $($origVal.Value.length)"
    Write-Host "Original validation: $($origVal.Value.validation)"
} else {
    Write-Host "Original Root Entry/i17/o not found in objectStreamValidations!"
}

if ($rebuiltVal) {
    Write-Host "Rebuilt length: $($rebuiltVal.Value.length)"
    Write-Host "Rebuilt validation: $($rebuiltVal.Value.validation)"
} else {
    Write-Host "Rebuilt Root Entry/i17/o not found in objectStreamValidations!"
}

Write-Host "`n=== All Object Stream Validation Sizes in Rebuilt ==="
$rebuilt.ParserValidation.ObjectStreamValidations.PSObject.Properties | ForEach-Object {
    Write-Host "$($_.Name) => Size: $($_.Value.length), Validation: $($_.Value.validation)"
}

Write-Host "`n=== MultiPage XStream Validations ==="
$orig.ParserValidation.MultiPageXStreamValidations.PSObject.Properties | ForEach-Object {
    Write-Host "Original: $($_.Name) | pageCount: $($_.Value.pageCount) | pageIds: $(($_.Value.pageIds) -join ', ') | validation: $($_.Value.validation)"
}
$rebuilt.ParserValidation.MultiPageXStreamValidations.PSObject.Properties | ForEach-Object {
    Write-Host "Rebuilt: $($_.Name) | pageCount: $($_.Value.pageCount) | pageIds: $(($_.Value.pageIds) -join ', ') | validation: $($_.Value.validation)"
}
