using System.Text.Json.Serialization;

namespace KittenIO.Protocol;

/// <summary>Canonical control modes. Exactly one mode payload is permitted per frame.</summary>
public static class ControlModes
{
    public const string ThrustVectorTarget = "thrust_vector_target";
}

public sealed record ControlCapabilities
{
    [JsonPropertyName("exact_throttle")] public required bool ExactThrottle { get; init; }
    [JsonPropertyName("engine_state")] public required bool EngineState { get; init; }
    [JsonPropertyName("stage")] public required bool Stage { get; init; }
    [JsonPropertyName("set_target")] public required bool SetTarget { get; init; }
    [JsonPropertyName("thrust_vector_target")] public required bool ThrustVectorTarget { get; init; }
    [JsonPropertyName("deadman_timeout_ms")] public required int DeadmanTimeoutMs { get; init; }
}

public sealed record DynamicsCapabilities
{
    [JsonPropertyName("gravity_cci")] public required bool GravityCci { get; init; }
    [JsonPropertyName("gravity_cce")] public required bool GravityCce { get; init; }
    [JsonPropertyName("gravity_local")] public required bool GravityLocal { get; init; }
    [JsonPropertyName("total_acceleration")] public required bool TotalAcceleration { get; init; }
    [JsonPropertyName("non_gravitational_acceleration")] public required bool NonGravitationalAcceleration { get; init; }
    [JsonPropertyName("body_parameters")] public required bool BodyParameters { get; init; }
}

public sealed record PropulsionCapabilities
{
    [JsonPropertyName("aggregate")] public required bool Aggregate { get; init; }
    [JsonPropertyName("engines")] public required bool Engines { get; init; }
    [JsonPropertyName("engine_positions")] public required bool EnginePositions { get; init; }
    [JsonPropertyName("gimbal_state")] public required bool GimbalState { get; init; }
    [JsonPropertyName("gimbal_rate_limits")] public required bool GimbalRateLimits { get; init; }
    [JsonPropertyName("restart_counts")] public required bool RestartCounts { get; init; }
    [JsonPropertyName("response_parameters")] public required bool ResponseParameters { get; init; }
}

public sealed record RigidBodyCapabilities
{
    [JsonPropertyName("center_of_mass")] public required bool CenterOfMass { get; init; }
    [JsonPropertyName("inertia_tensor")] public required bool InertiaTensor { get; init; }
    [JsonPropertyName("angular_acceleration")] public required bool AngularAcceleration { get; init; }
    [JsonPropertyName("total_force_moment")] public required bool TotalForceMoment { get; init; }
    [JsonPropertyName("propulsive_force_moment")] public required bool PropulsiveForceMoment { get; init; }
    [JsonPropertyName("aerodynamic_force_moment")] public required bool AerodynamicForceMoment { get; init; }
    [JsonPropertyName("rcs_force_moment")] public required bool RcsForceMoment { get; init; }
    [JsonPropertyName("torque_authority")] public required bool TorqueAuthority { get; init; }
}

public sealed record AtmosphereCapabilities
{
    [JsonPropertyName("pressure")] public required bool Pressure { get; init; }
    [JsonPropertyName("density")] public required bool Density { get; init; }
    [JsonPropertyName("temperature")] public required bool Temperature { get; init; }
    [JsonPropertyName("speed_of_sound")] public required bool SpeedOfSound { get; init; }
    [JsonPropertyName("mach")] public required bool Mach { get; init; }
    [JsonPropertyName("dynamic_pressure")] public required bool DynamicPressure { get; init; }
    [JsonPropertyName("wind")] public required bool Wind { get; init; }
    [JsonPropertyName("air_relative_velocity")] public required bool AirRelativeVelocity { get; init; }
    [JsonPropertyName("angles")] public required bool Angles { get; init; }
}

public sealed record LandingCapabilities
{
    [JsonPropertyName("site_definition")] public required bool SiteDefinition { get; init; }
    [JsonPropertyName("local_frame")] public required bool LocalFrame { get; init; }
    [JsonPropertyName("terrain_height")] public required bool TerrainHeight { get; init; }
    [JsonPropertyName("ground_normal")] public required bool GroundNormal { get; init; }
    [JsonPropertyName("surface_slope")] public required bool SurfaceSlope { get; init; }
}

