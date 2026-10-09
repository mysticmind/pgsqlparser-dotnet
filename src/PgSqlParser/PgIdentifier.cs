using System.Collections.Concurrent;
using System.Text;

namespace PgSqlParser;

/// <summary>
/// Quoting of identifiers (table, column, schema names and so on) for use in SQL text, following the
/// rules of PostgreSQL's own <c>quote_ident</c>.
/// </summary>
public static class PgIdentifier
{
    // Whether a name that is otherwise safe is a keyword that must be quoted. Only names that are
    // already safe by their characters get here, so the cache stays small.
    private static readonly ConcurrentDictionary<string, bool> KeywordNeedsQuoting = new();

    /// <summary>
    /// Returns true if <paramref name="name"/> must be written in double quotes: it has characters other
    /// than lower case ASCII letters, digits and underscores, starts with a digit, or is a keyword that
    /// cannot be used as a name as it is.
    /// </summary>
    public static bool NeedsQuoting(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (name.Length == 0 || !(name[0] is >= 'a' and <= 'z' or '_'))
            return true;

        foreach (var c in name)
        {
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_'))
                return true;
        }

        return KeywordNeedsQuoting.GetOrAdd(name, static candidate =>
        {
            // The scanner knows every keyword of this PostgreSQL version and its category. Like
            // quote_ident, only unreserved keywords are left unquoted.
            var scan = Parser.Scan(candidate);
            return !scan.IsSuccess
                   || scan.Value.Tokens.Count != 1
                   || scan.Value.Tokens[0].KeywordKind is not (KeywordKind.NoKeyword or KeywordKind.UnreservedKeyword);
        });
    }

    /// <summary>
    /// Returns <paramref name="name"/> ready to use in SQL: unchanged if it is safe, otherwise in double
    /// quotes with any double quote inside it doubled.
    /// </summary>
    public static string Quote(string name)
    {
        return NeedsQuoting(name) ? "\"" + name.Replace("\"", "\"\"") + "\"" : name;
    }

    /// <summary>
    /// Returns a qualified name such as <c>schema.table</c>, quoting each part that needs it.
    /// </summary>
    public static string Quote(params string[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            if (builder.Length > 0)
                builder.Append('.');
            builder.Append(Quote(part));
        }

        return builder.ToString();
    }
}
