using BepInEx;
using BepInEx.Configuration;

namespace ReflectSix.Source
{
    internal sealed class ConfigurationManagerAttributes { public bool? IsAdvanced; }

    // What the debug views show. Normals / Smoothness / SpecColor are the G-buffer probe — the FIRST thing
    // to check when reflections misbehave: black/garbage means the tracer has no surface data at this hook.
    internal enum ESsrDebugView
    {
        Off,
        Normals,        // G-buffer world normals (should be a smooth colour-by-orientation image)
        Smoothness,     // G-buffer smoothness (white = glossy; water/glass/wet should light up)
        SpecColor,      // G-buffer specular colour (F0)
        Depth,          // linear01 depth (sanity: matches the scene, per eye)
        ReflectionMask, // where SSR lands and how strongly (white = full reflection)
        ReflectionOnly, // the traced reflection colour on black
        HitDist,        // traced hit distance (sqrt-scaled against Max Distance; black = miss)
        Coherence,      // normal coherence — the foliage detector (white = flat/coherent, dark = chaotic)
        FoliageMask,    // what the foliage gate decides: red = F0/grass, green = normals/trees, black = keep
        TraceEnd,       // why each ray ended: green=hit yellow=rejected red=budget blue=miss cyan=sky magenta=selfz
        ProbeRefl,      // the game's baked-probe specular buffer (_CameraReflectionsTexture)
        SceneMip,       // the sanitized scene copy at mip 3 — what the cone fetch / firefly reference read
        HistoryLength,  // new denoiser: frames accumulated per pixel (green), red = history rejected, blue = edge fetch
        MotionCheck,    // new denoiser: motion vectors vs camera-only motion (static world must stay black)
    }

    // Which of the denoiser's passes run at half resolution.
    internal enum ESsrHalfDenoise
    {
        Off,            // all at full resolution
        BlursOnly,      // the blur before the accumulation, for wide lobes
        Everything,     // resolve, both blurs, the accumulation and its history; upsampled in the composite
    }

    // How a reflection ray is marched along the screen.
    internal enum ESsrMarch
    {
        Fast,           // one depth read a loop turn; a ray that stays behind something is not refined again at every sample
        FastExact,      // one depth read a loop turn, every crossing refined: the same hits as Previous
        Previous,       // the march as it was up to 2026-10-13 (refines inside the step, skips empty screen blocks)
    }

    internal static class SsrConfig
    {
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<bool> ScopeRefl;
        public static ConfigEntry<bool> SuppressGameSsr;
        public static ConfigEntry<bool> Debug;
        public static ConfigEntry<ESsrDebugView> DebugView;

        // Look
        public static ConfigEntry<float> Intensity;
        public static ConfigEntry<float> SmoothnessCutoff;
        public static ConfigEntry<float> ReflSoftness;
        public static ConfigEntry<float> Foliage;
        public static ConfigEntry<float> RainFlow;
        public static ConfigEntry<float> EdgeFade;
        public static ConfigEntry<bool> Glossy;
        public static ConfigEntry<float> GlossyScale;
        public static ConfigEntry<float> GlossyAniso;
        public static ConfigEntry<bool> Stochastic;
        public static ConfigEntry<float> SpecularAA;
        public static ConfigEntry<float> Firefly;
        public static ConfigEntry<float> FoliageF0;
        public static ConfigEntry<float> SelfExclusion;
        public static ConfigEntry<float> SkimRejection;
        public static ConfigEntry<bool> SkyRefl;
        public static ConfigEntry<bool> WorldRays;
        public static ConfigEntry<bool> WorldRaysSoft;
        public static ConfigEntry<bool> WorldRaysRt;
        public static ConfigEntry<bool> RtOnly;
        public static ConfigEntry<bool> NoBakedCube;

