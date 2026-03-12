using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;
using GLTFast;

public class RuntimeModelSpawner : MonoBehaviour
{
    [Header("Server")]
    public string generateEndpoint = "http://localhost:8000/api/text-to-3d";

    [Header("Persistence")]
    public bool loadOnStart = true;

    [Header("Generated Model Setup")]
    public bool addMeshCollidersToChildren = true;
    public bool addRootBoxColliderIfMissing = true;

    string ModelsDir => Path.Combine(Application.persistentDataPath, "GeneratedModels");
    string RegistryPath => Path.Combine(Application.persistentDataPath, "generated_models_registry.json");

    [Serializable]
    public class SpawnRecord
    {
        public string name;
        public string localGlbPath;
        public Vector3 position;
        public Vector3 eulerRotation;
        public Vector3 scale = Vector3.one;
    }

    [Serializable]
    public class Registry
    {
        public List<SpawnRecord> items = new List<SpawnRecord>();
    }

    [Serializable]
    class TextTo3DRequest
    {
        public string prompt;
        public string name;
        public string stage = "preview";
        public string art_style = "realistic";
    }

    [Serializable]
    class TextTo3DResponse
    {
        public string status;
        public int progress;
        public string downloadUrl;
        public string message;
        public string jobId;
    }

    void Awake()
    {
        Directory.CreateDirectory(ModelsDir);
    }

    void Start()
    {
        if (loadOnStart)
            StartCoroutine(RespawnSavedModels());
    }

    public void GenerateAndSpawn(string prompt, string assetName, Vector3 position, string stage = "preview", string artStyle = "realistic")
    {
        StartCoroutine(GenerateAndSpawnCo(prompt, assetName, position, stage, artStyle));
    }

    IEnumerator GenerateAndSpawnCo(string prompt, string assetName, Vector3 position, string stage, string artStyle)
    {
        var reqObj = new TextTo3DRequest
        {
            prompt = prompt,
            name = assetName,
            stage = stage,
            art_style = artStyle
        };

        string jsonReq = JsonUtility.ToJson(reqObj);
        string downloadUrl = null;

        for (int attempt = 1; attempt <= 240; attempt++)
        {
            using (var www = new UnityWebRequest(generateEndpoint, "POST"))
            {
                byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonReq);
                www.uploadHandler = new UploadHandlerRaw(bodyRaw);
                www.downloadHandler = new DownloadHandlerBuffer();
                www.SetRequestHeader("Content-Type", "application/json");

                yield return www.SendWebRequest();

                if (www.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError($"[RuntimeModelSpawner] Server error: {www.error}\n{www.downloadHandler.text}");
                    yield break;
                }

                var raw = www.downloadHandler.text;
                TextTo3DResponse resp = null;

                try
                {
                    resp = JsonUtility.FromJson<TextTo3DResponse>(raw);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[RuntimeModelSpawner] Could not parse JSON (will retry): " + e + "\nRAW:\n" + raw);
                }

                if (resp != null)
                {
                    string st = (resp.status ?? "").Trim().ToUpperInvariant();
                    int pr = resp.progress;

                    Debug.Log($"[RuntimeModelSpawner] Poll {attempt}/240 status={st} progress={pr} url={(string.IsNullOrEmpty(resp.downloadUrl) ? "null" : "ok")}");

                    bool ready =
                        !string.IsNullOrEmpty(resp.downloadUrl) &&
                        (st == "DONE" || st == "SUCCEEDED" || st == "SUCCESS" || pr >= 100);

                    if (ready)
                    {
                        downloadUrl = resp.downloadUrl;
                        break;
                    }

                    if (st == "FAILED" || st == "ERROR")
                    {
                        Debug.LogError("[RuntimeModelSpawner] Generation failed: " + resp.message);
                        yield break;
                    }
                }
            }

            yield return new WaitForSeconds(3f);
        }

        if (string.IsNullOrEmpty(downloadUrl))
        {
            Debug.LogError("[RuntimeModelSpawner] Timed out waiting for model generation. (No downloadUrl received)");
            yield break;
        }

        string safeName = Sanitize(assetName);
        string localPath = Path.Combine(ModelsDir, safeName + ".glb");

