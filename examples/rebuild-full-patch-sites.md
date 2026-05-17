# Full Patch Stage 2: FormSiteData string rebuild

This test exercises mixed object-stream and form-stream mutations:

- object payload variable strings (`caption`)
- object payload scalar fields (`fontSize`, `width`, `height`)
- FormSiteData scalar fields (`left`, `top`, `tabIndex`)
- FormSiteData counted strings (`name`, `controlTipText`)

```powershell
dotnet run --project src\FrxEdit.Cli\FrxEdit.Cli.csproj -- rebuild UserForm1.frm `
  --out out\UserForm1.full-patch-sites.frm `
  --mode strict `
  --stream-mode full-patch `
  --patch examples\rebuild-full-patch-sites.patch.json `
  --report-out out\UserForm1.full-patch-sites.report.json


dotnet run --project src\FrxEdit.Cli\FrxEdit.Cli.csproj -- inspect out\UserForm1.full-patch-sites.frm `
  --mode strict `
  --out out\UserForm1.full-patch-sites.inspect.json `
  --raw-out out\UserForm1.full-patch-sites.inspect.raw.json
```

Expected result: strict parser success and `semanticMatch: true`.
