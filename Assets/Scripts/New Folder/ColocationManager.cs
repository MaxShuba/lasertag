using UnityEngine;
using FishNet.Object;
using FishNet.Connection;
using FishNet.Object.Synchronizing; // For SyncVar if used later
using System.Collections; // For potential coroutines

public class ColocationManager : NetworkBehaviour
{
    // --- Singleton Pattern (Simple version) ---
    private static ColocationManager _instance;
    public static ColocationManager Instance
    {
        get
        {
            if (_instance == null)
                Debug.LogError("ColocationManager instance not found!");
            return _instance;
        }
    }

    void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Debug.LogWarning("Duplicate ColocationManager instance detected. Destroying self.");
            Destroy(gameObject);
        }
        else
        {
            _instance = this;
            // Optional: DontDestroyOnLoad(gameObject); if needed across scene loads (not needed for single scene)
        }
    }
    // --- End Singleton ---

    [Header("Colocation State")]
    // The single, authoritative world pose for the reference board
    public Pose ReferencePose { get; private set; }
    public bool HasReferencePose { get; private set; } = false;

    // Flag to prevent multiple submissions if the first one is processing
    private bool _isSubmittingReference = false;

    // --- Server-Side Logic ---

    public override void OnStartServer()
    {
        base.OnStartServer();
        // Server is ready, reset state if necessary (e.g., for restarts)
        HasReferencePose = false;
        _isSubmittingReference = false;
        Debug.Log("ColocationManager: Server started. Waiting for reference pose.");
    }

    /// <summary>
    /// Called by a client wanting to establish the reference pose.
    /// Only the first valid submission is accepted by the server.
    /// </summary>
    [ServerRpc(RequireOwnership = false)] // Any client can try, server decides
    public void CmdSubmitReferencePose(Pose clientCalculatedWorldPose, NetworkConnection sender = null)
    {
        // Only process if no reference exists yet and no one else is currently submitting
        if (!HasReferencePose && !_isSubmittingReference)
        {
            _isSubmittingReference = true; // Lock submission temporarily

            Debug.Log($"ColocationManager: Server received reference pose submission from Client {sender?.ClientId ?? -1}.");

            // Basic validation (optional: add more checks if needed)
            if (clientCalculatedWorldPose.position == Vector3.zero && clientCalculatedWorldPose.rotation == Quaternion.identity)
            {
                Debug.LogWarning($"ColocationManager: Received potentially invalid zero pose from client {sender?.ClientId ?? -1}. Ignoring.");
                _isSubmittingReference = false; // Unlock
                return;
            }

            // --- Accept this pose as the reference ---
            ReferencePose = clientCalculatedWorldPose;
            HasReferencePose = true;

            Debug.Log($"ColocationManager: Reference Pose ESTABLISHED by Client {sender?.ClientId ?? -1}. Pose: {ReferencePose.position}, {ReferencePose.rotation.eulerAngles}");

            // --- Distribute the established pose to ALL clients ---
            RpcDistributeReferencePose(ReferencePose);

            // No need to unlock _isSubmittingReference, HasReferencePose now prevents others
        }
        else if (HasReferencePose)
        {
            Debug.Log($"ColocationManager: Server ignored reference pose submission from Client {sender?.ClientId ?? -1}. Reference already exists.");
            // Optionally, send the existing reference back to this specific client if they missed the broadcast?
            // TargetRpcDistributeReferencePose(sender, ReferencePose);
        }
        else
        {
            Debug.Log($"ColocationManager: Server ignored reference pose submission from Client {sender?.ClientId ?? -1}. Another client is currently submitting.");
        }
    }

    // --- Client-Side Logic ---

    public delegate void ReferencePoseUpdatedHandler(Pose newReferencePose);
    public event ReferencePoseUpdatedHandler OnReferencePoseReceived; // Event clients can subscribe to

    /// <summary>
    /// Called by the Server on all clients to inform them of the established reference pose.
    /// BufferLast ensures late-joining clients get the pose immediately.
    /// </summary>
    [ObserversRpc(BufferLast = true)]
    public void RpcDistributeReferencePose(Pose referencePose)
    {
        if (!HasReferencePose) // Only update if we haven't received it before
        {
            Debug.Log($"ColocationManager: Client received Reference Pose from Server. Pose: {referencePose.position}, {referencePose.rotation.eulerAngles}");
        }
        else
        {
            Debug.Log($"ColocationManager: Client received updated Reference Pose (or re-broadcast).");
        }

        ReferencePose = referencePose;
        HasReferencePose = true;

        // --- Notify local systems that the reference pose is ready ---
        OnReferencePoseReceived?.Invoke(ReferencePose);
    }


    // Optional: Method to specifically send to one client if needed
    // [TargetRpc]
    // public void TargetRpcDistributeReferencePose(NetworkConnection target, Pose referencePose)
    // {
    //     RpcDistributeReferencePose(referencePose); // Reuse the logic
    // }


    // --- Public Client Method to Initiate ---

    /// <summary>
    /// Call this from your local player/UI when they successfully scan the board
    /// and want to submit it as the potential reference.
    /// </summary>
    /// <param name="locallyCalculatedPose">The world pose calculated by the local ChArUco tracking.</param>
    public void TrySubmitReferencePose(Pose locallyCalculatedPose)
    {
        if (!base.IsClient) // Must be a client to submit
        {
            Debug.LogError("ColocationManager: Cannot submit reference pose - not running as client.");
            return;
        }

        if (HasReferencePose)
        {
            Debug.Log("ColocationManager: Reference pose already established locally. No need to submit.");
            // Optionally trigger alignment immediately if local scan happened after reference was received
            OnReferencePoseReceived?.Invoke(ReferencePose);
            return;
        }

        Debug.Log("ColocationManager: Client attempting to submit its calculated pose as reference...");
        CmdSubmitReferencePose(locallyCalculatedPose); // Call the ServerRpc
    }

    /// <summary>
    /// Call this to trigger the alignment process on the local client AFTER
    /// both the local scan is complete AND the reference pose has been received.
    /// </summary>
    public void TriggerLocalAlignment()
    {
        if (!HasReferencePose)
        {
            Debug.LogWarning("ColocationManager: Cannot trigger alignment - Reference Pose not yet received.");
            return;
        }
        // The actual alignment logic should be handled by the PlayerController or a dedicated script
        // subscribing to OnReferencePoseReceived. This method is just a potential trigger point.
        Debug.Log("ColocationManager: Alignment process should be triggered now.");
        // Example: FindObjectOfType<PlayerController>()?.AttemptAlignment();
        // Or use the event: OnReferencePoseReceived?.Invoke(ReferencePose); (already done in Rpc)
    }
}