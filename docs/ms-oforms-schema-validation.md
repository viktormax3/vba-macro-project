# MS-OFORMS schema validation

This document is the current control-schema checklist for factory-backed `add` and future `create`.
The source of truth remains `[MS-OFORMS]`; this file records the compact contract validated against `userformallcontrol.frm/frx` strict inspect output.

## Fixture Baseline

Command used:

```powershell
dotnet run --no-build --project src/FrxEdit.Cli -- inspect userformallcontrol.frm --mode strict --out out/userformallcontrol.schema.inspect.json --raw-out out/userformallcontrol.schema.inspect.raw.json
```

Observed controls:

| Type | Parser | Fixture propMask | TextProps | Various bits | Object size | Required structure |
| --- | --- | --- | --- | --- | ---: | --- |
| CommandButton | `msOFormsCommandButton` | `0x00000028` | `0x00000075` |  | 64 | DataBlock, ExtraDataBlock, StreamData, TextProps |
| Label | `msOFormsLabel` | `0x00000028` | `0x00000035` |  | 56 | DataBlock, ExtraDataBlock, StreamData, TextProps |
| TextBox | `msOFormsMorphData` | `0x0000000080000101` | `0x00000035` | `0x2C80481B` | 52 | MorphData blocks, StreamData, TextProps |
| ComboBox | `msOFormsMorphData` | `0x0000000080050141` | `0x00000035` | `0x2C80481B` | 56 | MorphData blocks, StreamData, TextProps, optional ColumnInfo |
| ListBox | `msOFormsMorphData` | `0x0000000080010160` | `0x00000035` |  | 52 | MorphData blocks, StreamData, TextProps, optional ColumnInfo |
| CheckBox | `msOFormsMorphData` | `0x0000000080C00146` | `0x00000035` |  | 84 | MorphData blocks, StreamData, TextProps |
| OptionButton | `msOFormsMorphData` | `0x0000000080C00146` | `0x00000035` |  | 88 | MorphData blocks, StreamData, TextProps |
| ToggleButton | `msOFormsMorphData` | `0x0000000080C00146` | `0x00000075` |  | 88 | MorphData blocks, StreamData, TextProps |
| Image | `msOFormsImage` | `0x00000600` |  |  | 27077 | Image blocks, StreamData |
| ScrollBar | `msOFormsScrollBar` | `0x00002008` |  |  | 20 | ScrollBar blocks, StreamData |
| SpinButton | `msOFormsSpinButton` | `0x00000808` |  |  | 20 | SpinButton blocks, StreamData |
| TabStrip | `msOFormsTabStrip` | `0x00FA8031` | `0x00000035` |  | 172 | TabStrip blocks, StreamData, TextProps, TabFlagData |
| Frame | `msOFormsFormSiteData` |  |  |  |  | Storage-backed FormControl/FormSiteData |
| MultiPage | `msOFormsFormSiteData` |  |  |  |  | Storage-backed FormControl/FormSiteData, x stream, inner TabStrip |
| Page | `msOFormsFormSiteData` |  |  |  |  | PageProperties in MultiPage x stream plus page storage |

## Factory Readiness

Ready:

- `CommandButton`
- `Label`
- `TextBox`
- `ComboBox`
- `ListBox`
- `CheckBox`
- `OptionButton`
- `ToggleButton`
- `Image` without initial picture
- `ScrollBar`
- `SpinButton`
- `TabStrip`
- `Frame` empty storage-backed container
- `MultiPage` storage-backed container with generated `x` stream, internal TabStrip, and initial Page storages
- `Page` storage-backed container generated under an existing `MultiPage`

Pending leaf factories:

- none

Pending storage factories:

- none

## Structural Rules

- Every generated leaf control must emit `OleSiteConcrete` from `FormSiteFactory`.
- Every generated leaf control with text must emit `TextProps` through `TextPropsFactory`, not hand-written inline bytes.
- MorphData subtypes must be separate schemas even though they share `MorphDataControl`; their display style, text/value/caption fields, and bitfields differ.
- Do not write default-valued properties just to make the JSON look full. Defaults should be absent from the object payload unless the schema says the designer persists them by default.
- A factory is considered ready only when strict inspect of generated output reports `semanticMatch: true` and the generated control has no heuristic parser fallback.

## Validation Commands

```powershell
dotnet build FrxEdit.sln
dotnet run --no-build --project src/FrxEdit.Cli -- validate userformallcontrol.frm --mode strict
dotnet run --no-build --project src/FrxEdit.Cli -- rebuild userformallcontrol.frm --out out/userformallcontrol.add-generated.frm --mode strict --stream-mode full-patch --patch examples/rebuild-add-generated.patch.json --report-out out/userformallcontrol.add-generated.report.json
dotnet run --no-build --project src/FrxEdit.Cli -- inspect out/userformallcontrol.add-generated.frm --mode strict --out out/userformallcontrol.add-generated.inspect.json --raw-out out/userformallcontrol.add-generated.inspect.raw.json
dotnet run --no-build --project src/FrxEdit.Cli -- create out/CreatePatched.frm --name CreatePatched --caption "Desde cero" --widthPt 340 --heightPt 240 --patch examples/create-form.patch.json
```

Latest generated `MultiPage` acceptance:

- `validate out/userformallcontrol.add-generated.frm --mode strict`: 37 controls detected.
- `rebuild` report: `semanticMatch: true`.
- strict raw parser diagnostics: `legacyScannerCount: 0`, `warningCount: 0`, `errorCount: 0`.
- `MultiPageGenerated` has `multiPageXStreamValidation: exact` and pages `MpGenPage1`/`MpGenPage2` mapped to parent `MultiPageGenerated`.

Latest direct `Page` acceptance:

- `rebuild userformallcontrol.frm --patch examples/rebuild-add-page.patch.json`: `semanticMatch: true`.
- `validate out/userformallcontrol.add-page.frm --mode strict`: 22 controls detected.
- strict raw parser diagnostics after add: `legacyScannerCount: 0`, `warningCount: 0`, `errorCount: 0`.
- chained add into generated page using `examples/rebuild-add-into-generated-page.patch.json`: `semanticMatch: true`, strict validate detects 23 controls.

Latest create-from-zero acceptance:

- `create out/CreateEmpty.frm --name CreateEmpty --caption Demo --widthPt 340 --heightPt 240`: strict validate detects 0 controls and root FormControl metadata.
- `create out/CreatePatched.frm --name CreatePatched --caption "Desde cero" --widthPt 340 --heightPt 240 --patch examples/create-form.patch.json`: strict validate detects 6 controls.
- generated-from-zero raw parser diagnostics: `legacyScannerCount: 0`, `warningCount: 0`, `errorCount: 0`.
- generated `MultiNuevo` has `multiPageXStreamValidation: exact` and pages `PageUno`/`PageDos`.
- strict raw output includes root storage CLSID plus `CompObj`, and owned storage CLSID plus `CompObj` for generated `Frame`, `MultiPage`, and `Page` storages.
