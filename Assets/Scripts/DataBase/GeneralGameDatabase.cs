using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = ResourceName, menuName = "Database/General")]
public class GameDatabase : ScriptableObject
{
    public const string ResourceName = "GeneralDatabase";

    // built-in ids (always present)
    public const int FistId = 0, AK47Id = 1;

    [Header("Pawn looks (Gender Any / Male / Female)")]
    public List<LookDef> Hair = new List<LookDef>();
    public List<LookDef> Head = new List<LookDef>();
    public List<LookDef> Body = new List<LookDef>();

    [Header("Combat")]
    public List<WeaponDef> Weapons = new List<WeaponDef>();

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

    /// <summary>Call after editing a list from code (the Sprite Assigner does).</summary>
    public void InvalidateCache()
    {
        hairIdx.Invalidate(); headIdx.Invalidate(); bodyIdx.Invalidate();
        weaponIdx.Invalidate();
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
    /// <summary>Adds Fist and AK-47 when missing. Assign the AK-47 sprite in the Inspector.</summary>
    public void EnsureDefaults()
    {
        if (Hair == null) Hair = new List<LookDef>();
        if (Head == null) Head = new List<LookDef>();
        if (Body == null) Body = new List<LookDef>();
        if (Weapons == null) Weapons = new List<WeaponDef>();

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