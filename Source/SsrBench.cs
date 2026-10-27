using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReflectSix.Source
{
    // Debug: what each part of ReflectSix costs, measured ("Log What SSR Costs"). Steps through a list of set-ups
    // while the player stands still and logs the frame time of each.
    // The switches here are never saved: they override the settings only while a run lasts.
    internal static class SsrBench
    {
        public static bool Off;         // no reflections drawn, as if Enabled were off
        // The frame's passes with the later ones LEFT OUT: a pass is priced by what the frame costs more with
        // it than without it (done twice, a pass shows what a repeat costs - not its first run in a frame:
        // TraceSix 0.24.30). 0 = all of them; 1..9 = up to and with StageName[n - 1] (SsrRenderer.Render). Below
        // 0 nothing is composited: the picture has no reflections while such a row runs.
        public static int UpTo;
        // The trace's own rows, all with UpTo 3 (nothing after the trace runs, so nothing downstream sees them):
        public static bool NoUpsample;  // stop after the trace itself (half-resolution trace: before its upsample)
        public static int Steps;        // 0 = Ray Steps as set, else this many
        public static int March = -1;   // -1 = Ray March as set, else the shader's _SsrMarch (0 as it was, 1 fast, 2 fast and exact)
        public static bool NoSkip;      // the march as it was, without its empty-space skip (the buffers are not built)
        public static bool Mirror;      // every ray along the mirror direction, whatever Stochastic Reflections says
        public static int HalfDn = -1;  // -1 = Half-Resolution Denoising as set, else that value (ESsrHalfDenoise)
        public static bool Running;
        public static bool ShareAsk;    // the main view's next frame counts the pixels that reflect
        public static bool ProbeAsk;    // ... and reads how its rays go
        public static long OwnTicks;    // Stopwatch ticks spent in SsrRenderer.Render (SsrOpaquePatch, while Running)

        private static readonly string[] StageName =
        {
            "the scene copy and its mips",
            "+ the skip buffers and the roughness",
            "+ the trace",
            "+ the off-screen rays",
            "+ the guide and the rays' hits",
            "+ the resolve",
            "+ the blur before the accumulation",
            "+ the reprojection and the accumulation",
            "+ the blur after it",
            "+ the composite: everything",
        };
        private static readonly string[] MarchName = { "as it was (Previous)", "Fast", "FastExact" };

        // What the main view's last frame ran with.
        private static int _w, _h, _marchSet;
        private static bool _dn, _vox, _marchCan;

        public static void See(int w, int h, bool dn, bool vox, bool marchCan, int marchSet)
        {
            _w = w; _h = h; _dn = dn; _vox = vox; _marchCan = marchCan; _marchSet = marchSet;
        }

        //--- how much of the picture reflects, and how the rays go ---------------------------------------------

        // -1 = not read back (yet), -2 = the bundle has no pass for it
        private static long _reflecting = -1, _all;
        // How a ray ended (traceSSR's endReason): the rays, and their loop turns / depth reads / crossings bisected.
        private const int Ends = 7;
        private static readonly string[] EndName =
        {
            "shot no ray", "hit something", "passed behind something, found nothing", "ran out of steps",
            "marched their whole way, found nothing", "left onto sky in the picture", "skipped the player's own depth, found nothing",
        };
        private static readonly long[] _rays = new long[Ends];
        private static readonly double[] _iters = new double[Ends], _reads = new double[Ends], _refines = new double[Ends];
        private static readonly float[] _longest = new float[Ends];
        private static int _probe = -1;     // -1 not read back, -2 no pass, 1 read

        public static void NoCount() { _reflecting = -2; }
        public static void NoProbe() { _probe = -2; }

        // rt: white where a pixel reflects. Let go once read.
        public static void Count(RenderTexture rt)
        {
            _all = (long)rt.width * rt.height;
            AsyncGPUReadback.Request(rt, 0, r =>
            {
                _reflecting = -1;
                if (!r.hasError)
                {
                    var d = r.GetData<byte>();
                    int stride = Mathf.Max(1, d.Length / Mathf.Max(1, r.width * r.height));
                    long n = 0;
                    for (int i = 0; i < d.Length; i += stride) if (d[i] > 127) n++;
                    _reflecting = n;
                }
                Free(rt);
            });
        }

        // rt: the probe pass's picture (ARGBFloat: how the ray ended, loop turns, depth reads, crossings bisected).
        public static void Probe(RenderTexture rt)
        {
            AsyncGPUReadback.Request(rt, 0, r =>
            {
                if (!r.hasError)
                {
                    var d = r.GetData<float>();
                    for (int e = 0; e < Ends; e++) { _rays[e] = 0; _iters[e] = _reads[e] = _refines[e] = 0.0; _longest[e] = 0f; }
                    for (int i = 0; i + 3 < d.Length; i += 4)
                    {
                        int e = Mathf.Clamp(Mathf.RoundToInt(d[i]), 0, Ends - 1);
                        _rays[e]++;
                        _iters[e] += d[i + 1];
                        _reads[e] += d[i + 2];
                        _refines[e] += d[i + 3];
                        if (d[i + 1] > _longest[e]) _longest[e] = d[i + 1];
                    }
                    _probe = 1;
                }
                Free(rt);
            });
        }

        private static void Free(RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            Object.Destroy(rt);
        }

        //--- the run -----------------------------------------------------------------------------------------

        private struct Setup { public string name; public bool off, noUpsample, noSkip, mirror; public int upTo, steps, march, halfDn; }   // halfDn: 0 as set, else ESsrHalfDenoise + 1   // steps: 0 as set, -1 half, 1 one; march: 0 as set, else _SsrMarch + 1

        private const int All = 1, Off0 = 0, Off1 = 2, Stages = 3, Off2 = 13;
        private const int Before = 14, TraceOnly = 15, TrA = 16, OldRow = 17, OldNoSkipRow = 18, TrB = 19, ExactRow = 20, HalfSteps = 21, TrC = 22,
                          OneStep = 23, MirrorRow = 24, TrD = 25, BeforeB = 26, Off3 = 27;
        private const int DnOffA = 28, DnBlurs = 29, DnAll = 30, DnOffB = 31;

        private static List<Setup> Setups()
        {
            var s = new List<Setup>
            {
                new Setup { name = "mod off", off = true },
                new Setup { name = "everything, as set" },
                new Setup { name = "mod off, again", off = true },
            };
            for (int k = 0; k < StageName.Length; k++)
                s.Add(new Setup { name = StageName[k], upTo = k < StageName.Length - 1 ? k + 1 : 0 });
            s.Add(new Setup { name = "mod off, a third time", off = true });
            // The trace taken apart: everything after it left out in every row.
            s.Add(new Setup { name = "trace rows: all before the trace", upTo = 2 });
            s.Add(new Setup { name = "trace rows: + the trace itself", upTo = 3, noUpsample = true });
            s.Add(new Setup { name = "trace rows: + its upsample (the whole trace stage)", upTo = 3 });
            s.Add(new Setup { name = "trace rows: the stage, the march as it was", upTo = 3, march = 1 });
            s.Add(new Setup { name = "trace rows: the stage, as it was without its skip", upTo = 3, march = 1, noSkip = true });
            s.Add(new Setup { name = "trace rows: the whole stage, again", upTo = 3 });
            s.Add(new Setup { name = "trace rows: the stage, fast march, exact", upTo = 3, march = 3 });
            s.Add(new Setup { name = "trace rows: the stage with half the ray steps", upTo = 3, steps = -1 });
            s.Add(new Setup { name = "trace rows: the whole stage, a third time", upTo = 3 });
            s.Add(new Setup { name = "trace rows: the stage with one ray step (no march)", upTo = 3, steps = 1 });
            s.Add(new Setup { name = "trace rows: the stage with mirror rays only", upTo = 3, mirror = true });
            s.Add(new Setup { name = "trace rows: the whole stage, a fourth time", upTo = 3 });
            s.Add(new Setup { name = "trace rows: all before the trace, again", upTo = 2 });
            s.Add(new Setup { name = "mod off, a fourth time", off = true });
            // Half-Resolution Denoising, each value against Off measured either side of it.
            s.Add(new Setup { name = "denoiser rows: half-res denoising off", halfDn = 1 });
            s.Add(new Setup { name = "denoiser rows: half-res denoising, blurs only", halfDn = 2 });
            s.Add(new Setup { name = "denoiser rows: half-res denoising, everything", halfDn = 3 });
            s.Add(new Setup { name = "denoiser rows: half-res denoising off, again", halfDn = 1 });
            return s;
        }

        public static IEnumerator Run()
        {
            if (Running) yield break;
            Running = true;
            Camera cam = Camera.main;
            Vector3 startPos = cam != null ? cam.transform.position : Vector3.zero;
            // No frame cap while this runs: at the cap a frame takes what the cap says, whatever the mod costs.
            int cap = Application.targetFrameRate, vsync = QualitySettings.vSyncCount;
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;
            List<Setup> setups = Setups();
            int n = setups.Count;
            var mean = new float[n];
            var median = new float[n];
            var worst = new float[n];
            var own = new float[n];
            var frames = new int[n];
            var t = new List<float>(2048);
            int stepsSet = Mathf.Max(1, SsrConfig.Steps.Value);
            _reflecting = -1;
            _probe = -1;
            for (int i = 0; i < n; i++)
            {
                Off = setups[i].off;
                UpTo = setups[i].upTo;
                NoUpsample = setups[i].noUpsample;
                NoSkip = setups[i].noSkip;
                Mirror = setups[i].mirror;
                March = setups[i].march - 1;
                HalfDn = setups[i].halfDn - 1;
                Steps = setups[i].steps == 0 ? 0 : setups[i].steps < 0 ? Mathf.Max(1, stepsSet / 2) : setups[i].steps;
                if (i == All) { ShareAsk = true; ProbeAsk = true; }     // as set, in the settling time
                float until = Time.unscaledTime + 1.5f;                 // the history fills again
                while (Time.unscaledTime < until) yield return null;
                t.Clear();
                OwnTicks = 0;
                until = Time.unscaledTime + 3f;
                while (Time.unscaledTime < until)
                {
                    yield return null;
                    t.Add(Time.unscaledDeltaTime * 1000f);
                }
                t.Sort();
                float sum = 0f;
                foreach (float x in t) sum += x;
                frames[i] = t.Count;
                mean[i] = t.Count > 0 ? sum / t.Count : 0f;
                median[i] = t.Count > 0 ? t[t.Count / 2] : 0f;
                worst[i] = t.Count > 0 ? t[Mathf.Min(t.Count - 1, (int)(t.Count * 0.95f))] : 0f;
                own[i] = t.Count > 0 ? (float)(OwnTicks * 1000.0 / Stopwatch.Frequency / t.Count) : 0f;
            }
            Off = false;
            UpTo = 0;
            NoUpsample = false;
            NoSkip = false;
            Mirror = false;
            March = -1;
            HalfDn = -1;
            Steps = 0;
            ShareAsk = false;
            ProbeAsk = false;
            Application.targetFrameRate = cap;
            QualitySettings.vSyncCount = vsync;
            Running = false;

            bool half = SsrConfig.HalfRes.Value;
            var sb = new StringBuilder();
            sb.Append("[ReflectSix] Cost: ").Append(_w).Append('x').Append(_h).Append(", frame cap ").Append(cap).Append(" and vsync ").Append(vsync)
              .Append(" (both lifted for the run), display ").Append(Screen.currentResolution.refreshRateRatio.value.ToString("F0"))
              .Append(" Hz; new denoiser ").Append(_dn ? "on" : "OFF (the stage rows below are not its passes)")
              .Append(", half-resolution trace ").Append(half ? "on" : "off (so the trace has no upsample)")
              .Append(", stochastic ").Append(SsrConfig.Stochastic.Value ? "on" : "off (so the mirror-ray row is the same thing)")
              .Append(", rough reflection denoising ").Append(SsrConfig.DenoiseBlur.Value ? "on" : "off (so the two blur rows add nothing)")
              .Append(", temporal smoothing ").Append(SsrConfig.TemporalSmoothing.Value.ToString("F2"))
              .Append(", off-screen rays through TraceSix ").Append(_vox ? "running" : "not running (so that row adds nothing)")
              .Append(", ray march ").Append(_marchCan ? MarchName[Mathf.Clamp(_marchSet, 0, 2)]
                  : "AS IT WAS - this bundle has no other (rebuild 'ssr'): the three march rows below are all the same march")
              .Append(", smoothness cutoff ").Append(SsrConfig.SmoothnessCutoff.Value.ToString("F2"))
              .Append(", ray steps ").Append(stepsSet).Append(", march stride ").Append(SsrConfig.MarchStride.Value.ToString("F1"))
              .Append(" px growing x").Append(SsrConfig.StrideGrowth.Value.ToString("F2")).Append(", max distance ").Append(SsrConfig.MaxDistance.Value.ToString("F0")).Append(" m");
            if (cam != null)
                sb.Append("; the camera moved ").Append((cam.transform.position - startPos).magnitude.ToString("F1")).Append(" m during the run (stand still for a clean one).");

            sb.Append("\n  Of the picture's ").Append(_all).Append(" pixels ");
            if (_reflecting == -2) sb.Append("- not counted: this bundle has no pass for it (rebuild 'ssr').");
            else if (_reflecting < 0 || _all <= 0) sb.Append("- not counted: the count never came back.");
            else sb.Append(_reflecting).Append(" reflect (").Append((100.0 * _reflecting / _all).ToString("F1")).Append("%).");

            // How the rays go: the numbers the march's cost is made of.
            if (_probe == -2) sb.Append("\n  How the rays go: not read - this bundle has no pass for it (rebuild 'ssr').");
            else if (_probe < 0) sb.Append("\n  How the rays go: not read - the reading never came back.");
            else
            {
                long px = 0, rays = 0;
                double it = 0, rd = 0, rf = 0;
                float longest = 0f;
                for (int e = 0; e < Ends; e++)
                {
                    px += _rays[e];
                    if (e == 0) continue;
                    rays += _rays[e];
                    it += _iters[e];
                    rd += _reads[e];
                    rf += _refines[e];
                    longest = Mathf.Max(longest, _longest[e]);
                }
                sb.Append("\n  How the rays go (one frame, as set): of the trace's ").Append(px).Append(" pixels ").Append(rays).Append(" (")
                  .Append(px > 0 ? (100.0 * rays / px).ToString("F1") : "?").Append("%) march a ray. A ray runs ")
                  .Append(rays > 0 ? (it / rays).ToString("F1") : "?").Append(" turns of the loop, the longest ").Append(longest.ToString("F0"))
                  .Append(" (what its group of pixels waits for), reads the depth ").Append(rays > 0 ? (rd / rays).ToString("F1") : "?")
                  .Append(" times in them and bisects ").Append(rays > 0 ? (rf / rays).ToString("F2") : "?").Append(" crossings")
                  .Append(_marchSet == 0 || !_marchCan ? " (the march as it was: six or seven more reads each, not in that count, and turns that hop an empty block read nothing)"
                                                         : " (their reads are turns of the loop like any other)")
                  .Append(". That is ").Append((it / 1000.0).ToString("F0")).Append(" thousand turns a frame. By how the ray ends - share of the rays / turns each / the longest / crossings bisected each / share of all the turns:");
                for (int e = 1; e < Ends; e++)
                {
                    if (_rays[e] == 0) continue;
                    sb.Append("\n    ").Append(EndName[e].PadRight(46)).Append((100.0 * _rays[e] / System.Math.Max(1, rays)).ToString("F1").PadLeft(5)).Append("% /")
                      .Append((_iters[e] / _rays[e]).ToString("F1").PadLeft(6)).Append(" /").Append(_longest[e].ToString("F0").PadLeft(5)).Append(" /")
                      .Append((_refines[e] / _rays[e]).ToString("F2").PadLeft(6)).Append(" /").Append((it > 0 ? 100.0 * _iters[e] / it : 0.0).ToString("F1").PadLeft(5)).Append("%");
                }
            }

            for (int i = 0; i < n; i++)
                sb.Append("\n  ").Append(setups[i].name.PadRight(52)).Append(": median ").Append(median[i].ToString("F2")).Append(" ms (")
                  .Append(median[i] > 0f ? (1000f / median[i]).ToString("F0") : "?").Append(" fps), mean ").Append(mean[i].ToString("F2"))
                  .Append(" ms, 95% under ").Append(worst[i].ToString("F2")).Append(" ms, ").Append(frames[i])
                  .Append(" frames; this mod's own code on the main thread ").Append(own[i].ToString("F3")).Append(" ms a frame");

            // The machine drifts over a minute, so the mod is switched off four times: a row is read against
            // the rows next to it, never against one from another part of the run.
            sb.Append("\n  From the medians. Mod off four times: ").Append(median[Off0].ToString("F2")).Append(" / ").Append(median[Off1].ToString("F2")).Append(" / ")
              .Append(median[Off2].ToString("F2")).Append(" / ").Append(median[Off3].ToString("F2")).Append(" ms (how far the run drifted). The whole mod as set: ")
              .Append((median[All] - 0.5f * (median[Off0] + median[Off1])).ToString("F2")).Append(" ms.");
            sb.Append("\n  The frame pass by pass, the later ones left out - each row against the one before it (the first against the mod off before it):");
            float total = 0f;
            for (int k = 0; k < StageName.Length; k++)
            {
                float a = median[Stages + k] - (k == 0 ? median[Off1] : median[Stages + k - 1]);
                total += a;
                sb.Append("\n    ").Append(StageName[k].PadRight(46)).Append(a.ToString("F2").PadLeft(6)).Append(" ms");
            }
            sb.Append("\n    ").Append("all of them".PadRight(46)).Append(total.ToString("F2").PadLeft(6)).Append(" ms");

            // The trace: each of its rows against the plain stage measured next to it.
            float before = 0.5f * (median[Before] + median[BeforeB]);
            float refAB = 0.5f * (median[TrA] + median[TrB]), refBC = 0.5f * (median[TrB] + median[TrC]), refCD = 0.5f * (median[TrC] + median[TrD]);
            float stage = 0.25f * (median[TrA] + median[TrB] + median[TrC] + median[TrD]) - before;
            sb.Append("\n  The trace stage taken apart (nothing after it runs in these rows). The whole stage as set four times: ")
              .Append(median[TrA].ToString("F2")).Append(" / ").Append(median[TrB].ToString("F2")).Append(" / ").Append(median[TrC].ToString("F2")).Append(" / ")
              .Append(median[TrD].ToString("F2")).Append(" ms, all before it twice: ").Append(median[Before].ToString("F2")).Append(" / ").Append(median[BeforeB].ToString("F2"))
              .Append(" (their spreads are the noise) -> the stage costs ").Append(stage.ToString("F2")).Append(" ms: the trace itself ")
              .Append((median[TraceOnly] - median[Before]).ToString("F2")).Append(", its upsample ").Append((median[TrA] - median[TraceOnly]).ToString("F2"))
              .Append(half ? "." : " (no upsample at full resolution).");
            sb.Append("\n    The march: as it was the stage costs ").Append((median[OldRow] - refAB).ToString("F2")).Append(" ms more than as set (")
              .Append(_marchCan ? MarchName[Mathf.Clamp(_marchSet, 0, 2)] : "the same march here").Append("), as it was without its skip buffers ")
              .Append((median[OldNoSkipRow] - refAB).ToString("F2")).Append(" ms more; fast with every crossing bisected ")
              .Append((median[ExactRow] - refBC).ToString("F2")).Append(" ms more. (Less, where negative.)");
            sb.Append("\n    Half the ray steps (").Append(Mathf.Max(1, stepsSet / 2)).Append(") saves ").Append((refBC - median[HalfSteps]).ToString("F2"))
              .Append(" ms; one step - no march at all - saves ").Append((refCD - median[OneStep]).ToString("F2"))
              .Append(" ms, so the march is that much of the stage; mirror rays only save ").Append((refCD - median[MirrorRow]).ToString("F2"))
              .Append(" ms - what choosing a ray inside the roughness lobe costs the trace, the scattered directions' march with it.");
            float dnOff = 0.5f * (median[DnOffA] + median[DnOffB]);
            sb.Append("\n  Half-Resolution Denoising (whole frame, against off measured either side: ").Append(median[DnOffA].ToString("F2")).Append(" / ")
              .Append(median[DnOffB].ToString("F2")).Append(" ms): blurs only saves ").Append((dnOff - median[DnBlurs]).ToString("F2"))
              .Append(" ms, everything saves ").Append((dnOff - median[DnAll]).ToString("F2")).Append(" ms (more, where larger; set now: ")
              .Append(SsrConfig.HalfDenoise.Value).Append(SsrConfig.DenoiseBlur.Value && _dn ? "" : " - needs the new denoiser + Rough Reflection Denoising: all four rows are the same")
              .Append(").");
            Plugin.MyLog.LogInfo(sb.ToString());
        }
    }
}
