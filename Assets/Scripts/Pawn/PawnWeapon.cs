using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Optional per-call overrides (special attacks, skills). null = use the weapon's own values.</summary>
public struct AttackParams
{
    public float? damage;
    public float? range;
    public int? maxTargets;
    public Vector2? direction;          // default: weapon aim direction
    public LayerMask? mask;             // default: Pawn.TargetMask
    public List<DamageResult> results;  // optional caller-owned list (no allocation)
}

/// <summary>Fully resolved attack. Passed to Execute so special weapons can change how it is performed.</summary>
public struct AttackData
{
    public WeaponDef weapon;
    public UnitAttribute attacker;
    public Vector2 origin, dir;
    public float damage, range, armourPen, width, falloff, halfAngle;
    public int maxTargets;
    public LayerMask mask;
    public List<DamageResult> results;
}

/// <summary>
/// Weapon pivot, sprite, aiming and attacks. Owns no persistent data: the weapon, ammo and target layers
/// are read from the Pawn.
/// - Rotates Root to the aim point (or move direction), flips it when aiming left.
/// - Sorting: facing North / West = behind the pawn, South / East = in front.
/// - Melee: cone swing. Ranged: hitscan with accuracy + stability spread, magazine and reload.
/// </summary>
public class PawnWeapon : TickBehaviour
{
    #region Settings
    [Header("References")]
    public Pawn owner;
    [Tooltip("Pivot that gets rotated. The renderer should be its child (offset from the pivot = hold position).")]
    public Transform Root;
    public SpriteRenderer Renderer;
    [Tooltip("Ranged shots start here. Empty = weapon Root.")]
    [SerializeField] private Transform muzzle;

    [Header("Sorting")]
    [SerializeField, Min(1)] private int sortingStep = 9;

    [Header("Attack")]
    [SerializeField] private bool attackEnabled = true;
    [Tooltip("IsAttacking stays true this long after the last attack request (also while holding the button on cooldown).")]
    [SerializeField, Min(0.05f)] private float attackStateTime = 0.3f;

    [Header("Ballistics (degrees)")]
    [Tooltip("Half-angle spread of a perfectly rested weapon with Accuracy 0.")]
    [SerializeField, Min(0f)] private float maxBaseSpread = 12f;
    [Tooltip("Half-angle spread with Accuracy near 1. Never reaches 0: no weapon is perfectly precise.")]
    [SerializeField, Min(0.01f)] private float minSpread = 0.2f;
    [Tooltip("Spread added per shot, scaled by (1 - Stability).")]
    [SerializeField, Min(0f)] private float bloomPerShot = 3f;
    [SerializeField, Min(0f)] private float maxBloom = 10f;
    [Tooltip("Degrees per second the bloom shrinks, scaled by Stability.")]
    [SerializeField, Min(0f)] private float bloomRecover = 6f;

    private const float StatMin = 0.05f, StatMax = 0.99f; // accuracy / stability are never 0 and never absolute
    #endregion

    #region State
    // pose
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

    // attack
    private bool isAttacking;
    private float attackingUntil;
    private float nextShotTime;
    private float bloom;
    private int cachedWeaponId = -1;

    // reload
    private bool reloading;
    private float reloadStart, reloadEnd;

    public bool IsAttacking => isAttacking;
    public bool IsReloading => reloading;
    public bool IsReady => !reloading && Time.time >= nextShotTime;
    public float CooldownLeft => Mathf.Max(0f, nextShotTime - Time.time);
    public float ReloadProgress => reloading ? Mathf.Clamp01((Time.time - reloadStart) / Mathf.Max(0.0001f, reloadEnd - reloadStart)) : 0f;
    public bool CanAttack => attackEnabled && IsReady && owner != null && owner.Attribute != null && owner.Attribute.IsAlive
                             && (!owner.Equipped.UsesAmmo || owner.Ammo > 0);
    public bool AttackEnabled { get => attackEnabled; set { attackEnabled = value; if (!value) ClearAttackState(); } }
    public int LastHits { get; private set; }

    /// <summary>Current half-angle spread in degrees (use it for a crosshair).</summary>
    public float CurrentSpread => owner == null ? 0f : SpreadOf(owner.Equipped);

    /// <summary>Stance changed (true = started attacking, false = stopped).</summary>
    public event Action<bool> OnAttackingChanged;
    /// <summary>After each executed attack: weapon and number of units hit. Hook VFX / sound / animation here.</summary>
    public event Action<WeaponDef, int> OnAttackPerformed;
    /// <summary>true = reload started, false = reload finished or cancelled.</summary>
    public event Action<bool> OnReloadingChanged;
    #endregion

