# Part 8: Lessons I Learned

I used AI to build a Roslyn source generator from an initial idea. I learned several lessons during this work.

## I define the requirements first

I get better results when I turn an idea into a clear specification before I ask AI to write code. I include the framework, SQL input rules, generated API, supported providers, test scope, and deferred work.

I also define edge cases in the specification. For example, I should have required a test with at least two SQL `AdditionalFiles`. That test could have exposed the generated file-name problem earlier.

## I test behavior, not only the build

A successful build did not show that the generated code would work for every input. I need tests for multiple SQL files, batch scope, parameter handling, result mapping, and generated file names.

I also need to test the NuGet package from a consuming project. A package that builds and packs can still have incorrect analyzer assets or dependencies.

## Specific Roslyn: I read the Roslyn generated code

I found reflection in the generated code where I wanted direct property access. I had to clarify how the generator should map SQL columns to properties and convert values.

I learned to inspect generated output as part of my review. I also need to give precise examples when I want a specific implementation.

## I use review findings to improve my tests

CodeRabbit reported issues with package layout, file paths, SQL batches, and generated code. After I asked Copilot to fix a batch, the review showed five resolved issues and four issues the earlier review missed.

I treat each review finding as a claim to test. I reproduce each finding against the code, add a focused test when needed, and run the relevant checks again. I do not treat an AI review or a successful build as proof that the project works.

## I keep the scope and effort realistic

I limited the first integration tests to SQLite and deferred PostgreSQL and SQL Server. This gave the project a clear initial scope.

I also recognized when I felt tired and the project no longer justified more effort. I chose to stop rather than continue without a clear reason.
