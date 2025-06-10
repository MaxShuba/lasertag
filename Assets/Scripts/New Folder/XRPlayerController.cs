using UnityEngine;
using FishNet.Object;
using FishNet.Component.Transforming; // For NetworkTransform
using OVR; // Assuming OVR namespace
using TryAR.MarkerTracking; // Add namespace for ChArUco scripts if needed
using System.Reflection;
using System;
using FishNet;
using FishNet.Managing;

public class PlayerController : NetworkBehaviour
{
    [Header("XR Rig Setup")]
    [SerializeField] private GameObject ovrCameraRigPrefab;
    [SerializeField] private Transform rigParent; // Assign the 'PlayerRigParent' transform here (if used, otherwise can be null)
    private GameObject _localRigInstance;

    [Header("Remote Visuals")]
    [SerializeField] private GameObject remoteVisualsRoot;

    [Header("Networked Visual Transforms")]
    [SerializeField] private NetworkTransform headNetworkTransform;
    [SerializeField] private NetworkTransform leftHandNetworkTransform;
    [SerializeField] private NetworkTransform rightHandNetworkTransform;

    [Header("Colocation & Alignment")]
    [SerializeField, Tooltip("Assign the 'WorldAlignmentOffset' child GameObject here.")]
    private Transform worldAlignmentOffsetTransform;

    // --- Local Player State ---
    private Transform _localCenterEyeAnchor;
    private Transform _localLeftHandAnchor;
    private Transform _localRightHandAnchor;

    // --- References to the Transforms of the Visual Objects ---
    private Transform _headVisualTransform;
    private Transform _leftHandVisualTransform;
    private Transform _rightHandVisualTransform;


    // We need access to the local tracking results
    private ChArUcoTrackingAppCoordinator _localTrackingCoordinator;
    private GameObject _localCharucoBoardAnchor;
    private ChArUcoMarkerTracking _localMarkerTracker;

    // Alignment State
    private bool _hasReceivedReferencePose = false;
    private Pose _currentReferencePose;
    private bool _isAligned = false;
    private bool _alignmentAttemptPending = false;

    #region FishNet Callbacks & Setup

    public override void OnStartClient()
    {
        base.OnStartClient();

        // Get references to the visual transforms first
        if (headNetworkTransform != null) _headVisualTransform = headNetworkTransform.transform;
        else Debug.LogError("HeadNetworkTransform is not assigned!", this);
        if (leftHandNetworkTransform != null) _leftHandVisualTransform = leftHandNetworkTransform.transform;
        else Debug.LogError("LeftHandNetworkTransform is not assigned!", this);
        if (rightHandNetworkTransform != null) _rightHandVisualTransform = rightHandNetworkTransform.transform;
        else Debug.LogError("RightHandNetworkTransform is not assigned!", this);


        if (base.IsOwner)
        {
            if (!FindSceneReferences()) return; // Exit if essential refs not found

            SetupLocalPlayer();
            SubscribeToColocationEvents();
        }
        else
        {
            SetupRemotePlayer();
        }
    }

    // Helper to find necessary scene objects for the local player
    private bool FindSceneReferences()
    {
        // Use newer API to address obsolete warning
        _localTrackingCoordinator = FindFirstObjectByType<ChArUcoTrackingAppCoordinator>();

        if (_localTrackingCoordinator == null)
        {
            Debug.LogError("LOCAL PLAYER: Could not find ChArUcoTrackingAppCoordinator in the scene!", this);
            return false;
        }

        // Safer way to access potentially private fields if Coordinator cannot be modified
        FieldInfo boardAnchorField = typeof(ChArUcoTrackingAppCoordinator).GetField("_charucoBoardWorldAnchor", BindingFlags.NonPublic | BindingFlags.Instance);
        FieldInfo markerTrackerField = typeof(ChArUcoTrackingAppCoordinator).GetField("m_charucoMarkerTracking", BindingFlags.NonPublic | BindingFlags.Instance);

        if (boardAnchorField == null || markerTrackerField == null)
        {
            Debug.LogError("LOCAL PLAYER: Could not find reflection fields for '_charucoBoardWorldAnchor' or 'm_charucoMarkerTracking' in ChArUcoTrackingAppCoordinator.", _localTrackingCoordinator);
            return false;
        }

        _localCharucoBoardAnchor = boardAnchorField.GetValue(_localTrackingCoordinator) as GameObject;
        _localMarkerTracker = markerTrackerField.GetValue(_localTrackingCoordinator) as ChArUcoMarkerTracking;

        if (_localCharucoBoardAnchor == null || _localMarkerTracker == null)
        {
            Debug.LogError("LOCAL PLAYER: Failed to get assigned values for '_charucoBoardWorldAnchor' or 'm_charucoMarkerTracking' from ChArUcoTrackingAppCoordinator instance.", _localTrackingCoordinator);
            return false;
        }
        return true;
    }

