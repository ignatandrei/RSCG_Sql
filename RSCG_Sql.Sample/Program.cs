using Microsoft.Data.Sqlite;
using RSCG_Sql.Sample;

await using var connection = new SqliteConnection("Data Source=:memory:");
await connection.OpenAsync();

await SqlCommands.CreatePeopleAsync(connection);
var id = await SqlCommands.InsertPersonAsync(connection, "Grace");
var people = await SqlCommands.GetPeopleAsync(connection);

Console.WriteLine($"Inserted person {id}: {people[0].Name}");

public sealed class Person
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
