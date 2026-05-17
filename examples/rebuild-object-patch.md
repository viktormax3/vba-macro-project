# Rebuild object-patch

`object-patch` applies variable-length object-payload property changes and rebuilds the `o` streams, updating `ObjectStreamSize` in the owning `f` streams.

Supported in this pass:

- `caption`
- `value`
- `groupName`
- `fontName`
- `fontSize`
- `backColor`
- `foreColor`
- `borderColor`

Not supported yet:

- `renames`, `tag`, `controlTipText`, `layout`, `add`, `remove`

Those require full `f` stream/FormSiteData rebuilding.

Example:

```powershell
dotnet run --project src/FrxEdit.Cli/FrxEdit.Cli.csproj -- rebuild UserForm1.frm --out out/UserForm1.object-patch.frm --mode strict --stream-mode object-patch --patch examples/rebuild-object-patch.patch.json --report-out out/UserForm1.object-patch.report.json

dotnet run --project src/FrxEdit.Cli/FrxEdit.Cli.csproj -- inspect out/UserForm1.object-patch.frm --mode strict --out out/UserForm1.object-patch.inspect.json --raw-out out/UserForm1.object-patch.inspect.raw.json
```
