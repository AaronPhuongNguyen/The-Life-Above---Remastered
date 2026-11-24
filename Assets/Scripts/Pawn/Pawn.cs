using System;
using UnityEngine;

/// <summary>Everything a save file needs for one pawn. Only ids and ammo, never references.</summary>
[Serializable]
public class PawnData
{
    public Gender gender = Gender.Male;
    [Tooltip("-1 = pick a random one that fits the gender.")]
    public int hairId = -1, headId = -1, bodyId = -1;
    public int weaponId = GameDatabase.FistId;
    [Tooltip("-1 = full magazine.")]
    public int ammo = -1;

    public int GetLook(LookSlot slot) => slot == LookSlot.Hair ? hairId : slot == LookSlot.Head ? headId : bodyId;

    public void SetLook(LookSlot slot, int id)
    {
        if (slot == LookSlot.Hair) hairId = id;
        else if (slot == LookSlot.Head) headId = id;
        else bodyId = id;
    }

    public PawnData Clone() => (PawnData)MemberwiseClone();
}

/// <summary>
/// Template for every unit (player, AI, summons). The single owner of unit data:
/// PawnData (ids into GameDatabase), target layers and derived values.
/// PawnVisualization and PawnWeapon only read from here.
/// </summary>
[RequireComponent(typeof(PawnVisualization))]
public class Pawn : GameUnit
{
    #region Settings
    [Header("Data (ids into GameDatabase, this is what a save stores)")]
    [SerializeField] private PawnData data = new PawnData();
    [Tooltip("Pick a random gender every time InitializeUnit is called (spawning).")]
    [SerializeField] private bool randomGenderOnInit = false;

    [Header("Combat")]
    [Tooltip("Layers this pawn's weapon can damage. Put each team on its own layer.")]
    public LayerMask targetMask = ~0;

    [Header("Components")]
    public PawnVisualization visualizer;
    public PawnWeapon weapon;

    [Header("Movement")]
    [Tooltip("Speed lost per weight unit carried (0.02 = 2% per kg).")]
    [SerializeField, Min(0f)] private float weightSlowPerKg = 0.02f;
    [Tooltip("Apply the MapManager terrain speed to force moves (mud, water, floors).")]
    [SerializeField] private bool forceUsesTerrainSpeed = true;
    [SerializeField, Min(0.01f)] private float forceArriveDistance = 0.05f;
    #endregion

    #region Data
    public GameDatabase Database => GameDatabase.Main;

    public Gender Gender => data.gender;
    public int HairId => data.hairId;
    public int HeadId => data.headId;
    public int BodyId => data.bodyId;
    public int WeaponId => data.weaponId;
    public int Ammo => data.ammo;
    public LayerMask TargetMask => targetMask;

    /// <summary>Equipped weapon definition (never null).</summary>
    public WeaponDef Equipped => equipped ?? (equipped = Database.GetWeapon(data.weaponId));
    /// <summary>Speed multiplier from the carried weight (1 = no slowdown).</summary>
    public float WeightFactor => weightFactor;

    /// <summary>Raised whenever gender, a look or the weapon changed (visuals and weapon listen to it).</summary>
    public event Action<Pawn> OnDataChanged;

    private WeaponDef equipped;
    private float weightFactor = 1f;
    #endregion

    #region Data API
    /// <summary>Copy of the save data.</summary>
    public PawnData ExportData() => data.Clone();

    /// <summary>Load from a save. Invalid ids are repaired (random look, Fist).</summary>
    public void ImportData(PawnData saved)
    {
        if (saved == null) return;
        data = saved.Clone();
        Rebuild();
    }

    public void SetGender(Gender value)
    {
        if (data.gender == value) return;
        data.gender = value;
        Rebuild(); // looks that do not fit the new gender are rerolled
    }

    /// <summary>Set one look by id. Use -1 to reroll it.</summary>
    public void SetLook(LookSlot slot, int id)
    {
        data.SetLook(slot, id);
        Rebuild();
    }

    public void SetLook(int hairId, int headId, int bodyId)
    {
        data.hairId = hairId; data.headId = headId; data.bodyId = bodyId;
        Rebuild();
    }

    public void RandomizeLook() => SetLook(-1, -1, -1);

    /// <summary>Equip by id and fill the magazine.</summary>
    public void EquipWeapon(int weaponId)
    {
        data.weaponId = weaponId;
        data.ammo = -1;
        Rebuild();
    }

    /// <summary>Change ammo without notifying listeners (called on every shot).</summary>
    public void SetAmmo(int value) => data.ammo = Mathf.Clamp(value, 0, Equipped.magazine);

