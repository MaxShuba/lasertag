using UnityEngine;
using FishNet.Managing;
using FishNet.Discovery; // Make sure you have the namespace for NetworkDiscovery
using FishNet.Transporting; // Required for LocalConnectionState
using FishNet.Connection; // <<< ADD THIS for NetworkConnection

[RequireComponent(typeof(NetworkManager), typeof(NetworkDiscovery))]
public class PCServer : MonoBehaviour
{
    private NetworkManager _networkManager;
    private NetworkDiscovery _networkDiscovery;

    void Awake()
    {
        Debug.Log("PCServer Awake: Initializing...");

        _networkManager = GetComponent<NetworkManager>();
        _networkDiscovery = GetComponent<NetworkDiscovery>();


        if (_networkManager == null)
        {
            Debug.LogError("PCServer: NetworkManager component not found on this GameObject!", this);
            enabled = false;
            return;
        }

        if (_networkDiscovery == null)
        {
            Debug.LogError("PCServer: NetworkDiscovery component not found on this GameObject!", this);
            enabled = false;
            return;
        }

        // Optional: Subscribe to know when the server actually starts, then advertise.
        // This is slightly more robust than just calling AdvertiseServer immediately after StartConnection.
        _networkManager.ServerManager.OnServerConnectionState += HandleServerConnectionState;

        // Subscribe to remote client state changes
        _networkManager.ServerManager.OnRemoteConnectionState += HandleRemoteConnectionState;

        // Start the server
        Debug.Log("PCServer: Starting FishNet Server...");
        _networkManager.ServerManager.StartConnection();
    }

    void OnDestroy()
    {
        // Unsubscribe to prevent memory leaks
        if (_networkManager != null && _networkManager.ServerManager != null)
        {
            _networkManager.ServerManager.OnServerConnectionState -= HandleServerConnectionState;
            _networkManager.ServerManager.OnRemoteConnectionState -= HandleRemoteConnectionState;
        }

        // Ensure advertising stops if the server is stopped while this component is active
        if (_networkDiscovery != null && _networkDiscovery.IsAdvertising)
        {
            Debug.Log("PCServer OnDestroy: Stopping advertising...");
            _networkDiscovery.StopSearchingOrAdvertising();
        }
    }

    private void HandleServerConnectionState(ServerConnectionStateArgs args)
    {
        if (args.ConnectionState == LocalConnectionState.Started)
        {
            Debug.Log("PCServer: Server successfully started. Starting advertising...");
            _networkDiscovery.AdvertiseServer();
        }
        else if (args.ConnectionState == LocalConnectionState.Stopped)
        {
            Debug.Log("PCServer: Server stopped. Stopping advertising...");
            if (_networkDiscovery.IsAdvertising)
            {
                _networkDiscovery.StopSearchingOrAdvertising();
            }
        }
    }

    // Add this method to log client connects/disconnects on the server
    private void HandleRemoteConnectionState(NetworkConnection conn, RemoteConnectionStateArgs args)
    {
        if (args.ConnectionState == RemoteConnectionState.Started)
        {
            // Get the address directly from the transport using the connection ID
            string clientAddress = "Unknown"; // Default value
            if (_networkManager != null && _networkManager.TransportManager != null && _networkManager.TransportManager.Transport != null)
            {
                // Use the transport's method to get the address for the client ID
                clientAddress = _networkManager.TransportManager.Transport.GetConnectionAddress(conn.ClientId);
            }
            Debug.Log($"[Server] Client connected! ID: {conn.ClientId}, Address: {clientAddress}");
        }
        else if (args.ConnectionState == RemoteConnectionState.Stopped)
        {
            // The reason for stopping isn't directly available in args here,
            // but seeing this log immediately after connection is a big clue.
            Debug.LogWarning($"[Server] Client disconnected! ID: {conn.ClientId}");
        }
    }
}