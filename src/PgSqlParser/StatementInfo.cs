using Google.Protobuf;

namespace PgSqlParser;

/// <summary>
/// The broad kind of a statement.
/// </summary>
public enum StatementKind
{
    /// <summary>Anything not covered by the other kinds.</summary>
    Other,

    /// <summary>SELECT, VALUES and TABLE.</summary>
    Select,
    Insert,
    Update,
    Delete,
    Merge,
    Truncate,
    Copy,

    /// <summary>
    /// Statements that define or change database objects, privileges or roles: CREATE, ALTER, DROP,
    /// COMMENT, GRANT, REVOKE and so on.
    /// </summary>
    Ddl,

    /// <summary>BEGIN, COMMIT, ROLLBACK, SAVEPOINT, PREPARE TRANSACTION, SET CONSTRAINTS.</summary>
    Transaction,

    /// <summary>SET, RESET, SHOW, DISCARD, LISTEN, UNLISTEN, NOTIFY, LOAD.</summary>
    Session,

    /// <summary>DECLARE, FETCH, MOVE, CLOSE.</summary>
    Cursor,

    /// <summary>PREPARE, EXECUTE, DEALLOCATE.</summary>
    Prepared,
    Explain,

    /// <summary>CALL and DO.</summary>
    Call,

    /// <summary>VACUUM, ANALYZE, CLUSTER, REINDEX, CHECKPOINT, REFRESH MATERIALIZED VIEW.</summary>
    Maintenance,
    Lock
}

/// <summary>
/// Facts about one statement, read from its parse tree. These are facts, not a verdict: what to
/// allow is for the caller to decide.
/// </summary>
public sealed record StatementInfo
{
    /// <summary>The statement node, for example a <see cref="SelectStmt"/>.</summary>
    public required IMessage Node { get; init; }

    public required StatementKind Kind { get; init; }

    /// <summary>
    /// True for everything except SELECT, INSERT, UPDATE, DELETE and MERGE, which are the statements
    /// PostgreSQL plans. Matches <see cref="Parser.IsUtilityStmt"/>.
    /// </summary>
    public bool IsUtility => Kind is not (StatementKind.Select or StatementKind.Insert or StatementKind.Update
        or StatementKind.Delete or StatementKind.Merge);

    /// <summary>True if a WITH clause anywhere in the statement contains an INSERT, UPDATE, DELETE or MERGE.</summary>
    public bool HasDataModifyingCte { get; init; }

    /// <summary>True for SELECT ... INTO, which creates a table.</summary>
    public bool HasSelectInto { get; init; }

    /// <summary>True if a SELECT anywhere in the statement has FOR UPDATE, FOR SHARE or a similar clause.</summary>
    public bool HasLockingClause { get; init; }

    /// <summary>True for EXPLAIN with ANALYZE, which runs the statement it explains.</summary>
    public bool ExecutesInner { get; init; }

    /// <summary>True for COPY ... PROGRAM, which runs a command on the server.</summary>
    public bool RunsProgram { get; init; }

    /// <summary>
    /// The statement wrapped by this one: the query of EXPLAIN, PREPARE, DECLARE CURSOR, CREATE TABLE AS
    /// or COPY (query). Null otherwise.
    /// </summary>
    public StatementInfo? Inner { get; init; }

    /// <summary>
    /// True if the statement, as written, changes no data and no database objects and takes no explicit
    /// locks. It errs on the side of false: CALL, DO and EXECUTE count as not read-only because what they
    /// run is not visible. A function called from a SELECT can still write, which the parse tree cannot show.
    /// </summary>
    public bool IsReadOnly => Kind switch
    {
        StatementKind.Select => !HasSelectInto && !HasDataModifyingCte && !HasLockingClause,
        StatementKind.Explain => !ExecutesInner || Inner is { IsReadOnly: true },
        StatementKind.Copy => Node is CopyStmt { IsFrom: false } && !RunsProgram && Inner is null or { IsReadOnly: true },
        StatementKind.Cursor => Inner is null or { IsReadOnly: true },
        StatementKind.Prepared => Node is DeallocateStmt || Inner is { IsReadOnly: true },
        StatementKind.Transaction or StatementKind.Session => true,
        _ => false
    };
}

