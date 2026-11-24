// Put this file in any folder named "Editor" (e.g. Assets/Editor/PawnSpriteAssigner.cs).
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

public class SpriteAssignReport
{
    public LookSlot slot;
    public string folder;
    public bool folderFound;
    public int count;
    public List<string> warnings = new List<string>();
}

/// <summary>
/// Fills GameDatabase.Hair / Head / Body from the sprite folders and keeps ids stable:
/// an existing entry is matched by its key (sub-folder + name without direction), so re-running never renumbers.
/// New variants get max id + 1. Entries whose sprites disappeared are removed (a warning lists their ids).
/// Entries created by hand (empty key) are left alone.
/// Weapons and bullets are not scanned: define them in the Inspector.
/// </summary>
public static class PawnSpriteAssigner
{
    public const string DefaultRoot = "Assets/Sprites/Pawn";
    private const string PrefRoot = "PawnSpriteAssigner.Root";
    private const string PrefDb = "PawnSpriteAssigner.DbGuid";
    private const string PrefAuto = "PawnSpriteAssigner.Auto";

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

    public static void RememberDatabase(GameDatabase db) =>
        EditorPrefs.SetString(PrefDb, db != null ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(db)) : "");

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
        Debug.Log("[PawnSpriteAssigner] " + Summary(Run(db, Root, true)), db);
    }

    public static string Summary(List<SpriteAssignReport> reports)
    {
        var parts = new List<string>();
        foreach (var r in reports) parts.Add(r.folderFound ? $"{r.slot}: {r.count}" : $"{r.slot}: folder missing");
        return string.Join(", ", parts);
    }
    #endregion

    #region Run
    public static List<SpriteAssignReport> Run(GameDatabase db, string root, bool apply)
    {
        var reports = new List<SpriteAssignReport>();
        if (db == null) return reports;
        root = (root ?? "").TrimEnd('/', '\\').Replace('\\', '/');

        if (apply) Undo.RecordObject(db, "Assign Pawn Sprites");

        foreach (LookSlot slot in Enum.GetValues(typeof(LookSlot)))
        {
            var report = new SpriteAssignReport { slot = slot, folder = root + "/" + slot };
            reports.Add(report);

            string folder = FindFolder(root, slot.ToString());
            if (folder == null)
            {
                report.warnings.Add($"Folder '{root}/{slot}' not found. List left untouched.");
                continue;
            }
            report.folderFound = true;
            report.folder = folder;

            var found = Scan(folder, report);
            Merge(db.GetList(slot), found, report, apply);
        }

        if (apply)
        {
            db.InvalidateCache();
            EditorUtility.SetDirty(db);
            AssetDatabase.SaveAssets();
        }
        return reports;
    }

    /// <summary>Group sprites into looks: key = sub-folder + name without the direction suffix.</summary>
    private static Dictionary<string, LookDef> Scan(string folder, SpriteAssignReport report)
    {
        var found = new Dictionary<string, LookDef>();

        var paths = new List<string>();
        foreach (var guid in AssetDatabase.FindAssets("t:Sprite", new[] { folder }))
        {
            string p = AssetDatabase.GUIDToAssetPath(guid);
            if (!paths.Contains(p)) paths.Add(p);
        }
        paths.Sort(NaturalCompare);

        foreach (var path in paths)
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path)) // sliced sheets: every sub-sprite
            {
                if (!(o is Sprite s)) continue;

                string rel = RelativeDir(path, folder);
                string baseName = StripDirection(s.name, out Dir dir, out bool hasDir);
                if (!hasDir) report.warnings.Add($"'{s.name}' has no East/North/South suffix, used as East.");

                string key = rel.Length == 0 ? baseName : rel + "/" + baseName;
                if (!found.TryGetValue(key, out var look))
                {
                    look = new LookDef { key = key, gender = GenderOf(rel) };
                    found[key] = look;
                }

                ref Sprite slot = ref Slot(look, dir);
                if (slot != null) { report.warnings.Add($"Duplicate {dir} for '{key}' ('{s.name}' ignored)."); continue; }
                slot = s;
            }

        foreach (var kv in found)
        {
            if (kv.Value.East == null) report.warnings.Add($"'{kv.Key}' has no East sprite.");
            else if (kv.Value.North == null || kv.Value.South == null)
                report.warnings.Add($"'{kv.Key}' misses North/South (East will be used).");
        }
        return found;
    }

    /// <summary>Update known entries in place, append new ones with fresh ids, drop vanished ones.</summary>
    private static void Merge(List<LookDef> list, Dictionary<string, LookDef> found, SpriteAssignReport report, bool apply)
    {
        var result = new List<LookDef>();
        var used = new HashSet<string>();
        int nextId = GameDatabase.NextId(list);

        foreach (var old in list)
        {
            if (old == null) continue;
            if (string.IsNullOrEmpty(old.key)) { result.Add(old); continue; } // hand-made entry

            if (found.TryGetValue(old.key, out var f))
            {
                old.gender = f.gender;
                old.East = f.East; old.North = f.North; old.South = f.South;
                result.Add(old);
                used.Add(old.key);
            }
            else report.warnings.Add($"Removed id {old.id} ('{old.key}'): its sprites no longer exist.");
        }

        var fresh = new List<string>();
        foreach (var k in found.Keys) if (!used.Contains(k)) fresh.Add(k);
        fresh.Sort(NaturalCompare);
        foreach (var k in fresh)
        {
            found[k].id = nextId++;
            result.Add(found[k]);
        }

        report.count = result.Count;
        if (!apply) return;
        list.Clear();
        list.AddRange(result);
    }
    #endregion

    #region Helpers
    private static ref Sprite Slot(LookDef p, Dir d)
    {
        switch (d)
        {
            case Dir.North: return ref p.North;
            case Dir.South: return ref p.South;
            default: return ref p.East;
        }
    }

    private static string StripDirection(string name, out Dir dir, out bool hasDir)
    {
        dir = Dir.East;
        var m = DirRegex.Match(name);
        hasDir = m.Success;
        if (!m.Success) return name;

        switch (m.Groups[1].Value.ToLowerInvariant())
        {
            case "north": dir = Dir.North; break;
            case "south": dir = Dir.South; break;
        }
        string baseName = name.Substring(0, m.Index);
        return baseName.Length == 0 ? name : baseName;
    }

    /// <summary>First sub-folder named Male / Female decides the gender, anything else is Any.</summary>
    private static LookGender GenderOf(string relativeDir)
    {
        if (relativeDir.Length == 0) return LookGender.Any;
        string first = relativeDir.Split('/')[0];
        if (string.Equals(first, "Male", StringComparison.OrdinalIgnoreCase)) return LookGender.Male;
        if (string.Equals(first, "Female", StringComparison.OrdinalIgnoreCase)) return LookGender.Female;
        return LookGender.Any;
    }

    private static string FindFolder(string root, string name)
    {
        if (!AssetDatabase.IsValidFolder(root)) return null;
        foreach (var sub in AssetDatabase.GetSubFolders(root))
            if (string.Equals(Path.GetFileName(sub), name, StringComparison.OrdinalIgnoreCase)) return sub;
        return null;
    }

    private static string RelativeDir(string assetPath, string folder)
    {
        string dir = (Path.GetDirectoryName(assetPath) ?? "").Replace('\\', '/');
        if (dir.Length <= folder.Length || !dir.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return "";
        return dir.Substring(folder.Length + 1);
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
    private bool auto;
    private List<SpriteAssignReport> reports;
    private Vector2 scroll;

    [MenuItem("Tools/Pawn/Sprite Assigner")]
    public static void Open() => GetWindow<PawnSpriteAssignerWindow>("Pawn Sprites");

    private void OnEnable()
    {
        root = PawnSpriteAssigner.Root;
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

        auto = EditorGUILayout.ToggleLeft("Auto assign when sprites under the root are imported / moved / deleted", auto);
        PawnSpriteAssigner.Root = root;
        PawnSpriteAssigner.AutoAssign = auto;

        EditorGUILayout.Space();
        using (new EditorGUI.DisabledScope(db == null))
        {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Scan (preview)", GUILayout.Height(26)))
                reports = PawnSpriteAssigner.Run(db, root, false);
            if (GUILayout.Button("Assign", GUILayout.Height(26)))
            {
                reports = PawnSpriteAssigner.Run(db, root, true);
                Debug.Log("[PawnSpriteAssigner] " + PawnSpriteAssigner.Summary(reports), db);
            }
            EditorGUILayout.EndHorizontal();
        }

        if (reports == null) return;
        scroll = EditorGUILayout.BeginScrollView(scroll);
        foreach (var r in reports)
        {
            EditorGUILayout.LabelField(r.folderFound ? $"{r.slot}  ({r.count} looks)" : $"{r.slot}  (folder missing)", EditorStyles.boldLabel);
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