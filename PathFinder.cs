namespace OriathHub.Plugins.Radar
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;
    using System.Threading;

    /// <summary>
    /// Grid-cell-resolution A* pathfinder for the radar POI path feature.
    /// Searches directly on the game's per-cell walkable data — no tile aggregation, no
    /// walkability guessing — then string-pulls the result into the geometrically shortest
    /// navigable path.
    /// </summary>
    internal static class PathFinder
    {
        private const int TileSize = 23; // grid cells per tile, used only to derive cell bounds

        // Upper bound on nodes expanded per query. Generous enough for long dungeon corridors,
        // but bounds runaway searches on open or disconnected maps.
        private const int MaxExpanded = 250_000;

        // Spiral search radius (in cells) used to snap a start/goal that lands on a non-walkable
        // cell onto the nearest walkable one.
        private const int SnapRadius = TileSize; // up to one tile away

        private const float Sqrt2 = 1.41421356f;

        /// <summary>
        /// Finds the shortest navigable path from <paramref name="startGrid"/> to
        /// <paramref name="goalGrid"/> at single-cell resolution. Returns grid-space waypoints
        /// after string pulling, or <c>null</c> when no path exists or the search was cancelled.
        /// Call via <c>Task.Run</c> — synchronous but potentially slow.
        /// </summary>
        internal static List<Vector2>? FindPath(
            byte[] walkableData,
            int bytesPerRow,
            Vector2 startGrid,
            Vector2 goalGrid,
            CancellationToken ct)
        {
            if (walkableData.Length == 0 || bytesPerRow <= 0)
                return null;

            // Cell grid dimensions, derived exactly as MapEdgeDetector does: two cells per byte
            // across, data.Length / bytesPerRow rows down. Avoids assuming tilesX*23 matches the
            // (possibly padded) row stride.
            int cellsX = bytesPerRow * 2;
            int cellsY = walkableData.Length / bytesPerRow;

            int startX = Math.Clamp((int)startGrid.X, 0, cellsX - 1);
            int startY = Math.Clamp((int)startGrid.Y, 0, cellsY - 1);
            int goalX = Math.Clamp((int)goalGrid.X, 0, cellsX - 1);
            int goalY = Math.Clamp((int)goalGrid.Y, 0, cellsY - 1);

            // A POI (or the player) can sit on a non-walkable cell (a wall texture, an edge).
            // Snap to the nearest walkable cell so the search has a valid endpoint.
            if (!IsCellWalkable(walkableData, bytesPerRow, startX, startY) &&
                !TrySnapToWalkable(walkableData, bytesPerRow, cellsX, cellsY, ref startX, ref startY))
                return null;
            if (!IsCellWalkable(walkableData, bytesPerRow, goalX, goalY) &&
                !TrySnapToWalkable(walkableData, bytesPerRow, cellsX, cellsY, ref goalX, ref goalY))
                return null;

            if (startX == goalX && startY == goalY)
                return [new Vector2(goalX, goalY)];

            var openSet = new PriorityQueue<(int x, int y), float>();
            var gScore = new Dictionary<(int, int), float>();
            var cameFrom = new Dictionary<(int, int), (int, int)>();
            var closedSet = new HashSet<(int, int)>();

            var start = (startX, startY);
            var goal = (goalX, goalY);

            gScore[start] = 0f;
            openSet.Enqueue(start, Heuristic(startX, startY, goalX, goalY));

            while (openSet.Count > 0)
            {
                if (ct.IsCancellationRequested || closedSet.Count > MaxExpanded)
                    return null;

                var current = openSet.Dequeue();
                if (closedSet.Contains(current))
                    continue;
                closedSet.Add(current);

                if (current == goal)
                {
                    var raw = ReconstructPath(cameFrom, current);
                    return ct.IsCancellationRequested ? null : StringPull(raw, walkableData, bytesPerRow);
                }

                float currentG = gScore[current];

                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;

                        int nx = current.x + dx;
                        int ny = current.y + dy;

                        if ((uint)nx >= (uint)cellsX || (uint)ny >= (uint)cellsY) continue;
                        if (!IsCellWalkable(walkableData, bytesPerRow, nx, ny)) continue;

                        // Don't cut diagonally across a wall corner: both axis-aligned cells
                        // sharing the corner must be walkable too.
                        if (dx != 0 && dy != 0 &&
                            (!IsCellWalkable(walkableData, bytesPerRow, current.x, ny) ||
                             !IsCellWalkable(walkableData, bytesPerRow, nx, current.y)))
                            continue;

                        var neighbor = (nx, ny);
                        if (closedSet.Contains(neighbor)) continue;

                        float moveCost = (dx != 0 && dy != 0) ? Sqrt2 : 1f;
                        float tentativeG = currentG + moveCost;

                        if (!gScore.TryGetValue(neighbor, out float existingG) || tentativeG < existingG)
                        {
                            cameFrom[neighbor] = current;
                            gScore[neighbor] = tentativeG;
                            openSet.Enqueue(neighbor, tentativeG + Heuristic(nx, ny, goalX, goalY));
                        }
                    }
                }
            }

            return null;
        }

        // Greedy string pulling: from the current anchor, jump to the furthest waypoint reachable
        // by an unobstructed straight line, then repeat. Collapses the cell staircase into the
        // shortest navigable polyline. Every segment is validated cell-by-cell, so the line never
        // crosses non-walkable terrain.
        private static List<Vector2> StringPull(List<Vector2> path, byte[] data, int bytesPerRow)
        {
            if (path.Count <= 2) return path;

            var result = new List<Vector2>(path.Count) { path[0] };
            int current = 0;

            while (current < path.Count - 1)
            {
                int furthest = current + 1;
                for (int candidate = path.Count - 1; candidate > current + 1; candidate--)
                {
                    if (HasGridLineOfSight(path[current], path[candidate], data, bytesPerRow))
                    {
                        furthest = candidate;
                        break;
                    }
                }
                result.Add(path[furthest]);
                current = furthest;
            }

            return result;
        }

        // Bresenham rasterisation at single-cell resolution. True only if every cell along the
        // line between the two grid positions is walkable.
        private static bool HasGridLineOfSight(Vector2 from, Vector2 to, byte[] data, int bytesPerRow)
        {
            int x0 = (int)from.X, y0 = (int)from.Y;
            int x1 = (int)to.X, y1 = (int)to.Y;

            int dx = Math.Abs(x1 - x0);
            int dy = Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1;
            int sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;
            int x = x0, y = y0;

            while (true)
            {
                if (!IsCellWalkable(data, bytesPerRow, x, y)) return false;
                if (x == x1 && y == y1) return true;

                int e2 = 2 * err;
                if (e2 > -dy) { err -= dy; x += sx; }
                if (e2 < dx) { err += dx; y += sy; }
            }
        }

        // Spiral outward (up to SnapRadius cells) to find the nearest walkable cell.
        private static bool TrySnapToWalkable(
            byte[] data, int bytesPerRow, int cellsX, int cellsY, ref int x, ref int y)
        {
            for (int r = 1; r <= SnapRadius; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue; // ring only
                        int nx = x + dx;
                        int ny = y + dy;
                        if ((uint)nx >= (uint)cellsX || (uint)ny >= (uint)cellsY) continue;
                        if (IsCellWalkable(data, bytesPerRow, nx, ny))
                        {
                            x = nx;
                            y = ny;
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        private static bool IsCellWalkable(byte[] data, int bytesPerRow, int gx, int gy)
        {
            int byteIdx = gy * bytesPerRow + gx / 2;
            if ((uint)byteIdx >= (uint)data.Length) return false;
            int shift = gx % 2 == 1 ? 4 : 0;
            return ((data[byteIdx] >> shift) & 0xF) != 0;
        }

        // Octile distance: admissible and consistent for 8-directional grids — expands far fewer
        // nodes than Euclidean while still guaranteeing the optimal path.
        private static float Heuristic(int x1, int y1, int x2, int y2)
        {
            float dx = Math.Abs(x2 - x1);
            float dy = Math.Abs(y2 - y1);
            return MathF.Max(dx, dy) + (Sqrt2 - 1f) * MathF.Min(dx, dy);
        }

        private static List<Vector2> ReconstructPath(Dictionary<(int, int), (int, int)> cameFrom, (int x, int y) current)
        {
            var path = new List<Vector2>();
            while (cameFrom.TryGetValue(current, out var prev))
            {
                path.Add(new Vector2(current.x, current.y));
                current = prev;
            }
            path.Add(new Vector2(current.x, current.y));
            path.Reverse();
            return path;
        }
    }
}
