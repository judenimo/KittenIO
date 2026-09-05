using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Brutal.Numerics;
using KittenIO.Mod;
using KittenIO.Mod.Configuration;
using KittenIO.Mod.Control;
using KittenIO.Mod.Diagnostics;
using KittenIO.Mod.Game;
using KittenIO.Mod.Networking;
using KittenIO.Mod.Patching;
using KittenIO.Mod.Runtime;
using KittenIO.Protocol;

var ksaRuntimeDirectory = Environment.GetEnvironmentVariable("KITTENIO_KSA_DIR") ?? Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Programs",
    "Kitten Space Agency");
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var candidate = Path.Combine(ksaRuntimeDirectory, $"{name.Name}.dll");
    return File.Exists(candidate) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate) : null;
};

var failures = new List<string>();
Run("validated internal context values and local authority", TestContextValues);
Run("control authority follows vehicle capability across view changes", TestVerifiedControlAuthority);
Run("independent lease routes, sequences, deadlines, and release", TestIndependentVehicleLeases);
Run("lease registry capacity and scoped cleanup", TestLeaseCapacityAndScopedCleanup);
Run("canonical coordinator admits one active lease and enforces dead-man", TestCoordinatorFacade);
Run("requested, accepted, and applied controls remain distinct", TestCoordinatorFeedback);
Run("authority epoch change releases control", TestAuthorityEpochRelease);
Run("side effects are context-scoped and non-evicting", TestScopedSideEffects);
Run("latest-by-key telemetry coalesces and rotates fairly", TestLatestScheduler);
Run("reliable bursts cannot starve telemetry", TestReliableFairness);
Run("inbound direction precedes finite sequence tracking", TestInboundSequencePolicy);
Run("control admission preserves the newest semantically valid frame", TestInboundControlAdmission);
Run("reliable overflow remains explicit", TestReliableOverflowIsExplicit);
Run("loopback WebSocket host authenticates, arbitrates, and reconnects", TransportHostTests.RunAll);
Run("bounded reflection metadata preserves alias, overload, and transient semantics", TestReflectionMetadata);
Run("reflection setter supports boxed inputs", TestReflectionSetter);
Run("lookup enumeration prefers integer GetIndex", TestLookupEnumeration);
Run("weak fallback IDs are stable and collision-free", TestWeakFallbackIds);
Run("catalog comparison detects renames and forced publication", TestCatalogComparison);
Run("fresh target catalogs revoke prior-session and removed entities", TestCatalogTargetReplacement);
Run("simulation clock uses authoritative KSA elapsed time", TestSimulationClockContract);
Run("bounded vehicle catalog and across-frame active switching", VehicleSwitchTests.RunAll);
Run("pinned propulsion telemetry validates availability and physical scalars", TestPropulsionTelemetryContract);
Run("current KSA control patch contract", TestKsaPatchContract);
Run("KSA overlay is typed, atomic, scoped, and fault-deferred", TestKsaControlledStateOverlay);
Run("future KSA build fails closed", TestFutureKsaBuildFailsClosed);
Run("time-warp requests are strict and authoritatively confirmed", TestTimeWarpCoordinator);
Run("time-warp disconnect cleanup preserves KSA ownership", TestTimeWarpPersistentOwnership);
Run("unchanged vehicle diagnostics avoid repeated formatting", TestDiagnosticFormatting);
Run("unload steps isolate failures", TestUnloadFailureIsolation);
Run("unsafe telemetry cadence cannot hold control", TestControlCadence);
Run("mod does not render a diagnostics overlay", TestNoDiagnosticsOverlay);

if (failures.Count > 0)
{
    Console.Error.WriteLine($"{failures.Count} KittenIO mod test(s) failed:");
    foreach (var failure in failures) Console.Error.WriteLine($"- {failure}");
    return 1;
}

Console.WriteLine("All KittenIO mod tests passed.");
return 0;

void Run(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{name}: {exception.Message}");
    }
}

static void TestContextValues()
{
    Throws<ArgumentException>(() => new GameInstanceId(Guid.Empty), "empty game instance IDs must fail");
    Throws<ArgumentException>(() => new EphemeralControllerId(Guid.Empty), "empty controller IDs must fail");
    Throws<ArgumentException>(() => new ProtocolSessionId(Guid.Empty), "empty session IDs must fail");
    Throws<ArgumentException>(() => new VehicleId(" "), "blank vehicle IDs must fail");
    Throws<ArgumentOutOfRangeException>(() => new AuthorityEpoch(-1), "negative authority epochs must fail");
    Assert(AuthorityIdentity.KsaLocalControlledInput.Value == "ksa.local-controlled-input",
        "the only verified authority marker changed");

    var route = Route(
        GameInstanceId.New(),
        EphemeralControllerId.New(),
        new ProtocolSessionId(Guid.NewGuid()),
        "vehicle",
        3);
    Assert(route.IsVerifiedLocalRoute, "the verified local route should be accepted");
    var unverified = route with { Authority = new AuthorityIdentity("unverified") };
    Assert(!unverified.IsVerifiedLocalRoute,
        "unverified authority must not become a local route");
    Assert(typeof(IKsaGameAdapter).GetProperties().All(property => property.PropertyType != typeof(object)),
        "the game adapter interface must not expose KSA object references");
}

static void TestVerifiedControlAuthority()
{
    Assert(KsaGameAdapter.HasVerifiedControlAuthority(true),
        "a capable selected vehicle should retain authority across camera modes");
    Assert(!KsaGameAdapter.HasVerifiedControlAuthority(false),
        "an uncontrollable vehicle must not receive authority");
    Assert(!KsaGameAdapter.HasVerifiedControlAuthority(null),
        "an unverified vehicle capability must fail closed");
}

static void TestIndependentVehicleLeases()
{
    var registry = new VehicleLeaseRegistry(capacity: 4);
    var game = GameInstanceId.New();
    var session = new ProtocolSessionId(Guid.NewGuid());
    var firstRoute = Route(game, EphemeralControllerId.New(), session, "vehicle-a", 1);
    var secondRoute = Route(game, EphemeralControllerId.New(), session, "vehicle-b", 7);

    Assert(!registry.TryAcquire(
            firstRoute with { Authority = new AuthorityIdentity("unverified") },
            100,
            10,
            out _,
            out var authorityError) && authorityError?.Code == "invalid_authority",
        "unverified authority must never acquire a lease");

    Assert(registry.TryAcquire(firstRoute, 100, 10, out var first, out _), "first route should acquire");
    Assert(registry.TryAcquire(secondRoute, 100, 10, out var second, out _), "second route should acquire independently");
    Assert(first is not null && second is not null, "leases should be returned");
    Assert(registry.TryAccept(firstRoute, Frame(first!.LeaseId, 0), 101, out _, out _), "first sequence zero should pass");
    Assert(registry.TryAccept(secondRoute, Frame(second!.LeaseId, 0), 102, out _, out _), "second sequence zero should pass independently");
    Assert(registry.TryGetFreshOverride(first.LeaseId, firstRoute, 102, out var firstOverride) && firstOverride?.ControlSequence == 0,
        "each route should retain its own accepted override");
    Assert(!registry.TryAccept(firstRoute, Frame(first.LeaseId, 0), 103, out _, out var stale), "duplicate first sequence should fail");
    Assert(stale?.Code == "stale_control", "duplicate route sequence should report stale_control");
    Assert(registry.TryAccept(secondRoute, Frame(second.LeaseId, 1), 103, out _, out _), "one route's stale frame must not poison another");

    var expired = registry.Expire(111);
    Assert(expired.Count == 1 && expired[0].LeaseId == first.LeaseId, "only the first independent deadline should expire");
    Assert(registry.TryGet(second.LeaseId, out _), "the later second deadline should remain active");
    Assert(registry.ReleaseLease(second.LeaseId, out var released) && released?.Route == secondRoute,
        "exact lease release should return the released route");
}

