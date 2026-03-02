using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.IO;

public class PosterSpawner : MonoBehaviour
{
    [Header("Materials")]
    public Material posterMaterialTemplate;       // Unlit/Texture recommended
    public float surfaceOffsetMeters = 0.005f;

    string PostersDir => Path.Combine(Application.persistentDataPath, "posters");

    void Awake()
    {
        Directory.CreateDirectory(PostersDir);
    }

    public void CreatePosterAtHit(RaycastHit hit, string imageUrl, float widthMeters, float heightMeters)
    {
        StartCoroutine(CreatePosterCoroutine(hit, imageUrl, widthMeters, heightMeters));
    }

    IEnumerator CreatePosterCoroutine(RaycastHit hit, string imageUrl, float widthMeters, float heightMeters)
    {
        // 1) Create poster object first (so we have an id for filename)
        var poster = GameObject.CreatePrimitive(PrimitiveType.Quad);
        poster.name = $"Poster_{System.DateTime.Now:HHmmss}";

        var persist = poster.AddComponent<PersistablePoster>();
        persist.imageUrl = imageUrl;
        persist.widthMeters = widthMeters;
        persist.heightMeters = heightMeters;

        // 2) Place + orient to wall normal (Quad forward points outward)
        poster.transform.position = hit.point + hit.normal * surfaceOffsetMeters;
        poster.transform.rotation = Quaternion.LookRotation(-hit.normal, Vector3.up);

        poster.transform.localScale = new Vector3(
            Mathf.Max(0.01f, widthMeters),
            Mathf.Max(0.01f, heightMeters),
            1f
        );

        // 3) Download texture
        using (var req = UnityWebRequestTexture.GetTexture(imageUrl))
        {
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("Poster download failed: " + req.error);
                Destroy(poster);
                yield break;
            }

            var tex = DownloadHandlerTexture.GetContent(req);
            tex.wrapMode = TextureWrapMode.Clamp;

            // 4) Apply material
            var r = poster.GetComponent<Renderer>();
            Material mat = (posterMaterialTemplate != null)
                ? new Material(posterMaterialTemplate)
                : new Material(Shader.Find("Unlit/Texture"));

            mat.mainTexture = tex;
            r.material = mat;

            // 5) Save PNG locally (THIS is persistence)
            byte[] png = tex.EncodeToPNG();
            string localPath = Path.Combine(PostersDir, $"{persist.id}.png");
            File.WriteAllBytes(localPath, png);
            persist.localPngPath = localPath;

            Debug.Log("[PosterSpawner] Saved poster png: " + localPath);
        }

        // 6) Optional: make it gaze-editable by your existing system
        var ai = poster.AddComponent<AIControllable>();
        ai.targetRenderer = poster.GetComponent<Renderer>();
        ai.rb = null;
        ai.useRigidbodyWhenAvailable = false;
    }
}
