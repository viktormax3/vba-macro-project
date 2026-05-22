# FRX property edit coverage

This is the practical editor coverage matrix. `[MS-OFORMS]` remains the source of truth; this file
tracks what `frxedit` can create, parse, and safely edit today.

## Status Terms

- `create`: factory-backed creation without `fromTemplate`.
- `parse`: strict parser reads the structure without legacy fallback.
- `edit`: `properties` patch can rewrite the value through documented offsets/serializer.
- `code`: `.frm` VBA generation, not `.frx` binary state.

## Current Coverage

| Control | Create | Parse | Editable Today | Not Yet 100% |
| --- | --- | --- | --- | --- |
| UserForm | yes | yes | caption/size through create baseline; OLE wrapper/CFB rebuild | full form property mutation surface |
| CommandButton | yes | yes | caption, fontName, fontSize/fontHeight, foreColor, backColor, enabled, locked, backStyle, wordWrap, autoSize, imeMode, picturePosition, mousePointer, accelerator, takeFocusOnClick, tabStop, visible, default, cancel, size/layout, tabIndex/site fields | picture/mouse icon binary payload |
| Label | yes | yes | caption, fontName, fontSize/fontHeight, textAlign/paragraphAlign, foreColor, backColor, borderColor, enabled, backStyle, wordWrap, autoSize, imeMode, picturePosition, mousePointer, borderStyle, specialEffect, accelerator, size/layout, tabIndex/site fields | picture/mouse icon binary payload |
| TextBox | yes | yes | value, fontName, fontSize/fontHeight, foreColor, backColor, borderColor, enabled, locked, backStyle, autoSize, autoTab, autoWordSelect, dragBehavior, enterFieldBehavior, enterKeyBehavior, hideSelection, integralHeight, multiLine, selectionMargin, tabKeyBehavior, wordWrap, imeMode, maxLength, passwordChar, scrollBars, borderStyle, specialEffect, mousePointer, textAlign, size/layout, tabIndex/site fields | mouse icon binary payload |
| ComboBox | yes | yes | value, fontName, fontSize/fontHeight, foreColor, backColor, borderColor, enabled, locked, backStyle, columnHeads, integralHeight, matchRequired, editable, dragBehavior, enterFieldBehavior, selectionMargin, autoWordSelect, autoSize, hideSelection, autoTab, imeMode, maxLength, borderStyle, specialEffect, mousePointer, displayStyle, listWidth, boundColumn, textColumn, columnCount, listRows, matchEntry, listStyle, showDropButtonWhen, dropButtonStyle, textAlign, size/layout, tabIndex/site fields | rows/list data, RowSource/ControlSource site strings, non-default column info arrays, mouse icon |
| ListBox | yes | yes | fontName, fontSize/fontHeight, foreColor, backColor, borderColor, enabled, locked, backStyle, columnHeads, integralHeight, selectionMargin, autoWordSelect, hideSelection, imeMode, borderStyle, specialEffect, mousePointer, displayStyle, listWidth, boundColumn, textColumn, columnCount, matchEntry, listStyle, multiSelect, textAlign, size/layout, tabIndex/site fields | rows/list data, RowSource/ControlSource site strings, non-default column info arrays, mouse icon |
| CheckBox | yes | yes | caption, value/tri-state value string, groupName, controlSource on generated/existing explicit sites, fontName, fontSize/fontHeight, foreColor, backColor, enabled, locked, backStyle, alignment, wordWrap, autoSize, imeMode, picturePosition, mousePointer, specialEffect, accelerator, textAlign, size/layout, tabIndex/site fields | picture/mouse icon binary payload |
| OptionButton | yes | yes | caption, value/tri-state value string, groupName, controlSource on generated/existing explicit sites, fontName, fontSize/fontHeight, foreColor, backColor, enabled, locked, backStyle, alignment, wordWrap, autoSize, imeMode, picturePosition, mousePointer, specialEffect, accelerator, textAlign, size/layout, tabIndex/site fields | picture/mouse icon binary payload |
| ToggleButton | yes | yes | caption, value, fontName, fontSize/fontHeight, foreColor/backColor, `variousPropertyBitsRaw`, size/layout, tabIndex/site fields | high-level toggle/button bitfield helpers |
| Image | yes, no picture | yes | size/layout, tabIndex/site fields, colors when present | picture payload, mouse icon, picture alignment/tiling setters |
| ScrollBar | yes | yes | orientation, size/layout, tabIndex/site fields, colors when present | min/max/value/smallChange/largeChange high-level setters |
| SpinButton | yes | yes | orientation, size/layout, tabIndex/site fields, colors when present | min/max/value/smallChange/delay high-level setters |
| TabStrip | yes | yes | tab captions/names at create time, fontName/fontSize, size/layout, tabIndex/site fields | tab array mutation after creation, tab state helpers |
| Frame | yes | yes | caption, size/layout, child add/move/remove | full form-property mutation surface |
| MultiPage | yes | yes | create pages, add/remove pages, child add into pages, internal TabStrip sync | page reorder, richer page/control properties |
| Page | yes | yes | add controls, size/layout through owner MultiPage model | page reorder, page-specific transition properties |

