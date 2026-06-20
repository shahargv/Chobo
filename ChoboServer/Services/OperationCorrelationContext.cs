using System.Threading;

namespace ChoboServer.Services;

public static class OperationCorrelationContext
{
    private static readonly AsyncLocal<string?> CurrentValue = new();

    public static string? CurrentOperationId => CurrentValue.Value;

    public static IDisposable Push(string operationId)
    {
        var previous = CurrentValue.Value;
        CurrentValue.Value = operationId;
        return new Scope(previous);
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        public void Dispose() => CurrentValue.Value = previous;
    }
}
