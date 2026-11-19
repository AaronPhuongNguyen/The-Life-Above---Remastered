using System.Collections.Generic;
using UnityEngine;

public readonly struct DamageResult
{
    public readonly float Damage;
    public readonly UnitAttribute Attacker;
    public readonly UnitAttribute Victim;

    public DamageResult(float damage, UnitAttribute a, UnitAttribute v)
    {
        Damage = damage;
        Attacker = a;
        Victim = v;
    }
}

/// <summary>
/// Collider -> UnitAttribute lookup. Replaces GetComponent in hot paths (one dictionary hit on an int key, no allocation).
/// Units register their colliders in OnEnable and unregister in OnDisable (see UnitBody below).
/// </summary>
public static class UnitRegistry
{
    static readonly Dictionary<int, UnitAttribute> map = new Dictionary<int, UnitAttribute>(512);

    public static void Register(Collider2D col, UnitAttribute attr) { if (col) map[col.GetInstanceID()] = attr; }
    public static void Unregister(Collider2D col) { if (col) map.Remove(col.GetInstanceID()); }
    public static bool TryGet(Collider2D col, out UnitAttribute attr) => map.TryGetValue(col.GetInstanceID(), out attr);

    // static state survives play-mode entry when Domain Reload is disabled
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Reset() => map.Clear();
}

/// <summary>Minimal example binding: put on a unit with one or more Collider2D.</summary>
public class UnitBody : MonoBehaviour
{
    public UnitAttribute Attribute = new UnitAttribute();
    Collider2D[] cols;

    void Awake() => cols = GetComponentsInChildren<Collider2D>();
    void OnEnable() { Attribute.RefreshCombat(); foreach (var c in cols) UnitRegistry.Register(c, Attribute); }
    void OnDisable() { foreach (var c in cols) UnitRegistry.Unregister(c); }
    void OnValidate() => Attribute.Refresh();
}

/// <summary>
/// All damage queries. Designed for 100-300 calls/frame:
///  - zero GC: static NonAlloc buffers, no LINQ/closures, optional caller-owned result list
///  - LayerMask filtering done by physics (put each team on its own layer), not in C#
///  - no GetComponent: UnitRegistry lookup
///  - per-query stamp de-duplicates multi-collider units without a HashSet
///  - sqrMagnitude where possible
/// </summary>
public static class Attack
{
    public const int MaxHits = 64;          // buffer size; hits beyond this are dropped (nearest first for rays)

    static readonly RaycastHit2D[] rayBuf = new RaycastHit2D[MaxHits];
    static readonly Collider2D[] colBuf = new Collider2D[MaxHits];
    static int stamp;

    #region Core
    static ContactFilter2D Filter(LayerMask mask)
    {
        ContactFilter2D f = new ContactFilter2D();   // struct, no allocation
        f.SetLayerMask(mask);
        f.useTriggers = true;                         // hit units even if their collider is a trigger
        return f;
    }

    static int NextStamp()
    {
        if (++stamp == int.MaxValue) stamp = 1;      // wrap-around: harmless, a stale stamp only skips one unit once
        return stamp;
    }

    /// <summary>Direct hit, no physics query (melee target already known, projectiles with OnTrigger, DoT ticks...).</summary>
    public static bool Apply(UnitAttribute attacker, UnitAttribute victim, float damage, out DamageResult result)
    {
        result = default;
        if (attacker == null || victim == null || !victim.IsAlive) return false;

        result = Calculation.CalculateDamage(damage, attacker, victim);
        if (result.Damage <= 0f) return false;

        victim.TakeDamage(result.Damage);            // fires OnDeath if lethal
        victim.OnTakeDamage?.Invoke(result);
        attacker.OnDealDamage?.Invoke(result);
        return true;
    }

    public static bool Apply(UnitAttribute attacker, UnitAttribute victim, float damage)
        => Apply(attacker, victim, damage, out _);

    /// <summary>Damage = attacker.ATK * multiplier.</summary>
    public static bool ApplyScaled(UnitAttribute attacker, UnitAttribute victim, float atkMultiplier = 1f)
        => attacker != null && Apply(attacker, victim, attacker.ATK * atkMultiplier, out _);

