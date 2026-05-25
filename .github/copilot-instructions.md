# FrxEdit Agent Instructions

This repository builds `frxedit`, a .NET 8 CLI for inspecting, validating, creating, and rebuilding VBA/MSForms `.frm` + `.frx` UserForms without opening the native VBA designer.

## Ground Rules

- Treat `.frx` as generated binary. Do not edit binary bytes by hand.
- Prefer JSON patches and the CLI commands below.
- Keep outputs under `out/` or `scratch/`.
- Use `--mode strict` when validating generated forms.
- Use `--stream-mode full-patch` for structural changes: add, remove, move, rename, parent changes, pages, frames, multipages.
- Use `docs/supported-controls.md` as the public property contract.
- Use `docs/architecture.md` for parser/rebuilder internals.

## Commands

Build:

```powershell
dotnet build FrxEdit.sln
```

Inspect human + raw JSON:

```powershell
dotnet run --project src/FrxEdit.Cli -- inspect UserForm1.frm --mode strict --out out/UserForm1.inspect.json --raw-out out/UserForm1.inspect.raw.json
```

Inspect as editable patch:

```powershell
dotnet run --project src/FrxEdit.Cli -- inspect UserForm1.frm --as-patch --out out/UserForm1.patch.json
```

Rebuild with patch:

```powershell
dotnet run --project src/FrxEdit.Cli -- build UserForm1.frm examples/base-edit-supported.patch.json --out out/UserForm1.rebuilt.frm --mode strict --stream-mode full-patch --report-out out/UserForm1.rebuilt.report.json
```

Create a UserForm from zero:

```powershell
dotnet run --project src/FrxEdit.Cli -- create out/NewForm.frm --name NewForm --caption "NewForm" --widthPt 340 --heightPt 240 --patch examples/base-create-all-supported.patch.json
```

Validate:

```powershell
dotnet run --project src/FrxEdit.Cli -- validate out/NewForm.frm --mode strict
```

Watch:

```powershell
dotnet run --project src/FrxEdit.Cli -- watch UserForm1.frm examples/base-edit-supported.patch.json --out out/UserForm1.watch.frm --mode strict --stream-mode full-patch
```

## Patch Shape

Use the repository examples as source of truth:

- `examples/base-create-all-supported.patch.json`
- `examples/base-add-all-supported.patch.json`
- `examples/base-edit-supported.patch.json`
- `examples/commandbutton-full.patch.json`
- `examples/textbox-full.patch.json`
- `examples/combo-list-full.patch.json`
- `examples/check-option-full.patch.json`

For new controls, prefer point units:

```json
{
  "add": [
    {
      "type": "CommandButton",
      "name": "BtnOk",
      "parent": null,
      "leftPt": 12,
      "topPt": 12,
      "widthPt": 80,
      "heightPt": 24,
      "properties": {
        "caption": "OK"
      }
    }
  ]
}
```

## Verification Pattern

After code or schema changes, run:

```powershell
dotnet build FrxEdit.sln
dotnet run --project src/FrxEdit.Cli -- validate out/NewForm.frm --mode strict
dotnet run --project src/FrxEdit.Cli -- inspect out/NewForm.frm --mode strict --out out/NewForm.inspect.json --raw-out out/NewForm.inspect.raw.json
```

Generated forms must import cleanly in Office/VBA and Corel when possible. Native designer import is the final compatibility check.

## Reusable Prompts

- `.github/prompts/frxedit-create-form.prompt.md`: generate a focused create patch.
- `.github/prompts/frxedit-clean-room-composite.prompt.md`: clean-room acceptance test for creating a complex composed form from zero.
- `.github/prompts/frxedit-debug-import.prompt.md`: debug forms that pass strict parser validation but fail native Office/Corel import.
