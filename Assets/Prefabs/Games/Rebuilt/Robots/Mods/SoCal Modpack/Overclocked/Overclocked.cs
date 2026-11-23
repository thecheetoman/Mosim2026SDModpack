using System;
using Games.Rebuilt.Components;
using Games.Rebuilt.Robots;
using MoSimLib;
using RobotFramework.Components;
using RobotFramework.Controllers.PidSystems;
using RobotFramework.Enums;
using UnityEngine;

public class Overclocked : RebuiltRobotBase
{
    // intake related settings
    [Header("Intake settings")]
    [SerializeField] private GenericJoint intakeJoint;
    
    [SerializeField] private PidConstants intakePidSettings;
    [SerializeField] private PidConstants intakeAgitatePid;   // copy of intakePidSettings with a lower Max
    [SerializeField, Min(0f)] private float agitateDelay = 0.15f;
    private bool _agitating;
    private float _agitateTimer;
    [SerializeField] private float intakeOutValue = 0;
    [SerializeField] private float intakeStowValue = 0;
    [SerializeField] private float intakeAgitatingValue = 0;
    // variables for handling intake state, if agitating, move the intake slower
    private float _targetIntakeDist;
    private bool _intakeDeployed;
    // intake rollers and rpm settings
    [SerializeField] private RollerGroup intakeRoller;
    [SerializeField] private float intakeActiveRPM = 0;
    [SerializeField] private float intakeInactiveRPM = 0;
    // hopper that moves with intake
    [SerializeField] private GenericJoint hopperJoint;
    [SerializeField] private PidConstants hopperPidSettings;
    private float _hopperDeployDistance;
    [SerializeField] private float hopperInValue = -0.3f;
    [SerializeField] private float hopperOutValue = 0f;
    [SerializeField] private float hopperAngleDeg = 18f;
    [SerializeField, Min(0f)] private float hopperExtraTravel = 0.05f; // 0.05 = hopper moves 5% further than the geometry says
    
    [Header("Indexer Settings")]
    [SerializeField] private RollerGroup indexerRollers;
    [SerializeField] private float indexerRPM = 0;
    [SerializeField] private float shootingIndexerRPM = 0;
    
    [Header("Autoalign Settings")]
    [SerializeField] private RebuiltAutoAimController aim;
    [SerializeField, Min(0f)] private float scoringAimTolerance = 4f;   // degrees, hub shots
    [SerializeField, Min(0f)] private float passingAimTolerance = 15f;  // degrees, passes
    
    [Header("Shooter Indexer Settings")]
    [SerializeField] private RollerGroup shooterIndexers;

    [SerializeField] private float shooterIndexerRPM = 0;
    [Header("Shooter Settings")]
    [SerializeField] private RollerGroup shooterRollers;   // flywheel drum
    [SerializeField] private RollerGroup hoodRollers;      // rollers on the hood
    [SerializeField] private GenericJoint hoodJoint;
    [SerializeField] private PidConstants hoodPidSettings;
    [SerializeField] private JointAxis hoodAxis = JointAxis.X;
    [SerializeField] private Vector2 hoodAngleLimits = new(0f, 35f);        // min, max in degrees
    [SerializeField, Min(0.01f)] private float hoodRpmReduction = 1.5f;     // hood rollers run at flywheel RPM / this
    [SerializeField, Min(0f)] private float hoodAngleTolerance = 0.5f;
    [SerializeField] private float idleHoodAngle = 0f; // Hood angle when not shooting// degrees
    // all readiness checks must hold this long before feeding starts
    [SerializeField, Min(0f)] private float feedReadyDebounce = 0.06f;

    [Header("Shot Interpolation")]
    [SerializeField] private RebuiltInterpolation hubFlywheelInterpolation;
    [SerializeField] private RebuiltInterpolation hubHoodInterpolation;
    [SerializeField] private RebuiltInterpolation passFlywheelInterpolation;
    [SerializeField] private RebuiltInterpolation passHoodInterpolation;

