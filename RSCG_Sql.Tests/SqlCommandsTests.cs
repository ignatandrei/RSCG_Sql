using Microsoft.Data.Sqlite;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
}
