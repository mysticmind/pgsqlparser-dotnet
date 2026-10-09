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

    [Fact]
    public void DeepQueryRoundTripsOnASmallStack()
    {
        // 30 chained operators nest about 65 levels deep. This used to overflow a 512 KB stack.
        var query = DeepQuery("concat", 30);

        var deparsed = OnThread(SmallStack, () =>
        {
            var tree = Parser.Parse(query).GetValueOrThrow();
            return Parser.Deparse(tree).GetValueOrThrow();
        });

        deparsed.ShouldBe(query);
    }

    // How deep a query the PostgreSQL parser itself accepts depends on the platform and on the stack
    // it is given, so beyond a modest depth a query may be rejected. Either way the process must
    // survive, and whatever succeeds must be correct.
    [Theory]
    [InlineData("concat", 100, SmallStack)]
    [InlineData("subquery", 30, SmallStack)]
    [InlineData("subquery", 100, SmallStack)]
    [InlineData("call", 100, SmallStack)]
    [InlineData("concat", 1000, LargeStack)]
    [InlineData("subquery", 300, LargeStack)]
    [InlineData("concat", 3000, SmallStack)]
    [InlineData("subquery", 3000, SmallStack)]
    [InlineData("call", 3000, SmallStack)]
    [InlineData("concat", 3000, LargeStack)]
    public void DeepQueryNeverCrashes(string kind, int levels, int stackSize)
    {
        var query = DeepQuery(kind, levels);

        OnThread(stackSize, () =>
        {
            var parsed = Parser.Parse(query);
            if (!parsed.IsSuccess)
            {
                parsed.Error.Message.ShouldNotBeNullOrEmpty();
                return true;
            }

            var deparsed = Parser.Deparse(parsed.Value);
            if (deparsed.IsSuccess)
                deparsed.Value.ShouldBe(query);
            else
                deparsed.Error.Message.ShouldNotBeNullOrEmpty();

            return true;
        });
    }

    [Theory]
    [InlineData(500)]
    [InlineData(1900)]
    public void DeepTreeBuiltByHandIsWrittenOnASmallStack(int levels)
    {
        var tree = NestedExpressions(levels);

        // Each level is an A_Expr inside a Node, so the tree is twice as deep as the level count.
        // Writing it for libpg_query must not overflow; whether libpg_query then accepts it may vary.
        var result = OnThread(SmallStack, () => Parser.Deparse(tree));

        if (!result.IsSuccess)
            result.Error.Message.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void TreeBeyondTheDepthLimitIsRejected()
    {
        var tree = NestedExpressions(2100);

        var result = OnThread(SmallStack, () => Parser.Deparse(tree));

        result.Error.ShouldNotBeNull().Message.ShouldBe("parse tree is nested more than 4000 levels deep");
    }

    [Fact]
    public void OtherApisHandleDeepQueries()
    {
        var query = DeepQuery("concat", 30);

        OnThread(SmallStack, () =>
        {
            Parser.Fingerprint(query).IsSuccess.ShouldBeTrue();
            Parser.Scan(query).GetValueOrThrow().Tokens.Count.ShouldBe(60);
            Parser.Normalize(query).IsSuccess.ShouldBeTrue();
            Parser.Summary(query).IsSuccess.ShouldBeTrue();
            Parser.SplitWithParser(query).GetValueOrThrow().Statements.Count.ShouldBe(1);
            return true;
        });
    }

    [Fact]
    public async Task DeepQueryParsesOnAThreadPoolThread()
    {
        var query = DeepQuery("concat", 30);

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

    // SELECT 1 + (1 + (1 + ...)), built without recursion.
    private static ParseResult NestedExpressions(int levels)
    {
        static Node Constant() => new() { AConst = new A_Const { Ival = new Integer { Ival = 1 } } };

        var expression = Constant();
        for (var i = 0; i < levels; i++)
        {
            expression = new Node
            {
                AExpr = new A_Expr
                {
                    Kind = A_Expr_Kind.AexprOp,
                    Name = { new Node { String = new String { Sval = "+" } } },
                    Lexpr = Constant(),
                    Rexpr = expression
                }
            };
        }

        var select = new SelectStmt
        {
            TargetList = { new Node { ResTarget = new ResTarget { Val = expression } } },
            LimitOption = LimitOption.Default,
            Op = SetOperation.SetopNone
        };
        return new ParseResult
        {
            Version = Parser.PgVersionNum,
            Stmts = { new RawStmt { Stmt = new Node { SelectStmt = select } } }
        };
    }

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
