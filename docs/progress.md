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

## Current Code Shape

- `Program.cs`: top-level entry only.
- `Cli/`: command dispatch, CLI parsing, and exceptions.
- `Vba/`: `.frm` loading, renaming, and companion metadata.
- `Frx/`: FRX parsing, OLE compound storage, and stream diagnostics.
- `MsForms/Model/`: JSON documents, control records, patch records, and shared model types.
- `MsForms/Binary/`: low-level `[MS-OFORMS]` binary helpers.
- `MsForms/Parsers/`: object stream and text property parsers.
- `Validation/`: patch validation.

## Next Parser Targets

1. Add compact `[MS-OFORMS]` schemas per control type.
2. Add explicit MultiPage/Page site parsing so page-owned controls get parent data without `*.scopes.json`.
3. Replace remaining nearby-string fallback with site data where possible.
4. Add control parsers in this order: `CommandButton`, `Label`, `Image`, `SpinButton`, `ScrollBar`, `TabStrip`, `MorphData`.
5. Add stream rebuilders after parser outputs are stable.

## Working Rule

Do not reread the full PDF during normal implementation. Extract or summarize the needed section into `docs/ms-oforms-control-map.md`, then implement from that compact map.
