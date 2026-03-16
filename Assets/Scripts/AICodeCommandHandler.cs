using System;
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public class AICodeCommandHandler : MonoBehaviour
{
    public static AICodeCommandHandler Instance { get; private set; }

    [Header("OpenAI")]
    [Tooltip("Optional: leave empty to load OPENAI_API_KEY from project-root .env")]
    public string openAiApiKey = "";
    public string model = "gpt-4o";
    public int maxTokens = 2000;
    public float temperature = 0.2f;

    [Header("UI Feedback (optional)")]
    public TMPro.TextMeshProUGUI statusText;

    [Header("Auto-Target")]
    [Tooltip("Camera used for raycast auto-detection. Defaults to Camera.main.")]
    public Camera targetCamera;
    [Tooltip("Max raycast distance for auto-target detection.")]
    public float autoTargetRayDistance = 100f;
    [Tooltip("Layer mask for auto-target raycast. Default = all layers.")]
    public LayerMask autoTargetLayers = ~0;

    /// <summary>The last object that was successfully resolved and used as a target.</summary>
    public GameObject LastAutoTarget { get; private set; }

    const string kApiUrl = "https://api.openai.com/v1/chat/completions";

    // {TARGET_NAME} and {SCENE_CONTEXT} are replaced per-request
    const string kSystemPrompt =
        "You are a Unity C# code generator for Unity 2022.3 LTS. " +
        "The user describes a behaviour they want applied to a specific GameObject. " +
        "The TARGET GameObject is named '{TARGET_NAME}'. " +
        "Write code as if it will be attached directly to that object " +
        "(use 'this.gameObject' or 'transform' to refer to it). " +
        "Reply with ONLY a single fenced ```csharp code block. Rules:\n" +
        "  - One class per response, must inherit MonoBehaviour.\n" +
        "  - Only use: UnityEngine, System, System.Collections, System.Collections.Generic.\n" +
        "  - No Editor-only APIs. No ML-Agents. No external packages.\n" +
        "  - Keep it simple and correct for Unity 2022.\n" +
        "  - Prefer transform-based movement/rotation.\n" +
        "  - Do not assume Renderer, Animator, or Rigidbody exists on the same GameObject.\n" +
        "  - If visual changes are needed, prefer GetComponentsInChildren<Renderer>().\n" +
        "  - Do NOT include any explanation outside the code block.\n\n" +
        "Current scene objects:\n{SCENE_CONTEXT}";

    // ------------------------------------------------------------------ particle intent detection

    static readonly (string[] keywords, string effectType)[] kParticleIntents =
    {
        (new[]{ "water", "fountain", "flow", "stream", "waterfall" }, "water"),
        (new[]{ "fire",  "flame",    "burn", "blaze"               }, "fire"),
        (new[]{ "smoke", "fog",      "mist", "haze"                }, "smoke"),
        (new[]{ "spark", "sparkle",  "glitter", "twinkle"          }, "sparkle"),
        (new[]{ "explosion", "explode", "blast", "boom"            }, "explosion"),
    };

    /// <summary>
    /// Returns true (and spawns the effect) if the command maps to a known particle type.
    /// Bypasses GPT entirely — fast, reliable, no compilation needed.
    /// </summary>
    bool TryHandleAsParticleEffect(string command, GameObject target)
    {
        if (ParticleEffectManager.Instance == null) return false;

        string lower = command.ToLowerInvariant();

        foreach (var (keywords, effectType) in kParticleIntents)
        {
            foreach (var kw in keywords)
            {
                if (!lower.Contains(kw)) continue;

                Vector3 spawnPos = target != null
                    ? target.transform.position
                    : Vector3.zero;

                var data = new ParticleEffectData(spawnPos, effectType)
                {
                    loop = true
                };

                // For water: orient so it flows downward
                if (effectType == "water")
                {
                    data.rotX = 0f; // ProceduralParticleGenerator sets shape rotation internally
                }

                string id = ParticleEffectManager.Instance.AddEffect(data);
                Log($"Particle effect '{effectType}' spawned (id={id[..8]}) on '{(target != null ? target.name : "world")}'.");
                return true;
            }
        }

        return false;
    }

    // ------------------------------------------------------------------ lifecycle

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;

        if (string.IsNullOrWhiteSpace(openAiApiKey))
            openAiApiKey = LoadKeyFromEnv("OPENAI_API_KEY");

        if (string.IsNullOrWhiteSpace(openAiApiKey))
            Debug.LogWarning("[AICodeCommandHandler] No OpenAI API key found.");
        else
            Debug.Log("[AICodeCommandHandler] Ready");
    }

    // ------------------------------------------------------------------ public API

    /// <summary>
    /// Main entry point — target is resolved automatically:
    ///   1. Raycast from camera centre (what the player is looking at).
    ///   2. Name match — does any scene object's name appear in the command?
    ///   3. Fallback to the last successfully resolved target.
    /// The generated script is compiled and attached to that object immediately.
    /// Particle-effect commands (fire, water, smoke, etc.) are intercepted
    /// before reaching GPT and dispatched directly to ParticleEffectManager.
    /// </summary>
    public void HandleCommand(string naturalLanguageCommand)
    {
        GameObject target = ResolveTarget(naturalLanguageCommand);
        HandleCommand(naturalLanguageCommand, target);
    }

    /// <summary>
    /// Explicit overload — supply a target directly if you already know it.
    /// </summary>
    public void HandleCommand(string naturalLanguageCommand, GameObject target)
    {
        if (string.IsNullOrWhiteSpace(naturalLanguageCommand))
        {
            Log("Empty command.");
            return;
        }

        if (target == null)
        {
            Log("No target found. Look at an object, or mention its name in the command.");
            return;
        }

        // Fast path: known particle-effect intents bypass GPT entirely.
        if (TryHandleAsParticleEffect(naturalLanguageCommand, target))
            return;

        if (string.IsNullOrWhiteSpace(openAiApiKey))
        {
            Log("OpenAI key not found in Inspector or .env");
            return;
        }

        LastAutoTarget = target;
        StartCoroutine(RequestCode(naturalLanguageCommand, target));
    }

    // ------------------------------------------------------------------ auto-target

    /// <summary>
    /// Resolves the best target GameObject using three strategies in order.
    /// </summary>
    public GameObject ResolveTarget(string command)
    {
        // 1. Raycast from camera centre
        Camera cam = targetCamera != null ? targetCamera : Camera.main;
        if (cam != null)
        {
            Ray ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            if (Physics.Raycast(ray, out RaycastHit hit, autoTargetRayDistance, autoTargetLayers))
            {
                GameObject found = hit.collider.gameObject;

                // Always walk up to the meaningful root so we never target
                // a child mesh like Mesh1.0 instead of water_fountain_01.
                // Priority: PersistableAIObject root > topmost named parent.
                var persist = found.GetComponentInParent<PersistableAIObject>();
                if (persist != null)
                {
                    found = persist.gameObject;
                }
                else
                {
                    // Walk to the topmost parent that has a meaningful name
                    // (skip objects whose name starts with "Mesh" or is very short)
                    Transform t = found.transform;
                    while (t.parent != null && t.parent.parent != null)
                        t = t.parent;
                    found = t.gameObject;
                }

                Log("Auto-target (raycast): " + found.name);
                LastAutoTarget = found;
                return found;
            }
        }

        // 2. Name match in command text
        if (!string.IsNullOrWhiteSpace(command))
        {
            GameObject byName = FindObjectByNameInCommand(command);
            if (byName != null)
            {
                Log("Auto-target (name match): " + byName.name);
                LastAutoTarget = byName;
                return byName;
            }
        }

        // 3. Fallback to last known target
        if (LastAutoTarget != null)
        {
            Log("Auto-target (fallback): " + LastAutoTarget.name);
            return LastAutoTarget;
        }

        Log("Could not resolve a target automatically.");
        return null;
    }

    /// <summary>
    /// Checks whether any scene object's name (or a child's name) appears
    /// as a substring in the command string (case-insensitive).
    /// </summary>
    static GameObject FindObjectByNameInCommand(string command)
    {
        string cmdLower = command.ToLowerInvariant();

        foreach (var root in UnityEngine.SceneManagement.SceneManager
                                .GetActiveScene().GetRootGameObjects())
        {
            if (NameAppearsInCommand(root.name, cmdLower))
                return root;

            foreach (Transform child in root.transform)
                if (NameAppearsInCommand(child.name, cmdLower))
                    return child.gameObject;
        }

        return null;
    }

    static bool NameAppearsInCommand(string objectName, string commandLower)
    {
        if (string.IsNullOrWhiteSpace(objectName) || objectName.Length < 3)
            return false;
        return commandLower.Contains(objectName.ToLowerInvariant());
    }

    // ------------------------------------------------------------------ core coroutine

    IEnumerator RequestCode(string userCommand, GameObject target)
    {
        Log("Asking GPT for code for '" + target.name + "'...");

        string sceneCtx = SceneContextBuilder.Instance != null
            ? SceneContextBuilder.Instance.Build()
            : "(SceneContextBuilder not found)";

        string systemPrompt = kSystemPrompt
            .Replace("{TARGET_NAME}", target.name)
            .Replace("{SCENE_CONTEXT}", sceneCtx);

        string payload = BuildPayload(systemPrompt, userCommand);
        byte[] body = Encoding.UTF8.GetBytes(payload);

        using (var www = new UnityWebRequest(kApiUrl, "POST"))
        {
            www.uploadHandler   = new UploadHandlerRaw(body);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            www.SetRequestHeader("Authorization", "Bearer " + openAiApiKey);

            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                Log("GPT failed: " + www.error + "\n" + www.downloadHandler.text);
                yield break;
            }

            string gptText = ExtractContent(www.downloadHandler.text);
            if (string.IsNullOrEmpty(gptText))
            {
                Log("Empty GPT response.");
                yield break;
            }

            Debug.Log("[AICodeCommandHandler] GPT response:\n" + gptText);

            string code = RuntimeBehaviourRegistry.ParseCodeFromGPTResponse(gptText);
            if (string.IsNullOrEmpty(code))
            {
                Log("No C# code block found in GPT response.");
                yield break;
            }

            Log("Compiling and attaching to '" + target.name + "'...");

            bool ok = RuntimeBehaviourRegistry.Instance != null &&
                      RuntimeBehaviourRegistry.Instance.RegisterAndAttach(target, code);

            Log(ok
                ? "Attached to '" + target.name + "' successfully."
                : "Compile failed — check Console for details.");
        }
    }

    // ------------------------------------------------------------------ helpers

    string BuildPayload(string system, string user)
    {
        string s = EscapeJson(system);
        string u = EscapeJson(user);
        string t = temperature.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return "{\"model\":\"" + model + "\",\"max_tokens\":" + maxTokens +
               ",\"temperature\":" + t +
               ",\"messages\":[{\"role\":\"system\",\"content\":\"" + s +
               "\"},{\"role\":\"user\",\"content\":\"" + u + "\"}]}";
    }

    static string ExtractContent(string json)
    {
        const string marker = "\"content\":";
        int idx = json.LastIndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;

        idx += marker.Length;
        while (idx < json.Length && (json[idx] == ' ' || json[idx] == '\n' || json[idx] == '\r'))
            idx++;

        if (idx >= json.Length || json[idx] != '"') return null;
        idx++;

        var sb = new StringBuilder();
        while (idx < json.Length)
        {
            char ch = json[idx++];
            if (ch == '\\' && idx < json.Length)
            {
                char esc = json[idx++];
                if      (esc == '"')  sb.Append('"');
                else if (esc == '\\') sb.Append('\\');
                else if (esc == 'n')  sb.Append('\n');
                else if (esc == 'r')  sb.Append('\r');
                else if (esc == 't')  sb.Append('\t');
                else if (esc == 'u' && idx + 3 < json.Length)
                {
                    string hex = json.Substring(idx, 4);
                    if (ushort.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                                        null, out ushort code))
                    { sb.Append((char)code); idx += 4; }
                    else
                    { sb.Append("\\u"); }
                }
                else sb.Append(esc);
            }
            else if (ch == '"') break;
            else sb.Append(ch);
        }

        return sb.ToString();
    }

    static string EscapeJson(string s)
    {
        if (s == null) return "";
        return s.Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
    }

    string LoadKeyFromEnv(string keyName)
    {
        try
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string envPath = Path.Combine(projectRoot, ".env");

            if (!File.Exists(envPath))
            {
                Debug.LogWarning("[AICodeCommandHandler] .env not found at: " + envPath);
                return null;
            }

            foreach (var rawLine in File.ReadAllLines(envPath))
            {
                if (string.IsNullOrWhiteSpace(rawLine)) continue;
                string line = rawLine.Trim();
                if (line.StartsWith("#")) continue;
                if (!line.StartsWith(keyName + "=", StringComparison.Ordinal)) continue;

                string value = line.Substring(keyName.Length + 1).Trim();
                if (value.StartsWith("\"") && value.EndsWith("\"") && value.Length >= 2)
                    value = value.Substring(1, value.Length - 2);
                if (value.StartsWith("'") && value.EndsWith("'") && value.Length >= 2)
                    value = value.Substring(1, value.Length - 2);

                return value;
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("[AICodeCommandHandler] Failed reading .env: " + ex);
        }

        return null;
    }

    void Log(string msg)
    {
        Debug.Log("[AICodeCommandHandler] " + msg);
        if (statusText != null) statusText.text = msg;
    }
}
