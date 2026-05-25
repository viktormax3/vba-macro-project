---
mode: agent
description: Generate a FrxEdit JSON patch for a new VBA/MSForms UserForm.
---

Use `docs/supported-controls.md` and the examples under `examples/` to generate a valid FrxEdit patch for a new UserForm.

Requirements:

- Output a JSON patch compatible with `frxedit create`.
- Use `leftPt`, `topPt`, `widthPt`, and `heightPt`.
- Put controls inside the correct parent: `null` for root, `Frame` for frame children, `Page` for multipage page children, `MultiPage` only for `Page`.
- Avoid unsupported binary image fields unless the user explicitly asks for image testing.
- Include clear names suitable for VBA event handlers.
- Validate mentally against `docs/supported-controls.md`.

Suggested validation command:

```powershell
dotnet run --project src/FrxEdit.Cli -- create out/Generated.frm --name Generated --caption "Generated" --widthPt 340 --heightPt 240 --patch <patch.json>
dotnet run --project src/FrxEdit.Cli -- validate out/Generated.frm --mode strict
```
