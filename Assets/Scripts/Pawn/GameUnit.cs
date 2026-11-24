using System;
using System.Collections.Generic;
using UnityEngine;

public class GameUnit : TickBehaviour, IUnit
{
    #region Properties
    [Header("Unit Core")]
    public UnitAttribute Attribute;
    public UnitAttribute GetAttribute() => Attribute;

    //information
    [Header("Information")]
    public FaceDirection FaceDirection;
    [SerializeField] private Vector2 direction;     //use SetDirection() so it is normalized once instead of every frame
    [SerializeField] private bool active = false;

    [Header("Pathfinding")]
    [SerializeField] private bool allowDiagonal = true;
    [SerializeField] private bool cutCorners = false;
    [SerializeField] private bool avoidWater = false;
    [SerializeField] private bool smoothPath = true;
    [SerializeField] private bool allowPartialPath = true;
    [SerializeField, Range(0f, 1f)] private float terrainCostWeight = 1f;
    [SerializeField, Min(100)] private int maxSearchNodes = 20000;
    [Tooltip("If the destination is blocked, look for the nearest walkable cell within this many cells.")]
    [SerializeField, Min(0)] private int snapRadius = 6;

    [Header("Movement")]
    [SerializeField, Min(0.01f)] private float waypointTolerance = 0.1f;
    [Tooltip("Slow down inside this distance of the final point. 0 = off.")]
    [SerializeField, Min(0f)] private float arrivalSlowRadius = 0.75f;
    [SerializeField, Range(0.05f, 1f)] private float minArrivalSpeed = 0.3f;

    [Header("Repath / Safety")]
    [SerializeField, Min(0f)] private float repathCooldown = 0.4f;
    [SerializeField, Min(0)] private int maxRepathAttempts = 4;
    [SerializeField, Min(0.05f)] private float validateInterval = 0.25f;
    [SerializeField, Min(1)] private int validateLookahead = 4;
    [SerializeField, Min(0.1f)] private float stuckTime = 1f;
    [SerializeField, Min(0f)] private float stuckMinMove = 0.1f;

    //can be accessed by other but need to hide for clean.
    [HideInInspector] public bool canMove = true, isMoving;

    //public properties
    public bool Active => active; //change it through SetUnitActive / TurnUnitOn / TurnUnitOff so ticking stays in sync
    public FaceDirection Facing => FaceDirection; //read-only for others, change it through SetFaceDirection / SetDirection
    public Vector2 Direction => direction;        //normalized move direction (zero when standing still)

    //path state
    public PawnPathState PathState { get; private set; } = PawnPathState.Idle;
    public bool IsFollowingPath => PathState == PawnPathState.Moving;
    public Vector2Int CurrentCell => currentCell;
    public float SpeedMultiplier => speedMultiplier;
    public Vector2 Destination => finalPoint;
    public Vector2 Position => cachedTransform != null ? (Vector2)cachedTransform.position : (Vector2)transform.position;

    public event Action<GameUnit> OnArrived;
    public event Action<GameUnit> OnPathFailed;

    //cache
    private Transform cachedTransform;
    private Vector2 lastFacingDir;
    private Collider2D[] cols;

    //path data
    private readonly List<Vector2Int> cellPath = new List<Vector2Int>(64);
    private readonly List<Vector2> waypoints = new List<Vector2>(64);
    private int wpIndex;
    private Vector2Int destCell;
    private Vector2 finalPoint;
    private float stopDistance;
    private bool pathPartial;

    private Vector2Int currentCell;
    private bool cellInit;
    private float speedMultiplier = 1f;

    private int repathAttempts;
    private float repathTimer, validateTimer, stuckTimer;
    private Vector2 stuckPos;
    #endregion

    #region Initialize Unit
    protected override void Wake()
    {
        cachedTransform = transform;
        cols = GetComponentsInChildren<Collider2D>(true);   // true = include inactive children
        if (Attribute == null) Attribute = new UnitAttribute();
    }

    protected override void On()
    {
        base.On();
        Attribute.RefreshCombat();      // full HP when the unit is enabled
        RegisterColliders();
    }

