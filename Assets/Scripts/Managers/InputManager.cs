using UnityEngine;
using UnityEngine.InputSystem;

public class InputManager : MonoBehaviour
{
    #region Settings
    [Header("Desktop")]
    [SerializeField] private bool desktopEnabled = true;
    [SerializeField] private Camera cam;   // leave empty = Camera.main

    [Header("Timing")]
    [SerializeField, Range(0.02f, 0.1f)] private float inputDelay = 0.02f;
    #endregion

    #region Cache
    // separate timers so movement never starves rotation (and the other way round)
    private float moveNext, rotateNext;

    private bool wasMoving;          // a non-zero move was sent, so a zero must follow on release
    private Vector2 lastMousePos;
    private bool hasMousePos;
    #endregion

    #region Unity
    private void Awake()
    {
        if (cam == null) cam = Camera.main;
    }

    private void OnDisable()
    {
        // never leave the pawn walking when input is turned off
        ReleaseMovement();
    }

    private void Update()
    {
        if (!desktopEnabled) return;

        HandleMoveKeyboard();
        HandleRotateKeyboard();
        HandleAttackKeyboard();

        if (Input.GetKeyDown(KeyCode.R)) EventBus.RaiseReload();
    }
    #endregion

    #region Desktop
    public void HandleMoveKeyboard()
    {
        if (Time.time < moveNext) return;
        moveNext = Time.time + inputDelay;

        Vector2 v = new Vector2(Input.GetAxis("Horizontal"), Input.GetAxis("Vertical"));
        if (v == Vector2.zero) { ReleaseMovement(); return; }

        wasMoving = true;
        HandleMovementLogic(v);
    }

    /// <summary>Raises the mouse position in WORLD space (use it with PawnWeapon.AimAt). Only fires when the mouse moved.</summary>
    public void HandleRotateKeyboard()
    {
        if (Time.time < rotateNext) return;
        rotateNext = Time.time + inputDelay;

        if (cam == null) { cam = Camera.main; if (cam == null) return; }

        Vector2 mouse = Input.mousePosition;

        // no mouse / cursor outside the window gives -Infinity or NaN: ignore it
        if (!IsValidScreenPoint(mouse)) return;

        if (hasMousePos && mouse == lastMousePos) return;
        hasMousePos = true;
        lastMousePos = mouse;

        HandleRotationLogic(cam.ScreenToWorldPoint(mouse));
    }

    private static bool IsValidScreenPoint(Vector2 p)
    {
        if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsInfinity(p.x) || float.IsInfinity(p.y)) return false;
        return p.x >= 0f && p.y >= 0f && p.x <= Screen.width && p.y <= Screen.height;
    }

    public void HandleAttackKeyboard()
    {
        if (Input.GetMouseButton(0)) EventBus.RaiseAttack();
    }
    #endregion

    #region Mobile
    /// <summary>Hook to the joystick action (Started / Performed / Canceled).</summary>
    public void HandleMoveJoystick(InputAction.CallbackContext ctx)
    {
        // released: always send zero, never throttled
        if (ctx.canceled) { ReleaseMovement(); return; }

        if (Time.time < moveNext) return;
        moveNext = Time.time + inputDelay;

        Vector2 v = ctx.ReadValue<Vector2>();
        if (v == Vector2.zero) { ReleaseMovement(); return; }

        wasMoving = true;
        HandleMovementLogic(v);
    }
    /// <summary>Hook to the aim joystick. Raises a normalized DIRECTION only (never a world point).</summary>
    public void HandleRotateJoystick(InputAction.CallbackContext ctx)
    {
        if (ctx.canceled) return; // released: keep the last aim, send nothing

        if (Time.time < rotateNext) return;
        rotateNext = Time.time + inputDelay;

        Vector2 v = ctx.ReadValue<Vector2>();
        if (v.sqrMagnitude < 1e-4f) return;

        EventBus.RaiseAimDirection(v.normalized);
    }
    #endregion

    #region Functions
    public void HandleMovementLogic(Vector2 v) => EventBus.RaiseMovement(v);
    public void HandleRotationLogic(Vector2 v) => EventBus.RaiseRotation(v);

    /// <summary>Sends one zero vector after movement, so listeners can stop the pawn.</summary>
    private void ReleaseMovement()
    {
        if (!wasMoving) return;
        wasMoving = false;
        HandleMovementLogic(Vector2.zero);
    }
    #endregion
}