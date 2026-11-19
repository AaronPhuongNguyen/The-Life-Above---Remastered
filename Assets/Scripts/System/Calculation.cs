using System.Runtime.CompilerServices;
using UnityEngine;

public static class Calculation
{
    #region Motion
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector2 CalculateMovement(float speed, Vector2 dir, float deltaTime, bool isActive = true)
    {
        if (!isActive) return Vector2.zero; //simply prevent moving when gameStop and forget turning off unit
        float step = speed * deltaTime;
        return new Vector2(dir.x * step, dir.y * step);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 CalculateRotation(Vector2 dir, float offset = 90f, float rotateRatio = 1f, bool isActive = true)
    {
        if (!isActive) return Vector3.zero;
        float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg * rotateRatio - offset;
        return new Vector3(0f, 0f, angle);
    }
    #endregion

    #region Combat
    // (base + flat + base * perc) * total
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float AttributeFinalValue(float _base, float _flatBonus, float _percBonus, float _totalBonus)
        => (_base + _flatBonus + (_base * _percBonus)) * _totalBonus;

    /// <summary>Defense curve: 300 / (Defense + 300). 0 def = 100% damage, 300 def = 50%.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float DamageMultiplier(float defense) => 300f / (defense + 300f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DamageResult CalculateDamage(float originalDamage, UnitAttribute attacker, UnitAttribute victim)
    {
        if (originalDamage <= 0f || attacker == null || victim == null)
            return new DamageResult(0f, null, null);

        return new DamageResult(originalDamage * DamageMultiplier(victim.Defense), attacker, victim);
    }

    /// <summary>Linear falloff by distance. 1 at center, minPerc at edge.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Falloff(float sqrDist, float sqrRadius, float minPerc)
    {
        if (minPerc >= 1f || sqrRadius <= 0f) return 1f;
        float t = Mathf.Sqrt(sqrDist / sqrRadius);          // 0..1
        return Mathf.Lerp(1f, minPerc, Mathf.Clamp01(t));
    }
    #endregion
}