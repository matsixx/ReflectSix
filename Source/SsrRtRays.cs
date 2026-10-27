using UnityEngine;
using UnityEngine.Rendering;

namespace ReflectSix.Source
{
    // "Off-Screen Rays Ray-Traced" (2026-10-16): the rays that leave the picture traced by TraceSix's hardware ray tracing.
    // TraceSix hands its rays to its D3D12 side before the game lights the frame (BeforeLighting), and the trace runs beside
    // the lighting; this makes ReflectSix's own ones in time to go with them: at AfterReflections on the main camera, one
    // ray per trace pixel (ReflectSix.shader SSRRTRAYS - the pixel and ray the trace traces there this frame), published as
    // _SsrRtRayO / _SsrRtRayD with the frame they are for (_SsrRtRayFrame). Each is the ray the trace draws there this
    // frame (the mirror, or a GGX lobe sample with the same noise), not just the mirror. TraceSix traces them, lights their ends and
    // publishes _GiRtRefl (_GiRtReflFrame); SsrRenderer's world passes read that instead of marching the voxels.
    // Only while TraceSix's rays run on this camera (_GiRtFrame) and flatscreen (TraceSix's ray tracing is not in VR).
    internal class SsrRtRays : MonoBehaviour
    {
        private const CameraEvent Evt = CameraEvent.AfterReflections;
        private static readonly int IdGiRtFrame = Shader.PropertyToID("_GiRtFrame"), IdRayO = Shader.PropertyToID("_SsrRtRayO"),
            IdRayD = Shader.PropertyToID("_SsrRtRayD"), IdRayFrame = Shader.PropertyToID("_SsrRtRayFrame"),
            IdViewToWorld = Shader.PropertyToID("_SsrRtViewToWorld"), IdUVToView = Shader.PropertyToID("_SsrRtUVToView"),
            IdCamPos = Shader.PropertyToID("_SsrRtCamPos"), IdGrid = Shader.PropertyToID("_SsrRtGrid"), IdQuad = Shader.PropertyToID("_SsrRtQuad"),
            IdSample = Shader.PropertyToID("_SsrRtSample");
        // How far a reflection ray looks, metres: past that it reached the sky.
        public static float RayLength = 300f;

        // The frame the rays were made for and their size (the trace's): SsrRenderer takes TraceSix's answers only for these.
        public static int OfferedFrame = -1, OfferW, OfferH;

        private Camera _cam;
        private CommandBuffer _cb;
        private bool _attached;
        private RenderTexture _o, _d;

        private void Awake() => _cam = GetComponent<Camera>();

        private void OnPreRender()
        {
            Material mat = SsrRenderer.Mat;
            int pass = SsrRenderer.PassRtRays;
            float giAt = Shader.GetGlobalFloat(IdGiRtFrame);
            bool want = mat != null && pass >= 0 && SsrConfig.Enabled.Value && SsrConfig.WorldRays.Value && SsrConfig.WorldRaysRt.Value
                        && SsrConfig.SkyRefl.Value && !_cam.stereoEnabled && giAt > 0f && Time.frameCount - giAt <= 2f;
            if (!want)
            {
                Detach();
                return;
            }
            bool half = SsrConfig.HalfRes.Value;
            int w = _cam.pixelWidth, h = _cam.pixelHeight;
            int tw = half ? (w + 1) / 2 : w, th = half ? (h + 1) / 2 : h;
            if (_o == null || !_o.IsCreated() || _o.width != tw || _o.height != th || _d == null || !_d.IsCreated())
            {
                Free();
                _o = Target("ReflectSix RT Ray Starts", tw, th);
                _d = Target("ReflectSix RT Ray Directions", tw, th);
            }
            if (_cb == null) _cb = new CommandBuffer { name = "ReflectSix RT Rays" };
            Matrix4x4 p = _cam.projectionMatrix;
            _cb.Clear();
            _cb.SetGlobalMatrix(IdViewToWorld, _cam.cameraToWorldMatrix);
            _cb.SetGlobalVector(IdUVToView, new Vector4(2f / p.m00, 2f / p.m11, (p.m02 - 1f) / p.m00, (p.m12 - 1f) / p.m11));
            Vector3 cp = _cam.transform.position;   // (TraceSix keeps its ray starts relative to the same)
            _cb.SetGlobalVector(IdCamPos, new Vector4(cp.x, cp.y, cp.z, 0f));
            _cb.SetGlobalVector(IdGrid, new Vector4(half ? 2f : 1f, RayLength, w, h));
            _cb.SetGlobalVector(IdQuad, SsrRenderer.QuadOfsFor(half));
            _cb.SetGlobalVector(IdSample, SsrRenderer.SampleFor(half));   // (the trace's ray this frame, not the mirror)
            _cb.SetRenderTarget(new RenderTargetIdentifier[] { _o, _d }, _o);
            _cb.DrawProcedural(Matrix4x4.identity, mat, pass, MeshTopology.Triangles, 3);
            if (!_attached)
            {
                _cam.AddCommandBuffer(Evt, _cb);
                _attached = true;
            }
            Shader.SetGlobalTexture(IdRayO, _o);
            Shader.SetGlobalTexture(IdRayD, _d);
            Shader.SetGlobalFloat(IdRayFrame, Time.frameCount);
            OfferedFrame = Time.frameCount;
            OfferW = tw;
            OfferH = th;
        }

        private static RenderTexture Target(string name, int w, int h)
        {
            var rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
                { name = name, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
            rt.Create();
            return rt;
        }

        private void Detach()
        {
            if (_attached && _cam != null && _cb != null) _cam.RemoveCommandBuffer(Evt, _cb);
            _attached = false;
            OfferedFrame = -1;
        }

        private void Free()
        {
            foreach (RenderTexture rt in new[] { _o, _d })
                if (rt != null) { rt.Release(); Destroy(rt); }
            _o = _d = null;
        }

        private void OnDisable() => Detach();

        private void OnDestroy()
        {
            Detach();
            _cb?.Release();
            _cb = null;
            Free();
        }
    }
}
