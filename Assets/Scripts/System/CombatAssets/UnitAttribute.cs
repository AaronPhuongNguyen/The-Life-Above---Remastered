using System;
using UnityEngine;

public enum AttributeType : byte
{
    HP, ATK, Defense, Speed, Sight, AttackRange, AttackSpeed
}

[Serializable]
public class UnitAttribute
{
    #region Runtime
    public float HP_Current;
    public float HP_Lost_Flat => HP_Max - HP_Current;
    /// <summary>Fraction of HP already lost (0 = full health, 1 = dead).</summary>
    public float HP_Lost_Perc => HP_Max > 0f ? 1f - HP_Current / HP_Max : 1f;
    /// <summary>Fraction of HP remaining (1 = full health). For health bars.</summary>
    public float HP_Perc => HP_Max > 0f ? HP_Current / HP_Max : 0f;

    public bool IsAlive => HP_Current > 0f;

    /// <summary>Used by Attack to avoid hitting the same unit twice in one query (multi-collider units). Not serialized.</summary>
    [NonSerialized] public int HitStamp;
    #endregion

    #region Base (edit in Inspector)
    [Header("Base")]
    public float BaseHP = 100f;
    public float BaseATK = 10f;
    public float BaseDefense = 0f;
    public float BaseSpeed = 3f;
    public float BaseSight = 5f;
    public float BaseAttackRange = 1f;
    public float BaseAttackSpeed = 1f;
    #endregion

    #region Amplifiers
    [Header("Amplifiers")]
    public AttributeAmplifier Ampl_HP = new AttributeAmplifier();
    public AttributeAmplifier Ampl_ATK = new AttributeAmplifier();
    public AttributeAmplifier Ampl_Defense = new AttributeAmplifier();
    public AttributeAmplifier Ampl_Speed = new AttributeAmplifier();
    public AttributeAmplifier Ampl_Sight = new AttributeAmplifier();
    public AttributeAmplifier Ampl_AttackRange = new AttributeAmplifier();
    public AttributeAmplifier Ampl_AttackSpeed = new AttributeAmplifier();
    #endregion

    #region Attributes
    //final values: (base + flat + base * perc) * total, never below 0
    public float HP_Max => Final(BaseHP, Ampl_HP);
    public float ATK => Final(BaseATK, Ampl_ATK);
    public float Defense => Final(BaseDefense, Ampl_Defense);
    public float Speed => Final(BaseSpeed, Ampl_Speed);
    public float Sight => Final(BaseSight, Ampl_Sight);
    public float AttackRange => Final(BaseAttackRange, Ampl_AttackRange);
    public float AttackSpeed => Final(BaseAttackSpeed, Ampl_AttackSpeed);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static float Final(float baseValue, AttributeAmplifier a)
        => Mathf.Max(0f, Calculation.AttributeFinalValue(baseValue, a.FlatBonus, a.PercBonus, a.TotalBonus));
    #endregion

    #region Generic Access (data-driven buffs, UI, etc.)
    public Action<DamageResult> OnTakeDamage, OnDealDamage;
    public Action<float> OnHeal;
    public Action OnDeath;

