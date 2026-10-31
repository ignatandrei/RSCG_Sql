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
    [DataRow("rows")]
    [DataRow("non-query")]
    public async Task GeneratedBatchExecutesAsOneCommand(string kind)
    {
        var sql = kind switch
        {
            "query" => "-- @Name RunBatch\nDECLARE @value INT = 7;\nUPDATE People SET Id = @value OUTPUT INSERTED.Id;\n-- @Returns Person\nSELECT @value AS Id;\nUPDATE People SET Id = 8;",
            "insert" => "-- @Name RunBatch\nDECLARE @value INT = 7;\n-- @IdType long\nINSERT INTO People (Id) VALUES (@value);\nSELECT scope_identity();",
            "insert-output" => "-- @Name RunBatch\nDECLARE @value INT = 7;\n-- @IdType long\nINSERT INTO People (Id) OUTPUT INSERTED.Id VALUES (@value);",
            "rows" => "-- @Name RunBatch\nDECLARE @value INT = 7;\nUPDATE People SET Id = @value;\nDELETE FROM People WHERE Id = @value;",
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
        Assert.IsEmpty(diagnostics);
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
        StringAssert.Contains(connection.Command.CommandText, "DECLARE @value INT = 7;");
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
        else if (kind is "insert" or "insert-output")
        {
            Assert.AreEqual("scalar", connection.Command.ExecutionKind);
            StringAssert.Contains(connection.Command.CommandText,
                kind == "insert" ? "SELECT scope_identity();" : "OUTPUT INSERTED.Id");
            Assert.AreEqual(42L, task.GetType().GetProperty("Result")!.GetValue(task));
        }
        else
        {
            Assert.AreEqual("non-query", connection.Command.ExecutionKind);
            if (kind == "rows")
            {
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
