# frxedit

Minimal CLI for inspecting and patching VBA/MSForms `UserForm` `.frm` + `.frx` pairs without opening the native VBA designer.

## Commands

```powershell
dotnet run --project src/FrxEdit.Cli -- inspect UserForm1.frm --out layout.json
dotnet run --project src/FrxEdit.Cli -- apply UserForm1.frm sample.patch.json --out UserForm1.patched.frm
dotnet run --project src/FrxEdit.Cli -- validate UserForm1.patched.frm
```

The build output assembly is named `frxedit`; after publishing it can be used as `frxedit.exe`.

`inspect` emits controls in their binary/layout order (`nameOffset`), which makes controls that were authored near each other in the form easier to read together.

## Patch format

```json
{
  "renames": {
    "CommandButton1": "BtnExtraer"
  },
  "layout": {
    "BtnExtraer": {
      "left": 120,
      "top": 180,
      "width": 900,
      "height": 300
    }
  }
}
```

`apply` writes copies: the original `.frm/.frx` are not modified. The output `.frm` points its `OleObjectBlob` at the sibling output `.frx`.

## Current v1 limits

- Renames must be the same length as the original control name or shorter. Shorter names are padded in-place inside the binary FRX.
- Adding controls is reserved in the JSON shape but intentionally rejected until cloning/templates are implemented.
- The FRX parser is conservative and focused on MSForms controls found in this project. It validates the OLE compound signature and patches discovered byte offsets directly.
- Captions/text are intentionally omitted for now. Earlier guesses from nearby bytes were noisy for grouped controls, so v1 only reports properties it can patch with confidence.
