// Put this file in any folder named "Editor" (e.g. Assets/Editor/PawnSpriteAssigner.cs).
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

public class SpriteAssignReport
{
    public string field;
    public string folder;
    public bool folderFound;
    public int count;
    public List<string> warnings = new List<string>();
}

/// <summary>
/// Fills GameDatabase from the sprite folders.
///
/// Convention
///   Assets/Sprites/Pawn/<Category>/...            unisex  (Gun, ...)  -> PackageList / PackageList_Single field
///   Assets/Sprites/Pawn/<Category>/Male/...       gendered (Hair, Head, Body, later Clothes, Armour...)
///   Assets/Sprites/Pawn/<Category>/Female/...        -> GenderedPackageList field (or any [Serializable] class
///                                                       whose PackageList fields are named like the sub-folders)
///
/// Which database field belongs to which folder:
///   - [SpriteFolder("Name")] on the field, or
///   - fallback: the field name (HairSprite -> Hair, Guns -> Gun, ...).
/// For a group field every PackageList / PackageList_Single inside it (Male, Female) is a sub-folder with that name.
/// So a new category = add one field to GameDatabase. No change to this tool.
///
/// PackageList (3 directions): sprite names must end with East / North / South,
///   e.g. Hair_01_East, Hair_01_North, Hair_01_South  (or Hair_01/East.png ...).
///   Everything before the suffix (plus its sub-folder) identifies one package. West is the mirrored East at runtime.
/// PackageList_Single: every sprite becomes one package.
/// Sliced sprite sheets are supported (all sub-sprites are used).
/// </summary>
public static class PawnSpriteAssigner
{
    public const string DefaultRoot = "Assets/Sprites/Pawn";

    private const string PrefRoot = "PawnSpriteAssigner.Root";
    private const string PrefDb = "PawnSpriteAssigner.DbGuid";
    private const string PrefAuto = "PawnSpriteAssigner.Auto";
    private const string PrefKeep = "PawnSpriteAssigner.KeepOrder";

    private static readonly Regex DirRegex =
        new Regex(@"(?:^|[ _\-\.])(east|north|south)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private enum Dir { East, North, South }

    #region Prefs
    public static string Root
    {
        get => EditorPrefs.GetString(PrefRoot, DefaultRoot);
        set => EditorPrefs.SetString(PrefRoot, value);
    }
    public static bool AutoAssign
    {
        get => EditorPrefs.GetBool(PrefAuto, false);
        set => EditorPrefs.SetBool(PrefAuto, value);
    }
    public static bool KeepOrder
    {
        get => EditorPrefs.GetBool(PrefKeep, true);
        set => EditorPrefs.SetBool(PrefKeep, value);
    }

    public static void RememberDatabase(GameDatabase db)
    {
        EditorPrefs.SetString(PrefDb, db != null ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(db)) : "");
    }

    public static GameDatabase FindDatabase()
    {
        string guid = EditorPrefs.GetString(PrefDb, "");
        if (!string.IsNullOrEmpty(guid))
        {
            var db = AssetDatabase.LoadAssetAtPath<GameDatabase>(AssetDatabase.GUIDToAssetPath(guid));
            if (db != null) return db;
        }
        var guids = AssetDatabase.FindAssets("t:GameDatabase");
        return guids.Length > 0 ? AssetDatabase.LoadAssetAtPath<GameDatabase>(AssetDatabase.GUIDToAssetPath(guids[0])) : null;
    }
    #endregion

    #region Menu
    [MenuItem("Tools/Pawn/Assign Sprites Now")]
    public static void AssignFromPrefs()
    {
        var db = FindDatabase();
        if (db == null) { Debug.LogWarning("[PawnSpriteAssigner] No GameDatabase asset found."); return; }
        var reports = Run(db, Root, KeepOrder, true);
        Debug.Log("[PawnSpriteAssigner] " + Summary(reports), db);
    }

