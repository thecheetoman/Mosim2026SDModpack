using Games.Rebuilt.Components;
using Games.Rebuilt.Robots;
using MoSimLib;
using RobotFramework.Components;
using RobotFramework.Controllers.PidSystems;
using RobotFramework.Enums;
using UnityEngine;

public class ΣMotion : RebuiltTurretRobot
{
    [Header("Joints")]
    [SerializeField] private GenericJoint intakeJoint;
    [SerializeField] private GenericJoint hopperJoint;
    [SerializeField] private GenericJoint hoodJoint;
    [SerializeField] private RollerGroup intakeRollers;
    [SerializeField] private RollerGroup indexerRollers;
    [SerializeField] private RollerGroup shooterIndexerA;
    [SerializeField] private RollerGroup shooterIndexerB;
    [SerializeField] private RollerGroup shooterIndexerC;
    [SerializeField] private RollerGroup shooterIndexerD;
    [SerializeField] private GenericTurret turret;
    [SerializeField] private RollerGroup flywheelRollers;

    [Header("PIDS")]
    [SerializeField] private PidConstants intakePidConstants;
    [SerializeField] private PidConstants hopperPidConstants;
    [SerializeField] private PidConstants hoodPidConstants;
    [SerializeField] private PidConstants turretPidConstants;

    // Other PIDs and mechanism values: add serialized tuning fields here.

    [Header("Intake Values")]
    [SerializeField] private float intakeAngle;
    [SerializeField] private float intakeRpm = 2400f;

    [Header("Hopper Values")]
    [SerializeField] private float extendedDistance; // 0 = extended (inches)
    [SerializeField] private float hopperTolerance = 1f; // inches

    [Header("Indexer Values")]
    [SerializeField] private float indexerShootRpm = 1600f;
    [SerializeField] private float indexerIdleRpm = 800f;
    [SerializeField] private float indexerOuttakeRpm = -1000f;

    [Header("Shooter Indexer Values")]
    [SerializeField] private float shooterIndexerRpm = 2000f;
    [SerializeField] private float shooterIndexerReverseDelay = 0.1f;

    [Header("Turret Values")]
    [SerializeField] private float turretTolerance = 5f;
    [SerializeField] private bool requireAlignmentToShoot = false; // off = feed whenever shoot is requested

    [Header("Shooter Values")]
    [SerializeField] private RebuiltInterpolation flywheelInterpolation;
    [SerializeField] private RebuiltInterpolation hoodInterpolation;

    // Hood angles (table values, stow angle, limits, tolerance) are all in "hood space":
    // 0 = stowed and positive = opened, so 1 means 1 degree of opening.
    // With invertHood on, the joint is commanded the negative of that (1 -> -1 degree).
    [Header("Hood Values")]
    [SerializeField] private JointAxis hoodAxis = JointAxis.Y;
    [SerializeField] private bool invertHood = true;
    [SerializeField] private float hoodStowAngle = 0f;
    [SerializeField] private float hoodMinAngle = 0f;   // fully stowed
    [SerializeField] private float hoodMaxAngle = 25f;  // fully out
    [SerializeField] private float hoodTolerance = 2f;  // degrees

    private const float InchesToMeters = 0.0254f;

    private float HoodSign => invertHood ? -1f : 1f;

    private float _targetIntakeAngle;
    private float _targetHoodAngle; // hood space (positive = opened)
    private float _flywheelTargetRpm; // computed in the zone methods, applied only while shooting
    private bool _intakeDeployed;
    private bool _hopperLockedRetracted;
    private bool _hopperExtended;
    private float _shooterIndexerReverseTimer;

    // Other runtime state: add mechanism targets and toggles here.
    // swmRef, swmSettings, rb, and driveController are inherited.

