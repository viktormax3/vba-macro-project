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
- `MultiPage` generation now creates a storage-backed container with parent OleSiteConcrete, owned `iXX/f`, `iXX/o`, `iXX/x`, copied MultiPage `CompObj`, internal TabStrip object payload, and generated initial Page storages.
- Generated `MultiPage` pages are parsed in strict mode as real `Page` controls with exact `x` stream validation and no legacy scanner fallback.
- Direct `Page` add under an existing `MultiPage` now creates a page storage, patches the owning MultiPage `f`, rewrites `x`, rewrites the internal TabStrip arrays, and can be targeted by a later child-control add.
- Empty generated Page storages can be targeted by later rebuilds whether their empty FormSiteData uses class-info-count or no-class-count layout.
- `frxedit create` now generates a `.frm/.frx` pair from zero with a strict-parseable empty UserForm, and can immediately apply a full-patch add document.
- Built-in `CompObj` baselines are available for Form/Page, Frame, and MultiPage so generated containers no longer depend on source fixture CompObj streams.
- Strict validation now checks root/container storage CLSIDs and required `CompObj` streams, catching OLE storage defects that can pass parser-only validation but fail in the native designer.
- Empty native-style Frame storages now omit internal `FormSiteData` until the first child is added; the rebuilder can append the first child `FormSiteData` block later.
- When an empty generated Frame receives its first child, the rebuilder now promotes its `FormControl` to the native child-bearing Frame shape (`0x0C1A0C48`, `nextAvailableId`, `shapeCookie`) and removes the empty-frame font padding before `FormSiteData`.
- When an empty generated Page receives its first child, the rebuilder now appends the native 16-byte Page tail after `FormSiteData`, matching native MultiPage pages that contain controls.
- Generated MultiPage `f` streams now include the native MultiPage tail after FormSiteData (`00020C0019000000F08F0000FF010000`), separate from the Page tail.
- Added `CreateTabStripDemo` regression fixtures showing the correct standalone `TabStrip` usage: tabs select normal sibling `Frame` panels through VBA `TabStrip_Change`; `TabStrip` is not a storage/container owner.
- Patch documents now support `code.tabStripPanels`, generating marked `.frm` VBA that connects a standalone `TabStrip` to sibling `Frame` panels.
- Added `docs/property-edit-coverage.md` to distinguish strict parser/create coverage from true 100% property edit coverage.
- Added a multi-stage `CreateTorture` regression that builds a 42-control form with root controls, nested frames, multipages, pages, and controls inside generated containers.
- The previous `ObjectStreamRoundTripRewriter` build warnings have been resolved; current build is clean.
- Added `MsFormsControlSchemaCatalog` as the internal checklist for spec section, parser, masks, TextProps, site flags, and factory readiness per control type.
- Added `docs/ms-oforms-schema-validation.md` with the strict-inspect baseline for all common controls in `userformallcontrol`.
- Generated control add writes document-backed `OleSiteConcrete` site bytes plus object payload bytes and re-inspects with strict parser semantic match.
- Example fixture `examples/rebuild-add-generated.patch.json` demonstrates no-template add for all current leaf factories plus `Frame` and `MultiPage`.

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

1. Implement `Page` reorder under existing `MultiPage`, keeping `x`, inner TabStrip, internal sites, and page storages synchronized.
2. Build the public property schema/catalog described in `docs/property-edit-coverage.md`, including mask/default promotion rules.
3. Add strict validation that rejects common controls using `TabStrip` as `parent`; use sibling panels plus VBA or use `MultiPage` for real per-page containers.
4. Run import validation in Corel and Office/VBA for generated-from-zero forms after the strict OLE storage checks.
5. Add binary picture payload support for `Image` and picture-capable controls as the final feature layer.

## Working Rule

Do not reread the full PDF during normal implementation. Extract or summarize the needed section into `docs/ms-oforms-control-map.md`, then implement from that compact map.