    public static string Summary(List<SpriteAssignReport> reports)
    {
        var parts = new List<string>();
        foreach (var r in reports) parts.Add(r.folderFound ? $"{r.field}: {r.count}" : $"{r.field}: folder '{r.folder}' missing");
        return string.Join(", ", parts);
    }
    #endregion

    #region Categories
    private struct Category
    {
        public FieldInfo field;       // field of GameDatabase
        public FieldInfo sub;         // null for a plain list, else the list inside a group (Male / Female)
        public bool single;
        public string[] folderNames;  // folder of 'field' under the root
        public string[] subNames;     // folder of 'sub' under that folder
        public string Label => sub == null ? field.Name : field.Name + "." + sub.Name;
    }

    private static bool IsLeaf(Type t) => t == typeof(PackageList) || t == typeof(PackageList_Single);

    private static string[] FolderNames(FieldInfo f)
    {
        var names = new List<string>();
        var attr = f.GetCustomAttribute<SpriteFolderAttribute>();
        if (attr != null) names.Add(attr.Folder);
        else
        {
            string n = f.Name;
            names.Add(n);
            if (n.EndsWith("Sprites")) n = n.Substring(0, n.Length - 7);
            else if (n.EndsWith("Sprite")) n = n.Substring(0, n.Length - 6);
            names.Add(n);
            if (n.Length > 1 && n.EndsWith("s")) names.Add(n.Substring(0, n.Length - 1));
        }
        return names.ToArray();
    }