    #region Public API
    /// <summary>Set owner, cache sorting range and apply the initial pose. Called by PawnVisualization.Activate.</summary>
    public virtual void Activate(Pawn pawn)
    {
        Unbind();
        if (pawn != null) owner = pawn;
        ResolveReferences();
        Bind();

        cachedWeaponId = owner != null ? owner.WeaponId : -1;
        ResetCombatState();
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
    public bool HasAim => hasAimPoint;

    /// <summary>Where the weapon points right now (normalized). Aim point, else move direction, else last aim, else facing.</summary>
    public Vector2 AimDirection
    {
        get
        {
            if (owner == null) return Vector2.right;
            Vector2 d = hasAimPoint ? aimPoint - owner.Position : owner.Direction;
            float sqr = d.sqrMagnitude;
            if (sqr > 1e-6f) return d / Mathf.Sqrt(sqr);
            return hasDir ? lastDir : FaceVector(owner.Facing);
        }
    }

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

    #region Attack API
    /// <summary>Attack along the aim direction. Melee swings, ranged fires, depending on the equipped weapon.</summary>
    public bool TryAttack(in AttackParams p = default) => Perform(null, p);

    public bool TryAttackDir(Vector2 dir, in AttackParams p = default) => Perform(dir, p);

    /// <summary>Attack at a world point (also aims the weapon there). Handy for AI.</summary>
    public bool TryAttackAt(Vector2 worldPoint, in AttackParams p = default)
    {
        AimAt(worldPoint);
        return Perform(null, p);
    }

    /// <summary>Start reloading. False when the weapon has no magazine, is full or is already reloading.</summary>
    public bool Reload()
    {
        if (owner == null || reloading) return false;
        WeaponDef w = owner.Equipped;
        if (!w.UsesAmmo || owner.Ammo >= w.magazine) return false;

        float duration = ResolveReloadTime(w, owner.Attribute);
        reloadStart = Time.time;
        reloadEnd = reloadStart + duration;
        reloading = true;
        OnReloadingChanged?.Invoke(true);
        return true;
    }

    /// <summary>Drop the stance and any reload (stun, death...).</summary>
    public void CancelAttack(bool resetCooldown = false)
    {
        ClearAttackState();
        CancelReload();
        if (resetCooldown) nextShotTime = 0f;
    }
    #endregion

    #region Attack Core
    private bool Perform(Vector2? dirOverride, in AttackParams p)
    {
        if (!attackEnabled || owner == null) return false;
        UnitAttribute attr = owner.Attribute;
        if (attr == null || !attr.IsAlive) return false;

        // asking to attack keeps the stance alive, even while on cooldown (holding the button)
        attackingUntil = Time.time + attackStateTime;
        if (!isAttacking) { isAttacking = true; OnAttackingChanged?.Invoke(true); }

        if (reloading || Time.time < nextShotTime) return false;

        WeaponDef w = owner.Equipped;
        if (w.UsesAmmo && owner.Ammo <= 0) { Reload(); return false; }

        float rate = w.fireRate * attr.AttackSpeed;
        if (rate <= 0f) return false;

        AttackData d = Build(w, attr, dirOverride, p);
        if (d.damage <= 0f || d.range <= 0f) return false;

        nextShotTime = Time.time + 1f / rate;
        if (w.UsesAmmo) owner.SetAmmo(owner.Ammo - 1);
        if (w.IsRanged) AddBloom(w);

        LastHits = Execute(d);
        OnAttackPerformed?.Invoke(w, LastHits);

        if (w.UsesAmmo && owner.Ammo <= 0) Reload(); // auto reload on empty
        return true;
    }

    private AttackData Build(WeaponDef w, UnitAttribute attr, Vector2? dirOverride, in AttackParams p)
    {
        Vector2 dir = dirOverride ?? p.direction ?? AimDirection;
        float sqr = dir.sqrMagnitude;
        dir = sqr > 1e-6f ? dir / Mathf.Sqrt(sqr) : AimDirection;
        if (w.IsRanged) dir = ApplySpread(dir, SpreadOf(w));

        return new AttackData
        {
            weapon = w,
            attacker = attr,
            origin = w.IsRanged ? MuzzlePosition : owner.Position,
            dir = dir,
            damage = p.damage ?? ResolveDamage(w, attr),
            range = p.range ?? ResolveRange(w, attr),
            armourPen = w.armourPenetration,
            width = w.width,
            falloff = w.pierceFalloff,
            halfAngle = w.arc,
            maxTargets = p.maxTargets ?? w.MaxTargets,
            mask = p.mask ?? owner.TargetMask,
            results = p.results
        };
    }

    private Vector2 MuzzlePosition =>
        muzzle != null ? (Vector2)muzzle.position :
        Root != null ? (Vector2)Root.position : owner.Position;

    // ---- extension points: override in a subclass for special weapons / attacks ----
    protected virtual float ResolveDamage(WeaponDef w, UnitAttribute a) => w.baseDamage + a.ATK * w.unitAtkScale;
    protected virtual float ResolveRange(WeaponDef w, UnitAttribute a) => w.range * a.AttackRange;
    protected virtual float ResolveReloadTime(WeaponDef w, UnitAttribute a) => w.reloadTime / Mathf.Max(0.1f, w.manipulation);

    /// <summary>Does the actual hit. Returns the number of units damaged. Override for projectiles, beams, AoE...</summary>
    protected virtual int Execute(in AttackData d)
    {
        Attack.ArmourPenetration = d.armourPen;
        try
        {
            if (d.weapon.kind == WeaponKind.Melee)
                return Attack.Cone(d.origin, d.dir, d.range, d.halfAngle, d.damage, d.attacker, d.mask,
                                   1f, d.maxTargets, d.results);

            return Attack.Pierce(d.origin, d.dir, d.range, d.damage, d.attacker, d.mask,
                                 d.maxTargets, d.falloff, d.width, d.results);
        }
        finally { Attack.ArmourPenetration = 0f; }
    }
    #endregion

    #region Ballistics
    private float SpreadOf(WeaponDef w)
    {
        if (!w.IsRanged) return 0f;
        float acc = Mathf.Clamp(w.accuracy, StatMin, StatMax);
        return Mathf.Lerp(maxBaseSpread, minSpread, acc) + bloom;
    }

    private void AddBloom(WeaponDef w)
    {
        float stability = Mathf.Clamp(w.stability, StatMin, StatMax);
        bloom = Mathf.Min(bloom + bloomPerShot * (1f - stability), maxBloom);
    }

    private static Vector2 ApplySpread(Vector2 dir, float halfAngleDeg)
    {
        float a = RNG.GetFloat(-halfAngleDeg, halfAngleDeg) * Mathf.Deg2Rad;
        float cos = Mathf.Cos(a), sin = Mathf.Sin(a);
        return new Vector2(dir.x * cos - dir.y * sin, dir.x * sin + dir.y * cos);
    }
    #endregion

    #region State helpers
    private void FinishReload()
    {
        reloading = false;
        if (owner != null) owner.SetAmmo(owner.Equipped.magazine);
        OnReloadingChanged?.Invoke(false);
    }

    private void CancelReload()
    {
        if (!reloading) return;
        reloading = false;
        OnReloadingChanged?.Invoke(false);
    }

    private void ResetCombatState()
    {
        CancelReload();
        ClearAttackState();
        bloom = 0f;
        nextShotTime = 0f;
    }

    private void UpdateAttackState()
    {
        if (isAttacking && Time.time >= attackingUntil) ClearAttackState();
    }

    private void ClearAttackState()
    {
        if (!isAttacking) return;
        isAttacking = false;
        OnAttackingChanged?.Invoke(false);
    }

    // Weapon swapped: drop reload, bloom and cooldown. Gender / look changes do not touch the combat state.
    private void HandleDataChanged(Pawn p)
    {
        if (p.WeaponId == cachedWeaponId) return;
        cachedWeaponId = p.WeaponId;
        ResetCombatState();
    }

    private void Bind() { if (owner != null) owner.OnDataChanged += HandleDataChanged; }
    private void Unbind() { if (owner != null) owner.OnDataChanged -= HandleDataChanged; }
    #endregion

    #region Tick
    protected override void Wake()
    {
        base.Wake();
        ResolveReferences();
    }

    protected override void On()
    {
        base.On();
        Unbind();
        Bind();
    }

    protected override void Off()
    {
        base.Off();
        Unbind();
        ResetCombatState();
    }

    // LateTick: runs after the pawn moved this frame, so the weapon never lags one frame behind.
    protected override void LateTick(float deltaTime)
    {
        Apply(false);
        UpdateAttackState();

        if (reloading && Time.time >= reloadEnd) FinishReload();
        if (bloom > 0f && owner != null)
        {
            float stability = Mathf.Clamp(owner.Equipped.stability, StatMin, StatMax);
            bloom = Mathf.MoveTowards(bloom, 0f, bloomRecover * stability * deltaTime);
        }
    }
    #endregion

    #region Pose (rotation, flip, sorting)
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