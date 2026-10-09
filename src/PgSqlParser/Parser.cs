using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Google.Protobuf;
using PgSqlParser.Utils;

namespace PgSqlParser;

/// <summary>
/// <see cref="CursorPos"/> is a 1-based position in Unicode code points, as PostgreSQL reports it.
/// It is neither a UTF-8 byte offset nor a UTF-16 offset.
/// </summary>
public record Error(string? Message, string? FuncName, string? FileName, int LineNo, int CursorPos, string? Context)
{
    /// <summary>
    /// Converts <see cref="CursorPos"/> into a 0-based UTF-16 offset into <paramref name="query"/>, the
    /// query the error was reported for, so it can be used with <see cref="string.Substring(int)"/>.
    /// Returns -1 if the error has no cursor position or the position is outside the query.
    /// </summary>
    public int GetCursorCharOffset(string query)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (CursorPos <= 0)
            return -1;

        var charOffset = 0;
        for (var codePoints = CursorPos - 1; codePoints > 0; codePoints--)
        {
            if (charOffset >= query.Length)
                return -1;

            // A surrogate pair is one code point spanning two chars.
            charOffset += char.IsHighSurrogate(query[charOffset])
                          && charOffset + 1 < query.Length
                          && char.IsLowSurrogate(query[charOffset + 1])
                ? 2
                : 1;
        }

        return charOffset;
    }

    /// <summary>
    /// Returns the 1-based line and column of <see cref="CursorPos"/> in <paramref name="query"/>, or
    /// null if the error has no position in it. The column counts UTF-16 chars.
    /// </summary>
    public (int Line, int Column)? GetLineAndColumn(string query)
    {
        var offset = GetCursorCharOffset(query);
        if (offset < 0)
            return null;

        var line = 1;
        var lineStart = 0;
        for (var i = 0; i < offset; i++)
        {
            if (query[i] == '\n')
            {
                line++;
                lineStart = i + 1;
            }
        }

        return (line, offset - lineStart + 1);
    }

    /// <summary>
    /// Formats the error the way psql does: the message, then the line of <paramref name="query"/> it
    /// points at with a caret under the position.
    /// </summary>
    public string Format(string query)
    {
        var header = $"ERROR:  {Message}";
        if (GetLineAndColumn(query) is not var (line, column))
            return header;

        var offset = GetCursorCharOffset(query);
        var lineStart = offset - (column - 1);
        var lineEnd = query.IndexOf('\n', lineStart);
        // Tabs become spaces so the caret lines up whatever the tab width.
        var text = query[lineStart..(lineEnd < 0 ? query.Length : lineEnd)].TrimEnd('\r').Replace('\t', ' ');
        var prefix = $"LINE {line}: ";

        return $"{header}\n{prefix}{text}\n{new string(' ', prefix.Length + column - 1)}^";
    }
}

/// <summary>
/// Thrown by <see cref="Result{T}.GetValueOrThrow"/> when the call failed. <see cref="Error"/> has the details.
/// </summary>
public class PgSqlParserException : Exception
{
    public PgSqlParserException(Error error) : base(error.Message)
    {
        Error = error;
    }

    public Error Error { get; }
}

/// <summary>
/// The outcome of a parser call: either a <see cref="Value"/> or an <see cref="Error"/>.
/// </summary>
public readonly struct Result<T>
{
    /// <summary>The result of the call. Not null when <see cref="IsSuccess"/> is true.</summary>
    public T? Value { get; }

    /// <summary>Why the call failed. Not null when <see cref="IsSuccess"/> is false.</summary>
    public Error? Error { get; }

    [MemberNotNullWhen(true, nameof(Value))]
    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess { get; }

    private Result(T value)
    {
        Value = value;
        Error = null;
        IsSuccess = true;
    }

    private Result(Error error)
    {
        Value = default;
        Error = error;
        IsSuccess = false;
    }

    public static Result<T> Success(T value) => new(value);
    public static Result<T> Failure(Error error) => new(error);

    /// <summary>
    /// Returns the value, or throws a <see cref="PgSqlParserException"/> carrying the error if the call failed.
    /// </summary>
    public T GetValueOrThrow()
    {
        if (TryGetValue(out var value, out var error))
            return value;

        throw new PgSqlParserException(error);
    }

    /// <summary>
    /// Gets the value if the call succeeded.
    /// </summary>
    public bool TryGetValue([MaybeNullWhen(false)] out T value)
    {
        return TryGetValue(out value, out _);
    }

    /// <summary>
    /// Gets the value if the call succeeded, or the error if it failed.
    /// </summary>
    public bool TryGetValue([MaybeNullWhen(false)] out T value, [NotNullWhen(false)] out Error? error)
    {
        value = Value;
        // A default(Result<T>) has neither a value nor an error.
        error = IsSuccess ? null : Error ?? new Error("The result was not initialized", null, null, 0, 0, null);
        return IsSuccess;
    }

    /// <summary>
    /// Runs <paramref name="onSuccess"/> with the value or <paramref name="onFailure"/> with the error,
    /// and returns what it returns.
    /// </summary>
    public TResult Match<TResult>(Func<T, TResult> onSuccess, Func<Error, TResult> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);

        return TryGetValue(out var value, out var error) ? onSuccess(value) : onFailure(error);
    }

    /// <summary>
    /// Supports <c>var (value, error) = result;</c>. Exactly one of the two is not null.
    /// </summary>
    public void Deconstruct(out T? value, out Error? error)
    {
        value = Value;
        error = Error;
    }
}

public class SplitResult
{
    public List<SplitStmt> Statements { get; init; } = [];
}

/// <summary>
/// <see cref="Location"/> and <see cref="Length"/> are UTF-16 code unit offsets into the split query.
/// Parse tree locations use UTF-8 byte offsets; use <see cref="Utf8OffsetMapper"/> to convert them.
/// </summary>
public record SplitStmt(int Location, int Length, string Text);

[Flags]
public enum ParserOptions
{
    Default = 0,
    TypeName = 1,
    PlpgsqlExpr = 2,
    PlpgsqlAssign1 = 3,
    PlpgsqlAssign2 = 4,
    PlpgsqlAssign3 = 5,

    // Flags
    DisableBackslashQuote = 16,
    DisableStandardConformingStrings = 32,
    DisableEscapeStringWarning = 64
}