    static bool TryHit(Collider2D col, UnitAttribute attacker, float damage, int id, List<DamageResult> results)
    {
        if (!UnitRegistry.TryGet(col, out UnitAttribute victim)) return false;
        if (victim == attacker || victim.HitStamp == id) return false;
        victim.HitStamp = id;

        if (!Apply(attacker, victim, damage, out DamageResult r)) return false;
        results?.Add(r);
        return true;
    }

    static bool Normalize(ref Vector2 dir)
    {
        float sqr = dir.sqrMagnitude;
        if (sqr < 1e-8f) return false;
        if (Mathf.Abs(sqr - 1f) > 1e-4f) dir /= Mathf.Sqrt(sqr);
        return true;
    }

    // processes rayBuf[0..n) (already sorted by distance), applies per-hit falloff
    static int ProcessRayHits(int n, float damage, UnitAttribute attacker, int maxTargets, float falloffPerHit, List<DamageResult> results)
    {
        int id = NextStamp();
        int hits = 0;
        float dmg = damage;
        float keep = 1f - Mathf.Clamp01(falloffPerHit);

        for (int i = 0; i < n && hits < maxTargets && dmg > 0f; i++)
        {
            if (TryHit(rayBuf[i].collider, attacker, dmg, id, results))
            {
                hits++;
                dmg *= keep;                          // each unit pierced loses a share of damage
            }
        }
        return hits;
    }
    #endregion

    #region Single (first target on a line)
    /// <summary>Raycast, damages the first valid unit. Use width > 0 for a thick line (circle cast).</summary>
    public static bool Single(Vector2 origin, Vector2 dir, float range, float damage, UnitAttribute attacker,
                              LayerMask mask, out DamageResult result, float width = 0f)
    {
        result = default;
        if (attacker == null || damage <= 0f || !Normalize(ref dir)) return false;

        ContactFilter2D f = Filter(mask);
        int n = width > 0f
            ? Physics2D.CircleCast(origin, width * 0.5f, dir, f, rayBuf, range)
            : Physics2D.Raycast(origin, dir, f, rayBuf, range);

        int id = NextStamp();
        for (int i = 0; i < n; i++)           // nearest first; skips self / non-units
        {
            if (!UnitRegistry.TryGet(rayBuf[i].collider, out UnitAttribute v) || v == attacker) continue;
            v.HitStamp = id;
            return Apply(attacker, v, damage, out result);
        }
        return false;
    }

    public static bool Single(Vector2 origin, Vector2 dir, float range, UnitAttribute attacker, LayerMask mask, float width = 0f)
        => Single(origin, dir, range, attacker.ATK, attacker, mask, out _, width);
    #endregion

    #region Pierce (line through several targets)
    /// <param name="maxTargets">Stop after N units. int.MaxValue = unlimited (capped by MaxHits).</param>
    /// <param name="falloffPerHit">0..1, share of damage lost after each unit (0.2 = 100%, 80%, 64%...).</param>
    /// <param name="width">0 = thin ray, otherwise thickness of the beam.</param>
    /// <returns>Number of units damaged.</returns>
    public static int Pierce(Vector2 origin, Vector2 dir, float range, float damage, UnitAttribute attacker, LayerMask mask,
                             int maxTargets = int.MaxValue, float falloffPerHit = 0f, float width = 0f,
                             List<DamageResult> results = null)
    {
        if (attacker == null || damage <= 0f || !Normalize(ref dir)) return 0;

        ContactFilter2D f = Filter(mask);
        int n = width > 0f
            ? Physics2D.CircleCast(origin, width * 0.5f, dir, f, rayBuf, range)
            : Physics2D.Raycast(origin, dir, f, rayBuf, range);

        return ProcessRayHits(n, damage, attacker, maxTargets, falloffPerHit, results);
    }

    public static int Pierce(Vector2 origin, Vector2 dir, float range, UnitAttribute attacker, LayerMask mask,
                             int maxTargets = int.MaxValue, float falloffPerHit = 0f, float width = 0f,
                             List<DamageResult> results = null)
        => Pierce(origin, dir, range, attacker.ATK, attacker, mask, maxTargets, falloffPerHit, width, results);

