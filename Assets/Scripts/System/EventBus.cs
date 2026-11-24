using UnityEngine;
using System;

public static class EventBus
{
    #region Input Event
    public static Action<Vector2> OnMoved, OnRotated;
    public static Action<Vector2> OnAimDirection;
    public static Action OnAttack, OnReload;

    public static void RaiseMovement(Vector2 v) => OnMoved?.Invoke(v);
    public static void RaiseRotation(Vector2 v) => OnRotated?.Invoke(v);
    public static void RaiseAimDirection(Vector2 dir) => OnAimDirection?.Invoke(dir);
    public static void RaiseAttack() => OnAttack?.Invoke();
    public static void RaiseReload() => OnReload?.Invoke();
    #endregion

    #region Gameplay Event
    #endregion

    #region Misc Event
    #endregion
}