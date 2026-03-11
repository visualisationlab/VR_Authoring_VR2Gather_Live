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

    [Header("Context Target Memory")]
    [Tooltip("Remember the last object you looked at when you started a voice command.")]
    public bool rememberLastTarget = true;
    [Tooltip("How long (seconds) to keep using the last remembered target.")]
    public float rememberSeconds = 120f;
    [Tooltip("If true, automatically lock the remembered target while executing a voice command.")]
    public bool autoLockRememberedTarget = true;

    private GameObject _rememberedTarget = null;
    private float _rememberedTargetTime = -999f;
    private bool _autoLockedThisRun = false;

    private AudioClip recordedClip;
    private bool isRecording = false;

    [Header("Optional systems")]
    public RuntimeModelSpawner modelSpawner;

    [Header("Posters")]
    public PosterSpawner posterSpawner;

    [Header("Persistence")]
    public SceneStateStore stateStore;

    [Header("Server")]
    public string serverUrl = "http://localhost:8000/transcribe";
    public string textTo3dUrl = "http://localhost:8000/api/text-to-3d";

    [Header("Gaze Target")]
    public GazeTargetInteractor gazeInteractor;

    [Header("UI Feedback (Optional)")]
    public TextMeshProUGUI aiIntentText;
    public TextMeshProUGUI transcriptText;
    public TextMeshProUGUI recordingStatusText;

    [Header("Keyboard Controls")]
    public KeyCode startKey = KeyCode.R;
    public KeyCode stopKey = KeyCode.T;

    [Header("Movement & Scale Interpretation")]
    public bool translateInCameraSpace = false;
    public bool autoConvertCmToMeters = false;
    public float cmMin = 2f;
    public float cmMax = 500f;
    public float scalePercentThreshold = 3f;
    public float minScaleFactor = 0.1f;
    public float maxScaleFactor = 10f;

    [Header("Poster Defaults")]
    [Tooltip("Fallback poster width in metres when the AI does not specify one.")]
    public float defaultPosterWidthM = 1f;
    [Tooltip("Fallback poster height in metres when the AI does not specify one.")]
    public float defaultPosterHeightM = 1f;

    private InputDevice rightController;
    private bool lastAPressed = false;

    // =========================
    // Wall Anchor
    // =========================
    [System.Serializable]
    public struct WallAnchor
    {
        public Transform wall;
        public Vector3 localPoint;
        public Vector3 localNormal;

        public static WallAnchor FromHit(RaycastHit hit)
        {
            var a = new WallAnchor();
            a.wall = hit.collider != null ? hit.collider.transform : null;
            if (a.wall != null)
            {
                a.localPoint = a.wall.InverseTransformPoint(hit.point);
                a.localNormal = a.wall.InverseTransformDirection(hit.normal);
            }
            return a;
        }

        public bool IsValid() => wall != null;
        public Vector3 WorldPoint() => wall != null ? wall.TransformPoint(localPoint) : Vector3.zero;
        public Vector3 WorldNormal() => wall != null ? wall.TransformDirection(localNormal).normalized : Vector3.forward;
    }

    // =========================
    // Job system
    // =========================
    private int _jobSeq = 0;

    private class Job
    {
        public int id;
        public string type;
        public float startTime;
        public string status;
        public int progress;
        public GameObject boundTarget;
        public float ElapsedSeconds => Mathf.Max(0f, Time.realtimeSinceStartup - startTime);
    }

    private readonly Dictionary<int, Job> _jobs = new Dictionary<int, Job>();
    private readonly Dictionary<int, Coroutine> _modelPollRoutines = new Dictionary<int, Coroutine>();

    // UPDATED: allow overriding startTime (so instant commands can time from "stop recording")
    private int StartJob(string type, string initialStatus, GameObject boundTarget, float startTimeOverride = -1f)
    {
        int id = ++_jobSeq;
        _jobs[id] = new Job
        {
            id = id,
            type = type,
            startTime = (startTimeOverride >= 0f) ? startTimeOverride : Time.realtimeSinceStartup,
            status = initialStatus,
            progress = 0,
            boundTarget = boundTarget
        };
        RefreshJobsUI();
        return id;
    }

    private void UpdateJob(int id, string status, int progress = -1)
    {
        if (!_jobs.TryGetValue(id, out var j)) return;
        j.status = status ?? "";
        if (progress >= 0) j.progress = progress;
        RefreshJobsUI();
    }

    private void FinishJob(int id, bool ok, string reason = "")
    {
        if (!_jobs.TryGetValue(id, out var j)) return;

        float jobStartTime = j.startTime;
        _jobs.Remove(id);

        if (_modelPollRoutines.TryGetValue(id, out var co))
        {
            if (co != null) StopCoroutine(co);
            _modelPollRoutines.Remove(id);
        }

        string targetName = j.boundTarget != null ? j.boundTarget.name : "?";
        string detail = string.IsNullOrWhiteSpace(reason) ? j.status : reason;

        _completedJobs.Add(new CompletedJob
        {
            id = id,
            type = j.type,
            targetName = targetName,
            ok = ok,
            startTime = jobStartTime,
            finishTime = Time.realtimeSinceStartup,
            detail = detail
        });

        RefreshJobsUI();
    }

    // waits one frame so "running" row is visible before completion
    // AFTER:
    private IEnumerator FinishJobNextFrame(int jobId, bool ok, string reason = "")
    {
        float capturedFinishTime = Time.realtimeSinceStartup; // ← capture BEFORE yield
        yield return null;
        if (!_jobs.TryGetValue(jobId, out var j)) yield break;

        _jobs.Remove(jobId);
        if (_modelPollRoutines.TryGetValue(jobId, out var co))
        {
            if (co != null) StopCoroutine(co);
            _modelPollRoutines.Remove(jobId);
        }

        _completedJobs.Add(new CompletedJob
        {
            id = jobId,
            type = j.type,
            targetName = j.boundTarget != null ? j.boundTarget.name : "?",
            ok = ok,
            startTime = j.startTime,            // ← stop-recording time (already set correctly)
            finishTime = capturedFinishTime,      // ← actual dispatch time, not next-frame
            detail = string.IsNullOrWhiteSpace(reason) ? j.status : reason
        });

        RefreshJobsUI();
    }

    // =========================
    // UI
    // =========================
    private string _headerLine = "";

    private class CompletedJob
    {
        public int id;
        public string type;
        public string targetName;
        public bool ok;
        public float startTime;
        public float finishTime;
        public string detail;
    }
    private readonly List<CompletedJob> _completedJobs = new List<CompletedJob>();

    [Header("UI Settings")]
    [Tooltip("How many seconds a completed job stays visible in the status panel.")]
    public float completedJobLingerSeconds = 7f;

    private void SetHeaderLine(string msg)
    {
        _headerLine = msg;
        RefreshJobsUI();
    }

    private void RefreshJobsUI()
    {
        if (recordingStatusText == null) return;

        float now = Time.realtimeSinceStartup;
        _completedJobs.RemoveAll(c => now - c.finishTime > completedJobLingerSeconds);

        var sb = new System.Text.StringBuilder();

        if (!string.IsNullOrEmpty(_headerLine))
            sb.AppendLine(_headerLine);

        bool hasRunning = _jobs.Count > 0;
        bool hasDone = _completedJobs.Count > 0;

        if (!hasRunning && !hasDone)
        {
            if (string.IsNullOrEmpty(_headerLine))
                sb.Append("Ready.");
        }
        else
        {
            if (hasRunning)
            {
                sb.AppendLine($"── {_jobs.Count} running ──");
                foreach (var kv in _jobs)
                {
                    var j = kv.Value;
                    string targetName = j.boundTarget != null ? j.boundTarget.name : "?";
                    sb.AppendLine($"⏳ #{j.id} {j.type}  [{targetName}]  {j.progress}%  ⏱ {j.ElapsedSeconds:0.0}s  ({j.status})");
                }
            }

            if (hasDone)
            {
                if (hasRunning) sb.AppendLine("── done ──");
                foreach (var c in _completedJobs)
                {
                    string icon = c.ok ? "✅" : "❌";
                    float took = c.finishTime - c.startTime;
                    string tookStr = took < 0.05f ? "<0.1s" : $"{took:0.0}s";
                    sb.AppendLine($"{icon} #{c.id} {c.type}  [{c.targetName}]  {c.detail}  ⏱{tookStr}");
                }
            }
        }

        recordingStatusText.text = sb.ToString().TrimEnd();
    }

    private float _nextTickTime = 0f;
    public float tickUiInterval = 0.25f;

    // =========================
    // JSON models
    // =========================
    [System.Serializable]
    public class Command
    {
        public string action;
        public string target;
        public string color;
        public string text;
        public float font_size;
        public string texture_prompt;
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
        public float width_m;
        public float height_m;
        public string image_url;
        public string image_prompt;
        public string behaviour_prompt;
    }

    [System.Serializable]
    public class AgentResult
    {
        public string transcript;
        public Command command;
        public Command[] commands;
    }

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
        public string image_url;
        public string message;
    }

    [System.Serializable]
    private class TextureImageRequest
    {
        public string prompt;
        public int size_px = 1024;
    }

    // =========================
    // Unity lifecycle
    // =========================
    void Start()
    {
        if (Microphone.devices.Length > 0)
        {
            microphoneDevice = Microphone.devices[0];
            Debug.Log("[VoiceCaptureAndSend] Using microphone: " + microphoneDevice);
        }
        else
        {
            Debug.LogError("[VoiceCaptureAndSend] No microphone found");
            SetHeaderLine("❌ No microphone found.");
        }

        if (gazeInteractor == null)
            gazeInteractor = FindFirstObjectByType<GazeTargetInteractor>();
        if (stateStore == null)
            stateStore = FindFirstObjectByType<SceneStateStore>();
        if (modelSpawner == null)
            modelSpawner = FindFirstObjectByType<RuntimeModelSpawner>();
        if (posterSpawner == null)
            posterSpawner = FindFirstObjectByType<PosterSpawner>();

        SetHeaderLine("Ready. Press R to record / T to stop.");
        RefreshJobsUI();

        var devices = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(XRNode.RightHand, devices);
        if (devices.Count > 0) rightController = devices[0];

        if (AICodeCommandHandler.Instance == null)
            Debug.LogWarning("[VoiceCaptureAndSend] AICodeCommandHandler not found in scene.");
    }

    void Update()
    {
        if (Input.GetKeyDown(startKey)) StartRecording();
        if (Input.GetKeyDown(stopKey)) StopRecordingAndSend();

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

        if ((_jobs.Count > 0 || _completedJobs.Count > 0) && Time.realtimeSinceStartup >= _nextTickTime)
        {
            _nextTickTime = Time.realtimeSinceStartup + tickUiInterval;
            RefreshJobsUI();
        }
    }

    // =========================
    // UI helpers
    // =========================
    private string F(float v) => v.ToString("0.00");

    private void ShowCleanAction(string msg)
    {
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

    // =========================
    // Context target memory
    // =========================
    private void RememberTarget(GameObject go)
    {
        if (!rememberLastTarget || go == null) return;
        _rememberedTarget = go;
        _rememberedTargetTime = Time.realtimeSinceStartup;
    }

    private bool TryGetRememberedTarget(out GameObject go)
    {
        go = null;
        if (!rememberLastTarget || _rememberedTarget == null) return false;
        if (Time.realtimeSinceStartup - _rememberedTargetTime > rememberSeconds) return false;
        go = _rememberedTarget;
        return true;
    }

    private bool ActionNeedsObjectTarget(string action)
    {
        switch (action)
        {
            case "set_color":
            case "move":
            case "translate":
            case "set_scale":
            case "scale_by":
            case "place_on_floor":
            case "set_material":
                return true;
            default:
                return false;
        }
    }

    private GameObject ResolveCommandTarget()
    {
        if (TryGetCurrentTargetObject(out GameObject gazed))
        {
            RememberTarget(gazed);
            return gazed;
        }
        if (TryGetRememberedTarget(out GameObject remembered))
            return remembered;
        return null;
    }

    private bool EnsureContextTargetLockedIfNeeded(GameObject targetOverride)
    {
        if (gazeInteractor == null) return false;

        if (targetOverride != null)
        {
            if (autoLockRememberedTarget)
            {
                gazeInteractor.LockSpecific(targetOverride, fromVoice: true);
                _autoLockedThisRun = true;
            }
            return true;
        }

        if (TryGetCurrentTargetObject(out GameObject gazed))
        {
            RememberTarget(gazed);
            if (autoLockRememberedTarget)
            {
                gazeInteractor.LockCurrent(fromVoice: true);
                _autoLockedThisRun = true;
            }
            return true;
        }

        return false;
    }

    // =========================
    // Movement / scale helpers
    // =========================
    private float NormalizeDistanceToMeters(float v)
    {
        if (!autoConvertCmToMeters) return v;
        float av = Mathf.Abs(v);
        if (av >= cmMin && av <= cmMax) return v * 0.01f;
        return v;
    }

    private Vector3 ToWorldDelta(float dx, float dy, float dz)
    {
        dx = NormalizeDistanceToMeters(dx);
        dy = NormalizeDistanceToMeters(dy);
        dz = NormalizeDistanceToMeters(dz);

        if (!translateInCameraSpace) return new Vector3(dx, dy, dz);

        var cam = Camera.main;
        if (cam == null) return new Vector3(dx, dy, dz);
        return cam.transform.right * dx + cam.transform.up * dy + cam.transform.forward * dz;
    }

    private float NormalizeScaleFactor(float f)
    {
        if (Mathf.Abs(f) > scalePercentThreshold)
            f = 1f + (f / 100f);
        if (f <= 0.01f) return 0f;
        return Mathf.Clamp(f, minScaleFactor, maxScaleFactor);
    }

    // =========================
    // Recording
    // =========================
    public void StartRecording()
    {
        if (isRecording) { SetHeaderLine("⚠ Already recording..."); return; }
        if (string.IsNullOrEmpty(microphoneDevice)) { SetHeaderLine("❌ No microphone selected."); return; }

        recordedClip = Microphone.Start(microphoneDevice, true, maxRecordLengthSeconds, sampleRate);
        isRecording = true;
        SetHeaderLine("🎙 Recording...");
    }

    public void StopRecordingAndSend()
    {
        if (!isRecording) { SetHeaderLine("⚠ Not recording."); return; }

        int position = Microphone.GetPosition(microphoneDevice);
        Microphone.End(microphoneDevice);
        isRecording = false;

        GameObject commandTarget = ResolveCommandTarget();
        if (commandTarget != null) RememberTarget(commandTarget);

        RaycastHit capturedHit = default;
        bool hasCommandHit = gazeInteractor != null && gazeInteractor.TryGetCurrentHit(out capturedHit);
        WallAnchor commandWallAnchor = hasCommandHit ? WallAnchor.FromHit(capturedHit) : default;

        _autoLockedThisRun = false;

        if (position <= 0 || recordedClip == null) { SetHeaderLine("⚠ No audio captured."); return; }

        int channels = recordedClip.channels;
        float[] samples = new float[position * channels];
        recordedClip.GetData(samples, 0);

        AudioClip trimmedClip = AudioClip.Create("TrimmedRecording", position, channels, recordedClip.frequency, false);
        trimmedClip.SetData(samples, 0);

        byte[] wavData = AudioClipToWav(trimmedClip);

        // UPDATED: Start a visible VOICE job immediately on stop-recording
        float requestStartTime = Time.realtimeSinceStartup;
        int voiceJobId = StartJob("voice", "UPLOADING", commandTarget, requestStartTime);

        SetHeaderLine("⏹ Processing...");
        StartCoroutine(SendAudioToServer(wavData, commandTarget, commandWallAnchor, voiceJobId, requestStartTime));
    }

    // =========================
    // Server
    // =========================
    IEnumerator SendAudioToServer(byte[] wavData, GameObject commandTarget, WallAnchor commandWallAnchor, int voiceJobId, float requestStartTime)
    {
        WWWForm form = new WWWForm();
        form.AddBinaryData("audio", wavData, "recording.wav", "audio/wav");

        UpdateJob(voiceJobId, "SENDING", 10);

        using (UnityWebRequest www = UnityWebRequest.Post(serverUrl, form))
        {
            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("[VoiceCaptureAndSend] Error: " + www.error);
                SetHeaderLine("❌ upload_failed");
                FinishJob(voiceJobId, false, "upload_failed");
                yield break;
            }

            UpdateJob(voiceJobId, "PARSING", 60);

            string json = www.downloadHandler.text;
            Debug.Log("[VoiceCaptureAndSend] Raw JSON: " + json);

            AgentResult result = null;
            try { result = JsonUtility.FromJson<AgentResult>(json); }
            catch (System.Exception e)
            {
                Debug.LogError("[VoiceCaptureAndSend] JSON parse error: " + e);
                SetHeaderLine("❌ json_parse_failed");
                FinishJob(voiceJobId, false, "json_parse_failed");
                yield break;
            }

            if (result == null)
            {
                SetHeaderLine("❌ empty_response");
                FinishJob(voiceJobId, false, "empty_response");
                yield break;
            }

            if (transcriptText != null)
                transcriptText.text = result.transcript;

            UpdateJob(voiceJobId, "EXECUTING", 85);

            bool executedAnything = false;
            bool startedAnyAsyncJob = false;

            if (result.commands != null && result.commands.Length > 0)
            {
                foreach (var c in result.commands)
                {
                    if (c == null) continue;
                    bool startedJob = ApplyCommand(c, commandTarget, commandWallAnchor, requestStartTime);
                    startedAnyAsyncJob |= startedJob;
                    executedAnything = true;
                }
            }
            else if (result.command != null)
            {
                startedAnyAsyncJob = ApplyCommand(result.command, commandTarget, commandWallAnchor, requestStartTime);
                executedAnything = true;
            }

            if (!executedAnything)
            {
                SetHeaderLine("⚠ No command executed.");
                FinishJob(voiceJobId, false, "no_command");
                yield break;
            }

            if (stateStore != null) stateStore.RequestSave();

            // VOICE job finishes once commands are dispatched
            FinishJob(voiceJobId, true, startedAnyAsyncJob ? "dispatched_async" : "done");

            if (_autoLockedThisRun && !startedAnyAsyncJob && gazeInteractor != null)
            {
                gazeInteractor.ClearLock();
                _autoLockedThisRun = false;
            }

            _headerLine = "";
            RefreshJobsUI();
        }
    }

    // =========================
    // Command dispatch
    // =========================
    // UPDATED: takes startTimeOverride so instant commands time from stop-recording
    bool ApplyCommand(Command cmd, GameObject commandTarget, WallAnchor commandWallAnchor, float startTimeOverride)
    {
        if (cmd == null || string.IsNullOrEmpty(cmd.action)) return false;
        if (gazeInteractor == null)
        {
            Debug.LogError("[VoiceCaptureAndSend] GazeTargetInteractor not assigned.");
            return false;
        }

        string action = cmd.action.Trim().ToLowerInvariant();
        if (action == "no_action") return false;

        if (ActionNeedsObjectTarget(action))
        {
            bool ok = EnsureContextTargetLockedIfNeeded(commandTarget);
            if (!ok) Debug.LogWarning("[VoiceCaptureAndSend] No target for action: " + action);
        }

        switch (action)
        {
            // ── Instant commands: StartJob uses startTimeOverride (stop-recording time)
            // and FinishJobNextFrame keeps them visible properly.

            case "set_color":
                {
                    int jobId = StartJob("set_color", $"→{cmd.color}", commandTarget, startTimeOverride);
                    gazeInteractor.SetColorOnGazed(cmd.color);
                    StartCoroutine(FinishJobNextFrame(jobId, true, $"→ {cmd.color}"));
                    return false;
                }

            case "translate":
                {
                    Vector3 deltaWorld = ToWorldDelta(cmd.dx, cmd.dy, cmd.dz);
                    int jobId = StartJob("translate", $"Δ({F(deltaWorld.x)},{F(deltaWorld.y)},{F(deltaWorld.z)})", commandTarget, startTimeOverride);
                    gazeInteractor.TranslateGazedWorld(deltaWorld.x, deltaWorld.y, deltaWorld.z);
                    StartCoroutine(FinishJobNextFrame(jobId, true));
                    return false;
                }

            case "set_scale":
                {
                    bool hasUniform = cmd.uniform > 0f;
                    bool hasAnyXYZ = (cmd.x > 0f || cmd.y > 0f || cmd.z > 0f);
                    string scaleDesc = hasUniform ? $"uniform={F(cmd.uniform)}" : $"xyz=({F(cmd.x)},{F(cmd.y)},{F(cmd.z)})";
                    int jobId = StartJob("set_scale", scaleDesc, commandTarget, startTimeOverride);

                    if (hasUniform)
                        gazeInteractor.SetScaleUniformOnGazed(Mathf.Clamp(cmd.uniform, 0.01f, 100f));
                    else if (hasAnyXYZ)
                    {
                        float sx = Mathf.Clamp(cmd.x > 0f ? cmd.x : 1f, 0.01f, 100f);
                        float sy = Mathf.Clamp(cmd.y > 0f ? cmd.y : 1f, 0.01f, 100f);
                        float sz = Mathf.Clamp(cmd.z > 0f ? cmd.z : 1f, 0.01f, 100f);
                        gazeInteractor.SetScaleXYZOnGazed(sx, sy, sz);
                    }
                    bool scaleOk = hasUniform || hasAnyXYZ;
                    StartCoroutine(FinishJobNextFrame(jobId, scaleOk, scaleOk ? "" : "no scale values"));
                    return false;
                }

            case "scale_by":
                {
                    float f = NormalizeScaleFactor(cmd.factor);
                    int jobId = StartJob("scale_by", $"×{F(f)}", commandTarget, startTimeOverride);
                    if (f > 0f) gazeInteractor.ScaleGazedBy(f);
                    StartCoroutine(FinishJobNextFrame(jobId, f > 0f, f <= 0f ? "invalid factor" : ""));
                    return false;
                }

            case "place_on_floor":
                {
                    int jobId = StartJob("place_on_floor", "→floor", commandTarget, startTimeOverride);
                    gazeInteractor.PlaceGazedOnFloor(0f);
                    StartCoroutine(FinishJobNextFrame(jobId, true));
                    return false;
                }

            case "lock":
                gazeInteractor.LockCurrent(fromVoice: true);
                return false;

            case "unlock":
                gazeInteractor.ClearLock();
                _autoLockedThisRun = false;
                return false;

            case "stack_on":
                gazeInteractor.StackLockedOnCurrent(0.01f);
                return false;

            case "stack_on_next":
                gazeInteractor.ArmStackOnNext(0.01f);
                return false;

            case "set_material":
                {
                    int jobId = StartJob("set_material", $"→{cmd.material}", commandTarget, startTimeOverride);
                    if (!string.IsNullOrWhiteSpace(cmd.material))
                        gazeInteractor.SetMaterialOnGazed(cmd.material);
                    bool matOk = !string.IsNullOrWhiteSpace(cmd.material);
                    StartCoroutine(FinishJobNextFrame(jobId, matOk, matOk ? "" : "missing material name"));
                    return false;
                }

            case "write_text":
                {
                    if (!commandWallAnchor.IsValid())
                    {
                        Debug.LogWarning("[VoiceCaptureAndSend] write_text: no wall surface captured. Look at wall before stopping.");
                        return false;
                    }
                    string wtMsg = string.IsNullOrWhiteSpace(cmd.text) ? "Hello World" : cmd.text;
                    float wtSize = (cmd.font_size > 0f) ? cmd.font_size : 5f;

                    // If you ALSO want write_text to include voice round-trip time, keep startTimeOverride.
                    // Otherwise, replace startTimeOverride with -1f.
                    int jobId = StartJob("write_text", $"\"{wtMsg}\"", commandTarget, startTimeOverride);

                    CreateTextOnWallAtAnchor(commandWallAnchor, wtMsg, wtSize);
                    if (stateStore != null) stateStore.RequestSave();
                    StartCoroutine(FinishJobNextFrame(jobId, true));
                    return false;
                }

            // ─────────────────────────────────────────────────────────────
            // ASYNC PARTS BELOW: unchanged behaviour (poster/texture/model)
            // ─────────────────────────────────────────────────────────────

            case "generate_model":
                {
                    if (modelSpawner == null) { Debug.LogError("[VoiceCaptureAndSend] RuntimeModelSpawner missing."); return false; }
                    string p = string.IsNullOrWhiteSpace(cmd.prompt) ? "simple cube" : cmd.prompt;
                    string n = string.IsNullOrWhiteSpace(cmd.name) ? "Generated_01" : cmd.name;
                    string stage = string.IsNullOrWhiteSpace(cmd.stage) ? "preview" : cmd.stage;
                    string style = string.IsNullOrWhiteSpace(cmd.art_style) ? "realistic" : cmd.art_style;

                    int jobId = StartJob("model", "PENDING", commandTarget);
                    Vector3 pos = GetPlacementPosition();
                    modelSpawner.GenerateAndSpawn(p, n, pos, stage, style);
                    Coroutine co = StartCoroutine(PollTextTo3D_Job(jobId, p, n, stage, style));
                    _modelPollRoutines[jobId] = co;
                    return true;
                }

            case "create_poster":
                {
                    if (posterSpawner == null) { Debug.LogError("[VoiceCaptureAndSend] PosterSpawner missing."); return false; }
                    if (!commandWallAnchor.IsValid())
                    {
                        Debug.LogWarning("[VoiceCaptureAndSend] create_poster: no wall surface captured. Look at wall before stopping.");
                        return false;
                    }
                    WallAnchor anchor = commandWallAnchor;

                    float w = cmd.width_m > 0.01f ? cmd.width_m : defaultPosterWidthM;
                    float h = cmd.height_m > 0.01f ? cmd.height_m : defaultPosterHeightM;

                    if (!string.IsNullOrWhiteSpace(cmd.image_url))
                    {
                        int instantId = StartJob("poster", "POSTER_READY", commandTarget);
                        posterSpawner.CreatePosterAtAnchor(anchor, cmd.image_url, w, h);
                        if (stateStore != null) stateStore.RequestSave();
                        StartCoroutine(FinishJobNextFrame(instantId, true, $"{w:0.0}x{h:0.0}m"));
                        return false;
                    }

                    string prompt = !string.IsNullOrWhiteSpace(cmd.image_prompt) ? cmd.image_prompt : cmd.prompt;
                    if (string.IsNullOrWhiteSpace(prompt)) prompt = "A beautiful minimal poster";

                    int jobId = StartJob("poster", "POSTER_GENERATION", commandTarget);
                    StartCoroutine(GeneratePosterImageAndSpawn_Job(jobId, prompt, anchor, w, h));
                    return true;
                }

            case "set_wall_texture":
                {
                    if (!commandWallAnchor.IsValid())
                    {
                        Debug.LogWarning("[VoiceCaptureAndSend] set_wall_texture: no wall surface captured. Look at wall before stopping.");
                        return false;
                    }
                    WallAnchor anchor = commandWallAnchor;
                    string tPrompt = !string.IsNullOrWhiteSpace(cmd.texture_prompt) ? cmd.texture_prompt
                        : (!string.IsNullOrWhiteSpace(cmd.prompt) ? cmd.prompt : "seamless tileable brick wall texture");
                    float tileScale = (cmd.tile_scale > 0.01f) ? cmd.tile_scale : 1.5f;

                    int jobId = StartJob("texture", "TEXTURE_GENERATION", commandTarget);
                    StartCoroutine(GenerateTextureAndApply_Job(jobId, tPrompt, anchor, tileScale));
                    return true;
                }

            case "run_code":
                {
                    GameObject runTarget = commandTarget;

                    if (runTarget == null && !string.IsNullOrWhiteSpace(cmd.target))
                        runTarget = GameObject.Find(cmd.target);

                    if (runTarget == null)
                    {
                        Debug.LogWarning("[VoiceCaptureAndSend] run_code: no target. Look at an object first.");
                        return false;
                    }

                    string prompt = !string.IsNullOrWhiteSpace(cmd.behaviour_prompt) ? cmd.behaviour_prompt
                                  : !string.IsNullOrWhiteSpace(cmd.prompt) ? cmd.prompt
                                  : "make this object rotate slowly";

                    if (AICodeCommandHandler.Instance == null)
                    {
                        Debug.LogError("[VoiceCaptureAndSend] AICodeCommandHandler is not in the scene.");
                        return false;
                    }

                    AICodeCommandHandler.Instance.HandleCommand(prompt, runTarget);
                    return true;
                }

            default:
                Debug.LogWarning("[VoiceCaptureAndSend] Unknown action: " + cmd.action);
                return false;
        }
    }

    // =========================
    // Async: Texture
    // =========================
    private IEnumerator GenerateTextureAndApply_Job(int jobId, string texturePrompt, WallAnchor anchor, float tileScale)
    {
        string endpoint = "http://localhost:8000/api/texture-image";
        var reqObj = new TextureImageRequest { prompt = texturePrompt, size_px = 1024 };
        byte[] bodyRaw = Encoding.UTF8.GetBytes(JsonUtility.ToJson(reqObj));
        UpdateJob(jobId, "TEXTURE_GENERATION", 5);

        using (UnityWebRequest www = new UnityWebRequest(endpoint, "POST"))
        {
            www.uploadHandler = new UploadHandlerRaw(bodyRaw);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                FinishJob(jobId, false, "TEXTURE_FAILED: " + www.error);
                yield break;
            }

            var resp = JsonUtility.FromJson<PosterImageResponse>(www.downloadHandler.text);
            if (resp == null || string.IsNullOrWhiteSpace(resp.image_url))
            {
                FinishJob(jobId, false, "TEXTURE_FAILED (no image_url)");
                yield break;
            }

            var applier = FindFirstObjectByType<WallTextureApplier>();
            if (applier == null) { FinishJob(jobId, false, "WallTextureApplier missing"); yield break; }

            UpdateJob(jobId, "TEXTURE_APPLYING", 70);
            applier.ApplyTextureUrlToAnchor(anchor, resp.image_url, tileScale);
            if (stateStore != null) stateStore.RequestSave();
            UpdateJob(jobId, "TEXTURE_APPLIED", 100);
            FinishJob(jobId, true);
        }
    }

    // =========================
    // Async: Poster
    // =========================
    private IEnumerator GeneratePosterImageAndSpawn_Job(int jobId, string prompt, WallAnchor anchor, float widthMeters, float heightMeters)
    {
        string posterEndpoint = "http://localhost:8000/api/poster-image";

        int wpx = 1024, hpx = 1024;
        float aspect = widthMeters / Mathf.Max(0.0001f, heightMeters);
        if (aspect > 1.2f) { wpx = 1344; hpx = 768; }
        else if (aspect < 0.8f) { wpx = 768; hpx = 1344; }

        var reqObj = new PosterImageRequest { prompt = prompt, width_px = wpx, height_px = hpx };
        byte[] bodyRaw = Encoding.UTF8.GetBytes(JsonUtility.ToJson(reqObj));
        UpdateJob(jobId, "POSTER_GENERATION", 5);

        using (UnityWebRequest www = new UnityWebRequest(posterEndpoint, "POST"))
        {
            www.uploadHandler = new UploadHandlerRaw(bodyRaw);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                FinishJob(jobId, false, "POSTER_FAILED: " + www.error);
                yield break;
            }

            PosterImageResponse resp = null;
            try { resp = JsonUtility.FromJson<PosterImageResponse>(www.downloadHandler.text); }
            catch { }

            if (resp == null || string.IsNullOrWhiteSpace(resp.image_url))
            {
                FinishJob(jobId, false, "POSTER_FAILED (no image_url)");
                yield break;
            }

            UpdateJob(jobId, "POSTER_READY", 100);
            posterSpawner.CreatePosterAtAnchor(anchor, resp.image_url, widthMeters, heightMeters);
            if (stateStore != null) stateStore.RequestSave();
            FinishJob(jobId, true, $"{widthMeters:0.0}x{heightMeters:0.0}m");
        }
    }

    // =========================
    // Async: 3D model polling
    // =========================
    private IEnumerator PollTextTo3D_Job(int jobId, string prompt, string name, string stage, string artStyle)
    {
        const float pollIntervalSeconds = 1.0f;

        while (true)
        {
            var reqObj = new TextTo3DRequest { prompt = prompt, name = name, stage = stage, art_style = artStyle };
            byte[] bodyRaw = Encoding.UTF8.GetBytes(JsonUtility.ToJson(reqObj));

            using (UnityWebRequest www = new UnityWebRequest(textTo3dUrl, "POST"))
            {
                www.uploadHandler = new UploadHandlerRaw(bodyRaw);
                www.downloadHandler = new DownloadHandlerBuffer();
                www.SetRequestHeader("Content-Type", "application/json");
                yield return www.SendWebRequest();

                if (www.result != UnityWebRequest.Result.Success)
                {
                    FinishJob(jobId, false, "poll_failed: " + www.error);
                    yield break;
                }

                TextTo3DProgressResponse resp = null;
                try { resp = JsonUtility.FromJson<TextTo3DProgressResponse>(www.downloadHandler.text); }
                catch { }

                if (resp != null)
                {
                    string st = string.IsNullOrEmpty(resp.status) ? "IN_PROGRESS" : resp.status;
                    UpdateJob(jobId, st, resp.progress);

                    if (st == "SUCCEEDED" || resp.progress >= 100)
                    {
                        UpdateJob(jobId, "SUCCEEDED", 100);
                        FinishJob(jobId, true);
                        yield break;
                    }

                    if (st == "FAILED" || st == "ERROR")
                    {
                        FinishJob(jobId, false, "status=FAILED");
                        yield break;
                    }
                }

                yield return new WaitForSeconds(pollIntervalSeconds);
            }
        }
    }

    // =========================
    // Text on wall
    // =========================
    private void CreateTextOnWallAtAnchor(WallAnchor anchor, string textContent, float fontSize)
    {
        if (!anchor.IsValid()) return;

        Vector3 worldNormal = anchor.WorldNormal();
        Collider col = anchor.wall.GetComponent<Collider>();
        if (col == null) col = anchor.wall.GetComponentInChildren<Collider>();

        Vector3 localCenter = Vector3.zero;
        Transform boxOwner = anchor.wall;
        Vector2 wallSize = new Vector2(2f, 2f);

        if (col != null)
            wallSize = GetWallSizeLocalRobust(col, out localCenter, out boxOwner);

        float usableW = Mathf.Max(0.05f, wallSize.x * 0.80f);
        float usableH = Mathf.Max(0.05f, wallSize.y * 0.80f);

        GameObject textObj = new GameObject("WallText_TMP");
        textObj.transform.SetParent(anchor.wall, worldPositionStays: false);
        textObj.transform.localScale = Vector3.one * 0.02f;

        Vector3 worldCenter = boxOwner.TransformPoint(localCenter);
        textObj.transform.position = worldCenter + worldNormal * 0.02f;

        var cam = Camera.main;
        if (cam != null)
            textObj.transform.rotation = Quaternion.LookRotation(
                (cam.transform.position - textObj.transform.position).normalized, Vector3.up);
        else
            textObj.transform.rotation = Quaternion.LookRotation(-worldNormal, Vector3.up);

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
    }

    private void CreateTextOnWallAtHit(RaycastHit hit, string textContent, float _ignoredFontSizeFromServer)
    {
        Transform wall = hit.collider.transform;
        Vector3 localCenter;
        Transform boxOwner;
        Vector2 wallSize = GetWallSizeLocalRobust(hit.collider, out localCenter, out boxOwner);

        if (wallSize.x <= 0.001f || wallSize.y <= 0.001f)
        {
            Debug.LogWarning("[VoiceCaptureAndSend] write_text: Can't determine wall size.");
            return;
        }

        float paddingPercent = 0.10f;
        float usableW = Mathf.Max(0.05f, wallSize.x * (1f - 2f * paddingPercent));
        float usableH = Mathf.Max(0.05f, wallSize.y * (1f - 2f * paddingPercent));

        GameObject textObj = new GameObject("WallText_TMP");
        textObj.transform.SetParent(wall, worldPositionStays: false);
        textObj.transform.localScale = Vector3.one * 0.02f;

        Vector3 worldCenter = boxOwner.TransformPoint(localCenter);
        textObj.transform.position = worldCenter + hit.normal * 0.02f;

        var cam = Camera.main;
        if (cam != null)
            textObj.transform.rotation = Quaternion.LookRotation(
                (cam.transform.position - textObj.transform.position).normalized, Vector3.up);
        else
            textObj.transform.rotation = Quaternion.LookRotation(-hit.normal, Vector3.up);

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
    }

    private Vector2 GetWallSizeLocalRobust(Collider col, out Vector3 localCenter, out Transform boxOwner)
    {
        localCenter = Vector3.zero;
        boxOwner = col.transform;

        var box = col.GetComponent<BoxCollider>() ?? col.GetComponentInParent<BoxCollider>();
        if (box == null) return Vector2.zero;

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

    // =========================
    // Helpers
    // =========================
    private Vector3 GetPlacementPosition()
    {
        var cam = Camera.main;
        if (cam == null) return Vector3.zero;
        return cam.transform.position + cam.transform.forward * 2f;
    }

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
            int fileSize = bytesData.Length + 44 - 8;
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
                writer.Write(bytesData.Length);
                writer.Write(bytesData);
            }
            return stream.ToArray();
        }
    }
}