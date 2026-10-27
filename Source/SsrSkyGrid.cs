using EFT.EnvironmentEffect;
using System.Collections.Generic;
using UnityEngine;

namespace ReflectSix.Source
{
    // Roof-occlusion grid for the sky-map reflection fallback. When CloudSix publishes its sky map, EVERY
    // reflection ray that finds nothing on screen takes the map (the shader's "one sky source" rule), so
    // this grid is the only thing that keeps sky out of a reflection: a small world-anchored grid, each
    // cell answering "is there open sky over this spot?". Reflecting surfaces look up THEIR cell, so an
    // outdoor puddle seen from a doorway keeps its sky while the indoor floor next to it doesn't. The
    // runtime stand-in for baked local reflection probes / sky-occlusion data.
    //
    // A cell is roofed when either says so:
    //  - a physics ray straight up from its centre hits something (a roof the game did not mark), or
    //  - its centre is inside one of the game's own shelter volumes (IndoorTrigger: the boxes its rain,
    //    exposure and reverb go by; FogSix thins its fog by the same ones).
    // The ray alone called Reserve's bunkers open sky (2026-10-08): underground, a cell's centre is mostly
    // solid earth beside the corridor, and from there the ray hits nothing - terrain and ground colliders
    // are one-sided, seen from above only. The shelter volumes are boxes around whole interiors, earth
    // between the corridors included, so they do not have that blind spot.
    //
    // Two grids (2026-10-10): one around the player for the main view, one around what a scope is aimed at.
    // The shader clamps a lookup to the grid's edge, so through a scope - everything in view 100 m and more
    // away - the player's own grid answered with one edge cell for the whole picture: aiming out of a
    // doorway at a puddle took the roof over the player, aiming into a far shed took the sky over him.
    internal sealed class SsrSkyGrid
    {
        private const int N = 8;            // N x N cells (must match SSR_SKYGRID_N in the shader)
        private const float CELL = 8f;      // metres per cell -> 64x64m coverage
        private const float RAY_LEN = 80f;

        public static readonly SsrSkyGrid Main = new SsrSkyGrid(8);
        // A scope sweeps its grid across the map as it is aimed: more cells looked at per frame, so a fresh
        // grid is answered in four frames and not eight.
        public static readonly SsrSkyGrid Scope = new SsrSkyGrid(16);

        private readonly int _raysPerFrame;
        private readonly float[] _open = new float[N * N];   // what the shader gets: 1 = sky above, 0 = roofed
        private readonly bool[] _roofed = new bool[N * N];   // the ray up from the cell's centre hit something
        private readonly bool[] _inside = new bool[N * N];   // the centre is inside a shelter volume
        private readonly bool[] _known = new bool[N * N];
        private float _originX, _originZ;                    // world xz of cell (0,0) corner
        private bool _originValid;
        private float _y;
        private int _cursor;

        // The game's shelter volumes: unit cubes in their own space (IndoorTrigger.Check = |local| < 0.5).
        // Static scene objects; read again after every scene load (a raid's scenes arrive one by one).
        private static readonly List<Matrix4x4> _shelters = new List<Matrix4x4>();
        private static bool _sheltersRead;

        private SsrSkyGrid(int raysPerFrame) { _raysPerFrame = raysPerFrame; }

        public static void ClearAll()
        {
            _sheltersRead = false;
            _shelters.Clear();
            Main.Clear();
            Scope.Clear();
        }

        private void Clear()
        {
            _originValid = false;
            _cursor = 0;
            for (int i = 0; i < N * N; i++) { _open[i] = 1f; _roofed[i] = _inside[i] = _known[i] = false; }
        }

        private static void ReadShelters()
        {
            _sheltersRead = true;
            _shelters.Clear();
            foreach (IndoorTrigger t in Object.FindObjectsOfType<IndoorTrigger>())
                if (t != null) _shelters.Add(t.transform.worldToLocalMatrix);
            // every cell is looked at again
            for (int i = 0; i < N * N; i++) Main._known[i] = Scope._known[i] = false;
        }

