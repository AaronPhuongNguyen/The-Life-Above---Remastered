using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Central update dispatcher for TickBehaviour.
///
/// Time semantics:
///   ForcedTick(unscaledDeltaTime) : always runs, even while paused.
///   SimulTick(simulDeltaTime)     : unscaled * DesignedGameSpeed (the speed synced with Unity's Time.timeScale).
///   Tick / LateTick(deltaTime)    : unscaled * effective CurrentGameSpeed (includes hit-stop). Most gameplay uses this.
///   While paused, Simul/Tick/LateTick are skipped entirely.
/// </summary>
[DefaultExecutionOrder(-1000)]
public sealed class TickManager : MonoBehaviour
{
    #region Constants / Types
    private const int PhaseSimul = 0;
    private const int PhaseTick = 1;
    private const int PhaseLate = 2;
    private const int PhaseForced = 3;
    private const int PhaseCount = 4;
    private const int InitialCapacity = 128;

    // Flat array with null holes. Removal never shifts elements, so it is safe to
    // subscribe/unsubscribe from inside a tick. Holes are compacted after each pass.
    private sealed class TickList
    {
        public TickBehaviour[] items = new TickBehaviour[InitialCapacity];
        public int count;
        public int holes;

        public int Add(TickBehaviour t)
        {
            if (count == items.Length) Array.Resize(ref items, count << 1);
            items[count] = t;
            return count++;
        }

        public void RemoveAt(int index)
        {
            items[index] = null;
            holes++;
        }
    }
    #endregion

    #region Fields
    [Header("Settings")]
    [SerializeField, Tooltip("Clamp for frame spikes (seconds).")]
    private float maxDeltaTime = 0.1f;
    [SerializeField, Tooltip("Keep Unity's Time.timeScale equal to DesignedGameSpeed (0 when paused).")]
    private bool syncUnityTimeScale = true;

    private float CurrentGameSpeed = 1f;  //Use for most tasks
    private float DesignedGameSpeed = 1f; //Use for logic confirm game synced with Unity Timescale.

    private bool paused;
    private float hitStopTimer;
    private float hitStopSpeed;

    private float frameUnscaledDt;
    private float frameScaledDt;

    private readonly TickList[] lists = new TickList[PhaseCount];

    private static TickManager instance;
    private static bool isQuitting;
    private static readonly Dictionary<Type, byte> maskCache = new Dictionary<Type, byte>(64);
    #endregion

    #region Public API
    public static TickManager Instance => EnsureInstance();
    public static bool IsPaused => instance != null && instance.paused;

    /// <summary>Base speed used by Tick/LateTick.</summary>
    public static float GameSpeed
    {
        get => instance != null ? instance.CurrentGameSpeed : 1f;
        set { if (EnsureInstance() is TickManager m) m.CurrentGameSpeed = Mathf.Max(0f, value); }
    }

    /// <summary>Speed used by SimulTick; mirrored to Time.timeScale when syncUnityTimeScale is on.</summary>
    public static float DesignedSpeed
    {
        get => instance != null ? instance.DesignedGameSpeed : 1f;
        set
        {
            if (EnsureInstance() is TickManager m)
            {
                m.DesignedGameSpeed = Mathf.Max(0f, value);
                m.ApplyTimeScale();
            }
        }
    }

    public static void SetPaused(bool value)
    {
        if (EnsureInstance() is TickManager m)
        {
            m.paused = value;
            m.ApplyTimeScale();
        }
    }

    /// <summary>
    /// Freeze/slow the game for a real-time duration (hit-stop). Uses the lowest speed
    /// if called again while active. Runs on unscaled time so it always ends.
    /// </summary>
    public static void HitStop(float duration, float speed = 0f)
    {
        if (duration <= 0f || EnsureInstance() is not TickManager m) return;
        speed = Mathf.Max(0f, speed);
        m.hitStopSpeed = m.hitStopTimer > 0f ? Mathf.Min(m.hitStopSpeed, speed) : speed;
        m.hitStopTimer = Mathf.Max(m.hitStopTimer, duration);
    }

    public static int GetSubscriberCount(bool tickPhaseOnly = true)
    {
        if (instance == null) return 0;
        var l = instance.lists[PhaseTick];
        return l.count - l.holes;
    }
    #endregion

    #region Lifecycle
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        // Supports "Enter Play Mode without domain reload".
        instance = null;
        isQuitting = false;
        maskCache.Clear();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap() => EnsureInstance();

