# CLI create/add/edit contract

This is the public JSON contract that is safe to use today. It is intentionally narrower than the
native MSForms property grid: `[MS-OFORMS]` contains many optional/default-backed fields that still
need explicit mask promotion/removal before they can be edited with full native parity.

## Commands

Create from zero:

```powershell
dotnet run --project src/FrxEdit.Cli -- create out/BaseCreate.frm --name BaseCreate --caption "Base Create" --widthPt 640 --heightPt 520 --patch examples/base-create-all-supported.patch.json
```

Add to an existing form:

```powershell
dotnet run --project src/FrxEdit.Cli -- rebuild out/BaseCreate.frm --out out/BaseAdd.frm --mode strict --stream-mode full-patch --patch examples/base-add-all-supported.patch.json
```

Edit supported properties:

```powershell
dotnet run --project src/FrxEdit.Cli -- rebuild out/BaseAdd.frm --out out/BaseEdit.frm --mode strict --stream-mode full-patch --patch examples/base-edit-supported.patch.json
```

When adding a `Page` and then controls inside that new page, use two rebuild passes: first add the
`Page`, inspect/validate the output, then target that generated page in the next patch. The rebuilder
already supports this staged flow; same-patch children for a newly generated page are intentionally not
part of the green public contract yet.

## Common add fields

Every `add[]` entry accepts:

| Field | Meaning |
| --- | --- |
| `type` | Required when `fromTemplate` is absent. Supported: `CommandButton`, `Label`, `TextBox`, `ComboBox`, `ListBox`, `CheckBox`, `OptionButton`, `ToggleButton`, `Image`, `ScrollBar`, `SpinButton`, `TabStrip`, `Frame`, `MultiPage`, `Page`. |
| `name` | Required VBA/MSForms control name. |
| `parent` | Optional. Empty means UserForm root. Can be `Frame` or `Page` for common controls. `MultiPage` accepts only `Page`. `TabStrip` is not a container. |
| `leftPt`, `topPt`, `widthPt`, `heightPt` | Preferred human-friendly geometry in points. |
| `caption` | Convenience field for caption-capable controls. |
| `value` | Convenience field for value/text-capable controls. |
| `properties` | Type-specific creation properties listed below. |

Low-level `left`, `top`, `width`, `height`, `rawWidth`, and `rawHeight` remain available for diagnostics,
but normal patches should use point units.

## Factory-backed create/add properties

| Type | Supported creation properties |
| --- | --- |
| `CommandButton` | `caption`, `properties.foreColor`, `properties.backColor`, `properties.enabled`, `properties.locked`, `properties.backStyle`, `properties.wordWrap`, `properties.autoSize`, `properties.imeMode`, `properties.picturePosition`, `properties.mousePointer`, `properties.accelerator`, `properties.takeFocusOnClick`, `properties.tabStop`, `properties.visible`, `properties.default`, `properties.cancel`, `properties.fontName`, `properties.fontSize`, `properties.paragraphAlign` at creation time |
| `Label` | `caption`, `properties.foreColor`, `properties.backColor`, `properties.borderColor`, `properties.enabled`, `properties.backStyle`, `properties.wordWrap`, `properties.autoSize`, `properties.imeMode`, `properties.picturePosition`, `properties.mousePointer`, `properties.borderStyle`, `properties.specialEffect`, `properties.textAlign` (`left`, `center`, `right`), `properties.accelerator`, `properties.fontName`, `properties.fontSize` |
| `TextBox` | `value`, `properties.value`, `properties.fontName`, `properties.fontSize`, `properties.backColor`, `properties.foreColor`, `properties.borderColor`, `properties.enabled`, `properties.locked`, `properties.backStyle`, `properties.autoSize`, `properties.autoTab`, `properties.autoWordSelect`, `properties.dragBehavior`, `properties.enterFieldBehavior`, `properties.enterKeyBehavior`, `properties.hideSelection`, `properties.integralHeight`, `properties.multiLine`, `properties.selectionMargin`, `properties.tabKeyBehavior`, `properties.wordWrap`, `properties.imeMode`, `properties.maxLength`, `properties.passwordChar`, `properties.scrollBars`, `properties.borderStyle`, `properties.specialEffect`, `properties.mousePointer`, `properties.textAlign` (`left`, `center`, `right`) |
| `CheckBox` | `caption`, `value`, `properties.caption`, `properties.value`, `properties.backColor`, `properties.foreColor`, `properties.fontName`, `properties.fontSize` |
| `OptionButton` | same as `CheckBox` |
| `ToggleButton` | same as `CheckBox`, with command-button TextProps mask |
| `ComboBox` | `properties.fontName`, `properties.fontSize`; list rows/columns are not generated yet |
| `ListBox` | `properties.fontName`, `properties.fontSize`; list rows/columns are not generated yet |
| `ScrollBar` | `properties.orientation` |
| `SpinButton` | `properties.orientation` |
| `Image` | geometry only; picture payload is intentionally pending |
| `TabStrip` | `properties.tabCaptions`, `properties.tabNames`, `properties.fontName`, `properties.fontSize` |
| `Frame` | `caption` |
| `MultiPage` | `properties.pageNames`, `properties.pageCaptions` |
| `Page` | `parent` must be a `MultiPage`; page caption is currently driven by owning MultiPage TabStrip sync |

