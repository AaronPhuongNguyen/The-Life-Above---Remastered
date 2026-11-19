using UnityEngine;

/// <summary>
/// Owns the weapon pivot (Root) and its SpriteRenderer.
/// - Rotates Root to the pawn's move direction (keeps the last aim while standing still).
/// - Flips Root vertically when aiming left so the gun is never upside down.
/// - Sorting: facing North / West = behind the pawn (min order - 1), South / East = in front (max order + 1).
/// Everything is change-driven: no work is done unless direction or facing actually changed.
/// The gun sprite is pushed in by PawnVisualization (SetSprite).
/// </summary>
public class PawnWeapon : TickBehaviour
{
    [Header("References")]
    public Pawn owner;
    [Tooltip("Pivot that gets rotated. The renderer should be its child (offset from the pivot = hold position).")]
    public Transform Root;
    public SpriteRenderer Renderer;

    [Header("Sorting")]
    [SerializeField, Min(1)] private int sortingStep = 1;

    private Vector2 lastDir;
    private bool hasDir;
    private bool flipped;
    private Vector3 baseScale = Vector3.one;

    private FaceDirection lastFace;
    private bool hasFace;
    private int minOrder, maxOrder;

    private bool visible = true;
    private bool hasAimPoint;
    private Vector2 aimPoint;

    #region Public API
    /// <summary>Set owner, cache sorting range and apply the initial pose. Called by PawnVisualization.Activate.</summary>
    public virtual void Activate(Pawn pawn)
    {
        if (pawn != null) owner = pawn;
        ResolveReferences();
        CacheSorting();
        hasDir = false;
        hasFace = false;
        Apply(true);
    }

    public void SetSprite(Sprite sprite)
    {
        if (Renderer != null) Renderer.sprite = sprite;
    }

    public void Show(bool show)
    {
        visible = show;
        if (Renderer != null) Renderer.enabled = show;
    }

    /// <summary>Aim at a world point instead of the move direction (until ClearAim).</summary>
    public void AimAt(Vector2 worldPoint) { aimPoint = worldPoint; hasAimPoint = true; }
    public void ClearAim() => hasAimPoint = false;

    /// <summary>Re-read the pawn's renderer sorting orders (call if they change at runtime).</summary>
    public void CacheSorting()
    {
        if (Renderer == null) return;

        int min = int.MaxValue, max = int.MinValue;
        var v = owner != null ? owner.visualizer : null;
        if (v != null)
        {
            Collect(v.Hair, ref min, ref max);
            Collect(v.Head, ref min, ref max);
            Collect(v.Body, ref min, ref max);
            if (v.Body != null) Renderer.sortingLayerID = v.Body.sortingLayerID;
        }
        if (min > max) { min = max = Renderer.sortingOrder; }

        minOrder = min;
        maxOrder = max;
        hasFace = false; // force re-apply
    }
    #endregion

    #region Tick
    protected override void Wake()
    {
        base.Wake();
        ResolveReferences();
    }

    // LateTick: runs after the pawn moved this frame, so the weapon never lags one frame behind.
    protected override void LateTick(float deltaTime) => Apply(false);
    #endregion

    #region Internals
    private void ResolveReferences()
    {
        if (owner == null) owner = GetComponentInParent<Pawn>();
        if (Root == null && Renderer != null) Root = Renderer.transform;
        if (Renderer == null && Root != null) Renderer = Root.GetComponentInChildren<SpriteRenderer>(true);
        if (Root != null) baseScale = Root.localScale;
    }

    private void Apply(bool force)
    {
        if (owner == null || Root == null || Renderer == null || !visible) return;

        FaceDirection face = owner.Facing;

        // direction
        Vector2 dir = hasAimPoint ? aimPoint - owner.Position : owner.Direction;
        float sqr = dir.sqrMagnitude;
        if (sqr > 1e-6f)
        {
            dir /= Mathf.Sqrt(sqr);
            if (force || !hasDir || (dir - lastDir).sqrMagnitude > 1e-6f) Rotate(dir);
        }
        else if (!hasDir) Rotate(FaceVector(face)); // never moved yet: point where the pawn looks

        // sorting
        if (force || !hasFace || face != lastFace)
        {
            hasFace = true;
            lastFace = face;
            bool behind = face == FaceDirection.North || face == FaceDirection.West;
            Renderer.sortingOrder = behind ? minOrder - sortingStep : maxOrder + sortingStep;
        }
    }

    private void Rotate(Vector2 dir)
    {
        hasDir = true;
        lastDir = dir;
        Root.localRotation = Quaternion.Euler(Calculation.CalculateRotation(dir, 0f));

        bool flip = dir.x < 0f;
        if (flip != flipped || Root.localScale.y != (flip ? -baseScale.y : baseScale.y))
        {
            flipped = flip;
            Root.localScale = new Vector3(baseScale.x, flip ? -baseScale.y : baseScale.y, baseScale.z);
        }
    }

    private static void Collect(SpriteRenderer r, ref int min, ref int max)
    {
        if (r == null) return;
        int o = r.sortingOrder;
        if (o < min) min = o;
        if (o > max) max = o;
    }

    private static Vector2 FaceVector(FaceDirection face)
    {
        switch (face)
        {
            case FaceDirection.North: return Vector2.up;
            case FaceDirection.South: return Vector2.down;
            case FaceDirection.West: return Vector2.left;
            default: return Vector2.right;
        }
    }
    #endregion
}