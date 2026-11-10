using Comfort.Common;
using EFT;
using GPUInstancer;
using UnityEngine;

namespace ReflectSix.Source
{
    // What "Blend Like The Game" and the instanced-foliage share need from the game each frame.
    // The game's ambient pass (AmbientLight's "Custom Ambient" buffer) adds its sky-cube reflection to the picture
    // multiplied by _ReflectionIntensity, which lives in a MaterialPropertyBlock (1 with the game's SSR setting on,
    // else the rain-wetting curve x ReflectionIntensity), so it is read from there, not from the material.
    // The AmbientLight is looked up twice a raid (2 s and 15 s after its GameWorld appears), never on a timer:
    // FindObjectOfType walks every object in the scene.
    internal static class SsrGameAmbient
    {
        private static readonly int IdReflectionIntensity = Shader.PropertyToID("_ReflectionIntensity");
        private static AmbientLight _ambient;
        private static GameWorld _world;
        private static float _findAt = -1f, _findAgainAt = -1f;

        // Plugin.Update
        public static void Tick()
        {
            GameWorld gw = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance : null;
            if (!ReferenceEquals(_world, gw))
            {
                _world = gw;
                _ambient = null;
                _findAt = gw != null ? Time.unscaledTime + 2f : -1f;
                _findAgainAt = gw != null ? Time.unscaledTime + 15f : -1f;
            }
            if (gw == null) return;
            float now = Time.unscaledTime;
            if (_findAt >= 0f && now >= _findAt)
            {
                _findAt = -1f;
                Find();
            }
            if (_findAgainAt >= 0f && now >= _findAgainAt)
            {
                _findAgainAt = -1f;
                if (_ambient == null) Find();
            }
        }

        private static void Find()
        {
            _ambient = Object.FindObjectOfType<AmbientLight>();
            if (SsrConfig.Debug.Value)
                Plugin.MyLog.LogInfo(_ambient != null
                    ? $"[ReflectSix] Game ambient pass found: reflection intensity {ReflectionIntensity:0.###}."
                    : "[ReflectSix] No AmbientLight in this raid: Blend Like The Game falls back to the old blend.");
        }

        // The ambient pass's _ReflectionIntensity this frame; -1 = no ambient pass known (the shader then blends as before).
        public static float ReflectionIntensity
        {
            get
            {
                if (_ambient == null) return -1f;
                MaterialPropertyBlock block = _ambient._screenAmbientBlock;
                return block != null ? block.GetFloat(IdReflectionIntensity) : -1f;
            }
        }

        // GPU Instancer wrote its motion-vector depth (_CameraMotionVectorsDepth) this frame: it has managers and draws
        // motion vectors (on with the game's SSR setting, TAA or an upscaler). Without managers the ambient pass binds
        // a black texture, which reads as "not drawn by it" anyway.
        public static bool GpuiDepthValid
        {
            get
            {
                var managers = GPUInstancerManager.activeManagerList;
                return managers != null && managers.Count > 0 && GPUInstancerManager.bGenerateMotionVectors;
            }
        }
    }
}
