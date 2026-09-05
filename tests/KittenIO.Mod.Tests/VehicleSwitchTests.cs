using System.Text.Json;
using KittenIO.Mod;
using KittenIO.Mod.Control;
using KittenIO.Mod.Diagnostics;
using KittenIO.Mod.Game;
using KittenIO.Mod.Runtime;
using KittenIO.Protocol;

internal static class VehicleSwitchTests
{
    public static void RunAll()
    {
        TestCatalogCapacityAndOrder();
        TestLeaseBarrier();
        TestExactSwitchRequestParameters();
        TestSwitchResponseReplayIsContextScoped();
        TestChangedVehicleConfirmation();
        TestAlreadyActiveConfirmation();
        TestFailureTimeoutAndCancellation();
    }

    private static void TestExactSwitchRequestParameters()
    {
        var accepted = ParseSwitchParameters("{\"vehicle_id\":\"Rocket_Gate\"}");
        Assert(accepted.Ok && accepted.VehicleId == "Rocket_Gate" && accepted.ErrorCode is null,
            "the switch request must preserve the exact opaque vehicle ID");

        foreach (var invalid in new[]
        {
            "null",
            "[]",
            "{}",
            "{\"Vehicle_id\":\"Rocket_Gate\"}",
            "{\"vehicle_id\":42}",
            "{\"vehicle_id\":\"\"}",
            "{\"vehicle_id\":\"   \"}",
            "{\"vehicle_id\":\"Rocket_Gate\",\"extra\":true}",
            "{\"vehicle_id\":\"Rocket_Gate\",\"vehicle_id\":\"Banjo\"}",
        })
        {
            var rejected = ParseSwitchParameters(invalid);
            Assert(!rejected.Ok && rejected.VehicleId is null && rejected.ErrorCode == "invalid_params",
                $"invalid switch params were not rejected canonically: {invalid}");
        }
    }

    private static void TestSwitchResponseReplayIsContextScoped()
    {
        var game = GameInstanceId.New();
        var controller = EphemeralControllerId.New();
        var session = new ProtocolSessionId(Guid.NewGuid());
        var context = Context(game, controller, session, 4, 100);
        var commandId = Guid.Parse("55555555-5555-4555-8555-555555555555");
        var registry = new SideEffectRegistry(capacity: 4);

        Assert(registry.TryBegin(context, commandId, out _) == SideEffectAdmission.Admitted,
            "the first switch command ID should be admitted");
        Assert(registry.TryBegin(context, commandId, out _) == SideEffectAdmission.InProgress,
            "an in-progress duplicate switch command must not enqueue KSA twice");
        var completed = new ResponsePayload
        {
            Ok = true,
            Result = new { vehicle_id = "Rocket_Gate" },
        };
        registry.Complete(context, commandId, completed);
        Assert(registry.TryBegin(context, commandId, out var replay) == SideEffectAdmission.Replay &&
               ReferenceEquals(replay, completed),
            "a completed duplicate switch must replay the exact cached response");

        var nextSession = Context(
            game,
            controller,
            new ProtocolSessionId(Guid.NewGuid()),
            4,
            100);
        Assert(registry.TryBegin(nextSession, commandId, out _) == SideEffectAdmission.Admitted,
            "switch command IDs must remain scoped to the exact protocol session");
    }

    private static void TestCatalogCapacityAndOrder()
    {
        var candidates = Enumerable.Range(0, 70)
            .Reverse()
            .Select(index => new EntityReference
            {
                Id = $"vehicle-{index:D3}",
                Name = $"Vehicle {index}",
                Kind = "vehicle",
            })
            .ToArray();
        var selection = VehicleCatalogPolicy.Select("vehicle-069", candidates);
        Assert(selection.Vehicles.Count == VehicleCatalogPolicy.MaximumVehicles,
            "the public catalog projection must remain bounded to 64 vehicles");
        Assert(selection.OmittedCount == 6,
            "catalog overflow should report the exact omitted vehicle count");
        Assert(selection.Vehicles[0].Id == "vehicle-069",
            "the active vehicle must be admitted first");
        Assert(selection.Vehicles.Skip(1).Select(item => item.Id)
                .SequenceEqual(Enumerable.Range(0, 63).Select(index => $"vehicle-{index:D3}")),
            "non-active vehicles must use opaque-ID ordinal order");

        Throws<InvalidOperationException>(() => VehicleCatalogPolicy.Select(
            null,
            [
                new EntityReference { Id = "duplicate", Name = "One", Kind = "vehicle" },
                new EntityReference { Id = "duplicate", Name = "Two", Kind = "vehicle" },
            ]), "duplicate vehicle IDs must fail catalog capture instead of becoming ambiguous handles");
    }

