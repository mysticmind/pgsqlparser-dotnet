using System.Globalization;
using Google.Protobuf;

namespace PgSqlParser;

/// <summary>
/// Builds parse tree nodes. Each method gives the same node the parser would for the matching SQL, so
/// the result can be put into a tree, compared with parsed SQL and deparsed.
/// Arguments that take a node accept any node type or a <see cref="Node"/> wrapper.
/// </summary>
public static class Ast
{
    /// <summary>Parses an expression into a node: <c>Ast.Expression("a > 1 AND b IS NULL")</c>.</summary>
    /// <exception cref="PgSqlParserException">The text is not a single expression.</exception>
    public static Node Expression(string sql) => Parser.ParseExpression(sql).GetValueOrThrow();

    /// <summary>A column reference: <c>Ast.Column("name")</c> or <c>Ast.Column("t", "name")</c>.</summary>
    public static ColumnRef Column(params string[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Length == 0)
            throw new ArgumentException("A column reference needs at least a name.", nameof(parts));

        var column = new ColumnRef();
        column.Fields.Add(parts.Select(Name));
        return column;
    }

    /// <summary><c>*</c>, or <c>table.*</c> when a qualifier is given.</summary>
    public static ColumnRef Star(params string[] qualifier)
    {
        ArgumentNullException.ThrowIfNull(qualifier);

        var column = new ColumnRef();
        column.Fields.Add(qualifier.Select(Name));
        column.Fields.Add(new Node { AStar = new A_Star() });
        return column;
    }

    public static A_Const Const(int value) => new() { Ival = new Integer { Ival = value } };

    /// <summary>An integer constant. Values outside the 32-bit range are held the way the parser holds them.</summary>
    public static A_Const Const(long value)
    {
        return value is >= int.MinValue and <= int.MaxValue
            ? Const((int)value)
            : new A_Const { Fval = new Float { Fval = value.ToString(CultureInfo.InvariantCulture) } };
    }

    public static A_Const Const(decimal value) => new() { Fval = new Float { Fval = value.ToString(CultureInfo.InvariantCulture) } };

    public static A_Const Const(double value) => new() { Fval = new Float { Fval = value.ToString("R", CultureInfo.InvariantCulture) } };

    /// <summary>A string constant. The value is taken as it is; quoting happens when the tree is deparsed.</summary>
    public static A_Const Const(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new A_Const { Sval = new String { Sval = value } };
    }

    public static A_Const Const(bool value) => new() { Boolval = new Boolean { Boolval = value } };

    public static A_Const Null() => new() { Isnull = true };

    /// <summary>A parameter reference: <c>Ast.Param(1)</c> is <c>$1</c>.</summary>
    public static ParamRef Param(int number) => new() { Number = number };

    /// <summary>A binary operator: <c>Ast.Op("+", left, right)</c>.</summary>
    public static A_Expr Op(string op, IMessage left, IMessage right)
    {
        ArgumentNullException.ThrowIfNull(op);

        var expression = new A_Expr { Kind = A_Expr_Kind.AexprOp, Lexpr = Wrap(left), Rexpr = Wrap(right) };
        expression.Name.Add(Name(op));
        return expression;
    }

    public static A_Expr Eq(IMessage left, IMessage right) => Op("=", left, right);
    public static A_Expr NotEq(IMessage left, IMessage right) => Op("<>", left, right);
    public static A_Expr Lt(IMessage left, IMessage right) => Op("<", left, right);
    public static A_Expr LtEq(IMessage left, IMessage right) => Op("<=", left, right);
    public static A_Expr Gt(IMessage left, IMessage right) => Op(">", left, right);
    public static A_Expr GtEq(IMessage left, IMessage right) => Op(">=", left, right);

    /// <summary><c>a AND b AND ...</c>. An argument that is itself an AND is merged in, as the parser does.</summary>
    public static BoolExpr And(params IMessage[] conditions) => Bool(BoolExprType.AndExpr, conditions);

    /// <summary><c>a OR b OR ...</c>. An argument that is itself an OR is merged in, as the parser does.</summary>
    public static BoolExpr Or(params IMessage[] conditions) => Bool(BoolExprType.OrExpr, conditions);

    public static BoolExpr Not(IMessage condition)
    {
        var expression = new BoolExpr { Boolop = BoolExprType.NotExpr };
        expression.Args.Add(Wrap(condition));
        return expression;
    }

    public static NullTest IsNull(IMessage expression) =>
        new() { Arg = Wrap(expression), Nulltesttype = NullTestType.IsNull };

    public static NullTest IsNotNull(IMessage expression) =>
        new() { Arg = Wrap(expression), Nulltesttype = NullTestType.IsNotNull };

    /// <summary><c>expression IN (value, ...)</c>.</summary>
    public static A_Expr In(IMessage expression, params IMessage[] values) => InList("=", expression, values);

