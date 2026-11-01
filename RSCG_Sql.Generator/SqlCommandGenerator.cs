using System.Collections.Immutable;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace RSCG_Sql.Generator;

[Generator]
public sealed class SqlCommandGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor SqlParseError = new(
        "SQLGEN001", "Invalid SQL", "SQL batch in '{0}' could not be parsed: {1}",
        "SQL generation", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidBatch = new(
        "SQLGEN002", "Invalid SQL batch", "{0}", "SQL generation",
        DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly Regex BatchSeparatorLine = new(
        @"^[ \t]*GO[ \t]*(?:--[^\r\n]*)?[ \t]*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Annotation = new(
        @"(?im)^\s*--\s*@(?<key>Name|Returns|IdType)\s+(?<value>[^\r\n]+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AnnotationComment = new(
        @"(?im)^\s*--\s*@(Name|Returns|IdType)\s+[^\r\n]*(?:\r?\n|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var sqlFiles = context.AdditionalTextsProvider
            .Where(static file => file.Path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Select(static (file, cancellationToken) =>
                new SqlFile(file.Path, file.GetText(cancellationToken)?.ToString() ?? string.Empty))
            .Collect();

        var rootNamespace = context.AnalyzerConfigOptionsProvider
            .Select(static (options, _) =>
                options.GlobalOptions.TryGetValue("build_property.RootNamespace", out var value)
                    ? value
                    : "Generated");
        var projectDirectory = context.AnalyzerConfigOptionsProvider
            .Select(static (options, _) =>
                options.GlobalOptions.TryGetValue("build_property.MSBuildProjectDirectory", out var value)
                    ? value
                    : string.Empty);

        context.RegisterSourceOutput(
            sqlFiles.Combine(rootNamespace).Combine(projectDirectory).Combine(context.CompilationProvider),
            static (productionContext, input) =>
                Generate(productionContext, input.Left.Left.Left, input.Left.Left.Right, input.Left.Right, input.Right));
    }

    private static void Generate(
        SourceProductionContext context,
        ImmutableArray<SqlFile> files,
        string rootNamespace,
        string projectDirectory,
        Compilation compilation)
    {
        var generatedFiles = new List<(string HintName, List<GeneratedMethod> Methods)>();
        var methodNames = new HashSet<string>(StringComparer.Ordinal);
        var hintNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var hintName = GetHintName(file.Path, projectDirectory);
            if (!hintNames.Add(hintName))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidBatch, Location.None,
                    $"Additional SQL files produce the same generated file name '{hintName}'."));
                continue;
            }

            var methods = new List<GeneratedMethod>();
            foreach (var batch in ParseFile(context, file))
            {
                if (batch is not null)
                {
                    if (batch.Kind == MethodKind.Query &&
                        !TryResolveResult(context, compilation, rootNamespace, batch))
                    {
                        continue;
                    }

                    if (!methodNames.Add(batch.Name))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            InvalidBatch, Location.None,
                            $"Method name '{batch.Name}' is declared more than once."));
                        continue;
                    }

                    methods.Add(batch);
                }
            }

            if (methods.Count > 0)
            {
                generatedFiles.Add((hintName, methods));
            }
        }

        var helperFileIndex = generatedFiles.FindIndex(
            generatedFile => generatedFile.Methods.Any(method => method.Kind == MethodKind.InsertId));
        for (var index = 0; index < generatedFiles.Count; index++)
        {
            var generatedFile = generatedFiles[index];
            context.AddSource(
                generatedFile.HintName,
                SourceText.From(EmitSource(rootNamespace, generatedFile.Methods, index == helperFileIndex), Encoding.UTF8));
        }
    }

    private static string GetHintName(string filePath, string projectDirectory)
    {
        var relativePath = Path.IsPathRooted(filePath) ? Path.GetFileName(filePath) : filePath;
        if (!string.IsNullOrEmpty(projectDirectory))
        {
            var fullPath = Path.GetFullPath(filePath);
            var directoryPrefix = Path.GetFullPath(projectDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var comparison = Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            relativePath = fullPath.StartsWith(directoryPrefix, comparison)
                ? fullPath.Substring(directoryPrefix.Length)
                : Path.GetFileName(fullPath);
        }

        var fileName = Path.ChangeExtension(relativePath, null) ?? relativePath;
        var hintName = new StringBuilder(fileName.Length);
        foreach (var character in fileName)
        {
            hintName.Append(char.IsLetterOrDigit(character) ||
                character is '_' or '-' or '.' or ' ' ? character : '_');
        }
        return $"{hintName}.g.cs";
    }

    private static bool TryResolveResult(
        SourceProductionContext context, Compilation compilation, string rootNamespace, GeneratedMethod method)
    {
        var isGlobalQualified = method.ResultType!.StartsWith("global::", StringComparison.Ordinal);
        var typeName = isGlobalQualified ? method.ResultType.Substring("global::".Length) : method.ResultType;
        var type = isGlobalQualified
            ? compilation.GetTypeByMetadataName(typeName)
            : compilation.GetTypeByMetadataName(rootNamespace + "." + typeName) ??
              compilation.GetTypeByMetadataName(typeName);
        if (type is null)
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidBatch, Location.None,
                $"Result type '{method.ResultType}' could not be resolved."));
            return false;
        }

        var properties = new Dictionary<string, IPropertySymbol>(StringComparer.Ordinal);
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (!properties.ContainsKey(property.Name))
                {
                    properties.Add(property.Name, property);
                }
            }
        }

        foreach (var column in method.ResultColumnNames)
        {
            var matches = properties.Values.Where(property =>
                !property.IsStatic && !property.IsIndexer &&
                property.DeclaredAccessibility == Accessibility.Public &&
                property.SetMethod is { DeclaredAccessibility: Accessibility.Public, IsInitOnly: false } &&
                string.Equals(property.Name, column, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (column is null || matches.Length != 1)
            {
                context.ReportDiagnostic(Diagnostic.Create(InvalidBatch, Location.None,
                    $"SELECT column '{column ?? "(unnamed)"}' must match exactly one public writable property on '{method.ResultType}'."));
                return false;
            }

            method.ResultProperties.Add(matches[0]);
        }

        if (method.ResultColumnNames.Length == 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidBatch, Location.None,
                $"SELECT for '{method.Name}' must explicitly name its result columns."));
            return false;
        }

        return true;
    }

    private static string PropertyConversion(ITypeSymbol type, string value)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            type = nullable.TypeArguments[0];
        }

        var conversion = type.SpecialType switch
        {
            SpecialType.System_Boolean => "ToBoolean",
            SpecialType.System_Byte => "ToByte",
            SpecialType.System_SByte => "ToSByte",
            SpecialType.System_Int16 => "ToInt16",
            SpecialType.System_UInt16 => "ToUInt16",
            SpecialType.System_Int32 => "ToInt32",
            SpecialType.System_UInt32 => "ToUInt32",
            SpecialType.System_Int64 => "ToInt64",
            SpecialType.System_UInt64 => "ToUInt64",
            SpecialType.System_Single => "ToSingle",
            SpecialType.System_Double => "ToDouble",
            SpecialType.System_Decimal => "ToDecimal",
            SpecialType.System_Char => "ToChar",
            SpecialType.System_String => "ToString",
            SpecialType.System_DateTime => "ToDateTime",
            _ => null
        };
        if (conversion is not null)
        {
            return $"global::System.Convert.{conversion}({value}, global::System.Globalization.CultureInfo.InvariantCulture)!";
        }

        if (type.ToDisplayString() == "System.Guid")
        {
            return $"global::System.Guid.Parse(global::System.Convert.ToString({value}, global::System.Globalization.CultureInfo.InvariantCulture)!)";
        }

        var typeName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return type.TypeKind == TypeKind.Enum
            ? $"({typeName})({PropertyConversion(((INamedTypeSymbol)type).EnumUnderlyingType!, value)})"
            : $"({typeName}){value}";
    }

    private static IEnumerable<GeneratedMethod?> ParseFile(SourceProductionContext context, SqlFile file)
    {
        var batchStart = 0;
        foreach (var separator in FindBatchSeparators(file.Text).Append(new BatchBoundary(file.Text.Length, 0)))
        {
            var batchEnd = separator.Index;
            var batchText = file.Text.Substring(batchStart, batchEnd - batchStart);
            var startOffset = batchStart;
            batchStart = separator.Index + separator.Length;

            if (string.IsNullOrWhiteSpace(batchText))
            {
                continue;
            }

            var parser = new TSql160Parser(initialQuotedIdentifiers: true);
            var script = parser.Parse(new StringReader(batchText), out IList<ParseError> errors);
            if (errors.Count > 0)
            {
                var error = errors[0];
                var location = CreateLocation(file, startOffset + error.Offset);
                context.ReportDiagnostic(Diagnostic.Create(
                    SqlParseError, location, file.Path, error.Message));
                continue;
            }

            if (script is not TSqlScript sqlScript)
            {
                continue;
            }

            foreach (var sqlBatch in sqlScript.Batches)
            {
                if (sqlBatch.Statements.Count == 0)
                {
                    continue;
                }

                var annotations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var annotationMatches = Annotation.Matches(batchText).Cast<Match>().ToArray();
                var duplicateAnnotation = false;
                foreach (var match in annotationMatches)
                {
                    var key = match.Groups["key"].Value;
                    if (annotations.ContainsKey(key))
                    {
                        ReportInvalid(context, file, startOffset,
                            $"Batch contains more than one '@{match.Groups["key"].Value}' annotation.");
                        duplicateAnnotation = true;
                        break;
                    }

                    annotations.Add(key, match.Groups["value"].Value.Trim());
                }

                if (duplicateAnnotation)
                {
                    continue;
                }

                if (!annotations.TryGetValue("Name", out var methodName) ||
                    !SyntaxFacts.IsValidIdentifier(methodName) ||
                    SyntaxFacts.GetKeywordKind(methodName) != SyntaxKind.None ||
                    !IsAnnotationAttached(batchText, annotationMatches, "Name", sqlBatch.Statements[0].StartOffset))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        InvalidBatch,
                        CreateLocation(file, startOffset),
                        $"Each SQL batch needs a valid '-- @Name MethodName' annotation immediately before its first statement in '{file.Path}'."));
                    continue;
                }

                var statements = sqlBatch.Statements
                    .Select(statement => ExtractStatement(batchText, statement))
                    .Where(statement => !string.IsNullOrWhiteSpace(statement))
                    .ToArray();
                if (statements.Length == 0)
                {
                    continue;
                }

                if (!TryBuildMethod(context, file, startOffset, methodName, annotations, annotationMatches, batchText, sqlBatch, statements, out var method))
                {
                    continue;
                }

                yield return method;
            }
        }
    }

    private static IEnumerable<BatchBoundary> FindBatchSeparators(string text)
    {
            var state = SqlLexicalState.Normal;
            var blockCommentDepth = 0;
            var lineStart = 0;
            while (lineStart < text.Length)
            {
                var lineEnd = text.IndexOfAny(['\r', '\n'], lineStart);
                if (lineEnd < 0)
                {
                    lineEnd = text.Length;
                }

                var line = text.Substring(lineStart, lineEnd - lineStart);
                var newlineLength = lineEnd == text.Length
                    ? 0
                    : text[lineEnd] == '\r' && lineEnd + 1 < text.Length && text[lineEnd + 1] == '\n' ? 2 : 1;
                if (state == SqlLexicalState.Normal && BatchSeparatorLine.IsMatch(line))
                {
                    yield return new BatchBoundary(lineStart, line.Length + newlineLength);
                    lineStart = lineEnd + newlineLength;
                    continue;
                }

                for (var index = lineStart; index < lineEnd; index++)
                {
                    var current = text[index];
                    var next = index + 1 < lineEnd ? text[index + 1] : '\0';
                    if (state == SqlLexicalState.Normal && current == '-' && next == '-') break;
                    switch (state)
                    {
                        case SqlLexicalState.Normal:
                            if (current == '/' && next == '*')
                            {
                                state = SqlLexicalState.BlockComment;
                                blockCommentDepth++;
                                index++;
                            }
                            else if (current == '\'') state = SqlLexicalState.SingleQuoted;
                            else if (current == '"') state = SqlLexicalState.DoubleQuoted;
                            else if (current == '[') state = SqlLexicalState.BracketQuoted;
                            break;
                        case SqlLexicalState.SingleQuoted:
                            if (current == '\'' && next == '\'') index++;
                            else if (current == '\'') state = SqlLexicalState.Normal;
                            break;
                        case SqlLexicalState.DoubleQuoted:
                            if (current == '"' && next == '"') index++;
                            else if (current == '"') state = SqlLexicalState.Normal;
                            break;
                        case SqlLexicalState.BracketQuoted:
                            if (current == ']' && next == ']') index++;
                            else if (current == ']') state = SqlLexicalState.Normal;
                            break;
                        case SqlLexicalState.BlockComment:
                            if (current == '/' && next == '*')
                            {
                                blockCommentDepth++;
                                index++;
                            }
                            else if (current == '*' && next == '/')
                            {
                                blockCommentDepth--;
                                index++;
                                if (blockCommentDepth == 0) state = SqlLexicalState.Normal;
                            }
                            break;
                    }
                }

                lineStart = lineEnd + newlineLength;
            }
    }

    private static string ExtractStatement(string batchText, TSqlStatement statement)
    {
        var start = Math.Max(0, Math.Min(statement.StartOffset, batchText.Length));
        var length = Math.Max(0, Math.Min(statement.FragmentLength, batchText.Length - start));
        var text = batchText.Substring(start, length);
        return AnnotationComment.Replace(text, string.Empty).Trim();
    }

    private static bool TryBuildMethod(
        SourceProductionContext context,
        SqlFile file,
        int batchStart,
        string methodName,
        Dictionary<string, string> annotations,
        Match[] annotationMatches,
        string batchText,
        TSqlBatch sqlBatch,
        string[] statements,
        out GeneratedMethod method)
    {
        method = null!;
        var statementNodes = sqlBatch.Statements.ToArray();
        var selectIndexes = statementNodes
            .Select((statement, index) => (statement, index))
            .Where(item => item.statement is SelectStatement)
            .Select(item => item.index)
            .ToArray();

        if (annotations.TryGetValue("Returns", out var returnType))
        {
            if (selectIndexes.Length != 1 ||
                !IsValidTypeName(returnType) ||
                !IsAnnotationAttached(batchText, annotationMatches, "Returns", statementNodes[selectIndexes[0]].StartOffset))
            {
                ReportInvalid(context, file, batchStart,
                    $"Batch '{methodName}' must annotate exactly one following SELECT with a valid '-- @Returns TypeName'.");
                return false;
            }

            method = new GeneratedMethod(
                methodName,
                statements,
                CollectParameters(sqlBatch),
                MethodKind.Query,
                returnType,
                null,
                statementNodes.Take(selectIndexes[0]).Count(statement => statement switch
                {
                    InsertStatement insert => insert.InsertSpecification.OutputClause is not null,
                    UpdateStatement update => update.UpdateSpecification.OutputClause is not null,
                    DeleteStatement delete => delete.DeleteSpecification.OutputClause is not null,
                    MergeStatement merge => merge.MergeSpecification.OutputClause is not null,
                    _ => false
                }),
                GetResultColumnNames(statementNodes[selectIndexes[0]]));
            return true;
        }

        var insertIndexes = statementNodes
            .Select((statement, index) => (statement, index))
            .Where(item => item.statement is InsertStatement)
            .Select(item => item.index)
            .ToArray();
        var identitySelectIndex = selectIndexes
            .Where(index => IsIdentitySelect(statementNodes[index]))
            .DefaultIfEmpty(-1)
            .First();
        var hasOutputIdentity = insertIndexes.Any(index =>
            statementNodes[index] is InsertStatement insert && HasOutputIdentity(insert));

        if (insertIndexes.Length > 0)
        {
            if (identitySelectIndex >= 0 && hasOutputIdentity)
            {
                ReportInvalid(context, file, batchStart,
                    $"Insert batch '{methodName}' cannot contain both an OUTPUT identity and an identity SELECT.");
                return false;
            }

            if (insertIndexes.Length != 1 ||
                !annotations.TryGetValue("IdType", out var idType) ||
                !IsIdType(idType) ||
                !IsAnnotationAttached(batchText, annotationMatches, "IdType", statementNodes[insertIndexes[0]].StartOffset) ||
                (identitySelectIndex < 0 && !hasOutputIdentity))
            {
                ReportInvalid(context, file, batchStart,
                    $"Insert batch '{methodName}' needs one insert, a recognized identity expression, and '-- @IdType int|long|Guid'.");
                return false;
            }

            if (selectIndexes.Any(index => index != identitySelectIndex))
            {
                ReportInvalid(context, file, batchStart,
                    $"Insert batch '{methodName}' contains an unsupported SELECT without a '-- @Returns' annotation.");
                return false;
            }

            method = new GeneratedMethod(
                methodName,
                statements,
                CollectParameters(sqlBatch),
                MethodKind.InsertId,
                null,
                idType,
                0,
                []);
            return true;
        }

        if (selectIndexes.Length > 0)
        {
            ReportInvalid(context, file, batchStart,
                $"SELECT batch '{methodName}' needs a '-- @Returns TypeName' annotation.");
            return false;
        }

        var isRowsAffected = statementNodes.Any(statement =>
            statement is UpdateStatement or DeleteStatement);
        method = new GeneratedMethod(
            methodName,
            statements,
            CollectParameters(sqlBatch),
            isRowsAffected ? MethodKind.RowsAffected : MethodKind.NonQuery,
            null,
            null,
            0,
            []);
        return true;
    }

    private static string?[] GetResultColumnNames(TSqlStatement statement)
    {
        if (statement is not SelectStatement { QueryExpression: QuerySpecification query } ||
            query.SelectElements.Any(element => element is not SelectScalarExpression))
        {
            return [];
        }

        return query.SelectElements
            .Cast<SelectScalarExpression>()
            .Select(element =>
                element.ColumnName?.Identifier?.Value ??
                (element.Expression as ColumnReferenceExpression)?.MultiPartIdentifier?.Identifiers.LastOrDefault()?.Value)
            .ToArray();
    }

    private static bool IsIdentitySelect(TSqlStatement statement)
    {
        var visitor = new IdentityExpressionCollector();
        statement.Accept(visitor);
        return visitor.Found;
    }

    private static bool HasOutputIdentity(InsertStatement statement) =>
        statement.InsertSpecification.OutputClause?.SelectColumns
            .OfType<SelectScalarExpression>()
            .Any(column =>
                column.Expression is ColumnReferenceExpression reference &&
                reference.MultiPartIdentifier.Identifiers.Count >= 2 &&
                reference.MultiPartIdentifier.Identifiers[reference.MultiPartIdentifier.Identifiers.Count - 2].Value.Equals("inserted", StringComparison.OrdinalIgnoreCase) &&
                reference.MultiPartIdentifier.Identifiers[reference.MultiPartIdentifier.Identifiers.Count - 1].Value.Equals("id", StringComparison.OrdinalIgnoreCase)) == true;

    private static ImmutableArray<SqlParameter> CollectParameters(TSqlBatch batch)
    {
        var declarations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inferredTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var statement in batch.Statements.OfType<DeclareVariableStatement>())
        {
            foreach (var declaration in statement.Declarations)
            {
                var name = declaration.VariableName.Value;
                var type = declaration.DataType is SqlDataTypeReference sqlType
                    ? MapSqlType(sqlType.Name.BaseIdentifier.Value)
                    : "object?";
                declarations[name] = type;
                if (declaration.Value is VariableReference source && !inferredTypes.ContainsKey(source.Name))
                {
                    inferredTypes.Add(source.Name, type);
                }
            }
        }

        var visitor = new VariableReferenceCollector();
        batch.Accept(visitor);
        var variables = visitor.Variables
            .Where(variable =>
                !variable.Name.StartsWith("@@", StringComparison.Ordinal) &&
                !declarations.ContainsKey(variable.Name))
            .GroupBy(variable => variable.Name, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(variable => variable.Offset)
            .ToArray();
        var usedIdentifiers = new HashSet<string>(
            ["connection", "cancellationToken", "command", "results", "reader", "resultIndex", "item",
             "column", "insertedValue", "insertedId", "rows"],
            StringComparer.Ordinal);
        for (var index = 0; index < variables.Length; index++)
        {
            usedIdentifiers.Add("sqlParameter_" + index);
        }

        var parameters = ImmutableArray.CreateBuilder<SqlParameter>(variables.Length);
        foreach (var variable in variables)
        {
            var identifier = SafeIdentifier(variable.Name.TrimStart('@'));
            var baseIdentifier = identifier;
            var suffix = 2;
            while (!usedIdentifiers.Add(identifier))
            {
                identifier = baseIdentifier + "_" + suffix++;
            }

            parameters.Add(new SqlParameter(
                variable.Name.TrimStart('@'),
                inferredTypes.TryGetValue(variable.Name, out var type) ? type : "object?",
                identifier));
        }

        return parameters.ToImmutable();
    }

    private static bool IsNullableType(ITypeSymbol type) =>
        type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };

    private static string MapSqlType(string sqlType) => sqlType.ToLowerInvariant() switch
    {
        "int" or "integer" => "int",
        "bigint" => "long",
        "smallint" => "short",
        "tinyint" => "byte",
        "bit" => "bool",
        "uniqueidentifier" => "global::System.Guid",
        "decimal" or "numeric" or "money" or "smallmoney" => "decimal",
        "float" => "double",
        "real" => "float",
        "date" or "datetime" or "datetime2" or "smalldatetime" => "global::System.DateTime",
        "time" => "global::System.TimeSpan",
        "binary" or "varbinary" or "image" => "byte[]?",
        "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext" or "xml" => "string?",
        _ => "object?"
    };

    private static bool IsIdType(string type) =>
        type is "int" or "long" or "Guid" or "guid";

    private static bool IsValidTypeName(string type)
    {
        var parsed = SyntaxFactory.ParseTypeName(type);
        return !parsed.ContainsDiagnostics;
    }

    private static bool IsAnnotationAttached(
        string batchText,
        Match[] annotations,
        string name,
        int statementOffset)
    {
        var annotation = annotations.FirstOrDefault(match =>
            match.Groups["key"].Value.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (annotation is null || annotation.Index + annotation.Length > statementOffset)
        {
            return false;
        }

        var annotationEnd = annotation.Index + annotation.Length;
        var linesBetween = batchText.Substring(annotationEnd, statementOffset - annotationEnd);
        return linesBetween.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).All(line =>
        {
            var trimmed = line.Trim();
            return trimmed.Length == 0 || trimmed.StartsWith("--", StringComparison.Ordinal);
        });
    }

    private static string EmitSource(
        string rootNamespace,
        IReadOnlyList<GeneratedMethod> methods,
        bool includeHelpers)
    {
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated />");
        builder.AppendLine("#nullable enable");
        builder.Append("namespace ").Append(rootNamespace).AppendLine(";");
        builder.AppendLine();
        builder.AppendLine("public static partial class SqlCommands");
        builder.AppendLine("{");

        foreach (var method in methods)
        {
            EmitMethod(builder, method);
        }

        if (includeHelpers)
        {
            EmitHelpers(builder);
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    private static void EmitMethod(StringBuilder builder, GeneratedMethod method)
    {
        var returnType = method.Kind switch
        {
            MethodKind.Query => $"global::System.Threading.Tasks.Task<{method.ResultType}[]>",
            MethodKind.InsertId => $"global::System.Threading.Tasks.Task<{IdTypeName(method.IdType!)}>",
            MethodKind.RowsAffected => "global::System.Threading.Tasks.Task<int>",
            _ => "global::System.Threading.Tasks.Task"
        };
        var parameters = new List<string> { "global::System.Data.Common.DbConnection connection" };
        parameters.AddRange(method.Parameters.Select(parameter =>
            $"{parameter.Type} {EscapeIdentifier(parameter.Identifier)}"));
        parameters.Add("global::System.Threading.CancellationToken cancellationToken = default");

        builder.Append("    public static async ").Append(returnType).Append(' ')
            .Append(method.Name).Append("Async(").Append(string.Join(", ", parameters)).AppendLine(")");
        builder.AppendLine("    {");
        builder.AppendLine("        if (connection.State != global::System.Data.ConnectionState.Open)");
        builder.AppendLine("        {");
        builder.AppendLine("            throw new global::System.InvalidOperationException(\"The supplied DbConnection must already be open.\");");
        builder.AppendLine("        }");

        switch (method.Kind)
        {
            case MethodKind.Query:
                EmitQuery(builder, method);
                return;
            case MethodKind.InsertId:
                EmitIdScalar(builder, method);
                builder.AppendLine("        return insertedId;");
                break;
            default:
                EmitNonQuery(builder, method, method.Kind == MethodKind.RowsAffected);
                break;
        }

        builder.AppendLine("    }");
        builder.AppendLine();
    }

    private static void EmitNonQuery(StringBuilder builder, GeneratedMethod method, bool countRows)
    {
        builder.AppendLine("        await using (var command = connection.CreateCommand())");
        builder.AppendLine("        {");
        builder.Append("            command.CommandText = ").Append(Literal(string.Join("\n", method.Statements))).AppendLine(";");
        EmitParameters(builder, method.Parameters);
        builder.AppendLine("            var rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);");
        if (countRows)
        {
            builder.AppendLine("            return rows;");
        }

        builder.AppendLine("        }");
    }

    private static void EmitQuery(StringBuilder builder, GeneratedMethod method)
    {
        builder.AppendLine("        var results = new global::System.Collections.Generic.List<" + method.ResultType + ">();");
        builder.AppendLine("        await using (var command = connection.CreateCommand())");
        builder.AppendLine("        {");
        builder.Append("            command.CommandText = ").Append(Literal(string.Join("\n", method.Statements))).AppendLine(";");
        EmitParameters(builder, method.Parameters);
        builder.AppendLine("            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);");
        builder.AppendLine("            while (reader.FieldCount == 0)");
        builder.AppendLine("            {");
        builder.AppendLine("                if (!await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))");
        builder.AppendLine("                    throw new global::System.InvalidOperationException(\"The annotated SELECT did not return a result set.\");");
        builder.AppendLine("            }");
        if (method.QueryResultIndex > 0)
        {
            builder.Append("            for (var resultIndex = 0; resultIndex < ").Append(method.QueryResultIndex).AppendLine("; resultIndex++)");
            builder.AppendLine("            {");
            builder.AppendLine("                do");
            builder.AppendLine("                {");
            builder.AppendLine("                    if (!await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))");
            builder.AppendLine("                        throw new global::System.InvalidOperationException(\"The annotated SELECT did not return a result set.\");");
            builder.AppendLine("                } while (reader.FieldCount == 0);");
            builder.AppendLine("            }");
        }
        builder.AppendLine("            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))");
        builder.AppendLine("            {");
        builder.Append("                var item = (").Append(method.ResultType)
            .Append(")global::System.Activator.CreateInstance(typeof(").Append(method.ResultType).AppendLine("))!;");
        builder.AppendLine("                for (var column = 0; column < reader.FieldCount; column++)");
        builder.AppendLine("                {");
        builder.AppendLine("                    switch (column)");
        builder.AppendLine("                    {");
        for (var column = 0; column < method.ResultColumnNames.Length; column++)
        {
            if (method.ResultColumnNames[column] is not null)
            {
                builder.Append("                        case ").Append(column).AppendLine(":");
                var property = method.ResultProperties[column];
                var propertyAccess = "item." + EscapeIdentifier(property.Name);
                if (property.Type.IsReferenceType || IsNullableType(property.Type))
                {
                    builder.AppendLine("                            if (await reader.IsDBNullAsync(column, cancellationToken).ConfigureAwait(false))");
                    builder.Append("                                ").Append(propertyAccess).AppendLine(" = default!;");
                    builder.AppendLine("                            else");
                    builder.AppendLine("                            {");
                    builder.Append("                                ").Append(propertyAccess).Append(" = ")
                        .Append(PropertyConversion(property.Type, "reader.GetValue(column)")).AppendLine(";");
                    builder.AppendLine("                            }");
                }
                else
                {
                    builder.AppendLine("                            if (!await reader.IsDBNullAsync(column, cancellationToken).ConfigureAwait(false))");
                    builder.AppendLine("                            {");
                    builder.Append("                                ").Append(propertyAccess).Append(" = ")
                        .Append(PropertyConversion(property.Type, "reader.GetValue(column)")).AppendLine(";");
                    builder.AppendLine("                            }");
                }
                builder.AppendLine("                            break;");
            }
        }

        builder.AppendLine("                        default:");
        builder.AppendLine("                            throw new global::System.InvalidOperationException($\"The SELECT returned an unmapped column at ordinal {column}.\");");
        builder.AppendLine("                    }");
        builder.AppendLine("                }");
        builder.AppendLine("                results.Add(item);");
        builder.AppendLine("            }");
        builder.AppendLine("            while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false)) { }");
        builder.AppendLine("        }");
        builder.AppendLine("        return results.ToArray();");
        builder.AppendLine("    }");
        builder.AppendLine();
    }

    private static void EmitIdScalar(StringBuilder builder, GeneratedMethod method)
    {
        builder.AppendLine("        object? insertedValue;");
        builder.AppendLine("        await using (var command = connection.CreateCommand())");
        builder.AppendLine("        {");
        builder.Append("            command.CommandText = ").Append(Literal(string.Join("\n", method.Statements))).AppendLine(";");
        EmitParameters(builder, method.Parameters);
        builder.AppendLine("            insertedValue = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);");
        builder.AppendLine("        }");
        builder.AppendLine("        if (insertedValue is null || insertedValue is global::System.DBNull) throw new global::System.InvalidOperationException(\"The identity query did not return a value.\");");
        builder.Append("        var insertedId = ConvertResult<").Append(IdTypeName(method.IdType!))
            .AppendLine(">(insertedValue);");
    }

    private static void EmitParameters(StringBuilder builder, ImmutableArray<SqlParameter> parameters)
    {
        for (var index = 0; index < parameters.Length; index++)
        {
            var parameter = parameters[index];
            var localName = "sqlParameter_" + index;
            builder.Append("            var ").Append(localName).AppendLine(" = command.CreateParameter();");
            builder.Append("            ").Append(localName).Append(".ParameterName = ")
                .Append(Literal("@" + parameter.Name)).AppendLine(";");
            builder.Append("            ").Append(localName).Append(".Value = (object?)")
                .Append(EscapeIdentifier(parameter.Identifier)).AppendLine(" ?? global::System.DBNull.Value;");
            builder.Append("            command.Parameters.Add(").Append(localName).AppendLine(");");
        }
    }

    private static void EmitHelpers(StringBuilder builder)
    {
        builder.AppendLine("    private static T ConvertResult<T>(object value)");
        builder.AppendLine("    {");
        builder.AppendLine("        if (typeof(T) == typeof(global::System.Guid)) return (T)(object)global::System.Guid.Parse(global::System.Convert.ToString(value, global::System.Globalization.CultureInfo.InvariantCulture)!);");
        builder.AppendLine("        return (T)global::System.Convert.ChangeType(value, typeof(T), global::System.Globalization.CultureInfo.InvariantCulture)!;");
        builder.AppendLine("    }");
        builder.AppendLine();
    }

    private static string IdTypeName(string type) =>
        type.ToLowerInvariant() switch
        {
            "guid" => "global::System.Guid",
            "long" => "long",
            _ => "int"
        };

    private static string EscapeIdentifier(string name)
    {
        var identifier = SafeIdentifier(name);
        return SyntaxFacts.GetKeywordKind(identifier) != SyntaxKind.None ? "@" + identifier : identifier;
    }

    private static string SafeIdentifier(string name)
    {
        var identifier = new string(name.Select((character, index) =>
            index == 0
                ? (SyntaxFacts.IsIdentifierStartCharacter(character) ? character : '_')
                : (SyntaxFacts.IsIdentifierPartCharacter(character) ? character : '_')).ToArray());
        return string.IsNullOrEmpty(identifier) ? "_" : identifier;
    }

    private static string Literal(string value) =>
        SymbolDisplay.FormatLiteral(value, quote: true);

    private static void ReportInvalid(SourceProductionContext context, SqlFile file, int batchStart, string message) =>
        context.ReportDiagnostic(Diagnostic.Create(
            InvalidBatch, CreateLocation(file, batchStart), message));

    private static Location CreateLocation(SqlFile file, int offset)
    {
        offset = Math.Max(0, Math.Min(offset, file.Text.Length));
        var linePosition = SourceText.From(file.Text).Lines.GetLinePosition(offset);
        return Location.Create(
            file.Path,
            new TextSpan(offset, 0),
            new LinePositionSpan(linePosition, linePosition));
    }

    private sealed class VariableReferenceCollector : TSqlFragmentVisitor
    {
        public List<SqlVariable> Variables { get; } = [];

        public override void ExplicitVisit(VariableReference node) =>
            Variables.Add(new SqlVariable(node.Name, node.StartOffset));
    }

    private sealed record SqlFile(string Path, string Text);
    private sealed record SqlVariable(string Name, int Offset);
    private sealed record SqlParameter(string Name, string Type, string Identifier);
    private sealed record BatchBoundary(int Index, int Length);
    private sealed record GeneratedMethod(
        string Name,
        string[] Statements,
        ImmutableArray<SqlParameter> Parameters,
        MethodKind Kind,
        string? ResultType,
        string? IdType,
        int QueryResultIndex,
        string?[] ResultColumnNames)
    {
        public List<IPropertySymbol> ResultProperties { get; } = [];
    }

    private enum MethodKind
    {
        Query,
        InsertId,
        RowsAffected,
        NonQuery
    }

    private enum SqlLexicalState
    {
        Normal,
        SingleQuoted,
        DoubleQuoted,
        BracketQuoted,
        BlockComment
    }

    private sealed class IdentityExpressionCollector : TSqlFragmentVisitor
    {
        public bool Found { get; private set; }

        public override void ExplicitVisit(FunctionCall node)
        {
            if (node.FunctionName.Value.Equals("scope_identity", StringComparison.OrdinalIgnoreCase) ||
                node.FunctionName.Value.Equals("ident_current", StringComparison.OrdinalIgnoreCase) ||
                node.FunctionName.Value.Equals("last_insert_rowid", StringComparison.OrdinalIgnoreCase) ||
                node.FunctionName.Value.Equals("currval", StringComparison.OrdinalIgnoreCase))
            {
                Found = true;
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(GlobalVariableExpression node)
        {
            if (node.Name.Equals("@@identity", StringComparison.OrdinalIgnoreCase))
            {
                Found = true;
            }

            base.ExplicitVisit(node);
        }
    }
}
