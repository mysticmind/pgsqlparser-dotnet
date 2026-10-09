using Google.Protobuf;

namespace PgSqlParser;

/// <summary>
/// PostgreSQL's table-level lock modes, weakest first. The values are PostgreSQL's own lock mode numbers.
/// </summary>
public enum LockMode
{
    AccessShare = 1,
    RowShare = 2,
    RowExclusive = 3,
    ShareUpdateExclusive = 4,
    Share = 5,
    ShareRowExclusive = 6,
    Exclusive = 7,
    AccessExclusive = 8
}

/// <summary>
/// A table lock a statement takes.
/// </summary>
/// <param name="Schema">The schema as written in the statement, or null.</param>
/// <param name="Table">The table name.</param>
/// <param name="Mode">The lock mode.</param>
/// <param name="StatementIndex">The top-level statement that takes it, when known.</param>
public sealed record TableLock(string? Schema, string Table, LockMode Mode, int? StatementIndex)
{
    public override string ToString() => $"{(Schema is null ? Table : $"{Schema}.{Table}")}: {Mode}";
}

/// <summary>
/// Works out which table locks a statement takes, from its parse tree and PostgreSQL's documented
/// rules. See <see cref="GetLocks(ParseResult)"/> for what is covered.
/// </summary>
public static class StatementLocks
{
    // The conflict table of PostgreSQL's "Table-Level Lock Modes" documentation, as bit masks by mode number.
    private static readonly int[] Conflicts =
    [
        0,
        Mask(LockMode.AccessExclusive),
        Mask(LockMode.Exclusive, LockMode.AccessExclusive),
        Mask(LockMode.Share, LockMode.ShareRowExclusive, LockMode.Exclusive, LockMode.AccessExclusive),
        Mask(LockMode.ShareUpdateExclusive, LockMode.Share, LockMode.ShareRowExclusive, LockMode.Exclusive, LockMode.AccessExclusive),
        Mask(LockMode.RowExclusive, LockMode.ShareUpdateExclusive, LockMode.ShareRowExclusive, LockMode.Exclusive, LockMode.AccessExclusive),
        Mask(LockMode.RowExclusive, LockMode.ShareUpdateExclusive, LockMode.Share, LockMode.ShareRowExclusive, LockMode.Exclusive, LockMode.AccessExclusive),
        Mask(LockMode.RowShare, LockMode.RowExclusive, LockMode.ShareUpdateExclusive, LockMode.Share, LockMode.ShareRowExclusive, LockMode.Exclusive, LockMode.AccessExclusive),
        Mask(LockMode.AccessShare, LockMode.RowShare, LockMode.RowExclusive, LockMode.ShareUpdateExclusive, LockMode.Share, LockMode.ShareRowExclusive, LockMode.Exclusive, LockMode.AccessExclusive)
    ];

    // Storage parameters whose change needs ACCESS EXCLUSIVE; the other known ones need SHARE UPDATE
    // EXCLUSIVE. From the relOpts tables in PostgreSQL 18's reloptions.c.
    private static readonly HashSet<string> AccessExclusiveOptions =
    [
        "autosummarize", "buffering", "check_option", "fastupdate", "gin_pending_list_limit", "pages_per_range",
        "security_barrier", "security_invoker", "user_catalog_table"
    ];

    private static readonly HashSet<string> ShareUpdateExclusiveOptions =
    [
        "autovacuum_analyze_scale_factor", "autovacuum_analyze_threshold", "autovacuum_enabled",
        "autovacuum_freeze_max_age", "autovacuum_freeze_min_age", "autovacuum_freeze_table_age",
        "autovacuum_multixact_freeze_max_age", "autovacuum_multixact_freeze_min_age",
        "autovacuum_multixact_freeze_table_age", "autovacuum_vacuum_cost_delay", "autovacuum_vacuum_cost_limit",
        "autovacuum_vacuum_insert_scale_factor", "autovacuum_vacuum_insert_threshold",
        "autovacuum_vacuum_max_threshold", "autovacuum_vacuum_scale_factor", "autovacuum_vacuum_threshold",
        "deduplicate_items", "effective_io_concurrency", "fillfactor", "log_autovacuum_min_duration",
        "maintenance_io_concurrency", "n_distinct", "n_distinct_inherited", "parallel_workers", "random_page_cost",
        "seq_page_cost", "toast_tuple_target", "vacuum_cleanup_index_scale_factor", "vacuum_index_cleanup",
        "vacuum_max_eager_freeze_failure_rate", "vacuum_truncate"
    ];