        private static bool Sheltered(Vector3 p)
        {
            for (int k = 0; k < _shelters.Count; k++)
            {
                Vector3 q = _shelters[k].MultiplyPoint3x4(p);
                if (Mathf.Abs(q.x) < 0.5f && Mathf.Abs(q.y) < 0.5f && Mathf.Abs(q.z) < 0.5f) return true;
            }
            return false;
        }

        // at = the place the grid is centred on, and the height its cells are judged at: the camera for the
        // main view, the surface a scope is aimed at for its own.
        public void Update(Vector3 at)
        {
            if (!_sheltersRead) ReadShelters();
            _y = at.y;

            // World-snapped origin so cells are STABLE as the centre moves (a surface keeps its cell).
            float ox = (Mathf.Floor(at.x / CELL) - N / 2) * CELL;
            float oz = (Mathf.Floor(at.z / CELL) - N / 2) * CELL;
            if (!_originValid || ox != _originX || oz != _originZ)
            {
                // Shift: carry over the overlapping cells, mark newcomers unknown (they answer "open" until
                // their turn comes - a few frames at most).
                int dx = _originValid ? Mathf.RoundToInt((ox - _originX) / CELL) : N;
                int dz = _originValid ? Mathf.RoundToInt((oz - _originZ) / CELL) : N;
                var roofed = new bool[N * N];
                var inside = new bool[N * N];
                var known = new bool[N * N];
                for (int z = 0; z < N; z++)
                for (int x = 0; x < N; x++)
                {
                    int sx = x + dx, sz = z + dz;
                    if (sx < 0 || sx >= N || sz < 0 || sz >= N) continue;
                    int to = z * N + x, from = sz * N + sx;
                    roofed[to] = _roofed[from]; inside[to] = _inside[from]; known[to] = _known[from];
                }
                System.Array.Copy(roofed, _roofed, N * N);
                System.Array.Copy(inside, _inside, N * N);
                System.Array.Copy(known, _known, N * N);
                _originX = ox; _originZ = oz; _originValid = true;
            }

            // Round-robin refresh: unknown cells first, then steady re-verification.
            int fired = 0;
            for (int scanned = 0; scanned < N * N && fired < _raysPerFrame; scanned++)
            {
                int i = _cursor;
                _cursor = (_cursor + 1) % (N * N);
                if (_known[i] && fired > 0) continue;   // spend spare budget on re-checks, priority to unknowns
                int cx = i % N, cz = i / N;
                Vector3 p = new Vector3(_originX + (cx + 0.5f) * CELL, at.y + 1f,
                                        _originZ + (cz + 0.5f) * CELL);
                _roofed[i] = Physics.Raycast(p, Vector3.up, RAY_LEN,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                // At the reference height itself: a shelter box is the height of its rooms.
                _inside[i] = Sheltered(new Vector3(p.x, at.y, p.z));
                _known[i] = true;
                fired++;
            }
        }

        public void Apply(Material mat)
        {
            for (int i = 0; i < N * N; i++) _open[i] = (_roofed[i] || _inside[i]) ? 0f : 1f;
            mat.SetFloatArray("_SsrSkyGrid", _open);
            mat.SetFloat("_SsrSkyTopOn", 0f);   // the shader's column-height rule (a first try at this) stays off
            mat.SetVector("_SsrSkyGridParams", new Vector4(_originX, _originZ, 1f / CELL, N));
        }

        // Debug Log: the grid as it stands. One row per z, cells 8 m apart, its centre in the middle.
        //   R = something overhead (ray), I = inside a shelter volume, B = both, . = open sky, ? = not looked at yet
        public string Describe()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("roof grid: ").Append(_shelters.Count).Append(" shelter volumes in the map, judged at y ").Append(_y.ToString("F1"))
              .Append(", cells from (").Append(_originX.ToString("F0")).Append(", ").Append(_originZ.ToString("F0")).Append(')');
            for (int z = N - 1; z >= 0; z--)
            {
                sb.Append("\n    ");
                for (int x = 0; x < N; x++)
                {
                    int i = z * N + x;
                    sb.Append(!_known[i] ? '?' : _roofed[i] && _inside[i] ? 'B' : _roofed[i] ? 'R' : _inside[i] ? 'I' : '.').Append(' ');
                }
            }
            return sb.ToString();
        }
    }
}
