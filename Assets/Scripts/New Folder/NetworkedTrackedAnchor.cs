using UnityEngine;
using FishNet.Object;
using FishNet.Object.Synchronizing; // Required for NetworkVariable
using FishNet.Component.Transforming; // For NetworkTransform
using FishNet.Connection; // For NetworkConnection
using FishNet.Managing.Server; // For ServerManager
using System.Collections; // For IEnumerator

/// <summary>
/// This script should be placed on the Player Prefab (or another owned object).
/// It spawns a networked representation of a locally tracked object (like a ChArUco board)
/// and synchronizes its transform based on a local target transform.
/// </summary>
public class NetworkedTrackedAnchor : NetworkBehaviour
{
    private Transform _localTargetTransform;

    [Header("Networking")]
    [SerializeField, Tooltip("Prefab with NetworkObject and NetworkTransform (Client Auth) to represent the tracked object over network.")]
    private GameObject _networkedRepresentationPrefab;

    // NetworkVariable to sync the spawned instance reference from server to client
    // Make sure read permission is Observers so the owner client can read it.
    private readonly SyncVar<NetworkObject> _spawnedInstanceNetVar = new SyncVar<NetworkObject>(new SyncTypeSettings() { ReadPermission = ReadPermission.Observers });

    // Local cache for the spawned instance once the NetworkVariable is set
    private NetworkObject _spawnedInstanceCache;
    private bool _isSpawnConfirmed = false; // Flag indicating the client knows the spawn happened

    // Called on the client when this object is initialized
    public override void OnStartClient()
    {
        base.OnStartClient();

        if (base.IsOwner)
        {
            // --- Find Local Target Transform via Singleton ---
            if (ARReferenceManager.Instance != null)
            {
                _localTargetTransform = ARReferenceManager.Instance.LocalARAnchorTransform;
                if (_localTargetTransform != null)
                {
                    Debug.Log("NetworkedTrackedAnchor: Found local target transform via ARReferenceManager.");
                }
                else
                {
                    Debug.LogError("NetworkedTrackedAnchor: ARReferenceManager exists, but its LocalARAnchorTransform is null!", this);
                    this.enabled = false; return;
                }
            }
            else
            {
                Debug.LogError("NetworkedTrackedAnchor: Could not find ARReferenceManager instance in the scene!", this);
                this.enabled = false; return; // Stop if we can't find the manager
            }
            // --- End Find Local Target ---

            // --- Validation (Prefab only) ---
            if (_networkedRepresentationPrefab == null)
            {
                Debug.LogError("NetworkedTrackedAnchor: _networkedRepresentationPrefab is not assigned!", this);
                this.enabled = false; return;
            }
            if (_networkedRepresentationPrefab.GetComponent<NetworkObject>() == null || _networkedRepresentationPrefab.GetComponent<NetworkTransform>() == null)
            {
                Debug.LogError("NetworkedTrackedAnchor: _networkedRepresentationPrefab MUST have NetworkObject and NetworkTransform components.", this);
                this.enabled = false; return;
            }
            // --- End Validation ---

            // Request the server to spawn the object
            ServerRequestSpawnAnchor();

            // Start waiting for the server to confirm the spawn via the NetworkVariable
            StartCoroutine(WaitForSpawnConfirmation());
        }
    }

    // ServerRpc: Called by the owner client, executed on the server
    [ServerRpc(RequireOwnership = false)]
    private void ServerRequestSpawnAnchor(NetworkConnection sender = null)
    {
        if (_networkedRepresentationPrefab == null)
        {
            Debug.LogError("ServerRequestSpawnAnchor: Prefab is null on the server!", this);
            return;
        }

        // 1. Instantiate the prefab locally on the server
        GameObject instanceGO = Instantiate(_networkedRepresentationPrefab);
        NetworkObject nob = instanceGO.GetComponent<NetworkObject>();

        if (nob == null)
        {
            Debug.LogError("ServerRequestSpawnAnchor: Instantiated prefab is missing NetworkObject!", this);
            Destroy(instanceGO);
            return;
        }

        // 2. Tell FishNet to spawn this instance across the network,
        //    giving ownership to the client who requested it (sender)
        base.ServerManager.Spawn(nob, sender);
        _spawnedInstanceNetVar.Value = nob; // Sync the reference back
        Debug.Log($"Server spawned anchor {nob.ObjectId} for client {sender.ClientId}");
    }

    // Coroutine run by the owner client to wait for the spawn confirmation
    private IEnumerator WaitForSpawnConfirmation()
    {
        Debug.Log("Client waiting for spawn confirmation...");
        yield return new WaitUntil(() => _spawnedInstanceNetVar.Value != null);

        _spawnedInstanceCache = _spawnedInstanceNetVar.Value;
        _isSpawnConfirmed = true;
        Debug.Log($"Client confirmed spawn of anchor {_spawnedInstanceCache.ObjectId}");

        if (_localTargetTransform != null)
        {
            UpdateNetworkedTransform();
        }
    }

    // Called when the client stops (disconnects or object is destroyed)
    public override void OnStopClient()
    {
        base.OnStopClient();

        // If we are the owner, request the server to despawn our object
        if (base.IsOwner)
        {
            // Check if we ever got a confirmed spawn
            if (_spawnedInstanceCache != null)
            {
                ServerRequestDespawn();
            }
            _spawnedInstanceCache = null; // Clear local cache regardless
            _isSpawnConfirmed = false;
        }
    }

    // ServerRpc: Called by the owner client, executed on the server
    [ServerRpc(RequireOwnership = false)]
    private void ServerRequestDespawn()
    {
        // Get the object reference from the NetworkVariable on the server
        NetworkObject nobToDespawn = _spawnedInstanceNetVar.Value;

        if (nobToDespawn != null && nobToDespawn.IsSpawned)
        {
            Debug.Log($"Server despawning anchor {nobToDespawn.ObjectId}");
            base.ServerManager.Despawn(nobToDespawn);
            _spawnedInstanceNetVar.Value = null; // Clear the variable
        }
        else
        {
            Debug.LogWarning("ServerRequestDespawn called, but NetworkVariable was null or object not spawned.", this);
            // Ensure variable is cleared if it somehow got desynced
            if (_spawnedInstanceNetVar.Value != null)
                _spawnedInstanceNetVar.Value = null;
        }
    }

    // Update is called once per frame
    void Update()
    {
        // Only the owner updates the transform based on the local target,
        // and only after the spawn has been confirmed.
        if (base.IsOwner && _isSpawnConfirmed && _spawnedInstanceCache != null && _localTargetTransform != null)
        {
            UpdateNetworkedTransform();
        }
    }

    // Helper method to copy the local transform to the networked one
    void UpdateNetworkedTransform()
    {
        // Check if the cached instance is still valid (it might get destroyed unexpectedly)
        if (_spawnedInstanceCache == null)
        {
            // This might happen if the object was destroyed but OnStopClient wasn't called yet,
            // or if the NetworkVariable got cleared before the client processed it.
            _isSpawnConfirmed = false; // Stop trying to update
            Debug.LogWarning("UpdateNetworkedTransform: _spawnedInstanceCache became null unexpectedly.");
            return;
        }

        _spawnedInstanceCache.transform.SetPositionAndRotation(
              _localTargetTransform.position,
              _localTargetTransform.rotation
          );
    }
}