public sealed record SurfaceCapabilities
{
    [JsonPropertyName("ground_contact")] public required bool GroundContact { get; init; }
    [JsonPropertyName("touchdown_events")] public required bool TouchdownEvents { get; init; }
    [JsonPropertyName("collision_events")] public required bool CollisionEvents { get; init; }
    [JsonPropertyName("structural_failure_events")] public required bool StructuralFailureEvents { get; init; }
    [JsonPropertyName("engine_out_events")] public required bool EngineOutEvents { get; init; }
    [JsonPropertyName("propellant_exhaustion")] public required bool PropellantExhaustion { get; init; }
    [JsonPropertyName("controllability")] public required bool Controllability { get; init; }
    [JsonPropertyName("contact_points")] public required bool ContactPoints { get; init; }
    [JsonPropertyName("landing_leg_state")] public required bool LandingLegState { get; init; }
}

public sealed record TimeWarpCapabilities
{
    [JsonPropertyName("control")] public required bool Control { get; init; }
    [JsonPropertyName("available_rates")] public required IReadOnlyList<double> AvailableRates { get; init; }
    [JsonPropertyName("ownership")] public required string Ownership { get; init; }
    [JsonPropertyName("unavailable_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UnavailableReason { get; init; }
}

public sealed record CapabilitySet
{
    [JsonPropertyName("control")] public required ControlCapabilities Control { get; init; }
    [JsonPropertyName("dynamics")] public required DynamicsCapabilities Dynamics { get; init; }
    [JsonPropertyName("propulsion")] public required PropulsionCapabilities Propulsion { get; init; }
    [JsonPropertyName("rigid_body")] public required RigidBodyCapabilities RigidBody { get; init; }
    [JsonPropertyName("atmosphere")] public required AtmosphereCapabilities Atmosphere { get; init; }
    [JsonPropertyName("landing")] public required LandingCapabilities Landing { get; init; }
    [JsonPropertyName("surface")] public required SurfaceCapabilities Surface { get; init; }
    [JsonPropertyName("applied_control")] public required bool AppliedControl { get; init; }
    [JsonPropertyName("recording_replay")] public required bool RecordingReplay { get; init; }
    [JsonPropertyName("time_warp")] public required TimeWarpCapabilities TimeWarp { get; init; }
}

public sealed record Matrix3SymmetricValue
{
    [JsonPropertyName("xx")] public required double Xx { get; init; }
    [JsonPropertyName("xy")] public required double Xy { get; init; }
    [JsonPropertyName("xz")] public required double Xz { get; init; }
    [JsonPropertyName("yy")] public required double Yy { get; init; }
    [JsonPropertyName("yz")] public required double Yz { get; init; }
    [JsonPropertyName("zz")] public required double Zz { get; init; }
}

public sealed record CelestialBodyDynamics
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("gravitational_parameter_m3ps2")] public double? GravitationalParameterM3Ps2 { get; init; }
    [JsonPropertyName("mean_radius_m")] public double? MeanRadiusM { get; init; }
    [JsonPropertyName("rotation_rate_radps")] public double? RotationRateRadps { get; init; }
    [JsonPropertyName("rotation_axis_cce")] public Vector3Value? RotationAxisCce { get; init; }
}

