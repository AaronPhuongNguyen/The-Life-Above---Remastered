using System;
using System.Collections.Generic;
using UnityEngine;

public enum PawnPathState : byte { Idle, Moving, Arrived, Failed }

[Serializable]
public struct PathOptions
{
    public bool allowDiagonal;
    public bool cutCorners;       // false = diagonal steps need both side cells walkable
    public bool avoidWater;       // treat Water ground as blocked
    public bool allowPartial;     // when the goal is unreachable return the path to the closest explored cell
    [Range(0f, 1f)] public float terrainWeight; // 0 = shortest distance, 1 = fastest travel time (avoids mud/water)
    public int maxNodes;          // search budget (expanded nodes)
}

/// <summary>
/// Grid A* on top of MapManager. Static + pooled buffers: zero allocation per search after warm-up.
/// Cost of a step = distance / speedPenalty (so mud and water are naturally avoided), blended by terrainWeight.
/// Single-threaded: call from the main thread only.
/// </summary>
public static class GridPathfinder
{
    private const float Sqrt2 = 1.41421356f;

    // first 4 orthogonal, last 4 diagonal
    private static readonly Vector2Int[] Dirs =
    {
        new Vector2Int(1,0), new Vector2Int(-1,0), new Vector2Int(0,1), new Vector2Int(0,-1),
        new Vector2Int(1,1), new Vector2Int(1,-1), new Vector2Int(-1,1), new Vector2Int(-1,-1)
    };

    private static int[] seen, done, parent;
    private static float[] g;
    private static int stamp, cap, mapW;

    private static int[] heapNode = new int[256];
    private static float[] heapF = new float[256];
    private static int hCount;

    private static readonly List<Vector2Int> tmp = new List<Vector2Int>(64);

    public static bool IsPassable(MapManager map, Vector2Int p, PathOptions o)
    {
        if (!map.TryGetCell(p, out var c)) return false;
        if (c.BlocksPath) return false;
        if (o.avoidWater && c.ground == GroundType.Water) return false;
        return true;
    }

    private static void EnsureCapacity(MapManager map)
    {
        int n = map.Width * map.Height;
        if (n != cap || map.Width != mapW)
        {
            seen = new int[n]; done = new int[n]; parent = new int[n]; g = new float[n];
            cap = n; mapW = map.Width; stamp = 0;
        }
        stamp++;
        if (stamp == int.MaxValue)
        {
            Array.Clear(seen, 0, seen.Length);
            Array.Clear(done, 0, done.Length);
            stamp = 1;
        }
    }

    private static float H(Vector2Int a, Vector2Int b, PathOptions o)
    {
        float dx = Mathf.Abs(a.x - b.x), dy = Mathf.Abs(a.y - b.y);
        if (!o.allowDiagonal) return dx + dy;
        return Mathf.Max(dx, dy) + (Sqrt2 - 1f) * Mathf.Min(dx, dy); // octile
    }

    /// <summary>Find a path. result = cells from start to goal (inclusive). Partial paths end before the goal.</summary>
    public static bool FindPath(MapManager map, Vector2Int start, Vector2Int goal, PathOptions o, List<Vector2Int> result)
    {
        result.Clear();
        if (map == null || !map.InBounds(start) || !map.InBounds(goal)) return false;
        if (start == goal) { result.Add(start); return true; }
        if (!IsPassable(map, goal, o)) return false;

        EnsureCapacity(map);
        int w = map.Width;
        int s = start.y * w + start.x, t = goal.y * w + goal.x;

        hCount = 0;
        g[s] = 0f; parent[s] = -1; seen[s] = stamp;
        float bestH = H(start, goal, o);
        int best = s;
        Push(s, bestH);

        int expanded = 0;
        int maxNodes = Mathf.Max(1, o.maxNodes);
        int dirCount = o.allowDiagonal ? 8 : 4;

        while (hCount > 0)
        {
            int cur = Pop();
            if (done[cur] == stamp) continue;
            done[cur] = stamp;

            if (cur == t) { Build(t, result); return true; }
            if (++expanded > maxNodes) break;

            var cp = new Vector2Int(cur % w, cur / w);
            float ch = H(cp, goal, o);
            if (ch < bestH) { bestH = ch; best = cur; }
            float curPen = map.GetSpeedPenalty(cp);

            for (int d = 0; d < dirCount; d++)
            {
                var dir = Dirs[d];
                var np = new Vector2Int(cp.x + dir.x, cp.y + dir.y);
                if (!map.InBounds(np)) continue;
                int ni = np.y * w + np.x;
                if (done[ni] == stamp) continue;
                if (!IsPassable(map, np, o)) continue;

                bool diag = d >= 4;
                if (diag && !o.cutCorners)
                {
                    if (!IsPassable(map, new Vector2Int(cp.x + dir.x, cp.y), o) ||
                        !IsPassable(map, new Vector2Int(cp.x, cp.y + dir.y), o)) continue;
                }

                float pen = Mathf.Max(0.05f, (curPen + map.GetSpeedPenalty(np)) * 0.5f);
                float step = diag ? Sqrt2 : 1f;
                float cost = step * (1f + o.terrainWeight * (1f / pen - 1f));
                float tg = g[cur] + cost;

                if (seen[ni] == stamp && tg >= g[ni]) continue;
                seen[ni] = stamp; g[ni] = tg; parent[ni] = cur;
                Push(ni, tg + H(np, goal, o));
            }
        }

        if (o.allowPartial && best != s) { Build(best, result); return true; }
        return false;
    }

