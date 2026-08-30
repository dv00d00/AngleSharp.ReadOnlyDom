# AngleSharp streaming

This product root contains the standalone UTF-8 streaming parser/query/rewrite library, focused tests, source
generators, and runnable examples. No production project references AngleSharp; the test project uses its released
package only as a differential oracle.

```powershell
dotnet restore AngleSharp.Streaming.slnx
dotnet build AngleSharp.Streaming.slnx -c Release --no-restore
dotnet test tests/AngleSharp.Streaming.Tests -c Release --no-build --no-restore
```
