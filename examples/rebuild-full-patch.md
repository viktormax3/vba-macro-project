# Rebuild full-patch example

This mode combines object stream variable serialization with simple FormSiteData scalar patching.

```powershell
dotnet run --project src\FrxEdit.Cli\FrxEdit.Cli.csproj -- rebuild UserForm1.frm `
  --out out\UserForm1.full-patch.frm `
  --mode strict `
  --stream-mode full-patch `
  --patch examples\rebuild-full-patch.patch.json `
  --report-out out\UserForm1.full-patch.report.json

 dotnet run --project src\FrxEdit.Cli\FrxEdit.Cli.csproj -- inspect out\UserForm1.full-patch.frm `
  --mode strict `
  --out out\UserForm1.full-patch.inspect.json `
  --raw-out out\UserForm1.full-patch.inspect.raw.json
```

Supported in this pass:

- object payload properties: caption, value, groupName, fontName, fontSize, backColor, foreColor, borderColor
- FormSiteData scalar properties: left/top via layout, tabIndex
- object payload size: width/height via layout

Still intentionally unsupported:

- renames
- tag/controlTipText variable rebuild
- add/remove controls
- moving controls between parents