public sealed record VehicleState
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("parent_id")] public string? ParentId { get; init; }
    [JsonPropertyName("target_id")] public string? TargetId { get; init; }
    [JsonPropertyName("target")] public TargetState? Target { get; init; }
    [JsonPropertyName("situation")] public string? Situation { get; init; }
    [JsonPropertyName("controllable")] public required bool Controllable { get; init; }
    [JsonPropertyName("position_cci_m")] public Vector3Value? PositionCciM { get; init; }
    [JsonPropertyName("position_cce_m")] public Vector3Value? PositionCceM { get; init; }
    [JsonPropertyName("position_orb_m")] public Vector3Value? PositionOrbM { get; init; }
    [JsonPropertyName("velocity_cci_mps")] public Vector3Value? VelocityCciMps { get; init; }
    [JsonPropertyName("velocity_cce_mps")] public Vector3Value? VelocityCceMps { get; init; }
    [JsonPropertyName("velocity_orb_mps")] public Vector3Value? VelocityOrbMps { get; init; }
    [JsonPropertyName("surface_velocity_cci_mps")] public Vector3Value? SurfaceVelocityCciMps { get; init; }
    [JsonPropertyName("body_to_cci")] public QuaternionValue? BodyToCci { get; init; }
    [JsonPropertyName("body_rates_radps")] public Vector3Value? BodyRatesRadps { get; init; }
    [JsonPropertyName("angular_acceleration_body_radps2")] public Vector3Value? AngularAccelerationBodyRadps2 { get; init; }
    [JsonPropertyName("total_acceleration_body_mps2")] public Vector3Value? TotalAccelerationBodyMps2 { get; init; }
    [JsonPropertyName("total_acceleration_cci_mps2")] public Vector3Value? TotalAccelerationCciMps2 { get; init; }
    [JsonPropertyName("total_acceleration_cce_mps2")] public Vector3Value? TotalAccelerationCceMps2 { get; init; }
    [JsonPropertyName("total_acceleration_orb_mps2")] public Vector3Value? TotalAccelerationOrbMps2 { get; init; }
    [JsonPropertyName("gravity_cci_mps2")] public Vector3Value? GravityCciMps2 { get; init; }
    [JsonPropertyName("gravity_cce_mps2")] public Vector3Value? GravityCceMps2 { get; init; }
    [JsonPropertyName("non_gravitational_acceleration_body_mps2")] public Vector3Value? NonGravitationalAccelerationBodyMps2 { get; init; }
    [JsonPropertyName("non_gravitational_acceleration_cci_mps2")] public Vector3Value? NonGravitationalAccelerationCciMps2 { get; init; }
    [JsonPropertyName("barometric_altitude_m")] public double? BarometricAltitudeM { get; init; }
    [JsonPropertyName("radar_altitude_m")] public double? RadarAltitudeM { get; init; }
    [JsonPropertyName("inertial_speed_mps")] public double? InertialSpeedMps { get; init; }
    [JsonPropertyName("surface_speed_mps")] public double? SurfaceSpeedMps { get; init; }
    [JsonPropertyName("total_mass_kg")] public double? TotalMassKg { get; init; }
    [JsonPropertyName("inert_mass_kg")] public double? InertMassKg { get; init; }
    [JsonPropertyName("propellant_mass_kg")] public double? PropellantMassKg { get; init; }
    [JsonPropertyName("parent_body")] public CelestialBodyDynamics? ParentBody { get; init; }
    [JsonPropertyName("orbit")] public OrbitState? Orbit { get; init; }
}

public sealed record EngineTelemetry
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("staged_enabled")] public bool? StagedEnabled { get; init; }
    [JsonPropertyName("ignited")] public bool? Ignited { get; init; }
    [JsonPropertyName("propellant_available")] public bool? PropellantAvailable { get; init; }
    [JsonPropertyName("requested_throttle")] public double? RequestedThrottle { get; init; }
    [JsonPropertyName("applied_throttle")] public double? AppliedThrottle { get; init; }
    [JsonPropertyName("minimum_throttle")] public double? MinimumThrottle { get; init; }
    [JsonPropertyName("maximum_throttle")] public double? MaximumThrottle { get; init; }
    [JsonPropertyName("current_thrust_n")] public double? CurrentThrustN { get; init; }
    [JsonPropertyName("maximum_available_thrust_n")] public double? MaximumAvailableThrustN { get; init; }
    [JsonPropertyName("current_thrust_body_n")] public Vector3Value? CurrentThrustBodyN { get; init; }
    [JsonPropertyName("current_thrust_cci_n")] public Vector3Value? CurrentThrustCciN { get; init; }
    [JsonPropertyName("thrust_position_body_m")] public Vector3Value? ThrustPositionBodyM { get; init; }
    [JsonPropertyName("nominal_thrust_direction_body")] public Vector3Value? NominalThrustDirectionBody { get; init; }
    [JsonPropertyName("gimbal_angles_rad")] public Vector3Value? GimbalAnglesRad { get; init; }
    [JsonPropertyName("gimbal_limits_rad")] public Vector3Value? GimbalLimitsRad { get; init; }
    [JsonPropertyName("specific_impulse_s")] public double? SpecificImpulseS { get; init; }
    [JsonPropertyName("mass_flow_rate_kgps")] public double? MassFlowRateKgps { get; init; }
    [JsonPropertyName("restart_available")] public bool? RestartAvailable { get; init; }
    [JsonPropertyName("remaining_ignitions")] public int? RemainingIgnitions { get; init; }
    [JsonPropertyName("gimbal_rate_limits_radps")] public Vector3Value? GimbalRateLimitsRadps { get; init; }
    [JsonPropertyName("ignition_delay_s")] public double? IgnitionDelayS { get; init; }
    [JsonPropertyName("shutdown_delay_s")] public double? ShutdownDelayS { get; init; }
    [JsonPropertyName("response_time_constant_s")] public double? ResponseTimeConstantS { get; init; }
    [JsonPropertyName("ambient_pressure_pa")] public double? AmbientPressurePa { get; init; }
}

