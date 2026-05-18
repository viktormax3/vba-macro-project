# frxedit

Minimal CLI for inspecting and patching VBA/MSForms `UserForm` `.frm` + `.frx` pairs without opening the native VBA designer.

## Commands

```powershell
dotnet run --project src/FrxEdit.Cli -- inspect UserForm1.frm --out layout.json
dotnet run --project src/FrxEdit.Cli -- inspect UserForm1.frm --out layout.json --raw-out layout.raw.json
dotnet run --project src/FrxEdit.Cli -- apply UserForm1.frm sample.patch.json --out UserForm1.patched.frm
dotnet run --project src/FrxEdit.Cli -- validate UserForm1.patched.frm
dotnet run --project src/FrxEdit.Cli -- rebuild userformallcontrol.frm --out out/userformallcontrol.rebuilt.frm --mode strict --stream-mode full-patch --patch examples/rebuild-add-generated.patch.json --report-out out/userformallcontrol.rebuilt.report.json
dotnet run --project src/FrxEdit.Cli -- dump-records UserForm1.frm --around TextBox3 --before 4 --after 6 --out records.json
dotnet run --project src/FrxEdit.Cli -- dump-storage UserForm1.frm --out storage.json
```

The build output assembly is named `frxedit`; after publishing it can be used as `frxedit.exe`.

`inspect` emits a human-facing JSON by default: controls, bounds in property-grid points, raw units, and known natural properties.
Use `--raw-out` to also emit the full low-level inspection with offsets, stream names, markers, and parser diagnostics.
`inspect-frx.bat UserForm1.frx` writes both `UserForm1.inspect.json` and `UserForm1.inspect.raw.json`.
The raw inspect document emits controls in their binary/layout order (`nameOffset`), which makes controls that were authored near each other in the form easier to read together.
`recordIndex`, `recordDelta`, and `recordBlock` describe that binary order. `recordBlock` is inferred from large gaps between records; it is a write-block hint, not a confirmed MSForms group or parent.
If a sibling `UserForm1.scopes.json` file exists, `inspect` uses it to add each control's designer scope/owner (`UserForm1`, `Frame1`, `Frame2`, etc.).
Position fields are emitted twice: raw FRX units (`left`, `top`) and normalized VBA property-grid points (`leftPt`, `topPt`). Nearby size-like bytes are emitted as `rawWidth`/`rawHeight`; they are not the VBA property-grid `Width`/`Height` for every control type.
`dump-storage` lists the OLE compound streams inside the FRX and scans each stream for embedded resource signatures such as ICO and DIB. For ICO payloads it also reports the calculated icon length and, when present, the MSForms picture header that precedes the payload.

## Patch Format

```json
{
  "renames": {
    "CommandButton1": "BtnExtraer"
  },
  "layout": {
    "BtnExtraer": {
      "left": 120,
      "top": 180
    }
  },
  "properties": {
    "BtnExtraer": {
      "caption": "Extraer",
      "fontSize": 10,
      "backColor": "&H8000000F&"
    }
  }
}
```

`apply` writes copies: the original `.frm/.frx` are not modified. The output `.frm` points its `OleObjectBlob` at the sibling output `.frx`.
`apply` remains the conservative compatibility path for simple copies and in-place edits.

Use `rebuild --stream-mode full-patch` for structural editing. It rebuilds CFB streams and supports renames, layout changes, property changes, moving controls between compatible parents, removing leaf/container/page subtrees, adding from a template, and the first document-backed adds without a template.

Example generated add without `fromTemplate`:

```json
{
  "add": [
    {
      "type": "CommandButton",
      "name": "BtnGenerated",
      "parent": "Frame2",
      "leftPt": 12,
      "topPt": 64,
      "widthPt": 82,
      "heightPt": 22,
      "caption": "Generado"
    }
  ]
}
```

The first factory-backed types are `CommandButton`, `Label`, and `TextBox`. Other common controls can still be added through `fromTemplate` while their document-backed factories are implemented.
`parent` can target the root form, a `Frame`, or a `Page` for common controls. Direct common-control add to `MultiPage` is rejected; pages belong under `MultiPage`.

Object streams are parsed from `[MS-OFORMS]` layouts (`PropMask`, `DataBlock`, `ExtraDataBlock`, `TextProps`, and MorphData where applicable) instead of nearby-byte guessing.

## Current Limits

- The document-backed factory layer currently creates `CommandButton`, `Label`, and `TextBox` only.
- Creating a complete UserForm from zero is still pending; current rebuilds start from an existing `.frm/.frx` pair.
- Adding into an empty target `FormSiteData` stream is intentionally guarded until the site/class-table bootstrap path is implemented.
- `MultiPage` page add/reorder and full new `MultiPage` creation are still the next factory/storage milestone.
- Compatibility must still be confirmed by importing generated outputs in both Corel and Office/VBA; strict parser validation is necessary but not the final acceptance signal.