/// <summary>
/// A comment to re-insert when deparsing, as returned by <see cref="Parser.DeparseComments"/>.
/// </summary>
/// <param name="MatchLocation">
/// The comment is inserted before the first node whose location is equal to or higher than this.
/// Like parse tree locations, this is a UTF-8 byte offset into the query.
/// </param>
/// <param name="NewlinesBeforeComment">Newlines to insert before the comment.</param>
/// <param name="NewlinesAfterComment">Newlines to insert after the comment.</param>
/// <param name="Text">The comment text, including its start and end tokens.</param>
public record DeparseComment(int MatchLocation, int NewlinesBeforeComment, int NewlinesAfterComment, string Text);

/// <summary>
/// A parameter reference such as <c>$1</c>, found by <see cref="Parser.ParameterRefs"/>.
/// </summary>
/// <param name="Number">The parameter number: 1 for <c>$1</c>.</param>
/// <param name="Start">Where the reference starts in the query string, as a UTF-16 offset.</param>
/// <param name="End">Where it ends (exclusive), so <c>query[Start..End]</c> is the reference.</param>
public record ParameterRef(int Number, int Start, int End)
{
    /// <summary>
    /// The type the parameter is cast to in the query, as in <c>$1::int</c> or <c>CAST($1 AS int)</c>,
    /// or null if it is not cast or the query does not parse.
    /// </summary>
    public string? TypeName { get; init; }
}

/// <summary>
/// Options for <see cref="Parser.Deparse(ParseResult, DeparseOptions)"/>.
/// </summary>
public class DeparseOptions
{
    /// <summary>Comments to re-insert into the output, see <see cref="Parser.DeparseComments"/>.</summary>
    public IReadOnlyList<DeparseComment> Comments { get; init; } = [];

    /// <summary>Format the output across multiple indented lines.</summary>
    public bool PrettyPrint { get; init; }

    /// <summary>Indentation size in spaces when pretty printing.</summary>
    public int IndentSize { get; init; } = 4;

    /// <summary>Restricts the line length of certain lists of items when pretty printing.</summary>
    public int MaxLineLength { get; init; } = 80;

    /// <summary>Add a trailing newline at the end of the output when pretty printing.</summary>
    public bool TrailingNewline { get; init; }

    /// <summary>Place separating commas at the start of the line when pretty printing.</summary>
    public bool CommasStartOfLine { get; init; }
}

/// <summary>
/// Options for <see cref="Parser.Format"/>.
/// </summary>
public class FormatOptions
{
    /// <summary>Keep the comments of the query. On by default.</summary>
    public bool KeepComments { get; init; } = true;

    /// <summary>Indentation size in spaces.</summary>
    public int IndentSize { get; init; } = 4;

    /// <summary>Restricts the line length of certain lists of items.</summary>
    public int MaxLineLength { get; init; } = 80;

    /// <summary>Add a trailing newline at the end of the output.</summary>
    public bool TrailingNewline { get; init; }

    /// <summary>Place separating commas at the start of the line.</summary>
    public bool CommasStartOfLine { get; init; }
}

/// <summary>
/// Flags that control how fingerprints are calculated.
/// </summary>
[Flags]
public enum FingerprintOptions
{
    /// <summary>
    /// Follows Postgres 18+ query ID behavior: in SELECT/DML statements the alias name replaces the
    /// relation name when present, and schema names are ignored.
    /// </summary>
    Default = 0,

    /// <summary>Relation names are always fingerprinted, aliases are ignored.</summary>
    RangeVarIgnoreAliases = 1 << 0,

    /// <summary>Schema names are also fingerprinted in SELECT/DML statements.</summary>
    RangeVarIncludeSchema = 1 << 1,

    /// <summary>
    /// Matches how Postgres 17 and earlier calculate query IDs, and how libpg_query 17 and earlier
    /// calculated fingerprints.
    /// </summary>
    RangeVarPg17Compat = RangeVarIgnoreAliases | RangeVarIncludeSchema,

    /// <summary>
    /// Fingerprints the full relation name, instead of ignoring sequences of two or more digits
    /// (which groups queries on date/number-suffixed tables together).
    /// </summary>
    FullRelName = 1 << 4
}

public static class Parser
{
    public static string PgMajorVersion => LibPgQuery.PgMajorVersion;
    public static string PgVersion => LibPgQuery.PgVersion;
    public static int PgVersionNum => LibPgQuery.PgVersionNum;

    /// <summary>
    /// Transform DML query (SELECT, INSERT, UPDATE, DELETE) into a canonical form
    /// by replacing literal values (constants) with placeholders ($1, $2)
    /// </summary>
    /// <param name="query"></param>
    /// <returns></returns>
    public static Result<string> Normalize(string query)
    {
        if (InvalidQuery(query) is { } invalid)
            return Result<string>.Failure(invalid);

        var result = LibPgQuery.pg_query_normalize(query);

        try
        {
            return result.error == IntPtr.Zero
                ? Result<string>.Success(Marshal.PtrToStringUTF8(result.normalized_query) ?? string.Empty)
                : Result<string>.Failure(ParseError(result.error));
        }
        finally
        {
            LibPgQuery.pg_query_free_normalize_result(result);
        }
    }
    
    /// <summary>
    /// Async transform DML query (SELECT, INSERT, UPDATE, DELETE) into a canonical form
    /// by replacing literal values (constants) with placeholders ($1, $2)
    /// </summary>
    /// <param name="query"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static Task<Result<string>> NormalizeAsync(string query, CancellationToken cancellationToken = default)
    {
        return RunAsync(() => Normalize(query), cancellationToken);
    }

    /// <summary>
    /// Transform DDL and other utility commands (CREATE TABLE, ALTER TABLE, VACUUM, and ANALYZE et.al.)
    /// into a canonical form by replacing literal values (constants) with placeholders ($1, $2)
    /// </summary>
    /// <param name="query"></param>
    /// <returns></returns>
    public static Result<string> NormalizeUtility(string query)
    {
        if (InvalidQuery(query) is { } invalid)
            return Result<string>.Failure(invalid);

        var result = LibPgQuery.pg_query_normalize_utility(query);

        try
        {
            return result.error == IntPtr.Zero
                ? Result<string>.Success(Marshal.PtrToStringUTF8(result.normalized_query) ?? string.Empty)
                : Result<string>.Failure(ParseError(result.error));
        }
        finally
        {
            LibPgQuery.pg_query_free_normalize_result(result);
        }
    }