    /// <summary>True if a session holding <paramref name="mode"/> on a table blocks one asking for <paramref name="other"/>, and the reverse.</summary>
    public static bool ConflictsWith(this LockMode mode, LockMode other) => (Conflicts[(int)mode] & Mask(other)) != 0;

    /// <summary>True if the lock blocks plain SELECTs on the table. Only ACCESS EXCLUSIVE does.</summary>
    public static bool BlocksReads(this LockMode mode) => mode.ConflictsWith(LockMode.AccessShare);

    /// <summary>True if the lock blocks INSERT, UPDATE and DELETE on the table.</summary>
    public static bool BlocksWrites(this LockMode mode) => mode.ConflictsWith(LockMode.RowExclusive);

    /// <summary>
    /// Returns the table locks each statement takes on the tables it names, one entry per table and
    /// statement with the strongest mode.
    /// <para>
    /// Covered: SELECT (with and without FOR UPDATE and similar), INSERT, UPDATE, DELETE, MERGE, COPY,
    /// TRUNCATE, LOCK, CREATE INDEX, CREATE TRIGGER, CREATE STATISTICS, ALTER TABLE (per sub-command, as
    /// PostgreSQL's AlterTableGetLockLevel decides it), DROP TABLE and similar, VACUUM, ANALYZE, CLUSTER,
    /// REINDEX TABLE, REFRESH MATERIALIZED VIEW, COMMENT ON TABLE or COLUMN, and the table referenced by
    /// a new foreign key.
    /// </para>
    /// <para>
    /// Not covered, because the statement does not name the table or it takes the catalog to tell: locks
    /// on a table reached through a view, an index (DROP INDEX, REINDEX INDEX, ALTER INDEX), a partition
    /// or an inheritance child, and locks taken by triggers and functions. Other statement types give
    /// no entries. Row-level locks are not included.
    /// </para>
    /// </summary>
    public static IReadOnlyList<TableLock> GetLocks(this ParseResult tree)
    {
        ArgumentNullException.ThrowIfNull(tree);

        var locks = new List<TableLock>();
        for (var i = 0; i < tree.Stmts.Count; i++)
            locks.AddRange(LocksOf(tree.Stmts[i].Stmt?.Unwrap(), i));

        return locks;
    }

    /// <summary>Returns the table locks one statement takes. See <see cref="GetLocks(ParseResult)"/>.</summary>
    public static IReadOnlyList<TableLock> GetLocks(this RawStmt statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return LocksOf(statement.Stmt?.Unwrap(), null);
    }

    private static List<TableLock> LocksOf(IMessage? statement, int? index)
    {
        var found = new List<(string? Schema, string Table, LockMode Mode)>();
        if (statement is not null)
            Collect(statement, found);

        // One entry per table, with the strongest mode, in the order first seen.
        return found
            .GroupBy(item => (item.Schema, item.Table))
            .Select(group => new TableLock(group.Key.Schema, group.Key.Table, group.Max(item => item.Mode), index))
            .ToList();
    }