    protected override void Off()
    {
        base.Off();
        UnregisterColliders();
        ClearPath(PawnPathState.Idle);
    }

    // Collider -> UnitAttribute lookup used by Attack (one dictionary hit on an int key, no GetComponent, no allocation).
    private static readonly Dictionary<int, UnitAttribute> colliderMap = new Dictionary<int, UnitAttribute>(2048);

    /// <summary>Attribute of the unit owning this collider. False for non-units.</summary>
    public static bool TryGetAttribute(Collider2D col, out UnitAttribute attr)
    {
        if (col == null) { attr = null; return false; }
        return colliderMap.TryGetValue(col.GetInstanceID(), out attr);
    }

    // static state survives play-mode entry when Domain Reload is disabled
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetColliderMap() => colliderMap.Clear();

    void RegisterColliders()
    {
        if (cols == null) return;
        for (int i = 0; i < cols.Length; i++)
            if (cols[i] != null) colliderMap[cols[i].GetInstanceID()] = Attribute;
    }

    void UnregisterColliders()
    {
        if (cols == null) return;
        for (int i = 0; i < cols.Length; i++)
            if (cols[i] != null) colliderMap.Remove(cols[i].GetInstanceID());
    }

    public virtual void InitializeUnit(UnitAttribute attribute) //spawner this try to call this to summon, not automatically
    {
        if (attribute == null) return;

        // Own copy per unit; reuse the existing object when pooled (no allocation).
        if (Attribute == null) Attribute = attribute.Clone();
        else Attribute.CopyFrom(attribute);

        direction = Vector2.zero;
        lastFacingDir = Vector2.zero;
        isMoving = false;
        canMove = true;

        ClearPath(PawnPathState.Idle);
        cellInit = false;
        speedMultiplier = 1f;

        Attribute.RefreshCombat();      // full HP on (re)spawn
    }
    #endregion

    #region Given Tick
    protected override void Tick(float deltaTime)
    {
        RefreshCellAndSpeed();

        if (PathState != PawnPathState.Moving)
        {
            MoveUnit(deltaTime); // manual SetDirection movement keeps working
            return;
        }

        FollowPath(deltaTime);
    }
    #endregion

    #region Universal Behaviours
    public void MoveUnit(float deltaTime)
    {
        if (!canMove)
        {
            isMoving = false;
            return;
        }

        Vector2 delta = Calculation.CalculateMovement((float)Attribute.Speed, direction, deltaTime, active);
        isMoving = delta.x != 0f || delta.y != 0f;
        if (!isMoving) return;

        Vector3 p = cachedTransform.position;
        p.x += delta.x;
        p.y += delta.y; //z untouched
        cachedTransform.position = p;

        // Only recompute facing when the direction actually changed.
        if (direction.x != lastFacingDir.x || direction.y != lastFacingDir.y)
        {
            lastFacingDir = direction;
            FaceDirection = ResolveFaceDirection(direction); //always kept up to date (animators / sprites read it)
        }
    }

    public void SetDirection(Vector2 dir)
    {
        float sqr = dir.sqrMagnitude;
        direction = sqr > 1e-6f ? dir / Mathf.Sqrt(sqr) : Vector2.zero;
    }

    public void StopUnit()
    {
        direction = Vector2.zero;
        isMoving = false;
    }

    /// <summary>Face a cardinal direction without moving (spawn pose, idle, cutscene...).</summary>
    public void SetFaceDirection(FaceDirection face)
    {
        FaceDirection = face;

        Vector2 dir;
        switch (face)
        {
            case global::FaceDirection.North: dir = Vector2.up; break;
            case global::FaceDirection.South: dir = Vector2.down; break;
            case global::FaceDirection.East: dir = Vector2.right; break;
            default: dir = Vector2.left; break;
        }
        lastFacingDir = dir;
    }
    #endregion

    #region Pathfinding API
    /// <summary>Walk to a world position. stoppingDistance = stop this far from it (interaction range).</summary>
    public bool MoveTo(Vector2 world, float stoppingDistance = 0f)
    {
        var map = MapManager.Instance;
        if (map == null) return Fail();
        world = map.ClampToMap(world);
        return StartMove(map, map.WorldToCell(world), stoppingDistance, world);
    }

