using UnityEngine;
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine.InputSystem;
#endif

/// <summary>Inspector-only combat test. Space = damage, H = heal. No collider needed.</summary>
public class UnitDebug : MonoBehaviour
{
    public UnitAttribute Unit = new UnitAttribute();

    [Header("Amounts")]
    public float DamageAmount = 100f;
    public float HealAmount = 50f;

    [Header("Passive: +30% damage dealt / heal received when HP < 50%")]
    public bool PassiveEnabled = true;
    [Range(0f, 1f)] public float Threshold = 0.5f;
    public float Bonus = 0.3f;

    [Header("Last result (read only)")]
    [TextArea(2, 4)] public string Log;

    void Reset() => Unit.BaseHP = 500f;   // default 100 HP would die on the first hit

    void Awake() => Unit.RefreshCombat();

    void OnEnable()
    {
        Unit.OnDealDamage += OnDeal;
        Unit.OnTakeHeal += OnHeal;
    }

    void OnDisable()
    {
        Unit.OnDealDamage -= OnDeal;
        Unit.OnTakeHeal -= OnHeal;
    }

    void Update()
    {
        if (Pressed(KeyCode.Space)) Damage();
        if (Pressed(KeyCode.H)) Heal();
    }

    // passive
    bool Low => PassiveEnabled && Unit.HP_Perc < Threshold;
    void OnDeal(ref DamageResult r) { if (Low) r.Bonus_Total *= 1f + Bonus; }
    void OnHeal(ref HealResult r) { if (Low) r.Bonus_Total *= 1f + Bonus; }

    // actions
    [ContextMenu("Take Damage")]
    void Damage()
    {
        bool hit = Attack.Apply(Unit, Unit, DamageAmount, out DamageResult r);
        Log = hit
            ? $"DAMAGE {r.Damage:0.#} (base {r.Base:0.#} x{r.Bonus_Total:0.##} x def {r.Mitigation:0.##}) -> HP {Unit.HP_Current:0.#}/{Unit.HP_Max:0.#}"
            : "DAMAGE: no effect (dead or blocked)";
        Debug.Log(Log, this);
    }

    [ContextMenu("Heal")]
    void Heal()
    {
        float healed = Attack.Heal(Unit, Unit, HealAmount, out HealResult r);
        Log = healed > 0f
            ? $"HEAL {r.Amount:0.#} (base {r.Base:0.#} x{r.Bonus_Total:0.##}) -> HP {Unit.HP_Current:0.#}/{Unit.HP_Max:0.#}"
            : "HEAL: no effect (full HP or dead)";
        Debug.Log(Log, this);
    }

    [ContextMenu("Set HP 40%")] void SetLow() => Unit.HP_Current = Unit.HP_Max * 0.4f;
    [ContextMenu("Revive (full HP)")] void Revive() => Unit.RefreshCombat();
    [ContextMenu("Toggle +300 Defense")]
    void ToggleDef() => Unit.Ampl_Defense.FlatBonus = Unit.Ampl_Defense.FlatBonus > 0f ? 0f : 300f;

    static bool Pressed(KeyCode k)
    {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        var kb = Keyboard.current;
        if (kb == null) return false;
        return k == KeyCode.Space ? kb.spaceKey.wasPressedThisFrame : kb.hKey.wasPressedThisFrame;
#else
        return Input.GetKeyDown(k);
#endif
    }
}