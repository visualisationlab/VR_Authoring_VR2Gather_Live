using UnityEngine;

/// <summary>
/// Lightweight tag component placed on every spawned particle effect GameObject.
/// Lets the ParticleEffectManager locate and remove specific effects by ID.
/// </summary>
public class ParticleEffectTracker : MonoBehaviour
{
    [HideInInspector] public string effectId;
}
