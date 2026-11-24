using System;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = ResourceName, menuName = "Database/General")]
public class GameDatabase : ScriptableObject
{
    public const string ResourceName = "GeneralDatabase";

    // built-in ids (always present)
    public const int FistId = 0, AK47Id = 1;
    public const int Bullet556Id = 0, BulletFistId = 1;

    [Header("Pawn looks (Gender Any / Male / Female)")]
    public List<LookDef> Hair = new List<LookDef>();
    public List<LookDef> Head = new List<LookDef>();
    public List<LookDef> Body = new List<LookDef>();

    [Header("Combat")]
    public List<WeaponDef> Weapons = new List<WeaponDef>();
    public List<BulletDef> Bullets = new List<BulletDef>();

    #region Access
    private static GameDatabase main;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatic() => main = null;

    /// <summary>The shared database (Resources/GeneralDatabase). Never modify it at runtime.</summary>
    public static GameDatabase Main
    {
        get
        {
            if (main != null) return main;
            main = Resources.Load<GameDatabase>(ResourceName);
            if (main == null)
            {
                Debug.LogWarning($"[GameDatabase] '{ResourceName}' not found in a Resources folder. Using built-in defaults.");
                main = CreateInstance<GameDatabase>();
                main.EnsureDefaults();
            }
            return main;
        }
    }

    private void OnEnable() { EnsureDefaults(); InvalidateCache(); }
    private void OnValidate() { EnsureDefaults(); InvalidateCache(); }
    #endregion

    #region Lookup
    private sealed class IdIndex<T> where T : class, IHasId
    {
        private readonly Dictionary<int, T> map = new Dictionary<int, T>();
        private bool built;

        public T Get(List<T> list, int id)
        {
            if (!built) Build(list);
            return map.TryGetValue(id, out T v) ? v : null;
        }

        public void Invalidate() => built = false;

        private void Build(List<T> list)
        {
            map.Clear();
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                    if (list[i] != null) map[list[i].Id] = list[i];
            built = true;
        }
    }

    private readonly IdIndex<LookDef> hairIdx = new IdIndex<LookDef>();
    private readonly IdIndex<LookDef> headIdx = new IdIndex<LookDef>();
    private readonly IdIndex<LookDef> bodyIdx = new IdIndex<LookDef>();
    private readonly IdIndex<WeaponDef> weaponIdx = new IdIndex<WeaponDef>();
    private readonly IdIndex<BulletDef> bulletIdx = new IdIndex<BulletDef>();

    /// <summary>Call after editing a list from code (the Sprite Assigner does).</summary>
    public void InvalidateCache()
    {
        hairIdx.Invalidate(); headIdx.Invalidate(); bodyIdx.Invalidate();
        weaponIdx.Invalidate(); bulletIdx.Invalidate();
    }

    public List<LookDef> GetList(LookSlot slot)
    {
        switch (slot)
        {
            case LookSlot.Hair: return Hair;
            case LookSlot.Head: return Head;
            default: return Body;
        }
    }

    private IdIndex<LookDef> IndexOf(LookSlot slot)
    {
        switch (slot)
        {
            case LookSlot.Hair: return hairIdx;
            case LookSlot.Head: return headIdx;
            default: return bodyIdx;
        }
    }

    /// <summary>Null when the id does not exist.</summary>
    public LookDef GetLook(LookSlot slot, int id) => IndexOf(slot).Get(GetList(slot), id);

    /// <summary>Unknown ids fall back to Fist, so a unit is never weaponless.</summary>
    public WeaponDef GetWeapon(int id) => weaponIdx.Get(Weapons, id) ?? weaponIdx.Get(Weapons, FistId);

    /// <summary>Unknown ids fall back to 5.56.</summary>
    public BulletDef GetBullet(int id) => bulletIdx.Get(Bullets, id) ?? bulletIdx.Get(Bullets, Bullet556Id);
    #endregion

    #region Random / validation (seeded through RNG)
    /// <summary>Random look id that fits the gender. -1 when the list has none.</summary>
    public int RandomLookId(LookSlot slot, Gender gender)
    {
        List<LookDef> list = GetList(slot);
        int count = 0;
        for (int i = 0; i < list.Count; i++)
            if (list[i] != null && list[i].Fits(gender)) count++;
        if (count == 0) return -1;

        int pick = count == 1 ? 0 : RNG.GetInt(0, count);
        for (int i = 0; i < list.Count; i++)
            if (list[i] != null && list[i].Fits(gender) && pick-- == 0) return list[i].id;
        return -1;
    }