    private float _targetHoodAngle;
    private bool _shooterReady;
    private float _feedReadyTimer;
    private bool _feedReady;


    // Runtime state: add target positions, toggles, and the auto-aim controller here.

    protected override void Init()
    {
        // TODO: Set initial PIDs, targets, and toggles; initialize interpolation maps.
        base.Init();
        // make sure that the aimcontroller exists
        if (aim == null && !TryGetComponent(out aim))
        {
            Debug.LogError("aim controller yo pls", this);
        }
        // set initial intake pid and add it to the updatePids
        _targetIntakeDist = intakeStowValue;
        _hopperDeployDistance = hopperInValue;
        intakeJoint.SetPid(intakePidSettings);
        hopperJoint.SetPid(hopperPidSettings);
        hoodJoint.SetPid(hoodPidSettings);
        hubFlywheelInterpolation.Initialize(this);
        hubHoodInterpolation.Initialize(this);
        passFlywheelInterpolation.Initialize(this);
        passHoodInterpolation.Initialize(this);
        _targetHoodAngle = Mathf.Clamp(idleHoodAngle, hoodAngleLimits.x, hoodAngleLimits.y);
        _intakeDeployed = false;
        _feedReady = false;
        _feedReadyTimer = 0f;
        
        Scheduler.AddSubsystems(Intake, Hopper, Shooter, HandleAutoAim, Indexer);
        Scheduler.AddProtectedSystem(UpdateJoints);
        Scheduler.AddLateRecursive(UpdatePids);
    }

    private void UpdatePids()
    {
        // set intake pids
        intakeJoint.UpdatePid(intakePidSettings);
        hopperJoint.UpdatePid(hopperPidSettings);
        hoodJoint.UpdatePid(hoodPidSettings);
    }

    private void Indexer()
    {
        // feeds only once aimed, flywheel + hood rollers at speed, and hood at angle
        if (ReadyToFeed())
        {
            indexerRollers.SetRpm(shootingIndexerRPM);
        }
        else
        {
            indexerRollers.SetRpm(indexerRPM);
        }
    }

    private void Intake()
    {
        if (IntakeAction)
        {
            _intakeDeployed = true;
        }

        if (!_intakeDeployed)
        {
            _targetIntakeDist = intakeStowValue;
            intakeRoller.Stop();
            _agitating = false;
            _agitateTimer = 0f;
            return;
        }

        // Count how long we've been shooting (and not intaking). Releasing resets it.
        if (ReadyToShoot() && !IntakeAction)
        {
            _agitateTimer += Time.fixedDeltaTime;
        }
        else
        {
            _agitateTimer = 0f;
        }
        _agitating = _agitateTimer >= agitateDelay;

        _targetIntakeDist = _agitating ? intakeAgitatingValue : intakeOutValue;
        intakeRoller.SetRpm(IntakeAction ? intakeActiveRPM : intakeInactiveRPM);
    }

    private void Hopper()
    {
        float intakePos = intakeJoint.GetAxisLocation(JointAxis.X);

        // 0 when the intake is out, negative as it stows
        float fromOut = intakePos - intakeOutValue;

        // cosine: the hopper travels cos(angle) of the intake's travel, plus a little extra
        float geometric = fromOut * Mathf.Cos(hopperAngleDeg * Mathf.Deg2Rad);
        float target = hopperOutValue + geometric * (1f + hopperExtraTravel);

        // never go past the hopper's physical limits
        _hopperDeployDistance = Mathf.Clamp(
            target,
            Mathf.Min(hopperInValue, hopperOutValue),
            Mathf.Max(hopperInValue, hopperOutValue));
    }


    private void HandleAutoAim()
    {
        if (aim == null) return;

        if (OuttakeAction && InAllianceZone())
        {
            aim.SetDefaultMap(DefaultMap.Hub);   // hub shot from your alliance zone
            aim.Aim();
        }
        else if (IsPassShot())
        {
            aim.SetPassMapClosest();             // AutoPass, or shoot key outside your zone
            aim.Aim();
        }
        else
        {
            aim.SetDefaultMap(DefaultMap.Hub);   // idle: keep the hub selected
        }
    }

