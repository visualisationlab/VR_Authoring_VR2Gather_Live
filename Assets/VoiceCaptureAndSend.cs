using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.IO;
using System.Text;
using TMPro;
using UnityEngine.XR;
using System.Collections.Generic;

public class VoiceCaptureAndSend : MonoBehaviour
{
    [Header("Microphone")]
    public string microphoneDevice;
    public int sampleRate = 16000;
    public int maxRecordLengthSeconds = 600;

    private AudioClip recordedClip;
    private bool isRecording = false;

    [Header("Optional systems")]
    public RuntimeModelSpawner modelSpawner;

    [Header("Posters (NEW)")]
    public PosterSpawner posterSpawner;

    [Header("Persistence")]
    public SceneStateStore stateStore;

    [Header("Server")]
    public string serverUrl = "http://localhost:8000/transcribe";

    // Meshy polling endpoint
    public string textTo3dUrl = "http://localhost:8000/api/text-to-3d";

    [Header("Gaze Target")]
    public GazeTargetInteractor gazeInteractor;

    [Header("UI Feedback (Optional)")]
    public TextMeshProUGUI aiIntentText;         // ✅ will show clean one-line messages (overwrites old)
    public TextMeshProUGUI transcriptText;
    public TextMeshProUGUI recordingStatusText;

    [Header("Keyboard Controls")]
    public KeyCode startKey = KeyCode.R;
    public KeyCode stopKey = KeyCode.T;

    // ---------------- NEW: movement/scale interpretation ----------------
    [Header("Movement & Scale Interpretation (NEW)")]
    [Tooltip("If true: dx/dy/dz are interpreted relative to the camera (left/right/up/forward).")]
    public bool translateInCameraSpace = true;

    [Tooltip("If true: treat values that look like centimeters as cm and convert to meters.")]
    public bool autoConvertCmToMeters = true;

    [Tooltip("Heuristic range for cm values (absolute). If value is in [cmMin..cmMax] it will be treated as centimeters.")]
    public float cmMin = 2f;

    [Tooltip("Heuristic range for cm values (absolute). If value is in [cmMin..cmMax] it will be treated as centimeters.")]
    public float cmMax = 500f;

    [Tooltip("If a scale factor is > this, treat it as percent (e.g., 20 -> 1.20).")]
    public float scalePercentThreshold = 3f;

    [Tooltip("Clamp scale factor to avoid crazy jumps.")]
    public float minScaleFactor = 0.1f;

    [Tooltip("Clamp scale factor to avoid crazy jumps.")]
    public float maxScaleFactor = 10f;

    // ---------- Two-line UI state ----------
    private string _recordingLine = "";
    private string _generationLine = "";
    private int _lastProgress = -1;
    private string _lastGenStatus = "";

    // XR controller (optional)
    private InputDevice rightController;
    private bool lastAPressed = false;

    // Track current polling coroutine so multiple generate calls don't overlap
    private Coroutine _pollRoutine = null;

    // ---------- JSON models ----------
    [System.Serializable]
    public class Command
    {
        public string action;
        public string target;

        public string color;

        public string text; // for write_text
        public float font_size; // comes from server

        public string texture_prompt;   // e.g. "seamless brick wall texture..."
        public float tile_scale;

        public float x;
        public float y;
        public float z;

        public float dx;
        public float dy;
        public float dz;

        public float uniform;
        public float factor;

        public string prompt;
        public string name;
        public string stage;
        public string art_style;

        public string material;

        // -------- POSTER FIELDS (NEW) --------
        public float width_m;     // e.g., 2.0
        public float height_m;    // e.g., 3.0
        public string image_url;  // optional if server returns it directly in command
        public string image_prompt; // optional if you want a separate prompt for the poster image
    }

    [System.Serializable]
    public class AgentResult
    {
        public string transcript;
        public Command command;
        public Command[] commands;
    }

    // Progress request/response
    [System.Serializable]
    private class TextTo3DRequest
    {
        public string prompt;
        public string name;
        public string stage;
        public string art_style;
    }

    [System.Serializable]
    private class TextTo3DProgressResponse
    {
        public string status;
        public int progress;
        public string downloadUrl;
        public string message;
    }

    // Poster image request/response (NEW)
    [System.Serializable]
    private class PosterImageRequest
    {
        public string prompt;
        public int width_px = 1024;
        public int height_px = 1024;
    }

    [System.Serializable]
    private class PosterImageResponse
    {
        public string image_url; // server should return this
        public string message;
    }

