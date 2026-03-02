using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using GLTFast;

public class RuntimeModelSpawner : MonoBehaviour
{
    [Header("Server")]
    public string generateEndpoint = "http://localhost:8000/api/text-to-3d";

    [Header("Persistence")]
    public bool loadOnStart = true;

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

    // IMPORTANT: match what your server returns (VoiceCaptureAndSend uses: status + progress + downloadUrl)
    [Serializable]
    class TextTo3DResponse
    {
        public string status;        // e.g. PENDING / SUCCEEDED / FAILED  (or done)
        public int progress;         // 0..100 (if server sends it; default 0)
        public string downloadUrl;   // MUST be exactly "downloadUrl" in server JSON
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
        // 1) Ask server for model (poll until ready)
        var reqObj = new TextTo3DRequest { prompt = prompt, name = assetName, stage = stage, art_style = artStyle };
        string jsonReq = JsonUtility.ToJson(reqObj);

        string downloadUrl = null;

        // Poll up to ~12 minutes (240 * 3 sec)
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

                    // Accept multiple conventions:
                    // - done + downloadUrl
                    // - SUCCEEDED + downloadUrl
                    // - progress>=100 + downloadUrl
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

        // 2) Download GLB to persistentDataPath
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

        // 3) Load GLB via glTFast runtime
        yield return LoadGlbIntoScene(localPath, assetName, position);

        // 4) Save registry so it persists
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

        // Use a safe absolute file URI on Windows: file:///C:/...
        string uri = new Uri(localGlbPath).AbsoluteUri;

        var loadTask = import.Load(uri);
        while (!loadTask.IsCompleted) yield return null;

        bool success = false;
        try { success = loadTask.Result; }
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

        // If you ever spawn but can't see it, uncomment to force it visible in front of camera:
        // var cam = Camera.main;
        // if (cam != null) go.transform.position = cam.transform.position + cam.transform.forward * 2f;

        // Optional: attach your controller so voice can color/move/scale it
        if (go.GetComponent<AIControllable>() == null)
            go.AddComponent<AIControllable>();

        Debug.Log($"✅ Spawned runtime model: {instanceName} @ {localGlbPath} | children={go.transform.childCount}");
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

        // avoid duplicates by name (simple strategy)
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
