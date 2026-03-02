using UnityEngine;
using UnityEngine.SceneManagement;

public class XRSceneDebug : MonoBehaviour
{
    void Start()
    {
        var cams = FindObjectsOfType<Camera>(true);
        var origins = GameObject.FindObjectsOfType<Unity.XR.CoreUtils.XROrigin>(true);

        Debug.Log($"[XRSceneDebug] Scene: {SceneManager.GetActiveScene().name}");
        Debug.Log($"[XRSceneDebug] Cameras found: {cams.Length}");
        foreach (var c in cams) Debug.Log($"  Camera: {c.name} active={c.gameObject.activeInHierarchy} tag={c.tag}");

        Debug.Log($"[XRSceneDebug] XROrigins found: {origins.Length}");
        foreach (var o in origins) Debug.Log($"  XROrigin: {o.name} active={o.gameObject.activeInHierarchy}");
    }
}
