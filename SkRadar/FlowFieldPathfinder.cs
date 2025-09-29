using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using GameHelper.RemoteObjects.States.InGameStateObjects;

namespace Radar
{
    /// <summary>
    /// Per-target reverse Dijkstra ("flow field"). Builds a compact direction grid (byte[,])
    /// that lets us reconstruct start->target paths in O(L). Based on the idea used in exCore2,
    /// adapted for GameHelper2's AreaInstance (GridWalkableData nibbles).
    /// </summary>
    internal sealed class FlowFieldPathfinder
    {
        // 8-neighborhood with orthogonal cost 10, diagonal 14 (int to avoid float heap churn).
        private static readonly (sbyte dx, sbyte dy, byte cost)[] Neigh = new (sbyte, sbyte, byte)[]
        {
            ( 1,  0, 10), (-1,  0, 10), ( 0,  1, 10), ( 0, -1, 10),
            ( 1,  1, 14), ( 1, -1, 14), (-1,  1, 14), (-1, -1, 14)
        };

        private readonly int width;
        private readonly int height;
        private readonly bool[,] walkable; // [y,x]
        private readonly ConcurrentDictionary<(int x, int y), byte[,]> dirFieldByTarget = new(); // 0=blocked, 1..8 = index into Neigh (direction to step)
        private readonly SemaphoreSlim buildSemaphore;
        private CancellationTokenSource cts = new();

        public FlowFieldPathfinder(AreaInstance area)
        {
            int maxParallelBuilds = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
            this.buildSemaphore = new SemaphoreSlim(maxParallelBuilds, maxParallelBuilds);

            // Build walkable grid from nibbles (>0 = passable)
            var bytesPerRow = Math.Max(1, area.TerrainMetadata.BytesPerRow);
            width = bytesPerRow * 2;
            height = area.GridWalkableData.Length / bytesPerRow;

            walkable = new bool[height, width];
            var data = area.GridWalkableData;

            for (int y = 0; y < height; y++)
            {
                int rowStart = y * bytesPerRow;
                for (int x = 0; x < width; x++)
                {
                    int bi = rowStart + (x >> 1);
                    if ((uint)bi >= (uint)data.Length) { continue; }
                    byte b = data[bi];
                    int val = ((x & 1) == 0) ? (b & 0xF) : ((b >> 4) & 0xF);
                    walkable[y, x] = val > 0;
                }
            }
        }

        public void CancelAll()
        {
            try { cts.Cancel(); } catch { }
            cts = new CancellationTokenSource();
        }

        public bool TryGetDirectionField((int x, int y) target, out byte[,] field)
            => dirFieldByTarget.TryGetValue(target, out field);

        /// <summary>
        /// Queue a build for a target if not already present.
        /// Returns false if already cached or build already started.
        /// </summary>
        public bool EnsureDirectionField((int x, int y) target)
        {
            if (dirFieldByTarget.ContainsKey(target)) return false;
            var token = cts.Token;

            // Reserve a placeholder so other callers don't spin up duplicate builds.
            dirFieldByTarget.TryAdd(target, null);

            Task.Run(async () =>
            {
                try
                {
                    bool acquired = false;
                    try
                    {
                        await buildSemaphore.WaitAsync(token).ConfigureAwait(false);
                        acquired = true;
                        token.ThrowIfCancellationRequested();
                        var df = BuildDirectionField(target, token);
                        if (df is null) { dirFieldByTarget.TryRemove(target, out _); return; }
                        dirFieldByTarget[target] = df;
                    }
                    finally
                    {
                        if (acquired)
                            buildSemaphore.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    dirFieldByTarget.TryRemove(target, out _);
                }
                catch
                {
                    dirFieldByTarget.TryRemove(target, out _);
                }
            }, token);

            return true;
        }

        /// <summary>
        /// Reconstruct a path using the direction field (fast).
        /// Returns false if not ready or no path.
        /// </summary>
        public bool TryGetPath((int x, int y) start, (int x, int y) target, List<Vector2> outPath, int maxLen = 16384)
        {
            if (!dirFieldByTarget.TryGetValue(target, out var df) || df is null) return false;

            int cx = start.x, cy = start.y;
            outPath.Clear();
            int steps = 0;

            if (!InBounds(cx, cy) || df[cy, cx] == 0) return false;

            // Follow directions until we hit the target.
            while ((cx != target.x || cy != target.y) && steps++ < maxLen)
            {
                byte dir = df[cy, cx];
                if (dir == 0) return false; // dead
                var (dx, dy, _) = Neigh[dir - 1];
                cx += dx; cy += dy;
                if (!InBounds(cx, cy)) return false;
                outPath.Add(new Vector2(cx, cy));
            }

            return (cx == target.x && cy == target.y);
        }

        private bool InBounds(int x, int y) => (uint)x < (uint)width && (uint)y < (uint)height;

        /// <summary>
        /// Reverse Dijkstra from the target across the passable grid, storing for each cell
        /// the best neighbor direction to move that decreases distance to the goal.
        /// </summary>
        private byte[,] BuildDirectionField((int x, int y) target, CancellationToken token)
        {
            if (!InBounds(target.x, target.y) || !walkable[target.y, target.x]) return null;

            var INF = int.MaxValue / 4;
            var dist = new int[height, width];
            var dir = new byte[height, width];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++) dist[y, x] = INF;

            var pq = new PriorityQueue<(int x, int y), int>();
            dist[target.y, target.x] = 0;
            pq.Enqueue(target, 0);

            // Relax
            var sw = Stopwatch.StartNew();
            while (pq.Count > 0)
            {
                token.ThrowIfCancellationRequested();

                var cur = pq.Dequeue();
                int cd = dist[cur.y, cur.x];

                // Explore neighbors of cur as PREDECESSORS (we want a reverse frontier)
                for (int i = 0; i < Neigh.Length; i++)
                {
                    var (dx, dy, cost) = Neigh[i];
                    int nx = cur.x + dx, ny = cur.y + dy;
                    if (!InBounds(nx, ny)) continue;
                    if (!walkable[ny, nx]) continue;

                    // Forbid cutting corners on diagonals
                    if (dx != 0 && dy != 0)
                    {
                        if (!walkable[cur.y, cur.x + dx] || !walkable[cur.y + dy, cur.x])
                            continue;
                    }

                    int nd = cd + cost;
                    if (nd < dist[ny, nx])
                    {
                        dist[ny, nx] = nd;
                        // When standing at (nx,ny), which step moves you closer to goal? -> step towards (cur)
                        // That is exactly the opposite direction of (dx,dy) from cur to nx,ny:
                        // To go from (nx,ny) to (cur.x,cur.y), we need (-dx,-dy) which corresponds to index of that vector.
                        // Find index for (-dx,-dy)
                        for (byte j = 0; j < Neigh.Length; j++)
                        {
                            if (Neigh[j].dx == (sbyte)-dx && Neigh[j].dy == (sbyte)-dy) { dir[ny, nx] = (byte)(j + 1); break; }
                        }

                        pq.Enqueue((nx, ny), nd);
                    }
                }

                // Cooperative yield-ish: break regularly to avoid long stalls if you later run inside a coroutine.
                if (sw.ElapsedMilliseconds > 12)
                {
                    // No explicit yield here; this runs on a worker Task. Reset timer.
                    sw.Restart();
                }
            }

            // Ensure target cell has a self-direction so TryGetPath can step into it cleanly.
            dir[target.y, target.x] = dir[target.y, target.x] == 0 ? (byte)1 : dir[target.y, target.x];
            return dir;
        }
    }
}
