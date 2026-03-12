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

    const string kApiUrl = "https://api.openai.com/v1/chat/completions";

    const string kSystemPrompt =
        "You are a Unity C# code generator for Unity 2022.3 LTS. " +
        "The user describes a behaviour they want applied to a specific GameObject. " +
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

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;

        if (string.IsNullOrWhiteSpace(openAiApiKey))
            openAiApiKey = LoadKeyFromEnv("OPENAI_API_KEY");

        if (string.IsNullOrWhiteSpace(openAiApiKey))
            Debug.LogWarning("[AICodeCommandHandler] No OpenAI API key found. Set it in Inspector or project-root .env");
        else
            Debug.Log("[AICodeCommandHandler] Ready ✅");
    }

    public void HandleCommand(string naturalLanguageCommand, GameObject target)
    {
        if (string.IsNullOrWhiteSpace(naturalLanguageCommand))
        {
            Log("⚠ Empty command.");
            return;
        }

        if (target == null)
        {
            Log("⚠ No target — look at an object first.");
            return;
        }

        if (string.IsNullOrWhiteSpace(openAiApiKey))
        {
            Log("❌ OpenAI key not found in Inspector or .env");
            return;
        }

        StartCoroutine(RequestCode(naturalLanguageCommand, target));
    }

    IEnumerator RequestCode(string userCommand, GameObject target)
    {
        Log("🧠 Asking GPT for code…");

        string sceneCtx = SceneContextBuilder.Instance != null
            ? SceneContextBuilder.Instance.Build()
            : "(SceneContextBuilder not found)";

        string systemPrompt = kSystemPrompt.Replace("{SCENE_CONTEXT}", sceneCtx);
        string payload = BuildPayload(systemPrompt, userCommand);
        byte[] body = Encoding.UTF8.GetBytes(payload);

        using (var www = new UnityWebRequest(kApiUrl, "POST"))
        {
            www.uploadHandler = new UploadHandlerRaw(body);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            www.SetRequestHeader("Authorization", "Bearer " + openAiApiKey);

            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                Log("❌ GPT failed: " + www.error + "\n" + www.downloadHandler.text);
                yield break;
            }

            string gptText = ExtractContent(www.downloadHandler.text);
            if (string.IsNullOrEmpty(gptText))
            {
                Log("❌ Empty GPT response.");
                yield break;
            }

            Debug.Log("[AICodeCommandHandler] GPT response:\n" + gptText);

            string code = RuntimeBehaviourRegistry.ParseCodeFromGPTResponse(gptText);
            if (string.IsNullOrEmpty(code))
            {
                Log("❌ No C# code block found.");
                yield break;
            }

            Log("⚙ Compiling…");

            bool ok = RuntimeBehaviourRegistry.Instance != null &&
                      RuntimeBehaviourRegistry.Instance.RegisterAndAttach(target, code);

            Log(ok ? $"✅ Attached to '{target.name}'" : "❌ Compile failed — check Console.");
        }
    }

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

        if (idx >= json.Length || json[idx] != '"')
            return null;

        idx++;

        var sb = new StringBuilder();
        while (idx < json.Length)
        {
            char ch = json[idx++];

            if (ch == '\\' && idx < json.Length)
            {
                char esc = json[idx++];
                if (esc == '"') sb.Append('"');
                else if (esc == '\\') sb.Append('\\');
                else if (esc == 'n') sb.Append('\n');
                else if (esc == 'r') sb.Append('\r');
                else if (esc == 't') sb.Append('\t');
                else if (esc == 'u' && idx + 3 < json.Length)
                {
                    string hex = json.Substring(idx, 4);
                    if (ushort.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out ushort code))
                    {
                        sb.Append((char)code);
                        idx += 4;
                    }
                    else
                    {
                        sb.Append("\\u");
                    }
                }
                else
                {
                    sb.Append(esc);
                }
            }
            else if (ch == '"')
            {
                break;
            }
            else
            {
                sb.Append(ch);
            }
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
                Debug.LogWarning("[AICodeCommandHandler] .env file not found at: " + envPath);
                return null;
            }

            foreach (var rawLine in File.ReadAllLines(envPath))
            {
                if (string.IsNullOrWhiteSpace(rawLine))
                    continue;

                string line = rawLine.Trim();

                if (line.StartsWith("#"))
                    continue;

                if (!line.StartsWith(keyName + "=", StringComparison.Ordinal))
                    continue;

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
        if (statusText != null)
            statusText.text = msg;
    }
}