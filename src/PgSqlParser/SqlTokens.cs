namespace PgSqlParser;

/// <summary>
/// What a token of a query is, for syntax highlighting and similar uses.
/// </summary>
public enum SqlTokenKind
{
    Other,
    Keyword,
    Identifier,
    StringLiteral,
    NumericLiteral,
    Parameter,
    Operator,
    Punctuation,
    Comment
}

/// <summary>
/// A token of a query.
/// </summary>
/// <param name="Start">Where it starts in the query string, as a UTF-16 offset.</param>
/// <param name="End">Where it ends (exclusive), so <c>query[Start..End]</c> is its text.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Token">The scanner's own token, for finer distinctions.</param>
/// <param name="KeywordKind">For a keyword, how reserved it is.</param>
public readonly record struct SqlToken(int Start, int End, SqlTokenKind Kind, Token Token, KeywordKind KeywordKind);

internal static class SqlTokenClassifier
{
    public static SqlTokenKind KindOf(Token token, KeywordKind keywordKind)
    {
        if (keywordKind != KeywordKind.NoKeyword)
            return SqlTokenKind.Keyword;

        return token switch
        {
            Token.Ident or Token.Uident => SqlTokenKind.Identifier,
            Token.Sconst or Token.Usconst or Token.Bconst or Token.Xconst => SqlTokenKind.StringLiteral,
            Token.Iconst or Token.Fconst => SqlTokenKind.NumericLiteral,
            Token.Param => SqlTokenKind.Parameter,
            Token.SqlComment or Token.CComment => SqlTokenKind.Comment,
            Token.Op or Token.Typecast or Token.DotDot or Token.ColonEquals or Token.EqualsGreater or Token.LessEquals
                or Token.GreaterEquals or Token.NotEquals => SqlTokenKind.Operator,
            // + - * / % ^ < > = and the lone $ are operators.
            Token.Ascii37 or Token.Ascii42 or Token.Ascii43 or Token.Ascii45 or Token.Ascii47 or Token.Ascii60
                or Token.Ascii61 or Token.Ascii62 or Token.Ascii94 => SqlTokenKind.Operator,
            // ( ) , . : ; [ ] and the rest.
            Token.Ascii36 or Token.Ascii40 or Token.Ascii41 or Token.Ascii44 or Token.Ascii46 or Token.Ascii58
                or Token.Ascii59 or Token.Ascii63 or Token.Ascii91 or Token.Ascii92 or Token.Ascii93 => SqlTokenKind.Punctuation,
            _ => SqlTokenKind.Other
        };
    }
}