        // Quality / performance
        public static ConfigEntry<bool> NewDenoiser;
        public static ConfigEntry<bool> DenoiseBlur;
        public static ConfigEntry<bool> Stabilize;
        public static ConfigEntry<ESsrHalfDenoise> HalfDenoise;
        public static ConfigEntry<float> TemporalSmoothing;
        public static ConfigEntry<int> Steps;
        public static ConfigEntry<float> MaxDistance;
        public static ConfigEntry<float> MarchStride;
        public static ConfigEntry<float> StrideGrowth;
        public static ConfigEntry<float> Thickness;
        public static ConfigEntry<bool> HalfRes;
        public static ConfigEntry<int> WorldRaySpacing;
        public static ConfigEntry<bool> CostNow;
        public static ConfigEntry<ESsrMarch> March;

        private static ConfigDescription Adv(string desc, AcceptableValueBase range = null)
        {
            return new ConfigDescription(desc, range, new ConfigurationManagerAttributes { IsAdvanced = true });
        }

        public static void Bind(ConfigFile config)
        {
            string currentVersion = MetadataHelper.GetMetadata(typeof(Plugin)).Version.ToString();
            var version = config.Bind("Internal", "ConfigVersion", "", "Do not modify");
            if (version == null || version.Value != currentVersion)
            {
                config.Clear();
                System.IO.File.WriteAllText(config.ConfigFilePath, "");
                config.Reload();
                version = config.Bind("Internal", "ConfigVersion", currentVersion, "Do not modify");
                version.Value = currentVersion;
                config.Save();
                Plugin.MyLog.LogInfo($"Config reset for version {currentVersion}");
            }

            // ---- General (master toggles stay visible; diagnostics are advanced) ----
            Enabled = config.Bind("General", "Enabled", true,
                "Screen-space reflections on glossy surfaces (water, puddles, glass, wet ground). Works " +
                "flatscreen and in VR (per-eye).");
            // NOTE: BepInEx forbids ' (and = " [ ] etc.) in section/KEY names — an apostrophe here threw
            // in Bind and killed the whole plugin Awake. Descriptions may contain them; keys must not.
            ScopeRefl = config.Bind("General", "Reflections In Scopes", true,
                "Reflections in the picture a magnified scope shows, too. While you aim through one the " +
                "reflections are worked out a second time for that picture, so they cost about as much again " +
                "as they do for the main view, in proportion to the scope picture's size. Off = a scope shows " +
                "what the game shows without this mod. Thermal sights are always left alone.");
            SuppressGameSsr = config.Bind("General", "Disable Tarkov SSR Render", true,
                "Disables Tarkov's built in SSR");
            Debug = config.Bind("General", "Debug Log", false,
                Adv("Log diagnostics (reflection-probe census, shader-load status)."));
            DebugView = config.Bind("General", "Debug View", ESsrDebugView.Off,
                Adv("Fullscreen diagnostic overlays. Normals/Smoothness/SpecColor visualize the G-buffer " +
                    "inputs — check these FIRST if reflections misbehave: black/garbage means the tracer " +
                    "has no surface data at this hook. ReflectionMask/ReflectionOnly show what the tracer " +
                    "produces; HitDist shows how far each ray flew before hitting (black = miss)."));
            CostNow = config.Bind("General", "Log What SSR Costs", false,
                Adv("Tick and stand still for 144 seconds: the mod steps through 32 set-ups (off four times, " +
                    "everything as set, the frame's passes left out one after another, and the trace taken apart: " +
                    "without its upsample, with the march as it was - with and without its skip buffers - with " +
                    "the fast march refining every crossing, with half the ray steps, with one, with mirror rays " +
                    "only; then Half-Resolution Denoising off, blurs only, everything, off) and writes each one's frame time to the log, with the share of the picture that " +
                    "reflects and how the rays go (how they end, how many turns of the loop they run). The frame " +
                    "cap is lifted while it runs and the reflections come and go (unticks itself). Needs the " +
                    "rebuilt 'ssr' bundle."));

            // ---- Look: the everyday knobs (not advanced) ----
            Intensity = config.Bind("Look", "Intensity", 1.0f,
                new ConfigDescription("Strength of the reflections. 1 = physically-motivated (fresnel-weighted); " +
                    "lower if surfaces look like chrome mirrors, raise for a stylized wet look.",
                    new AcceptableValueRange<float>(0f, 2f)));
            SmoothnessCutoff = config.Bind("Look", "Smoothness Cutoff", 0.55f,
                new ConfigDescription("Only surfaces at least this smooth (G-buffer smoothness) get reflections. " +
                    "Lower = more surfaces reflect (wet asphalt/concrete pick up soft reflections) but more " +
                    "rays traced; raise for glossy-only and a little more performance.",
                    new AcceptableValueRange<float>(0f, 1f)));
            ReflSoftness = config.Bind("Look", "Reflection Softness", 0.0f,
                new ConfigDescription("Baseline blur for ALL reflections (in mip levels), on top of the " +
                    "roughness-driven glossy blur. 0 = razor sharp; ~0.5 = a subtle filmic soften; 1.5+ = " +
                    "visibly soft. Needs Glossy Reflections ON.",
                    new AcceptableValueRange<float>(0f, 3f)));
            Foliage = config.Bind("Look", "Foliage Gloss Reduction", 0.9f,
                new ConfigDescription("Reduces the gloss/reflection on grass (can have an effect on other surfaces)",
                    new AcceptableValueRange<float>(0f, 1f)));
            RainFlow = config.Bind("Look", "Rain Streaks On Walls", 0.4f,
                new ConfigDescription("Adds a flowing down effect on vertical surfaces",
                    new AcceptableValueRange<float>(0f, 1f)));

            // ---- Look: advanced tuning ----
            EdgeFade = config.Bind("Look", "Edge Fade", 0.2f,
                Adv("Screen-border band over which reflections fade out (hides rays running off-screen; in VR " +
                    "also hides the per-eye disagreement at the view edges).",
                    new AcceptableValueRange<float>(0.01f, 0.5f)));
            Glossy = config.Bind("Look", "Glossy Reflections", true,
                Adv("Roughness-aware reflection blur (cone tracing against a prefiltered pyramid). Rougher " +
                    "surfaces reflect softer, and blur grows with hit distance, so contact points stay sharp " +
                    "while distant content goes soft. Also the spatial denoiser for Stochastic Reflections."));
            GlossyScale = config.Bind("Look", "Glossy Blur Scale", 0.5f,
                Adv("Multiplier on the roughness-driven blur cone. 1 = GGX-motivated width; raise for a " +
                    "softer, dreamier wet look, lower toward 0 for sharper reflections everywhere.",
                    new AcceptableValueRange<float>(0f, 3f)));
            GlossyAniso = config.Bind("Look", "Glossy Anisotropy", 0.6f,
                Adv("Grazing-angle stretch of the glossy blur — the elongated light streaks on wet " +
                    "streets at night. 0 = round blur only; 1 = full physical stretch. Head-on " +
                    "reflections are never affected. Needs Glossy Reflections ON.",
                    new AcceptableValueRange<float>(0f, 1f)));
            Stochastic = config.Bind("Look", "Stochastic Reflections (SSSR)", true,
                Adv("Trace REAL glossy reflections: each pixel's ray importance-samples its GGX roughness " +
                    "lobe (noise-rotated every frame) and a BRDF-weighted resolve shares rays between " +
                    "neighbouring pixels — parallax-correct roughness blur and contact hardening from the " +
                    "trace itself instead of the post-blur approximation. NEEDS Temporal Smoothing above " +
                    "~0.8 to integrate the sample noise (at 0 you see raw grain). OFF = deterministic " +
                    "mirror rays + pyramid blur."));
            SpecularAA = config.Bind("Look", "Specular AA", 0.0f,
                Adv("Geometric specular antialiasing. Detailed surfaces (brick mortar, gun rails) pack " +
                    "many differently-aimed micro-mirrors into a pixel once rain raises smoothness; this " +
                    "measures that sub-pixel variance and widens the reflection lobe to match, turning " +
                    "would-be specks into soft glints (energy-preserving blur). Mostly redundant now that " +
                    "the white-speck source is handled at the material level; adds softening. 0 = off.",
                    new AcceptableValueRange<float>(0f, 1f)));
            Firefly = config.Bind("Look", "Firefly Suppression", 0.7f,
                Adv("Tames bright white speckles in reflections — reflections of the scene's own tiny hot " +
                    "pixels (streetlight sparkles, HDR specular highlights) that otherwise flicker as " +
                    "blinding dots. Clamps a reflected pixel's brightness relative to its neighbourhood, so " +
                    "real coherent lights stay bright while lone sparkles are reined in. 0 = off; 1 = strong.",
                    new AcceptableValueRange<float>(0f, 1f)));
            FoliageF0 = config.Bind("Look", "Foliage F0 Threshold", 0.065f,
                Adv("The specular-colour level at/below which a surface counts as grass (\"declares no " +
                    "specular response\"). Tune it with Debug View = FoliageMask: RED marks what this " +
                    "catches — raise until grass turns red, and stop before WATER does (real water sits " +
                    "around 0.02, glass around 0.04). Only matters when Foliage Gloss Reduction is above 0.",
                    new AcceptableValueRange<float>(0.001f, 0.08f)));
            SelfExclusion = config.Bind("Look", "Self-Reflection Exclusion", 0.0f,
                Adv("Reflection rays ignore anything closer to the camera than this (metres). In VR your " +
                    "rendered body, arms, gun and wrist UI hover over the ground and can otherwise reflect " +
                    "as black body-shaped blobs in puddles when you look down — this treats near-camera " +
                    "depth as the viewer, not the world. 0 = off (viewmodel/body reflects again). Raise it " +
                    "if body blobs appear at extreme downward angles.",
                    new AcceptableValueRange<float>(0f, 3f)));
            SkimRejection = config.Bind("Look", "Skim Rejection", 0.12f,
                Adv("Rejects grazing 'skim' hits — rays that clip the ground's own micro-bumps a metre or " +
                    "two ahead and paint dark ground where the real reflection (vehicle, building, sky) " +
                    "belongs; the ray marches on to its true target instead. This was the VR 'black bar'. " +
                    "The value is how decisively (metres) a ray must sink behind a surface to count as a " +
                    "real hit: too low = the bar returns, too high = thin/edge-on reflections disappear. " +
                    "0 = off.",
                    new AcceptableValueRange<float>(0f, 0.5f)));
            SkyRefl = config.Bind("Look", "Sky In Reflections", true,
                Adv("Reflections always show the sky where they should: rays that reach sky ON SCREEN " +
                    "fetch it exactly (clouds included), and when the sky isn't in view, CloudSix's live " +
                    "sky map fills in — gated by a roof check so interiors never reflect phantom clouds. " +
                    "Needs CloudSix's 'Publish Sky For Reflections' for the not-on-screen half."));
            WorldRays = config.Bind("Look", "Off-Screen Rays Through TraceSix", true,
                Adv("With TraceSix running, a reflection ray that leaves the picture carries on through TraceSix's " +
                    "voxel world: a puddle under a roof mirrors the sky past the roof's edge and nothing " +
                    "through the roof itself (that part needs CloudSix's published sky). Off, or without TraceSix: the " +
                    "roof check decides, asked where the ray was last seen on screen. Scopes always use the roof check."));
            WorldRaysSoft = config.Bind("Look", "Off-Screen Rays Soft Edges", true,
                Adv("The march through TraceSix's voxels blends the eight voxels around each sample, so the " +
                    "outline of an off-screen roof or wall in a puddle is a soft edge and not voxel stairs. " +
                    "About three times the voxel reads of the plain march. Off: one voxel a sample."));
            WorldRaysRt = config.Bind("Look", "Off-Screen Rays Ray-Traced", true,
                Adv("With TraceSix's hardware ray tracing running (flatscreen), the rays that leave the picture are traced " +
                    "against the map's real triangles instead of marched through its voxels (the very ray that left): exact " +
                    "shapes, no light through thin walls or roofs, and an off-screen ray shows what it HIT - lit by TraceSix " +
                    "(the sun, lamps and bounce light there) - not only whether it reached the sky. Its colour comes from " +
                    "TraceSix's Ray-Traced Textures (a small copy of each texture; off = the voxels' coarser colours). Bots, " +
                    "loot and grass are not in it. Off, or without the ray tracing: the voxel march."));
            RtOnly = config.Bind("Look", "Reflections Ray-Traced Only", true,
                Adv("With Off-Screen Rays Ray-Traced running: no screen-space trace at all - every reflection is what a " +
                    "mirror ray from the surface hits in TraceSix's ray-traced scene (lit by TraceSix), or the sky past it. " +
                    "No screen-space faults (things that leave the picture, rays passing behind objects), but what is on " +
                    "screen is reflected in TraceSix's small texture copies instead of the picture's full detail (the voxels' " +
                    "colours with its Ray-Traced Textures off), and bots, loot and grass are not reflected. " +
                    "Rough surfaces are blurred as the screen-space ones are: each pixel traces a ray from its glossy " +
                    "lobe and the denoiser averages them. Without the ray tracing (VR, TraceSix's ray tracing off), or " +
                    "without CloudSix's published sky (the sky past what the rays hit): the normal reflections, with the " +
                    "ray-traced rays for what leaves the picture."));
            NoBakedCube = config.Bind("Look", "Remove Baked Cube Reflections", true,
                "Many of the game's surfaces - concrete and tiled floors, guns, gear - add a fixed pre-made picture of " +
                "their surroundings (a cubemap) as a shine, the same in every room, day or night, and the real " +
                "reflections then land on top of it. On = that shine is taken off every solid surface while ReflectSix " +
                "is on; their own specular highlight stays. Off, or ReflectSix off: the game's shine is back. Glass " +
                "and other see-through surfaces keep theirs.");
            NoBakedCube.SettingChanged += (s, e) => SsrBakedCube.SettingChanged();
            Enabled.SettingChanged += (s, e) => SsrBakedCube.SettingChanged();

            // ---- Performance: the everyday knobs (not advanced) ----
            // Temporary A/B switch while the new denoiser is being judged in game (Off = the previous path, exactly).
            NewDenoiser = config.Bind("Performance", "New Denoiser", true,
                "Reworked glossy-reflection denoiser: filters only the reflected light and applies each " +
                "pixel's own material afterwards (surface detail stays sharp), keeps history per pixel " +
                "(checked against last frame's surface, short for mirror-like reflections so they cannot " +
                "ghost, long for rough ones), and follows moving reflectors. Off = the previous denoiser, " +
                "for comparison. Needs the rebuilt 'ssr' bundle; with an old bundle the previous path runs.");
            DenoiseBlur = config.Bind("Performance", "Rough Reflection Denoising", true,
                "For reflections on rougher surfaces (New Denoiser with Stochastic Reflections on): each " +
                "frame is blurred along the surface before it is added to the history, and the result once " +
                "more after, wide where there is little history and about a pixel where there is a lot. " +
                "Never wider than half of what the surface's own roughness blurs anyway, so sharp and " +
                "contact reflections are left alone. Off = the denoiser as it was (noisier, and darker " +
                "where a rough surface reflects something small and bright). Needs the rebuilt 'ssr' bundle.");
            Stabilize = config.Bind("Performance", "Stabilize Output", true,
                "With Rough Reflection Denoising: the finished reflection is also averaged over the last frames " +
                "(NVIDIA ReBLUR's temporal stabilization), which calms the grain rough surfaces keep - most with " +
                "Half-Resolution Trace. It may only differ from the frame's own reflection by about that grain and " +
                "follows the reflection's motion as the history does, so real changes still show at once. Off = each " +
                "frame's denoised reflection as it is. Needs the rebuilt 'ssr' bundle.");
            HalfDenoise = config.Bind("Performance", "Half-Resolution Denoising", ESsrHalfDenoise.Off,
                Adv("Runs parts of the rough-reflection denoiser at half resolution (needs Rough Reflection " +
                    "Denoising). BlursOnly: the blur before the accumulation, for wide (rough) lobes only - " +
                    "narrow ones keep the full-resolution blur; a small saving, no change to sharp reflections. " +
                    "Everything: the resolve, both blurs and the accumulation with its history, upsampled once " +
                    "by depth and normal - the larger saving, and sharp mirror-like reflections become half " +
                    "resolution too (the full-resolution history is what rebuilt their detail from the " +
                    "half-resolution trace). Off = all at full resolution. Needs the rebuilt 'ssr' bundle."));
            TemporalSmoothing = config.Bind("Performance", "Temporal Smoothing", 0.8f,
                new ConfigDescription("How much history the denoiser may accumulate. New Denoiser: 0.8 = up " +
                    "to 32 frames on the roughest surfaces and 4 on mirror-like ones; lower = less " +
                    "accumulation (more noise, faster response). 0 = off.",
                    new AcceptableValueRange<float>(0f, 0.95f)));
            Steps = config.Bind("Performance", "Ray Steps", 160,
                new ConfigDescription("March iteration budget per ray. Increases the reach of the ray across the screen.",
                    new AcceptableValueRange<int>(16, 512)));
            MaxDistance = config.Bind("Performance", "Max Distance", 160f,
                new ConfigDescription("How far a reflected ray may travel, in metres. Reflections of things " +
                    "farther than this from the reflecting surface fade out.",
                    new AcceptableValueRange<float>(5f, 300f)));

            // ---- Performance: advanced ----
            MarchStride = config.Bind("Performance", "March Stride", 2.7f,
                Adv("Screen pixels between march samples. Smaller = finer contact detail and thinner " +
                    "reflected features, but shorter reach for the same Ray Steps; larger = longer reach, " +
                    "may step over very thin objects.",
                    new AcceptableValueRange<float>(1f, 8f)));
            StrideGrowth = config.Bind("Performance", "Far Stride Growth", 1.01f,
                Adv("The march stride GROWS by this factor per step (capped at 3x March Stride), so " +
                    "rays sample finely near their origin — where contact detail lives — and sweep the " +
                    "far field faster. ~3x the reach for the same Ray Steps; long rays stop dying " +
                    "mid-screen. Hits are still refined to sub-pixel precision; the trade is thin " +
                    "DISTANT objects between coarse samples. 1.0 = constant stride (old behavior).",
                    new AcceptableValueRange<float>(1f, 1.1f)));
            Thickness = config.Bind("Performance", "Thickness", 2.5f,
                Adv("How far (metres) a march sample may sink behind the depth buffer and still count as a hit. " +
                    "Too small = gaps in reflections of thin objects; too big = smeary streaks under objects.",
                    new AcceptableValueRange<float>(0.05f, 3f)));
            HalfRes = config.Bind("Performance", "Half-Resolution Trace", true,
                Adv("Trace one ray per 2x2 pixels (quarter the rays): the biggest performance lever. With the " +
                    "New Denoiser the traced pixel rotates through each 2x2 block every frame and rays are " +
                    "shared between neighbours by their real probabilities, so detail converges back to " +
                    "full resolution within a few frames. Debug views always render full-res."));
            WorldRaySpacing = config.Bind("Performance", "Off-Screen Ray Spacing", 4,
                Adv("The rays that leave the picture are answered by TraceSix's voxels in a pass of their own: " +
                    "one march for every block of this many pixels a side, and each pixel takes its answer " +
                    "from the blocks around it. 4 = a quarter of the marches of half-resolution trace, 8 = a " +
                    "sixteenth; 1 or 2 = one for every traced ray. Larger blurs the outline of off-screen " +
                    "things in small puddles.",
                    new AcceptableValueList<int>(1, 2, 4, 8)));
            March = config.Bind("Performance", "Ray March", ESsrMarch.Fast,
                Adv("How a reflection ray is walked along the screen. Fast: every turn of the loop is one depth " +
                    "read for every ray, so no ray holds the others up, and a ray that is already behind " +
                    "something is not refined again at every sample. FastExact: the same loop with every " +
                    "crossing refined - the same hits as Previous, to compare the picture. Previous: the march " +
                    "as it was. Fast can place a reflection of something thinner than a march stride about a " +
                    "stride away from where Previous did. Needs the rebuilt 'ssr' bundle (an older one " +
                    "marches as before)."));
        }
    }
}