    /// <summary>Make the data valid, refresh derived values, notify listeners.</summary>
    private void Rebuild()
    {
        GameDatabase db = Database;

        data.hairId = db.ValidLookId(LookSlot.Hair, data.hairId, data.gender);
        data.headId = db.ValidLookId(LookSlot.Head, data.headId, data.gender);
        data.bodyId = db.ValidLookId(LookSlot.Body, data.bodyId, data.gender);

        equipped = db.GetWeapon(data.weaponId);
        data.weaponId = equipped.id;
        data.ammo = data.ammo < 0 ? equipped.magazine : Mathf.Min(data.ammo, equipped.magazine);
        weightFactor = Mathf.Max(0.3f, 1f / (1f + equipped.weight * weightSlowPerKg));

        OnDataChanged?.Invoke(this);
    }
    #endregion

    #region Lifecycle
    protected override void Wake()
    {
        base.Wake();
        ResolveReferences();
    }

    protected override void Init()
    {
        base.Init();
        Rebuild();                                     // valid data first...
        if (visualizer != null) visualizer.Activate(this); // ...then bind visuals and weapon
    }

    protected override void Off()
    {
        base.Off();
        EndForce(false);
    }

    public override void InitializeUnit(UnitAttribute attribute)
    {
        base.InitializeUnit(attribute);
        EndForce(false);
        if (randomGenderOnInit) data.gender = RNG.GetInt(0, 2) == 0 ? Gender.Male : Gender.Female;
        data.ammo = -1; // full magazine on (re)spawn
        Rebuild();
    }

    protected override void OnValidate()
    {
        base.OnValidate();
        ResolveReferences();
    }

    private void ResolveReferences()
    {
        if (visualizer == null) visualizer = GetComponent<PawnVisualization>();
        if (weapon == null) weapon = GetComponentInChildren<PawnWeapon>(true);
    }
    #endregion

    #region Movement Commands
    /// <summary>Move with pathfinding (A*, smoothing, repath and stuck handling live in GameUnit).</summary>
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
    /// Move along a direction with NO pathfinding. duration &lt;= 0 = until StopMove.
    /// ignoreBlocking = walk through blocked cells (still clamped to the map).
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
        CancelMove();
        StopUnit();
        if (wasForce) OnForceMoveEnded?.Invoke(this);
    }

    public float DistanceTo(Vector2 world) => Vector2.Distance(Position, world);
    public bool IsAt(Vector2 world, float tolerance = 0.1f) => IsNear(world, tolerance);

    /// <summary>Current effective speed (attribute * terrain * weight).</summary>
    public float CurrentSpeed => (float)Attribute.Speed * (forceActive ? forceSpeedMul : SpeedMultiplier) * weightFactor;

    /// <summary>Turn to face a world point without moving.</summary>
    public void LookAt(Vector2 world)
    {
        Vector2 d = world - Position;
        if (d.sqrMagnitude > 1e-6f) FaceDirection = Resolve(d);
    }

    /// <summary>Turn the body to the weapon aim direction (even while moving).</summary>
    public void FaceAim()
    {
        if (weapon != null) FaceDirection = Resolve(weapon.AimDirection);
    }

    /// <summary>Turn the body back to the move direction (does nothing when standing still).</summary>
    public void FaceMoveDirection()
    {
        Vector2 d = Direction;
        if (d.sqrMagnitude > 1e-6f) FaceDirection = Resolve(d);
    }

    private static FaceDirection Resolve(Vector2 d)
    {
        if (Mathf.Abs(d.x) >= Mathf.Abs(d.y)) return d.x >= 0f ? FaceDirection.East : FaceDirection.West;
        return d.y >= 0f ? FaceDirection.North : FaceDirection.South;
    }
    #endregion

    #region Force Move (internals)
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

    protected override float SpeedModifier => weightFactor;

    private void BeginForce(Vector2 dir, float duration, bool ignoreBlocking, bool hasTarget, Vector2 target)
    {
        CancelMove();
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

        Vector2 delta = Calculation.CalculateMovement((float)Attribute.Speed * forceSpeedMul * weightFactor, forceDir, dt, Active);

        bool reached = false;
        if (forceHasTarget)
        {
            Vector2 rem = forceTarget - pos;
            if (delta.magnitude >= rem.magnitude - forceArriveDistance) { delta = rem; reached = true; }
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

    #region Combat (any unit: player, AI, summons)
    public bool HasWeapon => weapon != null;
    public bool IsAttacking => weapon != null && weapon.IsAttacking;
    public bool IsReloading => weapon != null && weapon.IsReloading;
    public bool CanAttack => weapon != null && weapon.CanAttack;

    public bool TryAttack(in AttackParams p = default) => weapon != null && weapon.TryAttack(p);
    public bool TryAttackDir(Vector2 dir, in AttackParams p = default) => weapon != null && weapon.TryAttackDir(dir, p);
    public bool TryAttackAt(Vector2 world, in AttackParams p = default) => weapon != null && weapon.TryAttackAt(world, p);
    public bool TryAttackUnit(GameUnit target, in AttackParams p = default) => target != null && TryAttackAt(target.Position, p);
    public bool Reload() => weapon != null && weapon.Reload();

    public void AimAt(Vector2 world) { if (weapon != null) weapon.AimAt(world); }
    public void ClearAim() { if (weapon != null) weapon.ClearAim(); }
    #endregion
}