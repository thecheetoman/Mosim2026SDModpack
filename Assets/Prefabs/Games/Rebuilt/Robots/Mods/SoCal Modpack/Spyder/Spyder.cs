using Games.Rebuilt.Components;
using Games.Rebuilt.Robots;
using MoSimLib;
using RobotFramework.Components;
using RobotFramework.Controllers.PidSystems;
using RobotFramework.Enums;
using UnityEngine;

public class Spyder : RebuiltRobotBase
{
    [Header("Intake Settings")]
    // intake variables and object references
    [SerializeField] private GenericJoint intakeJoint;

    [SerializeField] private PidConstants intakePidSettings;
    [SerializeField] private PidConstants intakeAgitatePidSettings;
    [SerializeField, Min(0f)] private float agitateDelay = 0.25f;
    // private values for handling intake agitation while shooting and other stuff
    private bool _agitating;
    private float _agitateTimer;
    
    private bool _intakeDeployed;
    private float _targetIntakeDist;
    
    // intake position settings
    [SerializeField] private float intakeOutValue = 0;
    [SerializeField] private float intakeInValue = 0;
    [SerializeField] private float intakeAgitateValue = 0;
    // intake roller settings
    [SerializeField] private RollerGroup intakeRoller;
    [SerializeField] private float intakeActiveRPM = 0f;
    [SerializeField] private float intakeInactiveRPM = 0f;
    
    // indexer config
    [Header("Indexer Setup")]
    [SerializeField] private RollerGroup indexerRollers;
    [SerializeField] private RollerGroup shooterIndexers;
    
    [SerializeField] private float indexerIdleRPM = 0f;
    [SerializeField] private float indexerShootingRPM = 0f;
    
    [Header("Auto Align Settings")]
    [SerializeField] private RebuiltAutoAimController aim;
    [SerializeField] private float scoringAimTolerance = 4f;
    [SerializeField] private float passingAimTolerance = 15f;
    
    [Header("Shooter Settings")]
    [SerializeField] private RollerGroup shooterRollers;   // flywheel drum
    [SerializeField] private RollerGroup hoodRollers;      // rollers on the hood
    [SerializeField, Min(0.01f)] private float hoodRpmReduction = 1.5f;   // hood rollers run at flywheel RPM / this
    [SerializeField] private GenericJoint hoodJoint;

    [SerializeField] private PidConstants hoodPid;
    [SerializeField] private JointAxis hoodAxis = JointAxis.X;         // the axis the hood pivots on
    [SerializeField] private Vector2 hoodAngleLimits = new(0f, 35f);  // min, max in degrees
    [SerializeField] private float idleHoodAngle = 0f;                // hood angle when not shooting
    [SerializeField, Min(0f)] private float hoodAngleTolerance = 0.5f;
    [SerializeField, Min(0f)] private float feedReadyDebounce = 0.06f;  // readiness must hold this long before feeding

    [Header("Shot Interpolation")]
    [SerializeField] private RebuiltInterpolation shooterHoodInterpolation;
    [SerializeField] private RebuiltInterpolation shooterFlywheelInterpolation;
    // optional separate tables for passes; fall back to the hub tables if left empty
    [SerializeField] private RebuiltInterpolation passHoodInterpolation;
    [SerializeField] private RebuiltInterpolation passFlywheelInterpolation;

    private float _targetHoodAngle = 0;
    // bool if shooter is ready for feeding
    private bool shooterReady = false;
    private float _feedReadyTimer;
    private bool _feedReady;

    protected override void Init()
    {
        // TODO: Set initial PIDs, targets, and toggles; initialize interpolation maps.
        base.Init();
        // double check that aim controller actually exists
        if (aim == null && !TryGetComponent(out aim))
        {
            Debug.LogError("aim controller yo pls", this);
        }
        _targetIntakeDist = intakeInValue;
        intakeJoint.SetPid(intakePidSettings);
        hoodJoint.SetPid(hoodPid);
        shooterHoodInterpolation.Initialize(this);
        shooterFlywheelInterpolation.Initialize(this);
        if (passHoodInterpolation != null)
        {
            passHoodInterpolation.Initialize(this);
        }
        if (passFlywheelInterpolation != null)
        {
            passFlywheelInterpolation.Initialize(this);
        }
        _targetHoodAngle = Mathf.Clamp(idleHoodAngle, hoodAngleLimits.x, hoodAngleLimits.y);
        shooterReady = false;
        _feedReady = false;
        _feedReadyTimer = 0f;
        _intakeDeployed = false;
        // Shooter runs before Indexer so the indexer sees this frame's feed-ready state
        Scheduler.AddSubsystems(Intake, Shooter, Indexer, HandleAutoAim, Climber);
        Scheduler.AddProtectedSystem(UpdateJoints);
        Scheduler.AddLateRecursive(UpdatePids);
    }

