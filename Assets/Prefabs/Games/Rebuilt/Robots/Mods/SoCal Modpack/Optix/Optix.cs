using Games.Rebuilt.Components;
using Games.Rebuilt.Robots;
using MoSimLib;
using RobotFramework.Components;
using RobotFramework.Controllers.PidSystems;
using RobotFramework.Enums;
using UnityEngine;

public class Optix : RebuiltRobotBase
{
    [Header("Intake Settings")]
    [SerializeField] private GenericJoint intakeJoint;

    [SerializeField] private float intakeInValue = 0;
    [SerializeField] private float intakeOutValue = 0;
    [SerializeField] private PidConstants intakePid;

    // used while the intake is pulled in by shooting (copy of intakePid with a lower Max to move slower)
    [SerializeField] private PidConstants intakeAgitationPid;
    [SerializeField, Min(0f)] private float agitateDelay = 0.25f;

    [SerializeField] private RollerGroup intakeRoller;
    [SerializeField] private float intakeActiveRPM;
    [SerializeField] private float intakeInactiveRPM;

    private bool _agitating;      // true while the intake is pulled in by shooting
    private float _agitateTimer;
    
    private bool _intakeDeployed;

    private float _targetIntakeDist;
    [Header("Beltdexer and shooter indexer")]
    [SerializeField] private RollerGroup indexer;
    [SerializeField] private float indexerInactiveRPM = 0f;
    [SerializeField] private float indexerActiveRPM = 0f;

    [SerializeField] private RollerGroup shooterIndexer;

    [SerializeField] private float shooterIndexeRpm = 0f;

    [Header("Shooter Settings")]
    [SerializeField] private RollerGroup shooterRoller;

    [SerializeField] private RebuiltInterpolation hubShootingInterpolation;

    [SerializeField] private RebuiltInterpolation passingInterpolation;
    // Runtime state: add target positions, toggles, and the auto-aim controller here.
    [Header("Auto Align Settings")]
    [SerializeField] private RebuiltAutoAimController aim;
    [SerializeField] private float scoringAimTolerance = 4f;
    [SerializeField] private float passingAimTolerance = 15f;

    protected override void Init()
    {
        base.Init();
        intakeJoint.SetPid(intakePid);
        _targetIntakeDist = intakeInValue;
        _intakeDeployed = false;
        _agitating = false;
        _agitateTimer = 0f;
        hubShootingInterpolation.Initialize(this);
        passingInterpolation.Initialize(this);

        Scheduler.AddSubsystems(Intake, Indexer, Shooter, HandleAutoAim, Climber);
        Scheduler.AddProtectedSystem(UpdateJoints);
        Scheduler.AddLateRecursive(UpdatePids);
    }

    private void UpdatePids()
    {
        intakeJoint.UpdatePid(_agitating ? intakeAgitationPid : intakePid);
    }

    private void Climber()
    {
        // TODO: Read ClimbAction and choose the climber target.
    }

    private void Intake()
    {
        // Pressing intake (shift) always puts the intake out and clears the pulled-in latch.
        if (IntakeAction)
        {
            intakeRoller.SetRpm(intakeActiveRPM);
            _intakeDeployed = true;
            _agitating = false;
            _agitateTimer = 0f;
        }
        else
        {
            intakeRoller.SetRpm(intakeInactiveRPM);
        }

        // Stay stowed until intake is pressed for the first time.
        if (!_intakeDeployed)
        {
            intakeRoller.Stop();
            _targetIntakeDist = intakeInValue;
            _agitateTimer = 0f;
            _agitating = false;
            return;
        }

        // Once pulled in by shooting, it stays in until intake is pressed again.
        if (!_agitating)
        {
            // count how long we've been shooting and aimed (not while intaking)
            if (ReadyToShoot() && !IntakeAction)
            {
                _agitateTimer += Time.fixedDeltaTime;
            }
            else
            {
                _agitateTimer = 0f;
            }

            if (_agitateTimer >= agitateDelay)
            {
                _agitating = true;   // latch
            }
        }

        _targetIntakeDist = _agitating ? intakeInValue : intakeOutValue;
    }

    private void Indexer()
    {
        if (ReadyToShoot())
        {
            indexer.SetRpm(indexerActiveRPM);
            shooterIndexer.SetRpm(shooterIndexeRpm);
        }
        else
        {
            indexer.SetRpm(indexerInactiveRPM);
            shooterIndexer.SetRpm(-200);
        }
    }
    
    private void HandleAutoAim()
    {
        if (aim == null) return;

        if (OuttakeAction && InAllianceZone())
        {
            aim.SetDefaultMap(DefaultMap.Hub); // hub shot from your alliance zone
            aim.Aim();
        }
        else if (IsPassShot())
        {
            aim.SetPassMapClosest(); // AutoPass, or shoot key outside your zone
            aim.Aim();
        }
        else
        {
            aim.SetDefaultMap(DefaultMap.Hub); // idle: keep the hub selected
        }
    }

    private void Shooter()
    {
        if (!WantsToShoot())
        {
            shooterRoller.Stop();
            return;
        }
        bool pass = IsPassShot();
        RebuiltInterpolation flywheelInterp = (pass && passingInterpolation != null)
            ? passingInterpolation : hubShootingInterpolation;
        // choose what the tables measure distance to
        if (pass)
        {
            flywheelInterp.SetPassMapClosest();
        }
        else
        {
            flywheelInterp.SetDefaultMap(DefaultMap.Hub);
        }
        float rpm = flywheelInterp.Interpolate();
        shooterRoller.SetRpm(rpm);
    }

    private void SetInterpMap()
    {
        // TODO: Select the hub / pass maps for the flywheel and hood.
    }

    private void UpdateJoints()
    {
        intakeJoint.SetLinearTarget(_targetIntakeDist / 0.0254f).withAxis(JointAxis.X);
    }

    private void WobbleIntake()
    {
        // TODO: Adjust the intake target to clear a jam or agitate fuel.
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
    private bool WantsToShoot()
    {
        return (OuttakeAction || AutoPass || ManualShoot) && _intakeDeployed;
    }
    private bool ReadyToShoot()
    {
        return WantsToShoot() && (ManualShoot || AimedToShoot());
    }
}