    protected override void Init()
    {
        // Registers HandleSwm, zone dispatch, and shooter-state dispatch exactly once.
        base.Init();

        intakeJoint.SetPid(intakePidConstants);
        hopperJoint.SetPid(hopperPidConstants);
        hoodJoint.SetPid(hoodPidConstants);
        turret.Initialize(this);
        turret.SetPid(turretPidConstants);
        flywheelInterpolation.Initialize(this);
        hoodInterpolation.Initialize(this);

        _intakeDeployed = false;
        _targetIntakeAngle = 110;
        _targetHoodAngle = hoodStowAngle;
        _hopperLockedRetracted = true;
        _hopperExtended = false;

        // Hopper starts locked via the inspector (X/Y/Z Motion = Locked).
        // Do not call hopperJoint.lockAllAxis() here: it throws a NullReferenceException in Init().

        // TODO: Set initial PIDs, mechanism targets, and interpolation maps for the remaining mechanisms.

        Scheduler.AddSubsystems(Intake, Hopper, TurretTrack);
        Scheduler.AddProtectedSystem(UpdateJoints);
        Scheduler.AddLateRecursive(UpdatePids);
    }
    // fix for hood interpolation not working on first boot(in editor and in game)
    protected override void LateInit()
    {
        base.LateInit();
        // force interpolation maps to fix hood disfunctionality
        flywheelInterpolation.SetDefaultMap(DefaultMap.Hub);
        hoodInterpolation.SetDefaultMap(DefaultMap.Hub);
        // apply changes via setHoodTarget
        SetHoodTarget();
    }

    private void Intake()
    {
        if (IntakeAction)
        {
            _targetIntakeAngle = intakeAngle; // 0 = out
            _intakeDeployed = true;
        }

        float direction = RobotSpecial.IsPressed() ? -1f : 1f;

        if (_intakeDeployed && IntakeAction)
        {
            intakeRollers.SetRpm(intakeRpm * direction);
        }
        else
        {
            intakeRollers.Stop();
        }
    }

    private void Hopper()
    {
        if (!_intakeDeployed || _hopperExtended)
        {
            return;
        }

        // Free the Y axis once when deploying starts.
        if (_hopperLockedRetracted)
        {
            hopperJoint.freeLinearAxis(JointAxis.Y);
            _hopperLockedRetracted = false;
        }

        hopperJoint.SetLinearTarget(extendedDistance).withAxis(JointAxis.Y);

        // Direction-agnostic: lock once within tolerance of the target, from either side.
        float targetMeters = extendedDistance * InchesToMeters;
        float currentMeters = hopperJoint.GetAxisLocation(JointAxis.Y);
        if (Mathf.Abs(currentMeters - targetMeters) <= hopperTolerance * InchesToMeters)
        {
            hopperJoint.lockAllAxis();
            _hopperExtended = true;
        }
    }

    // The target (hub / pass) is chosen in AllianceZone / NeutralZone.
    // Align drives the turret toward it.
    // For steer feed forward, use turret.Align(RotateAction.ReadValue<float>()), which needs
    // Unity.InputSystem added to your assembly definition references.
    private void TurretTrack()
    {
        turret.Align();
    }

    private void UpdatePids()
    {
        intakeJoint.UpdatePid(intakePidConstants);
        hopperJoint.UpdatePid(hopperPidConstants);
        hoodJoint.UpdatePid(hoodPidConstants);
        turret.UpdatePid(turretPidConstants);
        // TODO: Update each remaining joint's PID constants for live tuning.
    }

    private void UpdateJoints()
    {
        intakeJoint.SetTargetAngle(_targetIntakeAngle).withAxis(JointAxis.Z);
        // Hood space -> joint space (inverted when invertHood is on).
        hoodJoint.SetTargetAngle(_targetHoodAngle * HoodSign).withAxis(hoodAxis);
        // TODO: Apply remaining mechanism targets to the joints.
    }