    private void Shooter()
    {
        if (!WantsToShoot())
        {
            shooterIndexers.SetRpm(-1000);
            shooterRollers.Stop();
            hoodRollers.Stop();
            _targetHoodAngle = Mathf.Clamp(idleHoodAngle, hoodAngleLimits.x, hoodAngleLimits.y);
            _shooterReady = false;
            _feedReady = false;
            _feedReadyTimer = 0f;
            return;
        }

        bool pass = IsPassShot();
        RebuiltInterpolation flywheelInterp = pass ? passFlywheelInterpolation : hubFlywheelInterpolation;
        RebuiltInterpolation hoodInterp = pass ? passHoodInterpolation : hubHoodInterpolation;

        // choose what the tables measure distance to
        if (pass)
        {
            passFlywheelInterpolation.SetPassMapClosest();
            passHoodInterpolation.SetPassMapClosest();
        }
        else
        {
            hubFlywheelInterpolation.SetDefaultMap(DefaultMap.Hub);
            hubHoodInterpolation.SetDefaultMap(DefaultMap.Hub);
        }

        float rpm = flywheelInterp.Interpolate();
        float angle = Mathf.Clamp(hoodInterp.Interpolate(), hoodAngleLimits.x, hoodAngleLimits.y);

        shooterRollers.SetRpm(rpm);
        hoodRollers.SetRpm(rpm / hoodRpmReduction);
        _targetHoodAngle = angle;

        _shooterReady = shooterRollers.AtSpeed() && hoodRollers.AtSpeed() && HoodAtAngle();

        // the shooter indexers only run once every readiness check has held for the debounce time
        UpdateFeedReady();
        shooterIndexers.SetRpm(_feedReady ? shooterIndexerRPM : 0f);
    }

    private bool HoodAtAngle()
    {
        return Mathf.Abs(Mathf.DeltaAngle(hoodJoint.GetAngle(), _targetHoodAngle)) <= hoodAngleTolerance;
    }

    private void SetInterpMap()
    {
        // TODO: Select the hub / pass maps for the flywheel and hood.
    }

    private void UpdateJoints()
    {
        intakeJoint.SetLinearTarget(_targetIntakeDist/ 0.0254f).withAxis(JointAxis.X);
        hopperJoint.SetLinearTarget(_hopperDeployDistance / 0.0254f).withAxis(JointAxis.X);
        hoodJoint.SetTargetAngle(_targetHoodAngle).withAxis(hoodAxis);
    }

    private bool WantsToShoot()
    {
        return (OuttakeAction || AutoPass || ManualShoot) && _intakeDeployed;
    }
    private bool IsPassShot()
    {
        return AutoPass || (OuttakeAction && !InAllianceZone());
    }

    private bool AimedToShoot()
    {
        if (aim == null) return true;
        float tolerance = IsPassShot() ? passingAimTolerance : scoringAimTolerance;
        return Mathf.Abs(aim.GetRotationError()) <= tolerance;
    }

    // The temporary shoot gate: wants to shoot AND aimed (ManualShoot skips the aim check).
    private bool ReadyToShoot()
    {
        return WantsToShoot() && (ManualShoot || AimedToShoot());
    }

    // Debounced: aimed (or ManualShoot), flywheel + hood rollers at speed, hood at angle.
    private void UpdateFeedReady()
    {
        bool ready = WantsToShoot() && _shooterReady && (ManualShoot || AimedToShoot());
        _feedReadyTimer = ready ? _feedReadyTimer + Time.fixedDeltaTime : 0f;
        _feedReady = _feedReadyTimer >= feedReadyDebounce;
    }

    private bool ReadyToFeed()
    {
        return _feedReady;
    }
}