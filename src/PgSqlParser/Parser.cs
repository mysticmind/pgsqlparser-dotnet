using System.Runtime.InteropServices;
using Google.Protobuf;
using PgSqlParser.Utils;

namespace PgSqlParser;

/// <summary>
/// <see cref="CursorPos"/> is a 1-based position in Unicode code points, as PostgreSQL reports it.
/// It is neither a UTF-8 byte offset nor a UTF-16 offset.
/// </summary>
public record Error(string? Message, string? FuncName, string? FileName, int LineNo, int CursorPos, string? Context);

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
    /// <returns></returns>
    public static Result<string> Fingerprint(string query,
        ParserOptions parserOptions = ParserOptions.Default)
    {
        var result = LibPgQuery.pg_query_fingerprint_opts(query, (int)parserOptions);
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
    /// Split a SQL script containing multiple statements into an array of clean, standalone SQL statements
    /// using lexical (token-based) analysis.
    /// </summary>
    /// <param name="query"></param>
    /// <returns></returns>
    public static Result<SplitResult> SplitWithScanner(string query)
    {
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
        var updatedBytes = parseResult.ToByteArray();
        LibPgQuery.PgQueryProtobuf parseTree;
        parseTree.len = (UIntPtr)updatedBytes.Length;
        parseTree.data = Marshal.AllocHGlobal(updatedBytes.Length);
        Marshal.Copy(updatedBytes, 0, parseTree.data, updatedBytes.Length);

        var deparseResult = LibPgQuery.pg_query_deparse_protobuf(parseTree);

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