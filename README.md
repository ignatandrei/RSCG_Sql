# RSCG_Sql

`RSCG_Sql.Generator` is a netstandard2.0 Roslyn incremental source generator that reads `.sql` files registered as `AdditionalFiles`. It parses each `GO`-separated batch with Microsoft ScriptDom and generates asynchronous methods in a `SqlCommands` class in the consuming project's `RootNamespace`. It emits one source file per SQL AdditionalFile, named using its relative folders and file name joined by underscores (for example, `Sql\Commands.sql` becomes `Sql_Commands.g.cs`). The tests and sample target .NET 10.

## Install from NuGet

Install the published [`RSCG_Sql` package](https://www.nuget.org/packages/RSCG_Sql/2026.11.1.1516):

```powershell
dotnet add package RSCG_Sql --version 2026.11.1.1516
```

Or add it directly to your project:

```xml
<ItemGroup>
<PackageReference Include="RSCG_Sql" Version="2026.11.1.1516" />
<AdditionalFiles Include="Sql\Commands.sql" />
<CompilerVisibleProperty Include="MSBuildProjectDirectory" />
</ItemGroup>
```

The package includes the generator and its ScriptDom runtime under `analyzers/dotnet/cs`. For local development against this repository, reference the generator project as an analyzer and include the ScriptDom assembly:

```xml
<ProjectReference Include="..\RSCG_Sql.Generator\RSCG_Sql.Generator.csproj"
                  OutputItemType="Analyzer"
                  ReferenceOutputAssembly="false" />
<Analyzer Include="..\RSCG_Sql.Generator\bin\$(Configuration)\netstandard2.0\Microsoft.SqlServer.TransactSql.ScriptDom.dll" />
<AdditionalFiles Include="Sql\Commands.sql" />
<CompilerVisibleProperty Include="MSBuildProjectDirectory" />
```

Annotate each batch with a method name. `@Returns` names the row type for a `SELECT`; `@IdType` is required for inserts that return an identity:

```sql
-- @Name FindPerson
-- @Returns Person
SELECT id, name FROM people WHERE id = @id;
GO
-- @Name InsertPerson
-- @IdType long
INSERT INTO people(name) VALUES (@name);
SELECT last_insert_rowid();
```

Generated methods accept an already-open `DbConnection`, one argument for each SQL variable reference, and an optional `CancellationToken`. They do not open/close connections or create transactions. Annotated result rows are materialized with a public parameterless constructor and public writable properties matched to result column names without case sensitivity. `UPDATE` and `DELETE` batches return the sum of affected rows; non-query DDL batches return `Task`.

## Supported syntax and mappings

- Batches are separated by a standalone `GO` line; SQL comments use `--`.
- Supported operation forms are `SELECT` with `@Returns`, `INSERT` with `@IdType` and a recognized identity expression, `UPDATE`/`DELETE`, and non-query statements such as DDL. Each batch produces one method.
- Identity expressions recognized by the generator are SQL Server `@@IDENTITY`, `SCOPE_IDENTITY()`, `IDENT_CURRENT(...)`, and `OUTPUT INSERTED.ID`; SQLite `last_insert_rowid()`; and PostgreSQL `currval(...)`. Only SQLite is covered by integration tests.
- SQL declaration types map naturally: integer, bigint, smallint, tinyint, bit, uniqueidentifier, decimal/numeric/money, float/real, date/datetime, time, binary, and character/text types map to their corresponding C# primitives, `Guid`, `DateTime`, `TimeSpan`, `byte[]`, and `string`. Types the generator cannot infer use `object?`.
- SQL dialect extensions must be accepted by the SQL Server ScriptDom parser. Malformed batches, missing/invalid annotations, unsupported identity forms, ambiguous result sets, and duplicate method names produce compiler errors rather than generated fallback behavior.

## Build and tests

```powershell
dotnet build RSCG_Sql.slnx
dotnet test RSCG_Sql.Tests\RSCG_Sql.Tests.csproj
```

The test project executes generated methods against an in-memory SQLite database. `RSCG_Sql.Sample` is a runnable console consumer demonstrating the same integration.
