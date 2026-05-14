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

## Current Code Shape

- `Program.cs`: top-level entry only.
- `Cli/`: command dispatch, CLI parsing, and exceptions.
- `Vba/`: `.frm` loading, renaming, and companion metadata.
- `Frx/`: FRX parsing, OLE compound storage, and stream diagnostics.
- `MsForms/Model/`: JSON documents, control records, patch records, and shared model types.
- `Validation/`: patch validation.

## Next Parser Targets

1. Move spec-backed object parsing out of `FrxBinary` into `MsForms/Parsers`.
2. Add compact `[MS-OFORMS]` schemas per control type.
3. Implement `TextProps` parser as reusable shared parser.
4. Implement `FormControl` and `OleSiteConcrete` parser for official name, parent, position, tabIndex, visibility, tabStop, default/cancel, tag, and controlTipText.
5. Replace `*.scopes.json` and nearby-string fallback with site data where possible.
6. Add control parsers in this order: `CommandButton`, `Label`, `Image`, `SpinButton`, `ScrollBar`, `TabStrip`, `MorphData`.
7. Add stream rebuilders after parser outputs are stable.

## Working Rule

Do not reread the full PDF during normal implementation. Extract or summarize the needed section into `docs/ms-oforms-control-map.md`, then implement from that compact map.
