using UnityEngine;
using UnityEngine.InputSystem;

public class InputManager : MonoBehaviour
{
    #region Settings
    [SerializeField] private bool desktopEnabled = true;
    [SerializeField,Range(0.02f,0.1f),Min(0.02f)] private float inputDelay = 0.02f;
    #endregion

    #region Cache
    private float delayInterval;
    #endregion

    #region Unity
    public void Update()
    {
        if (desktopEnabled)
        {
            HandleMoveKeyboard();
        }
    }
    #endregion

    #region Functions    
    //Desktop
    public void HandleMoveKeyboard()
    {
        if (Time.time < delayInterval) return;
        delayInterval = Time.time + inputDelay;

        float x = Input.GetAxis("Horizontal");
        float y = Input.GetAxis("Vertical");
        Vector2 vector = new Vector2(x, y);
        if (vector == Vector2.zero) return;

        HandleMovementLogic(vector);
    }
    public void HandleRotateKeyboard()
    {
        if (Time.time < delayInterval) return;
        delayInterval = Time.time + inputDelay;

        float x = Input.mousePosition.x;
        float y = Input.mousePosition.y;        
    }

    //Mobile
    public void HandleMoveJoystick(InputAction.CallbackContext ctx)
    {
        if (Time.time < delayInterval) return;
        delayInterval = Time.time + inputDelay;

        Vector2 vector = ctx.ReadValue<Vector2>();
        if(vector == Vector2.zero) return;
        HandleMovementLogic(vector);
    }

    //Functions
    public void HandleMovementLogic(Vector2 v) => EventBus.RaiseMovement(v);
    public void HandleRotationLogic(Vector2 v) => EventBus.RaiseRotation(v);
    #endregion
}