    /// <summary>Keeps the id when it exists and fits the gender, otherwise picks a random fitting one.</summary>
    public int ValidLookId(LookSlot slot, int id, Gender gender)
    {
        LookDef d = GetLook(slot, id);
        return d != null && d.Fits(gender) ? id : RandomLookId(slot, gender);
    }
    #endregion

    #region Defaults
    /// <summary>Adds Fist, AK-47, 5.56 and Fist Hit when missing. Assign the AK-47 sprite in the Inspector.</summary>
    public void EnsureDefaults()
    {
        if (Hair == null) Hair = new List<LookDef>();
        if (Head == null) Head = new List<LookDef>();
        if (Body == null) Body = new List<LookDef>();
        if (Weapons == null) Weapons = new List<WeaponDef>();
        if (Bullets == null) Bullets = new List<BulletDef>();

        if (!Has(Bullets, Bullet556Id)) Bullets.Add(BulletDef.Create556());
        if (!Has(Bullets, BulletFistId)) Bullets.Add(BulletDef.CreateFistHit());
        if (!Has(Weapons, FistId)) Weapons.Add(WeaponDef.CreateFist());
        if (!Has(Weapons, AK47Id)) Weapons.Add(WeaponDef.CreateAK47());
    }

    private static bool Has<T>(List<T> list, int id) where T : class, IHasId
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] != null && list[i].Id == id) return true;
        return false;
    }

    /// <summary>Next free id of a list (max + 1).</summary>
    public static int NextId(List<LookDef> list)
    {
        int max = -1;
        for (int i = 0; i < list.Count; i++)
            if (list[i] != null && list[i].id > max) max = list[i].id;
        return max + 1;
    }
    #endregion
}

#region Enums & interfaces
public enum Gender : byte { Male, Female }
public enum LookGender : byte { Any, Male, Female }
public enum LookSlot : byte { Hair, Head, Body }
public enum WeaponKind : byte { Melee, Ranged }

public interface IHasId { int Id { get; } }
#endregion

#region Definitions
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

/// <summary>Pure data, no sprite. Both guns and melee weapons point at one of these for their damage.</summary>
[Serializable]
public class BulletDef : IHasId
{
    public int id;
    public string name = "5.56x45mm";
    [Min(0f)] public float baseDamage = 20f;
    [Tooltip("0..1. Share of the target's defense that is ignored.")]
    [Range(0f, 1f)] public float armourPenetration = 0.25f;
    [Tooltip("Extra targets after the first one. 0 = hits one target.")]
    [Min(0)] public int pierceMaxTargets = 0;
    [Tooltip("Share of damage lost after each target pierced (ranged only).")]
    [Range(0f, 1f)] public float pierceFalloff = 0.2f;
    [Tooltip("Thickness of the shot. 0 = thin ray.")]
    [Min(0f)] public float width = 0.05f;

    public int Id => id;
    public int MaxTargets => 1 + pierceMaxTargets;

    public static BulletDef Create556() => new BulletDef { id = GameDatabase.Bullet556Id, name = "5.56x45mm" };
    public static BulletDef CreateFistHit() => new BulletDef
    {
        id = GameDatabase.BulletFistId,
        name = "Fist Hit",
        baseDamage = 5f,
        armourPenetration = 0f,
        pierceMaxTargets = 2,
        pierceFalloff = 0f,
        width = 0f
    };
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
    [Tooltip("Share of the unit's ATK added to the bullet damage.")]
    [Min(0f)] public float unitAtkScale = 0f;
    public int bulletId = GameDatabase.Bullet556Id;

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

    public static WeaponDef CreateFist() => new WeaponDef
    {
        id = GameDatabase.FistId,
        name = "Fist",
        kind = WeaponKind.Melee,
        fireRate = 2f,
        range = 1.2f,
        arc = 60f,
        unitAtkScale = 1f,
        bulletId = GameDatabase.BulletFistId,
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
        bulletId = GameDatabase.Bullet556Id
    };
}
#endregion

/// <summary>
/// Read-only content database. Everything is looked up by id; a save file only needs those ids.
/// Add a new category = add one List field + one lookup. Nothing else changes.
/// </summary>
