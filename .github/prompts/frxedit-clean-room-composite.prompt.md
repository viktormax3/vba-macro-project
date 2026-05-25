---
mode: agent
description: Clean-room test: create a complex MSForms UserForm from zero using only FrxEdit docs, skill, and CLI.
---

You are starting from a clean conversation. Do not rely on prior chat context, hidden assumptions, or any existing `.frm`/`.frx` as a template.

Your task is to prove that FrxEdit can create a composed VBA/MSForms UserForm from zero using only:

- `docs/supported-controls.md`
- `.github/copilot-instructions.md`
- `ai-plugin/skills/frxedit-builder.md`
- the `frxedit` CLI in this repository
- JSON patches under `examples/` only as syntax references, not as base forms

## Goal

Create a new UserForm named `CleanRoomComposite` under `out/` with a useful, visibly composed layout.

The generated form must include:

- A root UserForm with a clear caption and non-default size.
- At least one `Frame` containing nested controls.
- At least one `MultiPage` with at least three `Page` children.
- At least two controls inside each `Page`.
- A `TabStrip` plus generated VBA panel-switching code if needed for visible behavior.
- At least one of each common control where currently supported:
  - `CommandButton`
  - `Label`
  - `TextBox`
  - `ComboBox`
  - `ListBox`
  - `CheckBox`
  - `OptionButton`
  - `ToggleButton`
  - `SpinButton`
  - `ScrollBar`
  - `Image` without binary picture payload
  - `Frame`
  - `MultiPage`
  - `Page`
  - `TabStrip`

Avoid `picture` and `mouseIcon` binary payloads for this test.

## Required Workflow

1. Read the relevant docs listed above.
2. Create a new JSON patch at:

   ```text
   out/clean-room-composite.patch.json
   ```

3. Generate the form from zero:

   ```powershell
   dotnet run --project src/FrxEdit.Cli -- create out/CleanRoomComposite.frm --name CleanRoomComposite --caption "Clean Room Composite" --widthPt 520 --heightPt 380 --patch out/clean-room-composite.patch.json
   ```

4. Validate strict:

   ```powershell
   dotnet run --project src/FrxEdit.Cli -- validate out/CleanRoomComposite.frm --mode strict
   ```

5. Inspect strict, human and raw:

   ```powershell
   dotnet run --project src/FrxEdit.Cli -- inspect out/CleanRoomComposite.frm --mode strict --out out/CleanRoomComposite.inspect.json --raw-out out/CleanRoomComposite.inspect.raw.json
   ```

6. If create, validate, or inspect fails, fix the patch or code and rerun until strict validation passes.

## Patch Design Rules

- Use `leftPt`, `topPt`, `widthPt`, `heightPt` for all visible controls.
- Use stable VBA-friendly names: `BtnSave`, `TxtName`, `FraOptions`, etc.
- `parent` rules:
  - `null` or omitted means root UserForm.
  - Common controls may be parented to root, `Frame`, or `Page`.
  - `Page` may only be parented to `MultiPage`.
  - Do not parent ordinary controls directly to `MultiPage`; parent them to a `Page`.
- Keep coordinates inside the parent bounds.
- Use properties documented in `docs/supported-controls.md`.
- Prefer readable enum intent in comments outside JSON, but JSON itself must remain valid JSON with no comments.
- Avoid unsupported or experimental fields unless the CLI validates them.

## Acceptance Criteria

Report the final result with:

- Paths written:
  - `out/clean-room-composite.patch.json`
  - `out/CleanRoomComposite.frm`
  - `out/CleanRoomComposite.frx`
  - `out/CleanRoomComposite.inspect.json`
  - `out/CleanRoomComposite.inspect.raw.json`
- Confirmation that `dotnet build FrxEdit.sln` passes.
- Confirmation that strict `validate` passes.
- A short inventory from the inspect output:
  - total controls
  - containers
  - pages
  - controls nested under each page
  - controls nested under each frame
- Any warnings that remain.

Do not claim Office/Corel native import success unless you actually performed that external import manually. Parser strict success is necessary, but native import remains a separate acceptance step.
