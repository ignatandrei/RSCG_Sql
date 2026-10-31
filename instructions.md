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



