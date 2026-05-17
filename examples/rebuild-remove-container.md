# Remove container examples

Remove a whole Frame subtree:

```powershell
dotnet run --project src\FrxEdit.Cli\FrxEdit.Cli.csproj -- rebuild UserForm1.frm `
  --out out\UserForm1.remove-frame.frm `
  --mode strict `
  --stream-mode full-patch `
  --patch examples\rebuild-full-patch-remove-frame.patch.json `
  --report-out out\UserForm1.remove-frame.report.json
```

Remove a whole MultiPage subtree:

```powershell
dotnet run --project src\FrxEdit.Cli\FrxEdit.Cli.csproj -- rebuild userformallcontrol.frm `
  --out out\userformallcontrol.remove-multipage.frm `
  --mode strict `
  --stream-mode full-patch `
  --patch examples\rebuild-full-patch-remove-multipage.patch.json `
  --report-out out\userformallcontrol.remove-multipage.report.json
```

This pass intentionally rejects removing a single Page from a MultiPage, because that requires rebuilding the MultiPage `x` stream PageProperties/PageIDs.