static void TestLeaseCapacityAndScopedCleanup()
{
    var registry = new VehicleLeaseRegistry();
    var game = GameInstanceId.New();
    var session = new ProtocolSessionId(Guid.NewGuid());
    for (var index = 0; index < VehicleLeaseRegistry.DefaultCapacity; index++)
    {
        var route = Route(game, EphemeralControllerId.New(), session, $"vehicle-{index}", index + 1);
        Assert(registry.TryAcquire(route, 1, 750, out _, out _), $"lease {index} should fit bounded capacity");
    }
    Assert(!registry.TryAcquire(
            Route(game, EphemeralControllerId.New(), session, "overflow", 100),
            1,
            750,
            out _,
            out var capacityError),
        "lease 65 must be rejected");
    Assert(capacityError?.Code == "lease_capacity" && registry.Count == VehicleLeaseRegistry.DefaultCapacity,
        "capacity rejection must remain bounded at 64");
    Assert(registry.ReleaseAll().Count == VehicleLeaseRegistry.DefaultCapacity && registry.Count == 0,
        "global cleanup should release every route");

    var seeded = SeedRegistry();
    Assert(seeded.Registry.ReleaseVehicle(new VehicleId("vehicle-a")).Count == 2,
        "vehicle cleanup should remove both matching routes only");
    Assert(seeded.Registry.Count == 1, "vehicle cleanup removed an unrelated route");

    seeded = SeedRegistry();
    Assert(seeded.Registry.ReleaseSession(seeded.SessionOne).Count == 2 && seeded.Registry.Count == 1,
        "session cleanup should be independent");

    seeded = SeedRegistry();
    Assert(seeded.Registry.ReleaseController(seeded.ControllerOne).Count == 2 && seeded.Registry.Count == 1,
        "controller cleanup should be independent");

    seeded = SeedRegistry();
    Assert(seeded.Registry.ReleaseEpoch(new AuthorityEpoch(2)).Count == 1 && seeded.Registry.Count == 2,
        "epoch cleanup should remove only the exact authority epoch");
}

static void TestCoordinatorFacade()
{
    var vehicleInstance = new object();
    var vehicleIdentity = VehicleInstanceIdentities.For(vehicleInstance);
    var coordinator = CoordinatorWithLease(100, out var context, out var lease, vehicleInstance);
    Assert(!coordinator.TryAcquire(context, "vehicle-1", 100, out _, out var secondError),
        "v1 must reject a second controlled lease");
    Assert(secondError?.Code == "lease_unavailable", "second lease should report lease_unavailable");
    Assert(coordinator.TryAccept(context, Frame(lease.LeaseId, 0), 101, out _), "first control should pass");
    Assert(coordinator.TryGetFreshOverrideForControlledVehicle(850, vehicleIdentity, out _), "override should be fresh before its deadline");
    Assert(!coordinator.TryGetFreshOverrideForControlledVehicle(851, vehicleIdentity, out _), "override should be stale at its deadline");
    Assert(coordinator.IsAuthorized(context, "vehicle-1", lease.LeaseId),
        "patch-side freshness reads must not mutate lease authorization");
    Assert(coordinator.ExpireIfStale(851), "main-thread expiry should release the lease");
    Assert(!coordinator.IsLeaseActive, "expired lease should be inactive");
    Assert(coordinator.TryReadReleaseNotice(out var notice) &&
        notice?.Payload.LeaseId == lease.LeaseId &&
        notice.Context.GameInstance == context.GameInstance &&
        notice.Context.Controller == context.Controller &&
        notice.Context.Session == context.Session,
        "dead-man expiry should produce an authoritative release notice");
}

static void TestCoordinatorFeedback()
{
    const long now = 500;
    var game = GameInstanceId.New();
    var controller = EphemeralControllerId.New();
    var session = new ProtocolSessionId(Guid.NewGuid());
    var vehicleObject = new object();
    var vehicleIdentity = VehicleInstanceIdentities.For(vehicleObject);
    var coordinator = new ControlCoordinator(750, new BridgeDiagnostics(), game);
    coordinator.SetControlAvailability(true);
    coordinator.UpdateActiveVehicle(Observation("vehicle-1", 1, vehicleInstance: vehicleObject));
    var context = Context(game, controller, session, ProtocolConstants.MessageTypes.LeaseAcquire, 0, now)
        with { ProtocolVersion = ProtocolConstants.Version };
    Assert(coordinator.TryAcquire(context, "vehicle-1", now, out var lease, out _), "lease should acquire");
    var controlContext = context with { MessageType = ProtocolConstants.MessageTypes.Control };
    var control = Frame(lease!.LeaseId, 3);

    Assert(coordinator.TryValidateInbound(controlContext, control, now + 1, out _), "frame should pass receive admission");
    var received = coordinator.CaptureAppliedControl();
    Assert(received.LatestRequested?.ControlSequence == 3 && received.Accepted is null && received.Applied is null,
        "receive admission must report requested without claiming acceptance or application");

    Assert(coordinator.TryAccept(controlContext, control, now + 2, out _), "frame should be accepted on the game thread");
    var accepted = coordinator.CaptureAppliedControl();
    Assert(accepted.Accepted?.ControlSequence == 3 && accepted.Applied is null,
        "game-thread acceptance must not claim KSA application");
    Assert(coordinator.TryGetFreshOverrideForControlledVehicle(now + 3, vehicleIdentity, out var normalized) && normalized is not null,
        "accepted override should bind to the exact active vehicle identity");

    coordinator.ReportApplied(normalized!, now + 4);
    var applied = coordinator.CaptureAppliedControl();
    Assert(applied.Applied?.ControlSequence == 3 && applied.Applied.MonotonicMs == now + 4,
        "only the verified patch report should advance applied feedback");
    Assert(applied.AppliedGameFrame is null && applied.AppliedSimulationTimeS is null,
        "unavailable authoritative application clocks must remain null");

    var stale = Frame(lease.LeaseId, 3);
    Assert(!coordinator.TryValidateInbound(controlContext, stale, now + 5, out var staleError) && staleError?.Code == "stale_control",
        "control sequencing must reject a duplicate frame");
    Assert(coordinator.CaptureAppliedControl().RejectionReason == "stale_control",
        "control feedback should preserve the latest rejection reason");
}

static void TestAuthorityEpochRelease()
{
    var vehicleInstance = new object();
    var vehicleIdentity = VehicleInstanceIdentities.For(vehicleInstance);
    var coordinator = CoordinatorWithLease(100, out var context, out var lease, vehicleInstance);
    Assert(coordinator.TryAccept(context, Frame(lease.LeaseId, 0), 101, out _), "control should pass");
    coordinator.UpdateActiveVehicle(Observation("vehicle-1", 1, vehicleInstance: vehicleInstance));
    Assert(coordinator.IsLeaseActive, "an unchanged authority observation should preserve the lease");
    coordinator.UpdateActiveVehicle(Observation("vehicle-1", 2, vehicleInstance: vehicleInstance));
    Assert(!coordinator.IsLeaseActive, "a changed authority/flight-computer epoch should release the lease");
    Assert(!coordinator.TryGetFreshOverrideForControlledVehicle(102, vehicleIdentity, out _), "old epoch override must be unpublished");
    Assert(coordinator.TryReadReleaseNotice(out var notice) && notice?.Payload.Reason?.Contains("authority", StringComparison.Ordinal) == true,
        "authority release should be observable");

    var bindingCoordinator = CoordinatorWithLease(200, out var bindingContext, out var bindingLease, vehicleInstance);
    Assert(bindingCoordinator.TryAccept(bindingContext, Frame(bindingLease.LeaseId, 0), 201, out _),
        "binding-loss control should pass before authority is revoked");
    bindingCoordinator.UpdateActiveVehicle(Observation(
        "vehicle-1",
        1,
        controllable: false,
        vehicleInstance: vehicleInstance));
    Assert(!bindingCoordinator.IsLeaseActive,
        "losing the active KSA input binding on the same object must release the lease");
    Assert(!bindingCoordinator.TryGetFreshOverrideForControlledVehicle(202, vehicleIdentity, out _),
        "binding loss must unpublish the same-object override");
}

