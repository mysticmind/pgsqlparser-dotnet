using Shouldly;
using Xunit;

namespace PgSqlParser.Tests;

/// <summary>
/// libpg_query reads queries as C strings, so null and NUL characters must be caught before the call.
/// </summary>
public class InvalidInputTests
{
    public static TheoryData<string, Func<string, Error?>> QueryApis => new()
    {
        { nameof(Parser.Normalize), q => Parser.Normalize(q).Error },
        { nameof(Parser.NormalizeUtility), q => Parser.NormalizeUtility(q).Error },
        { nameof(Parser.Scan), q => Parser.Scan(q).Error },
        { nameof(Parser.Parse), q => Parser.Parse(q).Error },
        { nameof(Parser.ParsePlpgsql), q => Parser.ParsePlpgsql(q).Error },
        { nameof(Parser.Fingerprint), q => Parser.Fingerprint(q).Error },
        { nameof(Parser.SplitWithScanner), q => Parser.SplitWithScanner(q).Error },
        { nameof(Parser.SplitWithParser), q => Parser.SplitWithParser(q).Error },
        { nameof(Parser.IsUtilityStmt), q => Parser.IsUtilityStmt(q).Error },
        { nameof(Parser.Summary), q => Parser.Summary(q).Error },
        { nameof(Parser.DeparseComments), q => Parser.DeparseComments(q).Error }
    };

    [Theory]
    [MemberData(nameof(QueryApis))]
    public void NullQueryThrows(string api, Func<string, Error?> call)
    {
        Should.Throw<ArgumentNullException>(() => call(null!), api);
    }

    [Theory]
    [MemberData(nameof(QueryApis))]
    public void NulCharacterIsRejected(string api, Func<string, Error?> call)
    {
        // Without the check this would be read as "SELECT 'é😀'" and succeed.
        var error = call("SELECT 'é😀'\0 this is not sql").ShouldNotBeNull(api);

        error.Message.ShouldBe("query contains a NUL character");
        // 1-based, in code points like PostgreSQL's own cursor positions: 11 code points come first.
        error.CursorPos.ShouldBe(12);
    }

    [Theory]
    [MemberData(nameof(QueryApis))]
    public void EmptyQueryDoesNotFail(string api, Func<string, Error?> call)
    {
        call("").ShouldBeNull(api);
    }

    [Fact]
    public async Task AsyncNullQueryThrows()
    {
        await Should.ThrowAsync<ArgumentNullException>(() => Parser.ParseAsync(null!));
        (await Parser.ParseAsync("SELECT 1\0")).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void DeparseNullTreeThrows()
    {
        Should.Throw<ArgumentNullException>(() => Parser.Deparse(null!));
        Should.Throw<ArgumentNullException>(() => Parser.Deparse(null!, new DeparseOptions()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("/* a \0 b */")]
    public void DeparseRejectsInvalidCommentText(string? text)
    {
        var parseResult = Parser.Parse("SELECT 1").Value.ShouldNotBeNull();
        var options = new DeparseOptions { Comments = [new DeparseComment(0, 0, 0, text!)] };

        Should.Throw<ArgumentException>(() => Parser.Deparse(parseResult, options));
    }

    [Theory]
    // 60 chained operators, 30 nested subqueries and 60 nested calls each exceed 100 protobuf levels.
    [InlineData("concat", 60)]
    [InlineData("subquery", 30)]
    [InlineData("call", 60)]
    public async Task DeeplyNestedQueryReturnsAnError(string kind, int levels)
    {
        var query = DeepQuery(kind, levels);

        var result = Parser.Parse(query);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Message.ShouldBe("parse tree is nested too deeply to read");
        (await Parser.ParseAsync(query)).Error.ShouldBe(result.Error);
        // Only reading the parse tree is limited; the other APIs handle the same query.
        Parser.Fingerprint(query).IsSuccess.ShouldBeTrue();
        Parser.Scan(query).IsSuccess.ShouldBeTrue();
        Parser.Summary(query).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData("concat", 30)]
    [InlineData("subquery", 15)]
    [InlineData("call", 30)]
    public void ModeratelyNestedQueryParses(string kind, int levels)
    {
        var query = DeepQuery(kind, levels);

        var tree = Parser.Parse(query).GetValueOrThrow();

        Parser.Deparse(tree).IsSuccess.ShouldBeTrue();
    }

    private static string DeepQuery(string kind, int levels) => kind switch
    {
        "concat" => "SELECT " + string.Join(" || ", Enumerable.Repeat("a", levels)),
        "subquery" => string.Concat(Enumerable.Repeat("SELECT * FROM (", levels)) + "SELECT 1"
                      + string.Concat(Enumerable.Range(0, levels).Select(i => $") s{i}")),
        _ => "SELECT " + string.Concat(Enumerable.Repeat("f(", levels)) + "1" + new string(')', levels)
    };
}
