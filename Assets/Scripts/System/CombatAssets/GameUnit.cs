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
    [SerializeField] private bool active = false;

    //settings
    [Header("Settings")]
    [SerializeField] private bool faceMovement = true;
    [SerializeField] private float rotationOffset = 90f;

    //can be accessed by other but need to hide for clean.
    [HideInInspector] public bool canMove = true, isMoving;
    [HideInInspector] public Vector2 direction;     //use SetDirection() so it is normalized once instead of every frame

    //public properties 
    public bool Active => active; //change it through SetUnitActive / TurnUnitOn / TurnUnitOff so ticking stays in sync
    public FaceDirection Facing => FaceDirection; //read-only for others, change it through SetFaceDirection / SetDirection

    //cache
    private Transform cachedTransform;
    private Vector2 lastFacingDir;
    #endregion

    #region Initialize Unit
    protected override void Wake()
    {
        cachedTransform = transform; //avoid the extern transform lookup every frame
        if (Attribute == null) Attribute = new UnitAttribute();
    }

    protected override void On()
    {
        // Inactive units are not subscribed at all, so they cost nothing in TickManager.
        if (active) base.On();
    }
    protected override void Off()
    {
        base.Off();
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
    }
    #endregion

    #region Given Tick
    protected override void Tick(float deltaTime) => MoveUnit(deltaTime);
    #endregion

    #region Universal Behaviours
    public void MoveUnit(float deltaTime)
    {
        if (!canMove)
        {
            isMoving = false;
            return;
        }

        Vector2 delta = Calculation.CalculateMovement((float)Attribute.Speed, direction, deltaTime);
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

            if (faceMovement) //rotation is optional, FaceDirection is not
                cachedTransform.rotation = Quaternion.Euler(Calculation.CalculateRotation(direction, rotationOffset));
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

        if (faceMovement && cachedTransform != null)
            cachedTransform.rotation = Quaternion.Euler(Calculation.CalculateRotation(dir, rotationOffset));
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

    private void OnValidate() => Attribute?.Refresh();
    #endregion
}

public enum FaceDirection
{
    North,
    South,
    East,
    West
}