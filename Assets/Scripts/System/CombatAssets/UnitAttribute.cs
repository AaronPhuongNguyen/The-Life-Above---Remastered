using System;
using System.Runtime.CompilerServices;
using UnityEngine;

public enum AttributeType : byte
{
    HP, ATK, Defense, Speed, Sight, AttackRange, AttackSpeed
}

[Serializable]
public class UnitAttribute
{
    //Status: HP, ATK, Defense, Speed, Sight, AttackRange, AttackSpeed.

    #region Base (edit in Inspector)
    [Header("Base")]
    public double BaseHP = 100;
    public double BaseATK = 10;
    public double BaseDefense = 0;
    public double BaseSpeed = 3;
    public double BaseSight = 5;
    public double BaseAttackRange = 1;
    public double BaseAttackSpeed = 1;
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
    public double HP_Max => Final(BaseHP, Ampl_HP);
    public double ATK => Final(BaseATK, Ampl_ATK);
    public double Defense => Final(BaseDefense, Ampl_Defense);
    public double Speed => Final(BaseSpeed, Ampl_Speed);
    public double Sight => Final(BaseSight, Ampl_Sight);
    public double AttackRange => Final(BaseAttackRange, Ampl_AttackRange);
    public double AttackSpeed => Final(BaseAttackSpeed, Ampl_AttackSpeed);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Final(double baseValue, AttributeAmplifier a)
        => Math.Max(0d, Calculation.AttributeFinalValue(baseValue, a.FlatBonus, a.PercBonus, a.TotalBonus));
    #endregion

    #region Generic Access (data-driven buffs, UI, etc.)
    public double Get(AttributeType type) => type switch
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

    public double GetBase(AttributeType type) => type switch
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

    public void SetBase(AttributeType type, double value)
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
        BaseHP = Math.Max(0d, BaseHP);
        BaseATK = Math.Max(0d, BaseATK);
        BaseDefense = Math.Max(0d, BaseDefense);
        BaseSpeed = Math.Max(0d, BaseSpeed);
        BaseSight = Math.Max(0d, BaseSight);
        BaseAttackRange = Math.Max(0d, BaseAttackRange);
        BaseAttackSpeed = Math.Max(0d, BaseAttackSpeed);

        Ampl_HP.Sanitize();
        Ampl_ATK.Sanitize();
        Ampl_Defense.Sanitize();
        Ampl_Speed.Sanitize();
        Ampl_Sight.Sanitize();
        Ampl_AttackRange.Sanitize();
        Ampl_AttackSpeed.Sanitize();
    }
    #endregion
}

[Serializable]
public class AttributeAmplifier
{
    public double FlatBonus;        // added to base:            +5
    public double PercBonus;        // additive % of base:       0.25 = +25%   (identity = 0)
    public double TotalBonus = 1;   // final multiplier:         1.5 = x1.5    (identity = 1)

    public void Reset()
    {
        FlatBonus = 0d;
        PercBonus = 0d;
        TotalBonus = 1d;
    }

    public void CopyFrom(AttributeAmplifier s)
    {
        FlatBonus = s.FlatBonus;
        PercBonus = s.PercBonus;
        TotalBonus = s.TotalBonus;
    }

    public void Sanitize()
    {
        if (TotalBonus < 0d) TotalBonus = 0d;
    }
}