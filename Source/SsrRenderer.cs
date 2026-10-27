using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReflectSix.Source
{
    // Renders the screen-space reflections: loads the shader bundle, builds the per-eye matrices, and
    // composites the traced reflections back INTO the source image (in place) at opaque time, so the fog /
    // vanilla scattering that runs after us sees a scene that already reflects. Handles BOTH flatscreen
    // (mono) and VR (per-eye) — the hook fires once per eye in multipass and Camera.current tells us which.
    //
    // v1.2.0: the tracer is VIEW-SPACE (see the shader header — the 22-round lesson). The trace consumes
    // only the projection pair and the view rotation. One lean temporal resolve remains (SSSR's denoiser
    // core — a raw single-frame trace always carries per-frame noise in motion); Hi-Z, planar refinement
    // and the diagnostic scaffolding are gone.
    internal static class SsrRenderer
    {
        private static Material _mat;
        internal static Material Mat => _mat;
        // "Off-Screen Rays Ray-Traced": the passes that make the rays (SsrRtRays) and read TraceSix's answers (-2 = not looked up)
        private static int _pRtRays = -2, _pWorldRtOpen = -2, _pWorldRtCol = -2;
        private static readonly int IdGiReflFrame = Shader.PropertyToID("_GiRtReflFrame");
        internal static int PassRtRays
        {
            get
            {
                if (_mat == null) return -1;
                if (_pRtRays == -2)
                {
                    _pRtRays = _mat.FindPass("SSRRTRAYS");
                    _pWorldRtOpen = _mat.FindPass("WORLDRTOPEN");
                    _pWorldRtCol = _mat.FindPass("WORLDRTCOL");
                }
                return _pWorldRtOpen >= 0 && _pWorldRtCol >= 0 ? _pRtRays : -1;
            }
        }

        // How the trace draws its ray this frame (SSRRTRAYS draws the same): x = stochastic, y = the new denoiser's sampler and
        // sequence, z = the frame index - as Render sets _SsrStochastic / _SsrDn / _SsrFrameIdx.
        internal static Vector4 SampleFor(bool halfRes)
        {
            bool dn = _mat != null && DenoiserReady();
            return new Vector4(SsrConfig.Stochastic.Value && !SsrBench.Mirror ? 1f : 0f, dn ? 1f : 0f,
                ((dn && halfRes) ? (Time.frameCount >> 2) : Time.frameCount) & 1023, 0f);
        }

        // The pixel the half-res trace traces within each 2 x 2 this frame (as Render sets _SsrQuadOfs around the trace).
        internal static Vector4 QuadOfsFor(bool halfRes)
        {
            if (_mat == null || !halfRes || !DenoiserReady()) return Vector4.zero;
            int quadPhase = Time.frameCount & 3;
            return new Vector4((quadPhase == 1 || quadPhase == 2) ? 1f : 0f, (quadPhase == 1 || quadPhase == 3) ? 1f : 0f);
        }
        private static bool _triedLoad;
        private static float _nextProbeLog;
        private static float _nextDnLog;
        private static float _rainFlowEnv;      // live rain envelope (trails RainController.Intensity)
        private static int _rainEnvFrame = -1;  // once-per-frame guard (Render fires per eye in VR)

        // Pass indices in Assets/ScreenSpaceReflections.shader (PINNED — keep in sync with the shader).
        private const int PassTrace = 0;     // the trace, at whatever resolution it's blitted at -> hit records
        private const int PassUpsample = 1;  // depth-aware upsample of the half-res hit records
        private const int PassShade = 2;     // records -> (premultiplied colour, blend weight), full res
        private const int PassComposite = 3; // reflections over the scene (+ glossy cone fetch)
        private const int PassDebug = 4;     // debug views
        private const int PassGlossDown = 5; // glossy pyramid: blurred downsample of the shaded reflections
        private const int PassAugBase = 6;   // glass depth cap: scene depth -> augmented target
        private const int PassAugGlass = 7;  // glass depth cap: rasterize one glass renderer, nearest-wins
        private const int PassTemporal = 8;  // temporal resolve (view-space virtual-point reprojection)
        private const int PassSanitize = 9;  // scene-copy scrub (NaN kill + radiance cap) before mip build
        // Denoiser v2 (appended, so a stale bundle still runs the old path — see DenoiserReady).
        private const int PassDnShade = 10;      // records -> demodulated radiance (pdf-weighted ray reuse)
        private const int PassDnReproject = 11;  // where each pixel's history lives + how long it is
        private const int PassDnTemporal = 12;   // running average over the per-pixel history length
        private const int PassDnGeo = 13;        // this frame's surface, for next frame's validation
        private const int PassDnComposite = 14;  // applies the material per pixel, composites
        private const int PassDnFilter = 15;     // display-only surface-aware 3x3 over the resolved radiance
        // Empty-space skip (appended; a bundle without them marches every sample as before).
        private const int PassMinZ = 16;         // nearest depth per 8x8 depth texels
        private const int PassMinZDown = 17;     // nearest depth per 64x64, from the level above
        private const int PassWorld = 18;        // TraceSix's voxels answer for the rays that left the picture, one to a block
        // The blurs round the accumulation (appended; a bundle without them denoises as before).
        private const int PassDnGuide = 19;      // the surface packed for the blurs' taps
        private const int PassDnBlur = 20;       // the lobe-aware blur, before the accumulation and after it
        // Once a frame, not once a use (appended; a bundle without them works everything out per use as before).
        private const int PassDnAlpha = 21;      // every pixel's roughness, measured once
        private const int PassDnRay = 22;        // every traced ray's hit shaded once, for the resolve's taps to share
        // The soft march's reads through the GPU's filter (appended; a bundle without it reads the voxels one by one).
        private const int PassVxCopy = 23;       // a volume of TraceSix's voxels unpacked for two linear samples
        // The cost log's two passes are found BY NAME (SSRCOUNT, SSRPROBE): -1 in a bundle without them.
        // (2026-10-13: a stencil mask for the pixels that reflect nothing was built, measured - 0.00 ms saved
        // with 64 % of the picture masked - and taken out again. Those pixels lie in large regions whose
        // groups return early together; a mask pays where the work is scattered.)
        private static int _passCount = -2, _passProbe = -2;
        // Half-Resolution Denoising's passes, by name (an older bundle has none: the setting then does nothing).
        private static int _pDnShadeH = -2, _pDnReprojH, _pDnTemporalH, _pDnGeoH, _pDnBlurH, _pDnUp;
        private static int _pDnStab = -2;   // (1.0.0) DNSTABILIZE, "Stabilize Output" (not in the bundle: none)
        // The march with one depth read a loop turn (ReflectSix.shader traceSSR, _SsrMarch): only in a bundle that
        // says so. An older bundle marches as before, whatever Ray March says.
        private static int _marchCan = -1;
        // Their numbers (ReflectSix.shader dnBlur; model: Tools/gisix/dnsim.py). Radii are shares of the picture's
        // height, so a blur covers the same part of the view at any resolution (0.0125 = 12 px at 960).
        //   blurPre        largest radius of the blur before the accumulation. Wider flattens what a lobe
        //                  itself still resolves (the model's blob kept 0.8 of its height at this, 0.6 at 20 px).
        //   blurLobeShare  no radius reaches further than this share of the lobe's size on screen.
        //   blurPost       radius after the accumulation where there is no history; blurFall = how fast it
        //                  falls with frames accumulated (radius = blurPost / (1 + blurFall x frames)),
        //                  blurMinPx = where it ends up.
        //   blurNormal / blurNormalFresh   two normals may differ by this share of the lobe's half angle,
        //                  with a long history / with none (and before the accumulation).
        //   blurRoom       on a wide lobe the history may differ from this frame by this share of the signal
        //                  whatever the frame's spread says. More = steadier and no light lost where a rough
        //                  surface reflects something small and bright; less = a change in what is reflected
        //                  shows sooner (the model: 0.2 = 15 frames for a halving, 0.3 = 35, 0.5 = 54).
        public static float blurPre = 0.0125f, blurLobeShare = 0.5f, blurPost = 0.025f, blurFall = 1f, blurMinPx = 1f;
        // Half-Resolution Denoising, Blurs Only: the blur radius (full-res pixels) over which the half-res blur
        // takes over from the full-res one. Under ~2.5 px a half-res blur's taps fall in the centre's own texel.
        public static float halfBlurLo = 2.5f, halfBlurHi = 4f;
        public static float blurNormal = 0.3f, blurNormalFresh = 0.6f, blurRoom = 0.25f;
        // (1.0.0) "Stabilize Output" (DNSTABILIZE): its most frames (and never more than the reflection history's own),
        // how far it may stray from this frame's reflection (spreads of the accumulation before the blur after it) and
        // the floor of that room (a share of the value). More frames / room = calmer, but a change it misreads as grain
        // shows that much later.
        public static float stabFrames = 32f, stabBox = 2f, stabFloor = 0.02f;
        // Soft edges: the summed coverage over which a ray is shut (ReflectSix.shader vxLeft; Tools/gisix/blendtest.py).
        // Wider apart = softer outlines of off-screen things; x lower = thin things (a truss) count sooner.
        // z, w: a ray the picture saw for a while is marched from z voxels (per axis) before the point it was
        // last seen at - a voxel says a surface is in it, not where - spending at most w voxels of ray on it.
        // z = 0: from the point itself (rays lost just under a ceiling then leak: sky lines along beams' edges).
        private static readonly Vector4 VoxEdge = new Vector4(0.25f, 0.45f, 0.5f, 10f);

        // Empty-space skip: the march tests two small nearest-depth buffers and jumps over screen blocks
        // the ray cannot hit anything in. Same hits, fewer depth reads. Live A/B for the frame-time gain.
        public static bool useEmptySpaceSkip = true;
        // Follow motion vectors for moving reflectors (gun, hands, doors). Live A/B; only used when the
        // camera already renders them (TAA / upscalers / SPT-VR upscalers turn them on).
        public static bool useMotionVectors = true;
        // The display-only spatial filter (pass 15). Live A/B, plus its tap spacing in pixels on
        // converged pixels / on pixels with almost no history (wider = smoother, softer reflections).
        public static bool useSpatialFilter = true;
        public static float filterSpacing = 1.0f;
        public static float filterSpacingFresh = 3.5f;
        // v2 composite: cap edge-on reflectance by smoothness (Unity's grazing term) instead of plain
        // Schlick, which outlined the gun in pure reflection at low Smoothness Cutoff. false = old look.
        public static bool smoothnessCappedFresnel = true;

        private static bool _warnedStaleBundle;

        // Blue-noise masks for the v2 sample sequence (embedded resource; see dnBase in the shader for
        // why the per-pixel offsets must be unstructured). Null = the shader's integer-hash fallback.
        private static Texture2D _blue;
        private static bool _blueTried;

        private static Texture2D BlueNoise()
        {
            if (_blue != null || _blueTried) return _blue;
            _blueTried = true;
            try
            {
                using (Stream s = typeof(SsrRenderer).Assembly.GetManifestResourceStream("ReflectSix.bluenoise128.bin"))
                {
                    byte[] data = new byte[128 * 128 * 3];
                    int read = 0;
                    while (s != null && read < data.Length)
                    {
                        int n = s.Read(data, read, data.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read != data.Length) throw new IOException("resource missing or short (" + read + " bytes)");
                    Texture2D t = new Texture2D(128, 128, TextureFormat.RGB24, false, true)
                    {
                        filterMode = FilterMode.Point,
                        wrapMode = TextureWrapMode.Repeat,
                        hideFlags = HideFlags.HideAndDontSave,
                    };
                    t.LoadRawTextureData(data);
                    t.Apply(false, true);
                    _blue = t;
                }
            }
            catch (System.Exception e)
            {
                Plugin.MyLog.LogWarning("[ReflectSix] Blue-noise masks unavailable, using the hash fallback: " + e.Message);
            }
            return _blue;
        }

        // Per-VIEW reflection history for the temporal resolve: one per camera and eye (mono, left, right
        // are separate renders - and so is the scope's).
        // Ping-pong: `read` is last frame's resolved output, `write` this frame's target; swapped after
        // each resolve. prevView/prevProjNoJit feed the RELATIVE reprojection matrix.
        // Denoiser v2 adds: info (reprojection + history length, ping-pong), geo (last frame's surface),
        // prevInvProj (to rebuild last frame's view positions). `dn` records which path wrote the
        // history — the two store different things, so a switch restarts accumulation.
        // SCOPES (2026-10-08): this was keyed by eye alone. The scope's camera carries TOD_Scattering too
        // (OpticComponentUpdater mirrors the main camera's onto it), so the hook fires for it, and on
        // flatscreen both it and the main camera are "Mono" - at different sizes. They took turns
        // finding the buffers the wrong size: every frame while scoped, each released and re-created its
        // history, denoiser and gloss targets and started its accumulation over. Players: "SSR hurts
        // performance a lot, especially looking through a scope."
        private class EyeHistory
        {
            public Camera cam;
            public RenderTexture read, write;
            public RenderTexture infoRead, infoWrite, geo;
            public RenderTexture stabRead, stabWrite;   // (1.0.0) "Stabilize Output": the stabilized reflection, ping-pong
            public RenderTexture gloss;
            public int glossMips;
            public Matrix4x4 prevView, prevProjNoJit, prevInvProj;
            public bool valid, dn, stabValid;
            public int lastFrame;   // the frame this view was last drawn in
        }
        private static readonly Dictionary<long, EyeHistory> _history = new Dictionary<long, EyeHistory>();
        private static readonly List<long> _dead = new List<long>();

        private static void Drop(ref RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            Object.Destroy(rt);
            rt = null;
        }

        private static void DropAll(EyeHistory hb)
        {
            Drop(ref hb.read);
            Drop(ref hb.write);
            Drop(ref hb.gloss);
            ReleaseDenoiserTargets(hb);
            hb.valid = false;
        }

        // Every view's buffers (a raid's cameras are gone once the next scene loads).
        public static void ClearViews()
        {
            foreach (EyeHistory hb in _history.Values) DropAll(hb);
            _history.Clear();
            DropVoxCopies();
            _scope.Clear();
            _thermal.Clear();
        }

        // A scope's picture is a second camera (the game's optic camera), and it gets reflections of its own
        // ("Reflections In Scopes"): its own history and buffers (View), restarted with every aim (the camera
        // is switched off in between), and its own roof grid around what it is aimed at (SsrSkyGrid.Scope).
        // With the setting off it is left entirely to the game - no ReflectSix reflections there, and the game's
        // own SSR not suppressed there either (SuppressGameSsrPatch) - as from 2026-10-08, when the scope
        // shared the main view's buffers and both were rebuilt every frame.
        private static readonly Dictionary<int, bool> _scope = new Dictionary<int, bool>();
        private static readonly Dictionary<int, ThermalVision> _thermal = new Dictionary<int, ThermalVision>();

        // Does this scope's picture get reflections? Not with the setting off, and never a thermal sight: its
        // picture is heat, drawn by an effect of its own over whatever is under it.
        public static bool ScopeActive(Camera cam)
        {
            if (cam == null || !SsrConfig.Enabled.Value || !SsrConfig.ScopeRefl.Value) return false;
            int id = cam.GetInstanceID();
            if (!_thermal.TryGetValue(id, out ThermalVision tv))
            {
                tv = cam.GetComponent<ThermalVision>();
                _thermal[id] = tv;
            }
            return tv == null || !tv.enabled || !tv.On;
        }

        // What a scope is aimed at: where its roof grid is centred. Nothing in reach = a point well out.
        // TraceSix's voxel world, as ReflectSix.shader's copy of its march reads it (worldSkyOpen): TraceSix publishes
        // the layout's number with the volumes, and a volume that is built has a voxel size in its origin's w.
        private const float GiVoxFormat = 1f;
        private static readonly int IdGiVoxFormat = Shader.PropertyToID("_GiVoxFormat");
        private static readonly int[] IdGiVoxOrigin =
        {
            Shader.PropertyToID("_GiVoxOrigin0"), Shader.PropertyToID("_GiVoxOrigin1"),
            Shader.PropertyToID("_GiVoxOrigin2"), Shader.PropertyToID("_GiVoxOrigin3")
        };

        private static bool GiVoxelsUp()
        {
            if (Shader.GetGlobalFloat(IdGiVoxFormat) != GiVoxFormat) return false;
            for (int l = 0; l < IdGiVoxOrigin.Length; l++)
                if (Shader.GetGlobalVector(IdGiVoxOrigin[l]).w > 0f) return true;
            return false;
        }

        // The soft march's filterable copy of each volume's coverages (ReflectSix.shader: pass 23, vxFacingBlend):
        // two volumes wide, 16 MB a volume, redrawn - a slice a draw - when TraceSix has rebuilt the volume it
        // was made from. TraceSix says so with a build count (_GiVoxStamp, 0.24.25 on); without one the copies
        // could go stale behind a door, so there are none and the march reads the voxels as before.
        // Called from Render: TraceSix publishes in Update, so a copy made here is this frame's volume.
        private static readonly int IdGiVoxStamp = Shader.PropertyToID("_GiVoxStamp"), IdGiVoxRes = Shader.PropertyToID("_GiVoxRes");
        private static readonly int IdVxSrc = Shader.PropertyToID("_SsrVxSrc"), IdVxSlice = Shader.PropertyToID("_SsrVxSlice");
        private static readonly int[] IdGiVox = { Shader.PropertyToID("_GiVox0"), Shader.PropertyToID("_GiVox1"), Shader.PropertyToID("_GiVox2"), Shader.PropertyToID("_GiVox3") };
        private static readonly int[] IdVxCov = { Shader.PropertyToID("_SsrVxCov0"), Shader.PropertyToID("_SsrVxCov1"), Shader.PropertyToID("_SsrVxCov2"), Shader.PropertyToID("_SsrVxCov3") };
        private static readonly RenderTexture[] _vxCov = new RenderTexture[4];
        private static readonly Texture[] _vxCovSrc = new Texture[4];
        private static readonly Vector4[] _vxCovOrg = new Vector4[4];
        private static readonly float[] _vxCovStamp = new float[4];
        private static CommandBuffer _vxCb;
        private static int _vxCovDrawn;          // volumes redrawn so far (the debug log)

        private static bool VoxCopies()
        {
            Vector4 stamp = Shader.GetGlobalVector(IdGiVoxStamp);
            int res = Mathf.RoundToInt(Shader.GetGlobalFloat(IdGiVoxRes));
            if (res <= 0) return false;
            for (int l = 0; l < 4; l++)
            {
                Vector4 org = Shader.GetGlobalVector(IdGiVoxOrigin[l]);
                if (org.w <= 0f) continue;                      // not built: the march never asks it
                Texture src = Shader.GetGlobalTexture(IdGiVox[l]);
                if (stamp[l] <= 0f || src == null) return false;
                RenderTexture rt = _vxCov[l];
                bool fresh = rt == null || rt.height != res || !rt.IsCreated();
                if (fresh)
                {
                    Drop(ref _vxCov[l]);
                    rt = new RenderTexture(res * 2, res, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
                    {
                        name = "ReflectSix Voxel Coverage " + l, dimension = TextureDimension.Tex3D, volumeDepth = res,
                        filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, useMipMap = false, hideFlags = HideFlags.DontSave
                    };
                    if (!rt.Create()) { rt.Release(); Object.Destroy(rt); return false; }
                    _vxCov[l] = rt;
                }
                if (fresh || !ReferenceEquals(src, _vxCovSrc[l]) || org != _vxCovOrg[l] || stamp[l] != _vxCovStamp[l])
                {
                    if (_vxCb == null) _vxCb = new CommandBuffer { name = "ReflectSix Voxel Coverage" };
                    _vxCb.Clear();
                    _vxCb.SetGlobalTexture(IdVxSrc, src);
                    for (int z = 0; z < res; z++)
                    {
                        _vxCb.SetGlobalFloat(IdVxSlice, z);
                        _vxCb.SetRenderTarget(new RenderTargetIdentifier(rt, 0, CubemapFace.Unknown, z));
                        _vxCb.DrawProcedural(Matrix4x4.identity, _mat, PassVxCopy, MeshTopology.Triangles, 3);
                    }
                    RenderTexture was = RenderTexture.active;
                    Graphics.ExecuteCommandBuffer(_vxCb);
                    RenderTexture.active = was;
                    _vxCovSrc[l] = src;
                    _vxCovOrg[l] = org;
                    _vxCovStamp[l] = stamp[l];
                    _vxCovDrawn++;
                }
                _mat.SetTexture(IdVxCov[l], rt);
            }
            return true;
        }

        private static void DropVoxCopies()
        {
            for (int l = 0; l < 4; l++)
            {
                Drop(ref _vxCov[l]);
                _vxCovSrc[l] = null;
            }
        }

        private static Vector3 AimPoint(Vector3 from, Vector3 fwd)
        {
            return Physics.Raycast(from + fwd * 2f, fwd, out RaycastHit hit, 600f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                ? hit.point : from + fwd * 150f;
        }

        public static bool IsScope(Camera cam)
        {
            if (cam == null) return false;
            int id = cam.GetInstanceID();
            if (!_scope.TryGetValue(id, out bool scope))
            {
                scope = cam.GetComponent<EFT.CameraControl.OpticComponentUpdater>() != null;
                _scope[id] = scope;
            }
            return scope;
        }

        // The state of one camera's one eye. Views whose camera is gone are let go when a new one arrives.
        private static EyeHistory View(Camera cam, Camera.MonoOrStereoscopicEye eye)
        {
            long key = ((long)cam.GetInstanceID() << 2) | (long)(int)eye;
            if (_history.TryGetValue(key, out EyeHistory hb)) return hb;
            _dead.Clear();
            foreach (KeyValuePair<long, EyeHistory> kv in _history)
                if (kv.Value.cam == null) { DropAll(kv.Value); _dead.Add(kv.Key); }
            foreach (long k in _dead) _history.Remove(k);
            hb = new EyeHistory { cam = cam };
            _history[key] = hb;
            return hb;
        }

        private static void EnsureHistory(EyeHistory hb, int w, int h)
        {
            if (hb.read != null && hb.read.width == w && hb.read.height == h) return;
            Drop(ref hb.read);
            Drop(ref hb.write);
            hb.read = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf) { filterMode = FilterMode.Bilinear };
            hb.write = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf) { filterMode = FilterMode.Bilinear };
            hb.valid = false;   // resolution changed -> stale history, restart accumulation
        }

        private static void EnsureDenoiserTargets(EyeHistory hb, int w, int h)
        {
            if (hb.geo != null && hb.geo.width == w && hb.geo.height == h) return;
            ReleaseDenoiserTargets(hb);
            // Half floats throughout: the info target stores uv DELTAS (small numbers), not absolute uvs.
            hb.infoRead = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf) { filterMode = FilterMode.Bilinear };
            hb.infoWrite = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf) { filterMode = FilterMode.Bilinear };
            hb.geo = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf) { filterMode = FilterMode.Point };
            hb.valid = false;
        }

        private static void ReleaseDenoiserTargets(EyeHistory hb)
        {
            Drop(ref hb.infoRead);
            Drop(ref hb.infoWrite);
            Drop(ref hb.geo);
            Drop(ref hb.stabRead);
            Drop(ref hb.stabWrite);
            hb.stabValid = false;
        }

        // (1.0.0) "Stabilize Output"'s pair, at the picture's size (full res: it is skipped under Half-Resolution Denoising
        // Everything, whose history is half size)
        private static void EnsureStab(EyeHistory hb, int w, int h)
        {
            if (hb.stabRead != null && hb.stabRead.width == w && hb.stabRead.height == h) return;
            Drop(ref hb.stabRead);
            Drop(ref hb.stabWrite);
            hb.stabRead = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf) { filterMode = FilterMode.Bilinear };
            hb.stabWrite = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf) { filterMode = FilterMode.Bilinear };
            hb.stabValid = false;
        }

        //--- the cost log's two readings ---------------------------------------------------------------------

        private static bool HalfDnPasses()
        {
            if (_pDnShadeH == -2)
            {
                _pDnShadeH = _mat.FindPass("DNSHADEHALF");
                _pDnReprojH = _mat.FindPass("DNREPROJECTHALF");
                _pDnTemporalH = _mat.FindPass("DNTEMPORALHALF");
                _pDnGeoH = _mat.FindPass("DNGEOHALF");
                _pDnBlurH = _mat.FindPass("DNBLURHALF");
                _pDnUp = _mat.FindPass("DNUP");
            }
            return _pDnShadeH >= 0 && _pDnReprojH >= 0 && _pDnTemporalH >= 0 && _pDnGeoH >= 0 && _pDnBlurH >= 0 && _pDnUp >= 0;
        }

        private static bool StabPass()
        {
            if (_pDnStab == -2) _pDnStab = _mat.FindPass("DNSTABILIZE");
            return _pDnStab >= 0;
        }

        private static bool BenchPasses()
        {
            if (_passCount == -2)
            {
                _passCount = _mat.FindPass("SSRCOUNT");
                _passProbe = _mat.FindPass("SSRPROBE");
            }
            return _passCount >= 0 && _passProbe >= 0 && SystemInfo.supportsAsyncGPUReadback;
        }

        // How much of the picture reflects, by the test every pass makes.
        private static void CountReflecting(RenderTexture sceneCopy)
        {
            SsrBench.ShareAsk = false;
            if (!BenchPasses()) { SsrBench.NoCount(); return; }
            RenderTextureFormat fmt = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.R8) ? RenderTextureFormat.R8 : RenderTextureFormat.ARGB32;
            RenderTexture rt = new RenderTexture(sceneCopy.width, sceneCopy.height, 0, fmt, RenderTextureReadWrite.Linear) { filterMode = FilterMode.Point };
            Graphics.Blit(sceneCopy, rt, _mat, _passCount);
            SsrBench.Count(rt);
        }

        // How the rays go: the trace once more with this frame's uniforms, into a target of the trace's size.
        private static void ProbeTrace(RenderTexture sceneCopy, int w, int h)
        {
            SsrBench.ProbeAsk = false;
            if (!BenchPasses() || !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat)) { SsrBench.NoProbe(); return; }
            RenderTexture rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear) { filterMode = FilterMode.Point };
            Graphics.Blit(sceneCopy, rt, _mat, _passProbe);
            SsrBench.Probe(rt);
        }

        // The new passes exist only in a bundle built from the current shader.
        private static bool DenoiserReady()
        {
            if (!SsrConfig.NewDenoiser.Value) return false;
            if (_mat.passCount > PassDnFilter) return true;
            if (!_warnedStaleBundle)
            {
                _warnedStaleBundle = true;
                Plugin.MyLog.LogWarning("[ReflectSix] New Denoiser needs a rebuilt 'ssr' bundle (shader has " +
                                        _mat.passCount + " passes, needs 16). Using the old path.");
            }
            return false;
        }

        // Glossy prefilter pyramid: mip 0 = the shaded reflections, each level below a blurred half-res
        // downsample. The composite cone-traces it. Same Blit+CopyTexture machinery as ever; Trilinear so
        // fractional LODs blend smoothly between levels. One per view, like the history.
        private static void BuildGlossPyramid(EyeHistory hb, RenderTexture refl, int w, int h)
        {
            if (hb.gloss == null || hb.gloss.width != w || hb.gloss.height != h)
            {
                Drop(ref hb.gloss);
                hb.gloss = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf)
                {
                    useMipMap = true,
                    autoGenerateMips = false,
                    filterMode = FilterMode.Trilinear,
                };
                hb.gloss.Create();
                // Levels down to ~8px — blur wider than that reads as murk, not gloss.
                hb.glossMips = 1;
                while (hb.glossMips < 6 && (Mathf.Min(w, h) >> hb.glossMips) >= 8) hb.glossMips++;
            }
            RenderTexture _gloss = hb.gloss;
            int _glossMips = hb.glossMips;

            Graphics.CopyTexture(refl, 0, 0, _gloss, 0, 0);
            RenderTexture prev = refl;
            for (int i = 1; i < _glossMips; i++)
            {
                RenderTexture cur = RenderTexture.GetTemporary(
                    Mathf.Max(1, w >> i), Mathf.Max(1, h >> i), 0, RenderTextureFormat.ARGBHalf);
                cur.filterMode = FilterMode.Bilinear;
                Graphics.Blit(prev, cur, _mat, PassGlossDown);
                Graphics.CopyTexture(cur, 0, 0, _gloss, 0, i);
                if (prev != refl) RenderTexture.ReleaseTemporary(prev);
                prev = cur;
            }
            if (prev != refl) RenderTexture.ReleaseTemporary(prev);

            _mat.SetTexture("_SsrGlossTex", _gloss);
            _mat.SetFloat("_SsrGlossMaxMip", _glossMips - 1);
        }

        // Max absolute element difference between two matrices (the live-vs-getter probe).
        private static float MatDelta(Matrix4x4 a, Matrix4x4 b)
        {
            float d = 0f;
            for (int i = 0; i < 16; i++) d = Mathf.Max(d, Mathf.Abs(a[i] - b[i]));
            return d;
        }

        // Double-precision 4x4 inverse (glMatrix cofactor scheme). Matrix4x4.inverse is float32; its
        // cancellation error leaves inv*M measurably off identity, which a per-pixel unproject turns into
        // a constant sub-pixel error. The projection inverse is the one matrix the whole trace hangs off.
        private static Matrix4x4 InverseD(Matrix4x4 m)
        {
            double a00 = m.m00, a01 = m.m01, a02 = m.m02, a03 = m.m03;
            double a10 = m.m10, a11 = m.m11, a12 = m.m12, a13 = m.m13;
            double a20 = m.m20, a21 = m.m21, a22 = m.m22, a23 = m.m23;
            double a30 = m.m30, a31 = m.m31, a32 = m.m32, a33 = m.m33;

            double b00 = a00 * a11 - a01 * a10;
            double b01 = a00 * a12 - a02 * a10;
            double b02 = a00 * a13 - a03 * a10;
            double b03 = a01 * a12 - a02 * a11;
            double b04 = a01 * a13 - a03 * a11;
            double b05 = a02 * a13 - a03 * a12;
            double b06 = a20 * a31 - a21 * a30;
            double b07 = a20 * a32 - a22 * a30;
            double b08 = a20 * a33 - a23 * a30;
            double b09 = a21 * a32 - a22 * a31;
            double b10 = a21 * a33 - a23 * a31;
            double b11 = a22 * a33 - a23 * a32;

            double det = b00 * b11 - b01 * b10 + b02 * b09 + b03 * b08 - b04 * b07 + b05 * b06;
            if (System.Math.Abs(det) < 1e-30)
                return m.inverse;   // degenerate — fall back
            double id = 1.0 / det;

            Matrix4x4 r = default;
            r.m00 = (float)((a11 * b11 - a12 * b10 + a13 * b09) * id);
            r.m01 = (float)((a02 * b10 - a01 * b11 - a03 * b09) * id);
            r.m02 = (float)((a31 * b05 - a32 * b04 + a33 * b03) * id);
            r.m03 = (float)((a22 * b04 - a21 * b05 - a23 * b03) * id);
            r.m10 = (float)((a12 * b08 - a10 * b11 - a13 * b07) * id);
            r.m11 = (float)((a00 * b11 - a02 * b08 + a03 * b07) * id);
            r.m12 = (float)((a32 * b02 - a30 * b05 - a33 * b01) * id);
            r.m13 = (float)((a20 * b05 - a22 * b02 + a23 * b01) * id);
            r.m20 = (float)((a10 * b10 - a11 * b08 + a13 * b06) * id);
            r.m21 = (float)((a01 * b08 - a00 * b10 - a03 * b06) * id);
            r.m22 = (float)((a30 * b04 - a31 * b02 + a33 * b00) * id);
            r.m23 = (float)((a21 * b02 - a20 * b04 - a23 * b00) * id);
            r.m30 = (float)((a11 * b07 - a10 * b09 - a12 * b06) * id);
            r.m31 = (float)((a00 * b09 - a01 * b07 + a02 * b06) * id);
            r.m32 = (float)((a31 * b01 - a30 * b03 - a32 * b00) * id);
            r.m33 = (float)((a20 * b03 - a21 * b01 + a22 * b00) * id);
            return r;
        }

        private static Vector3 FarRay(Matrix4x4 invProj, float u, float v)
        {
            Vector4 q = invProj * new Vector4(u * 2f - 1f, v * 2f - 1f, 1f, 1f);
            return new Vector3(q.x / q.w, q.y / q.w, q.z / q.w);
        }

        // viewRayToFar(uv) is a plane in uv whenever the projection's w does not depend on x and y - every
        // projection the game renders with. Handed to the shader as three vectors (two mads a tap instead of
        // a matrix and a divide); the flag stays 0, and the shader uses the matrix, if a projection ever is not.
        private static void SetFarRay(Matrix4x4 invProj)
        {
            Vector3 r0 = FarRay(invProj, 0f, 0f);
            Vector3 rx = FarRay(invProj, 1f, 0f) - r0, ry = FarRay(invProj, 0f, 1f) - r0;
            float err = Mathf.Max((FarRay(invProj, 1f, 1f) - (r0 + rx + ry)).magnitude,
                                  (FarRay(invProj, 0.3f, 0.7f) - (r0 + rx * 0.3f + ry * 0.7f)).magnitude);
            bool plane = err <= 1e-4f * Mathf.Max(r0.magnitude, 1f);
            _mat.SetVector("_SsrRay0", new Vector4(r0.x, r0.y, r0.z, plane ? 1f : 0f));
            _mat.SetVector("_SsrRayX", rx);
            _mat.SetVector("_SsrRayY", ry);
        }

        public static void Render(TOD_Scattering scattering, RenderTexture source)
        {
            if (_mat == null) LoadShader();
            if (_mat == null) return;                       // bundle missing -> scene stays vanilla

            Camera cam = scattering.GetComponent<Camera>();
            if (cam == null) return;
            bool scope = IsScope(cam);
            if (scope && !ScopeActive(cam)) return;

            cam.depthTextureMode |= DepthTextureMode.Depth; // the tracer walks the depth buffer

            // Per-eye view/projection (mono in flatscreen, each eye in VR). VR eye projections are
            // off-axis asymmetric, so they MUST come from the stereo getters. The LIVE projection matrix
            // is the raster's (verified: nothing in EFT rewrites camera state between raster and image
            // effects), and it carries whatever sub-pixel jitter the raster used — TAA on flatscreen,
            // VRJitterComponent under VR upscalers — so rays and depth share one lattice automatically.
            Camera.MonoOrStereoscopicEye eye = (Camera.current != null) ? Camera.current.stereoActiveEye : cam.stereoActiveEye;
            // LIVE matrices for BOTH modes (2026-07-17). This hook fires mid-eye-render, where the live
            // camera matrices ARE the raster truth for the eye being drawn — exactly what flatscreen
            // (which works) reads. The old VR path guessed the eye via Camera.current.stereoActiveEye and
            // pulled the stereo getters; any staleness/eye-mix there costs centimetre depth error that
            // only GRAZING rays notice (steep rays cross silhouettes decisively) — the VR-only grazing
            // failure fingerprint. The eye enum stays for history keying; the debug log prints the
            // live-vs-getter delta so a mismatch shows up as a number, not a vibe.
            Matrix4x4 view = cam.worldToCameraMatrix;
            Matrix4x4 proj = cam.projectionMatrix;
            Matrix4x4 projNoJit = cam.nonJitteredProjectionMatrix;   // reprojection pair: AVERAGE jitter, don't chase it

            // View-space tracer uniforms (the whole matrix diet: projection pair + view rotation).
            Matrix4x4 invProj = InverseD(proj);
            _mat.SetMatrix("_SsrProjM", proj);
            _mat.SetMatrix("_SsrInvProjM", invProj);
            SetFarRay(invProj);
            _mat.SetFloat("_SsrFastOn", 0f);     // until this frame's roughness texture is drawn (the debug views never draw it)
            _mat.SetMatrix("_SsrViewM", view);

            _mat.SetInt("_SsrSteps", SsrBench.Steps > 0 ? SsrBench.Steps : Mathf.Max(1, SsrConfig.Steps.Value));
            // 0 = the march as it was (with its skip buffers), 1 = one depth read a turn, 2 = that with every
            // crossing bisected (the old march's rays).
            if (_marchCan < 0) _marchCan = _mat.HasProperty("_SsrMarchV2") ? 1 : 0;
            int marchSet = _marchCan == 0 || SsrConfig.March.Value == ESsrMarch.Previous ? 0
                         : SsrConfig.March.Value == ESsrMarch.FastExact ? 2 : 1;
            int march = _marchCan == 1 && SsrBench.March >= 0 ? SsrBench.March : marchSet;
            _mat.SetFloat("_SsrMarch", march);
            _mat.SetFloat("_SsrStridePx", Mathf.Max(1f, SsrConfig.MarchStride.Value));
            _mat.SetFloat("_SsrStrideGrow", Mathf.Clamp(SsrConfig.StrideGrowth.Value, 1f, 1.1f));
            _mat.SetFloat("_SsrMaxDist", SsrConfig.MaxDistance.Value);
            _mat.SetFloat("_SsrThickness", SsrConfig.Thickness.Value);
            _mat.SetFloat("_SsrIntensity", SsrConfig.Intensity.Value);
            _mat.SetFloat("_SsrSmoothCutoff", SsrConfig.SmoothnessCutoff.Value);
            _mat.SetFloat("_SsrEdgeFade", SsrConfig.EdgeFade.Value);
            _mat.SetFloat("_SsrEdgeSoft", 0f);   // Silhouette Fade removed: faded hits let sky bleed through
            _mat.SetFloat("_SsrFirefly", SsrConfig.Firefly.Value);
            _mat.SetFloat("_SsrFoliage", SsrConfig.Foliage.Value);
            _mat.SetFloat("_SsrFoliageF0", SsrConfig.FoliageF0.Value);
            _mat.SetFloat("_SsrSelfZ", SsrConfig.SelfExclusion.Value);
            _mat.SetFloat("_SsrSkimMin", SsrConfig.SkimRejection.Value);
            _mat.SetFloat("_SsrSpecAA", SsrConfig.SpecularAA.Value);
            _mat.SetFloat("_SsrStochastic", SsrConfig.Stochastic.Value && !SsrBench.Mirror ? 1f : 0f);
            // Denoiser v2 + half-res trace: the traced texel walks each 2x2 quad over four frames, so
            // every full-res pixel gets its own ray in turn; the sample sequence then advances once per
            // cycle (stepping it every frame would hand each origin every 4th sample of the sequence).
            bool dn = DenoiserReady();
            bool halfRes = SsrConfig.HalfRes.Value;
            int quadPhase = Time.frameCount & 3;
            Vector4 quadOfs = (dn && halfRes)
                ? new Vector4((quadPhase == 1 || quadPhase == 2) ? 1f : 0f, (quadPhase == 1 || quadPhase == 3) ? 1f : 0f)
                : Vector4.zero;
            _mat.SetFloat("_SsrDn", dn ? 1f : 0f);
            _mat.SetVector("_SsrQuadOfs", Vector4.zero);   // set around the half-res trace only (debug views trace full res)
            Texture2D blue = dn ? BlueNoise() : null;
            if (blue != null) _mat.SetTexture("_SsrBlueTex", blue);
            _mat.SetFloat("_SsrBlueOn", blue != null ? 1f : 0f);
            // The stochastic noise sequence. Frame-keyed (not eye-keyed): both eyes draw the SAME
            // pattern, so the noise never disagrees binocularly.
            _mat.SetFloat("_SsrFrameIdx", ((dn && halfRes) ? (Time.frameCount >> 2) : Time.frameCount) & 1023);
            _mat.SetFloat("_SsrResolveSpacing", halfRes ? 2f : 1f);
            // World size of one screen pixel at 1m view depth — set BEFORE the shade pass now: the
            // cone-traced fetch needs it there, not just in the composite.
            _mat.SetFloat("_SsrPixWorld", 2f / (Mathf.Abs(proj.m11) * source.height));
            // Reflection probe = Keep (the dropdown was removed): our reflections composite OVER the
            // scene's baked-probe term, no subtraction, no SSRenabled forcing. The clean-material state
            // comes from Suppress Tarkov SSR Render instead (game SSR on + never rendered).
            _mat.SetFloat("_SsrProbeMode", 0f);
            _mat.SetFloat("_SsrFlipRayY", 0f);   // VR Trace Flip removed (was a confirmed-wrong diagnostic)

            // Sky in reflections — hybrid: the on-screen sky-exit fetch always works (gated only by the
            // config); the sky-MAP half needs CloudSix's published map AND feeds the roof grid (a small
            // world-anchored grid of upward rays — per-surface indoor suppression without triggers).
            bool skyExitOn = SsrConfig.SkyRefl.Value;
            Texture skyMapTex = Shader.GetGlobalTexture("_SsrSkyReflMap");
            bool skyMapOn = skyExitOn && skyMapTex != null;
            _mat.SetFloat("_SsrSkyOn", skyExitOn ? 1f : 0f);
            _mat.SetFloat("_SsrSkyMapOn", skyMapOn ? 1f : 0f);
            // A missed ray carries on through TraceSix's voxel world when TraceSix runs (see GiVoxelsUp). A scope
            // keeps its own roof grid: what it looks at is out where the voxels are 2 m and 8 m a side.
            // The march is a pass of its own (PassWorld): a bundle without it has no voxel rays.
            //   (2026-10-23) Only the voxel march needs CloudSix's map: it answers just how much of a ray gets out to the sky, and
            // without a map there is no sky to show for it. The ray-traced rays show what they HIT, map or not - to here they were
            // off without CloudSix ("RT reflections aren't working" for a player whose CloudSix published no map).
            bool worldOn = !scope && SsrConfig.WorldRays.Value && _mat.passCount > PassWorld && GiVoxelsUp();
            bool voxOn = worldOn && skyMapOn;
            _mat.SetFloat("_SsrVoxOn", voxOn ? 1f : 0f);
            // "Off-Screen Rays Ray-Traced": TraceSix traced this frame's rays (SsrRtRays made them at AfterReflections, for
            // the trace's size) - its answers stand in for the voxel march, one to a trace pixel.
            if (!scope && !cam.stereoEnabled && cam.GetComponent<SsrRtRays>() == null) cam.gameObject.AddComponent<SsrRtRays>();
            int traceW = halfRes ? (source.width + 1) / 2 : source.width, traceH = halfRes ? (source.height + 1) / 2 : source.height;
            float giReflAt = Shader.GetGlobalFloat(IdGiReflFrame);
            bool rtOn = worldOn && SsrConfig.WorldRaysRt.Value && PassRtRays >= 0 && SsrRtRays.OfferedFrame == Time.frameCount
                        && SsrRtRays.OfferW == traceW && SsrRtRays.OfferH == traceH && giReflAt > 0f && Time.frameCount - giReflAt < 0.5f;
            _mat.SetFloat("_SsrRtOn", rtOn ? 1f : 0f);
            // (no march: the BVH answers every ray - its misses need the map for their sky; without one the normal trace runs,
            // which still finds the sky on screen, with the ray-traced rays for what leaves the picture)
            bool rtOnly = rtOn && skyMapOn && SsrConfig.RtOnly.Value;
            _mat.SetFloat("_SsrRtOnly", rtOnly ? 1f : 0f);
            _mat.SetFloat("_SsrVoxBlend", SsrConfig.WorldRaysSoft.Value ? 1f : 0f);
            // Soft edges read through the GPU's filter where the copies can be kept (VoxCopies).
            bool softOn = SsrConfig.WorldRays.Value && SsrConfig.WorldRaysSoft.Value;
            bool vxCov = voxOn && softOn && _mat.passCount > PassVxCopy && VoxCopies();
            if (!softOn) DropVoxCopies();
            _mat.SetFloat("_SsrVxCovOn", vxCov ? 1f : 0f);
            _mat.SetVector("_SsrVoxEdge", VoxEdge);
            // One voxel march to a block of trace pixels (PassWorld, after the trace). Until its answers are
            // in, the shade passes are told there are none.
            int block = rtOn ? 1 : Mathf.Max(1, SsrConfig.WorldRaySpacing.Value / (halfRes ? 2 : 1));
            _mat.SetVector("_SsrWorldBlock", new Vector4(block, 0f, 0f, 0f));
            // Lobe-matched sky blur needs the map's mip chain; an old CloudSix DLL publishes mip-0 only
            // (mipmapCount 1) and this degrades to the sharp fetch.
            _mat.SetFloat("_SsrSkyMapMaxLod", skyMapOn ? Mathf.Max(0, skyMapTex.mipmapCount - 1) : 0f);
            // Rain flow on walls: strength = config x a LIVE rain envelope. RainController.Intensity is
            // the game's own static current-rain value; the envelope trails it by ~20s so rivulets keep
            // running briefly after a shower instead of snapping off (the persistent SHEEN is the game's
            // wetness system, not ours). Once per frame, not per eye.
            if (Time.frameCount != _rainEnvFrame)
            {
                _rainEnvFrame = Time.frameCount;
                float rain = 0f;
                try { rain = Mathf.Clamp01(RainController.Intensity); } catch { }
                _rainFlowEnv = Mathf.Max(rain, _rainFlowEnv - Time.deltaTime / 20f);
            }
            float rainFlow = SsrConfig.RainFlow.Value * _rainFlowEnv;
            bool rainFlowOn = rainFlow > 0.01f;
            _mat.SetFloat("_SsrRainFlow", rainFlowOn ? rainFlow : 0f);
            _mat.SetFloat("_SsrTime", Time.time);

            // The inverse view + roof grid serve the sky map AND the rain-flow gating (a roofed wall is
            // shiny, not wet). The grid defaults to "roofed" when never updated, so flow fails CLOSED.
            _mat.SetMatrix("_SsrInvViewM", InverseD(view));
            SsrSkyGrid grid = scope ? SsrSkyGrid.Scope : SsrSkyGrid.Main;
            if (skyMapOn || rainFlowOn)
            {
                Matrix4x4 toWorld = view.inverse;
                Vector3 camPos = toWorld.MultiplyPoint3x4(Vector3.zero);
                grid.Update(scope ? AimPoint(camPos, toWorld.MultiplyVector(Vector3.back).normalized) : camPos);
                grid.Apply(_mat);
            }

            // Reflected-path fog (FogSix bridge): only when FogSix published its froxel volume within
            // the last few frames (stamp = frameCount; stale/absent = legacy fog mode or mod missing).
            Vector4 fogStamp = Shader.GetGlobalVector("_FogSixVolStamp");
            bool fogOn = fogStamp.x > 0f && (Time.frameCount - fogStamp.x) < 5f &&
                         Shader.GetGlobalTexture("_FogSixScatterVol") != null;   // released RT = unbound
            _mat.SetFloat("_SsrFogOn", fogOn ? 1f : 0f);
            _mat.SetFloat("_SsrFogFlipV", fogStamp.y);

            _mat.SetFloat("_SsrDepthAugOn", 0f);   // Glass Occludes Reflections removed

            if (SsrConfig.Debug.Value && Time.unscaledTime >= _nextProbeLog)
            {
                _nextProbeLog = Time.unscaledTime + 5f;
                // Buffer-registration probe (the flatscreen round-11 lesson, re-run for VR): the trace
                // assumes source, the camera viewport, and the depth texture share one pixel lattice.
                // A mismatch here = errors that GROW with ray length (contact reflections fine, distant
                // objects never found) — check these agree before chasing anything in the shader.
                RenderTexture dt = Shader.GetGlobalTexture("_CameraDepthTexture") as RenderTexture;
                Camera pc = Camera.current != null ? Camera.current : cam;
                // Live-vs-stereo-getter deltas: nonzero here = the old getter path was tracing with
                // matrices that are NOT what rastered this eye (the VR grazing-failure suspect).
                string mdelta = "";
                if (eye != Camera.MonoOrStereoscopicEye.Mono)
                {
                    Camera.StereoscopicEye sE = (eye == Camera.MonoOrStereoscopicEye.Right)
                        ? Camera.StereoscopicEye.Right : Camera.StereoscopicEye.Left;
                    mdelta = $" dProj={MatDelta(proj, cam.GetStereoProjectionMatrix(sE)):E2}" +
                             $" dView={MatDelta(view, cam.GetStereoViewMatrix(sE)):E2}";
                }
                Plugin.MyLog.LogInfo($"[ReflectSix] {eye}{(scope ? " scope" : "")} src={source.width}x{source.height} " +
                    $"px={pc.pixelWidth}x{pc.pixelHeight} rect=({pc.rect.x:F3},{pc.rect.y:F3},{pc.rect.width:F3},{pc.rect.height:F3}) " +
                    $"depth={(dt != null ? dt.width + "x" + dt.height : "null")}{mdelta} " +
                    $"sky={(skyMapOn ? "MAP(seamless)" : (skyExitOn ? "SCREEN-ONLY(no CloudSix map: seams at the ray-budget edge)" : "off"))} " +
                    $"offScreenRays={(rtOn ? (rtOnly ? "ALL rays through TraceSix ray tracing (no screen-space march)" : "TraceSix ray tracing (one mirror ray per trace pixel, what it hits shown)") : voxOn ? (SsrConfig.WorldRaysSoft.Value ? "TraceSix voxels (soft edges" + (vxCov ? ", filtered copies, " + _vxCovDrawn + " redrawn so far)" : ", read voxel by voxel)") : "TraceSix voxels") + ", one march per " + block + "x" + block + " trace pixels" : "roof grid")} " +
                    $"rainFlow={rainFlow:F2}");
            }

            // The composite reads the scene while writing over it, so we work from a copy: trace from the
            // copy, write the composite back into `source` in place (downstream fog then runs source->dest).
            // MIPPED (2026-07-18): the shade pass cone-traces its colour fetches from this chain — the
            // pre-filtered hit neighbourhood is what kills the re-reflected probe-sparkle specks, and the
            // firefly clamp reads its coarse-mip reference from it too.
            RenderTextureDescriptor scDesc = new RenderTextureDescriptor(
                source.width, source.height, source.format, 0)
            {
                useMipMap = true,
                autoGenerateMips = false,
            };
            RenderTexture sceneCopy = RenderTexture.GetTemporary(scDesc);
            sceneCopy.filterMode = FilterMode.Trilinear;
            // Every other temporary, named here so that a frame stopped early (the cost log: SsrBench.UpTo)
            // lets go of whatever it has by then.
            RenderTexture minZ8 = null, minZ64 = null, alphaRT = null, rayRT = null, recFull = null, recHalf = null, world = null, worldCol = null;
            RenderTexture reflFull = null, guide = null, pre = null, accum = null, filtered = null;
            RenderTexture reflHalf = null, preHalf = null, upFull = null;     // Half-Resolution Denoising
            int upTo = SsrBench.UpTo;
            // Copy THROUGH the sanitize pass (NaN kill + 2000 radiance cap) so the mip chain is built
            // from a scrubbed image — GenerateMips averages, so one raw hot pixel would otherwise
            // contaminate every mip texel covering it (the "more specks than before" bug), and nothing
            // upstream is guaranteed to have cleaned the scene (PPv2 only NaN-kills it when its own
            // opaque chain runs, e.g. when Tarkov's SSR actually renders).
            Graphics.Blit(source, sceneCopy, _mat, PassSanitize);
            sceneCopy.GenerateMips();
            _mat.SetFloat("_SsrSceneMaxMip", Mathf.Max(0, sceneCopy.mipmapCount - 1));
            if (upTo == 1) goto done;

            // Empty-space skip buffers: the nearest depth of every 8x8 and 64x64 block of depth texels.
            // Built from the scene copy so the passes see the same texel lattice the march snaps to.
            // (Only the march as it was skips: in the new one the skip cost more than it saved.)
            bool skipOn = useEmptySpaceSkip && march == 0 && !SsrBench.NoSkip && _mat.passCount > PassMinZDown
                          && SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RFloat);
            if (skipOn)
            {
                int w8 = (source.width + 7) / 8, h8 = (source.height + 7) / 8;
                int w64 = (w8 + 7) / 8, h64 = (h8 + 7) / 8;
                minZ8 = RenderTexture.GetTemporary(w8, h8, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear);
                minZ64 = RenderTexture.GetTemporary(w64, h64, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear);
                minZ8.filterMode = minZ64.filterMode = FilterMode.Point;
                minZ8.wrapMode = minZ64.wrapMode = TextureWrapMode.Clamp;
                _mat.SetVector("_SsrMinZ8Size", new Vector4(w8, h8, 1f / w8, 1f / h8));
                _mat.SetVector("_SsrMinZ64Size", new Vector4(w64, h64, 1f / w64, 1f / h64));
                Graphics.Blit(sceneCopy, minZ8, _mat, PassMinZ);
                Graphics.Blit(minZ8, minZ64, _mat, PassMinZDown);
                _mat.SetTexture("_SsrMinZ8", minZ8);
                _mat.SetTexture("_SsrMinZ64", minZ64);
            }
            _mat.SetFloat("_SsrMinZOn", skipOn ? 1f : 0f);

            ESsrDebugView dbg = SsrConfig.DebugView.Value;
            // HistoryLength / MotionCheck are drawn by the v2 composite over the normal pipeline.
            bool dnDebug = dbg == ESsrDebugView.HistoryLength || dbg == ESsrDebugView.MotionCheck;
            _mat.SetFloat("_SsrDebug", dnDebug ? 0f : (int)dbg);
            if (dbg != ESsrDebugView.Off && !dnDebug)
            {
                // Debug views: full-res raw single-frame trace — shows the tracer's true output.
                _mat.SetFloat("_SsrTraceScale", 1f);
                Graphics.Blit(sceneCopy, source, _mat, PassDebug);
                if (minZ8 != null) { RenderTexture.ReleaseTemporary(minZ8); RenderTexture.ReleaseTemporary(minZ64); }
                RenderTexture.ReleaseTemporary(sceneCopy);
                return;
            }

            // 0) Every pixel's roughness, once: the trace, the voxel answers, the guide and the resolve read it
            // (with Specular AA on, measuring it is nine texture reads, and the frame used to do that about
            // eight times a pixel).
            bool fast = dn && _mat.passCount > PassDnRay && SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RFloat);
            if (!scope) SsrBench.See(source.width, source.height, dn, voxOn, _marchCan == 1, marchSet);
            if (fast)
            {
                alphaRT = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear);
                alphaRT.filterMode = FilterMode.Point;
                Graphics.Blit(sceneCopy, alphaRT, _mat, PassDnAlpha);
                _mat.SetTexture("_SsrAlphaTex", alphaRT);
                _mat.SetFloat("_SsrFastOn", 1f);
            }
            if (SsrBench.ShareAsk && !scope) CountReflecting(sceneCopy);
            if (upTo == 2) goto done;

            // 1) Trace into a full-res HIT-RECORD buffer (uv offset to the hit, hit distance, confidence).
            // ARGBFloat, not Half: a half-quantized whole-screen uv offset moves the colour fetch by most
            // of a pixel on far hits.
            recFull = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGBFloat);
            recFull.filterMode = FilterMode.Point;
            if (halfRes)
            {
                // Ceil'd half size: EFT's resolution scaling produces ODD source sizes; the pixel-ID
                // mapping in trace/upsample assumes half pixel h <-> full texels 2h/2h+1 everywhere.
                int hw = (source.width + 1) / 2, hh = (source.height + 1) / 2;
                recHalf = RenderTexture.GetTemporary(hw, hh, 0, RenderTextureFormat.ARGBFloat);
                recHalf.filterMode = FilterMode.Point;
                _mat.SetVector("_SsrQuadOfs", quadOfs);
                _mat.SetFloat("_SsrTraceScale", 2f);
                Graphics.Blit(sceneCopy, recHalf, _mat, PassTrace);
                if (SsrBench.ProbeAsk && !scope) ProbeTrace(sceneCopy, hw, hh);
                if (upTo == 3 && SsrBench.NoUpsample) goto done;
                _mat.SetTexture("_SsrHalfTex", recHalf);   // the v2 resolve reuses these rays directly
                Graphics.Blit(sceneCopy, recFull, _mat, PassUpsample);
            }
            else
            {
                _mat.SetFloat("_SsrTraceScale", 1f);
                Graphics.Blit(sceneCopy, recFull, _mat, PassTrace);
                if (SsrBench.ProbeAsk && !scope) ProbeTrace(sceneCopy, source.width, source.height);
            }
            if (minZ8 != null)
            {
                RenderTexture.ReleaseTemporary(minZ8);
                RenderTexture.ReleaseTemporary(minZ64);
                minZ8 = minZ64 = null;
            }
            if (upTo == 3) goto done;

            // 1b) The rays that left the picture, asked of TraceSix's voxels: one to a block, in a target the
            // trace's size over the block's side. Every pixel of it marches (or its block has nothing to
            // ask) - marched inside the trace, the few rays that march held up every batch of pixels.
            if (rtOn)
            {
                // TraceSix's answers (_GiRtRefl, one per trace pixel), re-packed: how much got out + the surface, and what was hit
                world = RenderTexture.GetTemporary(traceW, traceH, 0, RenderTextureFormat.ARGBHalf);
                worldCol = RenderTexture.GetTemporary(traceW, traceH, 0, RenderTextureFormat.ARGBHalf);
                world.filterMode = worldCol.filterMode = FilterMode.Point;
                Graphics.Blit(sceneCopy, world, _mat, _pWorldRtOpen);
                Graphics.Blit(sceneCopy, worldCol, _mat, _pWorldRtCol);
                _mat.SetTexture("_SsrWorldTex", world);
                _mat.SetTexture("_SsrWorldCol", worldCol);
                _mat.SetVector("_SsrWorldBlock", new Vector4(1f, 1f, 0f, 0f));
            }
            else if (voxOn)
            {
                RenderTexture recs = halfRes ? recHalf : recFull;
                world = RenderTexture.GetTemporary((recs.width + block - 1) / block, (recs.height + block - 1) / block, 0, RenderTextureFormat.ARGBHalf);
                world.filterMode = FilterMode.Point;
                if (!halfRes) _mat.SetTexture("_SsrHitTex", recFull);
                Graphics.Blit(sceneCopy, world, _mat, PassWorld);
                _mat.SetTexture("_SsrWorldTex", world);
                _mat.SetVector("_SsrWorldBlock", new Vector4(block, 1f, 0f, 0f));
            }
            if (upTo == 4) goto done;

            float smoothing = Mathf.Clamp(SsrConfig.TemporalSmoothing.Value, 0f, 0.95f);
            bool temporal = smoothing > 0.001f;

            // Half-Resolution Denoising (the shader's "the denoiser at half resolution"): only with the blurs
            // round the accumulation and the guide (the upsample's surface), and a bundle that has the passes.
            ESsrHalfDenoise halfDnSet = SsrBench.HalfDn >= 0 ? (ESsrHalfDenoise)SsrBench.HalfDn : SsrConfig.HalfDenoise.Value;
            bool halfDnOk = halfDnSet != ESsrHalfDenoise.Off && dn && temporal && fast && SsrConfig.Stochastic.Value
                            && SsrConfig.DenoiseBlur.Value && _mat.passCount > PassDnBlur && HalfDnPasses();
            bool halfAll = halfDnOk && halfDnSet == ESsrHalfDenoise.Everything;
            bool halfBlur = halfDnOk && halfDnSet == ESsrHalfDenoise.BlursOnly;
            int dnW = (source.width + 1) / 2, dnH = (source.height + 1) / 2;     // as the trace's half size
            if (halfDnOk) _mat.SetVector("_SsrDnHalf", new Vector4(source.width, source.height, dnW, dnH));
            _mat.SetFloat("_SsrDnHistHalf", halfAll ? 1f : 0f);
            _mat.SetFloat("_SsrDnUp", 0f);

            EyeHistory thisView = View(cam, eye);
            // A view that has not been drawn for some frames (a scope is switched off between aims) holds a
            // picture of somewhere else, and matrices from then: it starts over.
            if (Time.frameCount - thisView.lastFrame > 3) thisView.valid = false;
            thisView.lastFrame = Time.frameCount;
            EyeHistory h = temporal ? thisView : null;
            if (h != null)
            {
                // Everything: the history (and its info, last frame's surface) is half size; a size change starts it over.
                EnsureHistory(h, halfAll ? dnW : source.width, halfAll ? dnH : source.height);
                if (dn) EnsureDenoiserTargets(h, halfAll ? dnW : source.width, halfAll ? dnH : source.height);
                else if (h.geo != null) ReleaseDenoiserTargets(h);
                if (h.dn != dn) { h.valid = false; h.dn = dn; }   // the two paths store different things
            }
            bool histOk = h != null && h.valid;

            // 2) Shade the records at full res into the reflection buffer. v2 outputs DEMODULATED
            // radiance (the composite applies the material) and sizes each ray's prefilter by how much
            // history the pixel already has.
            reflFull = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGBHalf);
            reflFull.filterMode = FilterMode.Bilinear;
            _mat.SetTexture("_SsrHitTex", recFull);   // also read by the composite (hit distance -> cone)
            if (dn)
            {
                _mat.SetTexture("_SsrDnInfoPrev", histOk ? (Texture)h.infoRead : Texture2D.blackTexture);
                if (fast && SsrConfig.Stochastic.Value)
                {
                    // The surface packed for whoever reads a NEIGHBOUR's (the resolve's reused rays, the
                    // blurs' taps), and every traced ray's hit shaded once at trace resolution: the resolve
                    // then reads a finished ray where it used to shade each of its four again per pixel.
                    guide = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
                    guide.filterMode = FilterMode.Point;   // packed values: never interpolated
                    Graphics.Blit(sceneCopy, guide, _mat, PassDnGuide);
                    _mat.SetTexture("_SsrGuideTex", guide);
                    RenderTexture recs = halfRes ? recHalf : recFull;
                    rayRT = RenderTexture.GetTemporary(recs.width, recs.height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                    rayRT.filterMode = FilterMode.Point;
                    Graphics.Blit(sceneCopy, rayRT, _mat, PassDnRay);
                    _mat.SetTexture("_SsrRayTex", rayRT);
                }
                if (upTo == 5) { thisView.valid = false; goto done; }
                if (halfAll)
                {
                    reflHalf = RenderTexture.GetTemporary(dnW, dnH, 0, RenderTextureFormat.ARGBHalf);
                    reflHalf.filterMode = FilterMode.Bilinear;
                    Graphics.Blit(sceneCopy, reflHalf, _mat, _pDnShadeH);
                }
                else Graphics.Blit(sceneCopy, reflFull, _mat, PassDnShade);
            }
            else
            {
                Graphics.Blit(sceneCopy, reflFull, _mat, PassShade);
            }
            if (upTo == 5 || upTo == 6) { thisView.valid = false; goto done; }

            // 3) Temporal resolve. Old path: one fixed blend against camera-reprojected history. v2: a
            // reprojection pass decides per pixel where the history lives (surface or reflected-image
            // position, validated against last frame's surface, motion vectors for moving reflectors) and
            // how many frames it is worth; the resolve then averages over exactly that many.
            // Temporal Smoothing scales the caps: 0.8 = 32 frames for the roughest lobes, 4 for
            // mirror-like ones (deterministic rays carry no noise, so they never need a long history).
            float capRough = Mathf.Clamp(Mathf.LerpUnclamped(1f, 32f, smoothing / 0.8f), 1f, 48f);
            float capMirror = Mathf.Clamp(capRough / 8f, 1f, 4f);
            if (!SsrConfig.Stochastic.Value) capRough = capMirror;
            // Motion vectors only when the camera already renders them and the buffer shares our pixel
            // lattice (EFT renders through sub-viewports under resolution scaling).
            Texture mvTex = Shader.GetGlobalTexture("_CameraMotionVectorsTexture");
            bool mvOn = dn && useMotionVectors
                        && (cam.depthTextureMode & DepthTextureMode.MotionVectors) != 0
                        && mvTex != null && mvTex.width == source.width && mvTex.height == source.height;
            _mat.SetFloat("_SsrMvOn", mvOn ? 1f : 0f);
            _mat.SetVector("_SsrDnFrames", new Vector4(capRough, capMirror,
                dbg == ESsrDebugView.HistoryLength ? 1f : (dbg == ESsrDebugView.MotionCheck ? 2f : 0f), 0f));

            // 2b) Rough lobes: blur the frame along the surface BEFORE it is accumulated (so the frame the
            // history is judged by is a usable estimate) and the accumulated result once more (that goes
            // to the history and to the screen). See ReflectSix.shader "THE BLURS ROUND THE ACCUMULATION".
            bool blurOn = dn && temporal && SsrConfig.Stochastic.Value && SsrConfig.DenoiseBlur.Value
                          && _mat.passCount > PassDnBlur;
            _mat.SetFloat("_SsrBlurOn", blurOn ? 1f : 0f);
            RenderTexture frame = reflFull;      // what the temporal passes take as this frame
            float blurTurn = (Time.frameCount & 1023) * 0.618034f;
            if (blurOn)
            {
                if (guide == null)               // a bundle with the blurs but without the shared ray shading
                {
                    guide = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
                    guide.filterMode = FilterMode.Point;
                    Graphics.Blit(sceneCopy, guide, _mat, PassDnGuide);
                    _mat.SetTexture("_SsrGuideTex", guide);
                }
                float h1 = source.height;
                _mat.SetVector("_SsrBlurB", new Vector4(blurPre * h1, blurLobeShare, blurPost * h1, blurFall));
                _mat.SetVector("_SsrBlurC", new Vector4(blurMinPx, blurNormal, blurNormalFresh, blurRoom));
                _mat.SetVector("_SsrBlurA", new Vector4(0f, blurTurn % 1f, 0f, 0f));
                if (halfAll)
                {
                    // half res from the half-res resolve
                    preHalf = RenderTexture.GetTemporary(dnW, dnH, 0, RenderTextureFormat.ARGBHalf);
                    preHalf.filterMode = FilterMode.Bilinear;
                    _mat.SetTexture("_SsrDnIn", reflHalf);
                    _mat.SetFloat("_SsrDnInHalf", 1f);
                    Graphics.Blit(sceneCopy, preHalf, _mat, _pDnBlurH);
                    frame = preHalf;
                }
                else if (halfBlur)
                {
                    // half res from the full-res resolve, then back to full res: wide lobes from the half-res
                    // blur, narrow ones blurred at full res as before (blended over halfBlurLo..Hi px of radius)
                    preHalf = RenderTexture.GetTemporary(dnW, dnH, 0, RenderTextureFormat.ARGBHalf);
                    preHalf.filterMode = FilterMode.Bilinear;
                    _mat.SetTexture("_SsrDnIn", reflFull);
                    _mat.SetFloat("_SsrDnInHalf", 0f);
                    Graphics.Blit(sceneCopy, preHalf, _mat, _pDnBlurH);
                    pre = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGBHalf);
                    pre.filterMode = FilterMode.Bilinear;
                    _mat.SetTexture("_SsrDnIn", preHalf);
                    _mat.SetVector("_SsrDnUpA", new Vector4(0f, halfBlurLo, halfBlurHi, 0f));
                    Graphics.Blit(reflFull, pre, _mat, _pDnUp);
                    frame = pre;
                }
                else
                {
                    pre = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGBHalf);
                    pre.filterMode = FilterMode.Bilinear;
                    Graphics.Blit(reflFull, pre, _mat, PassDnBlur);
                    frame = pre;
                }
            }
            else if (halfAll) frame = reflHalf;   // (not reached: Everything needs the blurs)
            if (upTo == 7) { thisView.valid = false; goto done; }

            RenderTexture resolved = reflFull;
            Texture dnInfo = Texture2D.blackTexture;
            if (temporal)
            {
                if (histOk)
                {
                    // At rest the previous matrices equal the current ones: hand the SAME matrix to both
                    // sides and the residual-cancelled delta is exactly zero by construction.
                    bool rest = h.prevView == view && h.prevProjNoJit == projNoJit;
                    Matrix4x4 reprojPrev = rest ? projNoJit : h.prevProjNoJit * (h.prevView * InverseD(view));
                    _mat.SetMatrix("_SsrReprojPrev", reprojPrev);
                    _mat.SetMatrix("_SsrProjNoJitM", projNoJit);
                    _mat.SetTexture("_SsrHistTex", h.read);
                    if (dn)
                    {
                        _mat.SetMatrix("_SsrPrevToCurM", rest ? Matrix4x4.identity : view * InverseD(h.prevView));
                        _mat.SetMatrix("_SsrPrevInvProjM", h.prevInvProj);
                        _mat.SetTexture("_SsrPrevGeo", h.geo);
                        _mat.SetTexture("_SsrDnInfoPrev", h.infoRead);
                        // accumulate, then (the blurs on) blur by how much history each pixel has: that is the
                        // new history. The cost log's row without that blur accumulates straight into it.
                        bool postBlur = blurOn && upTo != 8;
                        RenderTexture acc = h.write;
                        if (postBlur)
                        {
                            accum = RenderTexture.GetTemporary(halfAll ? dnW : source.width, halfAll ? dnH : source.height, 0, RenderTextureFormat.ARGBHalf);
                            accum.filterMode = FilterMode.Bilinear;
                            acc = accum;
                        }
                        if (halfAll)
                        {
                            // the same three at half res: drawn from the scene copy, this frame in through _SsrDnIn
                            _mat.SetTexture("_SsrDnIn", frame);
                            _mat.SetFloat("_SsrDnInHalf", 1f);
                            Graphics.Blit(sceneCopy, h.infoWrite, _mat, _pDnReprojH);
                            _mat.SetTexture("_SsrDnInfo", h.infoWrite);
                            Graphics.Blit(sceneCopy, acc, _mat, _pDnTemporalH);
                            if (postBlur)
                            {
                                _mat.SetVector("_SsrBlurA", new Vector4(1f, (blurTurn + 0.37f) % 1f, 0f, 0f));
                                _mat.SetTexture("_SsrDnIn", accum);
                                Graphics.Blit(sceneCopy, h.write, _mat, _pDnBlurH);
                            }
                        }
                        else
                        {
                            Graphics.Blit(frame, h.infoWrite, _mat, PassDnReproject);
                            _mat.SetTexture("_SsrDnInfo", h.infoWrite);
                            Graphics.Blit(frame, acc, _mat, PassDnTemporal);
                            if (postBlur)
                            {
                                _mat.SetVector("_SsrBlurA", new Vector4(1f, (blurTurn + 0.37f) % 1f, 0f, 0f));
                                Graphics.Blit(accum, h.write, _mat, PassDnBlur);
                            }
                        }
                    }
                    else
                    {
                        _mat.SetFloat("_SsrTemporal", smoothing);
                        Graphics.Blit(reflFull, h.write, _mat, PassTemporal);
                    }
                }
                else
                {
                    Graphics.Blit(frame, h.write);   // first frame: seed the history with this frame's resolve
                    if (dn) Graphics.Blit(Texture2D.blackTexture, h.infoWrite);   // history length 0 everywhere
                }
                if (dn)
                {
                    Graphics.Blit(sceneCopy, h.geo, _mat, halfAll ? _pDnGeoH : PassDnGeo);
                    RenderTexture ti = h.infoRead; h.infoRead = h.infoWrite; h.infoWrite = ti;
                    dnInfo = h.infoRead;
                    h.prevInvProj = invProj;
                }
                h.prevView = view;
                h.prevProjNoJit = projNoJit;
                h.valid = true;
                RenderTexture t = h.read; h.read = h.write; h.write = t;
                resolved = h.read;
            }
            // 3a) (1.0.0) "Stabilize Output" (NVIDIA ReBLUR's temporal stabilization, DNSTABILIZE): the denoised reflection
            // averaged once more over the frames before, held to within the grain of this frame's - for the picture only.
            // Its history is found where pass 11 found the reflection's (this frame's info), so it starts over where that
            // does. Needs the blurs (their accumulation before the blur after it is the grain's measure); full res only.
            bool stabOn = dn && temporal && histOk && blurOn && !halfAll && accum != null && SsrConfig.Stabilize.Value && StabPass();
            if (stabOn)
            {
                EnsureStab(h, source.width, source.height);
                _mat.SetTexture("_SsrDnInfo", dnInfo);
                _mat.SetTexture("_SsrStabHist", h.stabRead);
                _mat.SetTexture("_SsrStabRef", accum);
                _mat.SetVector("_SsrStab", new Vector4(stabFrames, stabBox, h.stabValid ? 1f : 0f, stabFloor));
                Graphics.Blit(resolved, h.stabWrite, _mat, _pDnStab);
                RenderTexture ts = h.stabRead; h.stabRead = h.stabWrite; h.stabWrite = ts;
                resolved = h.stabRead;
            }
            if (h != null) h.stabValid = stabOn;
            if (upTo == 8 || upTo == 9) goto done;

            if (SsrConfig.Debug.Value && Time.unscaledTime >= _nextDnLog)
            {
                _nextDnLog = Time.unscaledTime + 5f;
                Plugin.MyLog.LogInfo($"[ReflectSix] {eye}{(scope ? " scope" : "")} denoiser={(dn ? "v2" : "old")} passes={_mat.passCount} " +
                    $"temporal={(temporal ? "on" : "off")} frames={capRough:F0}/{capMirror:F0} " +
                    $"motionVectors={(mvOn ? "on" : "off")} halfRes={halfRes} quadPhase={quadPhase} " +
                    $"blueNoise={(blue != null ? "on" : "hash")} " +
                    $"onceAFrame={(fast ? "on" : "off (bundle has no passes for it)")} " +
                    $"roughBlur={(blurOn ? "on" : (SsrConfig.DenoiseBlur.Value && dn && _mat.passCount <= PassDnBlur ? "off (bundle has no blur passes)" : "off"))} " +
                    $"stabilize={(stabOn ? "on" : (SsrConfig.Stabilize.Value && blurOn && !StabPass() ? "off (bundle has no stabilize pass)" : "off"))} " +
                    $"spatialFilter={(useSpatialFilter && !blurOn)} " +
                    $"halfDenoise={(halfAll ? "Everything" : halfBlur ? "BlursOnly" : halfDnSet == ESsrHalfDenoise.Off ? "off" : "off (needs Rough Reflection Denoising + the rebuilt bundle)")} " +
                    $"emptySpaceSkip={(skipOn ? "on" : (useEmptySpaceSkip ? "off (bundle has no skip passes)" : "off"))}");
                if (skyMapOn || rainFlowOn) Plugin.MyLog.LogInfo("[ReflectSix] " + (scope ? "scope " : "") + grid.Describe());
            }

            // 3b) v2 spatial filter, for display only: the history above stays unfiltered, so nothing
            // blurs cumulatively. It needs this frame's history lengths, hence temporal on. Not with the
            // blurs round the accumulation: what they wrote to the history is already what is shown.
            _mat.SetTexture("_SsrDnInfo", dnInfo);
            if (dn && temporal && useSpatialFilter && SsrConfig.Stochastic.Value && !blurOn)
            {
                filtered = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGBHalf);
                filtered.filterMode = FilterMode.Bilinear;
                _mat.SetVector("_SsrDnFilter", new Vector4(filterSpacing, filterSpacingFresh, 0f, 0f));
                Graphics.Blit(resolved, filtered, _mat, PassDnFilter);
                resolved = filtered;
            }

            // 4) Composite over the scene, in place into `source`. Old path / deterministic rays:
            // cone-traced against the glossy pyramid (roughness x hit-distance blur, contact hardening).
            // v2 with stochastic rays has already integrated the lobe, so the pyramid is only built when
            // the user asks for a Reflection Softness floor.
            bool glossOn = SsrConfig.Glossy.Value;
            if (dn && SsrConfig.Stochastic.Value) glossOn = glossOn && SsrConfig.ReflSoftness.Value > 0.01f;
            // Everything: the history is half res; the composite upsamples it by depth and normal - or, for
            // the glossy pyramid (a full-res source), it is upsampled here first.
            bool upInComposite = halfAll && resolved != reflFull;
            if (upInComposite && glossOn)
            {
                upFull = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGBHalf);
                upFull.filterMode = FilterMode.Bilinear;
                _mat.SetTexture("_SsrDnIn", resolved);
                _mat.SetVector("_SsrDnUpA", new Vector4(1f, 0f, 0f, 0f));
                Graphics.Blit(sceneCopy, upFull, _mat, _pDnUp);
                resolved = upFull;
                upInComposite = false;
            }
            _mat.SetFloat("_SsrDnUp", upInComposite ? 1f : 0f);
            if (glossOn) BuildGlossPyramid(thisView, resolved, source.width, source.height);
            _mat.SetFloat("_SsrGlossOn", glossOn ? 1f : 0f);
            _mat.SetFloat("_SsrGlossScale", SsrConfig.GlossyScale.Value);
            _mat.SetFloat("_SsrGlossAniso", glossOn ? SsrConfig.GlossyAniso.Value : 0f);
            _mat.SetFloat("_SsrGlossMinLod", SsrConfig.ReflSoftness.Value);
            _mat.SetTexture("_SsrReflTex", resolved);
            _mat.SetFloat("_SsrFresnelCap", smoothnessCappedFresnel ? 1f : 0f);
            Graphics.Blit(sceneCopy, source, _mat, dn ? PassDnComposite : PassComposite);

        done:
            // A frame stopped early (the cost log) has a temporary as the target: the camera's own is bound
            // again before any of them is let go.
            if (upTo != 0) Graphics.SetRenderTarget(source);
            if (minZ8 != null) { RenderTexture.ReleaseTemporary(minZ8); RenderTexture.ReleaseTemporary(minZ64); }
            if (filtered != null) RenderTexture.ReleaseTemporary(filtered);
            if (guide != null) RenderTexture.ReleaseTemporary(guide);
            if (alphaRT != null) RenderTexture.ReleaseTemporary(alphaRT);
            if (rayRT != null) RenderTexture.ReleaseTemporary(rayRT);
            if (pre != null) RenderTexture.ReleaseTemporary(pre);
            if (accum != null) RenderTexture.ReleaseTemporary(accum);
            if (reflHalf != null) RenderTexture.ReleaseTemporary(reflHalf);
            if (preHalf != null) RenderTexture.ReleaseTemporary(preHalf);
            if (upFull != null) RenderTexture.ReleaseTemporary(upFull);
            if (recHalf != null) RenderTexture.ReleaseTemporary(recHalf);
            if (world != null) RenderTexture.ReleaseTemporary(world);
            if (worldCol != null) RenderTexture.ReleaseTemporary(worldCol);
            if (reflFull != null) RenderTexture.ReleaseTemporary(reflFull);
            if (recFull != null) RenderTexture.ReleaseTemporary(recFull);
            RenderTexture.ReleaseTemporary(sceneCopy);
        }

        public static void LoadShader()
        {
            if (_mat != null || _triedLoad) return;
            _triedLoad = true;
            try
            {
                // (renamed from SSRSix 2026-10-16: the bundle "ssr" with "ssrMat" became "reflectsix" with "reflectSixMat")
                string bundlePath = Path.Combine(BepInEx.Paths.PluginPath, "ReflectSix", "Assets", "reflectsix");
                AssetBundle bundle = File.Exists(bundlePath) ? AssetBundle.LoadFromFile(bundlePath) : null;
                if (bundle == null)
                {
                    Plugin.MyLog.LogError("[ReflectSix] Failed to load AssetBundle at " + bundlePath + " - build the 'reflectsix' bundle in Unity and copy it there.");
                    return;
                }
                _mat = bundle.LoadAsset<Material>("reflectSixMat");
                if (_mat == null)
                    Plugin.MyLog.LogError("[ReflectSix] 'reflectSixMat' material not found in the bundle (an old SSRSix bundle?).");
                else
                    Plugin.MyLog.LogInfo("[ReflectSix] Material loaded.");
            }
            catch (System.Exception e)
            {
                Plugin.MyLog.LogError("[ReflectSix] Shader load failed: " + e);
            }
        }
    }
}
