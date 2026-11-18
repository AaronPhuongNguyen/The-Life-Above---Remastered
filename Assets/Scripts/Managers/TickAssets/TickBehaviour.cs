using UnityEngine;

[DefaultExecutionOrder(999)]
public class TickBehaviour : MonoBehaviour
{
    #region Advanced Features
    protected virtual void Wake() { }
    protected virtual void Init() { }
    protected virtual void On() => TickManager.SubscribeTick(this);
    protected virtual void Off() => TickManager.UnsubscribeTick(this);
    protected virtual void Die() { }
    #endregion

    #region Tick Behaviour
    protected virtual void SimulTick(float simulDeltaTime) { }
    protected virtual void Tick(float deltaTime) { }
    protected virtual void LateTick(float deltaTime) { }
    protected virtual void ForcedTick(float unscaledDeltaTime) { }
    #endregion

    #region Unity Binder
    private void Awake() => Wake();
    private void Start() => Init();
    private void OnEnable() => On();
    private void OnDisable() => Off();
    private void OnDestroy() => Die();
    #endregion

    #region TickManager Internals (do not touch)
    // Bookkeeping owned by TickManager: O(1) lookup, zero dictionary / allocation cost.
    internal bool tickSubscribed;
    internal byte tickMask;       // which Tick methods are actually overridden
    internal int idxSimul = -1;
    internal int idxTick = -1;
    internal int idxLate = -1;
    internal int idxForced = -1;

    internal void RunSimulTick(float dt) => SimulTick(dt);
    internal void RunTick(float dt) => Tick(dt);
    internal void RunLateTick(float dt) => LateTick(dt);
    internal void RunForcedTick(float dt) => ForcedTick(dt);
    #endregion
}