using System;
using UnityEngine;

public class Pawn : GameUnit
{
    #region Settings
    [Header("Identity")]
    [SerializeField] private Gender gender = Gender.Male;
    [Tooltip("Pick a random gender every time InitializeUnit is called (spawning).")]
    [SerializeField] private bool randomGenderOnInit = false;

    [Header("Assets")]
    public GameDatabase Database;
    public PawnVisualization visualizer;
    public PawnWeapon weapon;

    [Header("Force Move")]
    [Tooltip("Apply the MapManager terrain speed to force moves (mud, water, floors).")]
    [SerializeField] private bool forceUsesTerrainSpeed = true;
    [SerializeField, Min(0.01f)] private float forceArriveDistance = 0.05f;
    #endregion

    #region State
    /// <summary>Male or Female. Change it with SetGender so listeners (visualization, UI) are notified.</summary>
    public Gender Gender => gender;

    public event Action<Pawn> OnGenderChanged;

    /// <summary>Raised when a force move ends (duration over, target reached, blocked or StopMove).</summary>
    public event Action<Pawn> OnForceMoveEnded;

    public bool IsForceMoving => forceActive;
    /// <summary>True while following a path OR force moving.</summary>
    public bool HasMoveOrder => forceActive || IsFollowingPath;

    private bool forceActive;
    private Vector2 forceDir;
    private bool forceTimed;
    private float forceTimeLeft;
    private bool forceIgnoreBlocking;
    private bool forceHasTarget;
    private Vector2 forceTarget;
    private Vector2Int forceCell;
    private bool forceCellInit;
    private float forceSpeedMul = 1f;
    #endregion

    #region Lifecycle
    public override void InitializeUnit(UnitAttribute attribute)
    {
        base.InitializeUnit(attribute);
        EndForce(false);
        if (randomGenderOnInit) SetGender(RNG.GetInt(0, 2) == 0 ? Gender.Male : Gender.Female);
    }

    /// <summary>Change gender. PawnVisualization picks a matching Male/Female look on its next refresh.</summary>
    public void SetGender(Gender value)
    {
        if (gender == value) return;
        gender = value;
        OnGenderChanged?.Invoke(this);
    }

    protected override void OnValidate()
    {
        base.OnValidate();

        if (!gameObject.TryGetComponent<PawnVisualization>(out visualizer))
            visualizer = gameObject.AddComponent<PawnVisualization>();
        if (weapon == null) weapon = GetComponentInChildren<PawnWeapon>(true);
        visualizer.Activate(this);
    }

    protected override void Off()
    {
        base.Off();
        EndForce(false);
    }
    #endregion

    #region Commands
    /// <summary>
    /// Move with pathfinding (A*, smoothing, repath and stuck handling live in GameUnit).
    /// stoppingDistance = stop this far from the point (interaction range).
    /// </summary>
    public bool CommandMove(Vector2 world, float stoppingDistance = 0f)
    {
        EndForce(false);
        return MoveTo(world, stoppingDistance);
    }

    public bool CommandMove(Vector2Int cell, float stoppingDistance = 0f)
    {
        EndForce(false);
        return MoveToCell(cell, stoppingDistance);
    }

    /// <summary>
    /// Move along a direction with NO pathfinding.
    /// duration &lt;= 0 = until StopMove. ignoreBlocking = walk through blocked cells (still clamped to the map).
    /// </summary>
    public void ForceMove(Vector2 direction, float duration = 0f, bool ignoreBlocking = false)
    {
        if (direction.sqrMagnitude < 1e-6f) { StopMove(); return; }
        BeginForce(direction.normalized, duration, ignoreBlocking, false, default);
    }

    /// <summary>Straight line to a point, no pathfinding. Ends on arrival, or when blocked.</summary>
    public void ForceMoveTo(Vector2 worldTarget, bool ignoreBlocking = false)
    {
        Vector2 d = worldTarget - Position;
        float dist = d.magnitude;
        if (dist <= forceArriveDistance) return;
        BeginForce(d / dist, 0f, ignoreBlocking, true, worldTarget);
    }

    /// <summary>Cancel path and force move, and stop the unit.</summary>
    public void StopMove()
    {
        bool wasForce = forceActive;
        EndForce(false);
        CancelMove();   // clears path + StopUnit
        StopUnit();
        if (wasForce) OnForceMoveEnded?.Invoke(this);
    }
    #endregion

