# Rebuild object-normalize

This mode rebuilds the CFB container, reconstructs every `o` stream from parser object slices,
actively serializes known fields, and normalizes counted strings inside object payloads.

Unlike `object-serialize`, this mode may change individual object payload sizes. When that happens,
it patches the corresponding `ObjectStreamSize` value in the owning `f` stream.

```powershell
dotnet run --project src/FrxEdit.Cli/FrxEdit.Cli.csproj -- rebuild UserForm1.custom.frm `
  --out out/UserForm1.custom.object-normalize.frm `
  --mode strict `
  --stream-mode object-normalize `
  --report-out out/UserForm1.custom.object-normalize.report.json

 dotnet run --project src/FrxEdit.Cli/FrxEdit.Cli.csproj -- inspect out/UserForm1.custom.object-normalize.frm `
  --mode strict `
  --out out/UserForm1.custom.object-normalize.inspect.json `
  --raw-out out/UserForm1.custom.object-normalize.inspect.raw.json
```

Expected result:

```text
OK: rebuild stream mode object-normalize
OK: controls N -> N, semantic match: True
```
