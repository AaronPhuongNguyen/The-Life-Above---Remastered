using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

// ───────────────────────────── Enums & data ─────────────────────────────

public enum MapLayer : byte { Ground = 0, Floor = 1, Structure = 2, Unit = 3 }
public enum GroundType : byte { Dirt, Grass, Mud, Water, Stone }
public enum Biome : byte { Water, Shore, Grassland, Forest, Dryland, Mountain }

[Serializable]
public class NoiseSettings
{
    [Min(1f)] public float scale = 40f;
    [Range(1, 8)] public int octaves = 4;
    [Range(0f, 1f)] public float persistence = 0.5f;
    [Min(1f)] public float lacunarity = 2f;
}

[Serializable]
public class RiverSettings
{
    public bool enabled = true;
    [Min(0)] public int count = 3;
    [Min(1)] public int maxLength = 400;
    [Range(0, 3)] public int radius = 1;
    [Tooltip("Rivers start on cells with height above this.")]
    [Range(0f, 1f)] public float sourceMinHeight = 0.6f;
    [Tooltip("0 = always flow downhill, 1 = very wobbly.")]
    [Range(0f, 1f)] public float wobble = 0.25f;
}

[Serializable]
public class GroundTileSet
{
    public GroundType type;
    public TileBase[] tiles;
}

[Serializable]
public class NatureDef
{
    public string name = "Tree";
    public GameObject prefab;
    public MapLayer layer = MapLayer.Structure;
    public Biome[] biomes;
    public GroundType[] grounds = { GroundType.Grass, GroundType.Dirt };
    [Range(0f, 1f)] public float density = 0.1f;
    public Vector2 scaleRange = Vector2.one;
    public bool blocksView;
    public bool blocksPath;
}

[Serializable]
public class MapSettings
{
    [Header("Seed & Size")]
    public bool randomSeed = true;
    public uint seed = 12345;
    public Vector2Int size = new Vector2Int(400, 400);
    public float cellSize = 1f;

    [Header("Height noise")]
    public NoiseSettings height = new NoiseSettings();
    [Range(0f, 1f)] public float islandFalloff = 0.0f;

    [Header("Moisture noise")]
    public NoiseSettings moisture = new NoiseSettings { scale = 60f, octaves = 3 };

    [Header("Thresholds (height 0..1)")]
    [Range(0f, 1f)] public float waterLevel = 0.30f;
    [Range(0f, 1f)] public float mudLevel = 0.36f;
    [Range(0f, 1f)] public float stoneLevel = 0.72f;
    [Tooltip("Stone above this blocks view and path (mountain wall).")]
    [Range(0f, 1f)] public float mountainLevel = 0.85f;
    [Range(0f, 1f)] public float grassMoisture = 0.45f;
    [Range(0f, 1f)] public float forestMoisture = 0.62f;

    [Header("Rivers")]
    public RiverSettings river = new RiverSettings();

    [Header("Assets")]
    [Tooltip("TerrainGenerator: tiles per ground type.")]
    public GroundTileSet[] tileSets;
    [Tooltip("NatureGeneration: trees, bushes, rocks...")]
    public NatureDef[] nature;
}

/// <summary>One grid cell. Layers are independent and can share a coordinate.</summary>
public class MapCell
{
    public Vector2Int pos;
    public float height;
    public float moisture;
    public GroundType ground;
    public Biome biome;
    public float speedPenalty = 0.87f;   // speed multiplier (1 = full speed)
    public byte occupiedMask;            // bit per MapLayer
    public byte blockViewMask;           // bit per MapLayer
    public byte blockPathMask;           // bit per MapLayer

    public bool BlocksView => blockViewMask != 0;
    public bool BlocksPath => blockPathMask != 0;
    public bool IsOccupied(MapLayer l) => (occupiedMask & (1 << (int)l)) != 0;
}

// ───────────────────────────── Manager ─────────────────────────────

[DefaultExecutionOrder(-99)]
public class MapManager : MonoBehaviour
{
    #region Singleton
    public static MapManager Instance { get; private set; }

    public void Awake()
    {
        DoSingleton();
    }

    private void DoSingleton()
    {
        // Map lives in the scene only; reloading the scene wipes it, so no DontDestroyOnLoad.
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }
    #endregion

    #region Properties and Caches
    public MapSettings Setting = new MapSettings();

    [Header("Scene references")]
    [SerializeField] private Tilemap groundTilemap;
    [SerializeField] private Transform natureRoot;
    [SerializeField] private bool generateOnStart = true;

    [Header("Loading budget")]
    [Tooltip("Max milliseconds of generation work per frame.")]
    [SerializeField, Min(1f)] private float budgetMs = 8f;