    #region Helpers
    public float DistanceTo(Vector2 world) => Vector2.Distance(Position, world);
    public bool IsAt(Vector2 world, float tolerance = 0.1f) => IsNear(world, tolerance);

    /// <summary>Current effective speed (attribute speed * terrain multiplier).</summary>
    public float CurrentSpeed => (float)Attribute.Speed * (forceActive ? forceSpeedMul : SpeedMultiplier);

    /// <summary>Turn to face a world point without moving.</summary>
    public void LookAt(Vector2 world)
    {
        Vector2 d = world - Position;
        if (d.sqrMagnitude > 1e-6f) FaceDirection = Resolve(d);
    }

    private void BeginForce(Vector2 dir, float duration, bool ignoreBlocking, bool hasTarget, Vector2 target)
    {
        CancelMove();                 // drop path state
        forceActive = true;
        forceDir = dir;
        forceTimed = duration > 0f;
        forceTimeLeft = duration;
        forceIgnoreBlocking = ignoreBlocking;
        forceHasTarget = hasTarget;
        forceTarget = target;
        forceCellInit = false;
        forceSpeedMul = 1f;

        SetDirection(dir);            // weapon / visuals read Direction
        FaceDirection = Resolve(dir); // and Facing
    }

    private void EndForce(bool raiseEvent)
    {
        if (!forceActive) return;
        forceActive = false;
        forceHasTarget = false;
        forceTimed = false;
        StopUnit();
        if (raiseEvent) OnForceMoveEnded?.Invoke(this);
    }

    private static FaceDirection Resolve(Vector2 d)
    {
        if (Mathf.Abs(d.x) >= Mathf.Abs(d.y)) return d.x >= 0f ? FaceDirection.East : FaceDirection.West;
        return d.y >= 0f ? FaceDirection.North : FaceDirection.South;
    }
    #endregion

    #region Tick
    protected override void Tick(float deltaTime)
    {
        // A path command issued directly on GameUnit (MoveTo) overrides a running force move.
        if (forceActive && IsFollowingPath) EndForce(false);

        if (!forceActive) { base.Tick(deltaTime); return; }
        TickForce(deltaTime);
    }

    private void TickForce(float dt)
    {
        if (!canMove) { isMoving = false; return; }

        var map = MapManager.Instance;
        Vector2 pos = Position;

        // terrain speed: only re-read when entering a new cell
        if (map != null && forceUsesTerrainSpeed)
        {
            Vector2Int cell = map.WorldToCell(pos);
            if (!forceCellInit || cell != forceCell)
            {
                forceCell = cell;
                forceCellInit = true;
                forceSpeedMul = map.GetSpeedPenalty(cell);
            }
        }
        else forceSpeedMul = 1f;

        Vector2 delta = Calculation.CalculateMovement((float)Attribute.Speed * forceSpeedMul, forceDir, dt, Active);

        bool reached = false;
        if (forceHasTarget)
        {
            Vector2 rem = forceTarget - pos;
            float remDist = rem.magnitude;
            if (delta.magnitude >= remDist - forceArriveDistance) { delta = rem; reached = true; }
        }

        Vector2 next = pos + delta;

        if (map != null)
        {
            if (!forceIgnoreBlocking && !map.IsWalkable(map.WorldToCell(next)))
            {
                // slide along one axis, otherwise stop
                Vector2 sx = new Vector2(next.x, pos.y);
                Vector2 sy = new Vector2(pos.x, next.y);
                if (delta.x != 0f && map.IsWalkable(map.WorldToCell(sx))) { next = sx; reached = false; }
                else if (delta.y != 0f && map.IsWalkable(map.WorldToCell(sy))) { next = sy; reached = false; }
                else { EndForce(true); return; }
            }
            next = map.ClampToMap(next);
        }

        isMoving = delta.x != 0f || delta.y != 0f;
        if (isMoving)
        {
            Vector3 p = transform.position;
            p.x = next.x; p.y = next.y; // z untouched
            transform.position = p;
        }

        if (reached) { EndForce(true); return; }

        if (forceTimed)
        {
            forceTimeLeft -= dt;
            if (forceTimeLeft <= 0f) EndForce(true);
        }
    }
    #endregion
}