## Supported Patch Surface

The current public writer accepts a deliberately small set of property names:

| Patch area | Supported names |
| --- | --- |
| `properties` object payload | `caption`, `value`, `groupName`, `fontName`, `fontSize`, `backColor`, `foreColor`, `borderColor`, `enabled`, `locked`, `backStyle`, `alignment`, `wordWrap`, `autoSize`, `imeMode`, `picturePosition`, `mousePointer`, `accelerator`, `takeFocusOnClick`, `borderStyle`, `specialEffect`, `textAlign`, `paragraphAlign`, `maxLength`, `passwordChar`, `scrollBars`, `dragBehavior`, `enterFieldBehavior`, `enterKeyBehavior`, `tabKeyBehavior`, `selectionMargin`, `autoWordSelect`, `hideSelection`, `autoTab`, `multiLine`, `integralHeight` |
| `properties` site payload in `full-patch` | `tabIndex`, `controlTipText`, `controlSource` when the site already exposes it or create/add emits it |
| `layout` | `leftPt`, `topPt`, `widthPt`, `heightPt`, plus raw diagnostic aliases |
| structure | `renames`, `move`, `remove`, `add` |
| generated VBA | `code.tabStripPanels` |

`backColor`, `foreColor`, `borderColor`, and `controlTipText` require a documented field/span in the
parsed payload. If the property is currently default/absent, the writer does not yet promote the prop-mask
and allocate the new field. That promotion layer is the main gap between "safe editor" and native designer
parity.

`TripleState` does not have a separate MS-OFORMS field in the MorphData contract. For CheckBox and
OptionButton the persisted ternary state is the `value` string: `"0"` cleared, `"1"` selected, and any
other value is the indeterminate state described by the spec.

## Create/Add JSON Bases

Use these fixtures as the living public contract:

- `examples/base-create-all-supported.patch.json`: create-from-zero patch that exercises all generated
  control/container types and the recommended `TabStrip` + sibling `Frame` panel pattern.
- `examples/base-add-all-supported.patch.json`: follow-up add patch that targets generated `Frame`,
  `MultiPage`, and `Page` containers, including a generated `Page` under an existing `MultiPage`.
- `examples/base-edit-supported.patch.json`: edit patch limited to properties the current writer can
  safely serialize.

The more complete command-oriented explanation is in `docs/cli-create-add-contract.md`.

## Important TabStrip Rule

`TabStrip` is not a child-control container. It is a selector. To build tabbed content with `TabStrip`,
create sibling `Frame` panels and generate VBA with:

```json
{
  "code": {
    "tabStripPanels": {
      "TsSections": [ "FrameAlpha", "FrameBeta", "FrameGamma" ]
    }
  }
}
```

For real child containers per tab, use `MultiPage`.

## Remaining Work Toward "100% Editable"

1. Add a property schema/catalog that maps public property names to binary fields, masks, defaults, validators, and writer behavior.
2. Implement default-to-explicit promotion for optional fields whose prop-mask bit is currently absent.
3. Implement explicit-to-default removal for optional fields that return to defaults.
4. Replace raw bitfield edits with high-level booleans/enums for `VariousPropertyBits`, tab flags, list styles, alignment, and button states.
5. Add picture/mouse-icon `StreamData` support.
6. Add list/row/column data support for `ComboBox` and `ListBox`.
7. Add page reorder and richer `MultiPage`/`Page` property editing.

## Audit Conclusion

After comparing the current factories/writers with the documented MS-OFORMS structures, the honest product
status is:

- `create`/`add` can generate every common MSForms control and storage-backed container used by the project.
- strict parse/rebuild is stable for the base contract fixtures and torture fixtures.
- property editing is safe for the supported subset, but not yet a native property-grid equivalent.

The next "100%" pass should not add more heuristics. It should introduce a typed property catalog per control
with: MS-OFORMS mask bit, default value, binary location, validator, create support, edit support, and default
promotion/removal behavior.
