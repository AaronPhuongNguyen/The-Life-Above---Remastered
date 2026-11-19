using System;
using UnityEngine;

public enum Gender : byte { Male, Female }

[CreateAssetMenu(fileName = "GeneralDatabase", menuName = "Database/General")]
public class GameDatabase : ScriptableObject
{
    // To add a new category (Clothes, Armour, Accessories...):
    //   - add a GenderedPackageList field (Male / Female folders) or a PackageList / PackageList_Single field (unisex)
    //   - tag it with [SpriteFolder("FolderName")]
    // The editor tool "Tools/Pawn/Sprite Assigner" picks it up automatically.
    //
    // Folders:  Assets/Sprites/Pawn/Hair/Male, Hair/Female, Head/Male, Head/Female, Body/Male, Body/Female, Gun
    [Header("Body (Male / Female)")]
    [SpriteFolder("Hair")] public GenderedPackageList HairSprite = new GenderedPackageList();
    [SpriteFolder("Head")] public GenderedPackageList HeadSprite = new GenderedPackageList();
    [SpriteFolder("Body")] public GenderedPackageList BodySprite = new GenderedPackageList();

    [Header("Weapon (unisex)")]
    [SpriteFolder("Gun")] public PackageList_Single Guns = new PackageList_Single();

    #region Helpers
    /// <summary>Runtime copy so changes never touch the asset on disk. Sprites are shared references.</summary>
    public GameDatabase Clone() => Instantiate(this);

    // ---- random picks (seeded through RNG) ----
    public SpritePackage_Multiple GetRandomHair(Gender g, out int index) => HairSprite.GetRandom(g, out index);
    public SpritePackage_Multiple GetRandomHead(Gender g, out int index) => HeadSprite.GetRandom(g, out index);
    public SpritePackage_Multiple GetRandomBody(Gender g, out int index) => BodySprite.GetRandom(g, out index);
    public SpritePackage_Single GetRandomGun(out int index) => Guns.GetRandom(out index);

    public SpritePackage_Multiple GetRandomHair(Gender g) => GetRandomHair(g, out _);
    public SpritePackage_Multiple GetRandomHead(Gender g) => GetRandomHead(g, out _);
    public SpritePackage_Multiple GetRandomBody(Gender g) => GetRandomBody(g, out _);
    public SpritePackage_Single GetRandomGun() => GetRandomGun(out _);

    // ---- by index (index is inside the list of that gender) ----
    public SpritePackage_Multiple GetHair(Gender g, int index) => HairSprite.Get(g, index);
    public SpritePackage_Multiple GetHead(Gender g, int index) => HeadSprite.Get(g, index);
    public SpritePackage_Multiple GetBody(Gender g, int index) => BodySprite.Get(g, index);
    public SpritePackage_Single GetGun(int index) => Guns.Get(index);

    // ---- precise lookup by sprite name (matches any facing of the package) ----
    public int FindHairIndex(Gender g, string spriteName) => HairSprite.FindIndex(g, spriteName);
    public int FindHeadIndex(Gender g, string spriteName) => HeadSprite.FindIndex(g, spriteName);
    public int FindBodyIndex(Gender g, string spriteName) => BodySprite.FindIndex(g, spriteName);
    public int FindGunIndex(string spriteName) => Guns.FindIndex(spriteName);

    public SpritePackage_Multiple FindHair(Gender g, string spriteName) => HairSprite.Find(g, spriteName);
    public SpritePackage_Multiple FindHead(Gender g, string spriteName) => HeadSprite.Find(g, spriteName);
    public SpritePackage_Multiple FindBody(Gender g, string spriteName) => BodySprite.Find(g, spriteName);
    public SpritePackage_Single FindGun(string spriteName) => Guns.Find(spriteName);
    #endregion
}

#region List
/// <summary>A category that exists in a Male and a Female version (Hair, Head, Body, later Clothes...).</summary>
[Serializable]
public class GenderedPackageList
{
    public PackageList Male = new PackageList();
    public PackageList Female = new PackageList();

    public PackageList ForGender(Gender g) => g == Gender.Female ? Female : Male;

    public int Count(Gender g) => ForGender(g).Count;
    public bool IsValid(Gender g, int index) => ForGender(g).IsValid(index);

    public SpritePackage_Multiple Get(Gender g, int index) => ForGender(g).Get(index);
    public SpritePackage_Multiple GetWrapped(Gender g, int index) => ForGender(g).GetWrapped(index);

