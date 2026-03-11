using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public class AICodeCommandHandler : MonoBehaviour
{
    public static AICodeCommandHandler Instance { get; private set; }

    [Header("OpenAI — paste your key here")]
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
        "  - Do NOT include any explanation outside the code block.\n\n" +
        "Current scene objects:\n{SCENE_CONTEXT}";

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;

        if (string.IsNullOrEmpty(openAiApiKey))
            Debug.LogWarning("[AICodeCommandHandler] No API key set — paste it in the Inspector.");
        else
            Debug.Log("[AICodeCommandHandler] Ready ✅");
    }

    // ── Public API ────────────────────────────────────────────────────────────
    public void HandleCommand(string naturalLanguageCommand, GameObject target)
    {
        if (string.IsNullOrWhiteSpace(naturalLanguageCommand)) { Log("⚠ Empty command."); return; }
        if (target == null)                                     { Log("⚠ No target — look at an object first."); return; }
        if (string.IsNullOrEmpty(openAiApiKey))                 { Log("❌ Paste your OpenAI key in the Inspector."); return; }

        StartCoroutine(RequestCode(naturalLanguageCommand, target));
    }

    // ── Coroutine ─────────────────────────────────────────────────────────────
    IEnumerator RequestCode(string userCommand, GameObject target)
    {
        Log("🧠 Asking GPT for code…");

        string sceneCtx = SceneContextBuilder.Instance != null
            ? SceneContextBuilder.Instance.Build()
            : "(SceneContextBuilder not found)";

        string systemPrompt = kSystemPrompt.Replace("{SCENE_CONTEXT}", sceneCtx);
        string payload      = BuildPayload(systemPrompt, userCommand);
        byte[] body         = Encoding.UTF8.GetBytes(payload);

        using (var www = new UnityWebRequest(kApiUrl, "POST"))
        {
            www.uploadHandler   = new UploadHandlerRaw(body);
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
            if (string.IsNullOrEmpty(gptText)) { Log("❌ Empty GPT response."); yield break; }

            Debug.Log("[AICodeCommandHandler] GPT response:\n" + gptText);

            string code = RuntimeBehaviourRegistry.ParseCodeFromGPTResponse(gptText);
            if (string.IsNullOrEmpty(code)) { Log("❌ No C# code block found."); yield break; }

            Log("⚙ Compiling…");
            bool ok = RuntimeBehaviourRegistry.Instance != null &&
                      RuntimeBehaviourRegistry.Instance.RegisterAndAttach(target, code);

            Log(ok ? $"✅ Attached to '{target.name}'" : "❌ Compile failed — check Console.");
        }
    }

    // ── JSON helpers ──────────────────────────────────────────────────────────
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
        while (idx < json.Length && (json[idx] == ' ' || json[idx] == '\n' || json[idx] == '\r')) idx++;
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
                else                  sb.Append(esc);
            }
            else if (ch == '"') break;
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    static string EscapeJson(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"")
         .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");

    void Log(string msg)
    {
        Debug.Log("[AICodeCommandHandler] " + msg);
        if (statusText != null) statusText.text = msg;
    }
}
