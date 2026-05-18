# FRX editor progress

## Done

- CLI commands exist: `inspect`, `apply`, `validate`, `dump-records`, `dump-storage`, and `dump-stream-records`.
- `inspect-frx.bat` accepts `.frm` or `.frx` and writes both human and raw JSON outputs.
- `.frx` prefix and OLE compound signature are detected.
- OLE compound streams can be read, including mini-stream file-offset mapping for safe patching.
- Structured `f` stream records detect common MSForms controls by type code instead of default names.
- Human JSON separates natural properties from raw offsets.
- Raw JSON keeps stream, marker, type code, offsets, masks, and parser diagnostics.
- CommandButton object streams are partially spec-backed using `[MS-OFORMS]` `CommandButtonControl`.
- Conservative in-place property patching exists for discovered offsets such as caption, tag, controlTipText, colors, fontSize, and tabIndex.
- Project structure has been split out of the original monolithic `Program.cs`.
- Object stream parsing has been moved out of `FrxBinary` into `MsForms/Parsers`.
- `TextProps` parsing now reads the spec-backed version/cb/propMask/data/extra-data layout when the object stream exposes it.
- Common binary helpers for masks, alignment, `fmString`, and string cleanup live in `MsForms/Binary`.
- `FormSiteData` / `OleSiteConcrete` parsing now provides spec-backed site name, type cache index, position, tabIndex, flags, tag, controlTipText, and object stream size when present.
- Parent assignment now uses `siteDepth` within a form stream and associates child `f` streams with non-streamed container controls such as frames.
- MultiPage page sites are now recognized as `Page` controls, including nameless internal-site skipping, so page-owned controls receive page parents.
- Rebuild mode supports CFB/object/FormSite full patching for property edits, layout edits, renames, add-from-template, move, remove leaf/container/page, and MultiPage page-removal synchronization.
- `MsForms/Factories/GeneratedControlFactory` can now add `CommandButton`, `Label`, `TextBox`, `ComboBox`, `ListBox`, `CheckBox`, `OptionButton`, `ToggleButton`, `Image`, `ScrollBar`, `SpinButton`, and standalone `TabStrip` without `fromTemplate`.
- Factory generation is now schema-driven per control type instead of one generic byte builder.
- `Label` generation now uses label-specific site flags and masks (`siteBitFlags 0x32`, `LabelPropMask 0x28`, `TextPropsPropMask 0x35`) to avoid button-like/default-noise payloads.
- `TextBox` generation now uses fixture-aligned editable `VariousPropertyBits 0x2C80481B`.
- `ComboBox` and `ListBox` generation now uses fixture-aligned MorphData list baselines without `MorphDataColumnInfo`.
- `CheckBox`, `OptionButton`, and `ToggleButton` generation now uses fixture-aligned MorphData caption/value baselines with display styles `4`, `5`, and `6`.
- `Image` generation now supports a no-picture baseline; binary picture StreamData remains a dedicated follow-up.
- `SpinButton` and `ScrollBar` generation now supports baseline orientation plus size payloads.
- `TabStrip` generation now supports a standalone two-or-more-tab baseline with `ArrayString` tab captions/names and visible+enabled tab flags.
- `Frame` generation now creates a storage-backed empty container with parent OleSiteConcrete, owned `iXX/f`, owned `iXX/o`, and copied Frame `CompObj` when a fixture source is available.
- Empty generated Frame storages can be targeted by later rebuilds; the rebuilder can now create FormSiteData from an empty `CountOfSites = 0` stream.
- Added `MsFormsControlSchemaCatalog` as the internal checklist for spec section, parser, masks, TextProps, site flags, and factory readiness per control type.
- Added `docs/ms-oforms-schema-validation.md` with the strict-inspect baseline for all common controls in `userformallcontrol`.
- Generated control add writes document-backed `OleSiteConcrete` site bytes plus object payload bytes and re-inspects with strict parser semantic match.
- Example fixture `examples/rebuild-add-generated.patch.json` demonstrates no-template add for all current leaf factories.

## Current Code Shape

- `Program.cs`: top-level entry only.
- `Cli/`: command dispatch, CLI parsing, and exceptions.
- `Vba/`: `.frm` loading, renaming, and companion metadata.
- `Frx/`: FRX parsing, OLE compound storage, and stream diagnostics.
- `MsForms/Model/`: JSON documents, control records, patch records, and shared model types.
- `MsForms/Binary/`: low-level `[MS-OFORMS]` binary helpers.
- `MsForms/Parsers/`: object stream and text property parsers.
- `MsForms/Factories/`: document-backed byte factories for controls that no longer require template cloning.
- `MsForms/Factories/ControlSchemas.cs`: per-control generation contracts; new controls should be added here, not as ad hoc branches in the dispatcher.
- `MsForms/Schema/MsFormsControlSchemaCatalog.cs`: control readiness/default catalog used to keep factory work aligned with spec-backed parser output.
- `Frx/Rebuild/`: CFB, FormSiteData, object stream, movement, removal, and patch rebuild logic.
- `Validation/`: patch validation.

## Next Targets

1. Add storage factories for `MultiPage` and `Page`, including `x` stream bootstrapping and internal TabStrip generation.
2. Implement `Page` add/reorder under `MultiPage`, keeping `x`, inner TabStrip, internal sites, and page storages synchronized.
3. Add binary picture payload support for `Image` and picture-capable controls.
4. Add `frxedit create` to generate a new `.frm/.frx` pair from zero.
5. Run import validation in Corel and Office/VBA as the final acceptance layer.

## Working Rule

Do not reread the full PDF during normal implementation. Extract or summarize the needed section into `docs/ms-oforms-control-map.md`, then implement from that compact map.
