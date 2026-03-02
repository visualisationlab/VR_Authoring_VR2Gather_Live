/*
using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.IO;

public class WallTextureApplier : MonoBehaviour
{
    public float defaultTileScale = 1.8f;

    string TexturesDir => Path.Combine(Application.persistentDataPath, "textures");

    void Awake()
    {
        Directory.CreateDirectory(TexturesDir);
    }

    // Call this when you already have a final imageUrl (FastAPI returns this)
    public void ApplyTextureUrlToHit(RaycastHit hit, string imageUrl, float tileScale)
    {
        StartCoroutine(ApplyTextureCoroutine(hit, imageUrl, tileScale));
    }

    IEnumerator ApplyTextureCoroutine(RaycastHit hit, string imageUrl, float tileScale)
    {
        var go = hit.collider != null ? hit.collider.gameObject : null;
        if (go == null) yield break;

        var r = go.GetComponent<Renderer>();
        if (r == null)
        {
            Debug.LogWarning("[WallTextureApplier] No Renderer on hit object.");
            yield break;
        }

        var persist = go.GetComponent<PersistableAIObject>();
        if (persist == null)
        {
            Debug.LogWarning("[WallTextureApplier] No PersistableAIObject on hit object. Add it to walls you want to persist.");
            yield break;
        }

        // 1) Download image from URL
        using (var req = UnityWebRequestTexture.GetTexture(imageUrl))
        {
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("[WallTextureApplier] Texture download failed: " + req.error);
                yield break;
            }

            var tex = DownloadHandlerTexture.GetContent(req);
            tex.wrapMode = TextureWrapMode.Repeat;

            // 2) Apply to material
            r.material.mainTexture = tex;
            r.material.color = Color.white;

            float ts = (tileScale > 0f) ? tileScale : defaultTileScale;
            r.material.mainTextureScale = new Vector2(ts, ts);

            // 3) Save PNG locally (persistence)
            byte[] png = tex.EncodeToPNG();
            string localPath = Path.Combine(TexturesDir, $"{persist.id}_tex.png");
            File.WriteAllBytes(localPath, png);

            // 4) Store on PersistableAIObject so SceneStateStore saves it
            persist.textureUrl = imageUrl;
            persist.localTexturePath = localPath;
            persist.tileScale = ts;

            // 5) Request scene save
            FindFirstObjectByType<SceneStateStore>()?.RequestSave();

            Debug.Log("[WallTextureApplier] Saved texture png: " + localPath);
        }
    }
}

*/

///*
using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.IO;

public class WallTextureApplier : MonoBehaviour
{
    public float defaultTileScale = 1.8f;

    string TexturesDir => Path.Combine(Application.persistentDataPath, "textures");

    void Awake()
    {
        Directory.CreateDirectory(TexturesDir);
    }

    // Call this when you already have a final imageUrl (FastAPI returns this)
    public void ApplyTextureUrlToHit(RaycastHit hit, string imageUrl, float tileScale)
    {
        StartCoroutine(ApplyTextureCoroutine(hit, imageUrl, tileScale));
    }

    IEnumerator ApplyTextureCoroutine(RaycastHit hit, string imageUrl, float tileScale)
    {
        var go = hit.collider != null ? hit.collider.gameObject : null;
        if (go == null) yield break;

        // ✅ Safer: collider may be on parent, renderer on child
        var r = go.GetComponentInChildren<Renderer>(true);
        if (r == null)
        {
            Debug.LogWarning("[WallTextureApplier] No Renderer found on hit object or its children.");
            yield break;
        }

        var persist = go.GetComponent<PersistableAIObject>();
        if (persist == null)
        {
            // Sometimes PersistableAIObject is on parent, not the collider object
            persist = go.GetComponentInParent<PersistableAIObject>();
        }

        if (persist == null)
        {
            Debug.LogWarning("[WallTextureApplier] No PersistableAIObject found. Add it to walls you want to persist.");
            yield break;
        }

        // 1) Download image from URL
        using (var req = UnityWebRequestTexture.GetTexture(imageUrl))
        {
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("[WallTextureApplier] Texture download failed: " + req.error);
                yield break;
            }

            var tex = DownloadHandlerTexture.GetContent(req);
            if (tex == null)
            {
                Debug.LogError("[WallTextureApplier] Downloaded texture is null.");
                yield break;
            }

            tex.wrapMode = TextureWrapMode.Repeat;

            float ts = (tileScale > 0f) ? tileScale : defaultTileScale;

            // ✅ Built-in robust: replace material with fresh Standard and apply to ALL slots
            Shader std = Shader.Find("Standard");
            if (std == null)
            {
                Debug.LogError("[WallTextureApplier] Standard shader not found.");
                yield break;
            }

            Material newMat = new Material(std);
            newMat.name = $"TexMat_{persist.id}";
            newMat.mainTexture = tex;
            newMat.color = Color.white;                 // ✅ critical: prevents black tint
            newMat.SetFloat("_Metallic", 0f);
            newMat.SetFloat("_Glossiness", 0.1f);
            newMat.mainTextureScale = new Vector2(ts, ts);

            // Apply to all sub-materials
            var mats = r.materials;
            if (mats == null || mats.Length == 0)
            {
                r.material = newMat;
            }
            else
            {
                for (int i = 0; i < mats.Length; i++)
                    mats[i] = newMat;
                r.materials = mats;
            }

            // 3) Save PNG locally (persistence)
            byte[] png = tex.EncodeToPNG();
            string localPath = Path.Combine(TexturesDir, $"{persist.id}_tex.png");
            File.WriteAllBytes(localPath, png);

            // 4) Store on PersistableAIObject so SceneStateStore saves it
            persist.textureUrl = imageUrl;
            persist.localTexturePath = localPath;
            persist.tileScale = ts;

            // Optional: ensure any “saved color” won’t tint textures later
            if (persist.controllable != null)
                persist.controllable.TrySetColor(Color.white);

            // 5) Request scene save
            FindFirstObjectByType<SceneStateStore>()?.RequestSave();

            Debug.Log("[WallTextureApplier] Applied + saved texture png: " + localPath);
        }
    }
}