    private static void TestLeaseBarrier()
    {
        var game = GameInstanceId.New();
        var session = new ProtocolSessionId(Guid.NewGuid());
        var controller = EphemeralControllerId.New();
        var context = Context(game, controller, session, 0, 100);
        var coordinator = new ControlCoordinator(750, new BridgeDiagnostics(), game);
        coordinator.SetControlAvailability(true);
        coordinator.UpdateActiveVehicle(Observation("active", 1));
        Assert(coordinator.TryAcquire(context, "active", 100, out var lease, out _),
            "test lease should acquire");
        Assert(!coordinator.TryEnterVehicleSwitch(out var conflict) && conflict?.Code == "lease_conflict",
            "vehicle switching must reject an active control lease");
        Assert(coordinator.Release(lease!.LeaseId, "test release"),
            "test lease should release explicitly");
        Assert(coordinator.TryEnterVehicleSwitch(out _),
            "released control should allow the switch barrier to enter");
        Assert(coordinator.VehicleSwitchPending,
            "the barrier must publish pending state");
        Assert(!coordinator.TryAcquire(context, "active", 101, out _, out var busy) && busy?.Code == "busy",
            "lease acquisition must remain denied during a switch transition");
        Assert(!coordinator.TryEnterVehicleSwitch(out busy) && busy?.Code == "busy",
            "a second switch transition must be rejected");
        coordinator.ExitVehicleSwitch();
        Assert(!coordinator.VehicleSwitchPending &&
               coordinator.TryAcquire(context, "active", 102, out _, out _),
            "leases should become available only after the switch barrier exits");
    }

    private static void TestChangedVehicleConfirmation()
    {
        var game = GameInstanceId.New();
        var session = new ProtocolSessionId(Guid.NewGuid());
        var controller = EphemeralControllerId.New();
        var expectedVehicle = new object();
        var expectedFlightComputer = new object();
        var adapter = new FakeSwitchAdapter
        {
            ActiveVehicle = Observation("old", 3),
            RequestResult = SwitchSuccess(
                "target",
                alreadyActive: false,
                expectedVehicle,
                expectedFlightComputer),
        };
        var barrier = new FakeLeaseBarrier();
        var coordinator = new ActiveVehicleSwitchCoordinator(adapter, barrier, game);
        var context = Context(game, controller, session, 1, 100);
        Assert(coordinator.TryBegin(context, Guid.NewGuid(), "target", 10, 100, out _),
            "a valid switch should be admitted");
        Assert(barrier.Pending && adapter.RequestCount == 1,
            "admission should enter the lease barrier and enqueue KSA exactly once");
        Assert(!coordinator.TryAdvance(10, 101, out _),
            "the enqueueing frame cannot count as authoritative confirmation");

        adapter.ActiveVehicle = Observation(
            "target", 3, vehicleInstance: expectedVehicle, flightComputerInstance: expectedFlightComputer);
        Assert(!coordinator.TryAdvance(11, 102, out _),
            "a changed target without an advanced authority epoch must not confirm");
        adapter.ActiveVehicle = Observation(
            "target", 4, vehicleInstance: expectedVehicle, flightComputerInstance: expectedFlightComputer);
        Assert(!coordinator.TryAdvance(12, 103, out _),
            "one verified later frame is insufficient");
        Assert(!coordinator.TryAdvance(12, 104, out _),
            "the same game frame cannot be counted twice");
        adapter.ActiveVehicle = Observation(
            "target", 4, vehicleInstance: expectedVehicle, flightComputerInstance: new object());
        Assert(!coordinator.TryAdvance(13, 105, out _),
            "a different flight-computer binding on the same vehicle must restart confirmation");
        adapter.ActiveVehicle = Observation(
            "target", 4, vehicleInstance: expectedVehicle, flightComputerInstance: expectedFlightComputer);
        Assert(!coordinator.TryAdvance(14, 106, out _),
            "the restored exact vehicle/flight-computer pair needs its first confirmation");
        adapter.ActiveVehicle = Observation(
            "target", 4, vehicleInstance: new object(), flightComputerInstance: expectedFlightComputer);
        Assert(!coordinator.TryAdvance(15, 107, out _),
            "a different KSA vehicle instance must independently restart confirmation");
        adapter.ActiveVehicle = Observation(
            "target", 4, vehicleInstance: expectedVehicle, flightComputerInstance: expectedFlightComputer);
        Assert(!coordinator.TryAdvance(16, 108, out _),
            "the exact pair needs a first confirmation after either identity changes");
        adapter.ActiveVehicle = Observation(
            "target", 4, vehicleInstance: expectedVehicle, flightComputerInstance: expectedFlightComputer);
        Assert(coordinator.TryAdvance(17, 109, out var completion) && completion?.Result.Ok == true,
            "two later frames for one exact instance and authority epoch should complete");
        Assert(completion!.Vehicle.Value == "target" &&
               completion.Context.Session == session &&
               !barrier.Pending,
            "completion must retain exact request context and release the lease barrier");
    }

