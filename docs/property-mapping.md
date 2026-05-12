# FRX property mapping notes

These notes capture observed property-grid values from the current form and how they relate to bytes emitted by `inspect`.

## Confirmed

- `left` and `top` are stored in FRX units where `1 pt ~= 35.25` units.
- `leftPt`/`topPt` in `layout.json` match the VBA property grid closely:
  - `CheckBox3`: raw `left=2328`, `top=1693` -> `leftPt=66.04`, `topPt=48.03`; property grid shows `Left=66`, `Top=48`.
  - `Image1`: raw `left=176`, `top=1151` -> `leftPt=4.99`, `topPt=32.65`; property grid shows `Left=5`, `Top=32,65`.
  - `SpinButton4`: raw `left=1693`, `top=9489` -> `leftPt=48.03`, `topPt=269.19`; property grid shows `Left=48`, `Top=270`.
  - `CommandButton11`: raw `left=2963`, `top=6138` -> `leftPt=84.06`, `topPt=174.13`; property grid shows `Left=84`, `Top=174`.

## Not yet mapped

- VBA `Width`/`Height` are not the same as the nearby size-like bytes for all control types.
  - `CommandButton11` property grid shows `Width=72`, `Height=19`, while nearby bytes currently surface as `rawWidth=50`, `rawHeight=76`.
  - `SpinButton4` property grid shows `Width=12`, `Height=14`, while nearby bytes currently surface as `rawWidth=65`, `rawHeight=36`.
- Child-control `Caption` must not be inferred from neighboring strings; grouped controls can place other control names nearby.
- `CommandButton18` from the reference capture is not present in the current `UserForm1.frx` inspected in this workspace.

## Next targets

- Decode real size records by control type.
- Decode reliable captions for simple controls.
- Map common booleans/enums such as `Enabled`, `Visible`, `TabStop`, `BackStyle`, `PicturePosition`, and `Orientation`.

## Record/block diagnostics

Use `dump-records` to inspect how neighboring controls are written in the FRX:

```powershell
dotnet run --project src/FrxEdit.Cli -- dump-records UserForm1.frm --around TextBox3 --before 4 --after 6 --out records.textbox3.json
```

Current observations:

- Controls that look grouped in the form are usually adjacent records, not a separate group object.
- Around `TextBox3`, the FRX records are contiguous with small deltas:
  - `CommandButton10` -> `Label4` -> `TextBox3` -> `SpinButton3` -> `TextBox4` -> `SpinButton4` -> `Label5`.
  - Most deltas are 44-52 bytes, which looks like sequential control records.
  - The large gap before `CommandButton13`/`CommandButton10` appears to be a boundary before another block of compact records.
- `layout.json` now exposes `recordIndex`, `recordDelta`, and `recordBlock`. A new `recordBlock` starts when the gap between detected records is greater than 512 bytes.
- The `recordMarkerHex` ends with a control-kind marker in many records:
  - `1100` appears on command buttons.
  - `1500` appears on labels.
  - `1700` appears on text boxes/custom text-like controls.
  - `1000` appears on spin buttons.
  - `1A00` appears on check boxes.
  - `0C00` appears on images.
  - `0E00` appears on frames.
- The first two bytes in `recordMarkerHex` often behave like a per-container or tab/order index. Neighboring pseudo-groups share repeated small indexes, but this is not confirmed as a parent/group id yet.
- Frames (`Frame1`, `Frame2`) are records in the same stream. Child ownership is not yet represented by an explicit parent field in `layout.json`.