    [System.Serializable]
    private class TextureImageRequest
    {
        public string prompt;
        public int size_px = 1024;
    }

    void Start()
    {
        // Pick mic
        if (Microphone.devices.Length > 0)
        {
            microphoneDevice = Microphone.devices[0];
            Debug.Log("[VoiceCaptureAndSend] Using microphone: " + microphoneDevice);
        }
        else
        {
            Debug.LogError("[VoiceCaptureAndSend] No microphone found");
            SetRecordingLine("❌ No microphone found.");
        }

        // Auto-find gaze
        if (gazeInteractor == null)
            gazeInteractor = FindFirstObjectByType<GazeTargetInteractor>();

        if (gazeInteractor == null)
            Debug.LogError("[VoiceCaptureAndSend] GazeTargetInteractor not found. Add it to the scene and/or assign it in Inspector.");

        // Auto-find persistence store
        if (stateStore == null)
            stateStore = FindFirstObjectByType<SceneStateStore>();

        // Auto-find model spawner (optional)
        if (modelSpawner == null)
            modelSpawner = FindFirstObjectByType<RuntimeModelSpawner>();

        // Auto-find poster spawner (NEW)
        if (posterSpawner == null)
            posterSpawner = FindFirstObjectByType<PosterSpawner>();

        if (posterSpawner == null)
            Debug.LogWarning("[VoiceCaptureAndSend] PosterSpawner not found. Posters won't work until you add one to the scene.");

        SetRecordingLine("Ready. Use Right Controller Key A to record/stop");
        SetGenerationLine(""); // empty second line initially

        // Try to get right controller (optional)
        var devices = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(XRNode.RightHand, devices);

        if (devices.Count > 0)
        {
            rightController = devices[0];
            Debug.Log("Right controller found: " + rightController.name);
        }
        else
        {
            Debug.LogWarning("Right controller not found.");
        }
    }

    void Update()
    {
        // --- Keyboard ---
        if (Input.GetKeyDown(startKey)) StartRecording();
        if (Input.GetKeyDown(stopKey)) StopRecordingAndSend();

        // --- Controller A toggle (optional) ---
        if (rightController.isValid)
        {
            bool aPressed;
            if (rightController.TryGetFeatureValue(CommonUsages.primaryButton, out aPressed))
            {
                if (aPressed && !lastAPressed)
                {
                    if (!isRecording) StartRecording();
                    else StopRecordingAndSend();
                }
                lastAPressed = aPressed;
            }
        }
    }

    // ---------------- UI helpers ----------------

    private void RenderStatus()
    {
        if (recordingStatusText == null) return;

        string l1 = string.IsNullOrEmpty(_recordingLine) ? "" : _recordingLine;
        string l2 = string.IsNullOrEmpty(_generationLine) ? "" : _generationLine;

        if (!string.IsNullOrEmpty(l1) && !string.IsNullOrEmpty(l2))
            recordingStatusText.text = l1 + "\n" + l2;
        else
            recordingStatusText.text = l1 + l2;
    }

    private void SetRecordingLine(string msg)
    {
        _recordingLine = msg;
        RenderStatus();
    }

    private void SetGenerationLine(string msg)
    {
        _generationLine = msg;
        RenderStatus();
    }

    // ---------------- Clean one-line status in aiIntentText ----------------

    private string F(float v) => v.ToString("0.00");

    private void ShowCleanAction(string msg)
    {
        // Overwrites old message (old gets deleted)
        if (aiIntentText != null) aiIntentText.text = msg;
    }

    private bool TryGetCurrentTargetObject(out GameObject go)
    {
        go = null;
        if (gazeInteractor == null) return false;

        if (gazeInteractor.TryGetCurrentHit(out RaycastHit hit) && hit.collider != null)
        {
            go = hit.collider.gameObject;
            return true;
        }
        return false;
    }

    // ---------------- NEW: helpers for “weird” movement/scale ----------------

    private float NormalizeDistanceToMeters(float v)
    {
        if (!autoConvertCmToMeters) return v;

        float av = Mathf.Abs(v);
        if (av >= cmMin && av <= cmMax)
            return v * 0.01f; // cm -> meters

        return v; // already meters
    }