    private static void TestAlreadyActiveConfirmation()
    {
        var game = GameInstanceId.New();
        var session = new ProtocolSessionId(Guid.NewGuid());
        var expectedVehicle = new object();
        var expectedFlightComputer = new object();
        var adapter = new FakeSwitchAdapter
        {
            ActiveVehicle = Observation(
                "target", 8, vehicleInstance: expectedVehicle, flightComputerInstance: expectedFlightComputer),
            RequestResult = SwitchSuccess(
                "target",
                alreadyActive: true,
                expectedVehicle,
                expectedFlightComputer),
        };
        var barrier = new FakeLeaseBarrier();
        var coordinator = new ActiveVehicleSwitchCoordinator(adapter, barrier, game);
        Assert(coordinator.TryBegin(
                Context(game, EphemeralControllerId.New(), session, 0, 1),
                Guid.NewGuid(),
                "target",
                20,
                1,
                out _),
            "an already-active target should still enter authoritative confirmation");
        Assert(!coordinator.TryAdvance(21, 2, out _) &&
               coordinator.TryAdvance(22, 3, out var completion) &&
               completion?.Result.Ok == true,
            "already-active success should require two fresh frames without demanding a new epoch");
    }

    private static void TestFailureTimeoutAndCancellation()
    {
        var game = GameInstanceId.New();
        var session = new ProtocolSessionId(Guid.NewGuid());
        var controller = EphemeralControllerId.New();
        var adapter = new FakeSwitchAdapter
        {
            ActiveVehicle = Observation("old", 1),
            RequestResult = SwitchSuccess("target", alreadyActive: false),
        };
        var barrier = new FakeLeaseBarrier();
        var coordinator = new ActiveVehicleSwitchCoordinator(adapter, barrier, game);
        var context = Context(game, controller, session, 0, 100);
        Assert(coordinator.TryBegin(context, Guid.NewGuid(), "target", 1, 100, out _),
            "first transition should begin");
        Assert(!coordinator.TryBegin(context, Guid.NewGuid(), "other", 1, 100, out var busy) && busy?.Code == "busy",
            "simultaneous switch requests must fail explicitly");
        adapter.ValidationResult = GameCommandResult.Failure(
            "vehicle_not_found",
            "target disappeared");
        Assert(coordinator.TryAdvance(2, 101, out var disappeared) &&
               disappeared?.Result.Code == "vehicle_not_found" &&
               !barrier.Pending,
            "target disappearance must finish the exact pending transition safely");

        adapter.ValidationResult = GameCommandResult.Success();
        Assert(coordinator.TryBegin(context, Guid.NewGuid(), "target", 3, 200, out _),
            "another transition should begin after a completed failure");
        Assert(coordinator.TryAdvance(4, 5_200, out var timedOut) &&
               timedOut?.Result.Code == "switch_failed" &&
               !barrier.Pending,
            "the five-second monotonic deadline must release a stalled transition");

        Assert(coordinator.TryBegin(context, Guid.NewGuid(), "target", 5, 6_000, out _),
            "transition should begin for cancellation testing");
        Assert(!coordinator.CancelSession(new ProtocolSessionId(Guid.NewGuid())) && barrier.Pending,
            "an unrelated session cannot cancel the pending transition");
        Assert(coordinator.CancelSession(session) && !barrier.Pending && !coordinator.IsPending,
            "disconnect must cancel only the exact session and leave no lease barrier");

        barrier.Conflict = true;
        Assert(!coordinator.TryBegin(context, Guid.NewGuid(), "target", 6, 7_000, out var conflict) &&
               conflict?.Code == "lease_conflict",
            "the coordinator must surface the lease barrier's conflict without invoking KSA");

        foreach (var code in new[]
        {
            "vehicle_not_found",
            "vehicle_uncontrollable",
            "switch_unavailable",
            "switch_failed",
        })
        {
            var failedAdapter = new FakeSwitchAdapter
            {
                ActiveVehicle = Observation("old", 1),
                RequestResult = VehicleSwitchRequestResult.Failure(code, $"injected {code}"),
            };
            var failedBarrier = new FakeLeaseBarrier();
            var failedCoordinator = new ActiveVehicleSwitchCoordinator(
                failedAdapter,
                failedBarrier,
                game);
            Assert(!failedCoordinator.TryBegin(
                    context,
                    Guid.NewGuid(),
                    "target",
                    7,
                    8_000,
                    out var failure) &&
                   failure?.Code == code &&
                   !failedBarrier.Pending,
                $"the coordinator must preserve the adapter failure code {code}");
        }
    }