static void TestScopedSideEffects()
{
    Throws<ArgumentOutOfRangeException>(() => new SideEffectRegistry(SideEffectRegistry.MaximumCapacity + 1),
        "side-effect registry must not exceed 4096 entries");
    var registry = new SideEffectRegistry(capacity: 2);
    var game = GameInstanceId.New();
    var controller = EphemeralControllerId.New();
    var session = new ProtocolSessionId(Guid.NewGuid());
    var first = Context(game, controller, session, ProtocolConstants.MessageTypes.Request, 1, 10)
        .WithRoute(Route(game, controller, session, "vehicle-a", 1), Guid.NewGuid());
    var second = Context(game, controller, session, ProtocolConstants.MessageTypes.Request, 2, 11)
        .WithRoute(Route(game, controller, session, "vehicle-b", 2), Guid.NewGuid());
    var otherSession = new ProtocolSessionId(Guid.NewGuid());
    var third = Context(game, controller, otherSession, ProtocolConstants.MessageTypes.Request, 3, 12)
        .WithRoute(Route(game, controller, otherSession, "vehicle-b", 1), Guid.NewGuid());
    var commandId = Guid.NewGuid();

    Assert(registry.TryBegin(first, commandId, out _) == SideEffectAdmission.Admitted, "first scoped ID should be admitted");
    registry.Complete(first, commandId, new ResponsePayload { Ok = true, Result = new { staged = true } });
    Assert(registry.TryBegin(first, commandId, out var replay) == SideEffectAdmission.Replay && replay?.Ok == true,
        "completed command should replay in the same full context");
    Assert(registry.TryBegin(second, commandId, out var crossEpochReplay) == SideEffectAdmission.Replay && crossEpochReplay?.Ok == true,
        "the same logical command must replay across vehicle/authority changes in one session");
    Assert(registry.TryBegin(third, commandId, out _) == SideEffectAdmission.Admitted,
        "the same UUID in another protocol session should be independent");
    Assert(registry.TryBegin(first, Guid.NewGuid(), out _) == SideEffectAdmission.Full,
        "new IDs must not evict admitted entries at capacity");
    Assert(registry.ClearSession(session) == 1 && registry.Count == 1,
        "session cleanup should preserve another session's idempotency entry");
    Assert(registry.TryBegin(first, Guid.NewGuid(), out _) == SideEffectAdmission.Admitted,
        "scoped cleanup should free bounded capacity");
    Assert(registry.ClearController(controller) == 2 && registry.Count == 0,
        "controller cleanup should remove entries across its remaining sessions");
}

static void TestLatestScheduler()
{
    var scheduler = new LatestByKeyFairScheduler<string, string>(latestCapacity: 2, reliableCapacity: 4);
    Assert(scheduler.TryEnqueueLatest("a", "a1"), "first key should enqueue");
    Assert(scheduler.TryEnqueueLatest("a", "a2"), "same key should coalesce");
    Assert(scheduler.TryEnqueueLatest("b", "b1"), "second key should enqueue");
    Assert(!scheduler.TryEnqueueLatest("c", "c1"), "third distinct key should hit capacity");
    Assert(scheduler.LatestCount == 2, "coalescing must not consume another key slot");
    Assert(scheduler.TryDequeue(out var first, out var firstLane) && first == "a2" && firstLane == OutboundLane.Latest,
        "latest value should replace the stale value at the same fair slot");
    Assert(scheduler.TryEnqueueLatest("a", "a3"), "sent key should rejoin the round robin");
    Assert(scheduler.TryDequeue(out var second, out _) && second == "b1",
        "another pending key must run before a re-enqueued hot key");
    Assert(scheduler.TryDequeue(out var third, out _) && third == "a3", "hot key should run after its fair turn");

    var game = GameInstanceId.New();
    var controller = EphemeralControllerId.New();
    var session = new ProtocolSessionId(Guid.NewGuid());
    Assert(TelemetryStream.ActiveFlight(game, controller, session) == TelemetryStream.ActiveFlight(game, controller, session),
        "the telemetry key must be stable across active-vehicle samples");
}

static void TestReliableFairness()
{
    var scheduler = new LatestByKeyFairScheduler<string, int>(
        latestCapacity: 2,
        reliableCapacity: 32,
        reliableBurstLimit: 16);
    Assert(scheduler.TryEnqueueLatest("telemetry", 10_000), "telemetry should enqueue");
    for (var index = 0; index < 20; index++) Assert(scheduler.TryEnqueueReliable(index), "reliable item should enqueue");
    for (var index = 0; index < 16; index++)
    {
        Assert(scheduler.TryDequeue(out var value, out var lane) && value == index && lane == OutboundLane.Reliable,
            $"reliable item {index} should retain FIFO order inside the burst");
    }
    Assert(scheduler.TryDequeue(out var telemetry, out var telemetryLane) && telemetry == 10_000 && telemetryLane == OutboundLane.Latest,
        "telemetry must run after the bounded reliable burst");
    Assert(scheduler.TryDequeue(out var next, out var nextLane) && next == 16 && nextLane == OutboundLane.Reliable,
        "reliable delivery should resume after telemetry");
}

static void TestInboundSequencePolicy()
{
    var tracker = new InboundSequenceTracker();
    Assert(tracker.TryAdvance(ProtocolConstants.MessageTypes.Control, 0, out _), "allowed control stream should advance");
    Assert(!tracker.TryAdvance(ProtocolConstants.MessageTypes.Telemetry, 0, out var directionError),
        "mod-to-Python telemetry must be rejected inbound");
    Assert(directionError?.Code == "invalid_direction", "wrong direction should report invalid_direction");
    for (var index = 0; index < 1_000; index++)
    {
        Assert(!tracker.TryAdvance($"hostile-{index}", index, out _), "unknown type should be rejected before tracking");
    }
    Assert(tracker.Count == 1 && tracker.Count <= InboundProtocolPolicy.StreamCount,
        "hostile type names must not grow the finite sequence map");
    Assert(!tracker.TryAdvance(ProtocolConstants.MessageTypes.Control, 0, out var stale) && stale?.Code == "stale_sequence",
        "allowed streams still require strictly increasing sequence numbers");
}

static void TestInboundControlAdmission()
{
    const long now = 100;
    var coordinator = CoordinatorWithLease(now, out var acquireContext, out var lease);
    var context = acquireContext with { MessageType = ProtocolConstants.MessageTypes.Control };
    var newest = Frame(lease.LeaseId, 10);
    var older = Frame(lease.LeaseId, 9);
    Assert(coordinator.TryValidateInbound(context, newest, now + 1, out _),
        "the current lease should pass receive-side semantic admission");

    var slot = new LatestInboundControlSlot();
    Assert(slot.TryWrite(new InboundControl(context, newest, now + 1), out _),
        "the newer control should enter the empty latest slot");
    Assert(!slot.TryWrite(new InboundControl(context, older, now + 2), out var stale) &&
           stale?.Code == "stale_control",
        "a lower control_seq must not evict the newer pending control");
    Assert(slot.TryRead(out var selected) && selected?.Payload.ControlSequence == 10,
        "the pending high sequence must survive a lower-sequence arrival");

    Assert(coordinator.TryAccept(context, newest, now + 1, out _),
        "the selected newer control should advance the authoritative lease sequence");
    Assert(!coordinator.TryValidateInbound(context, older, now + 2, out stale) &&
           stale?.Code == "stale_control",
        "a frame older than the applied sequence must fail before slot admission");
    Assert(!coordinator.TryValidateInbound(
               context,
               Frame(Guid.NewGuid(), 11),
               now + 2,
               out var wrongLease) && wrongLease?.Code == "invalid_lease",
        "a wrong lease must not evict a valid pending frame");
    Assert(!coordinator.TryValidateInbound(
               context,
               Frame(lease.LeaseId, 11) with { Throttle = double.NaN },
               now + 2,
               out var malformed) && malformed?.Code == "invalid_throttle",
        "malformed controls must fail semantic admission before coalescing");
}