    /// <summary>Rectangular sweep (laser, sword wave, charge). Boxes are sorted by distance along dir.</summary>
    public static int PierceBox(Vector2 origin, Vector2 dir, float range, Vector2 size, float damage, UnitAttribute attacker,
                                LayerMask mask, int maxTargets = int.MaxValue, float falloffPerHit = 0f,
                                List<DamageResult> results = null)
    {
        if (attacker == null || damage <= 0f || !Normalize(ref dir)) return 0;

        float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
        int n = Physics2D.BoxCast(origin, size, angle, dir, Filter(mask), rayBuf, range);
        return ProcessRayHits(n, damage, attacker, maxTargets, falloffPerHit, results);
    }
    #endregion

    #region AoE (circle)
    /// <param name="minDamagePerc">Damage at the very edge (1 = no falloff, 0.5 = half damage at edge).</param>
    public static int Circle(Vector2 center, float radius, float damage, UnitAttribute attacker, LayerMask mask,
                             float minDamagePerc = 1f, int maxTargets = int.MaxValue, List<DamageResult> results = null)
    {
        if (attacker == null || damage <= 0f || radius <= 0f) return 0;

        int n = Physics2D.OverlapCircle(center, radius, Filter(mask), colBuf);
        int id = NextStamp();
        float sqrR = radius * radius;
        int hits = 0;

        for (int i = 0; i < n && hits < maxTargets; i++)
        {
            Collider2D c = colBuf[i];
            float mult = 1f;
            if (minDamagePerc < 1f)
                mult = Calculation.Falloff(((Vector2)c.ClosestPoint(center) - center).sqrMagnitude, sqrR, minDamagePerc);

            if (TryHit(c, attacker, damage * mult, id, results)) hits++;
        }
        return hits;
    }

    public static int Circle(Vector2 center, float radius, UnitAttribute attacker, LayerMask mask,
                             float minDamagePerc = 1f, int maxTargets = int.MaxValue, List<DamageResult> results = null)
        => Circle(center, radius, attacker.ATK, attacker, mask, minDamagePerc, maxTargets, results);
    #endregion

    #region AoE (box)
    public static int Box(Vector2 center, Vector2 size, float angle, float damage, UnitAttribute attacker, LayerMask mask,
                          int maxTargets = int.MaxValue, List<DamageResult> results = null)
    {
        if (attacker == null || damage <= 0f) return 0;

        int n = Physics2D.OverlapBox(center, size, angle, Filter(mask), colBuf);
        int id = NextStamp();
        int hits = 0;

        for (int i = 0; i < n && hits < maxTargets; i++)
            if (TryHit(colBuf[i], attacker, damage, id, results)) hits++;
        return hits;
    }
    #endregion

    #region AoE (cone / arc)
    /// <param name="halfAngle">Degrees from dir to each side (45 = 90 degree cone).</param>
    public static int Cone(Vector2 origin, Vector2 dir, float range, float halfAngle, float damage, UnitAttribute attacker,
                           LayerMask mask, int maxTargets = int.MaxValue, List<DamageResult> results = null)
    {
        if (attacker == null || damage <= 0f || range <= 0f || !Normalize(ref dir)) return 0;

        int n = Physics2D.OverlapCircle(origin, range, Filter(mask), colBuf);
        int id = NextStamp();
        float cos = Mathf.Cos(halfAngle * Mathf.Deg2Rad);
        int hits = 0;

        for (int i = 0; i < n && hits < maxTargets; i++)
        {
            Collider2D c = colBuf[i];
            Vector2 to = (Vector2)c.ClosestPoint(origin) - origin;
            float mag = to.magnitude;

            // inside/touching origin counts as hit, otherwise compare angle via dot product (no Acos)
            if (mag > 1e-4f && Vector2.Dot(dir, to) < cos * mag) continue;

            if (TryHit(c, attacker, damage, id, results)) hits++;
        }
        return hits;
    }
    #endregion
}