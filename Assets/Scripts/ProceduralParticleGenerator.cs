using UnityEngine;

/// <summary>
/// Builds a Unity ParticleSystem component entirely from code
/// based on a ParticleEffectData descriptor.
/// No prefabs required — attach this to any GameObject.
/// </summary>
public static class ProceduralParticleGenerator
{
    /// <summary>
    /// Creates a new GameObject with a fully configured ParticleSystem.
    /// </summary>
    public static GameObject Generate(ParticleEffectData data, Transform parent = null)
    {
        var go = new GameObject($"ParticleEffect_{data.effectType}_{data.effectId[..8]}");

        if (parent != null)
            go.transform.SetParent(parent, false);

        go.transform.position   = data.Position;
        go.transform.eulerAngles = data.Rotation;
        go.transform.localScale  = data.Scale;

        // Store the effect id so the manager can track/remove it later
        var tracker = go.AddComponent<ParticleEffectTracker>();
        tracker.effectId = data.effectId;

        var ps = go.AddComponent<ParticleSystem>();
        ConfigureSystem(ps, data);

        return go;
    }

    // ---------------------------------------------------------------

    static void ConfigureSystem(ParticleSystem ps, ParticleEffectData d)
    {
        // --- Main module ---
        var main = ps.main;
        main.duration       = d.duration;
        main.loop           = d.loop;
        main.startLifetime  = d.startLifetime;
        main.startSpeed     = d.startSpeed;
        main.startSize      = d.startSize;
        main.maxParticles   = d.maxParticles;
        main.startColor     = d.Color;
        main.simulationSpace = ParticleSystemSimulationSpace.World;

        // --- Emission ---
        var emission = ps.emission;
        emission.rateOverTime = d.emissionRate;

        // --- Shape (type-specific) ---
        var shape = ps.shape;
        shape.enabled = true;

        switch (d.effectType.ToLower())
        {
            case "fire":
                ConfigureFire(ps, d);
                break;
            case "smoke":
                ConfigureSmoke(ps, d);
                break;
            case "sparkle":
            case "sparks":
                ConfigureSparks(ps, d);
                break;
            case "explosion":
                ConfigureExplosion(ps, d);
                break;
            case "water":
            case "fountain":
                ConfigureWater(ps, d);
                break;
            default:
                // Generic cone emitter
                shape.shapeType = ParticleSystemShapeType.Cone;
                shape.angle     = 25f;
                shape.radius    = 0.2f;
                break;
        }

        ps.Play();
    }

    // --- Preset configurations ---

    static void ConfigureFire(ParticleSystem ps, ParticleEffectData d)
    {
        var main = ps.main;
        main.startColor    = new ParticleSystem.MinMaxGradient(
            new Color(1f, 0.6f, 0f), new Color(1f, 0.1f, 0f));
        main.startSize     = new ParticleSystem.MinMaxCurve(0.2f, 0.6f);
        main.startSpeed    = new ParticleSystem.MinMaxCurve(1f, 3f);
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.5f, 1.5f);
        main.gravityModifier = -0.1f;

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle     = 15f;
        shape.radius    = 0.15f;

