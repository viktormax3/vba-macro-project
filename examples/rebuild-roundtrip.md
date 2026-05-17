# Rebuilder pass 1: CFB round-trip

This first rebuilder pass regenerates the OLE/Compound File container while preserving the logical stream bytes (`f`, `o`, `x`, nested `iXX/*`) exactly.

It is intentionally conservative: the source form is parsed and validated first, then the rebuilt FRX is parsed again with the same mode.

```powershell
dotnet run --project src/FrxEdit.Cli/FrxEdit.Cli.csproj -- rebuild UserForm1.frm --out UserForm1.rebuilt.frm --mode strict --report-out UserForm1.rebuild.report.json

dotnet run --project src/FrxEdit.Cli/FrxEdit.Cli.csproj -- inspect UserForm1.rebuilt.frm --mode strict --out UserForm1.rebuilt.inspect.json --raw-out UserForm1.rebuilt.inspect.raw.json
```

Expected result:

- The rebuilt FRX opens as a fresh CFB file.
- `inspect --mode strict` passes.
- `rebuild.report.json` has `semanticMatch: true`.

This does not yet rewrite MSForms logical streams. It is the foundation for the next passes, where `o`, `f`, and `x` will be serialized from the parsed model instead of copied byte-for-byte.