    private static TickManager EnsureInstance()
    {
        if (instance != null) return instance;
        if (isQuitting) return null;

        var found = FindFirstObjectByType<TickManager>();
        if (found != null) { instance = found; return instance; }

        var go = new GameObject("[TickManager]");
        instance = go.AddComponent<TickManager>(); // Awake registers + DontDestroyOnLoad
        return instance;
    }

    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }
        instance = this;
        for (int i = 0; i < PhaseCount; i++) lists[i] ??= new TickList();
        DontDestroyOnLoad(gameObject);
        ApplyTimeScale();
    }

    private void OnApplicationQuit() => isQuitting = true;

    private void OnDestroy()
    {
        if (instance != this) return;
        instance = null;

        // Reset bookkeeping so behaviours can safely re-subscribe to a new manager.
        for (int p = 0; p < PhaseCount; p++)
        {
            var l = lists[p];
            for (int i = 0; i < l.count; i++)
            {
                var t = l.items[i];
                if (t != null) ResetSlots(t);
            }
            Array.Clear(l.items, 0, l.count);
            l.count = l.holes = 0;
        }
    }
    #endregion

    #region Logical Thread
    private void Update()
    {
        float unscaled = Mathf.Min(Time.unscaledDeltaTime, maxDeltaTime);
        frameUnscaledDt = unscaled;

        float speed = CurrentGameSpeed;
        if (hitStopTimer > 0f)
        {
            hitStopTimer -= unscaled;
            speed = Mathf.Min(speed, hitStopSpeed);
        }

        frameScaledDt = unscaled * speed;
        Tick(unscaled);
    }

    private void LateUpdate()
    {
        if (paused) return;
        RunPhase(PhaseLate, frameScaledDt);
    }
    #endregion

    #region Main Functions
    private void Tick(float unscaledDt)
    {
        RunPhase(PhaseForced, unscaledDt);

        if (!paused)
        {
            RunPhase(PhaseSimul, unscaledDt * DesignedGameSpeed);
            RunPhase(PhaseTick, frameScaledDt);
        }
    }

    public static void SubscribeTick(TickBehaviour ticker)
    {
        if (ticker == null || ticker.tickSubscribed) return;
        var m = EnsureInstance();
        if (m == null) return;

        byte mask = GetMask(ticker.GetType());
        ticker.tickMask = mask;
        ticker.tickSubscribed = true;

        // Newly added entries land beyond the current pass's snapshot count,
        // so they start ticking next frame (no mid-pass surprises).
        if ((mask & (1 << PhaseSimul)) != 0) ticker.idxSimul = m.lists[PhaseSimul].Add(ticker);
        if ((mask & (1 << PhaseTick)) != 0) ticker.idxTick = m.lists[PhaseTick].Add(ticker);
        if ((mask & (1 << PhaseLate)) != 0) ticker.idxLate = m.lists[PhaseLate].Add(ticker);
        if ((mask & (1 << PhaseForced)) != 0) ticker.idxForced = m.lists[PhaseForced].Add(ticker);
    }

    public static void UnsubscribeTick(TickBehaviour ticker)
    {
        if (ticker == null || !ticker.tickSubscribed) return;
        var m = instance;
        if (m == null) { ResetSlots(ticker); return; } // manager gone (quit / scene teardown)

        byte mask = ticker.tickMask;
        if ((mask & (1 << PhaseSimul)) != 0) m.lists[PhaseSimul].RemoveAt(ticker.idxSimul);
        if ((mask & (1 << PhaseTick)) != 0) m.lists[PhaseTick].RemoveAt(ticker.idxTick);
        if ((mask & (1 << PhaseLate)) != 0) m.lists[PhaseLate].RemoveAt(ticker.idxLate);
        if ((mask & (1 << PhaseForced)) != 0) m.lists[PhaseForced].RemoveAt(ticker.idxForced);
        ResetSlots(ticker);
    }
    #endregion

    #region Helpers
    private void RunPhase(int phase, float dt)
    {
        var list = lists[phase];
        int n = list.count; // snapshot: anything subscribed during this pass waits until next pass

        for (int i = 0; i < n; i++)
        {
            var t = list.items[i]; // re-read each time: array may be resized by Subscribe mid-pass
            if (t == null) continue;

            try
            {
                switch (phase)
                {
                    case PhaseSimul: t.RunSimulTick(dt); break;
                    case PhaseTick: t.RunTick(dt); break;
                    case PhaseLate: t.RunLateTick(dt); break;
                    default: t.RunForcedTick(dt); break;
                }
            }
            catch (Exception e)
            {
                // One broken behaviour must not stop the whole game loop.
                Debug.LogException(e, t);
            }
        }

        if (list.holes > 0) Compact(list, phase);
    }

    // Stable compaction (keeps execution order), O(n), only when something was removed.
    private static void Compact(TickList list, int phase)
    {
        var items = list.items;
        int w = 0;
        for (int r = 0; r < list.count; r++)
        {
            var t = items[r];
            if (t == null) continue;
            if (w != r)
            {
                items[w] = t;
                SetIndex(t, phase, w);
            }
            w++;
        }
        Array.Clear(items, w, list.count - w);
        list.count = w;
        list.holes = 0;
    }

    private static void SetIndex(TickBehaviour t, int phase, int index)
    {
        switch (phase)
        {
            case PhaseSimul: t.idxSimul = index; break;
            case PhaseTick: t.idxTick = index; break;
            case PhaseLate: t.idxLate = index; break;
            default: t.idxForced = index; break;
        }
    }

    private static void ResetSlots(TickBehaviour t)
    {
        t.tickSubscribed = false;
        t.idxSimul = t.idxTick = t.idxLate = t.idxForced = -1;
    }

    private void ApplyTimeScale()
    {
        if (!syncUnityTimeScale) return;
        Time.timeScale = paused ? 0f : DesignedGameSpeed;
        Debug.Log($"Current Timescale: {Time.timeScale}");
    }

    // Only tick methods a type actually overrides are scheduled: a behaviour that
    // only overrides Tick costs nothing in the other three passes.
    private static byte GetMask(Type type)
    {
        if (maskCache.TryGetValue(type, out byte cached)) return cached;

        byte mask = 0;
        if (Overrides(type, "SimulTick")) mask |= 1 << PhaseSimul;
        if (Overrides(type, "Tick")) mask |= 1 << PhaseTick;
        if (Overrides(type, "LateTick")) mask |= 1 << PhaseLate;
        if (Overrides(type, "ForcedTick")) mask |= 1 << PhaseForced;

        maskCache[type] = mask;
        return mask;
    }

    private static readonly Type[] FloatArg = { typeof(float) };
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static bool Overrides(Type type, string method)
    {
        var mi = type.GetMethod(method, Flags, null, FloatArg, null);
        if (mi == null) return true; // reflection unavailable (e.g. stripped): be safe, schedule it
        return mi.DeclaringType != typeof(TickBehaviour);
    }
    #endregion
}