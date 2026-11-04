Now , because I have the tests and everything and review with CodeRabbit, I was thinking it is done .

So I tried copilot

```text
Unresolved critical defects prevent the packaged generator from running for consumers and cause accepted SQL inputs to produce compiler errors or incorrect runtime behavior.

15 open issues
High severity Pack generator assets under analyzers/dotnet/cs · New
High severity Do not split SQL batches on GO inside string literals · New
High severity Honor global:: when resolving result annotations · New
High severity Avoid duplicate locals for multiple identity sources · New
High severity Emit the shared ConvertResult helper only once · New
High severity Avoid collisions between SQL parameters and generated identifiers · New
High severity Preserve SQL batch scope when executing statements · New
High severity Keep generated query results and statements inside the method · New
Medium severity Deduplicate overridden properties before matching columns · New
Medium severity Detect identity expressions from parsed SQL, not raw text · New
Medium severity Preserve distinct SQLite parameter spellings · New
Medium severity Infer SQL argument types before filtering local variables · New
Medium severity Execute identity queries at their original batch position · New
Medium severity Count only UPDATE and DELETE affected rows · New
Medium severity Assign null when materializing DBNull values · New

```
Happy for me there is 

```

Fix batch with copilot 
```

And the result was

```
5 resolved since last review
High severity Keep generated query results and statements inside the method
High severity Preserve SQL batch scope when executing statements
High severity Avoid duplicate locals for multiple identity sources
High severity Pack generator assets under analyzers/dotnet/cs
Medium severity Execute identity queries at their original batch position

4 previously missed issues
In code that hasn't changed since last review

Medium severity Preserve annotation-like text inside SQL string literals
Medium severity Reject identity inserts whose OUTPUT writes only INTO a table
Medium severity Classify nested UPDATE and DELETE statements correctly
Medium severity Collect SQL declarations recursively before binding parameters

```

However , I was tired. And it is not an work project. I decided to let it go. 