## Editable properties today

`properties` patch supports these public property names:

- Object payload: `caption`, `value`, `groupName`, `fontName`, `fontSize`, `backColor`, `foreColor`, `borderColor`.
- Site payload in `full-patch` mode: `tabIndex`, `controlTipText`.
- Layout/site: `layout[name].leftPt`, `topPt`, `widthPt`, `heightPt`; `renames`; `move`; `remove`.
- Code generation: `code.tabStripPanels`.

Important limitation: colors and other optional fields are only rewritten when the parsed payload already
contains that documented field/offset. The next property-schema pass must add default-to-explicit promotion
so a default property can be made explicit exactly like the native designer does.

Container layout edits can be written, but the current semantic report still compares some storage-backed
container displayed-size metadata separately from site geometry. Prefer validating those visually/native-side
until that comparator is upgraded.

## Current parity verdict

Create/add coverage for common controls is structurally strong and strict-validates. Native property-grid
parity is not complete yet. Remaining blockers are tracked in `docs/property-edit-coverage.md`: picture and
mouse-icon StreamData, ComboBox/ListBox row and column data, high-level bitfield/enumeration setters,
TabStrip tab mutation after creation, richer Page/MultiPage mutation, and default-to-explicit property promotion.

## Latest Fixture Validation

Validated on the base contract fixtures:

```powershell
dotnet build FrxEdit.sln
dotnet run --no-build --project src/FrxEdit.Cli -- create out/BaseCreate.frm --name BaseCreate --caption "Base Create" --widthPt 640 --heightPt 520 --patch examples/base-create-all-supported.patch.json
dotnet run --no-build --project src/FrxEdit.Cli -- rebuild out/BaseCreate.frm --out out/BaseAdd.frm --mode strict --stream-mode full-patch --patch examples/base-add-all-supported.patch.json --report-out out/BaseAdd.report.json
dotnet run --no-build --project src/FrxEdit.Cli -- rebuild out/BaseAdd.frm --out out/BaseEdit.frm --mode strict --stream-mode full-patch --patch examples/base-edit-supported.patch.json --report-out out/BaseEdit.report.json
dotnet run --no-build --project src/FrxEdit.Cli -- validate out/BaseCreate.frm --mode strict
dotnet run --no-build --project src/FrxEdit.Cli -- validate out/BaseAdd.frm --mode strict
dotnet run --no-build --project src/FrxEdit.Cli -- validate out/BaseEdit.frm --mode strict
```

Results:

- `BaseCreate`: 20 controls, strict parser OK.
- `BaseAdd`: 27 controls, `semanticMatch: true`.
- `BaseEdit`: 27 controls, `semanticMatch: true`.
