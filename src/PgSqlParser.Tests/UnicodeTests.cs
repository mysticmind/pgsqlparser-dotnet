using PgSqlParser.Utils;
using Shouldly;
using Xunit;

namespace PgSqlParser.Tests;

/// <summary>
/// Non-ASCII input through every API. 'û' and 'é' are 2 UTF-8 bytes and 1 UTF-16 char,
/// '😀' is 4 UTF-8 bytes and 2 UTF-16 chars (a surrogate pair).
/// </summary>
public class UnicodeTests
{
    private const string Query = "SELECT 'Coût', '😀' FROM \"données\" WHERE \"é\" = 'x'";

    [Fact]
    public void Normalize()
    {
        Parser.Normalize(Query).Value.ShouldBe("SELECT $1, $2 FROM \"données\" WHERE \"é\" = $3");
    }

    [Fact]
    public void NormalizeUtility()
    {
        Parser.NormalizeUtility("CREATE ROLE \"rôle😀\" PASSWORD 'mot de passé'").Value
            .ShouldBe("CREATE ROLE \"rôle😀\" PASSWORD $1");
    }

    [Fact]
    public void ParseKeepsTextAndReportsByteLocations()
    {
        var select = Parser.Parse(Query).Value.ShouldNotBeNull().Stmts[0].Stmt.SelectStmt;

        select.TargetList[0].ResTarget.Val.AConst.Sval.Sval.ShouldBe("Coût");
        select.TargetList[1].ResTarget.Val.AConst.Sval.Sval.ShouldBe("😀");
        var table = select.FromClause[0].RangeVar;
        table.Relname.ShouldBe("données");
        select.WhereClause.AExpr.Lexpr.ColumnRef.Fields[0].String.Sval.ShouldBe("é");

        // Parse tree locations are UTF-8 byte offsets; the mapper turns them into string offsets.
        var mapper = new Utf8OffsetMapper(Query);
        table.Location.ShouldBe(28);
        mapper.ToCharOffset(table.Location).ShouldBe(Query.IndexOf("\"données\"", StringComparison.Ordinal));
        var column = select.WhereClause.AExpr.Lexpr.ColumnRef;
        mapper.ToCharOffset(column.Location).ShouldBe(Query.IndexOf("\"é\"", StringComparison.Ordinal));
    }

    [Fact]
    public void DeparseRoundTrips()
    {
        var parseResult = Parser.Parse(Query).Value.ShouldNotBeNull();

        Parser.Deparse(parseResult).Value.ShouldBe(Query);
    }

    [Fact]
    public void DeparsePrettyPrint()
    {
        var parseResult = Parser.Parse("SELECT a, \"é😀\" FROM t WHERE x = 1 AND y = 'Coût'").Value.ShouldNotBeNull();

        Parser.Deparse(parseResult, new DeparseOptions { PrettyPrint = true }).Value
            .ShouldBe("SELECT a, \"é😀\"\nFROM t\nWHERE\n    x = 1\n    AND y = 'Coût'");
    }

    [Fact]
    public void DeparseComments()
    {
        const string query = "/* é😀 début */ SELECT 'Coût', /* 😀 milieu */2 -- fin é";

        var comments = Parser.DeparseComments(query).Value.ShouldNotBeNull();

        comments.Select(c => c.Text).ShouldBe(["/* é😀 début */", "/* 😀 milieu */", "-- fin é"]);
        // Match locations are UTF-8 byte offsets, like parse tree locations. The second comment
        // matches right after the comma that precedes it: byte 35, which is char 30.
        var mapper = new Utf8OffsetMapper(query);
        comments[1].MatchLocation.ShouldBe(35);
        mapper.ToCharOffset(comments[1].MatchLocation).ShouldBe(query.IndexOf(',') + 1);

        var parseResult = Parser.Parse(query).Value.ShouldNotBeNull();
        Parser.Deparse(parseResult, new DeparseOptions { Comments = comments }).Value.ShouldBe(query);
    }

