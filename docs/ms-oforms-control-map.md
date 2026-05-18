# MS-OFORMS compact control map

This file is the working map for parser implementation. `[MS-OFORMS].pdf` remains the source of truth; this file keeps only the pieces needed during coding.

## Common Stream Shape

Most controls persisted to an `IStream` follow:

1. `MinorVersion` byte, usually `0x00`.
2. `MajorVersion` byte, usually `0x02`.
3. `cbControl` UInt16: size of `PropMask + DataBlock + ExtraDataBlock`.
4. `PropMask`: UInt32 or UInt64 depending on control.
5. `DataBlock`: properties <= 4 bytes, in prop-mask order, aligned relative to stream start.
6. `ExtraDataBlock`: larger non-font/non-picture properties, in prop-mask order.
7. `StreamData`: picture properties.
8. Optional `TextProps`.

Stored properties are only those whose prop-mask bit is set. Missing properties use file-format defaults.

## Common Types

- `CountOfBytesWithCompressionFlag`: UInt32; low 31 bits are byte count, high bit means compressed.
- `fmString`: compressed strings are single-byte; uncompressed strings are UTF-16LE.
- `fmSize`: Int32 width + Int32 height, in HIMETRIC units.
- `fmPosition`: Int32 left + Int32 top, in HIMETRIC units.
- `OLE_COLOR`: UInt32, usually shown as VBA `&HXXXXXXXX&`.
- `VariousPropertyBits`: shared UInt32 bitfield for enabled, locked, backStyle, wordWrap, autoSize, and related flags.

## TextProps

Shared by several controls after their stream data.

Important fields:

- `FontName`: `CountOfBytesWithCompressionFlag` + `fmString`.
- `FontEffects`: UInt32 bitfield.
- `FontHeight`: UInt32, font size is `FontHeight / 20`.
- `FontCharSet`: byte.
- `FontPitchAndFamily`: byte.
- `ParagraphAlign`: byte.
- `FontWeight`: UInt16.

## FormControl / Sites

`f` streams for parent controls contain:

- `FormControl`.
- Optional `ClassTable`.
- `FormSiteData`.
- Optional `DesignExtender`.

`FormSiteData` contains:

- `FormObjectDepthTypeCount` array for hierarchy/depth.
- `OleSiteConcreteControl` array, one per embedded control.

`OleSiteConcrete` is the official source for site-level properties:

- `Name`
- `Tag`
- `ControlTipText`
- `Position`
- `TabIndex`
- `BitFlags` / `SITE_FLAG`
- `ObjectStreamSize`
- runtime/control source metadata

`SITE_FLAG` contains visible/tab/default/cancel/streamed/autosize-style flags.

## CommandButton

Section: `2.2.1`.

PropMask bits:

- bit 0: `ForeColor`
- bit 1: `BackColor`
- bit 2: `VariousPropertyBits`
- bit 3: `Caption` count in DataBlock, string in ExtraDataBlock
- bit 4: `PicturePosition`
- bit 5: `Size` in ExtraDataBlock, must be set
- bit 6: `MousePointer`
- bit 7: `Picture` marker + StreamData picture
- bit 8: `Accelerator`
- bit 9: `TakeFocusOnClick`
- bit 10: `MouseIcon` marker + StreamData mouse icon

Implemented status:

- Parser exists in `MsForms/Parsers/ObjectStreamParser.cs`.
- Document-backed add schema exists in `MsForms/Factories/ControlSchemas.cs`.
- Minimal generated object payload uses `CommandButtonPropMask = 0x00000028` (`Caption` + `Size`) plus `TextPropsPropMask = 0x00000075`.
- Generated site flags use `0x00000013` (`tabStop`, `visible`, `streamed`).

## Label

Section: `2.2.4`.

Similar to `CommandButton`, plus:

- `BorderColor`
- `BorderStyle`
- `SpecialEffect`

Uses `TextProps`.

Implemented status:

- Parser exists in `MsForms/Parsers/ObjectStreamParser.cs`.
- Document-backed add schema exists in `MsForms/Factories/ControlSchemas.cs`.
- Minimal generated object payload uses `LabelPropMask = 0x00000028` (`Caption` + `Size`) plus `TextPropsPropMask = 0x00000035`.
- Generated site flags use `0x00000032` (`visible`, `streamed`, `autoSize`) and intentionally do not set `tabStop`.
- Do not emit default `ForeColor`, `BackColor`, `VariousPropertyBits`, `BorderStyle`, or `SpecialEffect` unless a patch explicitly requests a non-default value.

## Image

Section: `2.2.3`.

Key fields:

- `AutoSize`
- `BorderColor`
- `BackColor`
- `BorderStyle`
- `MousePointer`
- `PictureSizeMode`
- `SpecialEffect`
- `Size`
- `Picture`
- `PictureAlignment`
- `PictureTiling`
- `VariousPropertyBits`
- `MouseIcon`

No `TextProps`.

## ScrollBar / SpinButton

Sections: `2.2.7` and `2.2.8`.

Shared important fields:

- `ForeColor`
- `BackColor`
- `VariousPropertyBits`
- `Size`
- `Min`
- `Max`
- `Position`
- `SmallChange`
- `LargeChange` for ScrollBar
- `Orientation`
- `Delay`
- `MouseIcon`
- `MousePointer`

## TabStrip

Section: `2.2.9`.

Includes arrays/lists of per-tab values. Requires special handling for array property values and `TabStripTabFlag`.

## MorphData

Section: `2.2.5`.

Aggregate backing format for:

- `TextBox`
- `ComboBox`
- `CheckBox`
- `OptionButton`
- `ToggleButton`
- `ListBox`

Subtype must be resolved from display/style/class/site metadata, not from object name.

Implemented status:

- Parser exists in `MsForms/Parsers/ObjectStreamParser.cs`.
- First document-backed add schema exists for `TextBox`.
- Generated `TextBox` uses fixture-aligned editable `VariousPropertyBits = 0x2C80481B`, `TextPropsPropMask = 0x00000035`, and site flags `0x00000013`.
- `ComboBox`, `ListBox`, `CheckBox`, `OptionButton`, and `ToggleButton` should each get their own schema entry even though they share `MorphDataControl`; their `DisplayStyle`, caption/value fields, colors, and bitfields differ enough that a shared generic builder is unsafe.