/// <summary>
/// Classifies statements by reading their parse tree.
/// </summary>
public static class StatementClassifier
{
    /// <summary>Classifies every statement of a parse tree, in order.</summary>
    public static IReadOnlyList<StatementInfo> Classify(this ParseResult tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        return tree.Stmts.Select(Classify).ToList();
    }

    /// <summary>Classifies one statement.</summary>
    public static StatementInfo Classify(this RawStmt statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return Classify(statement.Stmt?.Unwrap());
    }

    private static StatementInfo Classify(IMessage? node)
    {
        if (node is null)
            return new StatementInfo { Node = new Node(), Kind = StatementKind.Other };

        var inner = node switch
        {
            ExplainStmt explain => explain.Query,
            PrepareStmt prepare => prepare.Query,
            DeclareCursorStmt cursor => cursor.Query,
            CreateTableAsStmt createAs => createAs.Query,
            CopyStmt copy => copy.Query,
            _ => null
        };

        var hasDataModifyingCte = false;
        var hasLockingClause = false;
        node.Walk(visit =>
        {
            // The wrapped statement is classified on its own, as Inner.
            if (inner is not null && ReferenceEquals(visit.Node, inner.Unwrap()))
                return WalkAction.SkipChildren;

            switch (visit.Node)
            {
                case CommonTableExpr cte when cte.Ctequery?.Unwrap() is InsertStmt or UpdateStmt or DeleteStmt or MergeStmt:
                    hasDataModifyingCte = true;
                    break;
                case SelectStmt { LockingClause.Count: > 0 }:
                    hasLockingClause = true;
                    break;
            }

            return WalkAction.Continue;
        });

        return new StatementInfo
        {
            Node = node,
            Kind = KindOf(node),
            HasDataModifyingCte = hasDataModifyingCte,
            HasLockingClause = hasLockingClause || node is SelectStmt { LockingClause.Count: > 0 },
            HasSelectInto = node is SelectStmt { IntoClause: not null },
            ExecutesInner = node is ExplainStmt explainStmt && explainStmt.Options.Any(IsEnabledAnalyze),
            RunsProgram = node is CopyStmt { IsProgram: true },
            Inner = inner?.Unwrap() is { } innerNode ? Classify(innerNode) : null
        };
    }

    // EXPLAIN (ANALYZE), (ANALYZE true) and (ANALYZE on) run the statement; (ANALYZE false) does not.
    private static bool IsEnabledAnalyze(Node option)
    {
        if (option.DefElem is not { Defname: "analyze" } analyze)
            return false;

        return analyze.Arg?.Unwrap() switch
        {
            null => true,
            Boolean boolean => boolean.Boolval,
            Integer integer => integer.Ival != 0,
            String text => text.Sval is "true" or "on" or "1",
            _ => true
        };
    }

    private static StatementKind KindOf(IMessage node)
    {
        return node switch
        {
            SelectStmt => StatementKind.Select,
            InsertStmt => StatementKind.Insert,
            UpdateStmt => StatementKind.Update,
            DeleteStmt => StatementKind.Delete,
            MergeStmt => StatementKind.Merge,
            TruncateStmt => StatementKind.Truncate,
            CopyStmt => StatementKind.Copy,
            TransactionStmt or ConstraintsSetStmt => StatementKind.Transaction,
            VariableSetStmt or VariableShowStmt or DiscardStmt or ListenStmt or UnlistenStmt or NotifyStmt
                or LoadStmt => StatementKind.Session,
            DeclareCursorStmt or FetchStmt or ClosePortalStmt => StatementKind.Cursor,
            PrepareStmt or ExecuteStmt or DeallocateStmt => StatementKind.Prepared,
            ExplainStmt => StatementKind.Explain,
            CallStmt or DoStmt => StatementKind.Call,
            VacuumStmt or ClusterStmt or ReindexStmt or CheckPointStmt or RefreshMatViewStmt => StatementKind.Maintenance,
            LockStmt => StatementKind.Lock,
            // Not statements that can stand at the top level.
            RawStmt or ReturnStmt or PLAssignStmt or SetOperationStmt => StatementKind.Other,
            // Every remaining statement type creates, alters or drops something, or changes privileges.
            _ when node.Descriptor.Name.EndsWith("Stmt", StringComparison.Ordinal) => StatementKind.Ddl,
            _ => StatementKind.Other
        };
    }
}
