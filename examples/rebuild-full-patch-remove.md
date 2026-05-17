# Rebuild full-patch remove test

Pass 34 introduces conservative structural removal for leaf controls.

```powershell
dotnet run --project src\FrxEdit.Cli\FrxEdit.Cli.csproj -- rebuild UserForm1.frm `
  --out out\UserForm1.full-patch-remove.frm `
  --mode strict `
  --stream-mode full-patch `
  --patch examples\rebuild-full-patch-remove.patch.json `
  --report-out out\UserForm1.full-patch-remove.report.json

dotnet run --project src\FrxEdit.Cli\FrxEdit.Cli.csproj -- inspect out\UserForm1.full-patch-remove.frm `
  --mode strict `
  --out out\UserForm1.full-patch-remove.inspect.json `
  --raw-out out\UserForm1.full-patch-remove.inspect.raw.json
```

Expected result: strict parser passes, and the control count decreases by one.

Limitations in this pass:

- Only leaf controls are removable.
- Removing parent controls with children is intentionally rejected.
- The VBA code module is not rewritten to delete event procedures; only the FRX/MSForms structure is rebuilt.
