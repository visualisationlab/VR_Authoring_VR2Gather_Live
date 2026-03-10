using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.IO;

public class PosterSpawner : MonoBehaviour
{
    [Header("Materials")]
    public Material posterMaterialTemplate;       // Unlit/Texture recommended
    public float surfaceOffsetMeters = 0.005f;

    [Header("Debug")]
    public bool logSizes = false;

    string PostersDir => Path.Combine(Application.persistentDataPath, "posters");

    void Awake()
    {
        Directory.CreateDirectory(PostersDir);
    }

    // ✅ Existing API (still supported)
    public void CreatePosterAtHit(RaycastHit hit, string imageUrl, float widthMeters, float heightMeters)
    {
        StartCoroutine(CreatePosterCoroutine(
            hit.collider != null ? hit.collider.transform : null,
            hit.point,
            hit.normal,
            imageUrl,
            widthMeters,
            heightMeters
        ));
    }

    // ✅ NEW API for async jobs (safe to store and use later)
    public void CreatePosterAtAnchor(VoiceCaptureAndSend.WallAnchor anchor, string imageUrl, float widthMeters, float heightMeters)
    {
        if (!anchor.IsValid())
        {
            Debug.LogWarning("[PosterSpawner] CreatePosterAtAnchor: invalid anchor (wall == null).");
            return;
        }

        Vector3 point = anchor.WorldPoint();
        Vector3 normal = anchor.WorldNormal();
        StartCoroutine(CreatePosterCoroutine(anchor.wall, point, normal, imageUrl, widthMeters, heightMeters));
    }

    IEnumerator CreatePosterCoroutine(Transform parentWall, Vector3 worldPoint, Vector3 worldNormal,
                                     string imageUrl, float widthMeters, float heightMeters)
    {
        // Clamp sizes (safety)
        widthMeters = Mathf.Max(0.01f, widthMeters);
        heightMeters = Mathf.Max(0.01f, heightMeters);

        // 1) Create poster (unparented first so "localScale == world scale")
        var poster = GameObject.CreatePrimitive(PrimitiveType.Quad);
        poster.name = $"Poster_{System.DateTime.Now:HHmmss}";

        var persist = poster.AddComponent<PersistablePoster>();
        persist.imageUrl = imageUrl;
        persist.widthMeters = widthMeters;
        persist.heightMeters = heightMeters;

        // 2) Place + orient to wall normal (Quad forward points outward)
        Vector3 n = (worldNormal.sqrMagnitude > 0.0001f) ? worldNormal.normalized : Vector3.forward;

        poster.transform.position = worldPoint + n * surfaceOffsetMeters;
        poster.transform.rotation = Quaternion.LookRotation(-n, Vector3.up);

        // 3) Set WORLD size in meters (since it's unparented)
        poster.transform.localScale = new Vector3(widthMeters, heightMeters, 1f);

        // 4) Now parent it, preserving WORLD transform/scale
        if (parentWall != null)
            poster.transform.SetParent(parentWall, worldPositionStays: true);

        if (logSizes && parentWall != null)
        {
            Debug.Log($"[PosterSpawner] wall lossyScale={parentWall.lossyScale}, poster localScale={poster.transform.localScale}, poster lossyScale={poster.transform.lossyScale}");
        }

        // 5) Download texture
        using (var req = UnityWebRequestTexture.GetTexture(imageUrl))
        {
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("[PosterSpawner] Poster download failed: " + req.error);
                Destroy(poster);
                yield break;
            }

            var tex = DownloadHandlerTexture.GetContent(req);
            tex.wrapMode = TextureWrapMode.Clamp;

            // 6) Apply material
            var r = poster.GetComponent<Renderer>();
            Material mat = (posterMaterialTemplate != null)
                ? new Material(posterMaterialTemplate)
                : new Material(Shader.Find("Unlit/Texture"));

            mat.mainTexture = tex;
            r.material = mat;

            // 7) Save PNG locally (persistence)
            byte[] png = tex.EncodeToPNG();
            string localPath = Path.Combine(PostersDir, $"{persist.id}.png");
            File.WriteAllBytes(localPath, png);
            persist.localPngPath = localPath;

            Debug.Log("[PosterSpawner] Saved poster png: " + localPath);
        }

        // 8) Optional: make it gaze-editable
        var ai = poster.AddComponent<AIControllable>();
        ai.targetRenderer = poster.GetComponent<Renderer>();
        ai.rb = null;
        ai.useRigidbodyWhenAvailable = false;
    }
}