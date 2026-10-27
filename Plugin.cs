using BepInEx;
using BepInEx.Logging;
using ReflectSix.Patches;
using ReflectSix.Source;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ReflectSix
{
    [BepInPlugin("com.matsix.reflectsix", "ReflectSix", "1.0.1")]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource MyLog;

        private void Awake()
        {
            MyLog = Logger;
            MyLog.LogInfo("ReflectSix loaded!");

            SsrConfig.Bind(Config);
            new SsrOpaquePatch().Enable();
            new SuppressGameSsrPatch().Enable();

            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void Update()
        {
            SsrBakedCube.Tick();
            if (!SsrConfig.CostNow.Value) return;
            SsrConfig.CostNow.Value = false;
            if (!SsrBench.Running) StartCoroutine(SsrBench.Run());
        }

        // On a ray miss the scene pixel is kept, so the surface's baked reflection-probe specular is the
        // fallback look — worth knowing whether the probes are actually alive (especially in VR, where other
        // parts of the post stack are dead). Logged once per raid when Debug Log is on; purely diagnostic.
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (SsrConfig.Debug.Value) StartCoroutine(LogReflectionProbes());
            SsrSkyGrid.ClearAll();
            SsrRenderer.ClearViews();
            SsrBakedCube.Request(2f);   // a raid's scenes arrive one by one (no-op outside a raid)
        }

        private IEnumerator LogReflectionProbes()
        {
            yield return new WaitForSeconds(10f); // let the map stream in first
            ReflectionProbe[] probes = Object.FindObjectsOfType<ReflectionProbe>();
            int live = 0;
            foreach (ReflectionProbe p in probes)
                if (p.isActiveAndEnabled && (p.texture != null || p.bakedTexture != null)) live++;
            MyLog.LogInfo($"[ReflectSix] ReflectionProbes in scene: {probes.Length} total, {live} enabled with a texture");
        }
    }
}
