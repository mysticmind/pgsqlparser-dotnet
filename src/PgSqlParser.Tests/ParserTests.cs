using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace PgSqlParser.Tests;

public class ParserTests
{
    private readonly ITestOutputHelper _testOutputHelper;

    public ParserTests(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
    }

    [Fact]
    public void Normalize()
    {
        var items = Utils.ReadLines("normalize_tests.txt").ToArray();
        for (var i = 0; i < items.Length; i += 2)
        {
            var query = items[i].Replace("\\n", "\n");
            var expected = items[i + 1].Replace("\\n", "\n");
            var result = Parser.Normalize(query);
            result.Value.ShouldBe(expected);
        }
    }
    
    [Fact]
    public void NormalizeUtility()
    {
        var items = Utils.ReadLines("normalize_utility_tests.txt").ToArray();
        for (var i = 0; i < items.Length; i += 2)
        {
            var query = items[i].Replace("\\n", "\n");
            var expected = items[i + 1].Replace("\\n", "\n");
            var result = Parser.NormalizeUtility(query);
            result.Value.ShouldBe(expected);
        }
    }
    
    [Fact]
    public void Parse()
    {
        var items = Utils.ReadLines("parse_tests.txt").ToArray();
        for (var i = 0; i < items.Length; i += 2)
        {
            var query = items[i].Replace("\\n", "\n");
            var expected = ParseResult.Parser.ParseJson(items[i + 1]);
            var result = Parser.Parse(query);
            result.Value.ShouldBeEquivalentTo(expected);
        }
    }
    
    [Fact]
    public void ParseWithOpts()
    {
        var items = Utils.ReadLines("parse_with_opts_tests.txt").ToArray();
        for (var i = 0; i < items.Length; i += 3)
        {
            var query = items[i];
            var opts = (ParserOptions)int.Parse(items[i + 1]);
            var expected = ParseResult.Parser.ParseJson(items[i + 2]);
            var result = Parser.Parse(query, opts);
            result.Value.ShouldBeEquivalentTo(expected);
        }
    }
    
    [Fact]
    public void DeParse()
    {
        var items = Utils.ReadLines("deparse_tests.txt").ToArray();
        for (var i = 0; i < items.Length; i += 1)
        {
            var query = items[i].Replace("\\n", "\n");
            var parseResult = Parser.Parse(query).Value.ShouldNotBeNull();
            var result = Parser.Deparse(parseResult);
            result.IsSuccess.ShouldBeTrue(query);

            // Comments are not part of the parse tree, so only comment-free queries round-trip exactly.
            if (!query.Contains("/*") && !query.Contains("--"))
                result.Value.ShouldBe(query);
        }
    }

    [Fact]
    public void DeParseWithComments()
    {
        var items = Utils.ReadLines("deparse_tests.txt").ToArray();
        for (var i = 0; i < items.Length; i += 1)
        {
            var query = items[i].Replace("\\n", "\n");
            var parseResult = Parser.Parse(query).Value.ShouldNotBeNull();
            var comments = (Parser.DeparseComments(query)).Value.ShouldNotBeNull();
            var result = Parser.Deparse(parseResult, new DeparseOptions { Comments = comments });

            // With its comments passed back in, every query round-trips exactly.
            result.Value.ShouldBe(query);
        }
    }

    [Fact]
    public void DeParsePrettyPrint()
    {
        var parseResult = Parser.Parse("SELECT a, b FROM t JOIN u ON t.id = u.id WHERE x = 1 AND y = 2 ORDER BY a").Value.ShouldNotBeNull();

        (Parser.Deparse(parseResult, new DeparseOptions { PrettyPrint = true })).Value
            .ShouldBe("SELECT a, b\nFROM\n    t\n    JOIN u ON t.id = u.id\nWHERE\n    x = 1\n    AND y = 2\nORDER BY a");
        (Parser.Deparse(parseResult, new DeparseOptions
        {
            PrettyPrint = true, IndentSize = 2, MaxLineLength = 10, TrailingNewline = true, CommasStartOfLine = true
        })).Value
            .ShouldBe("SELECT a , b\nFROM\n  t\n  JOIN u ON t.id = u.id\nWHERE\n  x = 1\n  AND y = 2\nORDER BY a\n");
    }

    [Fact]
    public void DeParseRejectsInvalidTree()
    {
        var parseResult = new ParseResult { Stmts = { new RawStmt() } };

        var result = Parser.Deparse(parseResult, new DeparseOptions());

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldNotBeNull().Message.ShouldNotBeNullOrEmpty();
    }
    
    [Fact]
    public void SplitWithScanner()
    {
        var items = Utils.ReadLines("split_tests.txt").ToArray();
        for (var i = 0; i < items.Length; i += 3)
        {
            var query = items[i].Replace("\\n", "\n");
            var expected = JsonSerializer.Deserialize<SplitResult>(items[i + 1]);
            var result = Parser.SplitWithScanner(query);
            result.Value.ShouldBeEquivalentTo(expected);
        }
    }
    
