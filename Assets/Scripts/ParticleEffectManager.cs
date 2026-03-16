using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Singleton manager that persists across scenes (DontDestroyOnLoad).
///
/// USAGE:
///   // Spawn a procedural fire at a world position and auto-save:
///   ParticleEffectManager.Instance.AddEffect(new ParticleEffectData(transform.position, "fire"));
///
///   // Spawn from a prefab and save:
///   var data = new ParticleEffectData(transform.position, "custom") { prefabName = "MyFirePrefab" };
///   ParticleEffectManager.Instance.AddEffect(data);
///
///   // Remove a specific effect:
///   ParticleEffectManager.Instance.RemoveEffect(effectId);
///
///   // Wipe everything:
///   ParticleEffectManager.Instance.ClearAllEffects();
///
/// The save file is written to Application.persistentDataPath/particle_effects.json
/// and is loaded automatically on Awake every scene start.
/// </summary>
public class ParticleEffectManager : MonoBehaviour
{
    // ------------------------------------------------------------------ singleton
    public static ParticleEffectManager Instance { get; private set; }

    // ------------------------------------------------------------------ inspector
    [Tooltip("Optional prefab pool. Key = prefabName in ParticleEffectData.")]
    [SerializeField] private ParticlePrefabEntry[] prefabPool = new ParticlePrefabEntry[0];

    // ------------------------------------------------------------------ private
    private readonly Dictionary<string, ParticleEffectData> _savedEffects = new();
    private readonly Dictionary<string, GameObject>         _liveObjects  = new();
    private readonly Dictionary<string, GameObject>         _prefabLookup = new();

    private static string SavePath =>
        Path.Combine(Application.persistentDataPath, "particle_effects.json");

    // ================================================================== lifecycle

    void Awake()
    {
        // Singleton + survive scene loads
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        // Build prefab lookup
        foreach (var entry in prefabPool)
            if (entry.prefab != null)
                _prefabLookup[entry.name] = entry.prefab;

        LoadAndRespawn();
    }

    // ================================================================== public API

    /// <summary>Spawns a new particle effect and saves it so it reappears after reload.</summary>
    public string AddEffect(ParticleEffectData data)
    {
        _savedEffects[data.effectId] = data;
        SpawnOne(data);
        Save();
        return data.effectId;
    }

    /// <summary>Convenience overload: create data inline.</summary>
    public string AddEffect(Vector3 position, string effectType = "fire")
        => AddEffect(new ParticleEffectData(position, effectType));

    /// <summary>Removes a live effect and deletes it from the save file.</summary>
    public bool RemoveEffect(string effectId)
    {
        if (_liveObjects.TryGetValue(effectId, out var go))
        {
            Destroy(go);
            _liveObjects.Remove(effectId);
        }
        bool removed = _savedEffects.Remove(effectId);
        if (removed) Save();
        return removed;
    }

    /// <summary>Destroys all live effects and clears the save file.</summary>
    public void ClearAllEffects()
    {
        foreach (var go in _liveObjects.Values)
            if (go) Destroy(go);
        _liveObjects.Clear();
        _savedEffects.Clear();
        Save();
    }

    /// <summary>Returns a copy of every saved effect descriptor.</summary>
    public IEnumerable<ParticleEffectData> GetAllEffects() => _savedEffects.Values;

    /// <summary>Updates position/settings for an existing effect and re-saves.</summary>
    public void UpdateEffect(ParticleEffectData updated)
    {
        RemoveEffect(updated.effectId);
        AddEffect(updated);
    }

    // ================================================================== save / load

    void Save()
    {
        var file = new ParticleEffectSaveFile
        {
            effects = new ParticleEffectData[_savedEffects.Count]
        };
        _savedEffects.Values.CopyTo(file.effects, 0);

        string json = JsonUtility.ToJson(file, prettyPrint: true);
        File.WriteAllText(SavePath, json);
        Debug.Log($"[ParticleEffectManager] Saved {file.effects.Length} effects → {SavePath}");
    }

    void LoadAndRespawn()
    {
        if (!File.Exists(SavePath))
        {
            Debug.Log("[ParticleEffectManager] No save file found — starting fresh.");
            return;
        }

        string json = File.ReadAllText(SavePath);
        var file = JsonUtility.FromJson<ParticleEffectSaveFile>(json);

        if (file?.effects == null)
        {
            Debug.LogWarning("[ParticleEffectManager] Save file empty or corrupt.");
            return;
        }

        foreach (var data in file.effects)
        {
            _savedEffects[data.effectId] = data;
            SpawnOne(data);
        }

        Debug.Log($"[ParticleEffectManager] Loaded & respawned {file.effects.Length} effects.");
    }

    // ================================================================== internal spawn

    void SpawnOne(ParticleEffectData data)
    {
        GameObject go;

        if (!string.IsNullOrEmpty(data.prefabName) && _prefabLookup.TryGetValue(data.prefabName, out var prefab))
        {
            // Prefab-based spawn
            go = Instantiate(prefab, data.Position, Quaternion.Euler(data.Rotation));
            go.transform.localScale = data.Scale;

            var tracker = go.GetComponent<ParticleEffectTracker>()
                        ?? go.AddComponent<ParticleEffectTracker>();
            tracker.effectId = data.effectId;
        }
        else
        {
            // Procedural spawn
            go = ProceduralParticleGenerator.Generate(data, transform);
        }

        _liveObjects[data.effectId] = go;
    }

    // ================================================================== helper type
    [System.Serializable]
    public class ParticlePrefabEntry
    {
        public string     name;
        public GameObject prefab;
    }
}
