using UnityEngine;
using FishNet.Managing;
using FishNet.Discovery; // Make sure you have the namespace for NetworkDiscovery
using FishNet.Transporting; // Required for ClientConnectionStateArgs
using FishNet.Transporting.Tugboat; // <<< ADD THIS LINE
using System.Net; // Required for IPEndPoint

[RequireComponent(typeof(NetworkManager), typeof(NetworkDiscovery))]
public class XRClient : MonoBehaviour
{
    private NetworkManager _networkManager;
    private NetworkDiscovery _networkDiscovery;
    private bool _isConnecting = false; // Flag to prevent multiple connection attempts

    void Awake()
    {
        Debug.Log("XRClient Awake: Initializing...");

        _networkManager = GetComponent<NetworkManager>();
        _networkDiscovery = GetComponent<NetworkDiscovery>();

        if (_networkManager == null)
        {
            Debug.LogError("XRClient: NetworkManager component not found on this GameObject!", this);
            enabled = false;
            return;
        }

        if (_networkDiscovery == null)
        {
            Debug.LogError("XRClient: NetworkDiscovery component not found on this GameObject!", this);
            enabled = false;
            return;
        }

        // Subscribe to discovery and client state events
        _networkDiscovery.ServerFoundCallback += HandleServerFound;
        _networkManager.ClientManager.OnClientConnectionState += HandleClientConnectionState;

        // Start searching immediately if not already connected/started
        if (!_networkManager.ClientManager.Started)
        {
            StartSearch();
        }
    }

    void OnDestroy()
    {
        // Unsubscribe
        if (_networkDiscovery != null)
        {
            _networkDiscovery.ServerFoundCallback -= HandleServerFound;
        }
        if (_networkManager != null && _networkManager.ClientManager != null)
        {
            _networkManager.ClientManager.OnClientConnectionState -= HandleClientConnectionState;
        }

        // Ensure searching stops if the component is destroyed while running
        if (_networkDiscovery != null && _networkDiscovery.IsSearching)
        {
            Debug.Log("XRClient OnDestroy: Stopping search...");
            _networkDiscovery.StopSearchingOrAdvertising();
        }
    }

    private void StartSearch()
    {
        if (_networkDiscovery != null && !_networkDiscovery.IsSearching)
        {
            Debug.Log("XRClient: Starting server search...");
            _networkDiscovery.SearchForServers();
        }
        else
        {
            Debug.Log("XRClient: Already searching or NetworkDiscovery is null.");
        }
    }

    private void HandleClientConnectionState(ClientConnectionStateArgs args)
    {
        Debug.Log($"XRClient: Connection state changed to {args.ConnectionState}");

        // If we disconnect or fail to connect, start searching again
        if (args.ConnectionState == LocalConnectionState.Stopped)
        {
            Debug.Log("XRClient: Disconnected or failed to connect.");
            _isConnecting = false; // Reset connection flag
            StartSearch(); // Start searching again
        }
        // If we successfully connect, stop searching
        else if (args.ConnectionState == LocalConnectionState.Started)
        {
            Debug.Log("XRClient: Connected successfully. Stopping server search.");
            if (_networkDiscovery.IsSearching)
            {
                _networkDiscovery.StopSearchingOrAdvertising();
            }
        }
    }

    private void HandleServerFound(IPEndPoint endPoint)
    {
        // Ignore if already trying to connect or already connected
        if (_isConnecting || _networkManager.ClientManager.Started)
        {
            // Debug.Log("XRClient: Server found, but already connecting or connected. Ignoring.");
            return;
        }

        Debug.Log($"XRClient: Server found at {endPoint.Address}:{endPoint.Port}. Attempting to connect...");

        // Stop searching once we found one
        if (_networkDiscovery.IsSearching)
        {
            _networkDiscovery.StopSearchingOrAdvertising();
        }

        // Set the address in the transport (Example for Tugboat)
        Tugboat transport = _networkManager.TransportManager.Transport as Tugboat;
        if (transport != null)
        {
            transport.SetClientAddress(endPoint.Address.ToString());
            // IMPORTANT: Use the Transport's port (e.g., 7770), NOT the discovery port from endPoint.Port
            // Assuming the server uses the default Tugboat port unless configured otherwise.
            // You might need to make the transport port configurable if it's not default.
            // transport.SetPort((ushort)endPoint.Port); // This is usually WRONG for connection
            Debug.Log($"XRClient: Set Tugboat client address to {endPoint.Address}:{transport.GetPort()}");
        }
        else
        {
            Debug.LogWarning("XRClient: Could not get Tugboat transport to set address.");
            // Connection might still work if transport handles it implicitly, but explicit is better.
        }

        // Attempt to connect
        _isConnecting = true;
        _networkManager.ClientManager.StartConnection();
    }
}
