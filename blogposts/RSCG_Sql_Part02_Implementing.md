Now I will describe the implementation.

----

read the *.md files


Please implement the project and run the tests to ensure it works

---

The AI implemented the project. However, when I used this entry:
```xml

    <AdditionalFiles Include="Sql\Commands.sql" />

```
I saw it generate:

```csharp
context.AddSource("SqlCommands.g.cs", SourceText.From(EmitSource(rootNamespace, methods), Encoding.UTF8));
```

I needed to give the AI another instruction:

```
The name of the file generated must be the name of the additional file - with the folder name first , separated by _ . Add to final instructions file and
implement and redo the tests
```

Moral 1:
In hindsight, I should have told the AI that the test must use at least two additional files.

Moral 2:
I should read the generated code.
