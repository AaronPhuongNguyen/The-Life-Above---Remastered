using UnityEngine;

public class PlayerManager : TickBehaviour
{
    #region Properties
    public Pawn controlling; // which pawn the player is controlling

    [Header("Input")]
    [Tooltip("Move input shorter than this is treated as 'no input'.")]
    [SerializeField, Range(0f, 0.5f)] private float moveDeadzone = 0.1f;

    [Header("Facing")]
    [Tooltip("While standing still, turn the pawn's sprite to face the aim point.")]
    [SerializeField] private bool faceAimWhenIdle = true;
    [Tooltip("While attacking, the pawn always faces the aim direction, even while moving.")]
    [SerializeField] private bool faceAimWhileAttacking = true;
    #endregion

    #region Cache
    private Vector2 moveDir;
    private Vector2 appliedDir;
    private bool movingByInput;

    private Vector2 aimDir;
    private bool aimByDir;
    private const float AimDistance = 10f;
    private Vector2 aimPoint;
    private bool aimDirty;
    private bool hasAim;

    private bool attackRequested;
    private bool reloadRequested;
    private bool wasAttacking;
    #endregion

    #region Events
    protected override void On()
    {
        base.On();
        EventBus.OnMoved += HandleMove;
        EventBus.OnRotated += HandleRotate;
        EventBus.OnAimDirection += HandleAimDirection;
        EventBus.OnAttack += HandleAttack;
        EventBus.OnReload += HandleReload;
    }

    protected override void Off()
    {
        EventBus.OnMoved -= HandleMove;
        EventBus.OnRotated -= HandleRotate;
        EventBus.OnAimDirection -= HandleAimDirection;
        EventBus.OnAttack -= HandleAttack;
        EventBus.OnReload -= HandleReload;

        ReleaseMovement();
        base.Off();
    }

    // Handlers only store the value. The pawn is touched once per frame in Tick.
    private void HandleMove(Vector2 v) =>
        moveDir = v.sqrMagnitude > moveDeadzone * moveDeadzone ? v.normalized : Vector2.zero;

    private void HandleAimDirection(Vector2 dir)
    {
        aimDir = dir;
        aimByDir = true;
        aimDirty = true;
        hasAim = true;
    }

    private void HandleRotate(Vector2 worldPoint)
    {
        aimPoint = worldPoint;
        aimByDir = false;   // mouse takes over
        aimDirty = true;
        hasAim = true;
    }

    private void HandleAttack() => attackRequested = true;
    private void HandleReload() => reloadRequested = true;
    #endregion

    #region Control
    /// <summary>Take control of a pawn. The previous pawn is stopped and its aim released.</summary>
    public void Possess(Pawn pawn)
    {
        if (controlling == pawn) return;

        ReleaseMovement();
        if (controlling != null) controlling.ClearAim();

        controlling = pawn;
        movingByInput = false;
        attackRequested = false;
        reloadRequested = false;
        wasAttacking = false;
        aimDirty = true;
    }

    public void Unpossess() => Possess(null);
    #endregion

    #region Tick
    protected override void Tick(float deltaTime)
    {
        if (controlling == null) return;

        ApplyMovement();
        ApplyAim();
        ApplyActions();
        ApplyFacing();   // last, so it wins over the facing set by ForceMove
    }

    private void ApplyMovement()
    {
        if (moveDir == Vector2.zero) { ReleaseMovement(); return; }

        // Only talk to the pawn when something changed (direction, or the force move ended / was blocked).
        bool changed = !movingByInput
                       || !controlling.IsForceMoving
                       || (moveDir - appliedDir).sqrMagnitude > 1e-4f;
        if (!changed) return;

        controlling.ForceMove(moveDir);
        appliedDir = moveDir;
        movingByInput = true;
    }

    private void ApplyAim()
    {
        // direction aim must follow the pawn while it walks, so it is re-applied every frame
        if (!aimDirty && !aimByDir) return;
        aimDirty = false;
        if (!hasAim) return;

        controlling.AimAt(aimByDir ? controlling.Position + aimDir * AimDistance : aimPoint);
    }

    private void ApplyActions()
    {
        if (reloadRequested) { reloadRequested = false; controlling.Reload(); }
        if (attackRequested) { attackRequested = false; controlling.TryAttack(); }
    }

    private void ApplyFacing()
    {
        // attacking: always face the aim, walking or not
        if (faceAimWhileAttacking && controlling.IsAttacking)
        {
            wasAttacking = true;
            controlling.FaceAim();
            return;
        }

        // attack just ended: go back to facing the walking direction
        if (wasAttacking)
        {
            wasAttacking = false;
            controlling.FaceMoveDirection();
        }

        // standing still: face the aim
        if (faceAimWhenIdle && hasAim && !controlling.IsForceMoving) controlling.FaceAim();
    }

    /// <summary>Stop the pawn, but only if the current movement came from player input.</summary>
    private void ReleaseMovement()
    {
        if (!movingByInput) return;
        movingByInput = false;
        appliedDir = Vector2.zero;
        if (controlling != null) controlling.StopMove();
    }
    #endregion

    #region Helpers
    public bool HasPawn => controlling != null;

    /// <summary>Click-to-move for the controlled pawn (pathfinding). Cancels input movement.</summary>
    public bool CommandMoveTo(Vector2 world, float stoppingDistance = 0f)
    {
        if (controlling == null) return false;
        movingByInput = false;
        return controlling.CommandMove(world, stoppingDistance);
    }
    #endregion
}