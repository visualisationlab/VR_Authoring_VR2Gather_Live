using UnityEngine;
using GLTFast;

public class GLTFastSmokeTest : MonoBehaviour
{
    void Start()
    {
        var imp = new GltfImport();
        Debug.Log("glTFast OK: " + imp);
    }
}
