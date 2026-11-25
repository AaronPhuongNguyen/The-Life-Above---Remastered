using System;
using UnityEngine;

public interface IHasId { int Id { get; } }
public enum Gender : byte { Male, Female }
public enum LookGender : byte { Any, Male, Female }
public enum LookSlot : byte { Hair, Head, Body }
public enum WeaponKind : byte { Melee, Ranged }

/// <summary>One cosmetic variant (a hair, a head or a body). Gender decides who may use it.</summary>
[Serializable]
public class LookDef : IHasId
{
    public int id;
    [Tooltip("Written by the Sprite Assigner to keep the id stable. Do not edit.")]
    public string key;
    public LookGender gender = LookGender.Any;
    public Sprite East, North, South;

    public int Id => id;

    public bool Fits(Gender g) =>
        gender == LookGender.Any || (g == Gender.Male ? gender == LookGender.Male : gender == LookGender.Female);

    /// <summary>West has no art of its own: it is East mirrored. Falls back to East when a sprite is missing.</summary>
    public Sprite GetSprite(FaceDirection face, out bool flipX)
    {
        flipX = false;
        Sprite s;
        switch (face)
        {
            case FaceDirection.North: s = North; break;
            case FaceDirection.South: s = South; break;
            case FaceDirection.West: s = East; flipX = true; break;
            default: s = East; break;
        }
        if (s == null) { s = East; flipX = face == FaceDirection.West; }
        return s;
    }
}

/// <summary>Melee and ranged share this class. Stats a weapon does not need are simply 0.</summary>
[Serializable]
public class WeaponDef : IHasId
{
    public int id;
    public string name = "Weapon";
    public WeaponKind kind = WeaponKind.Ranged;
    public Sprite sprite;

    [Header("Shared")]
    [Tooltip("Attacks per second (multiplied by the unit's AttackSpeed attribute).")]
    [Min(0f)] public float fireRate = 1f;
    [Tooltip("World units (multiplied by the unit's AttackRange attribute).")]
    [Min(0f)] public float range = 1f;
    [Tooltip("Carried weight. Slows the unit down.")]
    [Min(0f)] public float weight = 0f;
    [Tooltip("Share of the unit's ATK added to the damage.")]
    [Min(0f)] public float unitAtkScale = 0f;

    [Header("Damage")]
    [Min(0f)] public float baseDamage = 20f;
    [Tooltip("0..1. Share of the target's defense that is ignored.")]
    [Range(0f, 1f)] public float armourPenetration = 0.25f;
    [Tooltip("Extra targets after the first one. 0 = hits one target.")]
    [Min(0)] public int pierceMaxTargets = 0;
    [Tooltip("Share of damage lost after each target pierced (ranged only).")]
    [Range(0f, 1f)] public float pierceFalloff = 0.2f;
    [Tooltip("Thickness of the shot. 0 = thin ray.")]
    [Min(0f)] public float width = 0.05f;

    [Header("Ranged only")]
    [Tooltip("Rounds per magazine. 0 = no ammo needed (melee).")]
    [Min(0)] public int magazine = 30;
    [Tooltip("Base reload time in seconds, divided by Manipulation.")]
    [Min(0f)] public float reloadTime = 2.5f;
    [Tooltip("Lower = bullets fly more chaotically around the aim. Never perfect at runtime.")]
    [Range(0f, 1f)] public float accuracy = 0.8f;
    [Tooltip("Lower = the aim spreads faster while firing. Never zero, never absolute.")]
    [Range(0f, 1f)] public float stability = 0.5f;
    [Tooltip("Speed of handling actions (reload...). 1 = normal.")]
    [Min(0f)] public float manipulation = 1f;

    [Header("Melee only")]
    [Tooltip("Half angle of the swing cone, in degrees.")]
    [Range(0f, 180f)] public float arc = 0f;

    public int Id => id;
    public bool IsRanged => kind == WeaponKind.Ranged;
    public bool UsesAmmo => magazine > 0;
    public int MaxTargets => 1 + pierceMaxTargets;

    public static WeaponDef CreateFist() => new WeaponDef
    {
        id = GameDatabase.FistId,
        name = "Fist",
        kind = WeaponKind.Melee,
        fireRate = 2f,
        range = 1.2f,
        arc = 60f,
        unitAtkScale = 1f,
        baseDamage = 5f,
        armourPenetration = 0f,
        pierceMaxTargets = 2,
        pierceFalloff = 0f,
        width = 0f,
        magazine = 0,
        reloadTime = 0f,
        accuracy = 0f,
        stability = 0f,
        manipulation = 0f,
        weight = 0f
    };

    public static WeaponDef CreateAK47() => new WeaponDef
    {
        id = GameDatabase.AK47Id,
        name = "AK-47",
        kind = WeaponKind.Ranged,
        fireRate = 10f,
        range = 25f,
        magazine = 30,
        reloadTime = 2.5f,
        accuracy = 0.8f,
        stability = 0.55f,
        manipulation = 1f,
        weight = 4.3f,
        unitAtkScale = 0f,
        baseDamage = 20f,
        armourPenetration = 0.25f,
        pierceMaxTargets = 0,
        pierceFalloff = 0.2f,
        width = 0.05f
    };
}