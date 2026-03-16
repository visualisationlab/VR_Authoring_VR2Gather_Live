using UnityEngine;

/// <summary>
/// Example component showing how to use ParticleEffectManager from any script.
///
/// Attach this to any GameObject in your scene to spawn / remove effects.
/// The manager handles all persistence automatically.
/// </summary>
public class ParticleEffectSpawner : MonoBehaviour
{
    [Header("Effect to spawn")]
    [Tooltip("fire | smoke | sparkle | sparks | explosion")]
    public string effectType = "fire";

    [Tooltip("Spawn at this transform's position, or override below.")]
    public bool useTransformPosition = true;
    public Vector3 worldPosition;

    [Header("Auto-spawn on Start")]
    public bool spawnOnStart = false;

    // Tracks the last spawned effect so we can remove it
    private string _lastEffectId;

    // ------------------------------------------------------------------

    void Start()
    {
        if (spawnOnStart)
            SpawnEffect();
    }

    // ------------------------------------------------------------------

    /// <summary>Spawns the effect and saves it.</summary>
    public void SpawnEffect()
    {
        Vector3 pos = useTransformPosition ? transform.position : worldPosition;

        var data = new ParticleEffectData(pos, effectType)
        {
            // Optionally tweak via inspector or code before passing:
            loop        = true,
            emissionRate = 30f,
            startSize   = 0.4f,
        };

        _lastEffectId = ParticleEffectManager.Instance.AddEffect(data);
        Debug.Log($"[Spawner] Spawned effect {_lastEffectId}");
    }

    /// <summary>Removes only the last effect spawned by this component.</summary>
    public void RemoveLastEffect()
    {
        if (string.IsNullOrEmpty(_lastEffectId)) return;
        ParticleEffectManager.Instance.RemoveEffect(_lastEffectId);
        _lastEffectId = null;
    }

    /// <summary>Wipes ALL saved effects from the scene and save file.</summary>
    public void ClearAll()
    {
        ParticleEffectManager.Instance.ClearAllEffects();
    }

    // ------------------------------------------------------------------
    // Optional: spawn on mouse click for quick scene testing

    void Update()
    {
        if (Input.GetMouseButtonDown(0))
        {
            Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
            if (Physics.Raycast(ray, out RaycastHit hit))
            {
                var data = new ParticleEffectData(hit.point, effectType);
                ParticleEffectManager.Instance.AddEffect(data);
                Debug.Log($"[Spawner] Click-spawned '{effectType}' at {hit.point}");
            }
        }
    }
}