    private Vector3 ToWorldDelta(float dx, float dy, float dz)
    {
        // Convert cm->m if needed
        dx = NormalizeDistanceToMeters(dx);
        dy = NormalizeDistanceToMeters(dy);
        dz = NormalizeDistanceToMeters(dz);

        if (!translateInCameraSpace)
            return new Vector3(dx, dy, dz);

        var cam = Camera.main;
        if (cam == null)
            return new Vector3(dx, dy, dz);

        // Camera-relative axes:
        // +dx = right, -dx = left
        // +dy = up,    -dy = down
        // +dz = forward, -dz = backward
        return cam.transform.right * dx + cam.transform.up * dy + cam.transform.forward * dz;
    }

    private float NormalizeScaleFactor(float f)
    {
        // If server sends "20" meaning 20% -> 1.20
        if (Mathf.Abs(f) > scalePercentThreshold)
            f = 1f + (f / 100f);

        // Guard and clamp
        if (f <= 0.01f) return 0f;

        return Mathf.Clamp(f, minScaleFactor, maxScaleFactor);
    }

    private void ShowCleanMessageForCommand(Command cmd)
    {
        if (cmd == null || string.IsNullOrWhiteSpace(cmd.action)) return;

        string action = cmd.action.Trim().ToLowerInvariant();

        switch (action)
        {
            case "set_color":
                ShowCleanAction($"change_color {cmd.color}");
                break;

            case "create_poster":
                {
                    string p = !string.IsNullOrWhiteSpace(cmd.image_prompt) ? cmd.image_prompt : cmd.prompt;
                    if (string.IsNullOrWhiteSpace(p)) p = "A beautiful minimal poster";
                    ShowCleanAction($"poster_generation {p}");
                    break;
                }

            case "move":
                {
                    // NOTE: move is absolute world position (x,y,z)
                    Vector3 newPos = new Vector3(cmd.x, cmd.y, cmd.z);

                    if (TryGetCurrentTargetObject(out GameObject go))
                    {
                        Vector3 oldPos = go.transform.position;
                        ShowCleanAction($"move old({F(oldPos.x)},{F(oldPos.y)},{F(oldPos.z)}) new({F(newPos.x)},{F(newPos.y)},{F(newPos.z)})");
                    }
                    else
                    {
                        ShowCleanAction($"move new({F(newPos.x)},{F(newPos.y)},{F(newPos.z)})");
                    }
                    break;
                }

            case "translate":
                {
                    // We show the *interpreted* world delta, not raw dx/dy/dz
                    if (TryGetCurrentTargetObject(out GameObject go))
                    {
                        Vector3 oldPos = go.transform.position;
                        Vector3 deltaWorld = ToWorldDelta(cmd.dx, cmd.dy, cmd.dz);
                        Vector3 newPos = oldPos + deltaWorld;
                        ShowCleanAction($"translate old({F(oldPos.x)},{F(oldPos.y)},{F(oldPos.z)}) new({F(newPos.x)},{F(newPos.y)},{F(newPos.z)})");
                    }
                    else
                    {
                        Vector3 deltaWorld = ToWorldDelta(cmd.dx, cmd.dy, cmd.dz);
                        ShowCleanAction($"translate delta({F(deltaWorld.x)},{F(deltaWorld.y)},{F(deltaWorld.z)})");
                    }
                    break;
                }

            case "scale_by":
                {
                    float f = NormalizeScaleFactor(cmd.factor);
                    if (f > 0f) ShowCleanAction($"scale_by {F(f)}x");
                    else ShowCleanAction("scale_by (invalid)");
                    break;
                }

            default:
                ShowCleanAction(action);
                break;
        }
    }

    // ---------------- Recording ----------------

    public void StartRecording()
    {
        if (isRecording)
        {
            Debug.LogWarning("[VoiceCaptureAndSend] StartRecording ignored: already recording.");
            SetRecordingLine("⚠ Already recording...");
            return;
        }

        if (string.IsNullOrEmpty(microphoneDevice))
        {
            Debug.LogWarning("[VoiceCaptureAndSend] Cannot start recording: no mic selected.");
            SetRecordingLine("❌ No microphone selected.");
            return;
        }

        recordedClip = Microphone.Start(microphoneDevice, true, maxRecordLengthSeconds, sampleRate);
        isRecording = true;

        SetRecordingLine("🎙 Recording started...");
        Debug.Log("[VoiceCaptureAndSend] Recording started (max " + maxRecordLengthSeconds + "s)");
    }