    public SpritePackage_Multiple GetRandom(Gender g, out int index) => ForGender(g).GetRandom(out index);
    public SpritePackage_Multiple GetRandom(Gender g) => ForGender(g).GetRandom();

    public int FindIndex(Gender g, string spriteName) => ForGender(g).FindIndex(spriteName);
    public SpritePackage_Multiple Find(Gender g, string spriteName) => ForGender(g).Find(spriteName);

    /// <summary>Search both lists. Returns false if the sprite name is in neither.</summary>
    public bool TryFind(string spriteName, out Gender gender, out int index)
    {
        index = Male.FindIndex(spriteName);
        if (index >= 0) { gender = Gender.Male; return true; }
        index = Female.FindIndex(spriteName);
        gender = Gender.Female;
        return index >= 0;
    }
}

[Serializable]
public class PackageList
{
    public SpritePackage_Multiple[] package;

    public int Count => package == null ? 0 : package.Length;
    public bool IsValid(int index) => index >= 0 && index < Count;

    public SpritePackage_Multiple Get(int index) => IsValid(index) ? package[index] : null;
    public bool TryGet(int index, out SpritePackage_Multiple result)
    {
        result = Get(index);
        return result != null;
    }

    /// <summary>Wraps the index around the list (handy for "next/previous" buttons).</summary>
    public SpritePackage_Multiple GetWrapped(int index)
    {
        int n = Count;
        if (n == 0) return null;
        return package[((index % n) + n) % n];
    }

    public SpritePackage_Multiple GetRandom(out int index)
    {
        int n = Count;
        index = n == 0 ? -1 : (n == 1 ? 0 : RNG.GetInt(0, n));
        return index < 0 ? null : package[index];
    }
    public SpritePackage_Multiple GetRandom() => GetRandom(out _);

    public int FindIndex(Predicate<SpritePackage_Multiple> match)
    {
        for (int i = 0; i < Count; i++)
            if (package[i] != null && match(package[i])) return i;
        return -1;
    }

    /// <summary>Index of the package having a sprite with this exact name (East, North or South). -1 if none.</summary>
    public int FindIndex(string spriteName) => FindIndex(p => p.HasSprite(spriteName));

    public SpritePackage_Multiple Find(string spriteName) => Get(FindIndex(spriteName));
}

[Serializable]
public class PackageList_Single
{
    public SpritePackage_Single[] package;

    public int Count => package == null ? 0 : package.Length;
    public bool IsValid(int index) => index >= 0 && index < Count;

    public SpritePackage_Single Get(int index) => IsValid(index) ? package[index] : null;
    public bool TryGet(int index, out SpritePackage_Single result)
    {
        result = Get(index);
        return result != null;
    }

    public SpritePackage_Single GetWrapped(int index)
    {
        int n = Count;
        if (n == 0) return null;
        return package[((index % n) + n) % n];
    }

    public SpritePackage_Single GetRandom(out int index)
    {
        int n = Count;
        index = n == 0 ? -1 : (n == 1 ? 0 : RNG.GetInt(0, n));
        return index < 0 ? null : package[index];
    }
    public SpritePackage_Single GetRandom() => GetRandom(out _);

    public int FindIndex(string spriteName)
    {
        for (int i = 0; i < Count; i++)
            if (package[i] != null && package[i].sprite != null && package[i].sprite.name == spriteName) return i;
        return -1;
    }

    public SpritePackage_Single Find(string spriteName) => Get(FindIndex(spriteName));
}

[Serializable]
public class SpritePackage_Multiple
{
    public Sprite East;
    public Sprite North;
    public Sprite South;

    public bool HasSprite(string spriteName) =>
        (East != null && East.name == spriteName) ||
        (North != null && North.name == spriteName) ||
        (South != null && South.name == spriteName);

    /// <summary>
    /// Sprite for a facing. West has no art of its own: it is East mirrored (flipX = true).
    /// Falls back to East when the requested sprite is missing.
    /// </summary>
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

[Serializable]
public class SpritePackage_Single
{
    public Sprite sprite;
}
#endregion

/// <summary>Tells the editor tool which sub-folder of the sprite root fills this database field.</summary>
[AttributeUsage(AttributeTargets.Field)]
public class SpriteFolderAttribute : Attribute
{
    public readonly string Folder;
    public SpriteFolderAttribute(string folder) { Folder = folder; }
}