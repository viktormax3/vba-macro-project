# Rebuild object-serialize mode

`object-serialize` is the first active object payload serializer pass. It keeps each control payload fixed-length, so `FormSiteData` and `ObjectStreamSize` do not change yet, but known fields are written through control-specific serializers instead of blindly copying each slice.

```powershell
dotnet run --project src/FrxEdit.Cli/FrxEdit.Cli.csproj -- rebuild UserForm1.frm --out out/UserForm1.object-serialize.frm --mode strict --stream-mode object-serialize --report-out out/UserForm1.object-serialize.report.json

dotnet run --project src/FrxEdit.Cli/FrxEdit.Cli.csproj -- inspect out/UserForm1.object-serialize.frm --mode strict --out out/UserForm1.object-serialize.inspect.json --raw-out out/UserForm1.object-serialize.inspect.raw.json
```

Expected result:

```text
OK: rebuild stream mode object-serialize
OK: controls N -> N, semantic match: True
```

This mode is a bridge between byte-slice roundtrip and future variable-length stream generation. It validates the serializer write path for known fixed-size fields and existing counted string spans without changing payload sizes.
