Now  about implementing

----

read the *.md files


Please implement the project and run the tests to ensure it works

---

The AI has done, however , from 
```xml

    <AdditionalFiles Include="Sql\Commands.sql" />

```
it has generated

```csharp
context.AddSource("SqlCommands.g.cs", SourceText.From(EmitSource(rootNamespace, methods), Encoding.UTF8));
```

Obviously , this was needed another instruction

```
The name of the file generated must be the name of the additional file - with the folder name first , separated by _ . Add to final instructions file and
implement and redo the tests
```

Moral 1 :
Retrospectively, maybe I have let it know that the test must have at least 2 additional files.

Moral 2:
Read the generated code. 
