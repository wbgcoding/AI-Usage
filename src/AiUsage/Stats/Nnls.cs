namespace AiUsage.Stats;

/// <summary>
/// Non-negative least squares: the vector x with every entry at least zero that makes A·x as close to
/// b as possible (Lawson and Hanson's active-set method). Meant for a handful of columns and a few
/// thousand rows; each step solves the small normal equations of the columns currently in play.
/// </summary>
public static class Nnls
{
    private const double Tolerance = 1e-10;

    /// <summary>Solves for x ≥ 0 minimizing |A·x - b|. <paramref name="rows"/> holds one array per row,
    /// all of the same length. Columns and b are scaled to unit length first, so a column counted in
    /// millions and one counted in ones solve equally well; the result is scaled back. A column that is
    /// all zero stays at zero.</summary>
    public static double[] Solve(IReadOnlyList<double[]> rows, IReadOnlyList<double> b)
    {
        var rowCount = rows.Count;
        var columnCount = rowCount == 0 ? 0 : rows[0].Length;
        var result = new double[columnCount];
        if (rowCount == 0 || columnCount == 0)
            return result;

        var columnScale = new double[columnCount];
        for (var j = 0; j < columnCount; j++)
        {
            var sum = 0.0;
            for (var i = 0; i < rowCount; i++)
                sum += rows[i][j] * rows[i][j];
            columnScale[j] = Math.Sqrt(sum);
        }

        var bScale = Math.Sqrt(b.Sum(value => value * value));
        if (bScale == 0)
            return result;

        var a = new double[rowCount][];
        var target = new double[rowCount];
        for (var i = 0; i < rowCount; i++)
        {
            a[i] = new double[columnCount];
            for (var j = 0; j < columnCount; j++)
                a[i][j] = columnScale[j] == 0 ? 0 : rows[i][j] / columnScale[j];
            target[i] = b[i] / bScale;
        }

        var x = SolveScaled(a, target, columnCount);
        for (var j = 0; j < columnCount; j++)
            result[j] = columnScale[j] == 0 ? 0 : x[j] * bScale / columnScale[j];
        return result;
    }

    private static double[] SolveScaled(double[][] a, double[] b, int columnCount)
    {
        var x = new double[columnCount];
        var inPlay = new bool[columnCount];
        var blocked = new bool[columnCount];
        var guard = 0;

        while (guard++ < 10 * columnCount + 10)
        {
            var gradient = Gradient(a, b, x);
            var next = -1;
            for (var j = 0; j < columnCount; j++)
            {
                if (!inPlay[j] && !blocked[j] && gradient[j] > Tolerance && (next < 0 || gradient[j] > gradient[next]))
                    next = j;
            }

            if (next < 0)
                break;

            inPlay[next] = true;
            for (var inner = 0; inner <= columnCount + 1; inner++)
            {
                var s = SolveSubset(a, b, inPlay);
                if (s is null)
                {
                    // Dependent on the columns already in play: leave this one out for good.
                    inPlay[next] = false;
                    blocked[next] = true;
                    break;
                }

                var allPositive = true;
                for (var j = 0; j < columnCount; j++)
                {
                    if (inPlay[j] && s[j] <= Tolerance)
                        allPositive = false;
                }

                if (allPositive)
                {
                    x = s;
                    break;
                }

                // Step as far toward s as stays non-negative, then drop what reached zero.
                var alpha = double.MaxValue;
                for (var j = 0; j < columnCount; j++)
                {
                    if (inPlay[j] && s[j] <= Tolerance)
                    {
                        var distance = x[j] - s[j];
                        alpha = Math.Min(alpha, distance > 0 ? x[j] / distance : 0);
                    }
                }

                for (var j = 0; j < columnCount; j++)
                    x[j] += alpha * (s[j] - x[j]);
                for (var j = 0; j < columnCount; j++)
                {
                    if (inPlay[j] && x[j] <= Tolerance)
                    {
                        inPlay[j] = false;
                        x[j] = 0;
                    }
                }
            }
        }

        return x;
    }

    /// <summary>Aᵀ(b - A·x): how much each column could still improve the fit.</summary>
    private static double[] Gradient(double[][] a, double[] b, double[] x)
    {
        var columnCount = x.Length;
        var gradient = new double[columnCount];
        for (var i = 0; i < a.Length; i++)
        {
            var residual = b[i];
            for (var j = 0; j < columnCount; j++)
                residual -= a[i][j] * x[j];
            for (var j = 0; j < columnCount; j++)
                gradient[j] += a[i][j] * residual;
        }

        return gradient;
    }

    /// <summary>Unconstrained least squares on the columns in play (the rest stay zero), through the
    /// normal equations; null when those columns are linearly dependent.</summary>
    private static double[]? SolveSubset(double[][] a, double[] b, bool[] inPlay)
    {
        var columns = Enumerable.Range(0, inPlay.Length).Where(j => inPlay[j]).ToArray();
        var size = columns.Length;
        var matrix = new double[size, size + 1];
        for (var p = 0; p < size; p++)
        {
            for (var q = 0; q < size; q++)
            {
                var sum = 0.0;
                for (var i = 0; i < a.Length; i++)
                    sum += a[i][columns[p]] * a[i][columns[q]];
                matrix[p, q] = sum;
            }

            var rhs = 0.0;
            for (var i = 0; i < a.Length; i++)
                rhs += a[i][columns[p]] * b[i];
            matrix[p, size] = rhs;
        }

        for (var pivot = 0; pivot < size; pivot++)
        {
            var best = pivot;
            for (var r = pivot + 1; r < size; r++)
            {
                if (Math.Abs(matrix[r, pivot]) > Math.Abs(matrix[best, pivot]))
                    best = r;
            }

            if (Math.Abs(matrix[best, pivot]) < 1e-12)
                return null;

            if (best != pivot)
            {
                for (var c = 0; c <= size; c++)
                    (matrix[pivot, c], matrix[best, c]) = (matrix[best, c], matrix[pivot, c]);
            }

            for (var r = pivot + 1; r < size; r++)
            {
                var factor = matrix[r, pivot] / matrix[pivot, pivot];
                for (var c = pivot; c <= size; c++)
                    matrix[r, c] -= factor * matrix[pivot, c];
            }
        }

        var solution = new double[inPlay.Length];
        for (var p = size - 1; p >= 0; p--)
        {
            var value = matrix[p, size];
            for (var q = p + 1; q < size; q++)
                value -= matrix[p, q] * solution[columns[q]];
            solution[columns[p]] = value / matrix[p, p];
        }

        return solution;
    }
}
