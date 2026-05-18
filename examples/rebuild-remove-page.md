# Rebuild full-patch: remove a single MultiPage Page

This example removes a single `Page` from an existing `MultiPage` while preserving the remaining pages and child controls.

```powershell
dotnet run --project src\FrxEdit.Cli\FrxEdit.Cli.csproj -- rebuild userformallcontrol.frm `
  --out out\userformallcontrol.remove-page.frm `
  --mode strict `
  --stream-mode full-patch `
  --patch examples\rebuild-full-patch-remove-page.patch.json `
  --report-out out\userformallcontrol.remove-page.report.json
```

The rebuilder updates:

- the `MultiPage` FormSiteData stream (`f`), removing the Page site;
- the Page storage subtree (`iXX`), removing its `f`/`o` streams and descendants;
- the MultiPage `x` stream, removing the corresponding `PageProperties` entry and `PageID`;
- `MultiPageProperties.PageCount`.