    public float Get(AttributeType type) => type switch
    {
        AttributeType.HP => HP_Max,
        AttributeType.ATK => ATK,
        AttributeType.Defense => Defense,
        AttributeType.Speed => Speed,
        AttributeType.Sight => Sight,
        AttributeType.AttackRange => AttackRange,
        AttributeType.AttackSpeed => AttackSpeed,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    public AttributeAmplifier GetAmplifier(AttributeType type) => type switch
    {
        AttributeType.HP => Ampl_HP,
        AttributeType.ATK => Ampl_ATK,
        AttributeType.Defense => Ampl_Defense,
        AttributeType.Speed => Ampl_Speed,
        AttributeType.Sight => Ampl_Sight,
        AttributeType.AttackRange => Ampl_AttackRange,
        AttributeType.AttackSpeed => Ampl_AttackSpeed,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    public float GetBase(AttributeType type) => type switch
    {
        AttributeType.HP => BaseHP,
        AttributeType.ATK => BaseATK,
        AttributeType.Defense => BaseDefense,
        AttributeType.Speed => BaseSpeed,
        AttributeType.Sight => BaseSight,
        AttributeType.AttackRange => BaseAttackRange,
        AttributeType.AttackSpeed => BaseAttackSpeed,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    public void SetBase(AttributeType type, float value)
    {
        switch (type)
        {
            case AttributeType.HP: BaseHP = value; break;
            case AttributeType.ATK: BaseATK = value; break;
            case AttributeType.Defense: BaseDefense = value; break;
            case AttributeType.Speed: BaseSpeed = value; break;
            case AttributeType.Sight: BaseSight = value; break;
            case AttributeType.AttackRange: BaseAttackRange = value; break;
            case AttributeType.AttackSpeed: BaseAttackSpeed = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(type));
        }
    }
    #endregion

    #region Methods
    /// <summary>Deep copy. Every unit must own its attributes, otherwise a buff on one unit hits all of them.</summary>
    public UnitAttribute Clone()
    {
        UnitAttribute clone = new UnitAttribute();
        clone.CopyFrom(this);
        clone.RefreshCombat();
        return clone;
    }

    /// <summary>In-place copy, zero allocation. Use this for pooled units.</summary>
    public void CopyFrom(UnitAttribute s)
    {
        if (s == null || ReferenceEquals(s, this)) return;

        BaseHP = s.BaseHP;
        BaseATK = s.BaseATK;
        BaseDefense = s.BaseDefense;
        BaseSpeed = s.BaseSpeed;
        BaseSight = s.BaseSight;
        BaseAttackRange = s.BaseAttackRange;
        BaseAttackSpeed = s.BaseAttackSpeed;

        Ampl_HP.CopyFrom(s.Ampl_HP);
        Ampl_ATK.CopyFrom(s.Ampl_ATK);
        Ampl_Defense.CopyFrom(s.Ampl_Defense);
        Ampl_Speed.CopyFrom(s.Ampl_Speed);
        Ampl_Sight.CopyFrom(s.Ampl_Sight);
        Ampl_AttackRange.CopyFrom(s.Ampl_AttackRange);
        Ampl_AttackSpeed.CopyFrom(s.Ampl_AttackSpeed);
    }

    /// <summary>Remove every buff/debuff (all amplifiers back to identity).</summary>
    public void ClearAll()
    {
        Ampl_HP.Reset();
        Ampl_ATK.Reset();
        Ampl_Defense.Reset();
        Ampl_Speed.Reset();
        Ampl_Sight.Reset();
        Ampl_AttackRange.Reset();
        Ampl_AttackSpeed.Reset();
    }

    /// <summary>Sanitize values (called from OnValidate): no negative bases, no negative total multipliers.</summary>
    public void Refresh()
    {
        BaseHP = Mathf.Max(0f, BaseHP);
        BaseATK = Mathf.Max(0f, BaseATK);
        BaseDefense = Mathf.Max(0f, BaseDefense);
        BaseSpeed = Mathf.Max(0f, BaseSpeed);
        BaseSight = Mathf.Max(0f, BaseSight);
        BaseAttackRange = Mathf.Max(0f, BaseAttackRange);
        BaseAttackSpeed = Mathf.Max(0f, BaseAttackSpeed);

        Ampl_HP.Sanitize();
        Ampl_ATK.Sanitize();
        Ampl_Defense.Sanitize();
        Ampl_Speed.Sanitize();
        Ampl_Sight.Sanitize();
        Ampl_AttackRange.Sanitize();
        Ampl_AttackSpeed.Sanitize();

        RefreshCombat();
    }

    #region Combat
    public void RefreshCombat()
    {
        HP_Current = HP_Max;
    }

    public void TakeDamage(float value)
    {
        if (value <= 0f || !IsAlive) return;

        HP_Current -= value;

        if (!IsAlive)
        {
            HP_Current = 0f;
            OnDeath?.Invoke();
        }
    }

    public void TakeHeal(float value)
    {
        if (value <= 0f || !IsAlive) return;

        HP_Current = Mathf.Min(HP_Current + value, HP_Max);
        OnHeal?.Invoke(value);
    }
    #endregion

    #endregion
}

[Serializable]
public class AttributeAmplifier
{
    public float FlatBonus;        // added to base:            +5
    public float PercBonus;        // additive % of base:       0.25 = +25%   (identity = 0)
    public float TotalBonus = 1f;  // final multiplier:         1.5 = x1.5    (identity = 1)

    public void Reset()
    {
        FlatBonus = 0f;
        PercBonus = 0f;
        TotalBonus = 1f;
    }

    public void CopyFrom(AttributeAmplifier s)
    {
        FlatBonus = s.FlatBonus;
        PercBonus = s.PercBonus;
        TotalBonus = s.TotalBonus;
    }

    public void Sanitize()
    {
        if (TotalBonus < 0f) TotalBonus = 0f;
    }
}