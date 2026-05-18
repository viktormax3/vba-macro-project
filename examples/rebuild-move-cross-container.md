# Rebuild full-patch: move and cross-parent template add

Pass 36 extends `--stream-mode full-patch` with two structural operations:

- `move`: move a leaf object-stream control to root, `Frame`, or `Page` containers.
- `add` with a different `parent`: clone a template into a different root/Frame/Page storage.

The rebuilder removes the original site/object slice from the source storage, appends the cloned site/object payload to the target storage, updates `CountOfSites`, `CountOfBytes`, `SiteDepthsAndTypes`, and updates `ObjectStreamSize` using the target `f` stream metadata.

Move example:

```powershell
dotnet run --project src\FrxEdit.Cli\FrxEdit.Cli.csproj -- rebuild UserForm1.frm `
  --out out\UserForm1.move.frm `
  --mode strict `
  --stream-mode full-patch `
  --patch examples\rebuild-full-patch-move.patch.json `
  --report-out out\UserForm1.move.report.json
```

Cross-parent clone example:

```powershell
dotnet run --project src\FrxEdit.Cli\FrxEdit.Cli.csproj -- rebuild UserForm1.frm `
  --out out\UserForm1.add-cross-parent.frm `
  --mode strict `
  --stream-mode full-patch `
  --patch examples\rebuild-full-patch-add-cross-parent.patch.json `
  --report-out out\UserForm1.add-cross-parent.report.json
```

Limitations in this pass:

- Moving parent controls is intentionally rejected.
- Moving/adding into empty containers is intentionally rejected until FormSiteData can be created from only the container FormControl metadata.
- `MultiPage` itself is not a direct target; use a `Page` as target parent.