    public event Action OnMapGenerated;
    public bool IsGenerated { get; private set; }

    private readonly Dictionary<Vector2Int, MapCell> _cells = new Dictionary<Vector2Int, MapCell>();
    private readonly Dictionary<Vector2Int, GameObject>[] _objects = new Dictionary<Vector2Int, GameObject>[4];

    private Vector2 _heightOffset, _moistureOffset;
    private long _sliceStart;
    private long _budgetTicks;

    private static readonly Vector2Int[] Dir8 =
    {
        new Vector2Int(1,0), new Vector2Int(-1,0), new Vector2Int(0,1), new Vector2Int(0,-1),
        new Vector2Int(1,1), new Vector2Int(1,-1), new Vector2Int(-1,1), new Vector2Int(-1,-1)
    };

    public int Width => Setting.size.x;
    public int Height => Setting.size.y;
    public IReadOnlyDictionary<Vector2Int, MapCell> Cells => _cells;

    private void Start()
    {
        if (generateOnStart) GenerateMap();
    }
    #endregion

    #region Main Function
    /// <summary>
    /// Full generation: Grid -> Terrain -> Nature. Runs over several frames and reports steps to
    /// LoadingManager (if one exists) so the loading screen waits for it.
    /// </summary>
    public void GenerateMap()
    {
        StartCoroutine(GenerateRoutine());
    }

    public IEnumerator GenerateRoutine()
    {
        Prepare();
        yield return GridRoutine();
        yield return TerrainRoutine();
        yield return NatureRoutine();
        IsGenerated = true;
        OnMapGenerated?.Invoke();
    }

    /// <summary>
    /// Generate the grid base: per cell height, moisture, ground type, SpeedPenalty, block view/path.
    /// Data is a Dictionary&lt;Vector2Int, MapCell&gt; and each layer (Ground, Floor, Structure, Unit)
    /// is independent, so objects of different layers can share one coordinate.
    /// Speed penalty defaults: dirt/grass/stone 87%, water 20%, mud 40%, floor always 100%.
    /// Synchronous version: finishes in one call (no frame budget).
    /// </summary>
    public void GenerateGrid() { Prepare(); Drain(GridRoutine()); }

    /// <summary>Fill the grid with tiles of the matching type (always the Ground layer). Needs GenerateGrid first.</summary>
    public void GenerateTerrain() { Drain(TerrainRoutine()); }

    /// <summary>Spawn biome-related nature prefabs, snapped to cells, never overlapping an occupied layer.</summary>
    public void GenerateNature() { Drain(NatureRoutine()); }

    private static void Drain(IEnumerator e) { while (e.MoveNext()) { } }

    private void Prepare()
    {
        if (Setting.randomSeed) Setting.seed = (uint)Environment.TickCount;
        RNG.SetSeed(Setting.seed);

        _heightOffset = new Vector2(RNG.GetFloat(-10000f, 10000f), RNG.GetFloat(-10000f, 10000f));
        _moistureOffset = new Vector2(RNG.GetFloat(-10000f, 10000f), RNG.GetFloat(-10000f, 10000f));

        _budgetTicks = (long)(budgetMs * System.Diagnostics.Stopwatch.Frequency / 1000.0);
        ClearMap();
    }

    private void ClearMap()
    {
        IsGenerated = false;
        _cells.Clear();
        for (int i = 0; i < _objects.Length; i++) _objects[i] = new Dictionary<Vector2Int, GameObject>();
        if (groundTilemap != null) groundTilemap.ClearAllTiles();
        if (natureRoot != null)
            for (int i = natureRoot.childCount - 1; i >= 0; i--) Destroy(natureRoot.GetChild(i).gameObject);
    }

    // ---- frame budget (Stopwatch.Frequency based) ----
    private void StartSlice() => _sliceStart = System.Diagnostics.Stopwatch.GetTimestamp();
    private bool SliceExpired() => System.Diagnostics.Stopwatch.GetTimestamp() - _sliceStart >= _budgetTicks;