    private void UpdatePids()
    {
        intakeJoint.UpdatePid(intakePidSettings);
        hoodJoint.UpdatePid(hoodPid);
    }

    private void Climber()
    {
        // TODO: Read ClimbAction and choose the climber target.
    }

    private void Intake()
    {
        // TODO: Read IntakeAction / StowAction and control deployment and rollers.
        // Call WobbleIntake() here if the mechanism supports it.
        if (IntakeAction)
        {
            _intakeDeployed = true;
        }

        if (!_intakeDeployed)
        {
            // if intake isnt deployed, intake should be in it shouldnt be able to agitate
            _targetIntakeDist = intakeInValue;
            _agitateTimer = 0f;
            _agitating = false;
            intakeRoller.Stop();
            return;
        }
        
        // if shooting, check if aligned and if still intake, then count how long and if ready, agitate
        if (ReadyToShoot() && !IntakeAction)
        {
            _agitateTimer += Time.fixedDeltaTime;
        }
        else
        {
            _agitateTimer = 0f;
        }
        // now set the angle based on agitation timer
        _agitating = _agitateTimer >= agitateDelay;
        _targetIntakeDist = _agitating ? intakeAgitateValue : intakeOutValue;
        // based on active state, run intake or high at low states
        intakeRoller.SetRpm(IntakeAction ? intakeActiveRPM : intakeInactiveRPM);
    }

    private void Indexer()
    {
        // feeds only once aimed, flywheel and hood rollers at speed, and hood at angle
        if (_feedReady)
        {
            indexerRollers.SetRpm(indexerShootingRPM);
            shooterIndexers.SetRpm(indexerShootingRPM);
        }
        else
        {
            indexerRollers.SetRpm(indexerIdleRPM);
            shooterIndexers.Stop();
        }
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
            shooterRollers.Stop();
            hoodRollers.Stop();
            _targetHoodAngle = Mathf.Clamp(idleHoodAngle, hoodAngleLimits.x, hoodAngleLimits.y);
            shooterReady = false;
            _feedReady = false;
            _feedReadyTimer = 0f;
            return;
        }

        bool pass = IsPassShot();
        RebuiltInterpolation hoodInterp = (pass && passHoodInterpolation != null)
            ? passHoodInterpolation : shooterHoodInterpolation;
        RebuiltInterpolation flywheelInterp = (pass && passFlywheelInterpolation != null)
            ? passFlywheelInterpolation : shooterFlywheelInterpolation;

        // choose what the tables measure distance to
        if (pass)
        {
            hoodInterp.SetPassMapClosest();
            flywheelInterp.SetPassMapClosest();
        }
        else
        {
            hoodInterp.SetDefaultMap(DefaultMap.Hub);
            flywheelInterp.SetDefaultMap(DefaultMap.Hub);
        }

        float rpm = flywheelInterp.Interpolate();
        _targetHoodAngle = Mathf.Clamp(hoodInterp.Interpolate(), hoodAngleLimits.x, hoodAngleLimits.y);

        shooterRollers.SetRpm(rpm);
        hoodRollers.SetRpm(rpm / hoodRpmReduction);

        shooterReady = shooterRollers.AtSpeed() && hoodRollers.AtSpeed() && HoodAtAngle();
        UpdateFeedReady();
    }

    // aimed (or ManualShoot), flywheel and hood rollers at speed, hood at angle, held for the debounce time
    private void UpdateFeedReady()
    {
        bool ready = WantsToShoot() && shooterReady && (ManualShoot || AimedToShoot());
        _feedReadyTimer = ready ? _feedReadyTimer + Time.fixedDeltaTime : 0f;
        _feedReady = _feedReadyTimer >= feedReadyDebounce;
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
        intakeJoint.SetLinearTarget(_targetIntakeDist / 0.0254f).withAxis(JointAxis.X);
        hoodJoint.SetTargetAngle(_targetHoodAngle).withAxis(hoodAxis);
    }

    private void WobbleIntake()
    {
        // TODO: Adjust the intake target to clear a jam or agitate fuel.
    }
    private bool AimedToShoot()
    {
        if (aim == null) return true;
        float tolerance = IsPassShot() ? passingAimTolerance : scoringAimTolerance;
        return Mathf.Abs(aim.GetRotationError()) <= tolerance;
    }
    private bool WantsToShoot()
    {
        return (OuttakeAction || AutoPass || ManualShoot) && _intakeDeployed;
    }
    private bool IsPassShot()
    {
        return AutoPass || (OuttakeAction && !InAllianceZone());
    }
    private bool ReadyToShoot()
    {
        return WantsToShoot() && (ManualShoot || AimedToShoot());
    }
}