    [Fact]
    public void SplitWithParser()
    {
        var items = Utils.ReadLines("split_tests.txt").ToArray();
        for (var i = 0; i < items.Length; i += 3)
        {
            var query = items[i].Replace("\\n", "\n");
            var expected = JsonSerializer.Deserialize<SplitResult>(items[i + 2]);
            var result = Parser.SplitWithParser(query);
            result.Value.ShouldBeEquivalentTo(expected);
        }
    }
    
    [Fact]
    public void Scan()
    {
        var items = Utils.ReadLines("scan_tests.txt").ToArray();
        for (var i = 0; i < items.Length; i += 2)
        {
            var query = items[i].Replace("\\n", "\n");
            var expected = items[i + 1].Replace("\\n", "\n");
            var result = (Parser.Scan(query)).Value.ShouldNotBeNull();
            result.Version.ShouldBe(Parser.PgVersionNum);

            // Token text is sliced with the reported offsets, so this also checks them.
            var actual = string.Concat(result.Tokens.Select(t =>
                $"{query[t.Start..t.End]} = {Utils.ProtoName(t.Token)}, {Utils.ProtoName(t.KeywordKind)}\n"));
            actual.ShouldBe(expected);
        }
    }
    
    [Fact]
    public void ErrorCarriesFuncNameAndFileName()
    {
        var result = Parser.Parse("SELECT FROM WHERE");

        result.IsSuccess.ShouldBeFalse();
        var error = result.Error.ShouldNotBeNull();
        error.Message.ShouldNotBeNull().ShouldStartWith("syntax error");
        error.FuncName.ShouldBe("scanner_yyerror");
        error.FileName.ShouldBe("scan.l");
        error.CursorPos.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Fingerprint()
    {
        var items = Utils.ReadLines("fingerprint_tests.txt").ToArray();
        for (var i = 0; i < items.Length; i += 2)
        {
            var query = items[i].Replace("\\n", "\n");
            var expected = items[i + 1];
            var result = Parser.Fingerprint(query);
            result.Value.ShouldBeEquivalentTo(expected);
        }
    }

    [Fact]
    public void FingerprintWithOpts()
    {
        var items = Utils.ReadLines("fingerprint_with_opts_tests.txt").ToArray();
        for (var i = 0; i < items.Length; i += 3)
        {
            var query = items[i];
            var opts = (ParserOptions)int.Parse(items[i + 1]);
            var expected = items[i + 2];
            var result = Parser.Fingerprint(query, opts);
            result.Value.ShouldBeEquivalentTo(expected);
        }
    }

    [Fact]
    public void FingerprintWithFingerprintOptions()
    {
        var items = Utils.ReadLines("fingerprint_options_tests.txt").ToArray();
        for (var i = 0; i < items.Length; i += 3)
        {
            var query = items[i];
            var opts = (FingerprintOptions)int.Parse(items[i + 1]);
            var expected = items[i + 2];
            var result = Parser.Fingerprint(query, ParserOptions.Default, opts);
            result.Value.ShouldBe(expected);
        }
    }

    [Fact]
    public void IsUtilityStmt()
    {
        (Parser.IsUtilityStmt("SELECT 1")).Value.ShouldBe([false]);
        (Parser.IsUtilityStmt("SHOW fsync")).Value.ShouldBe([true]);
        (Parser.IsUtilityStmt("SELECT 1; SET fsync = off; UPDATE t SET a = 1")).Value.ShouldBe([false, true, false]);
        (Parser.IsUtilityStmt("SELECT FROM WHERE")).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void Summary()
    {
        var result = (Parser.Summary("WITH c AS (SELECT 1) SELECT lower(x.name) FROM public.test AS x JOIN c ON true WHERE x.a = 1")).Value.ShouldNotBeNull();

        result.Tables.Select(t => t.Name).ShouldBe(["public.test"]);
        result.Tables[0].SchemaName.ShouldBe("public");
        result.Tables[0].TableName.ShouldBe("test");
        result.Tables[0].Context.ShouldBe(SummaryResult.Types.Context.Select);
        result.Aliases["x"].ShouldBe("public.test");
        result.CteNames.ShouldBe(["c"]);
        result.Functions.Select(f => f.Name).ShouldBe(["lower"]);
        result.FilterColumns.Select(c => c.Column).ShouldBe(["a"]);
        result.StatementTypes.ShouldBe(["SelectStmt"]);
        result.TruncatedQuery.ShouldBeEmpty();
    }

    [Fact]
    public void SummaryWithTruncation()
    {
        var result = (Parser.Summary("SELECT a, b, c, d, e, f FROM test WHERE a = 1", ParserOptions.Default, 20)).Value.ShouldNotBeNull();

        result.TruncatedQuery.Length.ShouldBeLessThanOrEqualTo(20);
        result.TruncatedQuery.ShouldContain("...");
    }

    [Fact]
    public void ParsePlpgsql()
    {
        var sql = Utils.ReadFile("plpgsql_samples.sql");
        sql = sql.Replace("\r\n", "\n");
        var result = Parser.ParsePlpgsql(sql);
        var resultVal = result.GetValueOrThrow().Replace("\r\n", "\n");
        var expected = Utils.ReadFile("plpgsql_samples.expected.json");
        expected = expected.Replace("\r\n", "\n");
        resultVal.ShouldBe(expected);
    }
}