    public void StopRecordingAndSend()
    {
        if (!isRecording)
        {
            Debug.LogWarning("[VoiceCaptureAndSend] StopRecordingAndSend called but not recording.");
            SetRecordingLine("⚠ Not recording.");
            return;
        }

        int position = Microphone.GetPosition(microphoneDevice);
        Microphone.End(microphoneDevice);
        isRecording = false;

        SetRecordingLine("⏹ Recording stopped. Processing...");
        Debug.Log("[VoiceCaptureAndSend] Recording stopped at sample position: " + position);

        if (position <= 0 || recordedClip == null)
        {
            Debug.LogWarning("[VoiceCaptureAndSend] No audio captured.");
            SetRecordingLine("⚠ No audio captured.");
            return;
        }

        int channels = recordedClip.channels;
        float[] samples = new float[position * channels];
        recordedClip.GetData(samples, 0);

        AudioClip trimmedClip = AudioClip.Create(
            "TrimmedRecording",
            position,
            channels,
            recordedClip.frequency,
            false
        );
        trimmedClip.SetData(samples, 0);

        Debug.Log("[VoiceCaptureAndSend] Trimmed clip length (sec): " + trimmedClip.length);

        byte[] wavData = AudioClipToWav(trimmedClip);
        StartCoroutine(SendAudioToServer(wavData));
    }

    // ---------------- Server ----------------

