using UnityEngine;
using System.Collections.Generic;
using System.IO;

/// SceneStateStore
/// - Saves/loads PersistableAIObject transforms/material/color
/// - ALSO saves/loads posters (PersistablePoster) including local PNG path
/// - Spawns posters on Load (because posters are created at runtime and won’t exist in-scene)
public class SceneStateStore : MonoBehaviour
{
    public bool loadOnStart = true;
    public float saveDebounceSeconds = 0.25f;

    float _saveAt = -1f;

    string SavePath => Path.Combine(Application.persistentDataPath, "scene_state.json");

    [System.Serializable]
    class SceneState
    {
        public List<PersistableAIObject.ObjectState> objects = new List<PersistableAIObject.ObjectState>();
        public List<PersistablePoster.PosterState> posters = new List<PersistablePoster.PosterState>();
    }

    void Start()
    {
        if (loadOnStart) Load();
    }

    void Update()
    {
        if (_saveAt > 0f && Time.unscaledTime >= _saveAt)
        {
            _saveAt = -1f;
            Save();
        }
    }

    public void RequestSave()
    {
        _saveAt = Time.unscaledTime + saveDebounceSeconds;
    }

    void OnApplicationQuit()
    {
        // Final safety net so "saved when I exit" is true.
        Save();
    }

    [ContextMenu("Save Now")]
    public void Save()
    {
        var state = new SceneState();

        // ---- Save normal AI objects ----
        var persistables = FindObjectsOfType<PersistableAIObject>(true);
        foreach (var p in persistables)
        {
            if (p == null) continue;
            state.objects.Add(p.Capture());
        }

        // ---- Save posters ----
        var posters = FindObjectsOfType<PersistablePoster>(true);
        foreach (var p in posters)
        {
            if (p == null) continue;
            state.posters.Add(p.Capture());
        }

        var json = JsonUtility.ToJson(state, true);
        File.WriteAllText(SavePath, json);

        Debug.Log("[SceneStateStore] Saved to: " + SavePath);
    }

    [ContextMenu("Load Now")]
    public void Load()
    {
        if (!File.Exists(SavePath))
        {
            Debug.Log("[SceneStateStore] No save file yet: " + SavePath);
            return;
        }

        var json = File.ReadAllText(SavePath);
        var state = JsonUtility.FromJson<SceneState>(json);
        if (state == null) return;

        // ---- Load/apply normal AI objects (only applies to objects already in the scene) ----
        if (state.objects != null)
        {
            // Build lookup by id
            var persistables = FindObjectsOfType<PersistableAIObject>(true);
            var map = new Dictionary<string, PersistableAIObject>();
            foreach (var p in persistables)
            {
                if (p != null && !string.IsNullOrEmpty(p.id))
                    map[p.id] = p;
            }

            foreach (var s in state.objects)
            {
                if (s == null || string.IsNullOrEmpty(s.id)) continue;
                if (map.TryGetValue(s.id, out var p))
                    p.Apply(s);
            }
        }

        // ---- Load posters (spawn them; they may not exist in the scene yet) ----
        if (state.posters != null && state.posters.Count > 0)
        {
            // Avoid duplicate-spawning if Load is called twice
            var existing = FindObjectsOfType<PersistablePoster>(true);
            var existingIds = new HashSet<string>();
            foreach (var e in existing)
                if (e != null && !string.IsNullOrEmpty(e.id)) existingIds.Add(e.id);

            // Optional: if you have a PosterSpawner with a template material, use it.
            // If not found, we still spawn posters with a basic Unlit/Texture material.
            var spawner = FindFirstObjectByType<PosterSpawner>();

            foreach (var ps in state.posters)
            {
                if (ps == null || string.IsNullOrEmpty(ps.id)) continue;
                if (existingIds.Contains(ps.id)) continue;

                SpawnPosterFromState(ps, spawner);
            }
        }

        Debug.Log("[SceneStateStore] Loaded from: " + SavePath);
    }

    void SpawnPosterFromState(PersistablePoster.PosterState s, PosterSpawner spawner)
    {
        // Create quad
        var poster = GameObject.CreatePrimitive(PrimitiveType.Quad);

        // ✅ FIX: PersistablePoster.PosterState does NOT contain "name"
        // (your earlier version referenced s.name which would not compile)
        poster.name = "Poster_Loaded";

        // Add persistable + apply transform/meta
        var persist = poster.AddComponent<PersistablePoster>();
        persist.id = s.id;
        persist.Apply(s);

        // Apply transform
        poster.transform.position = new Vector3(s.px, s.py, s.pz);
        poster.transform.rotation = new Quaternion(s.rx, s.ry, s.rz, s.rw);
        poster.transform.localScale = new Vector3(s.sx, s.sy, s.sz);

        // Renderer + material
        var r = poster.GetComponent<Renderer>();

        Material mat;
        if (spawner != null && spawner.posterMaterialTemplate != null)
            mat = new Material(spawner.posterMaterialTemplate);
        else
            mat = new Material(Shader.Find("Unlit/Texture"));

        r.material = mat;

        // Load PNG from disk
        if (!string.IsNullOrEmpty(s.localPngPath) && File.Exists(s.localPngPath))
        {
            byte[] bytes = File.ReadAllBytes(s.localPngPath);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            tex.LoadImage(bytes);
            tex.wrapMode = TextureWrapMode.Clamp;
            r.material.mainTexture = tex;
        }
        else
        {
            Debug.LogWarning("[SceneStateStore] Poster PNG missing on disk: " + s.localPngPath);
            // You *could* re-download using s.imageUrl here, but that requires a coroutine.
        }

        // Optional: let your gaze/voice transform controls work on posters too
        var ai = poster.AddComponent<AIControllable>();
        ai.targetRenderer = r;
        ai.rb = null;
        ai.useRigidbodyWhenAvailable = false;
    }
}