        // Colour over lifetime: bright orange → transparent red
        var col = ps.colorOverLifetime;
        col.enabled = true;
        var grad = new Gradient();
        grad.SetKeys(
            new[] { new GradientColorKey(new Color(1f, 0.8f, 0f), 0f),
                    new GradientColorKey(new Color(0.8f, 0.1f, 0f), 1f) },
            new[] { new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0f, 1f) });
        col.color = new ParticleSystem.MinMaxGradient(grad);

        // Size shrinks toward end
        var sizeLife = ps.sizeOverLifetime;
        sizeLife.enabled = true;
        var sizeCurve = new AnimationCurve(
            new Keyframe(0f, 1f), new Keyframe(1f, 0f));
        sizeLife.size = new ParticleSystem.MinMaxCurve(1f, sizeCurve);
    }

    static void ConfigureSmoke(ParticleSystem ps, ParticleEffectData d)
    {
        var main = ps.main;
        main.startColor    = new ParticleSystem.MinMaxGradient(
            new Color(0.3f, 0.3f, 0.3f, 0.8f), new Color(0.6f, 0.6f, 0.6f, 0.4f));
        main.startSize     = new ParticleSystem.MinMaxCurve(0.4f, 1.2f);
        main.startSpeed    = new ParticleSystem.MinMaxCurve(0.5f, 1.2f);
        main.startLifetime = new ParticleSystem.MinMaxCurve(2f, 4f);
        main.gravityModifier = -0.05f;

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle     = 20f;
        shape.radius    = 0.3f;

        var sizeLife = ps.sizeOverLifetime;
        sizeLife.enabled = true;
        var grow = new AnimationCurve(
            new Keyframe(0f, 0.2f), new Keyframe(0.5f, 0.8f), new Keyframe(1f, 1f));
        sizeLife.size = new ParticleSystem.MinMaxCurve(1f, grow);

        var col = ps.colorOverLifetime;
        col.enabled = true;
        var grad = new Gradient();
        grad.SetKeys(
            new[] { new GradientColorKey(Color.gray, 0f),
                    new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(0.7f, 0f),
                    new GradientAlphaKey(0f, 1f) });
        col.color = new ParticleSystem.MinMaxGradient(grad);
    }

    static void ConfigureSparks(ParticleSystem ps, ParticleEffectData d)
    {
        var main = ps.main;
        main.startColor    = new ParticleSystem.MinMaxGradient(
            new Color(1f, 1f, 0.2f), new Color(1f, 0.5f, 0f));
        main.startSize     = new ParticleSystem.MinMaxCurve(0.03f, 0.08f);
        main.startSpeed    = new ParticleSystem.MinMaxCurve(2f, 8f);
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.3f, 1.2f);
        main.gravityModifier = 1f;

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius    = 0.05f;

        var trails = ps.trails;
        trails.enabled    = true;
        trails.ratio      = 1f;
        trails.lifetime   = new ParticleSystem.MinMaxCurve(0.2f);
        trails.minVertexDistance = 0.05f;
        trails.widthOverTrail   = new ParticleSystem.MinMaxCurve(0.02f);
    }

    static void ConfigureExplosion(ParticleSystem ps, ParticleEffectData d)
    {
        var main = ps.main;
        main.loop        = false;
        main.duration    = 0.5f;
        main.startColor  = new ParticleSystem.MinMaxGradient(
            new Color(1f, 0.7f, 0f), new Color(1f, 0.2f, 0f));
        main.startSize   = new ParticleSystem.MinMaxCurve(0.1f, 0.5f);
        main.startSpeed  = new ParticleSystem.MinMaxCurve(3f, 12f);
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.5f, 1.5f);

        var emission = ps.emission;
        emission.rateOverTime = 0;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 80) });

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius    = 0.1f;
    }

    static void ConfigureWater(ParticleSystem ps, ParticleEffectData d)
    {
        // ── Main ──────────────────────────────────────────────────────
        var main = ps.main;
        main.loop             = true;
        main.duration         = 3f;
        main.startLifetime    = new ParticleSystem.MinMaxCurve(1.2f, 2.0f);
        main.startSpeed       = new ParticleSystem.MinMaxCurve(3.5f, 6.0f);
        main.startSize        = new ParticleSystem.MinMaxCurve(0.04f, 0.12f);
        main.maxParticles     = 300;
        main.gravityModifier  = 1.8f;           // strong gravity → arc then fall
        main.simulationSpace  = ParticleSystemSimulationSpace.World;

        // Blue-white water colour
        main.startColor = new ParticleSystem.MinMaxGradient(
            new Color(0.6f, 0.85f, 1.0f, 0.9f),
            new Color(0.9f, 0.97f, 1.0f, 0.7f));

        // ── Emission ─────────────────────────────────────────────────
        var emission = ps.emission;
        emission.rateOverTime = 80f;            // dense continuous stream

        // ── Shape: tight upward cone → jets arc outward ───────────────
        var shape = ps.shape;
        shape.enabled       = true;
        shape.shapeType     = ParticleSystemShapeType.Cone;
        shape.angle         = 18f;              // spread of the jets
        shape.radius        = 0.12f;            // fountain nozzle radius
        shape.radiusThickness = 1f;             // emit from the rim, not centre
        shape.rotation      = new Vector3(-90f, 0f, 0f); // shoot upward

        // ── Colour over lifetime: blue → white → transparent ─────────
        var col = ps.colorOverLifetime;
        col.enabled = true;
        var grad = new Gradient();
        grad.SetKeys(
            new[]
            {
                new GradientColorKey(new Color(0.5f, 0.80f, 1.0f), 0.0f),
                new GradientColorKey(new Color(0.8f, 0.95f, 1.0f), 0.5f),
                new GradientColorKey(new Color(1.0f, 1.00f, 1.0f), 1.0f),
            },
            new[]
            {
                new GradientAlphaKey(0.9f, 0.0f),
                new GradientAlphaKey(0.6f, 0.6f),
                new GradientAlphaKey(0.0f, 1.0f),
            });
        col.color = new ParticleSystem.MinMaxGradient(grad);

        // ── Size over lifetime: droplets shrink as they splash ────────
        var sizeLife = ps.sizeOverLifetime;
        sizeLife.enabled = true;
        var sizeCurve = new AnimationCurve(
            new Keyframe(0.0f, 0.6f),
            new Keyframe(0.5f, 1.0f),
            new Keyframe(1.0f, 0.2f));
        sizeLife.size = new ParticleSystem.MinMaxCurve(1f, sizeCurve);

        // ── Velocity over lifetime: slight random sideways drift ──────
        var vel = ps.velocityOverLifetime;
        vel.enabled = true;
        vel.x = new ParticleSystem.MinMaxCurve(-0.4f, 0.4f);
        vel.z = new ParticleSystem.MinMaxCurve(-0.4f, 0.4f);
        vel.space = ParticleSystemSimulationSpace.World;

        // ── Noise: organic turbulence so jets don't look robotic ──────
        var noise = ps.noise;
        noise.enabled   = true;
        noise.strength  = 0.15f;
        noise.frequency = 0.8f;
        noise.scrollSpeed = 0.4f;
        noise.quality   = ParticleSystemNoiseQuality.Medium;

        // ── Collision: droplets bounce/die on surfaces ────────────────
        var collision = ps.collision;
        collision.enabled       = true;
        collision.type          = ParticleSystemCollisionType.World;
        collision.mode          = ParticleSystemCollisionMode.Collision3D;
        collision.bounce        = new ParticleSystem.MinMaxCurve(0.15f);
        collision.lifetimeLoss  = new ParticleSystem.MinMaxCurve(0.4f);
        collision.radiusScale   = 0.3f;

        // ── Sub-emitter: tiny splash burst when a droplet hits ────────
        // (requires a child GameObject — we create it here)
        var splashGO = new GameObject("WaterSplash");
        splashGO.transform.SetParent(ps.transform, false);
        var splashPS = splashGO.AddComponent<ParticleSystem>();

        var sm = splashPS.main;
        sm.loop           = false;
        sm.duration       = 0.2f;
        sm.startLifetime  = new ParticleSystem.MinMaxCurve(0.2f, 0.5f);
        sm.startSpeed     = new ParticleSystem.MinMaxCurve(0.5f, 2.0f);
        sm.startSize      = new ParticleSystem.MinMaxCurve(0.02f, 0.06f);
        sm.maxParticles   = 50;
        sm.gravityModifier = 2f;
        sm.startColor     = new Color(0.7f, 0.9f, 1.0f, 0.6f);

        var se = splashPS.emission;
        se.rateOverTime = 0;
        se.SetBursts(new[] { new ParticleSystem.Burst(0f, 8) });

        var ss = splashPS.shape;
        ss.shapeType = ParticleSystemShapeType.Sphere;
        ss.radius    = 0.02f;

        var sub = ps.subEmitters;
        sub.enabled = true;
        sub.AddSubEmitter(splashPS, ParticleSystemSubEmitterType.Collision,
                          ParticleSystemSubEmitterProperties.InheritNothing);

        ps.Play();
    }
}