    /// <summary>
    /// Async transform DDL and other utility commands (CREATE TABLE, ALTER TABLE, VACUUM, and ANALYZE et.al.)
    /// into a canonical form by replacing literal values (constants) with placeholders ($1, $2)
    /// </summary>
    /// <param name="query"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static Task<Result<string>> NormalizeUtilityAsync(string query, CancellationToken cancellationToken = default)
    {
        return RunAsync(() => NormalizeUtility(query), cancellationToken);
    }

    /// <summary>
    /// Tokenize a query. Token <c>Start</c> and <c>End</c> are UTF-16 code unit offsets into <paramref name="query"/>.
    /// </summary>
    /// <param name="query"></param>
    /// <returns></returns>
    public static Result<ScanResult> Scan(string query)
    {
        if (InvalidQuery(query) is { } invalid)
            return Result<ScanResult>.Failure(invalid);

        var result = LibPgQuery.pg_query_scan(query);
        try
        {
            if (result.error != IntPtr.Zero)
                return Result<ScanResult>.Failure(ParseError(result.error));

            var scanResult = ScanResult.Parser.ParseFrom(ReadProtobuf(result.pbuf));
            var offsets = new Utf8OffsetMapper(query);
            foreach (var token in scanResult.Tokens)
            {
                token.Start = ToCharOffset(offsets, token.Start);
                token.End = ToCharOffset(offsets, token.End);
            }

            return Result<ScanResult>.Success(scanResult);
        }
        finally
        {
            LibPgQuery.pg_query_free_scan_result(result);
        }
    }
    
    /// <summary>
    /// Async tokenize a query
    /// </summary>
    /// <param name="query"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static Task<Result<ScanResult>> ScanAsync(string query, CancellationToken cancellationToken = default)
    {
        return RunAsync(() => Scan(query), cancellationToken);
    }
    
    /// <summary>
    /// Parse SQL and returns an AST. Locations in the parse tree are UTF-8 byte offsets into
    /// <paramref name="query"/>; use <see cref="Utf8OffsetMapper"/> to convert them to UTF-16 offsets.
    /// </summary>
    /// <param name="query"></param>
    /// <param name="parserOptions"></param>
    /// <returns></returns>
    public static Result<ParseResult> Parse(string query, ParserOptions parserOptions = ParserOptions.Default)
    {
        if (InvalidQuery(query) is { } invalid)
            return Result<ParseResult>.Failure(invalid);

        var result = LibPgQuery.pg_query_parse_protobuf_opts(query, (int)parserOptions);

        try
        {
            if (result.error != IntPtr.Zero)
                return Result<ParseResult>.Failure(ParseError(result.error));

            // Read through DeepProtobuf, so a deeply nested tree cannot overflow the stack.
            var tree = DeepProtobuf.Parse(ParseResult.Parser, ParseResult.Descriptor, ReadProtobuf(result.parse_tree));
            return tree is null
                ? Result<ParseResult>.Failure(TooDeepError())
                : Result<ParseResult>.Success(tree);
        }
        finally
        {
            LibPgQuery.pg_query_free_protobuf_parse_result(result);
        }
    }
    
    /// <summary>
    /// Async parse SQL and returns an AST 
    /// </summary>
    /// <param name="query"></param>
    /// <param name="parserOptions"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static Task<Result<ParseResult>> ParseAsync(string query, ParserOptions parserOptions = ParserOptions.Default, CancellationToken cancellationToken = default)
    {
        return RunAsync(() => Parse(query, parserOptions), cancellationToken);
    }
    
    /// <summary>
    /// Parse a single expression on its own, such as a column default, a CHECK condition or what
    /// <c>pg_get_expr</c> returns. Anything that is not exactly one expression is an error. Locations
    /// in the returned node are not relative to <paramref name="expression"/>.
    /// </summary>
    /// <param name="expression"></param>
    /// <returns></returns>
    public static Result<Node> ParseExpression(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);

        // In a WHERE clause there is room for exactly one expression, with no alias and no list.
        // The line break lets the expression end in a line comment.
        var parsed = Parse($"SELECT WHERE {expression}\n");
        if (!parsed.TryGetValue(out var tree, out var error))
            return Result<Node>.Failure(error);

        if (tree.Stmts.Count == 1
            && tree.Stmts[0].Stmt?.SelectStmt is { WhereClause: { } value } select
            && select.Equals(new SelectStmt { WhereClause = value, LimitOption = select.LimitOption, Op = SetOperation.SetopNone }))
        {
            return Result<Node>.Success(value);
        }