    // Stows the hood under the trench, otherwise follows the interpolation table.
    // The result is clamped to [hoodMinAngle, hoodMaxAngle].
    private void SetHoodTarget()
    {
        float target = UnderTrench() ? hoodStowAngle : hoodInterpolation.Interpolate();
        _targetHoodAngle = Mathf.Clamp(target, hoodMinAngle, hoodMaxAngle);
    }

    protected override void HandleSwm()
    {
        // Assign the inherited swmRef (with a parent) and swmSettings in the Inspector.
        // SOTMSettings is a struct; only the Rigidbody and Transform references can be null.
        if (rb != null && swmRef != null && swmRef.parent != null)
        {
            base.HandleSwm();
        }
    }

    // The target flywheel speed and hood angle are computed here every frame.
    // The flywheel itself only runs in Shooting() / IntakingAndShooting().
    protected override void AllianceZone()
    {
        turret.SetDefaultMap(GenericTurret.DefaultMap.Hub);
        flywheelInterpolation.SetDefaultMap(DefaultMap.Hub);
        hoodInterpolation.SetDefaultMap(DefaultMap.Hub);
        _flywheelTargetRpm = flywheelInterpolation.Interpolate();
        SetHoodTarget();
    }

    protected override void NeutralZone()
    {
        turret.SetPassMapClosest();
        // If RebuiltInterpolation has a pass map, set it on both interpolations here;
        // otherwise they keep the last map.
        _flywheelTargetRpm = flywheelInterpolation.Interpolate();
        SetHoodTarget();
    }

    private void SetIndexerRpm(float rpm)
    {
        indexerRollers.SetRpm(RobotSpecial.IsPressed() ? indexerOuttakeRpm : rpm);
    }

    private void RunShooterIndexer()
    {
        if (OuttakeAction.WasTriggered())
        {
            _shooterIndexerReverseTimer = shooterIndexerReverseDelay;
        }

        float rpm = _shooterIndexerReverseTimer > 0f ? -shooterIndexerRpm : shooterIndexerRpm;

        shooterIndexerA.SetRpm(rpm);
        shooterIndexerB.SetRpm(rpm);
        shooterIndexerC.SetRpm(-rpm);
        shooterIndexerD.SetRpm(-rpm);

        if (_shooterIndexerReverseTimer > 0f)
        {
            _shooterIndexerReverseTimer -= Time.fixedDeltaTime;
        }
    }

    private void StopShooterIndexer()
    {
        _shooterIndexerReverseTimer = 0f;
        shooterIndexerA.Stop();
        shooterIndexerB.Stop();
        shooterIndexerC.Stop();
        shooterIndexerD.Stop();
    }

    // Stop() lasts one frame, so it is called every frame in the non-shooting states.
    protected override void Intaking()
    {
        SetIndexerRpm(indexerIdleRpm);
        flywheelRollers.Stop();
        StopShooterIndexer();
    }

    protected override void IntakingAndShooting()
    {
        SetIndexerRpm(indexerShootRpm);
        flywheelRollers.SetRpm(_flywheelTargetRpm);
        RunShooterIndexer();
    }

    protected override void Shooting()
    {
        SetIndexerRpm(indexerShootRpm);
        flywheelRollers.SetRpm(_flywheelTargetRpm);
        RunShooterIndexer();
    }

    protected override void Idling()
    {
        SetIndexerRpm(indexerIdleRpm);
        flywheelRollers.Stop();
        StopShooterIndexer();
    }

    protected override bool Aligned()
    {
        // The base class only calls Shooting()/IntakingAndShooting() when this returns true,
        // so returning true here removes the alignment gate on feeding.
        if (!requireAlignmentToShoot)
        {
            return true;
        }

        // Convert the measured joint angle back to hood space before comparing.
        float hoodActual = hoodJoint.GetAngle() * HoodSign;

        return Mathf.Abs(turret.GetError()) <= turretTolerance
            && Mathf.Abs(hoodActual - _targetHoodAngle) <= hoodTolerance
            && flywheelRollers.AtSpeed();
    }
}