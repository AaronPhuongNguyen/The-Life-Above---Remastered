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
    public static Vector3 CalculateRotation(Vector2 dir, float offset = 90, float rotateRatio = 1, bool isActive = true)
    {
        if (!isActive) return Vector3.zero;
        float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg * rotateRatio - offset;
        return new Vector3(0, 0, angle);
    }
    #endregion

    #region Combat
    // (base + flat + base * perc) * total
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double AttributeFinalValue(double _base, double _flatBonus, double _percBonus, double _totalBonus)
        => (_base + _flatBonus + (_base * _percBonus)) * _totalBonus;
    #endregion
}