    private static void Collect(IMessage statement, List<(string?, string, LockMode)> found)
    {
        void Add(RangeVar? table, LockMode mode)
        {
            if (table is not null)
                found.Add((table.Schemaname.Length > 0 ? table.Schemaname : null, table.Relname, mode));
        }

        switch (statement)
        {
            case SelectStmt or InsertStmt or UpdateStmt or DeleteStmt or MergeStmt:
                AddQueryLocks(statement, found);
                break;

            case CopyStmt copy:
                Add(copy.Relation, copy.IsFrom ? LockMode.RowExclusive : LockMode.AccessShare);
                if (copy.Query?.Unwrap() is { } query)
                    AddQueryLocks(query, found);
                break;

            case ExplainStmt explain when explain.Query?.Unwrap() is { } explained:
                // EXPLAIN plans the statement, which takes the same table locks as running it.
                Collect(explained, found);
                break;

            case TruncateStmt truncate:
                foreach (var relation in truncate.Relations)
                    Add(relation.RangeVar, LockMode.AccessExclusive);
                break;

            case LockStmt explicitLock:
                foreach (var relation in explicitLock.Relations)
                    Add(relation.RangeVar, (LockMode)explicitLock.Mode);
                break;

            case IndexStmt index:
                Add(index.Relation, index.Concurrent ? LockMode.ShareUpdateExclusive : LockMode.Share);
                break;

            case CreateTrigStmt trigger:
                Add(trigger.Relation, LockMode.ShareRowExclusive);
                Add(trigger.Constrrel, LockMode.ShareRowExclusive);
                break;

            case CreateStatsStmt statistics:
                foreach (var relation in statistics.Relations)
                    Add(relation.RangeVar, LockMode.ShareUpdateExclusive);
                break;

            case CreateStmt create:
                // A new table is not locked against anyone, but a foreign key locks the table it points to.
                foreach (var constraint in create.Descendants<Constraint>())
                {
                    if (constraint.Contype == ConstrType.ConstrForeign)
                        Add(constraint.Pktable, LockMode.ShareRowExclusive);
                }

                break;

            case AlterTableStmt alter when alter.Objtype is ObjectType.ObjectTable or ObjectType.ObjectForeignTable
                or ObjectType.ObjectMatview or ObjectType.ObjectView:
                AddAlterTableLocks(alter, found);
                break;

            case RenameStmt rename when rename.Relation is not null && rename.RelationType != ObjectType.ObjectIndex
                                         && rename.RenameType != ObjectType.ObjectIndex:
                Add(rename.Relation, LockMode.AccessExclusive);
                break;

            case DropStmt drop when drop.RemoveType is ObjectType.ObjectTable or ObjectType.ObjectView
                or ObjectType.ObjectMatview or ObjectType.ObjectForeignTable:
                foreach (var target in drop.Objects)
                    AddByName(target.List?.Items, LockMode.AccessExclusive, found);
                break;

            case VacuumStmt vacuum:
                // VACUUM FULL rewrites the table; plain VACUUM and ANALYZE do not.
                var full = vacuum.IsVacuumcmd && vacuum.Options.Any(option => IsOn(option, "full"));
                foreach (var relation in vacuum.Rels)
                    Add(relation.VacuumRelation?.Relation, full ? LockMode.AccessExclusive : LockMode.ShareUpdateExclusive);
                break;

            case ClusterStmt cluster:
                Add(cluster.Relation, LockMode.AccessExclusive);
                break;

            case ReindexStmt { Kind: ReindexObjectType.ReindexObjectTable } reindex:
                // REINDEX TABLE blocks writes but not reads of the table; CONCURRENTLY blocks neither.
                Add(reindex.Relation, reindex.Params.Any(option => IsOn(option, "concurrently"))
                    ? LockMode.ShareUpdateExclusive
                    : LockMode.Share);
                break;

            case RefreshMatViewStmt refresh:
                Add(refresh.Relation, refresh.Concurrent ? LockMode.Exclusive : LockMode.AccessExclusive);
                break;

            case CommentStmt { Objtype: ObjectType.ObjectTable } comment:
                AddByName(comment.Object?.List?.Items, LockMode.ShareUpdateExclusive, found);
                break;

            case CommentStmt { Objtype: ObjectType.ObjectColumn } comment
                when comment.Object?.List?.Items is { Count: > 1 } parts:
                // The last part is the column.
                AddByName(parts.Take(parts.Count - 1).ToList(), LockMode.ShareUpdateExclusive, found);
                break;
        }
    }

    // SELECT and data-changing statements: the target is locked ROW EXCLUSIVE, everything read is locked
    // ACCESS SHARE, or ROW SHARE under FOR UPDATE and similar.
    private static void AddQueryLocks(IMessage statement, List<(string?, string, LockMode)> found)
    {
        var references = statement.GetReferences();
        var visits = new Dictionary<RangeVar, NodeVisit>(ReferenceEqualityComparer.Instance);
        foreach (var visit in statement.Walk())
        {
            if (visit.Node is RangeVar node)
                visits[node] = visit;
        }

        foreach (var table in references.Tables)
        {
            if (table.IsCte)
                continue;

            var mode = table.Role switch
            {
                TableRole.Write => LockMode.RowExclusive,
                // SELECT ... INTO creates its target, which nobody else can see yet.
                TableRole.Ddl => (LockMode?)null,
                _ => IsUnderLockingClause(visits[table.Node], statement) ? LockMode.RowShare : LockMode.AccessShare
            };

            if (mode is { } known)
                found.Add((table.Schema, table.Name, known));
        }
    }