    private void SubscribeToColocationEvents()
    {
        if (ColocationManager.Instance != null)
        {
            ColocationManager.Instance.OnReferencePoseReceived += HandleReferencePoseReceived;
            if (ColocationManager.Instance.HasReferencePose) // Handle late join
            {
                HandleReferencePoseReceived(ColocationManager.Instance.ReferencePose);
            }
        }
        else
        {
            Debug.LogError("LOCAL PLAYER: ColocationManager instance not found! Cannot subscribe to reference pose updates.", this);
        }
    }

    public override void OnStopClient()
    {
        base.OnStopClient();
        if (base.IsOwner && ColocationManager.Instance != null)
        {
            ColocationManager.Instance.OnReferencePoseReceived -= HandleReferencePoseReceived;
        }
        if (base.IsOwner && _localRigInstance != null)
        {
            Destroy(_localRigInstance);
        }
    }

    private void SetupLocalPlayer()
    {
        Debug.Log($"Setting up LOCAL Player Prefab (Client {LocalConnection?.ClientId ?? -1})");

        if (worldAlignmentOffsetTransform == null) Debug.LogError("WorldAlignmentOffsetTransform is not assigned!", this);
        if (remoteVisualsRoot != null) remoteVisualsRoot.SetActive(false); // Disable visuals for local player
        else Debug.LogError("RemoteVisualsRoot is not assigned!", this);

        GameObject bootstrapRig = GameObject.FindWithTag("BootstrapRig");
        if (bootstrapRig != null) bootstrapRig.SetActive(false);
        else Debug.LogWarning("Could not find 'BootstrapRig' to disable.");

        if (ovrCameraRigPrefab != null && worldAlignmentOffsetTransform != null)
        {
            Transform parentForRig = (rigParent != null) ? rigParent : worldAlignmentOffsetTransform;
            _localRigInstance = Instantiate(ovrCameraRigPrefab, parentForRig.position, parentForRig.rotation, parentForRig);

            OVRCameraRig rigComponent = _localRigInstance.GetComponent<OVRCameraRig>();
            if (rigComponent != null)
            {
                _localCenterEyeAnchor = rigComponent.centerEyeAnchor;
                _localLeftHandAnchor = rigComponent.leftHandAnchor;
                _localRightHandAnchor = rigComponent.rightHandAnchor;
                if (_localCenterEyeAnchor == null || _localLeftHandAnchor == null || _localRightHandAnchor == null)
                    Debug.LogError("Missing tracking anchors in OVRCameraRig component!", rigComponent);
            }
            else { Debug.LogError("Instantiated OVRCameraRig missing OVRCameraRig component!", _localRigInstance); }
        }
        else { Debug.LogError("OVRCameraRig Prefab or Parent/Offset Transform not assigned!", this); }
    }

    private void SetupRemotePlayer()
    {
        Debug.Log($"Setting up REMOTE Player Prefab representation (for Client {Owner?.ClientId ?? -1})");
        if (remoteVisualsRoot != null) remoteVisualsRoot.SetActive(true); // Enable visuals for remote
        else Debug.LogError("RemoteVisualsRoot is not assigned!", this);

        Camera cam = GetComponentInChildren<Camera>(true); if (cam != null && cam.enabled) cam.enabled = false;
        AudioListener listener = GetComponentInChildren<AudioListener>(true); if (listener != null && listener.enabled) listener.enabled = false;
    }

