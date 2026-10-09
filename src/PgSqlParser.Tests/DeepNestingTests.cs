using Shouldly;
using Xunit;

namespace PgSqlParser.Tests;

/// <summary>
/// Reading and writing a parse tree is recursive and costs several kilobytes of stack per level, so a
/// deeply nested query used to overflow the stack of a small thread and kill the process.
/// </summary>
public class DeepNestingTests
{
    // A thread pool thread on macOS has 512 KB, the smallest stack among the supported platforms.
    private const int SmallStack = 512 * 1024;
    private const int LargeStack = 64 * 1024 * 1024;

    [Theory]
    [InlineData("concat", 30)]
    [InlineData("concat", 100)]
    [InlineData("subquery", 30)]
    [InlineData("subquery", 100)]
    [InlineData("call", 100)]
    public void DeepQueryRoundTripsOnASmallStack(string kind, int levels)
    {
        var query = DeepQuery(kind, levels);

        var deparsed = OnThread(SmallStack, () =>
        {
            var tree = Parser.Parse(query).GetValueOrThrow();
            tree.Stmts.Count.ShouldBe(1);
            return Parser.Deparse(tree).GetValueOrThrow();
        });

        deparsed.ShouldBe(query);
    }

    [Theory]
    [InlineData("concat", 1000)]
    [InlineData("subquery", 300)]
    public void VeryDeepQueryRoundTripsOnALargeStack(string kind, int levels)
    {
        var query = DeepQuery(kind, levels);

        var deparsed = OnThread(LargeStack, () => Parser.Deparse(Parser.Parse(query).GetValueOrThrow()).GetValueOrThrow());

        deparsed.ShouldBe(query);
    }

    [Fact]
    public void TreeBeyondTheDepthLimitIsRejected()
    {
        var query = DeepQuery("concat", 3000);

        var error = OnThread(LargeStack, () => Parser.Parse(query).Error);

        error.ShouldNotBeNull().Message.ShouldBe("parse tree is nested more than 4000 levels deep");
    }

    [Theory]
    [InlineData("concat", 3000)]
    [InlineData("subquery", 3000)]
    [InlineData("call", 3000)]
    public void ExtremeNestingFailsWithoutCrashingOnASmallStack(string kind, int levels)
    {
        var query = DeepQuery(kind, levels);

        // Which limit is hit first depends on the stack: PostgreSQL's own, or the parse tree depth limit.
        var result = OnThread(SmallStack, () => Parser.Parse(query));

        result.IsSuccess.ShouldBeFalse();
        result.Error.Message.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void OtherApisHandleDeepQueries()
    {
        var query = DeepQuery("concat", 100);

        OnThread(SmallStack, () =>
        {
            Parser.Fingerprint(query).IsSuccess.ShouldBeTrue();
            Parser.Scan(query).GetValueOrThrow().Tokens.Count.ShouldBe(200);
            Parser.Normalize(query).IsSuccess.ShouldBeTrue();
            Parser.Summary(query).IsSuccess.ShouldBeTrue();
            Parser.SplitWithParser(query).GetValueOrThrow().Statements.Count.ShouldBe(1);
            return true;
        });
    }

    [Fact]
    public async Task DeepQueryParsesOnAThreadPoolThread()
    {
        var query = DeepQuery("concat", 100);

        var tree = (await Parser.ParseAsync(query)).GetValueOrThrow();

        (await Parser.DeparseAsync(tree)).GetValueOrThrow().ShouldBe(query);
    }

    private static string DeepQuery(string kind, int levels) => kind switch
    {
        "concat" => "SELECT " + string.Join(" || ", Enumerable.Repeat("a", levels)),
        "subquery" => string.Concat(Enumerable.Repeat("SELECT * FROM (", levels)) + "SELECT 1"
                      + string.Concat(Enumerable.Range(0, levels).Select(i => $") s{i}")),
        _ => "SELECT " + string.Concat(Enumerable.Repeat("f(", levels)) + "1" + new string(')', levels)
    };

    private static T OnThread<T>(int stackSize, Func<T> work)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }, stackSize);
        thread.Start();
        thread.Join();

        if (failure is not null)
            throw new Exception("The work failed on its thread.", failure);

        return result;
    }
}