    [Fact]
    public void Fingerprint()
    {
        var fingerprint = Parser.Fingerprint(Query).Value.ShouldNotBeNull();

        // Literals are ignored, identifiers are not.
        Parser.Fingerprint(Query.Replace("Coût", "Prix").Replace("😀", "x")).Value.ShouldBe(fingerprint);
        Parser.Fingerprint(Query.Replace("données", "données😀")).Value.ShouldNotBe(fingerprint);
        Parser.Fingerprint(Query, ParserOptions.Default, FingerprintOptions.RangeVarPg17Compat).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Scan()
    {
        var tokens = Parser.Scan(Query).Value.ShouldNotBeNull().Tokens;

        tokens.Select(t => Query[t.Start..t.End]).ShouldBe(
            ["SELECT", "'Coût'", ",", "'😀'", "FROM", "\"données\"", "WHERE", "\"é\"", "=", "'x'"]);
    }

    [Fact]
    public void Split()
    {
        const string query = "SELECT '😀' AS \"é\"; SELECT 'Coût'";

        Parser.SplitWithScanner(query).Value.ShouldNotBeNull().Statements.Select(s => s.Text)
            .ShouldBe(["SELECT '😀' AS \"é\"", " SELECT 'Coût'"]);
        var statements = Parser.SplitWithParser(query).Value.ShouldNotBeNull().Statements;
        statements.Select(s => s.Text).ShouldBe(["SELECT '😀' AS \"é\"", "SELECT 'Coût'"]);
        statements.ShouldAllBe(s => query.Substring(s.Location, s.Length) == s.Text);
    }

    [Fact]
    public void IsUtilityStmt()
    {
        Parser.IsUtilityStmt("SELECT 'é😀'; CREATE TABLE \"données😀\" (\"é\" int)").Value.ShouldBe([false, true]);
    }

    [Fact]
    public void Summary()
    {
        var result = Parser.Summary(Query).Value.ShouldNotBeNull();

        result.Tables.Single().TableName.ShouldBe("données");
        result.FilterColumns.Single().Column.ShouldBe("é");
    }

    [Theory]
    [InlineData(20, "SELECT ... FROM \"...")]
    [InlineData(24, "SELECT ... FROM \"donn...")]
    [InlineData(28, "SELECT ... FROM \"données\"...")]
    public void SummaryTruncatesOnCharacterBoundaries(int truncateLimit, string expected)
    {
        const string query = "SELECT \"é😀a\", \"é😀b\", \"é😀c\" FROM \"données\" WHERE \"é\" = 'Coût 😀 Coût 😀'";

        var result = Parser.Summary(query, ParserOptions.Default, truncateLimit).Value.ShouldNotBeNull();

        result.TruncatedQuery.ShouldBe(expected);
    }

    [Fact]
    public void ParsePlpgsql()
    {
        var json = Parser.ParsePlpgsql(
            "CREATE FUNCTION \"fonction😀\"() RETURNS text AS $$ DECLARE \"é\" text := 'Coût 😀'; BEGIN RETURN \"é\"; END; $$ LANGUAGE plpgsql")
            .Value.ShouldNotBeNull();

        json.ShouldContain("\"refname\":\"é\"");
        json.ShouldContain("\"query\":\"'Coût 😀'\"");
    }

    [Fact]
    public void ErrorMessageAndCursorPos()
    {
        const string query = "SELECT 'Coût', '😀' FROM WHERE";

        var error = Parser.Parse(query).Error.ShouldNotBeNull();

        error.Message.ShouldBe("syntax error at or near \"WHERE\"");
        // CursorPos is 1-based and counts code points: the emoji counts once, not as 2 chars or 4 bytes.
        var codePointsBefore = query[..query.IndexOf("WHERE", StringComparison.Ordinal)].EnumerateRunes().Count();
        error.CursorPos.ShouldBe(codePointsBefore + 1);
    }

    [Fact]
    public void ErrorMessageKeepsNonAsciiText()
    {
        var error = Parser.Scan("SELECT 'é😀").Error.ShouldNotBeNull();

        error.Message.ShouldBe("unterminated quoted string at or near \"'é😀\"");
    }

    [Fact]
    public async Task AsyncVariants()
    {
        (await Parser.NormalizeAsync(Query)).Value.ShouldBe(Parser.Normalize(Query).Value);
        (await Parser.FingerprintAsync(Query)).Value.ShouldBe(Parser.Fingerprint(Query).Value);
        (await Parser.ScanAsync(Query)).Value.ShouldBe(Parser.Scan(Query).Value);
        (await Parser.SplitWithParserAsync(Query)).Value!.Statements.ShouldBe(Parser.SplitWithParser(Query).Value!.Statements);
        (await Parser.SummaryAsync(Query)).Value.ShouldBe(Parser.Summary(Query).Value);
        (await Parser.IsUtilityStmtAsync(Query)).Value.ShouldBe([false]);
        var parseResult = (await Parser.ParseAsync(Query)).Value.ShouldNotBeNull();
        (await Parser.DeparseAsync(parseResult)).Value.ShouldBe(Query);
    }
}
