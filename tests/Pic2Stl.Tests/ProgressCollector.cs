namespace Pic2Stl.Tests;

/// <summary>
/// Collects progress reports safely: callbacks arrive on thread-pool threads, so a bare List would corrupt.
/// </summary>
internal sealed class ProgressCollector<T> : IProgress<T>
{
    private readonly List<T> _values = [];

    public void Report(T value)
    {
        lock (_values)
            _values.Add(value);
    }

    public IReadOnlyList<T> Snapshot()
    {
        lock (_values)
            return _values.ToArray();
    }

    public async Task<IReadOnlyList<T>> WaitForAsync(Func<IReadOnlyList<T>, bool> predicate, int timeoutMilliseconds = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            var snapshot = Snapshot();
            if (predicate(snapshot))
                return snapshot;

            await Task.Delay(20);
        }

        return Snapshot();
    }
}