    public bool MoveToCell(Vector2Int cell, float stoppingDistance = 0f)
    {
        var map = MapManager.Instance;
        if (map == null) return Fail();
        cell = map.ClampToMap(cell);
        return StartMove(map, cell, stoppingDistance, map.CellToWorld(cell));
    }

    /// <summary>Stop following the current path (no events).</summary>
    public void CancelMove() => ClearPath(PawnPathState.Idle);

    /// <summary>Teleport to a world position and drop the current path.</summary>
    public void Warp(Vector2 world)
    {
        var map = MapManager.Instance;
        if (map != null) world = map.ClampToMap(world);
        ClearPath(PawnPathState.Idle);
        var p = cachedTransform.position; p.x = world.x; p.y = world.y;
        cachedTransform.position = p;
        cellInit = false;
        RefreshCellAndSpeed();
    }

    /// <summary>Test a route without moving. Fills 'output' with world waypoints (excluding the current position).</summary>
    public bool TryGetPath(Vector2 world, List<Vector2> output)
    {
        output.Clear();
        var map = MapManager.Instance;
        if (map == null) return false;
        var o = BuildOptions();
        var goal = map.ClampToMap(map.WorldToCell(map.ClampToMap(world)));
        if (!GridPathfinder.FindNearestPassable(map, goal, o, snapRadius, out goal)) return false;

        var tmp = new List<Vector2Int>(64);
        if (!GridPathfinder.FindPath(map, map.ClampToMap(map.WorldToCell(Position)), goal, o, tmp)) return false;
        if (smoothPath) GridPathfinder.Smooth(map, tmp, o);
        for (int i = 1; i < tmp.Count; i++) output.Add(map.CellToWorld(tmp[i]));
        return true;
    }

    public bool CanReach(Vector2 world)
    {
        var list = new List<Vector2>(32);
        return TryGetPath(world, list);
    }

    public bool IsNear(Vector2 world, float range) => (world - Position).sqrMagnitude <= range * range;

    public float RemainingDistance
    {
        get
        {
            if (PathState != PawnPathState.Moving || wpIndex >= waypoints.Count) return 0f;
            Vector2 prev = Position;
            float sum = 0f;
            for (int i = wpIndex; i < waypoints.Count; i++)
            {
                sum += Vector2.Distance(prev, waypoints[i]);
                prev = waypoints[i];
            }
            return sum;
        }
    }
    #endregion

    #region Path building
    private PathOptions BuildOptions() => new PathOptions
    {
        allowDiagonal = allowDiagonal,
        cutCorners = cutCorners,
        avoidWater = avoidWater,
        allowPartial = allowPartialPath,
        terrainWeight = terrainCostWeight,
        maxNodes = maxSearchNodes
    };

    private bool StartMove(MapManager map, Vector2Int goal, float stopDist, Vector2 exactPoint)
    {
        var o = BuildOptions();
        Vector2Int requested = goal;
        if (!GridPathfinder.FindNearestPassable(map, goal, o, snapRadius, out goal)) return Fail();

        destCell = goal;
        finalPoint = goal == requested ? exactPoint : map.CellToWorld(goal);
        stopDistance = Mathf.Max(0f, stopDist);
        repathAttempts = 0;
        repathTimer = 0f;

        // already close enough
        if (stopDistance > 0f && (finalPoint - Position).sqrMagnitude <= stopDistance * stopDistance)
        {
            ClearPath(PawnPathState.Idle);
            Arrive();
            return true;
        }
        return BuildPath(map.ClampToMap(map.WorldToCell(Position)));
    }