        using (var dl = UnityWebRequest.Get(downloadUrl))
        {
            Debug.Log("[RuntimeModelSpawner] Downloading: " + downloadUrl);
            yield return dl.SendWebRequest();

            if (dl.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[RuntimeModelSpawner] Download failed: {dl.error}\n{dl.downloadHandler.text}");
                yield break;
            }

            try
            {
                File.WriteAllBytes(localPath, dl.downloadHandler.data);
            }
            catch (Exception e)
            {
                Debug.LogError("[RuntimeModelSpawner] Failed writing GLB to disk: " + e + "\nPath=" + localPath);
                yield break;
            }
        }

        yield return LoadGlbIntoScene(localPath, assetName, position);

        SaveRecord(new SpawnRecord
        {
            name = assetName,
            localGlbPath = localPath,
            position = position,
            eulerRotation = Vector3.zero,
            scale = Vector3.one
        });
    }

    IEnumerator LoadGlbIntoScene(string localGlbPath, string instanceName, Vector3 position)
    {
        if (!File.Exists(localGlbPath))
        {
            Debug.LogError("[RuntimeModelSpawner] GLB file missing: " + localGlbPath);
            yield break;
        }

        var import = new GltfImport();
        string uri = new Uri(localGlbPath).AbsoluteUri;

        var loadTask = import.Load(uri);
        while (!loadTask.IsCompleted) yield return null;

        bool success = false;
        try
        {
            success = loadTask.Result;
        }
        catch (Exception e)
        {
            Debug.LogError("[RuntimeModelSpawner] glTFast Load threw: " + e);
            yield break;
        }

        if (!success)
        {
            Debug.LogError("[RuntimeModelSpawner] glTFast Load failed: " + localGlbPath);
            yield break;
        }

        var go = new GameObject(instanceName);
        go.transform.position = position;

        var instTask = import.InstantiateMainSceneAsync(go.transform);
        while (!instTask.IsCompleted) yield return null;

        var ai = go.GetComponent<AIControllable>();
        if (ai == null)
            ai = go.AddComponent<AIControllable>();

        Renderer[] childRenderers = go.GetComponentsInChildren<Renderer>(true);
        if (childRenderers != null && childRenderers.Length > 0)
        {
            ai.targetRenderer = childRenderers
                .OrderByDescending(r => r.bounds.size.magnitude)
                .FirstOrDefault();
        }

        if (addMeshCollidersToChildren)
        {
            var meshFilters = go.GetComponentsInChildren<MeshFilter>(true);
            foreach (var mf in meshFilters)
            {
                if (mf == null || mf.sharedMesh == null)
                    continue;

                if (mf.GetComponent<Collider>() == null)
                {
                    try
                    {
                        var mc = mf.gameObject.AddComponent<MeshCollider>();
                        mc.sharedMesh = mf.sharedMesh;
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning("[RuntimeModelSpawner] Could not add MeshCollider to " + mf.name + ": " + e.Message);
                    }
                }
            }
        }

        if (addRootBoxColliderIfMissing && go.GetComponentInChildren<Collider>(true) == null)
        {
            var bc = go.AddComponent<BoxCollider>();

            if (ai.targetRenderer != null)
            {
                Bounds b = ai.targetRenderer.bounds;
                bc.center = go.transform.InverseTransformPoint(b.center);
                bc.size = b.size;
            }
            else
            {
                bc.center = Vector3.zero;
                bc.size = Vector3.one;
            }
        }

        Debug.Log(
            $"✅ Spawned runtime model: {instanceName} @ {localGlbPath} | children={go.transform.childCount} | targetRenderer={(ai.targetRenderer ? ai.targetRenderer.name : "null")}"
        );
    }

    IEnumerator RespawnSavedModels()
    {
        var reg = LoadRegistry();
        foreach (var item in reg.items)
        {
            if (string.IsNullOrEmpty(item.localGlbPath)) continue;
            if (!File.Exists(item.localGlbPath)) continue;

            yield return LoadGlbIntoScene(item.localGlbPath, item.name, item.position);
        }
    }

    void SaveRecord(SpawnRecord rec)
    {
        var reg = LoadRegistry();
        reg.items.RemoveAll(x => x.name == rec.name);
        reg.items.Add(rec);

        File.WriteAllText(RegistryPath, JsonUtility.ToJson(reg, true));
    }

    Registry LoadRegistry()
    {
        if (!File.Exists(RegistryPath)) return new Registry();

        try
        {
            return JsonUtility.FromJson<Registry>(File.ReadAllText(RegistryPath)) ?? new Registry();
        }
        catch
        {
            return new Registry();
        }
    }

    static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');

        return s.Trim();
    }
}