        return Result<Node>.Failure(NodeError("not a single expression"));
    }

    /// <summary>
    /// Parse a type name on its own, such as <c>numeric(10,2)</c>, <c>text[]</c> or <c>public.my_type</c>.
    /// </summary>
    /// <param name="typeName"></param>
    /// <returns></returns>
    public static Result<TypeName> ParseTypeName(string typeName)
    {
        ArgumentNullException.ThrowIfNull(typeName);

        var parsed = Parse($"SELECT NULL::{typeName}");
        if (!parsed.TryGetValue(out var tree, out var error))
            return Result<TypeName>.Failure(error);

        if (tree.Stmts.Count == 1
            && tree.Stmts[0].Stmt?.SelectStmt is { TargetList.Count: 1 } select
            && select.TargetList[0].ResTarget is { Name: "" } target
            && target.Val?.TypeCast is { TypeName: { } type, Arg.AConst.Isnull: true }
            && IsBareSelect(select))
        {
            return Result<TypeName>.Success(type);
        }

        return Result<TypeName>.Failure(NodeError("not a single type name"));
    }

    // True if the SELECT has a select list and nothing else.
    private static bool IsBareSelect(SelectStmt select)
    {
        var bare = new SelectStmt { LimitOption = select.LimitOption, Op = select.Op };
        bare.TargetList.Add(select.TargetList);
        return bare.Equals(select);
    }

    /// <summary>
    /// Parse PL/pgSQL function bodies and returns a JSON representation
    /// </summary>
    /// <param name="query"></param>
    /// <returns></returns>
    public static Result<string> ParsePlpgsql(string query)
    {
        if (InvalidQuery(query) is { } invalid)
            return Result<string>.Failure(invalid);

        var result = LibPgQuery.pg_query_parse_plpgsql(query);
        
        try
        {
            return result.error == IntPtr.Zero 
                ? Result<string>.Success(Marshal.PtrToStringUTF8(result.plpgsql_funcs) ?? string.Empty) 
                : Result<string>.Failure(ParseError(result.error));
        }
        finally
        {
            LibPgQuery.pg_query_free_plpgsql_parse_result(result);
        }
    }

    /// <summary>
    /// Async parse PL/pgSQL function bodies and returns a JSON representation
    /// </summary>
    /// <param name="query"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static Task<Result<string>> ParsePlpgsqlAsync(string query, CancellationToken cancellationToken = default)
    {
        return RunAsync(() => ParsePlpgsql(query), cancellationToken);
    }

    /// <summary>
    /// Parse PL/pgSQL function bodies into objects. Each <c>CREATE FUNCTION</c> and <c>DO</c> statement
    /// in <paramref name="query"/> gives one <see cref="PlpgsqlFunction"/>, with its variables, its
    /// statements and the SQL they contain. <see cref="ParsePlpgsql"/> returns the same data as a JSON string.
    /// </summary>
    /// <param name="query"></param>
    /// <returns></returns>
    public static Result<IReadOnlyList<PlpgsqlFunction>> ParsePlpgsqlFunctions(string query)
    {
        var json = ParsePlpgsql(query);
        if (!json.TryGetValue(out var text, out var error))
            return Result<IReadOnlyList<PlpgsqlFunction>>.Failure(error);

        // Clone detaches the elements from the pooled document, so nothing needs disposing later.
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(RepairPlpgsqlJson(text));
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Result<IReadOnlyList<PlpgsqlFunction>>.Failure(
                new Error("libpg_query returned PL/pgSQL JSON that could not be read", null, null, 0, 0, null));
        }

        var functions = new List<PlpgsqlFunction>();
        foreach (var element in root.EnumerateArray())
        {
            if (PlpgsqlNode.TryCreate(element, out var node))
                functions.Add(new PlpgsqlFunction(node));
        }

        return Result<IReadOnlyList<PlpgsqlFunction>>.Success(functions);
    }

    /// <summary>
    /// libpg_query 18.1.0 writes a data item it has no output for (the TG_* variables of trigger
    /// functions) as <c>{}}</c> where <c>{}</c> is meant, which is not valid JSON. This drops the
    /// stray brace. A valid <c>{}}</c>, an empty object closing its parent, always follows a colon,
    /// while the broken one is an array element and so follows a comma or an opening bracket.
    /// </summary>
    private static string RepairPlpgsqlJson(string json)
    {
        const string broken = "{}}";
        if (!json.Contains(broken, StringComparison.Ordinal))
            return json;

        var repaired = new System.Text.StringBuilder(json.Length);
        var inString = false;
        var previous = '\0';
        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];
            if (inString)
            {
                repaired.Append(c);
                if (c == '\\')
                    repaired.Append(json[++i]);
                else if (c == '"')
                {
                    inString = false;
                    previous = c;
                }

                continue;
            }

            if (c == '{' && (previous is ',' or '[') && string.CompareOrdinal(json, i, broken, 0, broken.Length) == 0)
            {
                repaired.Append("{}");
                i += broken.Length - 1;
                previous = '}';
                continue;
            }

            if (c == '"')
                inString = true;
            if (!char.IsWhiteSpace(c))
                previous = c;
            repaired.Append(c);
        }

        return repaired.ToString();
    }

    /// <summary>
    /// Async parse PL/pgSQL function bodies into objects
    /// </summary>
    /// <param name="query"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static Task<Result<IReadOnlyList<PlpgsqlFunction>>> ParsePlpgsqlFunctionsAsync(string query,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(() => ParsePlpgsqlFunctions(query), cancellationToken);
    }

    /// <summary>
    /// Generate a normalized hash (fingerprint) of a SQL statement, ignoring literals, whitespace, and minor variations
    /// </summary>
    /// <param name="query"></param>
    /// <param name="parserOptions"></param>
    /// <param name="fingerprintOptions"></param>
    /// <returns></returns>
    public static Result<string> Fingerprint(string query,
        ParserOptions parserOptions = ParserOptions.Default,
        FingerprintOptions fingerprintOptions = FingerprintOptions.Default)
    {
        if (InvalidQuery(query) is { } invalid)
            return Result<string>.Failure(invalid);

        var result = LibPgQuery.pg_query_fingerprint_opts(query, (int)parserOptions, (int)fingerprintOptions);
        try
        {
            return result.error == IntPtr.Zero
                ? Result<string>.Success(Marshal.PtrToStringUTF8(result.fingerprint_str) ?? string.Empty)
                : Result<string>.Failure(ParseError(result.error));
        }
        finally
        {
            LibPgQuery.pg_query_free_fingerprint_result(result);
        }
    }
    
    /// <summary>
    /// Async generate a normalized hash (fingerprint) of a SQL statement, ignoring literals, whitespace, and minor variations
    /// </summary>
    /// <param name="query"></param>
    /// <param name="parserOptions"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static Task<Result<string>> FingerprintAsync(string query,
        ParserOptions parserOptions = ParserOptions.Default,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(() => Fingerprint(query, parserOptions), cancellationToken);
    }

    /// <summary>
    /// Async generate a normalized hash (fingerprint) of a SQL statement, ignoring literals, whitespace, and minor variations
    /// </summary>
    /// <param name="query"></param>
    /// <param name="parserOptions"></param>
    /// <param name="fingerprintOptions"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static Task<Result<string>> FingerprintAsync(string query,
        ParserOptions parserOptions,
        FingerprintOptions fingerprintOptions,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(() => Fingerprint(query, parserOptions, fingerprintOptions), cancellationToken);
    }

    /// <summary>
    /// Check whether each statement in a query is a utility statement (DDL and other commands that are
    /// not SELECT, INSERT, UPDATE, DELETE or MERGE). Returns one entry per statement.
    /// </summary>
    /// <param name="query"></param>
    /// <returns></returns>
    public static Result<bool[]> IsUtilityStmt(string query)
    {
        if (InvalidQuery(query) is { } invalid)
            return Result<bool[]>.Failure(invalid);

        var result = LibPgQuery.pg_query_is_utility_stmt(query);
        try
        {
            if (result.error != IntPtr.Zero)
                return Result<bool[]>.Failure(ParseError(result.error));

            var items = new bool[result.length];
            for (var i = 0; i < items.Length; i++)
                items[i] = Marshal.ReadByte(result.items, i) != 0;

            return Result<bool[]>.Success(items);
        }
        finally
        {
            LibPgQuery.pg_query_free_is_utility_result(result);
        }
    }

    /// <summary>
    /// Async check whether each statement in a query is a utility statement
    /// </summary>
    /// <param name="query"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static Task<Result<bool[]>> IsUtilityStmtAsync(string query, CancellationToken cancellationToken = default)
    {
        return RunAsync(() => IsUtilityStmt(query), cancellationToken);
    }

    /// <summary>
    /// Summarize a query: the tables, aliases, CTE names, functions, filter columns and statement types
    /// it references, and optionally a truncated version of the query text.
    /// </summary>
    /// <param name="query"></param>
    /// <param name="parserOptions"></param>
    /// <param name="truncateLimit">
    /// Maximum length of <c>TruncatedQuery</c> in the result, or -1 to skip truncation.
    /// </param>
    /// <returns></returns>
    public static Result<SummaryResult> Summary(string query,
        ParserOptions parserOptions = ParserOptions.Default,
        int truncateLimit = -1)
    {
        if (InvalidQuery(query) is { } invalid)
            return Result<SummaryResult>.Failure(invalid);

        var result = LibPgQuery.pg_query_summary(query, (int)parserOptions, truncateLimit);
        try
        {
            return result.error == IntPtr.Zero
                ? Result<SummaryResult>.Success(SummaryResult.Parser.ParseFrom(ReadProtobuf(result.summary)))
                : Result<SummaryResult>.Failure(ParseError(result.error));
        }
        finally
        {
            LibPgQuery.pg_query_free_summary_parse_result(result);
        }
    }

    /// <summary>
    /// Async summarize a query
    /// </summary>
    /// <param name="query"></param>
    /// <param name="parserOptions"></param>
    /// <param name="truncateLimit"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static Task<Result<SummaryResult>> SummaryAsync(string query,
        ParserOptions parserOptions = ParserOptions.Default,
        int truncateLimit = -1,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(() => Summary(query, parserOptions, truncateLimit), cancellationToken);
    }

    /// <summary>
    /// Split a SQL script containing multiple statements into an array of clean, standalone SQL statements
    /// using lexical (token-based) analysis.
    /// </summary>
    /// <param name="query"></param>
    /// <returns></returns>
    public static Result<SplitResult> SplitWithScanner(string query)
    {
        if (InvalidQuery(query) is { } invalid)
            return Result<SplitResult>.Failure(invalid);

        var result = LibPgQuery.pg_query_split_with_scanner(query);

        try
        {
            if (result.error != IntPtr.Zero)
                return Result<SplitResult>.Failure(ParseError(result.error));

            return BuildSplitResult(query, result.stmts, result.n_stmts);
        }
        finally
        {
            LibPgQuery.pg_query_free_split_result(result);
        }
    }

    /// <summary>
    /// Async split a SQL script containing multiple statements into an array of clean, standalone SQL statements
    /// using lexical (token-based) analysis.
    /// </summary>
    /// <param name="query"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static Task<Result<SplitResult>> SplitWithScannerAsync(string query,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(() => SplitWithScanner(query), cancellationToken);
    }
    
    /// <summary>
    /// Split a SQL script containing multiple statements into an array of clean, standalone SQL statements
    /// using Postgres full parser
    /// </summary>
    /// <param name="query"></param>
    /// <returns></returns>
    public static Result<SplitResult> SplitWithParser(string query)
    {
        if (InvalidQuery(query) is { } invalid)
            return Result<SplitResult>.Failure(invalid);

        var result = LibPgQuery.pg_query_split_with_parser(query);

        try
        {
            if (result.error != IntPtr.Zero)
                return Result<SplitResult>.Failure(ParseError(result.error));

            return BuildSplitResult(query, result.stmts, result.n_stmts);
        }
        finally
        {
            LibPgQuery.pg_query_free_split_result(result);
        }
    }

    /// <summary>
    /// Async split a SQL script containing multiple statements into an array of clean, standalone SQL statements
    /// using Postgres full parser
    /// </summary>
    /// <param name="query"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static Task<Result<SplitResult>> SplitWithParserAsync(string query,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(() => SplitWithParser(query), cancellationToken);
    }

    /// <summary>
    /// Deparse AST back into a query string
    /// </summary>
    /// <param name="parseResult"></param>
    /// <returns></returns>
    public static Result<string> Deparse(ParseResult parseResult)
    {
        return Deparse(parseResult, null);
    }

    /// <summary>
    /// Deparse AST back into a query string, optionally pretty printed and with comments re-inserted
    /// </summary>
    /// <param name="parseResult"></param>
    /// <param name="options"></param>
    /// <returns></returns>
    public static Result<string> Deparse(ParseResult parseResult, DeparseOptions? options)
    {
        ArgumentNullException.ThrowIfNull(parseResult);

        // Written through DeepProtobuf, so a deeply nested tree cannot overflow the stack.
        var updatedBytes = DeepProtobuf.ToByteArray(parseResult);
        if (updatedBytes is null)
            return Result<string>.Failure(TooDeepError());

        LibPgQuery.PgQueryProtobuf parseTree;
        parseTree.len = (UIntPtr)updatedBytes.Length;
        parseTree.data = Marshal.AllocHGlobal(updatedBytes.Length);
        var comments = IntPtr.Zero;
        var commentCount = 0;

        try
        {
            Marshal.Copy(updatedBytes, 0, parseTree.data, updatedBytes.Length);

            LibPgQuery.PgQueryDeparseResult deparseResult;
            if (options is null)
            {
                deparseResult = LibPgQuery.pg_query_deparse_protobuf(parseTree);
            }
            else
            {
                commentCount = options.Comments.Count;
                comments = AllocDeparseComments(options.Comments);
                deparseResult = LibPgQuery.pg_query_deparse_protobuf_opts(parseTree, new LibPgQuery.PostgresDeparseOpts
                {
                    comments = comments,
                    comment_count = (UIntPtr)commentCount,
                    pretty_print = options.PrettyPrint ? (byte)1 : (byte)0,
                    indent_size = options.IndentSize,
                    max_line_length = options.MaxLineLength,
                    trailing_newline = options.TrailingNewline ? (byte)1 : (byte)0,
                    commas_start_of_line = options.CommasStartOfLine ? (byte)1 : (byte)0
                });
            }

            try
            {
                return deparseResult.error != IntPtr.Zero
                    ? Result<string>.Failure(ParseError(deparseResult.error))
                    : Result<string>.Success(Marshal.PtrToStringUTF8(deparseResult.query) ?? string.Empty);
            }
            finally
            {
                LibPgQuery.pg_query_free_deparse_result(deparseResult);
            }
        }
        finally
        {
            FreeDeparseComments(comments, commentCount);
            Marshal.FreeHGlobal(parseTree.data);
        }
    }

    /// <summary>
    /// Format a query: parse it and print it again across indented lines, keeping its comments.
    /// This is <see cref="Parse"/>, <see cref="DeparseComments"/> and <see cref="Deparse(ParseResult, DeparseOptions)"/>
    /// with pretty printing in one call.
    /// </summary>
    /// <param name="query"></param>
    /// <param name="options"></param>
    /// <returns></returns>
    public static Result<string> Format(string query, FormatOptions? options = null)
    {
        options ??= new FormatOptions();

        // Each statement is formatted on its own and they are joined one per paragraph. The scanner
        // split keeps the comments in front of a statement together with it.
        if (!SplitWithScanner(query).TryGetValue(out var split, out var error))
            return Result<string>.Failure(error);

        var formatted = new List<string>();
        var end = 0;
        foreach (var statement in split.Statements)
        {
            if (!FormatStatement(statement.Text.Trim(), options).TryGetValue(out var text, out error))
                return Result<string>.Failure(error);

            if (text.Length > 0)
                formatted.Add(text.TrimEnd('\n'));

            end = statement.Location + statement.Length;
        }

        var result = string.Join(";\n\n", formatted);
        if (formatted.Count > 1)
            result += ";";

        // What follows the last statement can only be comments.
        var tail = query[end..].Trim().TrimStart(';').Trim();
        if (options.KeepComments && tail.Length > 0)
            result = result.Length == 0 ? tail : $"{result}\n\n{tail}";

        return Result<string>.Success(options.TrailingNewline && result.Length > 0 ? result + "\n" : result);
    }

    private static Result<string> FormatStatement(string statement, FormatOptions options)
    {
        if (!Parse(statement).TryGetValue(out var tree, out var error))
            return Result<string>.Failure(error);

        // Only comments, or nothing at all.
        if (tree.Stmts.Count == 0)
            return Result<string>.Success(options.KeepComments ? statement : string.Empty);

        IReadOnlyList<DeparseComment> comments = [];
        if (options.KeepComments)
        {
            if (!DeparseComments(statement).TryGetValue(out var found, out error))
                return Result<string>.Failure(error);

            comments = found;
        }

        return Deparse(tree, new DeparseOptions
        {
            Comments = comments,
            PrettyPrint = true,
            IndentSize = options.IndentSize,
            MaxLineLength = options.MaxLineLength,
            CommasStartOfLine = options.CommasStartOfLine
        });
    }

    /// <summary>
    /// Async format a query
    /// </summary>
    /// <param name="query"></param>
    /// <param name="options"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static Task<Result<string>> FormatAsync(string query, FormatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(() => Format(query, options), cancellationToken);
    }

    /// <summary>
    /// Classify each statement of a query: its kind, whether it is read-only, and facts such as a
    /// data-modifying CTE or a locking clause. See <see cref="StatementInfo"/>.
    /// </summary>
    /// <param name="query"></param>
    /// <returns></returns>
    public static Result<IReadOnlyList<StatementInfo>> Classify(string query)
    {
        return Parse(query).TryGetValue(out var tree, out var error)
            ? Result<IReadOnlyList<StatementInfo>>.Success(tree.Classify())
            : Result<IReadOnlyList<StatementInfo>>.Failure(error);
    }

    /// <summary>
    /// Async classify each statement of a query
    /// </summary>
    /// <param name="query"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static Task<Result<IReadOnlyList<StatementInfo>>> ClassifyAsync(string query,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(() => Classify(query), cancellationToken);
    }

    /// <summary>
    /// Deparse a single node of a parse tree back into SQL, for example only a WHERE clause, one
    /// expression or one table reference. Supported are statements, expressions, items of a FROM
    /// clause (<see cref="RangeVar"/>, <see cref="JoinExpr"/>, subselects, functions), select list
    /// items (<see cref="ResTarget"/>), ORDER BY items (<see cref="SortBy"/>), WITH clauses and their
    /// common table expressions, and type names. Other nodes, such as a bare <see cref="String"/> or
    /// <see cref="Alias"/>, return an error. The node is not modified.
    /// </summary>
    /// <param name="node">A node of a parse tree, a <see cref="Node"/> wrapper, a <see cref="RawStmt"/> or a whole <see cref="ParseResult"/>.</param>
    /// <returns></returns>
    public static Result<string> DeparseNode(IMessage node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (node is Node wrapper)
        {
            return wrapper.Unwrap() is { } inner
                ? DeparseNode(inner)
                : Result<string>.Failure(NodeError("an empty Node cannot be deparsed"));
        }

        switch (node)
        {
            case ParseResult parseResult:
                return Deparse(parseResult);
            case RawStmt rawStmt:
                return Deparse(new ParseResult { Version = PgVersionNum, Stmts = { rawStmt } });
        }

        if (!ParseTreeExtensions.NodeFields.Value.ContainsKey(node.Descriptor.FullName))
            return Result<string>.Failure(NodeError($"a {node.Descriptor.Name} cannot be deparsed on its own"));

        var wrapped = node.AsNode();

        if (node.Descriptor.Name.EndsWith("Stmt", StringComparison.Ordinal))
            return Deparse(new ParseResult { Version = PgVersionNum, Stmts = { new RawStmt { Stmt = wrapped } } });

        // Anything else is deparsed inside a minimal SELECT, and the SELECT around it is cut off again.
        var select = new SelectStmt { LimitOption = LimitOption.Default, Op = SetOperation.SetopNone };
        string prefix;
        var suffix = "";
        switch (node)
        {
            case WithClause withClause:
                select.WithClause = withClause;
                prefix = "";
                suffix = " SELECT";
                break;
            case CommonTableExpr:
                select.WithClause = new WithClause { Ctes = { wrapped } };
                prefix = "WITH ";
                suffix = " SELECT";
                break;
            case TypeName typeName:
                select.WhereClause = new Node
                {
                    TypeCast = new TypeCast { Arg = new Node { AConst = new A_Const { Isnull = true } }, TypeName = typeName }
                };
                prefix = "SELECT WHERE NULL::";
                break;
            case RangeVar or JoinExpr or RangeSubselect or RangeFunction or RangeTableSample or RangeTableFunc:
                select.FromClause.Add(wrapped);
                prefix = "SELECT FROM ";
                break;
            case ResTarget:
                select.TargetList.Add(wrapped);
                prefix = "SELECT ";
                break;
            case SortBy:
                select.SortClause.Add(wrapped);
                prefix = "SELECT ORDER BY ";
                break;
            default:
                select.WhereClause = wrapped;
                prefix = "SELECT WHERE ";
                break;
        }

        var result = Deparse(new ParseResult
        {
            Version = PgVersionNum,
            Stmts = { new RawStmt { Stmt = new Node { SelectStmt = select } } }
        });
        // A node that fits none of these positions makes the deparser fail or give something else.
        if (!result.TryGetValue(out var sql)
            || !sql.StartsWith(prefix, StringComparison.Ordinal)
            || !sql.EndsWith(suffix, StringComparison.Ordinal))
        {
            return Result<string>.Failure(NodeError($"a {node.Descriptor.Name} cannot be deparsed on its own"));
        }

        return Result<string>.Success(sql[prefix.Length..^suffix.Length]);
    }

    private static Error NodeError(string message) => new(message, null, null, 0, 0, null);

    /// <summary>
    /// Find the parameter references (<c>$1</c>, <c>$2</c>, ...) in a query, in the order they appear.
    /// A reference inside a string literal or a comment is not a parameter and is not returned.
    /// When a parameter is cast, as in <c>$1::int</c>, its <see cref="ParameterRef.TypeName"/> is set.
    /// </summary>
    /// <param name="query"></param>
    /// <returns></returns>
    public static Result<IReadOnlyList<ParameterRef>> ParameterRefs(string query)
    {
        var scan = Scan(query);
        if (!scan.TryGetValue(out var scanResult, out var error))
            return Result<IReadOnlyList<ParameterRef>>.Failure(error);

        var parameters = new List<ParameterRef>();
        foreach (var token in scanResult.Tokens)
        {
            // A PARAM token is a dollar sign followed by digits.
            if (token.Token == Token.Param
                && int.TryParse(query.AsSpan(token.Start + 1, token.End - token.Start - 1), out var number))
            {
                parameters.Add(new ParameterRef(number, token.Start, token.End));
            }
        }

        return Result<IReadOnlyList<ParameterRef>>.Success(WithCastTypes(query, parameters));
    }

    // The cast type needs the parse tree. A query that scans but does not parse keeps its parameters,
    // only without types.
    private static IReadOnlyList<ParameterRef> WithCastTypes(string query, List<ParameterRef> parameters)
    {
        if (parameters.Count == 0 || !Parse(query).TryGetValue(out var tree))
            return parameters;

        // Tree locations are UTF-8 byte offsets, the parameters' Start is a string offset.
        var mapper = new Utf8OffsetMapper(query);
        var typeByStart = new Dictionary<int, string>();
        foreach (var cast in tree.Descendants<TypeCast>())
        {
            if (cast.Arg?.Unwrap() is ParamRef { Location: >= 0 } parameter
                && cast.TypeName is not null
                && mapper.TryToCharOffset(parameter.Location, out var start)
                && DeparseNode(cast.TypeName).TryGetValue(out var typeName))
            {
                typeByStart[start] = typeName;
            }
        }

        return typeByStart.Count == 0
            ? parameters
            : parameters.Select(p => typeByStart.TryGetValue(p.Start, out var type) ? p with { TypeName = type } : p).ToList();
    }

    /// <summary>
    /// Async find the parameter references in a query
    /// </summary>
    /// <param name="query"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static Task<Result<IReadOnlyList<ParameterRef>>> ParameterRefsAsync(string query,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(() => ParameterRefs(query), cancellationToken);
    }

    /// <summary>
    /// Async deparse AST back into a query string 
    /// </summary>
    /// <param name="parseResult"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static Task<Result<string>> DeparseAsync(ParseResult parseResult,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(() => Deparse(parseResult), cancellationToken);
    }

    /// <summary>
    /// Async deparse AST back into a query string, optionally pretty printed and with comments re-inserted
    /// </summary>
    /// <param name="parseResult"></param>
    /// <param name="options"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static Task<Result<string>> DeparseAsync(ParseResult parseResult,
        DeparseOptions? options,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(() => Deparse(parseResult, options), cancellationToken);
    }

    /// <summary>
    /// Extract the comments of a query, to pass to <see cref="Deparse(ParseResult, DeparseOptions)"/>
    /// through <see cref="DeparseOptions.Comments"/>. Comments are not part of the parse tree, so this
    /// is how they survive a parse and deparse round trip.
    /// </summary>
    /// <param name="query"></param>
    /// <returns></returns>
    public static Result<IReadOnlyList<DeparseComment>> DeparseComments(string query)
    {
        if (InvalidQuery(query) is { } invalid)
            return Result<IReadOnlyList<DeparseComment>>.Failure(invalid);

        var result = LibPgQuery.pg_query_deparse_comments_for_query(query);
        try
        {
            if (result.error != IntPtr.Zero)
                return Result<IReadOnlyList<DeparseComment>>.Failure(ParseError(result.error));

            var count = checked((int)result.comment_count);
            var comments = new List<DeparseComment>(count);
            for (var i = 0; i < count; i++)
            {
                var commentPtr = Marshal.ReadIntPtr(result.comments, i * IntPtr.Size);
                var comment = ReadStruct<LibPgQuery.PostgresDeparseComment>(commentPtr);
                comments.Add(new DeparseComment(
                    comment.match_location,
                    comment.newlines_before_comment,
                    comment.newlines_after_comment,
                    Marshal.PtrToStringUTF8(comment.str) ?? string.Empty));
            }

            return Result<IReadOnlyList<DeparseComment>>.Success(comments);
        }
        finally
        {
            LibPgQuery.pg_query_free_deparse_comments_result(result);
        }
    }

    /// <summary>
    /// Async extract the comments of a query
    /// </summary>
    /// <param name="query"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static Task<Result<IReadOnlyList<DeparseComment>>> DeparseCommentsAsync(string query,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(() => DeparseComments(query), cancellationToken);
    }

    private static IntPtr AllocDeparseComments(IReadOnlyList<DeparseComment> comments)
    {
        if (comments.Count == 0)
            return IntPtr.Zero;

        if (comments.Any(c => c?.Text is null || c.Text.Contains('\0')))
            throw new ArgumentException("Deparse comments must have text without NUL characters.", nameof(comments));

        var array = Marshal.AllocHGlobal(comments.Count * IntPtr.Size);
        for (var i = 0; i < comments.Count; i++)
            Marshal.WriteIntPtr(array, i * IntPtr.Size, IntPtr.Zero);

        try
        {
            for (var i = 0; i < comments.Count; i++)
            {
                var commentPtr = Marshal.AllocHGlobal(Unsafe.SizeOf<LibPgQuery.PostgresDeparseComment>());
                Marshal.WriteIntPtr(array, i * IntPtr.Size, commentPtr);
                // Zero it first so a failure below leaves nothing dangling for FreeDeparseComments.
                WriteStruct(commentPtr, new LibPgQuery.PostgresDeparseComment());
                WriteStruct(commentPtr, new LibPgQuery.PostgresDeparseComment
                {
                    match_location = comments[i].MatchLocation,
                    newlines_before_comment = comments[i].NewlinesBeforeComment,
                    newlines_after_comment = comments[i].NewlinesAfterComment,
                    str = Marshal.StringToCoTaskMemUTF8(comments[i].Text)
                });
            }
        }
        catch
        {
            FreeDeparseComments(array, comments.Count);
            throw;
        }

        return array;
    }

    private static void FreeDeparseComments(IntPtr array, int count)
    {
        if (array == IntPtr.Zero)
            return;

        for (var i = 0; i < count; i++)
        {
            var commentPtr = Marshal.ReadIntPtr(array, i * IntPtr.Size);
            if (commentPtr == IntPtr.Zero)
                continue;

            Marshal.FreeCoTaskMem(ReadStruct<LibPgQuery.PostgresDeparseComment>(commentPtr).str);
            Marshal.FreeHGlobal(commentPtr);
        }

        Marshal.FreeHGlobal(array);
    }
    
    /// <summary>
    /// Converts libpg_query's UTF-8 byte offsets into UTF-16 offsets of <paramref name="query"/>.
    /// </summary>
    private static Result<SplitResult> BuildSplitResult(string query, IntPtr stmts, int nStmts)
    {
        var offsets = new Utf8OffsetMapper(query);
        var splitResult = new SplitResult();

        for (var i = 0; i < nStmts; i++)
        {
            var stmtPtrPtr = Marshal.ReadIntPtr(stmts, i * IntPtr.Size);
            var stmt = ReadStruct<LibPgQuery.PgQuerySplitStmt>(stmtPtrPtr);

            var charStart = ToCharOffset(offsets, stmt.stmt_location);
            var charEnd = ToCharOffset(offsets, stmt.stmt_location + stmt.stmt_len);
            if (charEnd < charStart)
                throw new InvalidOperationException($"libpg_query returned negative statement length {stmt.stmt_len}.");

            splitResult.Statements.Add(
                new SplitStmt(charStart, charEnd - charStart, query.Substring(charStart, charEnd - charStart)));
        }

        return Result<SplitResult>.Success(splitResult);
    }

    /// <summary>
    /// libpg_query only reports offsets on character boundaries of the query it was given, so a failure
    /// here is a broken invariant rather than a query error.
    /// </summary>
    private static int ToCharOffset(Utf8OffsetMapper offsets, int byteOffset)
    {
        if (!offsets.TryToCharOffset(byteOffset, out var charOffset))
            throw new InvalidOperationException($"libpg_query returned UTF-8 byte offset {byteOffset}, which does not map to the query.");

        return charOffset;
    }

    /// <summary>
    /// libpg_query reads the query as a C string, so a null query would crash the process and a NUL
    /// character would silently cut the query short. PostgreSQL itself does not allow NUL in queries.
    /// </summary>
    private static Error? InvalidQuery(string query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var index = query.IndexOf('\0');
        if (index < 0)
            return null;

        // 1-based position in code points, like the cursor positions PostgreSQL reports.
        // A surrogate pair is one code point, so its second half is not counted.
        var cursorPos = index + 1;
        for (var i = 0; i < index; i++)
        {
            if (char.IsLowSurrogate(query[i]))
                cursorPos--;
        }

        return new Error("query contains a NUL character", null, null, 0, cursorPos, null);
    }

    // Plain pointer reads and writes of blittable structs, in place of the reflection-based
    // Marshal.PtrToStructure and Marshal.StructureToPtr, which do not work with Native AOT.
    private static unsafe T ReadStruct<T>(IntPtr ptr) where T : unmanaged => *(T*)ptr;

    private static unsafe void WriteStruct<T>(IntPtr ptr, T value) where T : unmanaged => *(T*)ptr = value;

    private static Error TooDeepError() =>
        new($"parse tree is nested more than {DeepProtobuf.MaxDepth} levels deep", null, null, 0, 0, null);

    private static byte[] ReadProtobuf(LibPgQuery.PgQueryProtobuf pbuf)
    {
        var len = checked((int)pbuf.len);
        var buffer = new byte[len];
        Marshal.Copy(pbuf.data, buffer, 0, len);
        return buffer;
    }

    private static Error ParseError(IntPtr errorPtr)
    {
        var error = ReadStruct<LibPgQuery.PgQueryError>(errorPtr);
        return new Error(
            Marshal.PtrToStringUTF8(error.message),
            Marshal.PtrToStringUTF8(error.funcname),
            Marshal.PtrToStringUTF8(error.filename),
            error.lineno,
            error.cursorpos,
            Marshal.PtrToStringUTF8(error.context));
    }
    
    private static Task<Result<T>> RunAsync<T>(Func<Result<T>> fn, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return fn();
        }, cancellationToken);
    }
}