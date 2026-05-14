# frxedit

Minimal CLI for inspecting and patching VBA/MSForms `UserForm` `.frm` + `.frx` pairs without opening the native VBA designer.

## Commands

```powershell
dotnet run --project src/FrxEdit.Cli -- inspect UserForm1.frm --out layout.json
dotnet run --project src/FrxEdit.Cli -- inspect UserForm1.frm --out layout.json --raw-out layout.raw.json
dotnet run --project src/FrxEdit.Cli -- apply UserForm1.frm sample.patch.json --out UserForm1.patched.frm
dotnet run --project src/FrxEdit.Cli -- validate UserForm1.patched.frm
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

## Patch format

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
Property writes are conservative and in-place. This currently supports discovered writable offsets such as `caption`, `tag`, `controlTipText`, `fontName`, `fontSize`, `backColor`, `foreColor`, and `tabIndex`; strings must fit in the existing binary field until stream growth is implemented.
`CommandButton` object streams are parsed from the `[MS-OFORMS]` `CommandButtonControl` layout (`PropMask`, `DataBlock`, `ExtraDataBlock`, and `TextProps`) instead of nearby-byte guessing.

## Current v1 limits

- Renames must be the same length as the original control name or shorter. Shorter names are padded in-place inside the binary FRX.
- Adding controls is reserved in the JSON shape but intentionally rejected until cloning/templates are implemented.
- The FRX parser is conservative and focused on MSForms controls found in this project. It validates the OLE compound signature and patches discovered byte offsets directly.
- `width`/`height` patch fields are intentionally rejected until the real MSForms size records are mapped by control type. `rawWidth`/`rawHeight` are available only for low-level experiments.
- Captions/text for child controls are intentionally omitted for now. Earlier guesses from nearby bytes were noisy for grouped controls, so v1 only reports properties it can patch or identify with confidence. UserForm-level textual properties are read from the `.frm` header.
