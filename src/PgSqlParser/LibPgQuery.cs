using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PgSqlParser;

// Source-generated P/Invoke (LibraryImport), so the bindings work with trimming and Native AOT.
// Every struct here is blittable and is passed to and returned from libpg_query by value.
internal static partial class LibPgQuery
{
    private const string DllName = "libpg_query";

    [StructLayout(LayoutKind.Sequential)]
    public struct PgQueryError
    {
        public IntPtr message;
        public IntPtr funcname;
        public IntPtr filename;
        public int lineno;
        public int cursorpos;
        public IntPtr context;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PgQueryProtobuf
    {
        public UIntPtr len;
        public IntPtr data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PgQueryScanResult
    {
        public PgQueryProtobuf pbuf;
        public IntPtr stderr_buffer;
        public IntPtr error;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PgQueryParseResult
    {
        public IntPtr parse_tree;
        public IntPtr stderr_buffer;
        public IntPtr error;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PgQueryProtobufParseResult
    {
        public PgQueryProtobuf parse_tree;
        public IntPtr stderr_buffer;
        public IntPtr error;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PgQuerySplitStmt
    {
        public int stmt_location;
        public int stmt_len;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PgQuerySplitResult
    {
        public IntPtr stmts; // PgQuerySplitStmt**
        public int n_stmts;
        public IntPtr stderr_buffer;
        public IntPtr error;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PgQueryDeparseResult
    {
        public IntPtr query;
        public IntPtr error;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PgQueryPlpgsqlParseResult
    {
        public IntPtr plpgsql_funcs;
        public IntPtr error;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PgQueryFingerprintResult
    {
        public ulong fingerprint;
        public IntPtr fingerprint_str;
        public IntPtr stderr_buffer;
        public IntPtr error;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PgQueryNormalizeResult
    {
        public IntPtr normalized_query;
        public IntPtr error;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    public struct PostgresDeparseComment
    {
        public int match_location;
        public int newlines_before_comment;
        public int newlines_after_comment;
        public IntPtr str;
    }

    // C bool fields are declared as byte to keep the struct blittable.
    [StructLayout(LayoutKind.Sequential)]
    public struct PostgresDeparseOpts
    {
        public IntPtr comments; // PostgresDeparseComment**
        public UIntPtr comment_count;
        public byte pretty_print;
        public int indent_size;
        public int max_line_length;
        public byte trailing_newline;
        public byte commas_start_of_line;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PgQueryDeparseCommentsResult
    {
        public IntPtr comments; // PostgresDeparseComment**
        public UIntPtr comment_count;
        public IntPtr error;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PgQueryIsUtilityResult
    {
        public int length;
        public IntPtr items; // bool*
        public IntPtr error;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PgQuerySummaryParseResult
    {
        public PgQueryProtobuf summary;
        public IntPtr stderr_buffer;
        public IntPtr error;
    }

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial PgQueryNormalizeResult pg_query_normalize(string input);
    
    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial PgQueryNormalizeResult pg_query_normalize_utility(string input);
    
    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial PgQueryScanResult pg_query_scan(string input);

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial PgQueryParseResult pg_query_parse(string input);

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial PgQueryParseResult pg_query_parse_opts(string input, int parser_options);
   
    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial PgQueryProtobufParseResult pg_query_parse_protobuf(string input);

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial PgQueryProtobufParseResult pg_query_parse_protobuf_opts(string input, int parser_options);

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial PgQueryPlpgsqlParseResult pg_query_parse_plpgsql(string input);
  
    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial PgQueryFingerprintResult pg_query_fingerprint(string input);
 
    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial PgQueryFingerprintResult pg_query_fingerprint_opts(string input, int parser_options, int fingerprint_options);

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial PgQuerySplitResult pg_query_split_with_scanner(string input);
   
    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial PgQuerySplitResult pg_query_split_with_parser(string input);
   
    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial PgQueryDeparseResult pg_query_deparse_protobuf(PgQueryProtobuf parse_tree);

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial PgQueryDeparseResult pg_query_deparse_protobuf_opts(PgQueryProtobuf parse_tree, PostgresDeparseOpts opts);

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial PgQueryDeparseCommentsResult pg_query_deparse_comments_for_query(string query);
   
    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial PgQueryIsUtilityResult pg_query_is_utility_stmt(string query);

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial PgQuerySummaryParseResult pg_query_summary(string input, int parser_options, int truncate_limit);

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void pg_query_free_normalize_result(PgQueryNormalizeResult result);
   
    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void pg_query_free_scan_result(PgQueryScanResult result);
   
    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void pg_query_free_parse_result(PgQueryParseResult result);
   
    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void pg_query_free_split_result(PgQuerySplitResult result);
   
    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void pg_query_free_deparse_result(PgQueryDeparseResult result);

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void pg_query_free_deparse_comments_result(PgQueryDeparseCommentsResult result);
   
    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void pg_query_free_protobuf_parse_result(PgQueryProtobufParseResult result);
   
    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void pg_query_free_plpgsql_parse_result(PgQueryPlpgsqlParseResult result);
   
    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void pg_query_free_fingerprint_result(PgQueryFingerprintResult result);

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void pg_query_free_is_utility_result(PgQueryIsUtilityResult result);

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void pg_query_free_summary_parse_result(PgQuerySummaryParseResult result);

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void pg_query_exit();

    public const string PgMajorVersion = "18";
    public const string PgVersion = "18.6";
    public const int PgVersionNum = 180006;
}

