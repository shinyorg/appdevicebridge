using System.Globalization;
using Shiny.AppDeviceBridge.Database.Client;

namespace Shiny.AppDeviceBridge.Database;

/// <summary>
/// A query plan read into one tree shape - <see cref="DatabasePlanNode"/> - so a page draws the same thing whichever engine
/// drew the plan. SQLite's <c>EXPLAIN QUERY PLAN</c> is rows of (id, parent, detail), which is a tree already; this only
/// reads it as one. A driver for another engine reads its own plan into the same nodes.
/// </summary>
/// <remarks>
/// Pure and forgiving: a plan this cannot read as a tree answers null and the rows are shown as they came, rather than a
/// half-built tree that misleads.
/// </remarks>
public static class QueryPlans
{
    /// <summary>SQLite's rows: <c>id, parent, notused, detail</c>.</summary>
    public static DatabasePlanNode[]? FromSqlite(IReadOnlyList<string?[]> rows)
    {
        var nodes = new List<(long Id, long Parent, string Detail)>();

        foreach (var row in rows)
        {
            if (row.Length < 4
                || !Int64.TryParse(row[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                || !Int64.TryParse(row[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parent))
                return null;

            nodes.Add((id, parent, row[3] ?? ""));
        }

        var ids = nodes.Select(x => x.Id).ToHashSet();

        DatabasePlanNode Build((long Id, long Parent, string Detail) node, int depth)
            => new(
                node.Detail,
                null,
                depth > 64
                    ? []
                    : nodes.Where(x => x.Parent == node.Id && x.Id != node.Id).Select(x => Build(x, depth + 1)).ToArray()
            );

        // parent 0 is the top; a parent that is not in the list is treated as the top too, rather than
        // losing the step
        return nodes
            .Where(x => x.Parent == 0 || !ids.Contains(x.Parent))
            .Select(x => Build(x, 0))
            .ToArray();
    }
}
