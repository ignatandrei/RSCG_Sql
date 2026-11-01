using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using RSCG_Sql.Generator;

namespace RSCG_Sql.Tests;

[TestClass]
public sealed class SqlCommandsTests
{
    [TestMethod]
    public async Task GeneratedCommandsExecuteAgainstInMemorySqlite()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await SqlCommands.CreatePeopleAsync(connection);
        var id = await SqlCommands.InsertPersonAsync(connection, "Ada", 37);
        var people = await SqlCommands.GetPersonAsync(connection, id);

        Assert.HasCount(1, people);
        Assert.AreEqual(id, people[0].Id);
        Assert.AreEqual("Ada", people[0].Name);
        Assert.AreEqual(37, people[0].Age);

        var insertedPeople = await SqlCommands.InsertPersonAndReturnAsync(connection, "Grace", 27);
        Assert.HasCount(1, insertedPeople);
        Assert.AreEqual("Grace", insertedPeople[0].Name);
        Assert.AreEqual(27, insertedPeople[0].Age);

        Assert.AreEqual(1, await SqlCommands.UpdatePersonAsync(connection, 38, id));
        var updated = await SqlCommands.GetPersonAsync(connection, id);
        Assert.AreEqual(38, updated[0].Age);

        Assert.AreEqual(1, await SqlCommands.DeletePersonAsync(connection, id));
        Assert.IsEmpty(await SqlCommands.GetPersonAsync(connection, id));
    }

    [TestMethod]
    public async Task GeneratedBatchesExecuteStatementsBeforeAndAfterTheQueryAndReturnTotalRowCounts()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await SqlCommands.CreatePeopleAsync(connection);
        var id = await SqlCommands.InsertPersonAsync(connection, "Ada", 37);

        var selected = await SqlCommands.UpdateAndGetPersonAsync(connection, id);

        Assert.AreEqual(38, selected.Single().Age);
        Assert.AreEqual(39, (await SqlCommands.GetPersonAsync(connection, id)).Single().Age);
        Assert.AreEqual(2, await SqlCommands.UpdatePersonTwiceAsync(connection, id));
        Assert.AreEqual(41, (await SqlCommands.GetPersonAsync(connection, id)).Single().Age);
    }

    [TestMethod]
    public async Task GeneratedCommandsRequireAnOpenConnection()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => SqlCommands.CreatePeopleAsync(connection));
    }

    [TestMethod]
    public void GeneratedFileNameIncludesAdditionalFileFolders()
    {
        var additionalFile = new TestAdditionalText(
            Path.Combine("Sql", "People", "Queries.sql"),
            "-- @Name GetPeople\n-- @Returns Person\nSELECT 1 AS Id;");
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new SqlCommandGenerator().AsSourceGenerator()],
            additionalTexts: [additionalFile]);

        driver = driver.RunGenerators(CreateResultCompilation("GeneratorNameTest"));

        var generated = driver.GetRunResult().GeneratedTrees;
        Assert.HasCount(1, generated);
        Assert.AreEqual("Sql_People_Queries.g.cs", Path.GetFileName(generated[0].FilePath));
    }

    [TestMethod]
    [DataRow("nested", "Sql_People_Queries.g.cs")]
    [DataRow("normalized", "Sql_Queries.g.cs")]
    [DataRow("outside", "Queries.g.cs")]
    [DataRow("sibling-prefix", "Queries.g.cs")]
    [DataRow("other-drive", "Queries.g.cs")]
    [DataRow("special-characters", "Sql_Queries____draft_.g.cs")]
    public void GeneratedFileNameUsesOnlyProjectRelativePathsAndSafeCharacters(string scenario, string expected)
    {
        var projectDirectory = Path.GetFullPath("HintNameProject");
        var filePath = scenario switch
        {
            "nested" => Path.Combine(projectDirectory, "Sql", "People", "Queries.sql"),
            "normalized" => Path.Combine(projectDirectory, "Sql", "People", "..", "Queries.sql"),
            "outside" => Path.Combine(projectDirectory, "..", "Shared", "Queries.sql"),
            "sibling-prefix" => Path.Combine(projectDirectory + "Other", "Queries.sql"),
            "other-drive" => Path.DirectorySeparatorChar == '\\'
                ? (char.ToUpperInvariant(projectDirectory[0]) == 'Z' ? @"Y:\Queries.sql" : @"Z:\Queries.sql")
                : Path.Combine(Path.GetPathRoot(projectDirectory)!, "Shared", "Queries.sql"),
            "special-characters" => Path.Combine(projectDirectory, "Sql", "Queries:#?%draft+.sql"),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var additionalFile = new TestAdditionalText(filePath, "-- @Name CreatePeople\nCREATE TABLE People (Id INT);");
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new SqlCommandGenerator().AsSourceGenerator()],
            additionalTexts: [additionalFile],
            optionsProvider: new TestAnalyzerConfigOptionsProvider(Path.Combine(projectDirectory, ".")));

        driver = driver.RunGenerators(CreateResultCompilation("GeneratorPathTest"));

        var result = driver.GetRunResult();
        Assert.IsNull(result.Results.Single().Exception);
        Assert.IsEmpty(result.Diagnostics);
        Assert.AreEqual(expected, result.Results.Single().GeneratedSources.Single().HintName);
    }

    [TestMethod]
    [DataRow("query")]
    [DataRow("insert")]
    [DataRow("insert-output")]
    [DataRow("identity-order")]
    [DataRow("rows")]
    [DataRow("non-query")]
    public async Task GeneratedBatchExecutesAsOneCommand(string kind)
    {
        var sql = kind switch
        {
            "query" => "-- @Name RunBatch\nDECLARE @value INT = 7;\nUPDATE People SET Id = @value OUTPUT INSERTED.Id;\n-- @Returns Person\nSELECT @value AS Id;\nUPDATE People SET Id = 8;",
            "insert" => "-- @Name RunBatch\nDECLARE @value INT = 7;\n-- @IdType long\nINSERT INTO People (Id) VALUES (@value);\nSELECT scope_identity();",
            "insert-output" => "-- @Name RunBatch\nDECLARE @value INT = 7;\n-- @IdType long\nINSERT INTO People (Id) OUTPUT INSERTED.Id VALUES (@value);",
            "identity-order" => "-- @Name RunBatch\n-- @IdType long\nINSERT INTO People (Id) VALUES (7);\nSELECT last_insert_rowid() FROM People;\nDELETE FROM People;",
            "rows" => "-- @Name RunBatch\nDECLARE @value INT = 7;\nUPDATE People SET Id = @value;\nCREATE TABLE Audit (Id INT);\nDELETE FROM People WHERE Id = @value;",
            _ => "-- @Name RunBatch\nDECLARE @value INT = 7;\nSET @value = 8;"
        };
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "BatchTest_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText("namespace Generated { public class Person { public long Id { get; set; } } }")],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new SqlCommandGenerator().AsSourceGenerator()],
            additionalTexts: [new TestAdditionalText("Batch.sql", sql)]);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        Assert.IsEmpty(diagnostics, string.Join(Environment.NewLine, diagnostics));
        using var stream = new MemoryStream();
        var emit = output.Emit(stream);
        Assert.IsTrue(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        var assembly = Assembly.Load(stream.ToArray());
        var method = assembly.GetType("Generated.SqlCommands")!.GetMethod("RunBatchAsync")!;
        using var connection = new BatchTestConnection();

        var task = (Task)method.Invoke(null, [connection, CancellationToken.None])!;
        await task;

        Assert.AreEqual(1, connection.CommandsCreated);
        Assert.AreEqual(1, connection.Command.Executions);
        if (kind != "identity-order")
        {
            StringAssert.Contains(connection.Command.CommandText, "DECLARE @value INT = 7;");
        }
        Assert.DoesNotContain("-- @", connection.Command.CommandText);
        if (kind == "query")
        {
            Assert.AreEqual("reader", connection.Command.ExecutionKind);
            StringAssert.Contains(connection.Command.CommandText, "UPDATE People SET Id = 8;");
            var results = (Array)task.GetType().GetProperty("Result")!.GetValue(task)!;
            Assert.HasCount(1, results);
            Assert.AreEqual(7L, results.GetValue(0)!.GetType().GetProperty("Id")!.GetValue(results.GetValue(0)));
            Assert.IsTrue(connection.Command.Reader!.IsClosed);
        }
        else if (kind is "insert" or "insert-output" or "identity-order")
        {
            Assert.AreEqual("scalar", connection.Command.ExecutionKind);
            if (kind == "insert")
            {
                StringAssert.Contains(connection.Command.CommandText, "SELECT scope_identity();");
            }
            else if (kind == "insert-output")
            {
                StringAssert.Contains(connection.Command.CommandText, "OUTPUT INSERTED.Id");
            }
            else
            {
                var insertPosition = connection.Command.CommandText.IndexOf("INSERT INTO", StringComparison.Ordinal);
                var identityPosition = connection.Command.CommandText.IndexOf("SELECT last_insert_rowid()", StringComparison.Ordinal);
                var deletePosition = connection.Command.CommandText.IndexOf("DELETE FROM", StringComparison.Ordinal);
                Assert.IsTrue(insertPosition < identityPosition && identityPosition < deletePosition);
            }

            Assert.AreEqual(42L, task.GetType().GetProperty("Result")!.GetValue(task));
        }
        else
        {
            Assert.AreEqual("non-query", connection.Command.ExecutionKind);
            if (kind == "rows")
            {
                StringAssert.Contains(connection.Command.CommandText, "CREATE TABLE Audit");
                Assert.AreEqual(5, task.GetType().GetProperty("Result")!.GetValue(task));
            }
        }
    }

    [TestMethod]
    public void GeneratedQueryUsesSelectedColumnNameAndPreservesCase()
    {
        var additionalFile = new TestAdditionalText(
            Path.Combine("Sql", "Queries.sql"),
            "-- @Name GetPerson\n-- @Returns Person\nSELECT p.first_name AS FirstName FROM people p;");
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new SqlCommandGenerator().AsSourceGenerator()],
            additionalTexts: [additionalFile]);

        driver = driver.RunGenerators(CreateResultCompilation("QueryColumnNameTest"));

        var generated = driver.GetRunResult().GeneratedTrees.Single().GetText().ToString();
        StringAssert.Contains(generated, "case 0:");
        StringAssert.Contains(generated, "item.FirstName = global::System.Convert.ToString(reader.GetValue(column)");
        Assert.DoesNotContain("ConvertProperty", generated);
        StringAssert.Contains(generated, "The SELECT returned an unmapped column");
        Assert.DoesNotContain("GetProperty(", generated);
        Assert.DoesNotContain("SetValue(", generated);
        Assert.DoesNotContain("SetProperty(", generated);
    }

    [TestMethod]
    public void BatchSeparatorInsideMultilineStringIsPreserved()
    {
        var sql = "-- @Name GetPerson\n-- @Returns Person\nSELECT 'first\nGO\nlast' AS Name;\nGO\n-- @Name CreatePeople\nCREATE TABLE People (Id INT);";
        var (driver, output, diagnostics) = RunGenerator(
            CreateTestCompilation("namespace Generated { public class Person { public string? Name { get; set; } } }"),
            new TestAdditionalText("Queries.sql", sql));

        Assert.IsEmpty(diagnostics);
        Assert.IsEmpty(output.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var generated = driver.GetRunResult().GeneratedTrees.Single().GetText().ToString();
        StringAssert.Contains(generated, "GO\\nlast");
        StringAssert.Contains(generated, "CreatePeopleAsync");
    }

    [TestMethod]
    public void GlobalResultTypeIgnoresRootNamespaceFallback()
    {
        var source = """
            namespace Generated.Models { public class Person { public int Name { get; set; } } }
            namespace Models { public class Person { public string Name { get; set; } = ""; } }
            """;
        var (driver, output, diagnostics) = RunGenerator(
            CreateTestCompilation(source),
            new TestAdditionalText("Queries.sql", "-- @Name GetPerson\n-- @Returns global::Models.Person\nSELECT 'Ada' AS Name;"));

        Assert.IsEmpty(diagnostics);
        Assert.IsEmpty(output.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        StringAssert.Contains(driver.GetRunResult().GeneratedTrees.Single().GetText().ToString(),
            "item.Name = global::System.Convert.ToString");
    }

    [TestMethod]
    public void OverriddenResultPropertiesAreMatchedOnlyOnce()
    {
        var source = """
            namespace Generated {
                public class BasePerson { public virtual string Name { get; set; } = ""; }
                public class Person : BasePerson { public override string Name { get; set; } = ""; }
            }
            """;
        var (driver, output, diagnostics) = RunGenerator(
            CreateTestCompilation(source),
            new TestAdditionalText("Queries.sql", "-- @Name GetPerson\n-- @Returns Person\nSELECT 'Ada' AS Name;"));

        Assert.IsEmpty(diagnostics);
        Assert.IsEmpty(output.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.HasCount(1, driver.GetRunResult().GeneratedTrees);
    }

    [TestMethod]
    public void IdentityStringLiteralIsNotTreatedAsIdentityExpression()
    {
        var (driver, _, diagnostics) = RunGenerator(
            CreateTestCompilation("namespace Generated { public class Person { public long Id { get; set; } } }"),
            new TestAdditionalText("Insert.sql",
                "-- @Name InsertPerson\n-- @IdType long\nINSERT INTO People (Id) VALUES (1);\nSELECT 'last_insert_rowid()';"));

        Assert.IsTrue(diagnostics.Any(diagnostic =>
            diagnostic.Id == "SQLGEN002" &&
            diagnostic.GetMessage().Contains("recognized identity expression", StringComparison.Ordinal)));
        Assert.IsEmpty(driver.GetRunResult().GeneratedTrees);
    }

    [TestMethod]
    public void InsertWithTwoIdentitySourcesIsRejected()
    {
        var (driver, _, diagnostics) = RunGenerator(
            CreateTestCompilation("namespace Generated { public class Person { public long Id { get; set; } } }"),
            new TestAdditionalText("Insert.sql",
                "-- @Name InsertPerson\n-- @IdType long\nINSERT INTO People (Id) OUTPUT INSERTED.Id VALUES (1);\nSELECT last_insert_rowid();"));

        Assert.IsTrue(diagnostics.Any(diagnostic =>
            diagnostic.GetMessage().Contains("both an OUTPUT identity and an identity SELECT", StringComparison.Ordinal)));
        Assert.IsEmpty(driver.GetRunResult().GeneratedTrees);
    }

    [TestMethod]
    public void HelpersAreEmittedOnlyOnceAcrossSqlFiles()
    {
        var (driver, output, diagnostics) = RunGenerator(
            CreateTestCompilation("namespace Generated { }"),
            new TestAdditionalText("First.sql",
                "-- @Name InsertFirst\n-- @IdType long\nINSERT INTO People (Id) VALUES (1);\nSELECT last_insert_rowid();"),
            new TestAdditionalText("Second.sql",
                "-- @Name InsertSecond\n-- @IdType long\nINSERT INTO People (Id) VALUES (2);\nSELECT last_insert_rowid();"));

        Assert.IsEmpty(diagnostics);
        Assert.IsEmpty(output.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.AreEqual(1, driver.GetRunResult().GeneratedTrees
            .Sum(tree => CountOccurrences(tree.GetText().ToString(), "private static T ConvertResult<T>")));
    }

    [TestMethod]
    public void SqlParameterNamesDoNotCollideWithGeneratedIdentifiers()
    {
        var source = """
            namespace Generated {
                public class Person {
                    public long Id { get; set; }
                    public string? Name { get; set; }
                    public long Age { get; set; }
                    public string? FirstName { get; set; }
                }
            }
            """;
        var sql = "-- @Name GetPerson\n-- @Returns Person\nSELECT @connection AS Id, @cancellationToken AS Name, @command AS Age, @results AS FirstName;";
        var (driver, output, diagnostics) = RunGenerator(
            CreateTestCompilation(source),
            new TestAdditionalText("Queries.sql", sql));

        Assert.IsEmpty(diagnostics);
        Assert.IsEmpty(output.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var generated = driver.GetRunResult().GeneratedTrees.Single().GetText().ToString();
        StringAssert.Contains(generated, "object? connection_2");
        StringAssert.Contains(generated, "object? cancellationToken_2");
        StringAssert.Contains(generated, ".ParameterName = \"@connection\"");
        StringAssert.Contains(generated, ".ParameterName = \"@cancellationToken\"");
    }

    [TestMethod]
    public void DistinctSqlParameterSpellingsAreBothBound()
    {
        var source = "namespace Generated { public class Person { public long Id { get; set; } public long Age { get; set; } } }";
        var (driver, output, diagnostics) = RunGenerator(
            CreateTestCompilation(source),
            new TestAdditionalText("Queries.sql",
                "-- @Name GetPerson\n-- @Returns Person\nSELECT @id AS Id, @ID AS Age;"));

        Assert.IsEmpty(diagnostics);
        Assert.IsEmpty(output.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var generated = driver.GetRunResult().GeneratedTrees.Single().GetText().ToString();
        StringAssert.Contains(generated, ".ParameterName = \"@id\"");
        StringAssert.Contains(generated, ".ParameterName = \"@ID\"");
        Assert.AreEqual(2, CountOccurrences(generated, "command.Parameters.Add("));
    }

    [TestMethod]
    public void ExternalParameterTypeIsInferredFromDeclarationInitializer()
    {
        var source = "namespace Generated { public class Person { public int Id { get; set; } } }";
        var (driver, output, diagnostics) = RunGenerator(
            CreateTestCompilation(source),
            new TestAdditionalText("Queries.sql",
                "-- @Name GetPerson\nDECLARE @local INT = @input;\n-- @Returns Person\nSELECT @local AS Id;"));

        Assert.IsEmpty(diagnostics);
        Assert.IsEmpty(output.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        StringAssert.Contains(driver.GetRunResult().GeneratedTrees.Single().GetText().ToString(), "int input");
    }

    [TestMethod]
    public void NullableResultPropertyIsAssignedWhenColumnIsNull()
    {
        var source = "namespace Generated { public class Person { public string? Name { get; set; } = \"fallback\"; } }";
        var (driver, output, diagnostics) = RunGenerator(
            CreateTestCompilation(source),
            new TestAdditionalText("Queries.sql",
                "-- @Name GetPerson\n-- @Returns Person\nSELECT NULL AS Name;"));

        Assert.IsEmpty(diagnostics);
        Assert.IsEmpty(output.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        StringAssert.Contains(driver.GetRunResult().GeneratedTrees.Single().GetText().ToString(),
            "item.Name = default!;");
    }

    private static (GeneratorDriver Driver, Compilation Output, ImmutableArray<Diagnostic> Diagnostics) RunGenerator(
        CSharpCompilation compilation,
        params TestAdditionalText[] additionalFiles)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new SqlCommandGenerator().AsSourceGenerator()],
            additionalTexts: additionalFiles);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        return (driver, output, diagnostics);
    }

    private static CSharpCompilation CreateTestCompilation(string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        return CSharpCompilation.Create(
            "GeneratorRegressionTests_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static CSharpCompilation CreateResultCompilation(string name) =>
        CSharpCompilation.Create(
            name,
            [CSharpSyntaxTree.ParseText("namespace Generated { public class Person { public long Id { get; set; } public string FirstName { get; set; } } }")],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);

    private sealed class TestAdditionalText(string path, string content) : AdditionalText
    {
        public override string Path { get; } = path;

        public override SourceText GetText(CancellationToken cancellationToken = default) =>
            SourceText.From(content);
    }

    private sealed class TestAnalyzerConfigOptionsProvider(string projectDirectory) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new TestAnalyzerConfigOptions(projectDirectory);

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => GlobalOptions;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => GlobalOptions;
    }

    private sealed class TestAnalyzerConfigOptions(string projectDirectory) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value)
        {
            value = key == "build_property.MSBuildProjectDirectory" ? projectDirectory : string.Empty;
            return key == "build_property.MSBuildProjectDirectory";
        }
    }

    private sealed class BatchTestConnection : DbConnection
    {
        public int CommandsCreated { get; private set; }
        public BatchTestCommand Command { get; } = new();
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => "BatchTest";
        public override string DataSource => "BatchTest";
        public override string ServerVersion => "1";
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public override void Close() { }
        public override void Open() { }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand()
        {
            CommandsCreated++;
            return Command;
        }
    }

    private sealed class BatchTestCommand : DbCommand
    {
        private readonly SqliteCommand parameterSource = new();
        public int Executions { get; private set; }
        public string? ExecutionKind { get; private set; }
        public DataTableReader? Reader { get; private set; }
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; }
        protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection => parameterSource.Parameters;
        public override void Cancel() => throw new NotSupportedException();
        public override void Prepare() => throw new NotSupportedException();
        protected override DbParameter CreateDbParameter() => new SqliteParameter();
        public override int ExecuteNonQuery()
        {
            Executions++;
            ExecutionKind = "non-query";
            return 5;
        }
        public override object ExecuteScalar()
        {
            Executions++;
            ExecutionKind = "scalar";
            return 42L;
        }
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            Executions++;
            ExecutionKind = "reader";
            var output = new DataTable();
            output.Columns.Add("Id", typeof(long));
            output.Rows.Add(99L);
            var selected = output.Clone();
            selected.Rows.Add(7L);
            Reader = new DataTableReader([new DataTable(), output, new DataTable(), selected, new DataTable()]);
            return Reader;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) parameterSource.Dispose();
            base.Dispose(disposing);
        }
    }
}
