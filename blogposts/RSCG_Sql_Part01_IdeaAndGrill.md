
I have met this repo - https://github.com/colinhacks/reylo . It transforms files into typescript with content mappers https://github.com/microsoft/typescript-go/pull/4712

This is same with Roslyn Code Generator from AdditionalFiles

So I said - this could be a nice additions for C# .

I did know how to do it - with ROslyn and https://www.nuget.org/packages/Microsoft.SqlServer.TransactSql.ScriptDom

However, I said : how about the AI will do for me  ? 

So I started with instructions

```md

Create a Rolsyn Code Generator in C# , that implements IIncrementalGenerator , that parses a additional sql file

with https://www.nuget.org/packages/Microsoft.SqlServer.TransactSql.ScriptDom to generate a function with parameters from the statements in the sql file. 

The statements will be separated by GO;

The statements will have comments starting with -- 

The name of the function will be in a comment near the statement.

The function must have as a parameter a DbConnection and other arguments derived from parameters of the statement.

In the sql file could be more insert / select / update / delete instructions 

If a select  , it looks for comment for the class that should be return . It assumes returning an array of the class .Also , it parses the sqlcommand for each 

if update / delete -  it looks for return value of number of rows modified

if insert - it looks for see if after insert it selects something .  

It hardcodes values for SqlServer ( @@identity , Scope\_Identity, ident\_current or OUTPUT INSERTED.ID ) , SQLite (last\_insert\_rowid()) ,PostgreSQL(currval) and others.

The type (int / long / guid ) must be in a comment before the stored procedure

The Roslyn Code Generator must have tests for SQLite in memeory ( leave Postgres and SqlServer for later)


```

And I have install one of my favorite skill , https://github.com/mattpocock/skills/blob/main/skills/productivity/grilling/SKILL.md - wuth one difference : max 30 questions

And after grilling

```txt
Read file @instructions.md . Grill me before starting the app . After finish, write in a file what it is needed
for the app
```

this was the final result of instructions:

```md

# Final Instructions

## Goal

Create a C# Roslyn incremental source generator implementing `IIncrementalGenerator`. It consumes SQL files supplied by a consuming project as Roslyn `AdditionalFiles`, parses them with `Microsoft.SqlServer.TransactSql.ScriptDom`, and generates strongly shaped database-operation methods.

## Projects and deliverables

- Use netstandard2.0 for the generator.
- Use .NET 10 for the tests, and sample.
- Create a source-generator project, an MSTest project, and a console sample consumer.
- The sample should demonstrate generated methods against an in-memory SQLite database.
- Add this requirements document to the repository.
- Build the solution and run its tests. The requested milestone is build-and-test; do not launch a long-running application.

## SQL input and annotations

- Consume `.sql` files registered as Roslyn `AdditionalFiles`.
- SQL batches are separated by `GO`. Generate one method per batch and execute the batch's statements in order.
- SQL comments use `--`.
- Support these annotations and attachment rules:
  - `-- @Name MethodName` immediately before the batch's first statement names the generated method.
  - `-- @Returns TypeName` annotates the following `SELECT` and identifies its result class.
  - `-- @IdType int|long|Guid` annotates the relevant insert or procedure and specifies the inserted ID's C# type.
- Document supported SQL syntax and emit clear diagnostics for malformed, ambiguous, or unsupported input rather than silently generating incorrect code.

## Generated API and behavior

- Generate a `SqlCommands` static class in the consuming project's root namespace.
- Generate one source file per SQL `AdditionalFile`. The generated file name must come from the AdditionalFile path, with its folder names first and separated from the file name by underscores (for example, `Sql\Commands.sql` generates `Sql_Commands.g.cs`; nested folders are joined with underscores).
- Each generated method is asynchronous, accepts a caller-provided `DbConnection`, its SQL parameters, and a `CancellationToken`.
- Require the supplied connection to already be open. Do not change its state or create transactions; connection and transaction ownership remain with the caller.
- Infer parameter types from SQL declarations or available SQL context. Use natural C# mappings for supported SQL types; use `object?` when a type cannot be inferred. Document the supported mappings.
- For a `SELECT` annotated with `@Returns`, return an array of the named class. Materialize rows by constructing a public parameterless instance and setting public writable properties whose names match result columns case-insensitively.
- For batches containing `UPDATE` and/or `DELETE` but no result `SELECT`, return the sum of affected rows.
- For an `INSERT` followed by a result `SELECT`, treat the statements as one operation and return the `SELECT` result.
- For an `INSERT` without a following result `SELECT`, return the inserted ID using the `@IdType` annotation and a recognized identity expression:
  - SQL Server: `@@IDENTITY`, `SCOPE_IDENTITY()`, `IDENT_CURRENT`, or `OUTPUT INSERTED.ID`.
  - SQLite: `last_insert_rowid()`.
  - PostgreSQL: `currval(...)`.
- Infer the provider from the identity SQL expression. SQLite is the only provider required for integration-style tests in this version. PostgreSQL and SQL Server tests are deferred; unspecified providers are out of scope unless explicitly added later.

## Tests and verification

- Add focused MSTest coverage for source generation and SQLite in-memory behavior.
- Build the solution and run the test suite after implementation.
- Keep PostgreSQL and SQL Server integration tests out of scope for this version.


```
