# FRX property mapping notes

These notes capture observed property-grid values from the current form and how they relate to bytes emitted by `inspect`.

## Confirmed

- `left` and `top` are stored in FRX units where `1 pt ~= 35.25` units.
- `leftPt`/`topPt` in `layout.json` match the VBA property grid closely:
  - `CheckBox3`: raw `left=2328`, `top=1693` -> `leftPt=66.04`, `topPt=48.03`; property grid shows `Left=66`, `Top=48`.
  - `Image1`: raw `left=176`, `top=1151` -> `leftPt=4.99`, `topPt=32.65`; property grid shows `Left=5`, `Top=32,65`.
  - `SpinButton4`: raw `left=1693`, `top=9489` -> `leftPt=48.03`, `topPt=269.19`; property grid shows `Left=48`, `Top=270`.
  - `CommandButton11`: raw `left=2963`, `top=6138` -> `leftPt=84.06`, `topPt=174.13`; property grid shows `Left=84`, `Top=174`.
- Frame sizes are stored in a container property bag before the frame's first child record, not in the compact frame record itself.
  - `Frame1`: `rawWidth=5080`, `rawHeight=8043` -> `widthPt=144.11`, `heightPt=228.17`; property grid shows `Width=144`, `Height=228`.
  - `Frame2`: `rawWidth=5927`, `rawHeight=8043` -> `widthPt=168.14`, `heightPt=228.17`; property grid shows `Width=168`, `Height=228`.
  - `layout.json` marks these with `properties.sizeSource = "containerPropertyBag"`.
- The control marker is a 4-byte field shaped like `[tabIndex, 00, typeCode, 00]`, but it is not always the last 4 bytes before the name. Some records have an additional 4-byte tail between the marker and the name.
  - `CommandButton12`: marker `02001100`, `tabIndex=2`, `typeCode=0x11`, tail `03000000`.
  - `TextBox3`: marker `06001700`, `tabIndex=6`, `typeCode=0x17`, tail `01000000`.
  - `SpinButton4`: marker `08001000`, `tabIndex=8`, `typeCode=0x10`, tail `05000000`.
  - `CommandButton14`: marker `0E001100`, `tabIndex=14`, `typeCode=0x11`, tail `04000000`.
- Confirmed type codes:
  - `0x0C` Image
  - `0x0E` Frame
  - `0x10` SpinButton
  - `0x11` CommandButton
  - `0x15` Label
  - `0x17` TextBox
  - `0x1A` CheckBox
  - `0x1B` OptionButton

## Not yet mapped

- VBA `Width`/`Height` are not the same as the nearby size-like bytes for all control types.
  - `CommandButton11` property grid shows `Width=72`, `Height=19`, while nearby bytes currently surface as `rawWidth=50`, `rawHeight=76`.
  - `SpinButton4` property grid shows `Width=12`, `Height=14`, while nearby bytes currently surface as `rawWidth=65`, `rawHeight=36`.
- For controls with images, the old size-like `rawHeight` field is usually resource payload length plus a record overhead, not visual height.
  - `Image1`: `rawHeight=4334`, image `resourceLength=4286`, overhead `48`.
  - `CommandButton12`: `rawHeight=4358`, image `resourceLength=4286`, overhead `72`.
  - `CommandButton10`: `rawHeight=1230`, image `resourceLength=1150`, overhead `80`.
  - `CommandButton14` and `CommandButton15`: `rawHeight=1226`, image `resourceLength=1150`, overhead `76`.
- Child-control `Caption` must not be inferred from neighboring strings; grouped controls can place other control names nearby.
- `CommandButton18` from the reference capture is not present in the current `UserForm1.frx` inspected in this workspace.

## Next targets

- Decode real size records by control type.
- Decode reliable captions for simple controls.
- Map common booleans/enums such as `Enabled`, `Visible`, `TabStop`, `BackStyle`, `PicturePosition`, and `Orientation`.
- Link `Picture` resources back to the owning controls.
- Promote the current `recordTailHex` observation into a named field once its meaning is confirmed. It correlates with the local scope/resource cluster, but is not yet proven to be parent id.

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
- Designer ownership/scope is separate from `recordBlock`. The current form has three observed scopes: `UserForm1`, `Frame1`, and `Frame2`.
- A sibling `UserForm1.scopes.json` can provide this designer-scope mapping without hard-coding it into the FRX parser.

## Embedded image resources

Use `dump-storage` to inspect the OLE compound streams inside the FRX:

```powershell
dotnet run --project src/FrxEdit.Cli -- dump-storage UserForm1.frm --out storage.json
```

Current observations:

- Embedded icon data is not stored as separate `.ico` files. It lives inside MSForms `o` streams.
- The current `UserForm1.frx` has two large `o` streams with validated ICO/DIB signatures:
  - One `o` stream (`size=13646`) contains five ICO entries plus their DIB payloads.
  - One `o` stream (`size=9628`) contains two ICO entries plus their DIB payloads.
- `frxparser.vba` (Olaf Schmidt, 2017) is useful as a reference for FRX entry payloads: it distinguishes text/list/binding/image entries and detects image entries by a length header before the content.
- In this OLE-backed MSForms FRX, the seven current ICO payloads are preceded by a repeated MSForms picture header rather than the exact 8/24-byte image header path from that parser:
  - CLSID bytes: `0452E30B918FCE119DE300AA004BB851`.
  - The four bytes immediately before the ICO payload match the full icon length.
  - 32x32 icons report `resourceLength=4286`; 16x16 icons report `resourceLength=1150`.
- `dump-storage` now emits `resourceLength` and `msFormsPictureHeader` for these ICO hits, including the stream-local header offset and declared payload length.
- Several button records near the icon-bearing controls have `rawHeight` values such as `4358`, `1230`, and `1226`; these appear more likely to be resource-related lengths/offsets than true control height.
- The next missing piece is the reference field that maps a control's `Picture` property to one of those ICO entries.
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
