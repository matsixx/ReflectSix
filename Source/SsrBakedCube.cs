using Comfort.Common;
using EFT;
using System.Collections.Generic;
using UnityEngine;

namespace ReflectSix.Source
{
    // The game's "Reflective" shaders (p0/Reflective/Bumped Specular SMap and its kin, on map concrete and tiles, guns,
    // gear) add a fixed cubemap into the lighting buffer in their deferred pass: reflect(view, normal) -> _Cube -> x the
    // main texture's alpha (its gloss mask) -> x _ReflectColor -> x a fresnel term -> emission (decoded 2026-10-17). One
    // pre-baked picture of somewhere else, in every room, day or night, wherever the gloss mask is bright - and the real
    // reflections land on top of it. The puddle decals (Decal/Water Deferred Decal) do the same: _Cube x _CubemapColor (+
    // _EmissionColor) into their emission only - their gloss in the G-buffer is _SpecColor, untouched. While ReflectSix is
    // on, "Remove Baked Cube Reflections" turns that colour's rgb black on every solid material that has a _Cube, which
    // removes that term only (the specular highlight stays), and puts the colours back when either is switched off and
    // when the raid ends.
    // Originals are kept by instance id, never by Material: a static reference would keep a finished raid's map loaded
    // (the game frees it with UnloadUnusedAssets). Restoring walks the loaded materials and matches the ids.
    internal static class SsrBakedCube
    {
        private static readonly int IdCube = Shader.PropertyToID("_Cube");
        // the colour each kind multiplies its _Cube by: the Reflective shaders', the puddle decals'
        private static readonly int[] IdCubeTints = { Shader.PropertyToID("_ReflectColor"), Shader.PropertyToID("_CubemapColor") };
        private static readonly Dictionary<(int material, int tint), Color> _orig = new Dictionary<(int material, int tint), Color>();
        private static readonly List<float> _due = new List<float>();
        private static GameWorld _world;
        private static int _players;

        private static bool Want => SsrConfig.Enabled.Value && SsrConfig.NoBakedCube.Value;

        public static void SettingChanged()
        {
            if (!Want) Restore();
            else if (_world != null) Scan();
        }

        // Plugin.Update. Scans at the raid's start (the map, the player's kit and the bots' and loot's that load with it),
        // 15 s later for stragglers, and a few seconds after anyone new is in the raid (a bot wave loads its gear then).
        public static void Tick()
        {
            GameWorld gw = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance : null;
            if (!ReferenceEquals(_world, null) && (_world == null || gw != _world))
            {
                Restore();            // the raid is over (its world destroyed) or another began
                _world = null;
                _due.Clear();
            }
            if (gw == null) return;
            if (_world == null)
            {
                _world = gw;
                _players = 0;
                Request(2f);
                Request(15f);
            }
            int n = _world.AllAlivePlayersList.Count;
            if (n > _players) Request(3f);
            _players = n;

            if (_due.Count == 0) return;
            float now = Time.unscaledTime;
            bool run = false;
            for (int i = _due.Count - 1; i >= 0; i--)
                if (_due[i] <= now) { _due.RemoveAt(i); run = true; }
            if (run && Want) Scan();
        }

        // Another scan in about this many seconds, unless one is already due in the 3 s before then.
        public static void Request(float delay)
        {
            float at = Time.unscaledTime + delay;
            foreach (float d in _due) if (d <= at && d >= at - 3f) return;
            _due.Add(at);
        }

        private static void Scan()
        {
            int set = 0;
            Dictionary<string, int> shaders = SsrConfig.Debug.Value ? new Dictionary<string, int>() : null;
            foreach (Material m in Resources.FindObjectsOfTypeAll<Material>())
            {
                // Solid surfaces only: glass and other see-through ones draw after the reflections are put on the
                // picture, so their own cubemap is the only reflection they have. Glass by name too: the glass-block
                // walls the game draws as solid ones are glass again with TraceSix's "See-Through Glass Windows".
                if (m == null || m.renderQueue >= 2500 || !m.HasProperty(IdCube)
                    || m.name.IndexOf("glass", System.StringComparison.OrdinalIgnoreCase) >= 0) continue;
                int id = m.GetInstanceID();
                bool took = false;
                foreach (int tint in IdCubeTints)
                {
                    if (!m.HasProperty(tint) || _orig.ContainsKey((id, tint))) continue;
                    Color c = m.GetColor(tint);
                    if (c.r <= 0f && c.g <= 0f && c.b <= 0f) continue;
                    _orig[(id, tint)] = c;
                    m.SetColor(tint, new Color(0f, 0f, 0f, c.a));
                    took = true;
                }
                if (!took) continue;
                set++;
                if (shaders != null)
                {
                    string s = m.shader != null ? m.shader.name : "?";
                    shaders[s] = shaders.TryGetValue(s, out int k) ? k + 1 : 1;
                }
            }
            if (set == 0) return;
            Plugin.LogDetail($"[ReflectSix] Baked cube reflections removed from {set} materials ({_orig.Count} colours this raid).");
            if (shaders != null)
                foreach (var kv in shaders) Plugin.MyLog.LogInfo($"[ReflectSix]   {kv.Value} x {kv.Key}");
        }

        private static void Restore()
        {
            if (_orig.Count == 0) return;
            int back = 0;
            foreach (Material m in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (m == null) continue;
                int id = m.GetInstanceID();
                bool had = false;
                foreach (int tint in IdCubeTints)
                    if (_orig.TryGetValue((id, tint), out Color c)) { m.SetColor(tint, c); had = true; }
                if (had) back++;
            }
            Plugin.LogDetail($"[ReflectSix] Baked cube reflections put back on {back} materials.");
            _orig.Clear();
        }
    }
}