    private static List<Category> GetCategories()
    {
        var list = new List<Category>();
        foreach (var f in typeof(GameDatabase).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (IsLeaf(f.FieldType))
            {
                list.Add(new Category { field = f, single = f.FieldType == typeof(PackageList_Single), folderNames = FolderNames(f) });
                continue;
            }

            // group (e.g. GenderedPackageList): each list inside is a sub-folder named like its field
            foreach (var sf in f.FieldType.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!IsLeaf(sf.FieldType)) continue;
                list.Add(new Category
                {
                    field = f,
                    sub = sf,
                    single = sf.FieldType == typeof(PackageList_Single),
                    folderNames = FolderNames(f),
                    subNames = FolderNames(sf)
                });
            }
        }
        return list;
    }

    /// <summary>The PackageList / PackageList_Single behind a category. create = make missing objects.</summary>
    private static object GetLeaf(GameDatabase db, Category cat, bool create)
    {
        object top = cat.field.GetValue(db);
        if (top == null)
        {
            if (!create) return null;
            top = Activator.CreateInstance(cat.field.FieldType);
            cat.field.SetValue(db, top);
        }
        if (cat.sub == null) return top;

        object leaf = cat.sub.GetValue(top);
        if (leaf == null && create)
        {
            leaf = Activator.CreateInstance(cat.sub.FieldType);
            cat.sub.SetValue(top, leaf);
        }
        return leaf;
    }

    private static string FindFolder(string root, string[] names)
    {
        if (!AssetDatabase.IsValidFolder(root)) return null;
        foreach (var sub in AssetDatabase.GetSubFolders(root))
        {
            string leaf = Path.GetFileName(sub);
            foreach (var n in names)
                if (string.Equals(leaf, n, StringComparison.OrdinalIgnoreCase)) return sub;
        }
        return null;
    }
    #endregion

    #region Run
    /// <summary>Scan (apply = false) or scan and write (apply = true) every category of the database.</summary>
    public static List<SpriteAssignReport> Run(GameDatabase db, string root, bool keepOrder, bool apply)
    {
        var reports = new List<SpriteAssignReport>();
        if (db == null) return reports;
        root = (root ?? "").TrimEnd('/', '\\').Replace('\\', '/');

        if (apply) Undo.RecordObject(db, "Assign Pawn Sprites");

        foreach (var cat in GetCategories())
        {
            string expected = cat.sub == null ? cat.folderNames[0] : cat.folderNames[0] + "/" + cat.subNames[0];
            var report = new SpriteAssignReport { field = cat.Label, folder = expected };
            reports.Add(report);

            string folder = FindFolder(root, cat.folderNames);
            if (folder != null && cat.sub != null) folder = FindFolder(folder, cat.subNames);
            if (folder == null)
            {
                report.warnings.Add($"Folder '{root}/{expected}' not found. Field left untouched.");
                continue;
            }
            report.folderFound = true;
            report.folder = folder;

            var sprites = CollectSprites(folder);
            if (cat.single) ProcessSingle(db, cat, folder, sprites, keepOrder, apply, report);
            else ProcessMultiple(db, cat, folder, sprites, keepOrder, apply, report);
        }

        if (apply)
        {
            EditorUtility.SetDirty(db);
            AssetDatabase.SaveAssets();
        }
        return reports;
    }

    private static List<KeyValuePair<Sprite, string>> CollectSprites(string folder)
    {
        var result = new List<KeyValuePair<Sprite, string>>();
        var paths = new List<string>();
        foreach (var guid in AssetDatabase.FindAssets("t:Sprite", new[] { folder }))
        {
            string p = AssetDatabase.GUIDToAssetPath(guid);
            if (!paths.Contains(p)) paths.Add(p);
        }
        paths.Sort(NaturalCompare);
        foreach (var p in paths)
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(p))
                if (o is Sprite s) result.Add(new KeyValuePair<Sprite, string>(s, p));
        return result;
    }
    #endregion

    #region Multiple (East / North / South)
    private static void ProcessMultiple(GameDatabase db, Category cat, string folder,
        List<KeyValuePair<Sprite, string>> sprites, bool keepOrder, bool apply, SpriteAssignReport report)
    {
        var map = new Dictionary<string, SpritePackage_Multiple>();
        foreach (var kv in sprites)
        {
            string key = ComputeKey(kv.Key, kv.Value, folder, out Dir dir, out bool hasDir);
            if (!hasDir) report.warnings.Add($"'{kv.Key.name}' has no East/North/South suffix, used as East.");

            if (!map.TryGetValue(key, out var pkg)) { pkg = new SpritePackage_Multiple(); map[key] = pkg; }
            ref Sprite slot = ref Slot(pkg, dir);
            if (slot != null) { report.warnings.Add($"Duplicate {dir} for '{key}' ('{kv.Key.name}' ignored)."); continue; }
            slot = kv.Key;
        }

        foreach (var kv in map)
        {
            if (kv.Value.East == null) report.warnings.Add($"'{kv.Key}' has no East sprite.");
            else if (kv.Value.North == null || kv.Value.South == null)
                report.warnings.Add($"'{kv.Key}' misses North/South (East will be used).");
        }

        var existingKeys = new List<string>();
        var pl = GetLeaf(db, cat, false) as PackageList;
        if (pl != null && pl.package != null)
            foreach (var p in pl.package) existingKeys.Add(p == null ? null : KeyOfPackage(p, folder));

        var ordered = Order(new List<string>(map.Keys), existingKeys, keepOrder);
        var arr = new SpritePackage_Multiple[ordered.Count];
        for (int i = 0; i < arr.Length; i++) arr[i] = map[ordered[i]];

        report.count = arr.Length;
        if (!apply) return;
        if (pl == null) pl = (PackageList)GetLeaf(db, cat, true);
        pl.package = arr;
    }

    private static ref Sprite Slot(SpritePackage_Multiple p, Dir d)
    {
        switch (d)
        {
            case Dir.North: return ref p.North;
            case Dir.South: return ref p.South;
            default: return ref p.East;
        }
    }

    private static string KeyOfPackage(SpritePackage_Multiple p, string folder)
    {
        Sprite s = p.East != null ? p.East : (p.North != null ? p.North : p.South);
        if (s == null) return null;
        return ComputeKey(s, AssetDatabase.GetAssetPath(s), folder, out _, out _);
    }

    /// <summary>Package id: sub-folder (relative to the category) + sprite name without the direction suffix.</summary>
    private static string ComputeKey(Sprite s, string assetPath, string folder, out Dir dir, out bool hasDir)
    {
        string name = s.name;
        var m = DirRegex.Match(name);
        string baseName;
        dir = Dir.East;
        hasDir = m.Success;
        if (m.Success)
        {
            baseName = name.Substring(0, m.Index);
            switch (m.Groups[1].Value.ToLowerInvariant())
            {
                case "north": dir = Dir.North; break;
                case "south": dir = Dir.South; break;
            }
        }
        else baseName = name;

        string rel = RelativeDir(assetPath, folder);
        string key = rel.Length == 0 ? baseName : (baseName.Length == 0 ? rel : rel + "/" + baseName);
        return key.Length == 0 ? name : key;
    }

    private static string RelativeDir(string assetPath, string folder)
    {
        string dir = (Path.GetDirectoryName(assetPath) ?? "").Replace('\\', '/');
        if (dir.Length <= folder.Length || !dir.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return "";
        return dir.Substring(folder.Length + 1);
    }
    #endregion

    #region Single
    private static void ProcessSingle(GameDatabase db, Category cat, string folder,
        List<KeyValuePair<Sprite, string>> sprites, bool keepOrder, bool apply, SpriteAssignReport report)
    {
        var map = new Dictionary<string, SpritePackage_Single>();
        foreach (var kv in sprites)
        {
            string key = SingleKey(kv.Key, kv.Value, folder);
            if (map.ContainsKey(key)) { report.warnings.Add($"Duplicate sprite id '{key}' ignored."); continue; }
            map[key] = new SpritePackage_Single { sprite = kv.Key };
        }

        var existingKeys = new List<string>();
        var pl = GetLeaf(db, cat, false) as PackageList_Single;
        if (pl != null && pl.package != null)
            foreach (var p in pl.package)
                existingKeys.Add(p == null || p.sprite == null ? null : SingleKey(p.sprite, AssetDatabase.GetAssetPath(p.sprite), folder));

        var ordered = Order(new List<string>(map.Keys), existingKeys, keepOrder);
        var arr = new SpritePackage_Single[ordered.Count];
        for (int i = 0; i < arr.Length; i++) arr[i] = map[ordered[i]];

        report.count = arr.Length;
        if (!apply) return;
        if (pl == null) pl = (PackageList_Single)GetLeaf(db, cat, true);
        pl.package = arr;
    }

    private static string SingleKey(Sprite s, string path, string folder)
    {
        string rel = RelativeDir(path, folder);
        return rel.Length == 0 ? s.name : rel + "/" + s.name;
    }
    #endregion

    #region Ordering
    /// <summary>
    /// keepOrder: packages that already exist keep their relative order (so saved indices stay valid
    /// as long as nothing is removed) and new ones are appended. Otherwise everything is sorted naturally.
    /// </summary>
    private static List<string> Order(List<string> keys, List<string> existing, bool keepOrder)
    {
        var sorted = new List<string>(keys);
        sorted.Sort(NaturalCompare);
        if (!keepOrder) return sorted;

        var set = new HashSet<string>(keys);
        var result = new List<string>(keys.Count);
        var used = new HashSet<string>();
        foreach (var k in existing)
            if (k != null && set.Contains(k) && used.Add(k)) result.Add(k);
        foreach (var k in sorted)
            if (used.Add(k)) result.Add(k);
        return result;
    }

    public static int NaturalCompare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                string na = a.Substring(si, i - si).TrimStart('0');
                string nb = b.Substring(sj, j - sj).TrimStart('0');
                if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                int c = string.CompareOrdinal(na, nb);
                if (c != 0) return c;
            }
            else
            {
                int c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                if (c != 0) return c;
                i++; j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
    #endregion
}