static void TestReliableOverflowIsExplicit()
{
    var diagnostics = new BridgeDiagnostics();
    var config = new KittenIOConfig { PairingToken = new string('x', 43) };
    var hello = new HelloPayload
    {
        ModVersion = "test",
        GameVersion = "test",
        StarMapVersion = "test",
        Capabilities = CanonicalCapabilities(),
    };
    var client = new KittenIOWebSocketHost(
        config,
        diagnostics,
        hello,
        new ControlCoordinator(750, diagnostics));
    try
    {
        var ready = typeof(KittenIOWebSocketHost).GetField("_ready", BindingFlags.Instance | BindingFlags.NonPublic);
        var session = typeof(KittenIOWebSocketHost).GetField("_sessionId", BindingFlags.Instance | BindingFlags.NonPublic);
        var controller = typeof(KittenIOWebSocketHost).GetField("_controllerId", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert(ready is not null && session is not null && controller is not null, "test could not locate connection state");
        ready!.SetValue(client, 1);
        var activeSession = Guid.NewGuid();
        var activeController = EphemeralControllerId.New();
        session!.SetValue(client, activeSession);
        controller!.SetValue(client, activeController);
        var activeContext = new CommandContext(
            new GameInstanceId(client.GameInstanceId),
            activeController,
            new ProtocolSessionId(activeSession),
            ProtocolConstants.MessageTypes.Request,
            0,
            0,
            "active-request");
        Assert(!client.SendError("stale", "must not cross sessions", activeContext with
            {
                Session = new ProtocolSessionId(Guid.NewGuid()),
            }), "a response bound to a stale session must not enter the queue");
        Assert(!client.SendError("stale", "must not cross controllers", activeContext with
            {
                Controller = EphemeralControllerId.New(),
            }), "a response bound to a stale controller must not enter the queue");
        for (var index = 0; index < ProtocolConstants.ReliableQueueCapacity; index++)
        {
            Assert(client.SendEvent(Event(index)), $"reliable message {index} should fit");
        }
        Assert(!client.SendEvent(Event(ProtocolConstants.ReliableQueueCapacity)), "overflow should be rejected");
        Assert(diagnostics.Snapshot().LastError?.Contains("bounded capacity", StringComparison.Ordinal) == true,
            "overflow should be visible in diagnostics");
    }
    finally
    {
        client.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    static EventPayload Event(int index) => new()
    {
        Name = "test",
        Data = System.Text.Json.JsonSerializer.SerializeToElement(new { index }, WireCodec.Options),
    };
}

static void TestReflectionMetadata()
{
    var cache = new ReflectionMetadataCache(capacity: 2);
    cache.Get(typeof(string));
    cache.Get(typeof(int));
    cache.Get(typeof(double));
    Assert(cache.Count == 2, "metadata cache must remain at its configured capacity");
    Throws<ArgumentOutOfRangeException>(() => new ReflectionMetadataCache(ReflectionMetadataCache.MaximumTypes + 1),
        "metadata cache must not exceed 128 types");

    Assert((int?)ReflectionAccess.GetOrInvoke(new AliasSource(), "Missing", "Secondary") == 42,
        "aliases should retain caller order");
    var overload = new OverloadSource();
    Assert(ReflectionAccess.TryInvoke(overload, "Pick", ["value"], out var picked) && Equals(picked, "string"),
        "the exact overload should beat an assignable object overload");
    Assert(!ReflectionAccess.TryInvoke(overload, "Ambiguous", [new DualInterfaceValue()], out _),
        "equally compatible overloads should fail safely instead of invoking arbitrarily");

    var transient = new TransientSource();
    Assert(ReflectionAccess.Get(transient, nameof(TransientSource.Value)) is null,
        "a transient getter fault should be treated as unavailable");
    Assert((int?)ReflectionAccess.Get(transient, nameof(TransientSource.Value)) == 7,
        "metadata caching must not cache a transient getter result or failure");

    var weak = CreateReflectedWeakReference();
    ForceCollection(weak);
    Assert(!weak.IsAlive, "reflection metadata must not retain target instances");
}

static void TestReflectionSetter()
{
    object boxed = new FakeInputs { EngineOn = false, EngineThrottle = 0 };
    Assert(ReflectionAccess.Set(boxed, nameof(FakeInputs.EngineOn), true), "bool field should be set");
    Assert(ReflectionAccess.Set(boxed, nameof(FakeInputs.EngineThrottle), 0.5f), "float field should be set");
    var result = (FakeInputs)boxed;
    Assert(result.EngineOn && Math.Abs(result.EngineThrottle - 0.5f) < 0.001f, "boxed struct mutation should persist");
}

static void TestLookupEnumeration()
{
    var lookup = new FakeLookupCollection("Kerbin", "Mun", "Minmus");
    var values = ReflectionAccess.Enumerate(lookup).Cast<string>().ToArray();
    Assert(values.SequenceEqual(new[] { "Kerbin", "Mun", "Minmus" }),
        "Count/GetIndex lookup collections should enumerate in index order");
    Assert(lookup.StringGetCalls == 0, "enumeration must not bind an integer index to Get(string)");
}

static void TestWeakFallbackIds()
{
    var registry = new WeakEntityIdRegistry();
    var first = new CollisionHash();
    var second = new CollisionHash();
    var firstId = registry.GetId(first);
    Assert(firstId == registry.GetId(first), "the same object should retain its fallback ID");
    Assert(firstId != registry.GetId(second), "different objects with colliding hashes need distinct IDs");

    var weak = CreateWeakIdReference(registry);
    ForceCollection(weak);
    Assert(!weak.IsAlive, "weak ID keys must not retain game objects");
}

static void TestCatalogComparison()
{
    var tracker = new CatalogPublicationTracker();
    var first = Catalog("Explorer");
    var initial = tracker.Observe(first, force: false);
    Assert(initial.Publish && !initial.Changed, "first catalog should publish without a changed event");
    var unchanged = tracker.Observe(Catalog("Explorer"), force: false);
    Assert(!unchanged.Publish && !unchanged.Changed, "structurally identical catalog should not republish");
    var renamed = tracker.Observe(Catalog("Explorer II"), force: false);
    Assert(renamed.Publish && renamed.Changed, "an entity rename must be a structural catalog change");
    var forced = tracker.Observe(Catalog("Explorer II"), force: true);
    Assert(forced.Publish && !forced.Changed, "force should publish unchanged structure without a false change event");

    var reordered = new CatalogPayload
    {
        Bodies =
        [
            new EntityReference { Id = "body-b", Name = "B", Kind = "body" },
            new EntityReference { Id = "body-a", Name = "A", Kind = "body" },
        ],
        Vehicles = [new EntityReference { Id = "vehicle", Name = "Explorer II", Kind = "vehicle" }],
    };
    tracker.Reset();
    tracker.Observe(reordered, force: false);
    var sameDifferentOrder = reordered with { Bodies = reordered.Bodies.Reverse().ToArray() };
    Assert(!tracker.Observe(sameDifferentOrder, force: false).Publish,
        "catalog capture order alone should not create a change");
}

static void TestCatalogTargetReplacement()
{
    var targets = new CatalogTargetRegistry();
    var previousSessionTarget = new object();
    targets.Replace(new Dictionary<string, object>
    {
        ["target"] = previousSessionTarget,
        ["removed"] = new object(),
    });
    Assert(targets.TryResolve("target", out var first) && ReferenceEquals(first, previousSessionTarget),
        "the completed catalog should resolve its current target");
    var firstGeneration = targets.Generation;

    var currentSessionTarget = new object();
    targets.Replace(new Dictionary<string, object> { ["target"] = currentSessionTarget });
    Assert(targets.Generation == firstGeneration + 1, "catalog replacement should advance generation");
    Assert(targets.TryResolve("target", out var current) && ReferenceEquals(current, currentSessionTarget),
        "the same opaque ID must bind only to the current catalog object");
    Assert(!targets.TryResolve("removed", out _),
        "an entity removed from the current catalog must not remain targetable");

    targets.Replace(new Dictionary<string, object>());
    Assert(!targets.TryResolve("target", out _),
        "a new empty session catalog must revoke every previous target object");
}

static void TestKsaPatchContract()
{
    var game = new KsaGameAdapter();
    Assert(game.TimeWarpContractVerified, game.TimeWarpContractFailureReason);
    Assert(game.AvailableTimeWarpRates.SequenceEqual(PinnedKsaTimeWarpController.PinnedRates),
        "verified adapter must advertise the exact pinned KSA rate table");
    Assert(!PinnedKsaTimeWarpController.TryCreate(
            typeof(KsaGameAdapter).Assembly, out _, out _),
        "a non-KSA or mismatched assembly must fail time-warp capability negotiation closed");
    var coordinator = new ControlCoordinator(750, new BridgeDiagnostics());
    using var patch = new FlightComputerPatch();
    var compatibility = patch.Install(game.GameVersion, coordinator);
    Assert(compatibility.Available, compatibility.Description);
}

static void TestSimulationClockContract()
{
    var expected = KSA.Universe.GetElapsedSeconds();
    var actual = new KsaGameAdapter().GetSimulationTime();
    Assert(actual is not null && actual.Value == expected,
        "the adapter must publish Universe.GetElapsedSeconds rather than application wall time");
}

static void TestPropulsionTelemetryContract()
{
    Assert(PinnedKsaPropulsionTelemetry.TryCreate(
            typeof(KSA.Vehicle).Assembly,
            out var reader,
            out var reason) && reader is not null,
        reason);
    Assert(!PinnedKsaPropulsionTelemetry.TryCreate(
            typeof(KsaGameAdapter).Assembly,
            out _,
            out _),
        "a non-KSA assembly must fail the exact binary contract closed");

    Assert(PinnedKsaPropulsionTelemetry.ConvertPressureToPascals(0) == 0,
        "vacuum must remain a genuine 0 Pa");
    Assert(PinnedKsaPropulsionTelemetry.ConvertPressureToPascals(101_325) == 101_325,
        "KSA physics pressure must use the checked Pa identity conversion");
    Assert(PinnedKsaPropulsionTelemetry.ConvertPressureToPascals(-1) is null,
        "negative pressure must be unavailable");
    Assert(PinnedKsaPropulsionTelemetry.ConvertPressureToPascals(float.NaN) is null &&
           PinnedKsaPropulsionTelemetry.ConvertPressureToPascals(float.PositiveInfinity) is null,
        "non-finite pressure must be unavailable");
    Assert(PinnedKsaPropulsionTelemetry.AmbientPressuresMatch(0, 0),
        "cached and captured vacuum pressure must match");
    Assert(PinnedKsaPropulsionTelemetry.AmbientPressuresMatch(99_340.72f, 99_340.71f) &&
           PinnedKsaPropulsionTelemetry.AmbientPressuresMatch(99_340.46f, 99_340.52f),
        "sub-pascal pressure drift observed between adjacent live KSA update phases must remain coherent");
    Assert(PinnedKsaPropulsionTelemetry.AmbientPressuresMatch(101_325, 101_400),
        "a one-frame pressure change within 0.1 percent must remain coherent during ascent");
    Assert(!PinnedKsaPropulsionTelemetry.AmbientPressuresMatch(101_325, 101_500) &&
           !PinnedKsaPropulsionTelemetry.AmbientPressuresMatch(0, 1) &&
           !PinnedKsaPropulsionTelemetry.AmbientPressuresMatch(float.NaN, 0) &&
           !PinnedKsaPropulsionTelemetry.AmbientPressuresMatch(-1, 0),
        "stale, materially different, non-finite, and negative cached pressure must fail closed");
    var direction = new float3(1, 0, 0);
    var zeroThrottle = default(float3);
    var partialThrottle = 25f * direction;
    var fullThrottle = 50f * direction;
    Assert(PinnedKsaPropulsionTelemetry.MagnitudeOrNull(zeroThrottle) == 0,
        "engine-off and zero throttle must preserve a genuine 0 N");
    Assert(PinnedKsaPropulsionTelemetry.MagnitudeOrNull(partialThrottle) == 25 &&
           PinnedKsaPropulsionTelemetry.MagnitudeOrNull(fullThrottle) == 50,
        "current force magnitude must change from partial to full throttle state");
    var combinedNozzles = 40f * direction + 30f * new float3(0, 1, 0);
    Assert(Math.Abs(PinnedKsaPropulsionTelemetry.MagnitudeOrNull(combinedNozzles)!.Value - 50) < 0.0001,
        "multiple nozzle force vectors must be combined before taking the magnitude");
    Assert(!PinnedKsaPropulsionTelemetry.IsFiniteVector(new float3(float.NaN, 0, 0)) &&
           !PinnedKsaPropulsionTelemetry.IsFiniteVector(new float3(0, float.PositiveInfinity, 0)),
        "every force-vector component must be finite");
    Assert(PinnedKsaPropulsionTelemetry.IsUsableEngine(isActive: true, isPropellantAvailable: true),
        "a staged/enabled controller with propellant must be available at full power");
    Assert(!PinnedKsaPropulsionTelemetry.IsUsableEngine(isActive: false, isPropellantAvailable: true) &&
           !PinnedKsaPropulsionTelemetry.IsUsableEngine(isActive: true, isPropellantAvailable: false) &&
           !PinnedKsaPropulsionTelemetry.IsUsableEngine(isActive: false, isPropellantAvailable: false),
        "disabled, unstaged, and propellant-starved controllers must be excluded");
    var beforeStaging = new[] { (Active: true, Propellant: true), (Active: false, Propellant: true) }
        .Count(engine => PinnedKsaPropulsionTelemetry.IsUsableEngine(engine.Active, engine.Propellant));
    var afterStaging = new[] { (Active: true, Propellant: true), (Active: true, Propellant: true) }
        .Count(engine => PinnedKsaPropulsionTelemetry.IsUsableEngine(engine.Active, engine.Propellant));
    Assert(beforeStaging == 1 && afterStaging == 2,
        "staging changes maximum availability through the exact active-controller set");

    var previousVehicle = KSA.Program.ControlledVehicle;
    var previousActive = KSA.Program.IsControlledVehicleActive;
    try
    {
        KSA.Program.ControlledVehicle = null!;
        KSA.Program.IsControlledVehicleActive = false;
        var game = new KsaGameAdapter();
        game.Refresh();
        Assert(game.CaptureActiveVehicle() is null,
            "no active craft must continue to publish active_vehicle null");
    }
    finally
    {
        KSA.Program.ControlledVehicle = previousVehicle;
        KSA.Program.IsControlledVehicleActive = previousActive;
    }
}

static void TestKsaControlledStateOverlay()
{
    var now = MonotonicClock.NowMilliseconds;
    var authorizedVehicle = VehicleWithId("vehicle-1");
    var coordinator = CoordinatorWithLease(now, out var context, out var lease, authorizedVehicle);
    var frame = Frame(lease.LeaseId, 0) with
    {
        EngineOn = true,
        Throttle = 0.25,
    };
    Assert(coordinator.TryAccept(context, frame, now, out _), "control frame should be accepted");

    using var patch = new FlightComputerPatch();
    var compatibility = patch.Install(new KsaGameAdapter().GameVersion, coordinator);
    Assert(compatibility.Available, compatibility.Description);
    var apply = GetPatchApplyMethod();
    var nested = apply.DeclaringType!;
    Assert(nested.GetFields(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .All(field => !field.FieldType.IsArray),
        "the per-control patch must not carry axis arrays");

    var unrelated = (KSA.ThrusterMapFlags)(1 << 30);
    var inputs = new KSA.ManualControlInputs
    {
        EngineOn = false,
        EngineThrottle = 0.1f,
        ThrusterCommandFlags = unrelated | KSA.ThrusterMapFlags.PitchUp,
    };
    var previousControlledVehicle = KSA.Program.ControlledVehicle;
    var previousControlledVehicleActive = KSA.Program.IsControlledVehicleActive;
    try
    {
    KSA.Program.ControlledVehicle = authorizedVehicle;
    KSA.Program.IsControlledVehicleActive = true;
    var replaced = InvokePatch(apply, controlled: true, authorizedVehicle, inputs);
    Assert(replaced.EngineOn && Math.Abs(replaced.EngineThrottle - 0.25f) < 0.001f,
        "engine and throttle should reach the KSA input copy");
    Assert(replaced.ThrusterCommandFlags.HasFlag(unrelated), "unrelated KSA flags must be preserved");
    Assert(!replaced.ThrusterCommandFlags.HasFlag(KSA.ThrusterMapFlags.PitchUp),
        "player digital axes must be cleared while thrust-vector control is active");

    var mapModeVehicle = InvokePatch(apply, controlled: false, authorizedVehicle, inputs);
    Assert(mapModeVehicle.EngineOn && Math.Abs(mapModeVehicle.EngineThrottle - 0.25f) < 0.001f,
        "the exact selected vehicle should retain control when a view clears KSA's transient controlled flag");

    KSA.Program.IsControlledVehicleActive = false;
    var inactiveBinding = InvokePatch(apply, controlled: true, authorizedVehicle, inputs);
    Assert(inactiveBinding.EngineOn && Math.Abs(inactiveBinding.EngineThrottle - 0.25f) < 0.001f,
        "the exact selected vehicle should retain control while map mode clears the active-view flag");
    KSA.Program.IsControlledVehicleActive = true;

    var sameIdDifferentVehicle = VehicleWithId("vehicle-1");
    Assert(!ReferenceEquals(authorizedVehicle, sameIdDifferentVehicle),
        "same-ID KSA test vehicles must be distinct object instances");
    _ = VehicleInstanceIdentities.For(sameIdDifferentVehicle);
    KSA.Program.ControlledVehicle = sameIdDifferentVehicle;
    var otherVehicle = InvokePatch(apply, controlled: true, sameIdDifferentVehicle, inputs);
    Assert(otherVehicle.EngineOn == inputs.EngineOn &&
           otherVehicle.EngineThrottle == inputs.EngineThrottle &&
           otherVehicle.ThrusterCommandFlags == inputs.ThrusterCommandFlags,
        "a distinct controlled KSA object must be rejected even when its string route ID is reused");

    var staleControlledFlag = InvokePatch(apply, controlled: true, authorizedVehicle, inputs);
    Assert(staleControlledFlag.EngineOn == inputs.EngineOn &&
           staleControlledFlag.EngineThrottle == inputs.EngineThrottle &&
           staleControlledFlag.ThrusterCommandFlags == inputs.ThrusterCommandFlags,
        "a stale KSA IsControlled flag must not override the newly bound global vehicle");
    KSA.Program.ControlledVehicle = authorizedVehicle;

    var nullableFrame = Frame(lease.LeaseId, 1) with { EngineOn = null, Throttle = null };
    Assert(coordinator.TryAccept(context, nullableFrame, now + 1, out _), "nullable frame should be accepted");
    var nullableInputs = new KSA.ManualControlInputs
    {
        EngineOn = true,
        EngineThrottle = 0.7f,
        ThrusterCommandFlags = unrelated | KSA.ThrusterMapFlags.RollLeft,
    };
    var nullableResult = InvokePatch(apply, controlled: true, authorizedVehicle, nullableInputs);
    Assert(nullableResult.EngineOn && Math.Abs(nullableResult.EngineThrottle - 0.7f) < 0.001f,
        "nullable engine/throttle overrides must preserve player values");
    Assert(nullableResult.ThrusterCommandFlags.HasFlag(unrelated) &&
           !nullableResult.ThrusterCommandFlags.HasFlag(KSA.ThrusterMapFlags.RollLeft),
        "neutral axes should clear only KittenIO-owned direction bits");

    var faultFrame = Frame(lease.LeaseId, 2) with { EngineOn = false, Throttle = 0.9 };
    Assert(coordinator.TryAccept(context, faultFrame, now + 2, out _), "fault test frame should be accepted");
    var beforeFault = new KSA.ManualControlInputs
    {
        EngineOn = true,
        EngineThrottle = 0.3f,
        ThrusterCommandFlags = unrelated | KSA.ThrusterMapFlags.YawLeft,
    };
    FlightComputerPatch.AtomicCommitFaultInjector = static () => throw new InvalidOperationException("injected atomic fault");
    var afterFault = InvokePatch(apply, controlled: true, authorizedVehicle, beforeFault);
    FlightComputerPatch.AtomicCommitFaultInjector = null;
    Assert(afterFault.EngineOn == beforeFault.EngineOn &&
           afterFault.EngineThrottle == beforeFault.EngineThrottle &&
           afterFault.ThrusterCommandFlags == beforeFault.ThrusterCommandFlags,
        "a patch fault before commit must leave the entire input struct unchanged");
    Assert(coordinator.IsLeaseActive, "Harmony should defer fault cleanup instead of mutating lease state");
    Assert(coordinator.ReleaseForPendingPatchFault() && !coordinator.IsLeaseActive,
        "the next game-thread pass should atomically release a faulted override");

    var stateType = typeof(KSA.Mod).Assembly.GetType("KSA.VehicleUpdateState");
    var prepare = stateType?.GetMethod("PrepareFromVehicle", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    var patchInfo = prepare is null ? null : HarmonyLib.Harmony.GetPatchInfo(prepare);
    Assert(patchInfo?.Postfixes.Any(item => item.owner == "io.kitten.kittenio.control.v1") == true,
        "Harmony should install only the controlled-state postfix on KSA's handoff");
    }
    finally
    {
        FlightComputerPatch.AtomicCommitFaultInjector = null;
        KSA.Program.ControlledVehicle = previousControlledVehicle;
        KSA.Program.IsControlledVehicleActive = previousControlledVehicleActive;
    }
}

static void TestFutureKsaBuildFailsClosed()
{
    var coordinator = new ControlCoordinator(750, new BridgeDiagnostics());
    using var patch = new FlightComputerPatch();
    var compatibility = patch.Install("2026.9.7.54020", coordinator);
    Assert(!compatibility.Available, "a distinct future build must remain telemetry-only");
}

static void TestTimeWarpCoordinator()
{
    using var validDocument = System.Text.Json.JsonDocument.Parse("{\"rate\":120}");
    Assert(KittenIOMod.TryParseTimeWarpRequest(
            "time_warp.set_rate", validDocument.RootElement, out var kind, out var rate, out _),
        "a finite exact rate parameter should parse");
    Assert(kind == TimeWarpCommandKind.SetRate && rate == 120,
        "set-rate parsing changed the operation or rate");
    using var invalidDocument = System.Text.Json.JsonDocument.Parse("{\"rate\":120,\"extra\":true}");
    Assert(!KittenIOMod.TryParseTimeWarpRequest(
            "time_warp.set_rate", invalidDocument.RootElement, out _, out _, out var invalid) &&
           invalid?.Error?.Code == "invalid_params",
        "unknown time-warp parameters must fail closed");

    var simulationIdentity = new object();
    var adapter = new FakeTimeWarpAdapter
    {
        RequestResult = TimeWarpCommandResult.Success(1, 120, simulationIdentity),
        Observation = new TimeWarpObservation(true, false, false, 1, simulationIdentity),
    };
    var coordinator = new TimeWarpCoordinator(adapter);
    var context = Context(
        GameInstanceId.New(),
        EphemeralControllerId.New(),
        new ProtocolSessionId(Guid.NewGuid()),
        ProtocolConstants.MessageTypes.Request,
        4,
        100) with { ProtocolVersion = ProtocolConstants.Version };
    var commandId = Guid.NewGuid();
    Assert(coordinator.TryBegin(context, commandId, kind, rate, 10, 100, out _),
        "verified KSA queue admission should begin confirmation");
    Assert(!coordinator.TryAdvance(10, 101, out _),
        "the issuing frame must never confirm its own time-warp request");
    Assert(!coordinator.TryBegin(context, Guid.NewGuid(), kind, rate, 10, 101, out var busy) &&
           busy?.Code == "busy",
        "only one game-global transition may await confirmation");
    adapter.Observation = new TimeWarpObservation(true, false, false, 120, simulationIdentity);
    Assert(coordinator.TryAdvance(11, 102, out var completion) && completion is not null &&
           completion.CommandId == commandId && completion.Result.Ok,
        "a later authoritative KSA frame should confirm the exact rate");

    adapter.RequestResult = TimeWarpCommandResult.Success(120, 1, simulationIdentity);
    adapter.Observation = new TimeWarpObservation(true, false, false, 120, simulationIdentity);
    Assert(coordinator.TryBegin(context, Guid.NewGuid(), TimeWarpCommandKind.Cancel, null, 20, 200, out _),
        "native cancel should enter confirmation");
    adapter.Observation = new TimeWarpObservation(true, true, false, 0, simulationIdentity);
    Assert(coordinator.TryAdvance(21, 201, out var paused) &&
           paused?.Result.Code == "simulation_paused",
        "menu pause must not falsely confirm a zero-rate transition");

    adapter.Observation = new TimeWarpObservation(true, false, false, 120, simulationIdentity);
    Assert(coordinator.TryBegin(context, Guid.NewGuid(), TimeWarpCommandKind.Cancel, null, 30, 300, out _),
        "native cancel should be retryable after pause rejection");
    Assert(coordinator.TryAdvance(38, 301, out var refused) &&
           refused?.Result.Code == "time_warp_refused",
        "an unconfirmed transition must report KSA refusal after the bounded frame window");

    Assert(coordinator.TryBegin(context, Guid.NewGuid(), TimeWarpCommandKind.Cancel, null, 40, 400, out _),
        "native cancel should begin before a scene change");
    adapter.Observation = new TimeWarpObservation(true, false, false, 1, new object());
    Assert(coordinator.TryAdvance(41, 401, out var changed) &&
           changed?.Result.Code == "simulation_unavailable",
        "a changed KSA simulation identity must invalidate pending confirmation");
}

static void TestTimeWarpPersistentOwnership()
{
    var simulationIdentity = new object();
    var adapter = new FakeTimeWarpAdapter
    {
        RequestResult = TimeWarpCommandResult.Success(1, 600, simulationIdentity),
        Observation = new TimeWarpObservation(true, false, false, 1, simulationIdentity),
    };
    var coordinator = new TimeWarpCoordinator(adapter);
    var controller = EphemeralControllerId.New();
    var context = Context(
        GameInstanceId.New(), controller, new ProtocolSessionId(Guid.NewGuid()),
        ProtocolConstants.MessageTypes.Request, 1, 10) with
    { ProtocolVersion = ProtocolConstants.Version };
    Assert(coordinator.TryBegin(context, Guid.NewGuid(), TimeWarpCommandKind.SetRate, 600, 1, 10, out _),
        "time-warp transition should begin");
    coordinator.CancelAll();
    Assert(adapter.RequestCount == 1,
        "disconnect cleanup must abandon tracking without sending a native cancel");
    Assert(!coordinator.TryAdvance(20, 3_000, out _),
        "abandoned request tracking must stay cleared");
}

static void TestDiagnosticFormatting()
{
    var diagnostics = new BridgeDiagnostics();
    diagnostics.SetVehicle("Explorer", "vehicle-1");
    diagnostics.SetVehicle("Explorer", "vehicle-1");
    Assert(diagnostics.VehicleFormatCount == 1, "unchanged display state should not be reformatted every frame");
    diagnostics.SetVehicle("Explorer II", "vehicle-1");
    Assert(diagnostics.VehicleFormatCount == 2 && diagnostics.Snapshot().Vehicle == "Explorer II (vehicle-1)",
        "a real display change should format once and update diagnostics");
}

static void TestUnloadFailureIsolation()
{
    var diagnostics = new BridgeDiagnostics();
    var order = new List<int>();
    FailureIsolatedCleanup.Run(
        diagnostics,
        new CleanupStep("first", () => { order.Add(1); throw new InvalidOperationException("boom"); }),
        new CleanupStep("second", () => order.Add(2)),
        new CleanupStep("third", () => { order.Add(3); throw new InvalidOperationException("again"); }),
        new CleanupStep("fourth", () => order.Add(4)));
    Assert(order.SequenceEqual(new[] { 1, 2, 3, 4 }), "one unload failure must not skip later cleanup steps");
    Assert(diagnostics.Snapshot().LastError?.Contains("third failed", StringComparison.Ordinal) == true,
        "isolated unload failures should remain diagnostic");
}

static void TestControlCadence()
{
    Assert(!KittenIOMod.IsControlCadenceSafe(1, 750), "1 Hz must not acquire a 750 ms lease");
    Assert(!KittenIOMod.IsControlCadenceSafe(10, 250), "10 Hz lacks safety margin for a 250 ms lease");
    Assert(KittenIOMod.IsControlCadenceSafe(20, 750), "default cadence should be safe");
    Assert(KittenIOMod.IsControlCadenceSafe(20, 250), "20 Hz should safely refresh the minimum timeout");
}

static void TestNoDiagnosticsOverlay()
{
    var afterGui = typeof(KittenIOMod).GetMethod("AfterGui", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    Assert(afterGui is null, "KittenIO must not register an in-game diagnostics renderer");
}

static ControlCoordinator CoordinatorWithLease(
    long now,
    out CommandContext context,
    out LeaseGrantedPayload lease,
    object? vehicleInstance = null)
{
    var game = GameInstanceId.New();
    var controller = EphemeralControllerId.New();
    var session = new ProtocolSessionId(Guid.NewGuid());
    var coordinator = new ControlCoordinator(750, new BridgeDiagnostics(), game);
    coordinator.SetControlAvailability(true);
    coordinator.UpdateActiveVehicle(Observation("vehicle-1", 1, vehicleInstance: vehicleInstance));
    context = Context(game, controller, session, ProtocolConstants.MessageTypes.LeaseAcquire, 0, now);
    Assert(coordinator.TryAcquire(context, "vehicle-1", now, out var granted, out _), "lease should be granted");
    lease = granted!;
    return coordinator;
}

static ActiveVehicleObservation Observation(
    string id,
    long epoch,
    bool controllable = true,
    object? vehicleInstance = null)
{
    vehicleInstance ??= new object();
    return new(
        new VehicleId(id),
        id,
        controllable,
        controllable ? AuthorityIdentity.KsaLocalControlledInput : null,
        VehicleInstanceIdentities.For(vehicleInstance),
        FlightComputerInstanceIdentities.For(new object()),
        new AuthorityEpoch(epoch));
}

static ControlRoute Route(
    GameInstanceId game,
    EphemeralControllerId controller,
    ProtocolSessionId session,
    string vehicle,
    long epoch) => new(
    game,
    controller,
    session,
    new VehicleId(vehicle),
    VehicleInstanceIdentities.For(new object()),
    AuthorityIdentity.KsaLocalControlledInput,
    new AuthorityEpoch(epoch),
    ControlMode.ActiveVehicleLease);

static CommandContext Context(
    GameInstanceId game,
    EphemeralControllerId controller,
    ProtocolSessionId session,
    string messageType,
    long sequence,
    long now) => new(game, controller, session, messageType, sequence, now, null);

static CapabilitySet CanonicalCapabilities() => new()
{
    Control = new ControlCapabilities
    {
        ExactThrottle = true, EngineState = true, Stage = true, SetTarget = true,
        ThrustVectorTarget = true, DeadmanTimeoutMs = 750,
    },
    Dynamics = new DynamicsCapabilities
    {
        GravityCci = true, GravityCce = true, GravityLocal = true,
        TotalAcceleration = true, NonGravitationalAcceleration = true, BodyParameters = true,
    },
    Propulsion = new PropulsionCapabilities
    {
        Aggregate = true, Engines = true, EnginePositions = true, GimbalState = true,
        GimbalRateLimits = false, RestartCounts = false, ResponseParameters = false,
    },
    RigidBody = new RigidBodyCapabilities
    {
        CenterOfMass = true, InertiaTensor = true, AngularAcceleration = true,
        TotalForceMoment = true, PropulsiveForceMoment = true,
        AerodynamicForceMoment = false, RcsForceMoment = false, TorqueAuthority = false,
    },
    Atmosphere = new AtmosphereCapabilities
    {
        Pressure = true, Density = true, Temperature = false, SpeedOfSound = false,
        Mach = false, DynamicPressure = true, Wind = false, AirRelativeVelocity = true, Angles = false,
    },
    Landing = new LandingCapabilities
    {
        SiteDefinition = true, LocalFrame = true, TerrainHeight = false,
        GroundNormal = true, SurfaceSlope = true,
    },
    Surface = new SurfaceCapabilities
    {
        GroundContact = true, TouchdownEvents = true, CollisionEvents = false,
        StructuralFailureEvents = false, EngineOutEvents = false,
        PropellantExhaustion = true, Controllability = true,
        ContactPoints = false, LandingLegState = false,
    },
    AppliedControl = true,
    RecordingReplay = true,
    TimeWarp = new TimeWarpCapabilities
    {
        Control = true, AvailableRates = [0, 1, 2, 4],
        Ownership = "game_global_persistent", UnavailableReason = null,
    },
};

static ControlFramePayload Frame(Guid leaseId, long sequence) => new()
{
    LeaseId = leaseId,
    ControlSequence = sequence,
    Mode = ControlModes.ThrustVectorTarget,
    EngineOn = null,
    Throttle = 0.5,
    ThrustVectorTarget = new ThrustVectorTarget
    {
        DirectionCci = new Vector3Value(1, 0, 0),
    },
};

static SeededRegistry SeedRegistry()
{
    var registry = new VehicleLeaseRegistry(capacity: 4);
    var game = GameInstanceId.New();
    var sessionOne = new ProtocolSessionId(Guid.NewGuid());
    var sessionTwo = new ProtocolSessionId(Guid.NewGuid());
    var controllerOne = EphemeralControllerId.New();
    var controllerTwo = EphemeralControllerId.New();
    Assert(registry.TryAcquire(Route(game, controllerOne, sessionOne, "vehicle-a", 1), 1, 100, out _, out _), "seed a");
    Assert(registry.TryAcquire(Route(game, controllerOne, sessionOne, "vehicle-b", 2), 1, 100, out _, out _), "seed b");
    Assert(registry.TryAcquire(Route(game, controllerTwo, sessionTwo, "vehicle-a", 3), 1, 100, out _, out _), "seed c");
    return new SeededRegistry(registry, sessionOne, controllerOne);
}

static CatalogPayload Catalog(string vehicleName) => new()
{
    Bodies =
    [
        new EntityReference { Id = "body-a", Name = "A", Kind = "body" },
        new EntityReference { Id = "body-b", Name = "B", Kind = "body" },
    ],
    Vehicles = [new EntityReference { Id = "vehicle", Name = vehicleName, Kind = "vehicle" }],
};

static MethodInfo GetPatchApplyMethod()
{
    var postfixType = typeof(FlightComputerPatch).GetNestedType("ControlledStatePostfix", BindingFlags.NonPublic);
    var apply = postfixType?.GetMethod("Apply", BindingFlags.Public | BindingFlags.Static);
    Assert(apply is not null, "controlled update-state Harmony postfix should exist");
    var parameters = apply!.GetParameters();
    Assert(parameters.Length == 4 &&
           parameters[0].ParameterType == typeof(bool) &&
           parameters[1].ParameterType == typeof(KSA.Vehicle) &&
           parameters[2].ParameterType == typeof(KSA.VehicleUpdateData) &&
           parameters[3].ParameterType == typeof(KSA.ManualControlInputs).MakeByRefType(),
        "postfix must bind KSA's controlled flag, exact vehicle/update instances, and manual-input copy");
    return apply;
}

static KSA.ManualControlInputs InvokePatch(
    MethodInfo apply,
    bool controlled,
    KSA.Vehicle vehicle,
    KSA.ManualControlInputs inputs)
{
    var updateData = (KSA.VehicleUpdateData)RuntimeHelpers.GetUninitializedObject(typeof(KSA.VehicleUpdateData));
    object?[] invocation = [controlled, vehicle, updateData, inputs];
    apply.Invoke(null, invocation);
    return (KSA.ManualControlInputs)invocation[3]!;
}

static KSA.Vehicle VehicleWithId(string id)
{
    var vehicle = (KSA.Vehicle)RuntimeHelpers.GetUninitializedObject(typeof(KSA.Vehicle));
    var idField = typeof(KSA.Astronomical).GetField(
        "<Id>k__BackingField",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Pinned Astronomical.Id backing field is missing.");
    idField.SetValue(vehicle, id);
    Assert(vehicle.Id == id, "literal KSA vehicle ID was not installed in the test fixture");
    return vehicle;
}

[MethodImpl(MethodImplOptions.NoInlining)]
static WeakReference CreateReflectedWeakReference()
{
    var value = new AliasSource();
    _ = ReflectionAccess.Get(value, nameof(AliasSource.Secondary));
    return new WeakReference(value);
}

[MethodImpl(MethodImplOptions.NoInlining)]
static WeakReference CreateWeakIdReference(WeakEntityIdRegistry registry)
{
    var value = new CollisionHash();
    _ = registry.GetId(value);
    return new WeakReference(value);
}

static void ForceCollection(WeakReference weak)
{
    for (var index = 0; index < 10 && weak.IsAlive; index++)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}

static void Throws<TException>(Action action, string message)
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

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

internal readonly record struct SeededRegistry(
    VehicleLeaseRegistry Registry,
    ProtocolSessionId SessionOne,
    EphemeralControllerId ControllerOne);

internal struct FakeInputs
{
    public bool EngineOn;
    public float EngineThrottle;
}

internal sealed class FakeLookupCollection(params string[] values)
{
    public int Count => values.Length;
    public int StringGetCalls { get; private set; }
    public string Get(string key)
    {
        StringGetCalls++;
        throw new InvalidOperationException($"Get(string) must not receive index '{key}'.");
    }
    public string GetIndex(int index) => values[index];
    public T GetIndex<T>(int index) =>
        throw new InvalidOperationException($"Open generic GetIndex<{typeof(T).Name}>({index}) must not be selected.");
}

internal sealed class AliasSource
{
    public int Secondary => 42;
}

internal sealed class TransientSource
{
    private int _reads;
    public int Value => ++_reads == 1 ? throw new InvalidOperationException("transient") : 7;
}

internal interface IFirst { }
internal interface ISecond { }
internal sealed class DualInterfaceValue : IFirst, ISecond { }

internal sealed class OverloadSource
{
    public string Pick(object value) => value.GetType().Name;
    public string Pick(string value) => value.Length >= 0 ? "string" : "never";
    public string Ambiguous(IFirst value) => value.GetType().Name;
    public string Ambiguous(ISecond value) => value.GetType().Name;
}

internal sealed class CollisionHash
{
    public override int GetHashCode() => 1;
}

internal sealed class FakeTimeWarpAdapter : ITimeWarpAdapter
{
    public required TimeWarpObservation Observation { get; set; }
    public required TimeWarpCommandResult RequestResult { get; set; }
    public int RequestCount { get; private set; }

    public TimeWarpObservation ObserveTimeWarp() => Observation;

    public TimeWarpCommandResult RequestTimeWarp(TimeWarpCommandKind kind, double? rate)
    {
        RequestCount++;
        return RequestResult;
    }
}