    private bool BuildPath(Vector2Int start)
    {
        var map = MapManager.Instance;
        var o = BuildOptions();
        if (map == null || !GridPathfinder.FindPath(map, start, destCell, o, cellPath)) return Fail();

        pathPartial = cellPath[cellPath.Count - 1] != destCell;
        if (smoothPath) GridPathfinder.Smooth(map, cellPath, o);

        waypoints.Clear();
        for (int i = 0; i < cellPath.Count; i++) waypoints.Add(map.CellToWorld(cellPath[i]));
        if (!pathPartial) waypoints[waypoints.Count - 1] = finalPoint;

        wpIndex = waypoints.Count > 1 ? 1 : 0; // index 0 is the cell we already stand in
        PathState = PawnPathState.Moving;
        validateTimer = validateInterval;
        stuckTimer = 0f;
        stuckPos = Position;
        return true;
    }

    private bool Repath()
    {
        if (repathTimer > 0f) return false; // wait, don't walk an invalid path
        if (++repathAttempts > maxRepathAttempts) return Fail();
        repathTimer = repathCooldown;
        var map = MapManager.Instance;
        if (map == null) return Fail();
        return BuildPath(map.ClampToMap(map.WorldToCell(Position))) && PathState == PawnPathState.Moving;
    }

    private bool PathStillValid()
    {
        var map = MapManager.Instance;
        if (map == null) return false;
        var o = BuildOptions();
        Vector2Int prev = currentCell;
        int end = Mathf.Min(waypoints.Count, wpIndex + validateLookahead);
        for (int i = wpIndex; i < end; i++)
        {
            Vector2Int c = map.ClampToMap(map.WorldToCell(waypoints[i]));
            if (!GridPathfinder.IsPassable(map, c, o)) return false;
            if (!GridPathfinder.LineClear(map, prev, c, o)) return false;
            prev = c;
        }
        return true;
    }
    #endregion

    #region Path following
    private void FollowPath(float deltaTime)
    {
        if (!canMove) { isMoving = false; return; }

        repathTimer -= deltaTime;
        validateTimer -= deltaTime;

        if (validateTimer <= 0f)
        {
            validateTimer = validateInterval;
            if (!PathStillValid())
            {
                if (!Repath()) { isMoving = false; return; }
            }
        }

        Vector2 pos = Position;

        // stuck detection
        stuckTimer += deltaTime;
        if (stuckTimer >= stuckTime)
        {
            bool stuck = (pos - stuckPos).sqrMagnitude < stuckMinMove * stuckMinMove;
            stuckTimer = 0f;
            stuckPos = pos;
            if (stuck && !Repath()) { isMoving = false; return; }
        }

        if (wpIndex >= waypoints.Count) { FinishPath(); return; }

        // advance over reached intermediate waypoints
        Vector2 target = waypoints[wpIndex];
        Vector2 toT = target - pos;
        float dist = toT.magnitude;
        while (wpIndex < waypoints.Count - 1 && dist <= waypointTolerance)
        {
            wpIndex++;
            repathAttempts = 0; // progress made
            target = waypoints[wpIndex];
            toT = target - pos;
            dist = toT.magnitude;
        }

        bool last = wpIndex == waypoints.Count - 1;
        float stopAt = last ? Mathf.Max(stopDistance, waypointTolerance * 0.5f) : 0f;
        if (last && dist <= stopAt) { FinishPath(); return; }

        Vector2 dir = dist > 1e-5f ? toT / dist : Vector2.zero;

        float scale = 1f;
        if (last && arrivalSlowRadius > 0f)
            scale = Mathf.Clamp((dist - stopDistance) / arrivalSlowRadius, minArrivalSpeed, 1f);

        float speed = (float)Attribute.Speed * speedMultiplier * scale;
        Vector2 delta = Calculation.CalculateMovement(speed, dir, deltaTime, active);

        float maxStep = last ? Mathf.Max(0f, dist - stopDistance) : dist;
        float step = delta.magnitude;
        if (step > maxStep) delta = step > 0f ? delta * (maxStep / step) : Vector2.zero;

        isMoving = delta.x != 0f || delta.y != 0f;
        if (isMoving)
        {
            pos += delta;
            var map = MapManager.Instance;
            if (map != null) pos = map.ClampToMap(pos);
            Vector3 p = cachedTransform.position;
            p.x = pos.x; p.y = pos.y; // z untouched
            cachedTransform.position = p;
        }

        SetDirection(dir); // keeps direction in sync (animators, weapon, hand-over to manual control)
        if (dir.x != 0f || dir.y != 0f) FaceDirection = ResolveFaceDirection(dir);
    }