public class PawnSpriteAssignerWindow : EditorWindow
{
    private GameDatabase db;
    private string root;
    private bool keepOrder, auto;
    private List<SpriteAssignReport> reports;
    private Vector2 scroll;

    [MenuItem("Tools/Pawn/Sprite Assigner")]
    public static void Open() => GetWindow<PawnSpriteAssignerWindow>("Pawn Sprites");

    private void OnEnable()
    {
        root = PawnSpriteAssigner.Root;
        keepOrder = PawnSpriteAssigner.KeepOrder;
        auto = PawnSpriteAssigner.AutoAssign;
        db = PawnSpriteAssigner.FindDatabase();
    }

    private void OnGUI()
    {
        EditorGUILayout.Space();
        EditorGUI.BeginChangeCheck();
        db = (GameDatabase)EditorGUILayout.ObjectField("Database", db, typeof(GameDatabase), false);
        if (EditorGUI.EndChangeCheck()) PawnSpriteAssigner.RememberDatabase(db);

        EditorGUILayout.BeginHorizontal();
        root = EditorGUILayout.TextField("Sprite root", root);
        if (GUILayout.Button("...", GUILayout.Width(28)))
        {
            string picked = EditorUtility.OpenFolderPanel("Sprite root", Application.dataPath, "");
            if (!string.IsNullOrEmpty(picked))
            {
                picked = picked.Replace('\\', '/');
                string data = Application.dataPath.Replace('\\', '/');
                if (picked.StartsWith(data)) root = "Assets" + picked.Substring(data.Length);
                else Debug.LogWarning("Pick a folder inside the project.");
            }
        }
        EditorGUILayout.EndHorizontal();

        keepOrder = EditorGUILayout.ToggleLeft("Keep existing order (append new variants, keeps saved indices stable)", keepOrder);
        auto = EditorGUILayout.ToggleLeft("Auto assign when sprites under the root are imported / moved / deleted", auto);

        PawnSpriteAssigner.Root = root;
        PawnSpriteAssigner.KeepOrder = keepOrder;
        PawnSpriteAssigner.AutoAssign = auto;

        EditorGUILayout.Space();
        using (new EditorGUI.DisabledScope(db == null))
        {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Scan (preview)", GUILayout.Height(26)))
                reports = PawnSpriteAssigner.Run(db, root, keepOrder, false);
            if (GUILayout.Button("Assign", GUILayout.Height(26)))
            {
                reports = PawnSpriteAssigner.Run(db, root, keepOrder, true);
                Debug.Log("[PawnSpriteAssigner] " + PawnSpriteAssigner.Summary(reports), db);
            }
            EditorGUILayout.EndHorizontal();
        }

        if (reports == null) return;
        scroll = EditorGUILayout.BeginScrollView(scroll);
        foreach (var r in reports)
        {
            EditorGUILayout.LabelField(r.folderFound ? $"{r.field}  ({r.count} packages)" : $"{r.field}  (folder missing)", EditorStyles.boldLabel);
            if (r.folderFound) EditorGUILayout.LabelField(r.folder, EditorStyles.miniLabel);
            int shown = 0;
            foreach (var w in r.warnings)
            {
                if (shown++ >= 15) { EditorGUILayout.LabelField($"... {r.warnings.Count - 15} more"); break; }
                EditorGUILayout.HelpBox(w, MessageType.Warning);
            }
            EditorGUILayout.Space();
        }
        EditorGUILayout.EndScrollView();
    }
}

/// <summary>Optional auto mode: re-assigns the database when sprites under the root change.</summary>
public class PawnSpriteAssetPostprocessor : AssetPostprocessor
{
    private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
    {
        if (!PawnSpriteAssigner.AutoAssign) return;
        string root = PawnSpriteAssigner.Root.TrimEnd('/', '\\').Replace('\\', '/') + "/";
        if (!AnyUnder(imported, root) && !AnyUnder(deleted, root) && !AnyUnder(moved, root) && !AnyUnder(movedFrom, root)) return;

        EditorApplication.delayCall -= PawnSpriteAssigner.AssignFromPrefs;
        EditorApplication.delayCall += PawnSpriteAssigner.AssignFromPrefs;
    }

    private static bool AnyUnder(string[] paths, string root)
    {
        foreach (var p in paths)
            if (p.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}