    /// <summary><c>expression NOT IN (value, ...)</c>.</summary>
    public static A_Expr NotIn(IMessage expression, params IMessage[] values) => InList("<>", expression, values);

    /// <summary>A function call: <c>Ast.Call("lower", Ast.Column("name"))</c>.</summary>
    public static FuncCall Call(string name, params IMessage[] arguments) => Call([name], arguments);

    /// <summary>A function call with a qualified name: <c>Ast.Call(["pg_catalog", "now"])</c>.</summary>
    public static FuncCall Call(string[] name, params IMessage[] arguments)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(arguments);
        if (name.Length == 0)
            throw new ArgumentException("A function call needs a name.", nameof(name));

        var call = new FuncCall { Funcformat = CoercionForm.CoerceExplicitCall };
        call.Funcname.Add(name.Select(Name));
        call.Args.Add(arguments.Select(Wrap));
        return call;
    }

    /// <summary><c>count(*)</c>.</summary>
    public static FuncCall CountStar()
    {
        var call = Call("count");
        call.AggStar = true;
        return call;
    }

    /// <summary>A cast: <c>Ast.Cast(Ast.Param(1), "int")</c> is <c>$1::int</c>.</summary>
    /// <exception cref="PgSqlParserException"><paramref name="typeName"/> is not a type name.</exception>
    public static TypeCast Cast(IMessage expression, string typeName) =>
        new() { Arg = Wrap(expression), TypeName = Parser.ParseTypeName(typeName).GetValueOrThrow() };

    /// <summary>A table reference: <c>Ast.Table("users")</c>.</summary>
    public static RangeVar Table(string name) => Table(null, name);

    /// <summary>A table reference with an optional schema and alias: <c>Ast.Table("public", "users", "u")</c>.</summary>
    public static RangeVar Table(string? schema, string name, string? alias = null)
    {
        ArgumentNullException.ThrowIfNull(name);

        var table = new RangeVar { Relname = name, Schemaname = schema ?? string.Empty, Inh = true, Relpersistence = "p" };
        if (alias is not null)
            table.Alias = new Alias { Aliasname = alias };

        return table;
    }

    /// <summary>An item of a select list, with an optional alias: <c>expression AS alias</c>.</summary>
    public static ResTarget Target(IMessage expression, string? alias = null) =>
        new() { Val = Wrap(expression), Name = alias ?? string.Empty };

    /// <summary>An item of an ORDER BY clause.</summary>
    public static SortBy OrderBy(IMessage expression, bool descending = false) => new()
    {
        Node = Wrap(expression),
        SortbyDir = descending ? SortByDir.SortbyDesc : SortByDir.SortbyDefault,
        SortbyNulls = SortByNulls.Default
    };

    /// <summary>
    /// A SELECT statement. Items of <paramref name="targets"/> that are not already select list items
    /// are wrapped as such.
    /// </summary>
    public static SelectStmt Select(IEnumerable<IMessage> targets, IMessage? from = null, IMessage? where = null)
    {
        ArgumentNullException.ThrowIfNull(targets);

        var select = new SelectStmt { LimitOption = LimitOption.Default, Op = SetOperation.SetopNone };
        foreach (var target in targets)
            select.TargetList.Add(Unwrapped(target) is ResTarget item ? item.AsNode() : Target(target).AsNode());

        if (from is not null)
            select.FromClause.Add(Wrap(from));

        if (where is not null)
            select.WhereClause = Wrap(where);

        return select;
    }

    private static Node Name(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new Node { String = new String { Sval = value } };
    }

    private static Node Wrap(IMessage node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.AsNode();
    }

    private static IMessage? Unwrapped(IMessage node) => node is Node wrapper ? wrapper.Unwrap() : node;

    private static BoolExpr Bool(BoolExprType kind, IMessage[] conditions)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        if (conditions.Length < 2)
            throw new ArgumentException("AND and OR need at least two conditions.", nameof(conditions));

        var expression = new BoolExpr { Boolop = kind };
        foreach (var condition in conditions)
        {
            // a AND (b AND c) is one flat list of three in the parser's tree.
            if (Unwrapped(condition) is BoolExpr nested && nested.Boolop == kind)
                expression.Args.Add(nested.Args);
            else
                expression.Args.Add(Wrap(condition));
        }

        return expression;
    }

    private static A_Expr InList(string op, IMessage expression, IMessage[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length == 0)
            throw new ArgumentException("IN needs at least one value.", nameof(values));

        var list = new List();
        list.Items.Add(values.Select(Wrap));

        var result = new A_Expr { Kind = A_Expr_Kind.AexprIn, Lexpr = Wrap(expression), Rexpr = new Node { List = list } };
        result.Name.Add(Name(op));
        return result;
    }
}