public sealed record GimbalActuatorTelemetry
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("parent_part_id")] public required string ParentPartId { get; init; }
    [JsonPropertyName("angle_y_rad")] public required double AngleYRad { get; init; }
    [JsonPropertyName("angle_z_rad")] public required double AngleZRad { get; init; }
    [JsonPropertyName("limit_y_rad")] public required double LimitYRad { get; init; }
    [JsonPropertyName("limit_z_rad")] public required double LimitZRad { get; init; }
    [JsonPropertyName("rate_limit_y_radps")] public double? RateLimitYRadps { get; init; }
    [JsonPropertyName("rate_limit_z_radps")] public double? RateLimitZRadps { get; init; }
}

public sealed record PropulsionTelemetry
{
    [JsonPropertyName("requested_throttle")] public double? RequestedThrottle { get; init; }
    [JsonPropertyName("applied_throttle")] public double? AppliedThrottle { get; init; }
    [JsonPropertyName("minimum_throttle")] public double? MinimumThrottle { get; init; }
    [JsonPropertyName("maximum_throttle")] public double? MaximumThrottle { get; init; }
    [JsonPropertyName("current_thrust_body_n")] public Vector3Value? CurrentThrustBodyN { get; init; }
    [JsonPropertyName("current_thrust_cci_n")] public Vector3Value? CurrentThrustCciN { get; init; }
    [JsonPropertyName("maximum_available_thrust_n")] public double? MaximumAvailableThrustN { get; init; }
    [JsonPropertyName("maximum_available_thrust_body_n")] public Vector3Value? MaximumAvailableThrustBodyN { get; init; }
    [JsonPropertyName("maximum_available_thrust_cci_n")] public Vector3Value? MaximumAvailableThrustCciN { get; init; }
    [JsonPropertyName("mass_flow_rate_kgps")] public double? MassFlowRateKgps { get; init; }
    [JsonPropertyName("effective_specific_impulse_s")] public double? EffectiveSpecificImpulseS { get; init; }
    [JsonPropertyName("engines")] public IReadOnlyList<EngineTelemetry> Engines { get; init; } = [];
    [JsonPropertyName("gimbals")] public IReadOnlyList<GimbalActuatorTelemetry> Gimbals { get; init; } = [];
}

public sealed record RigidBodyTelemetry
{
    [JsonPropertyName("center_of_mass_vehicle_assembly_m")] public Vector3Value? CenterOfMassVehicleAssemblyM { get; init; }
    [JsonPropertyName("center_of_mass_body_m")] public Vector3Value? CenterOfMassBodyM { get; init; }
    [JsonPropertyName("inertia_body_kg_m2")] public Matrix3SymmetricValue? InertiaBodyKgM2 { get; init; }
    [JsonPropertyName("total_force_body_n")] public Vector3Value? TotalForceBodyN { get; init; }
    [JsonPropertyName("total_force_cci_n")] public Vector3Value? TotalForceCciN { get; init; }
    [JsonPropertyName("total_moment_body_nm")] public Vector3Value? TotalMomentBodyNm { get; init; }
    [JsonPropertyName("propulsive_force_body_n")] public Vector3Value? PropulsiveForceBodyN { get; init; }
    [JsonPropertyName("propulsive_moment_body_nm")] public Vector3Value? PropulsiveMomentBodyNm { get; init; }
    [JsonPropertyName("aerodynamic_force_body_n")] public Vector3Value? AerodynamicForceBodyN { get; init; }
    [JsonPropertyName("aerodynamic_moment_body_nm")] public Vector3Value? AerodynamicMomentBodyNm { get; init; }
    [JsonPropertyName("rcs_force_body_n")] public Vector3Value? RcsForceBodyN { get; init; }
    [JsonPropertyName("rcs_moment_body_nm")] public Vector3Value? RcsMomentBodyNm { get; init; }
    [JsonPropertyName("positive_torque_authority_body_nm")] public Vector3Value? PositiveTorqueAuthorityBodyNm { get; init; }
    [JsonPropertyName("negative_torque_authority_body_nm")] public Vector3Value? NegativeTorqueAuthorityBodyNm { get; init; }
    [JsonPropertyName("tvc_torque_authority_body_nm")] public Vector3Value? TvcTorqueAuthorityBodyNm { get; init; }
    [JsonPropertyName("rcs_torque_authority_body_nm")] public Vector3Value? RcsTorqueAuthorityBodyNm { get; init; }
}

