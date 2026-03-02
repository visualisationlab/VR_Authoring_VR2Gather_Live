using UnityEngine;

public class PosterSurface : MonoBehaviour
{
    public string imageUrl;         // or local file path if you save bytes
    public float widthMeters = 1f;
    public float heightMeters = 1f;

    // Optional: remember what we stuck it to
    public string wallObjectId = ""; // if you have IDs for walls
}