    private void RefreshCellAndSpeed()
    {
        var map = MapManager.Instance;
        if (map == null || cachedTransform == null) return;
        Vector2Int c = map.ClampToMap(map.WorldToCell(cachedTransform.position));
        if (cellInit && c == currentCell) return;
        currentCell = c;
        cellInit = true;
        speedMultiplier = map.GetSpeedPenalty(c); // changes only when entering a new cell
    }

    private void FinishPath()
    {
        if (pathPartial) { Fail(); return; }
        Arrive();
    }

    private void Arrive()
    {
        ClearPath(PawnPathState.Arrived);
        OnArrived?.Invoke(this);
    }

    private bool Fail()
    {
        ClearPath(PawnPathState.Failed);
        OnPathFailed?.Invoke(this);
        return false;
    }

    private void ClearPath(PawnPathState state)
    {
        PathState = state;
        cellPath.Clear();
        waypoints.Clear();
        wpIndex = 0;
        pathPartial = false;
        StopUnit();
    }
    #endregion

    #region Unit Helpers
    //Active
    protected void SetUnitActive(bool switcher)
    {
        if (active == switcher) return;
        active = switcher;

        // Not enabled yet: On() will read 'active' when the component is enabled.
        if (!isActiveAndEnabled) return;

        if (switcher) TickManager.SubscribeTick(this);
        else
        {
            TickManager.UnsubscribeTick(this);
            isMoving = false;
        }
    }
    public void ToggleUnitActive() => SetUnitActive(!active);
    public void TurnUnitOn() => SetUnitActive(true);
    public void TurnUnitOff() => SetUnitActive(false);

    //Move
    protected void SetUnitMovability(bool switcher)
    {
        canMove = switcher;
        if (!switcher) isMoving = false;
    }
    public void ToggleUnitMovability() => SetUnitMovability(!canMove);
    public void LockUnitMovement() => SetUnitMovability(false);
    public void UnlockUnitMovement() => SetUnitMovability(true);

    //Face
    // Dominant axis wins; a tie goes to horizontal.
    private static FaceDirection ResolveFaceDirection(Vector2 dir)
    {
        if (Mathf.Abs(dir.x) >= Mathf.Abs(dir.y))
            return dir.x >= 0f ? global::FaceDirection.East : global::FaceDirection.West;
        return dir.y >= 0f ? global::FaceDirection.North : global::FaceDirection.South;
    }
    #endregion

    #region Deep Debugger
    [ContextMenu("Try move random")]
    public void TryMoveRandom()
    {
        if (!Application.isPlaying || !active)
        {
            Debug.LogWarning("Try move random: enter Play mode and turn the unit on (Active) first.", this);
            return;
        }
        SetDirection(RNG.GetInsideCircle(RNG.GetFloat(0.5f, 5f)));
    }

    [ContextMenu("Path to random cell")]
    private void DebugPathToRandom()
    {
        var map = MapManager.Instance;
        if (!Application.isPlaying || map == null || !active)
        {
            Debug.LogWarning("Path to random cell: Play mode, generated map and an active unit are required.", this);
            return;
        }
        if (map.TryGetRandomWalkable(out var cell)) MoveToCell(cell);
    }

    protected virtual void OnValidate() => Attribute?.Refresh();

    private void OnDrawGizmosSelected()
    {
        if (waypoints.Count == 0) return;
        Gizmos.color = Color.cyan;
        Vector3 prev = transform.position;
        for (int i = wpIndex; i < waypoints.Count; i++)
        {
            Vector3 w = new Vector3(waypoints[i].x, waypoints[i].y, prev.z);
            Gizmos.DrawLine(prev, w);
            Gizmos.DrawWireSphere(w, 0.12f);
            prev = w;
        }
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(new Vector3(finalPoint.x, finalPoint.y, prev.z), Mathf.Max(0.2f, stopDistance));
    }
    #endregion
}

public enum FaceDirection
{
    North,
    South,
    East,
    West
}