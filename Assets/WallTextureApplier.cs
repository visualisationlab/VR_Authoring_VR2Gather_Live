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

    // ✅ Existing API (still supported)
    public void ApplyTextureUrlToHit(RaycastHit hit, string imageUrl, float tileScale)
    {
        StartCoroutine(ApplyTextureCoroutine_FromHit(hit, imageUrl, tileScale));
    }

    // ✅ NEW API for async jobs (safe to store and use later)
    // Works with VoiceCaptureAndSend.WallAnchor
    public void ApplyTextureUrlToAnchor(VoiceCaptureAndSend.WallAnchor anchor, string imageUrl, float tileScale)
    {
        if (!anchor.IsValid())
        {
            Debug.LogWarning("[WallTextureApplier] ApplyTextureUrlToAnchor: invalid anchor (wall == null).");
            return;
        }

        StartCoroutine(ApplyTextureCoroutine_FromWall(anchor.wall, imageUrl, tileScale));
    }

    // ------------------ Internals ------------------

    IEnumerator ApplyTextureCoroutine_FromHit(RaycastHit hit, string imageUrl, float tileScale)
    {
        var go = hit.collider != null ? hit.collider.gameObject : null;
        if (go == null) yield break;

        // Prefer PersistableAIObject on parent chain (more stable than collider object)
        var persist = go.GetComponentInParent<PersistableAIObject>();
        if (persist == null)
        {
            Debug.LogWarning("[WallTextureApplier] No PersistableAIObject found in parents. Add it to walls you want to persist.");
            yield break;
        }

        // Apply on the Persistable root (so we always target the same renderer)
        yield return ApplyTextureCoroutine_FromWall(persist.transform, imageUrl, tileScale);
    }

    IEnumerator ApplyTextureCoroutine_FromWall(Transform wallRoot, string imageUrl, float tileScale)
    {
        if (wallRoot == null) yield break;

        // Find persistence component
        var persist = wallRoot.GetComponent<PersistableAIObject>();
        if (persist == null)
            persist = wallRoot.GetComponentInParent<PersistableAIObject>();

        if (persist == null)
        {
            Debug.LogWarning("[WallTextureApplier] No PersistableAIObject found. Add it to walls you want to persist.");
            yield break;
        }

        // ✅ Safer: renderer may be on child
        var r = wallRoot.GetComponentInChildren<Renderer>(true);
        if (r == null)
        {
            Debug.LogWarning("[WallTextureApplier] No Renderer found on wall or its children.");
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

            // 2) Create a fresh Standard material (prevents black tint)
            Shader std = Shader.Find("Standard");
            if (std == null)
            {
                Debug.LogError("[WallTextureApplier] Standard shader not found.");
                yield break;
            }

            Material newMat = new Material(std);
            newMat.name = $"TexMat_{persist.id}";
            newMat.mainTexture = tex;
            newMat.color = Color.white;                 // ✅ critical: prevents tinting-to-black
            newMat.SetFloat("_Metallic", 0f);
            newMat.SetFloat("_Glossiness", 0.1f);
            newMat.mainTextureScale = new Vector2(ts, ts);

            // Apply to all sub-materials (multi-material renderers)
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