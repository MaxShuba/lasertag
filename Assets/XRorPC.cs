using UnityEngine;
using UnityEngine.XR;
using System.Collections.Generic;

// - Assets/Scripts/PCServer.cs 
// - Assets/Scripts/XRClient.cs 

public class XRorPC : MonoBehaviour
{
    void Start()
    {
        Debug.Log("XRorPC Start: Determining platform...");
        bool isXR = IsXRDevicePresent();
        if (isXR)
        {
            Debug.Log("XR Device Detected. Adding XRClient component.");
            if (GetComponent<XRClient>() == null)
            {
                gameObject.AddComponent<XRClient>();
            }
            else
            {
                Debug.LogWarning("XRClient component already exists on this GameObject.", this);
            }
        }
        else
        {
            Debug.Log("PC Platform Detected. Adding PCServer component.");
            if (GetComponent<PCServer>() == null)
            {
                gameObject.AddComponent<PCServer>();
            }
            else
            {
                Debug.LogWarning("PCServer component already exists on this GameObject.", this);
            }
        }
        //Destroy this script after it has served its purpose
        Destroy(this);
    }
    private bool IsXRDevicePresent()
    {
        var xrDisplaySubsystems = new List<XRDisplaySubsystem>();
        SubsystemManager.GetSubsystems(xrDisplaySubsystems);
        Debug.Log($"Found {xrDisplaySubsystems.Count} XR Display Subsystems.");
        foreach (var xrDisplay in xrDisplaySubsystems)
        {
            Debug.Log($" - Subsystem: {xrDisplay.subsystemDescriptor.id}, Running: {xrDisplay.running}");
            if (xrDisplay.running)
            {
                Debug.Log("Found RUNNING XR Display. Returning true.");
                return true;
            }
        }
        Debug.Log("No RUNNING XR Display found. Returning false.");
        return false;
    }
}
