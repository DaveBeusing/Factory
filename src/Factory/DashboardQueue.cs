namespace Factory;

internal sealed class DashboardQueue(Action unpause, Func<Task> run, Action<Exception> reportError)
{
    private readonly object _gate = new();
    private bool _running;
    private bool _restartRequested;

    internal bool Start(bool resume)
    {
        lock (_gate)
        {
            if (_running && !resume) return false;
            unpause();
            if (_running)
            {
                _restartRequested = true;
                return true;
            }
            _running = true;
            _ = Task.Run(RunAsync);
            return true;
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                try { await run(); }
                catch (Exception ex)
                {
                    try { reportError(ex); }
                    catch (Exception) { /* Reporting failures cannot retain scheduler ownership. */ }
                }
                lock (_gate)
                {
                    if (!_restartRequested) return;
                    _restartRequested = false;
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                if (_restartRequested)
                {
                    _restartRequested = false;
                    _ = Task.Run(RunAsync);
                }
                else _running = false;
            }
        }
    }
}
