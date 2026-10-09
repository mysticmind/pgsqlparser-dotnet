using Shouldly;
using Xunit;

namespace PgSqlParser.Tests;

public class ResultTests
{
    private const string InvalidQuery = "SELECT FROM WHERE";

    [Fact]
    public void IsSuccessTellsTheCompilerWhichSideIsSet()
    {
        var result = Parser.Normalize("SELECT 1");

        // No null-forgiving operators: IsSuccess narrows Value and Error for nullable analysis.
        if (result.IsSuccess)
            result.Value.Length.ShouldBe(9);
        else
            result.Error.Message.ShouldBeNull();

        result.IsSuccess.ShouldBeTrue();
        result.Error.ShouldBeNull();
    }

    [Fact]
    public void GetValueOrThrowReturnsTheValue()
    {
        Parser.Normalize("SELECT 1").GetValueOrThrow().ShouldBe("SELECT $1");
        Parser.Parse("SELECT 1").GetValueOrThrow().Stmts.Count.ShouldBe(1);
    }

    [Fact]
    public void GetValueOrThrowThrowsWithTheError()
    {
        var result = Parser.Parse(InvalidQuery);

        var exception = Should.Throw<PgSqlParserException>(() => result.GetValueOrThrow());

        exception.Error.ShouldBe(result.Error);
        exception.Message.ShouldBe("syntax error at or near \"WHERE\"");
        exception.Error.GetCursorCharOffset(InvalidQuery).ShouldBe(12);
    }

    [Fact]
    public void TryGetValue()
    {
        Parser.Fingerprint("SELECT 1").TryGetValue(out var fingerprint).ShouldBeTrue();
        fingerprint.ShouldBe("50fde20626009aba");

        Parser.Fingerprint("SELECT 1").TryGetValue(out fingerprint, out var error).ShouldBeTrue();
        fingerprint.ShouldBe("50fde20626009aba");
        error.ShouldBeNull();

        Parser.Fingerprint(InvalidQuery).TryGetValue(out fingerprint).ShouldBeFalse();
        fingerprint.ShouldBeNull();

        Parser.Fingerprint(InvalidQuery).TryGetValue(out fingerprint, out error).ShouldBeFalse();
        fingerprint.ShouldBeNull();
        error.ShouldNotBeNull().Message.ShouldStartWith("syntax error");
    }

    [Fact]
    public void Match()
    {
        Parser.Normalize("SELECT 1").Match(value => value, error => error.Message).ShouldBe("SELECT $1");
        Parser.Normalize(InvalidQuery).Match(value => value, error => error.Message)
            .ShouldBe("syntax error at or near \"WHERE\"");
    }

    [Fact]
    public void Deconstruct()
    {
        var (value, error) = Parser.Normalize("SELECT 1");
        value.ShouldBe("SELECT $1");
        error.ShouldBeNull();

        (value, error) = Parser.Normalize(InvalidQuery);
        value.ShouldBeNull();
        error.ShouldNotBeNull();
    }

    [Fact]
    public void WorksWithValueTypeAndCollectionResults()
    {
        Parser.IsUtilityStmt("SHOW fsync").GetValueOrThrow().ShouldBe([true]);
        Parser.SplitWithParser("SELECT 1; SELECT 2").GetValueOrThrow().Statements.Count.ShouldBe(2);
        Should.Throw<PgSqlParserException>(() => Parser.IsUtilityStmt(InvalidQuery).GetValueOrThrow());
    }

    [Fact]
    public async Task WorksWithAsyncCalls()
    {
        (await Parser.ParseAsync("SELECT 1")).GetValueOrThrow().Stmts.Count.ShouldBe(1);
        (await Parser.ParseAsync(InvalidQuery)).TryGetValue(out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNull();
    }

    [Fact]
    public void DefaultResultIsAFailure()
    {
        var result = default(Result<string>);

        result.IsSuccess.ShouldBeFalse();
        result.TryGetValue(out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNull().Message.ShouldBe("The result was not initialized");
        Should.Throw<PgSqlParserException>(() => result.GetValueOrThrow());
    }
}
