using System;
using UnityEngine;

/// <summary>
/// Serializable data for one saved particle effect instance.
/// Stored in JSON so it survives scene reloads.
/// </summary>
[Serializable]
public class ParticleEffectData
{
    public string effectId;          // unique GUID per instance
    public string effectType;        // e.g. "fire", "smoke", "sparkle", "custom"
    public float posX, posY, posZ;
    public float rotX, rotY, rotZ;
    public float scaleX, scaleY, scaleZ;

    // Core particle settings (used by ProceduralParticleGenerator)
    public float duration       = 5f;
    public bool  loop           = true;
    public float startLifetime  = 2f;
    public float startSpeed     = 3f;
    public float startSize      = 0.3f;
    public int   maxParticles   = 100;
    public float emissionRate   = 20f;

    // Color (stored as 0-1 floats)
    public float colorR = 1f, colorG = 0.5f, colorB = 0f, colorA = 1f;

    // Optional: prefab name (if you want prefab-based spawning instead of procedural)
    public string prefabName = "";

    public Vector3 Position => new Vector3(posX, posY, posZ);
    public Vector3 Rotation => new Vector3(rotX, rotY, rotZ);
    public Vector3 Scale    => new Vector3(scaleX, scaleY, scaleZ);
    public Color   Color    => new Color(colorR, colorG, colorB, colorA);

    public ParticleEffectData() { }

    public ParticleEffectData(Vector3 position, string type = "fire")
    {
        effectId   = Guid.NewGuid().ToString();
        effectType = type;
        posX = position.x; posY = position.y; posZ = position.z;
        rotX = rotY = rotZ = 0f;
        scaleX = scaleY = scaleZ = 1f;
    }
}

/// <summary>
/// Wrapper so Unity's JsonUtility can serialize a List of effects.
/// </summary>
[Serializable]
public class ParticleEffectSaveFile
{
    public ParticleEffectData[] effects = Array.Empty<ParticleEffectData>();
}
