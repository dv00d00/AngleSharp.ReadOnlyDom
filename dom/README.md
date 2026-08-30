# AngleSharp.ReadOnlyDom

This product root contains the AngleSharp-dependent object and compact DOM libraries, their shared test suite, samples,
and deterministic tag generator. It is self-contained so the directory can become a repository root later.

The matching AngleSharp source checkout must be beside this directory (or beside the current workspace while both
products still share a repository). Set `AngleSharpSourceRoot` for any other layout.

```powershell
dotnet restore AngleSharp.ReadOnlyDom.slnx --disable-parallel -p:RestoreUseStaticGraphEvaluation=false -m:1
dotnet build AngleSharp.ReadOnlyDom.slnx -c Release --no-restore -m:1
dotnet run --project tests/AngleSharp.ReadOnlyDom.Tests -c Release -f net10.0 --no-restore -- --minimum-expected-tests 1
```