public sealed record AtmosphereTelemetry
{
    [JsonPropertyName("pressure_pa")] public double? PressurePa { get; init; }
    [JsonPropertyName("density_kgpm3")] public double? DensityKgPm3 { get; init; }
    [JsonPropertyName("dynamic_pressure_pa")] public double? DynamicPressurePa { get; init; }
    [JsonPropertyName("air_relative_velocity_body_mps")] public Vector3Value? AirRelativeVelocityBodyMps { get; init; }
    [JsonPropertyName("air_relative_velocity_cci_mps")] public Vector3Value? AirRelativeVelocityCciMps { get; init; }
    [JsonPropertyName("air_relative_velocity_cce_mps")] public Vector3Value? AirRelativeVelocityCceMps { get; init; }
    [JsonPropertyName("temperature_k")] public double? TemperatureK { get; init; }
    [JsonPropertyName("speed_of_sound_mps")] public double? SpeedOfSoundMps { get; init; }
    [JsonPropertyName("mach")] public double? Mach { get; init; }
    [JsonPropertyName("wind_velocity_cci_mps")] public Vector3Value? WindVelocityCciMps { get; init; }
    [JsonPropertyName("angle_of_attack_rad")] public double? AngleOfAttackRad { get; init; }
    [JsonPropertyName("sideslip_angle_rad")] public double? SideslipAngleRad { get; init; }
}

public sealed record SurfaceTelemetry
{
    [JsonPropertyName("ground_contact")] public bool? GroundContact { get; init; }
    [JsonPropertyName("landed")] public bool? Landed { get; init; }
    [JsonPropertyName("ground_normal_body")] public Vector3Value? GroundNormalBody { get; init; }
    [JsonPropertyName("contact_count")] public int? ContactCount { get; init; }
    [JsonPropertyName("touchdown_event_seq")] public long? TouchdownEventSequence { get; init; }
    [JsonPropertyName("touchdown_vertical_speed_mps")] public double? TouchdownVerticalSpeedMps { get; init; }
    [JsonPropertyName("collision_event_seq")] public long? CollisionEventSequence { get; init; }
    [JsonPropertyName("structural_failure_event_seq")] public long? StructuralFailureEventSequence { get; init; }
    [JsonPropertyName("engine_out_event_seq")] public long? EngineOutEventSequence { get; init; }
    [JsonPropertyName("propellant_exhausted")] public bool? PropellantExhausted { get; init; }
    [JsonPropertyName("controllable")] public required bool Controllable { get; init; }
}

public sealed record LandingSiteDefinition
{
    [JsonPropertyName("body_id")] public required string BodyId { get; init; }
    [JsonPropertyName("position_ccf_m")] public required Vector3Value PositionCcfM { get; init; }
    [JsonPropertyName("latitude_deg")] public double? LatitudeDeg { get; init; }
    [JsonPropertyName("longitude_deg")] public double? LongitudeDeg { get; init; }
    [JsonPropertyName("altitude_m")] public double? AltitudeM { get; init; }
}

public sealed record LandingReferenceTelemetry
{
    [JsonPropertyName("site")] public required LandingSiteDefinition Site { get; init; }
    [JsonPropertyName("site_position_cci_m")] public Vector3Value? SitePositionCciM { get; init; }
    [JsonPropertyName("site_position_cce_m")] public Vector3Value? SitePositionCceM { get; init; }
    [JsonPropertyName("site_position_ccf_m")] public Vector3Value? SitePositionCcfM { get; init; }
    [JsonPropertyName("east_cci")] public Vector3Value? EastCci { get; init; }
    [JsonPropertyName("north_cci")] public Vector3Value? NorthCci { get; init; }
    [JsonPropertyName("up_cci")] public Vector3Value? UpCci { get; init; }
    [JsonPropertyName("position_enu_m")] public Vector3Value? PositionEnuM { get; init; }
    [JsonPropertyName("velocity_enu_mps")] public Vector3Value? VelocityEnuMps { get; init; }
    [JsonPropertyName("vertical_velocity_mps")] public double? VerticalVelocityMps { get; init; }
    [JsonPropertyName("horizontal_speed_mps")] public double? HorizontalSpeedMps { get; init; }
    [JsonPropertyName("distance_m")] public double? DistanceM { get; init; }
    [JsonPropertyName("gravity_enu_mps2")] public Vector3Value? GravityEnuMps2 { get; init; }
    [JsonPropertyName("ground_normal_enu")] public Vector3Value? GroundNormalEnu { get; init; }
    [JsonPropertyName("terrain_height_m")] public double? TerrainHeightM { get; init; }
    [JsonPropertyName("radar_altitude_m")] public double? RadarAltitudeM { get; init; }
    [JsonPropertyName("surface_slope_rad")] public double? SurfaceSlopeRad { get; init; }
    [JsonPropertyName("simulation_time_s")] public required double SimulationTimeS { get; init; }
}

