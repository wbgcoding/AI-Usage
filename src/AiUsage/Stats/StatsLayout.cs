namespace AiUsage.Stats;

/// <summary>One row of the statistics window: a single full-width section (<see cref="Right"/> empty,
/// exactly one key in <see cref="Left"/>) or two columns side by side, each a stack of one or more
/// sections.</summary>
public sealed class StatsLayoutRow
{
    public List<string> Left { get; set; } = [];

    public List<string> Right { get; set; } = [];
}

/// <summary>Where a dragged section lands relative to the section it is dropped on.</summary>
public enum StatsDropPosition
{
    Above,
    Below,
    Left,
    Right,
}

/// <summary>
/// The arrangement of the statistics window's sections as plain data, free of any WPF type so it can
/// be stored in the settings and tested directly. Every method works on a normalized copy and returns
/// new objects; the input is never changed.
/// </summary>
public static class StatsLayout
{
    /// <summary>The ten section keys in their default order.</summary>
    public static IReadOnlyList<string> SectionKeys { get; } =
        ["monthgrid", "figures", "breakdown", "perday", "cache", "provider", "model", "effort", "projects", "table"];

    /// <summary>The arrangement a fresh install shows.</summary>
    public static List<StatsLayoutRow> Default() =>
    [
        Single("monthgrid"),
        Single("figures"),
        Single("breakdown"),
        Single("perday"),
        Single("cache"),
        new StatsLayoutRow { Left = ["provider", "model", "effort"], Right = ["projects"] },
        Single("table"),
    ];

    /// <summary>Repairs any stored arrangement: drops unknown and repeated keys and empty columns,
    /// splits a one-column row with several sections into single rows, and appends every missing
    /// section as its own row at the end. Null gives the default.</summary>
    public static List<StatsLayoutRow> Normalize(IEnumerable<StatsLayoutRow>? rows)
    {
        if (rows is null)
        {
            return Default();
        }

        var known = new HashSet<string>(SectionKeys);
        var seen = new HashSet<string>();
        var result = new List<StatsLayoutRow>();

        List<string> Clean(IEnumerable<string>? keys)
        {
            var kept = new List<string>();
            foreach (var key in keys ?? [])
            {
                if (key is not null && known.Contains(key) && seen.Add(key))
                {
                    kept.Add(key);
                }
            }

            return kept;
        }

        foreach (var row in rows)
        {
            if (row is null)
            {
                continue;
            }

            var left = Clean(row.Left);
            var right = Clean(row.Right);
            if (left.Count > 0 && right.Count > 0)
            {
                result.Add(new StatsLayoutRow { Left = left, Right = right });
                continue;
            }

            var only = left.Count > 0 ? left : right;
            foreach (var key in only)
            {
                result.Add(Single(key));
            }
        }

        foreach (var key in SectionKeys)
        {
            if (seen.Add(key))
            {
                result.Add(Single(key));
            }
        }

        return result;
    }

    /// <summary>Moves <paramref name="key"/> next to <paramref name="targetKey"/>. Left and Right
    /// open a two-column row only beside a section that is alone in its row; beside a section in a
    /// two-column row they act as Above and Below.</summary>
    public static List<StatsLayoutRow> Move(IReadOnlyList<StatsLayoutRow> rows, string key, string targetKey, StatsDropPosition position)
    {
        var work = Normalize(rows);
        if (key == targetKey || !Contains(work, key) || !Contains(work, targetKey))
        {
            return work;
        }

        Detach(work, key);
        var (rowIndex, onLeft, columnIndex) = Find(work, targetKey);
        var row = work[rowIndex];
        var alone = row.Right.Count == 0;

        if (alone && position is StatsDropPosition.Left or StatsDropPosition.Right)
        {
            work[rowIndex] = position == StatsDropPosition.Left
                ? new StatsLayoutRow { Left = [key], Right = [targetKey] }
                : new StatsLayoutRow { Left = [targetKey], Right = [key] };
        }
        else
        {
            var above = position is StatsDropPosition.Above or StatsDropPosition.Left;
            if (alone)
            {
                work.Insert(above ? rowIndex : rowIndex + 1, Single(key));
            }
            else
            {
                (onLeft ? row.Left : row.Right).Insert(above ? columnIndex : columnIndex + 1, key);
            }
        }

        return Normalize(work);
    }

