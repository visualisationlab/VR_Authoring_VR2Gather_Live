// =============================================================================
// DialogFollowAgent.cs
// Attach this to LLM_Output_Confirmation_Command (the World Space Canvas).
//
// Behaviour:
//   - Always stays directly above AI_Agent (updates every frame)
//   - Always faces the player camera (billboard)
//   - Freezes between teleports — only snaps when agent moves
// =============================================================================

using UnityEngine;

public class DialogFollowAgent : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Drag AI_Agent here.")]
    public Transform agentTransform;

    [Tooltip("Drag your XR Head / Main Camera here.")]
    public Transform playerCamera;

    [Header("Position")]
    [Tooltip("Height above the agent's pivot point.")]
    public float heightAboveAgent = 1.8f;

    // ─────────────────────────────────────────────────────────────────────────
    private Vector3 _lastAgentPos;

    // ─────────────────────────────────────────────────────────────────────────

    void Start()
    {
        BindReferences();
        SnapAboveAgent();
        if (agentTransform != null)
            _lastAgentPos = agentTransform.position;
    }

    void LateUpdate()
    {
        if (agentTransform == null || playerCamera == null)
        {
            BindReferences();
            return;
        }

        // Snap whenever the agent itself has moved (agent only moves on teleport)
        if (Vector3.Distance(agentTransform.position, _lastAgentPos) > 0.01f)
        {
            SnapAboveAgent();
            _lastAgentPos = agentTransform.position;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────

    void SnapAboveAgent()
    {
        if (agentTransform == null) return;

        // Place directly above agent
        transform.position = agentTransform.position + Vector3.up * heightAboveAgent;

        // Billboard: face the player camera horizontally
        if (playerCamera != null)
        {
            Vector3 lookDir = transform.position - playerCamera.position;
            lookDir.y = 0f;
            if (lookDir.sqrMagnitude > 0.001f)
                transform.rotation = Quaternion.LookRotation(lookDir, Vector3.up);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────

    void BindReferences()
    {
        // ── Agent ─────────────────────────────────────────────────────────
        if (agentTransform == null)
        {
            var agentGO = GameObject.Find("AI_Agent");
            if (agentGO != null)
            {
                agentTransform = agentGO.transform;
                _lastAgentPos  = agentTransform.position;
                Debug.Log("[DialogFollowAgent] Auto-bound to AI_Agent.");
            }
            else
            {
                Debug.LogWarning("[DialogFollowAgent] AI_Agent not found — drag it into the Inspector.");
            }
        }

        // ── Camera — same logic as HeadHudFollow ─────────────────────────
        if (playerCamera == null)
        {
            var cam = Camera.main != null ? Camera.main : FindFirstObjectByType<Camera>();
            if (cam != null)
            {
                playerCamera = cam.transform;
                Debug.Log("[DialogFollowAgent] Camera bound to: " + playerCamera.name);
            }
            else
            {
                Debug.LogWarning("[DialogFollowAgent] No camera found. Ensure there is a Camera in the scene.");
            }
        }
    }
}


