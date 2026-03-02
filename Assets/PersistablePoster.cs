using UnityEngine;

[DisallowMultipleComponent]
public class PersistablePoster : MonoBehaviour
{
    public string id;

    // Where the image came from (optional, good for debugging / re-download)
    public string imageUrl;

    // Where the image is stored locally (this is what guarantees persistence)
    public string localPngPath;

    public float widthMeters = 1f;
    public float heightMeters = 1f;

    void Awake()
    {
        if (string.IsNullOrEmpty(id))
            id = System.Guid.NewGuid().ToString("N");
    }

    [System.Serializable]
    public class PosterState
    {
        public string id;

        public float px, py, pz;
        public float rx, ry, rz, rw;
        public float sx, sy, sz;

        public float widthMeters, heightMeters;
        public string imageUrl;
        public string localPngPath;
    }

    public PosterState Capture()
    {
        var t = transform;
        return new PosterState
        {
            id = id,
            px = t.position.x,
            py = t.position.y,
            pz = t.position.z,
            rx = t.rotation.x,
            ry = t.rotation.y,
            rz = t.rotation.z,
            rw = t.rotation.w,
            sx = t.localScale.x,
            sy = t.localScale.y,
            sz = t.localScale.z,
            widthMeters = widthMeters,
            heightMeters = heightMeters,
            imageUrl = imageUrl,
            localPngPath = localPngPath
        };
    }

    public void Apply(PosterState s)
    {
        transform.position = new Vector3(s.px, s.py, s.pz);
        transform.rotation = new Quaternion(s.rx, s.ry, s.rz, s.rw);
        transform.localScale = new Vector3(s.sx, s.sy, s.sz);

        widthMeters = s.widthMeters;
        heightMeters = s.heightMeters;
        imageUrl = s.imageUrl;
        localPngPath = s.localPngPath;
    }
}
