using KittenIO.Mod.Runtime;

namespace KittenIO.Mod.Game;

internal sealed record TimeWarpCompletion(
    CommandContext Context,
    Guid CommandId,
    GameCommandResult Result);

internal sealed class TimeWarpCoordinator
{
    private const int MinimumConfirmationFrames = 1;
    private const int RefusalFrames = 8;
    private const long ConfirmationTimeoutMs = 2_000;

    private readonly ITimeWarpAdapter _adapter;
    private PendingTimeWarp? _pending;

    public TimeWarpCoordinator(ITimeWarpAdapter adapter) => _adapter = adapter;

    public bool TryBegin(
        CommandContext context,
        Guid commandId,
        TimeWarpCommandKind kind,
        double? rate,
        long frameNumber,
        long nowMs,
        out GameCommandResult? failure)
    {
        failure = null;
        if (_pending is not null)
        {
            failure = GameCommandResult.Failure(
                "busy",
                "Another time-warp transition is awaiting authoritative KSA confirmation.");
            return false;
        }

        var requested = _adapter.RequestTimeWarp(kind, rate);
        if (!requested.Ok || requested.TargetRate is not { } targetRate ||
            requested.PreviousRate is not { } previousRate || requested.SimulationIdentity is null)
        {
            failure = GameCommandResult.Failure(requested.Code, requested.Message);
            return false;
        }

        _pending = new PendingTimeWarp(
            context,
            commandId,
            previousRate,
            targetRate,
            requested.SimulationIdentity,
            frameNumber,
            checked(nowMs + ConfirmationTimeoutMs));
        return true;
    }

    public bool TryAdvance(long frameNumber, long nowMs, out TimeWarpCompletion? completion)
    {
        completion = null;
        var pending = _pending;
        if (pending is null || frameNumber <= pending.IssuedFrame + MinimumConfirmationFrames - 1) return false;

        var observation = _adapter.ObserveTimeWarp();
        if (observation.SimulationAvailable && !observation.MenuPauseActive && !observation.AutoWarpActive &&
            ReferenceEquals(observation.SimulationIdentity, pending.SimulationIdentity) &&
            observation.CurrentRate is { } currentRate &&
            PinnedKsaTimeWarpController.Approximately(currentRate, pending.TargetRate))
        {
            completion = Complete(
                pending,
                GameCommandResult.Success(new
                {
                    rate = currentRate,
                    previous_rate = pending.PreviousRate,
                    confirmed_frame = frameNumber,
                }));
            return true;
        }

        if (observation.MenuPauseActive)
        {
            completion = Complete(
                pending,
                GameCommandResult.Failure(
                    "simulation_paused",
                    "KSA's menu pause took ownership before the transition could be confirmed."));
            return true;
        }
        if (observation.SimulationAvailable &&
            !ReferenceEquals(observation.SimulationIdentity, pending.SimulationIdentity))
        {
            completion = Complete(
                pending,
                GameCommandResult.Failure(
                    "simulation_unavailable",
                    "KSA changed simulation scenes before the transition could be confirmed."));
            return true;
        }

        var observedFrames = frameNumber - pending.IssuedFrame;
        if (nowMs < pending.DeadlineMs && observedFrames < RefusalFrames) return false;

        completion = Complete(
            pending,
            observation.SimulationAvailable && observation.CurrentRate is not null
                ? GameCommandResult.Failure(
                    "time_warp_refused",
                    "KSA did not report the requested simulation speed on a later authoritative frame.")
                : GameCommandResult.Failure(
                    "simulation_unavailable",
                    "KSA simulation state became unavailable before the transition could be confirmed."));
        return true;
    }

    public void CancelAll() => _pending = null;

    private TimeWarpCompletion Complete(PendingTimeWarp pending, GameCommandResult result)
    {
        _pending = null;
        return new TimeWarpCompletion(pending.Context, pending.CommandId, result);
    }

    private sealed record PendingTimeWarp(
        CommandContext Context,
        Guid CommandId,
        double PreviousRate,
        double TargetRate,
        object SimulationIdentity,
        long IssuedFrame,
        long DeadlineMs);
}
