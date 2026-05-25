---
mode: agent
description: Debug a FrxEdit form that passes parser validation but fails native VBA/Corel import.
---

Analyze the `.frm`, `.frx`, patch JSON, strict inspect JSON, raw inspect JSON, and rebuild report.

Checklist:

- Confirm the `.frm` `OleObjectBlob` points to the generated `.frx`.
- Run strict validation.
- Generate human and raw inspect output.
- Compare expected vs rebuilt report and require `semanticMatch: true` when applicable.
- Inspect container-specific structures for `Frame`, `MultiPage`, and `Page`.
- Check FormSiteData parent/depth/type consistency.
- Check object stream sizes and generated storage paths.
- For MultiPage/Page issues, inspect internal TabStrip and page `x` stream metadata.
- Do not treat parser success alone as native compatibility; native Office/Corel import remains authoritative.

Useful commands:

```powershell
dotnet run --project src/FrxEdit.Cli -- validate <form.frm> --mode strict
dotnet run --project src/FrxEdit.Cli -- inspect <form.frm> --mode strict --out out/debug.inspect.json --raw-out out/debug.inspect.raw.json
dotnet run --project src/FrxEdit.Cli -- dump-storage <form.frm> --out out/debug.storage.json
dotnet run --project src/FrxEdit.Cli -- dump-stream-records <form.frm> --out out/debug.stream-records.json
```
