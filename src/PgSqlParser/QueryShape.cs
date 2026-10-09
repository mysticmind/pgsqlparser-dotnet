using Google.Protobuf;

namespace PgSqlParser;

/// <summary>
/// A column a SELECT returns.
/// </summary>
/// <param name="Name">
/// The name PostgreSQL gives the column: its alias, or the name it derives from the expression, such as
/// the column or function name. Null for <c>*</c>, whose columns only the catalog can list.
/// </param>
/// <param name="Expression">The expression that produces it.</param>
/// <param name="IsStar">True for <c>*</c> and <c>table.*</c>, which stand for several columns.</param>
public sealed record OutputColumn(string? Name, IMessage Expression, bool IsStar);

/// <summary>
/// Reads the shape of a query's result from its parse tree.
/// </summary>
public static class QueryShape
{
    /// <summary>The name PostgreSQL uses for a column it cannot name from its expression.</summary>
    public const string UnnamedColumn = "?column?";

    /// <summary>
    /// Returns the columns a SELECT returns, named as PostgreSQL names them. For a UNION, INTERSECT or
    /// EXCEPT they come from the first branch, and for a VALUES list they are <c>column1</c>, <c>column2</c>, ...
    /// </summary>
    public static IReadOnlyList<OutputColumn> GetOutputColumns(this SelectStmt select)
    {
        ArgumentNullException.ThrowIfNull(select);

        // The leftmost branch of a set operation decides the names.
        while (select.Op != SetOperation.SetopNone && select.Larg is not null)
            select = select.Larg;

        if (select.ValuesLists.Count > 0)
        {
            var row = select.ValuesLists[0].List?.Items ?? [];
            return row.Select((value, index) => new OutputColumn($"column{index + 1}", value.Unwrap() ?? value, false)).ToList();
        }

        var columns = new List<OutputColumn>();
        foreach (var item in select.TargetList)
        {
            if (item.ResTarget is not { } target)
                continue;

            var expression = target.Val?.Unwrap();
            if (expression is ColumnRef { Fields.Count: > 0 } column && column.Fields[^1].AStar is not null)
                columns.Add(new OutputColumn(null, column, true));
            else
                columns.Add(new OutputColumn(target.Name.Length > 0 ? target.Name : FigureName(expression).Name ?? UnnamedColumn,
                    expression ?? target, false));
        }

        return columns;
    }

    // A port of FigureColnameInternal in PostgreSQL's parse_target.c. Strength 2 is a good name,
    // 1 a weak one (a type name, "case") that a better name from further in overrides.
    private static (string? Name, int Strength) FigureName(IMessage? node)
    {
        switch (node)
        {
            case ColumnRef column:
                return column.Fields.LastOrDefault(field => field.String is not null)?.String.Sval is { } name ? (name, 2) : (null, 0);
            case A_Indirection indirection:
                return indirection.Indirection.LastOrDefault(item => item.String is not null)?.String.Sval is { } field
                    ? (field, 2)
                    : FigureName(indirection.Arg?.Unwrap());
            case FuncCall call:
                return (call.Funcname.LastOrDefault()?.String?.Sval, 2);
            case A_Expr { Kind: A_Expr_Kind.AexprNullif }:
                return ("nullif", 2);
            case TypeCast cast:
                var inner = FigureName(cast.Arg?.Unwrap());
                return inner.Strength <= 1 && cast.TypeName?.Names.LastOrDefault()?.String?.Sval is { } type ? (type, 1) : inner;
            case CollateClause collate:
                return FigureName(collate.Arg?.Unwrap());
            case GroupingFunc:
                return ("grouping", 2);
            case MergeSupportFunc:
                return ("merge_action", 2);
            case SubLink { SubLinkType: SubLinkType.ExistsSublink }:
                return ("exists", 2);
            case SubLink { SubLinkType: SubLinkType.ArraySublink }:
                return ("array", 2);
            case SubLink { SubLinkType: SubLinkType.ExprSublink } link when link.Subselect?.Unwrap() is SelectStmt subselect:
                return subselect.GetOutputColumns().FirstOrDefault() is { IsStar: false, Name: { } first } && first != UnnamedColumn
                    ? (first, 2)
                    : (null, 0);
            case CaseExpr @case:
                var result = FigureName(@case.Defresult?.Unwrap());
                return result.Strength <= 1 ? ("case", 1) : result;
            case A_ArrayExpr:
                return ("array", 2);
            case RowExpr:
                return ("row", 2);
            case CoalesceExpr:
                return ("coalesce", 2);
            case MinMaxExpr minMax:
                return (minMax.Op == MinMaxOp.IsGreatest ? "greatest" : "least", 2);
            case SQLValueFunction function:
                return (function.Op switch
                {
                    SQLValueFunctionOp.SvfopCurrentDate => "current_date",
                    SQLValueFunctionOp.SvfopCurrentTime or SQLValueFunctionOp.SvfopCurrentTimeN => "current_time",
                    SQLValueFunctionOp.SvfopCurrentTimestamp or SQLValueFunctionOp.SvfopCurrentTimestampN => "current_timestamp",
                    SQLValueFunctionOp.SvfopLocaltime or SQLValueFunctionOp.SvfopLocaltimeN => "localtime",
                    SQLValueFunctionOp.SvfopLocaltimestamp or SQLValueFunctionOp.SvfopLocaltimestampN => "localtimestamp",
                    SQLValueFunctionOp.SvfopCurrentRole => "current_role",
                    SQLValueFunctionOp.SvfopCurrentUser => "current_user",
                    SQLValueFunctionOp.SvfopUser => "user",
                    SQLValueFunctionOp.SvfopSessionUser => "session_user",
                    SQLValueFunctionOp.SvfopCurrentCatalog => "current_catalog",
                    SQLValueFunctionOp.SvfopCurrentSchema => "current_schema",
                    _ => null
                }, 2);
            case XmlSerialize:
                return ("xmlserialize", 2);
            case JsonObjectConstructor:
                return ("json_object", 2);
            case JsonArrayConstructor or JsonArrayQueryConstructor:
                return ("json_array", 2);
            case JsonObjectAgg:
                return ("json_objectagg", 2);
            case JsonArrayAgg:
                return ("json_arrayagg", 2);
            default:
                return (null, 0);
        }
    }
}
