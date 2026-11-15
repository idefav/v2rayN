namespace ServiceLib.Models.Dto;

public enum ConnectionStage { Preparing, Stopping, Waiting, Starting, Verifying, ApplyingProxy, Completed, Failed, Cancelled }

/// <summary>Lifecycle facts, independent of the user's selected settings.</summary>
public sealed record ConnectionTransition(ConnectionStage Stage, bool TargetTun, string? Detail = null,
    int WaitSeconds = 0, bool Disconnecting = false, bool UpdatingProxy = false)
{
    private static long _nextOperationId;
    public long OperationId { get; init; }
    public bool IsBackground { get; init; }
    public static long NewOperationId() => Interlocked.Increment(ref _nextOperationId);
    public bool IsTerminal => Stage is ConnectionStage.Completed or ConnectionStage.Failed or ConnectionStage.Cancelled;
}