    private static void Build(int endIdx, List<Vector2Int> result)
    {
        int w = mapW;
        for (int i = endIdx; i != -1; i = parent[i]) result.Add(new Vector2Int(i % w, i / w));
        result.Reverse();
    }

    /// <summary>Spiral outwards to find the closest passable cell. Returns p itself if already passable.</summary>
    public static bool FindNearestPassable(MapManager map, Vector2Int p, PathOptions o, int maxRadius, out Vector2Int found)
    {
        if (IsPassable(map, p, o)) { found = p; return true; }
        for (int r = 1; r <= maxRadius; r++)
        {
            bool any = false;
            float bestSqr = float.MaxValue;
            found = p;
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy)) != r) continue; // ring only
                    var c = new Vector2Int(p.x + dx, p.y + dy);
                    if (!IsPassable(map, c, o)) continue;
                    float sq = dx * dx + dy * dy;
                    if (sq < bestSqr) { bestSqr = sq; found = c; any = true; }
                }
            if (any) return true;
        }
        found = p;
        return false;
    }

    /// <summary>Straight line check between two cells (Bresenham). Rejects blocked cells, corner squeezing and slower terrain than the endpoints.</summary>
    public static bool LineClear(MapManager map, Vector2Int a, Vector2Int b, PathOptions o)
    {
        float minPen = Mathf.Min(map.GetSpeedPenalty(a), map.GetSpeedPenalty(b)) - 0.001f;
        int x0 = a.x, y0 = a.y, x1 = b.x, y1 = b.y;
        int dx = Mathf.Abs(x1 - x0), dy = Mathf.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;

        while (true)
        {
            var c = new Vector2Int(x0, y0);
            if (!IsPassable(map, c, o) || map.GetSpeedPenalty(c) < minPen) return false;
            if (x0 == x1 && y0 == y1) return true;

            int e2 = 2 * err;
            int nx = x0, ny = y0;
            bool mx = false, my = false;
            if (e2 > -dy) { err -= dy; nx += sx; mx = true; }
            if (e2 < dx) { err += dx; ny += sy; my = true; }

            if (mx && my && !o.cutCorners)
            {
                if (!IsPassable(map, new Vector2Int(nx, y0), o) || !IsPassable(map, new Vector2Int(x0, ny), o)) return false;
            }
            x0 = nx; y0 = ny;
        }
    }

    /// <summary>String-pulling: removes waypoints that have a clear line between their neighbours.</summary>
    public static void Smooth(MapManager map, List<Vector2Int> path, PathOptions o)
    {
        if (!o.allowDiagonal || path.Count < 3) return;
        tmp.Clear();
        tmp.Add(path[0]);
        int anchor = 0;
        for (int i = 2; i < path.Count; i++)
        {
            if (!LineClear(map, path[anchor], path[i], o))
            {
                tmp.Add(path[i - 1]);
                anchor = i - 1;
            }
        }
        tmp.Add(path[path.Count - 1]);
        path.Clear();
        path.AddRange(tmp);
    }

    // ---- binary min-heap (lazy deletion, duplicates allowed) ----
    private static void Push(int node, float f)
    {
        if (hCount == heapNode.Length)
        {
            Array.Resize(ref heapNode, hCount * 2);
            Array.Resize(ref heapF, hCount * 2);
        }
        int i = hCount++;
        while (i > 0)
        {
            int p = (i - 1) >> 1;
            if (heapF[p] <= f) break;
            heapNode[i] = heapNode[p]; heapF[i] = heapF[p];
            i = p;
        }
        heapNode[i] = node; heapF[i] = f;
    }

    private static int Pop()
    {
        int top = heapNode[0];
        hCount--;
        if (hCount > 0)
        {
            int node = heapNode[hCount];
            float nf = heapF[hCount];
            int i = 0;
            while (true)
            {
                int l = 2 * i + 1;
                if (l >= hCount) break;
                int r = l + 1;
                int c = (r < hCount && heapF[r] < heapF[l]) ? r : l;
                if (heapF[c] >= nf) break;
                heapNode[i] = heapNode[c]; heapF[i] = heapF[c];
                i = c;
            }
            heapNode[i] = node; heapF[i] = nf;
        }
        return top;
    }
}