    #endregion

    #region Colocation & Alignment Logic

    private void HandleReferencePoseReceived(Pose referencePose)
    {
        if (!base.IsOwner) return;
        Debug.Log($"LOCAL PLAYER: Received Reference Pose: {referencePose.position}");
        _currentReferencePose = referencePose;
        _hasReceivedReferencePose = true;
        AttemptAlignment();
    }

    public void SubmitLocalPoseAsReference()
    {
        if (!base.IsOwner) return;
        if (_localMarkerTracker == null || _localCharucoBoardAnchor == null) { Debug.LogError("Cannot submit pose, tracking refs missing.", this); return; }

        if (_localMarkerTracker.HasValidPoseThisFrame)
        {
            Pose localPose = new Pose(_localCharucoBoardAnchor.transform.position, _localCharucoBoardAnchor.transform.rotation);
            Debug.Log($"LOCAL PLAYER: Submitting Local Pose: {localPose.position}");
            ColocationManager.Instance?.TrySubmitReferencePose(localPose);
        }
        else { Debug.LogWarning("LOCAL PLAYER: Cannot submit pose - local tracking not valid.", this); }
    }

    private void AttemptAlignment()
    {
        if (!base.IsOwner || _isAligned || !_hasReceivedReferencePose) return;
        if (ColocationManager.Instance == null || worldAlignmentOffsetTransform == null) { Debug.LogError("Cannot align - Refs missing.", this); return; }

        if (_localMarkerTracker != null && _localMarkerTracker.HasValidPoseThisFrame && _localCharucoBoardAnchor != null)
        {
            Pose localCalculatedPose = new Pose(_localCharucoBoardAnchor.transform.position, _localCharucoBoardAnchor.transform.rotation);
            Pose inverseLocalPose = localCalculatedPose.Inverse(); // Using extension method
            Quaternion correctionRot = _currentReferencePose.rotation * inverseLocalPose.rotation;
            Vector3 correctionPos = _currentReferencePose.rotation * inverseLocalPose.position + _currentReferencePose.position;
            Pose worldCorrection = new Pose(correctionPos, correctionRot);

            worldAlignmentOffsetTransform.SetPositionAndRotation(worldCorrection.position, worldCorrection.rotation);

            _isAligned = true;
            _alignmentAttemptPending = false;
            Debug.Log($"=========== LOCAL PLAYER ALIGNED! Offset: {worldCorrection.position} | {worldCorrection.rotation.eulerAngles} ===========");
        }
        else
        {
            _alignmentAttemptPending = true;
            if (_hasReceivedReferencePose) Debug.Log("LOCAL PLAYER: Ref pose received, but local tracking not valid. Alignment pending...");
        }
    }

    #endregion

    #region Update Loop

    void Update()
    {
        if (!base.IsOwner) return; // Only run for local player

        // Try aligning if pending
        if (_alignmentAttemptPending && !_isAligned)
        {
            AttemptAlignment();
        }

        // Manually update visual transforms from local rig anchors
        if (_localCenterEyeAnchor != null && _headVisualTransform != null)
        {
            _headVisualTransform.position = _localCenterEyeAnchor.position;
            _headVisualTransform.rotation = _localCenterEyeAnchor.rotation;
        }
        if (_localLeftHandAnchor != null && _leftHandVisualTransform != null)
        {
            _leftHandVisualTransform.position = _localLeftHandAnchor.position;
            _leftHandVisualTransform.rotation = _localLeftHandAnchor.rotation;
        }
        if (_localRightHandAnchor != null && _rightHandVisualTransform != null)
        {
            _rightHandVisualTransform.position = _localRightHandAnchor.position;
            _rightHandVisualTransform.rotation = _localRightHandAnchor.rotation;
        }

        // Example Input Trigger
        // if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch)) { SubmitLocalPoseAsReference(); }
    }

    #endregion
}

// --- Pose Extension ---
public static class PoseExtensions
{
    public static Pose Inverse(this Pose pose)
    {
        Quaternion invRot = Quaternion.Inverse(pose.rotation);
        Vector3 invPos = invRot * -pose.position;
        return new Pose(invPos, invRot);
    }
}