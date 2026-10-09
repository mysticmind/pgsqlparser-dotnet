using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace PgSqlParser.Tests;

public class ParserAsyncTests
{
    private IEnumerable<string> ReadLines(string path)
    {
        using var reader = new StreamReader(path);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }
    
    [Fact]
    public async Task Normalize()
    {
        var items = Utils.ReadLines("normalize_tests.txt").ToArray();
        for (var i = 0; i < items.Length; i += 2)
        {
            var query = items[i].Replace("\\n", "\n");
            var expected = items[i + 1].Replace("\\n", "\n");
            var result = await Parser.NormalizeAsync(query);
            result.Value.ShouldBe(expected);
        }
    }
    
    [Fact]
    public async Task NormalizeUtility()
    {
        var items = Utils.ReadLines("normalize_utility_tests.txt").ToArray();
        for (var i = 0; i < items.Length; i += 2)
        {
            var query = items[i].Replace("\\n", "\n");
            var expected = items[i + 1].Replace("\\n", "\n");
            var result = await Parser.NormalizeUtilityAsync(query);
            result.Value.ShouldBe(expected);
        }
    }
    
    [Fact]
    public async Task Parse()
    {
        var items = Utils.ReadLines("parse_tests.txt").ToArray();
        for (var i = 0; i < items.Length; i += 2)
        {
            var query = items[i].Replace("\\n", "\n");
            var expected = ParseResult.Parser.ParseJson(items[i + 1]);
            var result = await Parser.ParseAsync(query);
            result.Value.ShouldBeEquivalentTo(expected);
        }
    }

    [Fact]
    public async Task ParseWithOpts()
    {
        var items = Utils.ReadLines("parse_with_opts_tests.txt").ToArray();
        for (var i = 0; i < items.Length; i += 3)
        {
            var query = items[i];
            var opts = (ParserOptions)int.Parse(items[i + 1]);
            var expected = ParseResult.Parser.ParseJson(items[i + 2]);
            var result = await Parser.ParseAsync(query, opts);
            result.Value.ShouldBeEquivalentTo(expected);
        }
    }
    
    [Fact]
    public async Task DeParse()
    {
        var items = Utils.ReadLines("deparse_tests.txt").ToArray();
        for (var i = 0; i < items.Length; i += 1)
        {
            var query = items[i].Replace("\\n", "\n");
            var parseResult = Parser.Parse(query).Value.ShouldNotBeNull();
            var result = await Parser.DeparseAsync(parseResult);
            result.IsSuccess.ShouldBeTrue(query);

            // Comments are not part of the parse tree, so only comment-free queries round-trip exactly.
            if (!query.Contains("/*") && !query.Contains("--"))
                result.Value.ShouldBe(query);
        }
    }
    
    [Fact]
    public async Task SplitWithScanner()
    {
        var items = Utils.ReadLines("split_tests.txt").ToArray();
        for (var i = 0; i < items.Length; i += 3)
        {
            var query = items[i].Replace("\\n", "\n");
            var expected = JsonSerializer.Deserialize<SplitResult>(items[i + 1]);
            var result = await Parser.SplitWithScannerAsync(query);
            result.Value.ShouldBeEquivalentTo(expected);
        }
    }
    
    [Fact]
    public async Task SplitWithParser()
    {
        var items = Utils.ReadLines("split_tests.txt").ToArray();
        for (var i = 0; i < items.Length; i += 3)
        {
            var query = items[i].Replace("\\n", "\n");
            var expected = JsonSerializer.Deserialize<SplitResult>(items[i + 2]);
            var result = await Parser.SplitWithParserAsync(query);
            result.Value.ShouldBeEquivalentTo(expected);
        }
    }
    
    [Fact]
    public async Task Scan()
    {
        var items = Utils.ReadLines("scan_tests.txt").ToArray();
        for (var i = 0; i < items.Length; i += 2)
        {
            var query = items[i].Replace("\\n", "\n");
            var expected = items[i + 1].Replace("\\n", "\n");
            var result = (await Parser.ScanAsync(query)).Value.ShouldNotBeNull();
            result.Version.ShouldBe(Parser.PgVersionNum);

            // Token text is sliced with the reported offsets, so this also checks them.
            var actual = string.Concat(result.Tokens.Select(t =>
                $"{query[t.Start..t.End]} = {Utils.ProtoName(t.Token)}, {Utils.ProtoName(t.KeywordKind)}\n"));
            actual.ShouldBe(expected);
        }
    }
    
    [Fact]
    public async Task Fingerprint()
    {
        var items = Utils.ReadLines("fingerprint_tests.txt").ToArray();
        for (var i = 0; i < items.Length; i += 2)
        {
            var query = items[i].Replace("\\n", "\n");
            var expected = items[i + 1];
            var result = await Parser.FingerprintAsync(query);
            result.Value.ShouldBeEquivalentTo(expected);
        }
    }

    [Fact]
    public async Task FingerprintWithOpts()
    {
        var items = Utils.ReadLines("fingerprint_with_opts_tests.txt").ToArray();
        for (var i = 0; i < items.Length; i += 3)
        {
            var query = items[i];
            var opts = (ParserOptions)int.Parse(items[i + 1]);
            var expected = items[i + 2];
            var result = await Parser.FingerprintAsync(query, opts);
            result.Value.ShouldBeEquivalentTo(expected);
        }
    }

    [Fact]
    public async Task FingerprintWithFingerprintOptions()
    {
        var items = Utils.ReadLines("fingerprint_options_tests.txt").ToArray();
        for (var i = 0; i < items.Length; i += 3)
        {
            var query = items[i];
            var opts = (FingerprintOptions)int.Parse(items[i + 1]);
            var expected = items[i + 2];
            var result = await Parser.FingerprintAsync(query, ParserOptions.Default, opts);
            result.Value.ShouldBe(expected);
        }
    }

    [Fact]
    public async Task IsUtilityStmt()
    {
        (await Parser.IsUtilityStmtAsync("SELECT 1")).Value.ShouldBe([false]);
        (await Parser.IsUtilityStmtAsync("SHOW fsync")).Value.ShouldBe([true]);
        (await Parser.IsUtilityStmtAsync("SELECT 1; SET fsync = off; UPDATE t SET a = 1")).Value.ShouldBe([false, true, false]);
        (await Parser.IsUtilityStmtAsync("SELECT FROM WHERE")).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task Summary()
    {
        var result = (await Parser.SummaryAsync("WITH c AS (SELECT 1) SELECT lower(x.name) FROM public.test AS x JOIN c ON true WHERE x.a = 1")).Value.ShouldNotBeNull();

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
    public async Task SummaryWithTruncation()
    {
        var result = (await Parser.SummaryAsync("SELECT a, b, c, d, e, f FROM test WHERE a = 1", ParserOptions.Default, 20)).Value.ShouldNotBeNull();

        result.TruncatedQuery.Length.ShouldBeLessThanOrEqualTo(20);
        result.TruncatedQuery.ShouldContain("...");
    }
    
    [NonWindowsFact]
    public async Task ParsePlpgsql()
    {
        var sql = Utils.ReadFile("plpgsql_samples.sql");
        sql = sql.Replace("\r\n", "\n");
        var result = await Parser.ParsePlpgsqlAsync(sql);
        var resultVal = result.Value.Replace("\r\n", "\n");
        var expected = Utils.ReadFile("plpgsql_samples.expected.json");
        expected = expected.Replace("\r\n", "\n");
        resultVal.ShouldBe(expected);
    }
}
