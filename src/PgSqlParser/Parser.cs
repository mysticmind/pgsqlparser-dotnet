using System.Runtime.InteropServices;
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
}

public readonly struct Result<T>
{
    public T? Value { get; }
    public Error? Error { get; }
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
    public static Result<ParseResult?> Parse(string query, ParserOptions parserOptions = ParserOptions.Default)
    {
        if (InvalidQuery(query) is { } invalid)
            return Result<ParseResult?>.Failure(invalid);

        var result = LibPgQuery.pg_query_parse_protobuf_opts(query, (int)parserOptions);

        try
        {
            return result.error == IntPtr.Zero 
                ? Result<ParseResult?>.Success(ParseResult.Parser.ParseFrom(ReadProtobuf(result.parse_tree))) 
                : Result<ParseResult?>.Failure(ParseError(result.error));
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
    /// <returns></returns>
    public static Task<Result<ParseResult?>> ParseAsync(string query, ParserOptions parserOptions = ParserOptions.Default, CancellationToken cancellationToken = default)
    {
        return RunAsync(() => Parse(query, parserOptions), cancellationToken);
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
    /// Generate a normalized hash (fingerprint) of a SQL statement — ignoring literals, whitespace, and minor variations
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
    /// Async generate a normalized hash (fingerprint) of a SQL statement — ignoring literals, whitespace, and minor variations
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
    /// Async generate a normalized hash (fingerprint) of a SQL statement — ignoring literals, whitespace, and minor variations
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

        var updatedBytes = parseResult.ToByteArray();
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
                var comment = Marshal.PtrToStructure<LibPgQuery.PostgresDeparseComment>(commentPtr);
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
                var commentPtr = Marshal.AllocHGlobal(Marshal.SizeOf<LibPgQuery.PostgresDeparseComment>());
                Marshal.WriteIntPtr(array, i * IntPtr.Size, commentPtr);
                // Zero it first so a failure below leaves nothing dangling for FreeDeparseComments.
                Marshal.StructureToPtr(new LibPgQuery.PostgresDeparseComment(), commentPtr, false);
                Marshal.StructureToPtr(new LibPgQuery.PostgresDeparseComment
                {
                    match_location = comments[i].MatchLocation,
                    newlines_before_comment = comments[i].NewlinesBeforeComment,
                    newlines_after_comment = comments[i].NewlinesAfterComment,
                    str = Marshal.StringToCoTaskMemUTF8(comments[i].Text)
                }, commentPtr, false);
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

            Marshal.FreeCoTaskMem(Marshal.PtrToStructure<LibPgQuery.PostgresDeparseComment>(commentPtr).str);
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
            var stmt = Marshal.PtrToStructure<LibPgQuery.PgQuerySplitStmt>(stmtPtrPtr);

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

    private static byte[] ReadProtobuf(LibPgQuery.PgQueryProtobuf pbuf)
    {
        var len = checked((int)pbuf.len);
        var buffer = new byte[len];
        Marshal.Copy(pbuf.data, buffer, 0, len);
        return buffer;
    }

    private static Error ParseError(IntPtr errorPtr)
    {
        var error = Marshal.PtrToStructure<LibPgQuery.PgQueryError>(errorPtr);
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