public sealed record ThrustVectorTarget
{
    [JsonPropertyName("direction_cci")] public required Vector3Value DirectionCci { get; init; }
}

public sealed record ControlFramePayload
{
    [JsonPropertyName("lease_id")] public required Guid LeaseId { get; init; }
    [JsonPropertyName("control_seq")] public required long ControlSequence { get; init; }
    [JsonPropertyName("mode")] public required string Mode { get; init; }
    [JsonPropertyName("engine_on")] public required bool? EngineOn { get; init; }
    [JsonPropertyName("throttle")] public required double? Throttle { get; init; }
    [JsonPropertyName("thrust_vector_target")] public required ThrustVectorTarget? ThrustVectorTarget { get; init; }
}

public sealed record ControlCommandTelemetry
{
    [JsonPropertyName("control_seq")] public required long ControlSequence { get; init; }
    [JsonPropertyName("mode")] public required string Mode { get; init; }
    [JsonPropertyName("engine_on")] public bool? EngineOn { get; init; }
    [JsonPropertyName("throttle")] public double? Throttle { get; init; }
    [JsonPropertyName("thrust_vector_target")] public ThrustVectorTarget? ThrustVectorTarget { get; init; }
    [JsonPropertyName("monotonic_ms")] public required long MonotonicMs { get; init; }
}

public sealed record AppliedControlTelemetry
{
    [JsonPropertyName("lease_active")] public required bool LeaseActive { get; init; }
    [JsonPropertyName("lease_id")] public Guid? LeaseId { get; init; }
    [JsonPropertyName("active_mode")] public string? ActiveMode { get; init; }
    [JsonPropertyName("latest_requested")] public ControlCommandTelemetry? LatestRequested { get; init; }
    [JsonPropertyName("accepted")] public ControlCommandTelemetry? Accepted { get; init; }
    [JsonPropertyName("applied")] public ControlCommandTelemetry? Applied { get; init; }
    [JsonPropertyName("applied_game_frame")] public long? AppliedGameFrame { get; init; }
    [JsonPropertyName("applied_simulation_time_s")] public double? AppliedSimulationTimeS { get; init; }
    [JsonPropertyName("rejection_reason")] public string? RejectionReason { get; init; }
    [JsonPropertyName("saturation_reason")] public string? SaturationReason { get; init; }
}

public sealed record FlightStatePayload
{
    [JsonPropertyName("frame_number")] public required long FrameNumber { get; init; }
    [JsonPropertyName("player_time_s")] public required double PlayerTimeS { get; init; }
    [JsonPropertyName("delta_time_s")] public required double DeltaTimeS { get; init; }
    [JsonPropertyName("simulation_speed")] public double? SimulationSpeed { get; init; }
    [JsonPropertyName("captured_monotonic_ms")] public required long CapturedMonotonicMs { get; init; }
    [JsonPropertyName("capabilities")] public required CapabilitySet Capabilities { get; init; }
    [JsonPropertyName("vehicle")] public VehicleState? Vehicle { get; init; }
    [JsonPropertyName("propulsion")] public PropulsionTelemetry? Propulsion { get; init; }
    [JsonPropertyName("rigid_body")] public RigidBodyTelemetry? RigidBody { get; init; }
    [JsonPropertyName("atmosphere")] public AtmosphereTelemetry? Atmosphere { get; init; }
    [JsonPropertyName("surface")] public SurfaceTelemetry? Surface { get; init; }
    [JsonPropertyName("landing_reference")] public LandingReferenceTelemetry? LandingReference { get; init; }
    [JsonPropertyName("applied_control")] public required AppliedControlTelemetry AppliedControl { get; init; }
    [JsonPropertyName("manual_control")] public ManualControlState? ManualControl { get; init; }
}
