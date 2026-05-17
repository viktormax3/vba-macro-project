# Rebuild pass 26: object stream round-trip

This mode rebuilds the CFB/OLE container and also reconstructs each `o` object stream from parser-identified object stream slices.

It is still byte-preserving at each individual control payload, but it proves the next layer of the rebuilder:

- read `FormSiteData` / `OleSiteConcrete`
- use `objectStreamLocalOffset` + `objectStreamSize`
- rebuild each storage `o` stream in parser order
- preserve unknown gaps/internal object-stream slices
- regenerate the full CFB container
- validate the rebuilt form with strict parser mode

```powershell
dotnet run --project src/FrxEdit.Cli/FrxEdit.Cli.csproj -- rebuild UserForm1.frm --out out/UserForm1.o.rebuilt.frm --mode strict --stream-mode object-roundtrip --report-out out/UserForm1.o.rebuild.report.json

dotnet run --project src/FrxEdit.Cli/FrxEdit.Cli.csproj -- inspect out/UserForm1.o.rebuilt.frm --mode strict --out out/UserForm1.o.inspect.json --raw-out out/UserForm1.o.inspect.raw.json
```

Expected result:

```text
OK: rebuild stream mode object-roundtrip
OK: controls N -> N, semantic match: True
```