    // ---- 1. GRID ----
    private IEnumerator GridRoutine()
    {
        int w = Width, h = Height;
        int riverCount = Setting.river.enabled ? Setting.river.count : 0;
        LoadingManager.ReportTotal(h + riverCount, "Grid");
        StartSlice();

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                var c = new MapCell { pos = new Vector2Int(x, y) };
                float hv = Fbm(x, y, Setting.height, _heightOffset);
                if (Setting.islandFalloff > 0f)
                {
                    float dx = (x / (float)(w - 1)) * 2f - 1f;
                    float dy = (y / (float)(h - 1)) * 2f - 1f;
                    float d = Mathf.Clamp01(Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy)));
                    hv = Mathf.Clamp01(hv - Setting.islandFalloff * d * d * d);
                }
                c.height = hv;
                c.moisture = Fbm(x, y, Setting.moisture, _moistureOffset);
                c.ground = ClassifyGround(c);
                ApplyGroundProperties(c);
                _cells[c.pos] = c;
            }
            LoadingManager.ReportStep("Grid");
            if (SliceExpired()) { yield return null; StartSlice(); }
        }

        for (int r = 0; r < riverCount; r++)
        {
            CarveRiver();
            LoadingManager.ReportStep("Rivers");
            if (SliceExpired()) { yield return null; StartSlice(); }
        }
    }

    private float Fbm(int x, int y, NoiseSettings n, Vector2 offset)
    {
        float amp = 1f, freq = 1f, sum = 0f, norm = 0f;
        for (int o = 0; o < n.octaves; o++)
        {
            float sx = x / n.scale * freq + offset.x + o * 17.3f;
            float sy = y / n.scale * freq + offset.y + o * 31.7f;
            sum += Mathf.PerlinNoise(sx, sy) * amp;
            norm += amp;
            amp *= n.persistence;
            freq *= n.lacunarity;
        }
        return Mathf.Clamp01(sum / norm);
    }

    private GroundType ClassifyGround(MapCell c)
    {
        if (c.height < Setting.waterLevel) return GroundType.Water;
        if (c.height < Setting.mudLevel) return GroundType.Mud;
        if (c.height >= Setting.stoneLevel) return GroundType.Stone;
        return c.moisture >= Setting.grassMoisture ? GroundType.Grass : GroundType.Dirt;
    }

    private void ApplyGroundProperties(MapCell c)
    {
        switch (c.ground)
        {
            case GroundType.Water: c.speedPenalty = 0.20f; break;
            case GroundType.Mud: c.speedPenalty = 0.40f; break;
            default: c.speedPenalty = 0.87f; break; // dirt, grass, stone
        }

        const byte groundBit = 1 << (int)MapLayer.Ground;
        bool mountain = c.ground == GroundType.Stone && c.height >= Setting.mountainLevel;
        // ground layer bits are fully owned by the ground type
        c.blockViewMask = (byte)(mountain ? (c.blockViewMask | groundBit) : (c.blockViewMask & ~groundBit));
        c.blockPathMask = (byte)(mountain ? (c.blockPathMask | groundBit) : (c.blockPathMask & ~groundBit));
        c.occupiedMask |= groundBit; // ground always exists
    }

    private void CarveRiver()
    {
        var rs = Setting.river;
        int w = Width, h = Height;

        // pick a high source
        Vector2Int cur = default;
        bool found = false;
        for (int i = 0; i < 60 && !found; i++)
        {
            var p = new Vector2Int(RNG.GetInt(0, w), RNG.GetInt(0, h));
            if (_cells[p].height >= rs.sourceMinHeight && _cells[p].ground != GroundType.Water) { cur = p; found = true; }
        }
        if (!found) return;

        var visited = new HashSet<Vector2Int>();
        for (int step = 0; step < rs.maxLength; step++)
        {
            visited.Add(cur);
            bool reachedWater = _cells[cur].ground == GroundType.Water && step > 0 && _cells[cur].height < Setting.waterLevel;
            if (reachedWater) return;

            PaintRiver(cur, rs.radius);

            // choose next: lowest unvisited neighbour, sometimes a random one (wobble)
            Vector2Int best = cur;
            float bestH = float.MaxValue;
            bool any = false;
            for (int d = 0; d < Dir8.Length; d++)
            {
                var n = cur + Dir8[d];
                if (!InBounds(n) || visited.Contains(n)) continue;
                float hv = _cells[n].height + RNG.GetFloat(0f, rs.wobble * 0.1f);
                if (hv < bestH) { bestH = hv; best = n; any = true; }
            }
            if (!any) return;
            cur = best;
            if (cur.x == 0 || cur.y == 0 || cur.x == w - 1 || cur.y == h - 1) { PaintRiver(cur, rs.radius); return; }
        }
    }

    private void PaintRiver(Vector2Int centre, int radius)
    {
        for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                if (dx * dx + dy * dy > radius * radius + 1) continue;
                var p = new Vector2Int(centre.x + dx, centre.y + dy);
                if (!_cells.TryGetValue(p, out var c)) continue;
                c.ground = GroundType.Water;
                c.height = Mathf.Min(c.height, Setting.waterLevel - 0.01f);
                ApplyGroundProperties(c);
            }
    }

    // ---- 2. TERRAIN ----
    private IEnumerator TerrainRoutine()
    {
        int w = Width, h = Height;
        LoadingManager.ReportTotal(h, "Terrain");
        StartSlice();

        var lookup = new Dictionary<GroundType, TileBase[]>();
        if (Setting.tileSets != null)
            foreach (var ts in Setting.tileSets)
                if (ts != null && ts.tiles != null && ts.tiles.Length > 0) lookup[ts.type] = ts.tiles;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                var c = _cells[new Vector2Int(x, y)];
                c.biome = ComputeBiome(c);

                if (groundTilemap != null && lookup.TryGetValue(c.ground, out var tiles))
                {
                    var tile = tiles[tiles.Length == 1 ? 0 : RNG.GetInt(0, tiles.Length)];
                    Vector2 wp = CellToWorld(c.pos);
                    groundTilemap.SetTile(groundTilemap.WorldToCell(new Vector3(wp.x, wp.y, 0f)), tile);
                }
            }
            LoadingManager.ReportStep("Terrain");
            if (SliceExpired()) { yield return null; StartSlice(); }
        }
    }

    private Biome ComputeBiome(MapCell c)
    {
        switch (c.ground)
        {
            case GroundType.Water: return Biome.Water;
            case GroundType.Mud: return Biome.Shore;
            case GroundType.Stone: return Biome.Mountain;
        }
        if (c.moisture >= Setting.forestMoisture) return Biome.Forest;
        return c.ground == GroundType.Grass ? Biome.Grassland : Biome.Dryland;
    }

    // ---- 3. NATURE ----
    private IEnumerator NatureRoutine()
    {
        int w = Width, h = Height;
        LoadingManager.ReportTotal(h, "Nature");
        StartSlice();

        if (natureRoot == null)
        {
            var go = new GameObject("Nature");
            go.transform.SetParent(transform, false);
            natureRoot = go.transform;
        }

        var defs = Setting.nature;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (defs == null) break;
                var c = _cells[new Vector2Int(x, y)];
                for (int i = 0; i < defs.Length; i++)
                {
                    var d = defs[i];
                    if (d == null || d.prefab == null) continue;
                    if (!Contains(d.biomes, c.biome) || !Contains(d.grounds, c.ground)) continue;
                    if (RNG.GetPercent() >= d.density) continue;
                    if (c.IsOccupied(d.layer) && d.layer != MapLayer.Ground) continue; // layer taken -> skip
                    if (d.layer == MapLayer.Ground) continue; // ground is owned by terrain

                    var obj = Instantiate(d.prefab, CellToWorld(c.pos), Quaternion.identity, natureRoot);
                    float s = Mathf.Approximately(d.scaleRange.x, d.scaleRange.y) ? d.scaleRange.x : RNG.GetFloat(d.scaleRange.x, d.scaleRange.y);
                    if (s > 0f) obj.transform.localScale *= s;
                    Register(c, d.layer, obj, d.blocksView, d.blocksPath);
                    break; // one nature object per cell
                }
            }
            LoadingManager.ReportStep("Nature");
            if (SliceExpired()) { yield return null; StartSlice(); }
        }
    }

    private static bool Contains<T>(T[] arr, T value)
    {
        if (arr == null || arr.Length == 0) return true; // empty = any
        return Array.IndexOf(arr, value) >= 0;
    }
    #endregion

    #region Helpers
    public bool InBounds(Vector2Int p) => p.x >= 0 && p.y >= 0 && p.x < Width && p.y < Height;

    public Vector2Int ClampToMap(Vector2Int p) =>
        new Vector2Int(Mathf.Clamp(p.x, 0, Width - 1), Mathf.Clamp(p.y, 0, Height - 1));

    /// <summary>Clamp a world position inside the map bounds.</summary>
    public Vector2 ClampToMap(Vector2 world)
    {
        Vector2 half = HalfExtent;
        return new Vector2(Mathf.Clamp(world.x, -half.x, half.x), Mathf.Clamp(world.y, -half.y, half.y));
    }

    /// <summary>Half of the map size in world units. The map is centred on Vector2.zero.</summary>
    public Vector2 HalfExtent => new Vector2(Width, Height) * (Setting.cellSize * 0.5f);

    /// <summary>World bounds of the map (centre = Vector2.zero).</summary>
    public Rect WorldBounds => new Rect(-HalfExtent, HalfExtent * 2f);

    /// <summary>World position of a cell centre. Cell (0,0) is the bottom-left; the map centre is the world origin.</summary>
    public Vector2 CellToWorld(Vector2Int p) =>
        new Vector2((p.x + 0.5f) * Setting.cellSize, (p.y + 0.5f) * Setting.cellSize) - HalfExtent;

    public Vector2Int WorldToCell(Vector2 world)
    {
        Vector2 l = (world + HalfExtent) / Setting.cellSize;
        return new Vector2Int(Mathf.FloorToInt(l.x), Mathf.FloorToInt(l.y));
    }

    public bool TryGetCell(Vector2Int p, out MapCell cell) => _cells.TryGetValue(p, out cell);
    public MapCell GetCell(Vector2Int p) => _cells.TryGetValue(p, out var c) ? c : null;

    /// <summary>
    /// Speed multiplier of the cell. A Floor on the cell always gives 100%.
    /// Pawns should read this only when they step into a new cell (keep the previous value until then).
    /// </summary>
    public float GetSpeedPenalty(Vector2Int p)
    {
        if (!_cells.TryGetValue(p, out var c)) return 1f;
        return c.IsOccupied(MapLayer.Floor) ? 1f : c.speedPenalty;
    }

    public float GetSpeedPenalty(Vector2 world) => GetSpeedPenalty(WorldToCell(world));

    public bool BlocksView(Vector2Int p) => !_cells.TryGetValue(p, out var c) || c.BlocksView;
    public bool BlocksPath(Vector2Int p) => !_cells.TryGetValue(p, out var c) || c.BlocksPath;
    public bool IsWalkable(Vector2Int p) => !BlocksPath(p);

    /// <summary>True if that layer is already taken at p. Out of bounds counts as occupied.</summary>
    public bool IsOccupied(Vector2Int p, MapLayer layer) => !_cells.TryGetValue(p, out var c) || c.IsOccupied(layer);

    /// <summary>Strict check: building is allowed only if the layer is free (and in bounds).</summary>
    public bool CanBuild(Vector2Int p, MapLayer layer) =>
        layer != MapLayer.Ground && _cells.TryGetValue(p, out var c) && !c.IsOccupied(layer);

    /// <summary>Place an existing object on a layer. Fails if the layer is occupied.</summary>
    public bool TryPlace(Vector2Int p, MapLayer layer, GameObject obj, bool blocksView = false, bool blocksPath = false)
    {
        if (!CanBuild(p, layer)) return false;
        Register(_cells[p], layer, obj, blocksView, blocksPath);
        return true;
    }

    /// <summary>Free a layer (optionally destroying its object) so something new can be placed.</summary>
    public bool Release(Vector2Int p, MapLayer layer, bool destroyObject = true)
    {
        if (layer == MapLayer.Ground || !_cells.TryGetValue(p, out var c) || !c.IsOccupied(layer)) return false;
        byte bit = (byte)(1 << (int)layer);
        c.occupiedMask &= (byte)~bit;
        c.blockViewMask &= (byte)~bit;
        c.blockPathMask &= (byte)~bit;
        var dict = _objects[(int)layer];
        if (dict.TryGetValue(p, out var go))
        {
            dict.Remove(p);
            if (destroyObject && go != null) Destroy(go);
        }
        return true;
    }

    public GameObject GetObject(Vector2Int p, MapLayer layer) =>
        _objects[(int)layer].TryGetValue(p, out var go) ? go : null;

    /// <summary>Manually toggle 'isOccupied' of a layer without destroying anything.</summary>
    public void SetOccupied(Vector2Int p, MapLayer layer, bool occupied)
    {
        if (!_cells.TryGetValue(p, out var c)) return;
        byte bit = (byte)(1 << (int)layer);
        c.occupiedMask = (byte)(occupied ? (c.occupiedMask | bit) : (c.occupiedMask & ~bit));
    }

    private void Register(MapCell c, MapLayer layer, GameObject obj, bool blocksView, bool blocksPath)
    {
        byte bit = (byte)(1 << (int)layer);
        c.occupiedMask |= bit;
        if (blocksView) c.blockViewMask |= bit;
        if (blocksPath) c.blockPathMask |= bit;
        _objects[(int)layer][c.pos] = obj;
    }

    /// <summary>Random walkable cell (uses RNG, so it is seed-deterministic).</summary>
    public bool TryGetRandomWalkable(out Vector2Int result, int attempts = 100)
    {
        for (int i = 0; i < attempts; i++)
        {
            var p = new Vector2Int(RNG.GetInt(0, Width), RNG.GetInt(0, Height));
            if (_cells.TryGetValue(p, out var c) && !c.BlocksPath && c.ground != GroundType.Water) { result = p; return true; }
        }
        result = default;
        return false;
    }
    #endregion
}