    /// <summary>Moves a section one step up: swaps inside its column, leaves a two-column row as its
    /// own single row above it, or swaps a single row with the row before it. With
    /// <paramref name="visible"/> given, hidden sections are stepped over (null = all are visible).</summary>
    public static List<StatsLayoutRow> MoveUp(IReadOnlyList<StatsLayoutRow> rows, string key, IReadOnlyCollection<string>? visible = null) =>
        Step(rows, key, -1, visible);

    /// <summary>Mirror image of <see cref="MoveUp"/>.</summary>
    public static List<StatsLayoutRow> MoveDown(IReadOnlyList<StatsLayoutRow> rows, string key, IReadOnlyCollection<string>? visible = null) =>
        Step(rows, key, 1, visible);

    /// <summary>True when the normalized arrangement equals <see cref="Default"/>.</summary>
    public static bool IsDefault(IReadOnlyList<StatsLayoutRow> rows) => AreEqual(Normalize(rows), Default());

    /// <summary>Structural equality: same rows, columns and key order.</summary>
    public static bool AreEqual(IReadOnlyList<StatsLayoutRow> a, IReadOnlyList<StatsLayoutRow> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (!a[i].Left.SequenceEqual(b[i].Left) || !a[i].Right.SequenceEqual(b[i].Right))
            {
                return false;
            }
        }

        return true;
    }

    private static List<StatsLayoutRow> Step(IReadOnlyList<StatsLayoutRow> rows, string key, int direction, IReadOnlyCollection<string>? visible)
    {
        bool Shown(string k) => visible is null || visible.Contains(k);

        var work = Normalize(rows);
        if (!Contains(work, key))
        {
            return work;
        }

        var (rowIndex, onLeft, columnIndex) = Find(work, key);
        var row = work[rowIndex];
        if (row.Right.Count == 0)
        {
            var other = rowIndex + direction;
            while (other >= 0 && other < work.Count && !work[other].Left.Concat(work[other].Right).Any(Shown))
            {
                other += direction;
            }

            if (other >= 0 && other < work.Count)
            {
                (work[rowIndex], work[other]) = (work[other], work[rowIndex]);
            }

            return work;
        }

        var column = onLeft ? row.Left : row.Right;
        var next = columnIndex + direction;
        while (next >= 0 && next < column.Count && !Shown(column[next]))
        {
            next += direction;
        }

        if (next >= 0 && next < column.Count)
        {
            (column[columnIndex], column[next]) = (column[next], column[columnIndex]);
            return work;
        }

        var occupied = Detach(work, key);
        work.Insert(direction < 0 ? rowIndex : rowIndex + occupied, Single(key));
        return Normalize(work);
    }

    /// <summary>Takes <paramref name="key"/> out of its column. An emptied column disappears and the
    /// other column of that row falls apart into single rows. Returns how many rows now sit where the
    /// old row was (0 when the key was alone in its row).</summary>
    private static int Detach(List<StatsLayoutRow> rows, string key)
    {
        var (rowIndex, onLeft, _) = Find(rows, key);
        var row = rows[rowIndex];
        if (row.Right.Count == 0)
        {
            rows.RemoveAt(rowIndex);
            return 0;
        }

        var column = onLeft ? row.Left : row.Right;
        column.Remove(key);
        if (column.Count > 0)
        {
            return 1;
        }

        var rest = onLeft ? row.Right : row.Left;
        rows.RemoveAt(rowIndex);
        rows.InsertRange(rowIndex, rest.Select(Single));
        return rest.Count;
    }

    private static (int Row, bool OnLeft, int Index) Find(List<StatsLayoutRow> rows, string key)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            var at = rows[i].Left.IndexOf(key);
            if (at >= 0)
            {
                return (i, true, at);
            }

            at = rows[i].Right.IndexOf(key);
            if (at >= 0)
            {
                return (i, false, at);
            }
        }

        return (-1, false, -1);
    }

    private static bool Contains(List<StatsLayoutRow> rows, string key) => Find(rows, key).Row >= 0;

    private static StatsLayoutRow Single(string key) => new() { Left = [key] };
}