    IEnumerator SendAudioToServer(byte[] wavData)
    {
        WWWForm form = new WWWForm();
        form.AddBinaryData("audio", wavData, "recording.wav", "audio/wav");

        using (UnityWebRequest www = UnityWebRequest.Post(serverUrl, form))
        {
            Debug.Log("[VoiceCaptureAndSend] Sending audio to server...");
            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("[VoiceCaptureAndSend] Error sending audio: " + www.error);
                SetRecordingLine("❌ Upload failed.");
                yield break;
            }

            string json = www.downloadHandler.text;
            Debug.Log("[VoiceCaptureAndSend] Raw server JSON: " + json);

            AgentResult result = null;
            try
            {
                result = JsonUtility.FromJson<AgentResult>(json);
            }
            catch (System.Exception e)
            {
                Debug.LogError("[VoiceCaptureAndSend] Failed to parse AI response JSON: " + e);
                SetRecordingLine("❌ JSON parse failed.");
                yield break;
            }

            if (result == null)
            {
                Debug.LogWarning("[VoiceCaptureAndSend] AgentResult is null after JSON parse.");
                SetRecordingLine("❌ Empty response.");
                yield break;
            }

            Debug.Log("[VoiceCaptureAndSend] Transcript: " + result.transcript);

            if (transcriptText != null)
                transcriptText.text = "Transcript: " + result.transcript;

            bool executedAnything = false;

            // ✅ Clean message: show per command right before execution
            if (result.commands != null && result.commands.Length > 0)
            {
                foreach (var c in result.commands)
                {
                    if (c == null) continue;

                    ShowCleanMessageForCommand(c);
                    ApplyCommand(c);
                    executedAnything = true;
                }
            }
            else if (result.command != null)
            {
                ShowCleanMessageForCommand(result.command);
                ApplyCommand(result.command);
                executedAnything = true;
            }

            if (!executedAnything)
            {
                Debug.LogWarning("[VoiceCaptureAndSend] No command(s) received.");
                ShowCleanAction("(none)");
                SetRecordingLine("⚠ No command executed.");
            }
            else
            {
                if (stateStore != null)
                    stateStore.RequestSave();
                else
                    Debug.LogWarning("[VoiceCaptureAndSend] SceneStateStore not found; changes won't persist.");

                SetRecordingLine("✅ Done");
            }
        }
    }

    void ApplyCommand(Command cmd)
    {
        if (cmd == null || string.IsNullOrEmpty(cmd.action)) return;

        if (gazeInteractor == null)
        {
            Debug.LogError("[VoiceCaptureAndSend] GazeTargetInteractor not assigned/found.");
            return;
        }

        string action = cmd.action.Trim().ToLowerInvariant();
        if (action == "no_action") return;

        Debug.Log("[VoiceCaptureAndSend] Command action: " + action);

        switch (action)
        {
            case "set_color":
                gazeInteractor.SetColorOnGazed(cmd.color);
                break;

            case "move":
                // Move is absolute world position (x,y,z). Keep as-is.
                gazeInteractor.MoveGazedToWorld(cmd.x, cmd.y, cmd.z);
                break;

            case "translate":
                {
                    // ✅ FIX: interpret as camera-relative by default + cm->m conversion
                    Vector3 deltaWorld = ToWorldDelta(cmd.dx, cmd.dy, cmd.dz);
                    gazeInteractor.TranslateGazedWorld(deltaWorld.x, deltaWorld.y, deltaWorld.z);
                    break;
                }

            case "set_scale":
                {
                    // ✅ FIX: accept partial xyz; clamp; avoid zeroing axes
                    bool hasUniform = cmd.uniform > 0f;
                    bool hasAnyXYZ = (cmd.x > 0f || cmd.y > 0f || cmd.z > 0f);

                    if (hasUniform)
                    {
                        float u = Mathf.Clamp(cmd.uniform, 0.01f, 100f);
                        gazeInteractor.SetScaleUniformOnGazed(u);
                    }
                    else if (hasAnyXYZ)
                    {
                        float sx = cmd.x > 0f ? cmd.x : 1f;
                        float sy = cmd.y > 0f ? cmd.y : 1f;
                        float sz = cmd.z > 0f ? cmd.z : 1f;

                        sx = Mathf.Clamp(sx, 0.01f, 100f);
                        sy = Mathf.Clamp(sy, 0.01f, 100f);
                        sz = Mathf.Clamp(sz, 0.01f, 100f);

                        gazeInteractor.SetScaleXYZOnGazed(sx, sy, sz);
                    }
                    else
                    {
                        Debug.LogWarning("[VoiceCaptureAndSend] set_scale missing uniform/xyz.");
                    }
                    break;
                }

            case "scale_by":
                {
                    // ✅ FIX: normalize 20 -> 1.2 ; clamp ; guard
                    float f = NormalizeScaleFactor(cmd.factor);
                    if (f > 0f) gazeInteractor.ScaleGazedBy(f);
                    else Debug.LogWarning("[VoiceCaptureAndSend] scale_by invalid factor: " + cmd.factor);
                    break;
                }

            case "place_on_floor":
                gazeInteractor.PlaceGazedOnFloor(0f);
                break;

            case "lock":
                gazeInteractor.LockCurrent(fromVoice: true);
                break;

            case "unlock":
                gazeInteractor.ClearLock();
                break;

            case "stack_on":
                gazeInteractor.StackLockedOnCurrent(0.01f);
                break;

            case "stack_on_next":
                gazeInteractor.ArmStackOnNext(0.01f);
                break;

            case "generate_model":
                {
                    if (modelSpawner == null)
                    {
                        Debug.LogError("[VoiceCaptureAndSend] RuntimeModelSpawner not found in scene.");
                        break;
                    }

                    var p = string.IsNullOrWhiteSpace(cmd.prompt) ? "simple cube" : cmd.prompt;
                    var n = string.IsNullOrWhiteSpace(cmd.name) ? "Generated_01" : cmd.name;
                    var stage = string.IsNullOrWhiteSpace(cmd.stage) ? "preview" : cmd.stage;
                    var style = string.IsNullOrWhiteSpace(cmd.art_style) ? "realistic" : cmd.art_style;

                    var pos = GetPlacementPosition();

                    modelSpawner.GenerateAndSpawn(p, n, pos, stage, style);
                    StartMeshyProgressPolling(p, n, stage, style);
                    break;
                }

            case "set_material":
                {
                    if (!string.IsNullOrWhiteSpace(cmd.material))
                        gazeInteractor.SetMaterialOnGazed(cmd.material);
                    else
                        Debug.LogWarning("[VoiceCaptureAndSend] set_material missing material name.");
                    break;
                }

            // ---------------- POSTER (NEW) ----------------
            case "create_poster":
                {
                    if (posterSpawner == null)
                    {
                        Debug.LogError("[VoiceCaptureAndSend] PosterSpawner not found in scene.");
                        break;
                    }

                    // Need a wall/surface hit to place the poster
                    if (!gazeInteractor.TryGetCurrentHit(out RaycastHit hit))
                    {
                        Debug.LogWarning("[VoiceCaptureAndSend] create_poster: no gaze hit. Look at the wall where you want the poster.");
                        break;
                    }

                    float w = cmd.width_m > 0.01f ? cmd.width_m : 1f;
                    float h = cmd.height_m > 0.01f ? cmd.height_m : 1f;

                    // If the AI/server already returned an image URL inside the command, use it.
                    if (!string.IsNullOrWhiteSpace(cmd.image_url))
                    {
                        posterSpawner.CreatePosterAtHit(hit, cmd.image_url, w, h);
                        break;
                    }

                    // Otherwise, generate image from prompt (requires server endpoint)
                    string prompt = !string.IsNullOrWhiteSpace(cmd.image_prompt) ? cmd.image_prompt : cmd.prompt;
                    if (string.IsNullOrWhiteSpace(prompt))
                        prompt = "A beautiful minimal poster";

                    StartCoroutine(GeneratePosterImageAndSpawn(prompt, hit, w, h));
                    break;
                }

            case "write_text":
                {
                    if (!gazeInteractor.TryGetCurrentHit(out RaycastHit hit))
                    {
                        Debug.LogWarning("[VoiceCaptureAndSend] write_text: no gaze hit. Look at the wall where you want the text.");
                        break;
                    }

                    string msg = string.IsNullOrWhiteSpace(cmd.text) ? "Hello World" : cmd.text;
                    float size = (cmd.font_size > 0f) ? cmd.font_size : 5f;

                    CreateTextOnWallAtHit(hit, msg, size);

                    if (stateStore != null) stateStore.RequestSave();
                    break;
                }

            case "set_wall_texture":
                {
                    if (!gazeInteractor.TryGetCurrentHit(out RaycastHit hit))
                    {
                        Debug.LogWarning("[VoiceCaptureAndSend] set_wall_texture: no gaze hit. Look at the wall.");
                        break;
                    }

                    string tPrompt = !string.IsNullOrWhiteSpace(cmd.texture_prompt)
                        ? cmd.texture_prompt
                        : (!string.IsNullOrWhiteSpace(cmd.prompt) ? cmd.prompt : "seamless tileable brick wall texture, no perspective");

                    float tileScale = (cmd.tile_scale > 0.01f) ? cmd.tile_scale : 1.5f;

                    StartCoroutine(GenerateTextureAndApplyToHit(tPrompt, hit, tileScale));
                    break;
                }

            default:
                Debug.LogWarning("[VoiceCaptureAndSend] Unknown action: " + cmd.action);
                break;
        }
    }

    private IEnumerator GenerateTextureAndApplyToHit(string texturePrompt, RaycastHit hit, float tileScale)
    {
        string endpoint = "http://localhost:8000/api/texture-image";

        var reqObj = new TextureImageRequest
        {
            prompt = texturePrompt,
            size_px = 1024
        };

        string bodyJson = JsonUtility.ToJson(reqObj);
        byte[] bodyRaw = Encoding.UTF8.GetBytes(bodyJson);

        SetGenerationLine("status=TEXTURE_GENERATION progress=0");

        using (UnityWebRequest www = new UnityWebRequest(endpoint, "POST"))
        {
            www.uploadHandler = new UploadHandlerRaw(bodyRaw);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");

            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("[VoiceCaptureAndSend] Texture generation failed: " + www.error);
                SetGenerationLine("status=TEXTURE_FAILED progress=0");
                yield break;
            }

            var resp = JsonUtility.FromJson<PosterImageResponse>(www.downloadHandler.text);
            if (resp == null || string.IsNullOrWhiteSpace(resp.image_url))
            {
                Debug.LogWarning("[VoiceCaptureAndSend] No image_url returned: " + www.downloadHandler.text);
                SetGenerationLine("status=TEXTURE_FAILED progress=0");
                yield break;
            }

            string imageUrl = resp.image_url;

            var applier = FindFirstObjectByType<WallTextureApplier>();
            if (applier != null)
            {
                SetGenerationLine("status=TEXTURE_APPLYING progress=70");
                applier.ApplyTextureUrlToHit(hit, imageUrl, tileScale);
                SetGenerationLine("status=TEXTURE_APPLIED progress=100");

                if (stateStore != null) stateStore.RequestSave();
            }
            else
            {
                Debug.LogError("[VoiceCaptureAndSend] WallTextureApplier not found in scene.");
                SetGenerationLine("status=TEXTURE_FAILED progress=0");
            }
        }
    }

    private void CreateTextOnWallAtHit(RaycastHit hit, string textContent, float _ignoredFontSizeFromServer)
    {
        Transform wall = hit.collider.transform;

        Vector3 localCenter;
        Transform boxOwner;
        Vector2 wallSize = GetWallSizeLocalRobust(hit.collider, out localCenter, out boxOwner);

        if (wallSize.x <= 0.001f || wallSize.y <= 0.001f)
        {
            Debug.LogWarning("[VoiceCaptureAndSend] write_text: Can't determine wall size. Ensure BoxCollider exists.");
            return;
        }

        float paddingPercent = 0.10f;
        float usableW = Mathf.Max(0.05f, wallSize.x * (1f - 2f * paddingPercent));
        float usableH = Mathf.Max(0.05f, wallSize.y * (1f - 2f * paddingPercent));

        GameObject textObj = new GameObject("WallText_TMP");
        textObj.transform.SetParent(wall, worldPositionStays: false);

        textObj.transform.localScale = Vector3.one * 0.02f;

        Vector3 worldCenter = boxOwner.TransformPoint(localCenter);

        float surfaceOffset = 0.02f;
        textObj.transform.position = worldCenter + hit.normal * surfaceOffset;

        var cam = Camera.main;
        if (cam != null)
        {
            Vector3 toCam = (cam.transform.position - textObj.transform.position).normalized;
            textObj.transform.rotation = Quaternion.LookRotation(toCam, Vector3.up);
        }
        else
        {
            textObj.transform.rotation = Quaternion.LookRotation(-hit.normal, Vector3.up);
        }

        TextMeshPro tmp = textObj.AddComponent<TextMeshPro>();
        tmp.text = textContent;

        tmp.alignment = TextAlignmentOptions.Center;
        tmp.verticalAlignment = VerticalAlignmentOptions.Middle;

        tmp.color = Color.white;
        tmp.outlineWidth = 0.2f;
        tmp.outlineColor = Color.black;

        tmp.rectTransform.sizeDelta = new Vector2(usableW, usableH);

        tmp.enableWordWrapping = true;
        tmp.enableAutoSizing = true;
        tmp.fontSizeMax = 80;
        tmp.fontSizeMin = 2;

        tmp.overflowMode = TextOverflowModes.Overflow;
        tmp.ForceMeshUpdate();

        Debug.Log($"[write_text] Hit={hit.collider.name}, boxOwner={boxOwner.name}, Spawned '{textContent}' at {textObj.transform.position}");
    }

    private Vector2 GetWallSizeLocalRobust(Collider col, out Vector3 localCenter, out Transform boxOwner)
    {
        localCenter = Vector3.zero;
        boxOwner = col.transform;

        var box = col.GetComponent<BoxCollider>();
        if (box == null)
            box = col.GetComponentInParent<BoxCollider>();

        if (box == null)
            return Vector2.zero;

        boxOwner = box.transform;
        localCenter = box.center;

        Vector3 s = box.size;
        float ax = Mathf.Abs(s.x), ay = Mathf.Abs(s.y), az = Mathf.Abs(s.z);

        float w, h;
        if (ax <= ay && ax <= az) { w = ay; h = az; }
        else if (ay <= ax && ay <= az) { w = ax; h = az; }
        else { w = ax; h = ay; }

        return new Vector2(w, h);
    }

    // ---------------- POSTER generation (NEW) ----------------
    private IEnumerator GeneratePosterImageAndSpawn(string prompt, RaycastHit hit, float widthMeters, float heightMeters)
    {
        string posterEndpoint = "http://localhost:8000/api/poster-image";

        int wpx = 1024;
        int hpx = 1024;

        float aspect = widthMeters / Mathf.Max(0.0001f, heightMeters);
        if (aspect > 1.2f) { wpx = 1344; hpx = 768; }
        else if (aspect < 0.8f) { wpx = 768; hpx = 1344; }

        var reqObj = new PosterImageRequest { prompt = prompt, width_px = wpx, height_px = hpx };
        string bodyJson = JsonUtility.ToJson(reqObj);
        byte[] bodyRaw = Encoding.UTF8.GetBytes(bodyJson);

        SetGenerationLine("status=POSTER_GENERATION progress=0");

        using (UnityWebRequest www = new UnityWebRequest(posterEndpoint, "POST"))
        {
            www.uploadHandler = new UploadHandlerRaw(bodyRaw);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");

            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("[VoiceCaptureAndSend] Poster generation failed: " + www.error);
                SetGenerationLine("status=POSTER_FAILED progress=0");
                yield break;
            }

            string json = www.downloadHandler.text;
            PosterImageResponse resp = null;

            try { resp = JsonUtility.FromJson<PosterImageResponse>(json); }
            catch { Debug.LogWarning("[VoiceCaptureAndSend] Could not parse poster JSON: " + json); }

            if (resp == null || string.IsNullOrWhiteSpace(resp.image_url))
            {
                Debug.LogWarning("[VoiceCaptureAndSend] Poster endpoint returned no image_url: " + json);
                SetGenerationLine("status=POSTER_FAILED progress=0");
                yield break;
            }

            SetGenerationLine("status=POSTER_READY progress=100");
            posterSpawner.CreatePosterAtHit(hit, resp.image_url, widthMeters, heightMeters);

            if (stateStore != null) stateStore.RequestSave();
        }
    }

    // ---------------- Meshy progress polling ----------------

    private void StartMeshyProgressPolling(string prompt, string name, string stage, string artStyle)
    {
        _lastProgress = -1;
        _lastGenStatus = "";
        SetGenerationLine("status=PENDING progress=0");

        if (_pollRoutine != null)
        {
            StopCoroutine(_pollRoutine);
            _pollRoutine = null;
        }

        _pollRoutine = StartCoroutine(PollTextTo3D(prompt, name, stage, artStyle));
    }

    private IEnumerator PollTextTo3D(string prompt, string name, string stage, string artStyle)
    {
        const float pollIntervalSeconds = 1.0f;

        while (true)
        {
            TextTo3DRequest reqObj = new TextTo3DRequest
            {
                prompt = prompt,
                name = name,
                stage = stage,
                art_style = artStyle
            };

            string bodyJson = JsonUtility.ToJson(reqObj);
            byte[] bodyRaw = Encoding.UTF8.GetBytes(bodyJson);

            using (UnityWebRequest www = new UnityWebRequest(textTo3dUrl, "POST"))
            {
                www.uploadHandler = new UploadHandlerRaw(bodyRaw);
                www.downloadHandler = new DownloadHandlerBuffer();
                www.SetRequestHeader("Content-Type", "application/json");

                yield return www.SendWebRequest();

                if (www.result != UnityWebRequest.Result.Success)
                {
                    SetGenerationLine("status=FAILED progress=0");
                    Debug.LogError("[VoiceCaptureAndSend] Poll failed: " + www.error);
                    yield break;
                }

                string json = www.downloadHandler.text;

                TextTo3DProgressResponse resp = null;
                try { resp = JsonUtility.FromJson<TextTo3DProgressResponse>(json); }
                catch { Debug.LogWarning("[VoiceCaptureAndSend] Could not parse progress JSON: " + json); }

                if (resp != null)
                {
                    string st = string.IsNullOrEmpty(resp.status) ? "IN_PROGRESS" : resp.status;
                    int pr = resp.progress;

                    if (pr != _lastProgress || st != _lastGenStatus)
                    {
                        _lastProgress = pr;
                        _lastGenStatus = st;
                        SetGenerationLine($"status={st} progress={pr}");
                    }

                    if (st == "SUCCEEDED" || pr >= 100)
                    {
                        SetGenerationLine("status=SUCCEEDED progress=100");
                        yield break;
                    }

                    if (st == "FAILED" || st == "ERROR")
                    {
                        SetGenerationLine("status=FAILED progress=0");
                        yield break;
                    }
                }

                yield return new WaitForSeconds(pollIntervalSeconds);
            }
        }
    }

    // ---------------- Helpers ----------------

    private Vector3 GetPlacementPosition()
    {
        var cam = Camera.main;
        if (cam == null) return Vector3.zero;
        return cam.transform.position + cam.transform.forward * 2f;
    }

    // WAV encoder 16-bit PCM
    byte[] AudioClipToWav(AudioClip clip)
    {
        float[] samples = new float[clip.samples * clip.channels];
        clip.GetData(samples, 0);

        short[] intData = new short[samples.Length];
        byte[] bytesData = new byte[samples.Length * 2];

        const float rescaleFactor = 32767f;
        for (int i = 0; i < samples.Length; i++)
        {
            intData[i] = (short)(samples[i] * rescaleFactor);
            byte[] byteArr = System.BitConverter.GetBytes(intData[i]);
            byteArr.CopyTo(bytesData, i * 2);
        }

        using (MemoryStream stream = new MemoryStream())
        {
            int headerSize = 44;
            int fileSize = bytesData.Length + headerSize - 8;
            int audioLength = bytesData.Length;

            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(fileSize);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

                writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)clip.channels);
                writer.Write(clip.frequency);
                writer.Write(clip.frequency * clip.channels * 2);
                writer.Write((short)(clip.channels * 2));
                writer.Write((short)16);

                writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                writer.Write(audioLength);
                writer.Write(bytesData);
            }

            return stream.ToArray();
        }
    }
}