    private static CommandContext Context(
        GameInstanceId game,
        EphemeralControllerId controller,
        ProtocolSessionId session,
        long sequence,
        long nowMs) => new(
        game,
        controller,
        session,
        ProtocolConstants.MessageTypes.Request,
        sequence,
        nowMs,
        $"request-{sequence}");

    private static ActiveVehicleObservation Observation(
        string id,
        long epoch,
        bool controllable = true,
        object? vehicleInstance = null,
        object? flightComputerInstance = null)
    {
        vehicleInstance ??= new object();
        flightComputerInstance ??= new object();
        return new ActiveVehicleObservation(
            new VehicleId(id),
            id,
            controllable,
            controllable ? AuthorityIdentity.KsaLocalControlledInput : null,
            VehicleInstanceIdentities.For(vehicleInstance),
            FlightComputerInstanceIdentities.For(flightComputerInstance),
            new AuthorityEpoch(epoch));
    }

    private static VehicleSwitchRequestResult SwitchSuccess(
        string id,
        bool alreadyActive,
        object? vehicleInstance = null,
        object? flightComputerInstance = null)
    {
        vehicleInstance ??= new object();
        flightComputerInstance ??= new object();
        return VehicleSwitchRequestResult.Success(
            new VehicleId(id),
            alreadyActive,
            VehicleInstanceIdentities.For(vehicleInstance),
            FlightComputerInstanceIdentities.For(flightComputerInstance));
    }

    private static (bool Ok, string? VehicleId, string? ErrorCode) ParseSwitchParameters(string json)
    {
        using var document = JsonDocument.Parse(json);
        var ok = KittenIOMod.TryExactVehicleSwitchParameters(
            document.RootElement,
            out var vehicleId,
            out var error);
        return (ok, vehicleId, error?.Error?.Code);
    }

    private static void Throws<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakeSwitchAdapter : IActiveVehicleSwitchAdapter
    {
        public bool ActiveVehicleSwitchContractVerified => true;

        public string ActiveVehicleSwitchContractFailureReason => string.Empty;

        public ActiveVehicleObservation ActiveVehicle { get; set; } = Observation("old", 0);

        public GameCommandResult ValidationResult { get; set; } = GameCommandResult.Success();

        public VehicleSwitchRequestResult RequestResult { get; set; } =
            VehicleSwitchRequestResult.Failure("switch_unavailable", "not configured");

        public int RequestCount { get; private set; }

        public GameCommandResult ValidateActiveVehicleSwitchTarget(string vehicleId) => ValidationResult;

        public VehicleSwitchRequestResult RequestActiveVehicleSwitch(string vehicleId)
        {
            RequestCount++;
            return RequestResult;
        }
    }

    private sealed class FakeLeaseBarrier : IVehicleSwitchLeaseBarrier
    {
        public bool Conflict { get; set; }

        public bool Pending { get; private set; }

        public bool TryEnterVehicleSwitch(out ProtocolError? error)
        {
            if (Conflict)
            {
                error = new ProtocolError("lease_conflict", "lease active");
                return false;
            }
            if (Pending)
            {
                error = new ProtocolError("busy", "switch pending");
                return false;
            }
            Pending = true;
            error = null;
            return true;
        }

        public void ExitVehicleSwitch() => Pending = false;
    }
}