    // FOR UPDATE and similar apply to the tables in the FROM clause of the SELECT that carries them.
    private static bool IsUnderLockingClause(NodeVisit visit, IMessage top)
    {
        var select = visit.FindAncestor<SelectStmt>() ?? top as SelectStmt;
        if (select is null || select.LockingClause.Count == 0)
            return false;

        var table = (RangeVar)visit.Node;
        var name = table.Alias is { Aliasname.Length: > 0 } alias ? alias.Aliasname : table.Relname;
        foreach (var clause in select.LockingClause)
        {
            var locked = clause.LockingClause.LockedRels;
            // Without OF, every table of the FROM clause is locked.
            if (locked.Count == 0 || locked.Any(item => item.RangeVar?.Relname == name))
                return true;
        }

        return false;
    }

    // The lock of an ALTER TABLE is the strongest any of its sub-commands needs, following
    // AlterTableGetLockLevel in PostgreSQL 18's tablecmds.c.
    private static void AddAlterTableLocks(AlterTableStmt alter, List<(string?, string, LockMode)> found)
    {
        var mode = LockMode.AccessShare;
        var any = false;
        foreach (var item in alter.Cmds)
        {
            if (item.AlterTableCmd is not { } command)
                continue;

            any = true;
            var needed = command.Subtype switch
            {
                AlterTableType.AtEnableTrig or AlterTableType.AtEnableAlwaysTrig or AlterTableType.AtEnableReplicaTrig
                    or AlterTableType.AtEnableTrigAll or AlterTableType.AtEnableTrigUser or AlterTableType.AtDisableTrig
                    or AlterTableType.AtDisableTrigAll or AlterTableType.AtDisableTrigUser => LockMode.ShareRowExclusive,

                AlterTableType.AtAddConstraint or AlterTableType.AtReAddConstraint or AlterTableType.AtReAddDomainConstraint
                    when command.Def?.Constraint is { Contype: ConstrType.ConstrForeign } => LockMode.ShareRowExclusive,

                AlterTableType.AtSetStatistics or AlterTableType.AtClusterOn or AlterTableType.AtDropCluster
                    or AlterTableType.AtSetOptions or AlterTableType.AtResetOptions or AlterTableType.AtValidateConstraint
                    or AlterTableType.AtAttachPartition or AlterTableType.AtDetachPartitionFinalize => LockMode.ShareUpdateExclusive,

                AlterTableType.AtDetachPartition when command.Def?.PartitionCmd is { Concurrent: true } => LockMode.ShareUpdateExclusive,

                AlterTableType.AtSetRelOptions or AlterTableType.AtResetRelOptions => OptionsLock(command.Def?.List?.Items),

                _ => LockMode.AccessExclusive
            };

            if (needed > mode)
                mode = needed;

            // A new foreign key also locks the table it points to.
            if (command.Def?.Constraint is { Contype: ConstrType.ConstrForeign, Pktable: { } referenced })
                found.Add((referenced.Schemaname.Length > 0 ? referenced.Schemaname : null, referenced.Relname, LockMode.ShareRowExclusive));
        }

        if (any && alter.Relation is { } relation)
            found.Add((relation.Schemaname.Length > 0 ? relation.Schemaname : null, relation.Relname, mode));
    }

    // AlterTableGetRelOptionsLockLevel: the strongest lock any of the named options needs.
    private static LockMode OptionsLock(IEnumerable<Node>? options)
    {
        LockMode? mode = null;
        foreach (var option in options ?? [])
        {
            var name = option.DefElem?.Defname;
            if (name is null)
                continue;

            if (AccessExclusiveOptions.Contains(name))
                return LockMode.AccessExclusive;

            if (ShareUpdateExclusiveOptions.Contains(name))
                mode = LockMode.ShareUpdateExclusive;
        }

        // Options this table does not know are treated as needing the strongest lock.
        return mode ?? LockMode.AccessExclusive;
    }

    private static void AddByName(IEnumerable<Node>? parts, LockMode mode, List<(string?, string, LockMode)> found)
    {
        var names = parts?.Select(part => part.String?.Sval).OfType<string>().ToList();
        if (names is { Count: > 0 })
            found.Add((names.Count > 1 ? names[^2] : null, names[^1], mode));
    }

    // An option given as a bare word, or with a true value.
    private static bool IsOn(Node option, string name)
    {
        if (option.DefElem is not { } element || element.Defname != name)
            return false;

        return element.Arg?.Unwrap() switch
        {
            null => true,
            Boolean boolean => boolean.Boolval,
            Integer integer => integer.Ival != 0,
            String text => text.Sval is "true" or "on" or "1",
            _ => true
        };
    }

    private static int Mask(params LockMode[] modes) => modes.Aggregate(0, (mask, mode) => mask | 1 << (int)mode);
}
