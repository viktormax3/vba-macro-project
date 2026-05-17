# Rebuild full-patch add-from-template test

Pass 32 introduces a first conservative add path. It clones an existing control template inside the same parent/storage, then rebuilds the owner `f` stream and `o` stream.

```powershell
dotnet run --project src\FrxEdit.Cli\FrxEdit.Cli.csproj -- rebuild UserForm1.frm `
  --out out\UserForm1.full-patch-add.frm `
  --mode strict `
  --stream-mode full-patch `
  --patch examples\rebuild-full-patch-add-template.patch.json `
  --report-out out\UserForm1.full-patch-add.report.json

 dotnet run --project src\FrxEdit.Cli\FrxEdit.Cli.csproj -- inspect out\UserForm1.full-patch-add.frm `
  --mode strict `
  --out out\UserForm1.full-patch-add.inspect.json `
  --raw-out out\UserForm1.full-patch-add.inspect.raw.json
```

Expected result: strict parser passes, and the control count increases by one.

Limitations in this pass:

- `fromTemplate` is required.
- The new control must stay in the same parent/storage as the template.
- The control type is inherited from the template.
- This pass is intended for cloned controls, not arbitrary from-scratch control generation yet.
