// --- START OF FILE ChArUcoTrackingAppCoordinator.cs ---
// Demonstration of how to properly co-locate players,
// ensuring the first user sees the board at (0,0,0),
// and other users align to it.
//
// Requirements:
//  - A "Root" GameObject in the scene for each user (top-level).
//  - m_cameraAnchor points to the user's HMD camera transform.

using System;
using System.Collections;
using System.Collections.Generic;
using Meta.XR.Samples;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.UI;
using PassthroughCameraSamples;

// --- FishNet imports ---
using FishNet.Object;
using FishNet.Connection;
using FishNet.Object.Synchronizing;

namespace TryAR.MarkerTracking
{
    /// <summary>
    /// Holds both the board's final world pose AND the camera (HMD) pose at the same time.
    /// This is what we'll send to the server so it can unify them.
    /// </summary>
    [Serializable]
    public struct BoardAndCameraPose
    {
        public Vector3 boardPos;
        public Quaternion boardRot;
        public Vector3 cameraPos;
        public Quaternion cameraRot;
    }

    [MetaCodeSample("PassthroughCameraApiSamples-MarkerTracking")]
    public class ChArUcoTrackingAppCoordinator : NetworkBehaviour
    {
        [Header("Camera Setup")]
        [SerializeField] private WebCamTextureManager m_webCamTextureManager;
        private PassthroughCameraEye CameraEye => m_webCamTextureManager.Eye;
        private Vector2Int CameraResolution => m_webCamTextureManager.RequestedResolution;

        [SerializeField, Tooltip("Anchor transform matching the headset's camera pose.")]
        private Transform m_cameraAnchor;  // The user's HMD in world space

        [Header("UI Setup")]
        [SerializeField] private Canvas m_cameraCanvas;
        [SerializeField] private RawImage m_resultRawImage;
        [SerializeField] private float m_canvasDistance = 1f;
        [SerializeField, Tooltip("Optional: Text to display tracking status.")]
        private Text m_statusText;

        [Header("ChArUco Tracking")]
        [SerializeField, Tooltip("Handles ChArUco detection and pose estimation.")]
        private ChArUcoMarkerTracking m_charucoMarkerTracking;

        [SerializeField, Tooltip("The GameObject that represents the detected board's pose.")]
        private GameObject _charucoBoardWorldAnchor;

        private bool m_showCameraCanvas = true;       // Whether the passthrough canvas is shown
        private Texture2D m_resultTexture;            // For displaying debug
        private bool m_anchorShouldBeVisible = false; // Visibility of the board anchor

        // -------------- Networking for Co-Location ---------------
        private bool _scanningActive = true;
        private bool _hasAuthoritativePose = false; // Whether we have a stored "master" board/cam

        // We'll store the "master" board as (0,0,0) plus identity rotation.
        // i.e. after the first user is snapped, we treat the official board as origin.
        // The first user is offset so their board becomes (0,0,0).
        // Then others align to that.

        // Unity Lifecycle
        private IEnumerator Start()
        {
            if (m_webCamTextureManager == null || m_charucoMarkerTracking == null ||
                _charucoBoardWorldAnchor == null || m_cameraAnchor == null)
            {
                Debug.LogError($"[{nameof(ChArUcoTrackingAppCoordinator)}] Missing required references.");
                enabled = false;
                yield break;
            }
            if (m_cameraCanvas == null || m_resultRawImage == null)
            {
                Debug.LogWarning($"[{nameof(ChArUcoTrackingAppCoordinator)}]: UI components not assigned.");
            }

            UpdateStatus("Waiting for camera permission...");
            yield return WaitForCameraPermission();

            UpdateStatus("Initializing Camera...");
            yield return InitializeCamera();

            if (!m_webCamTextureManager.enabled
                || m_webCamTextureManager.WebCamTexture == null
                || !m_webCamTextureManager.WebCamTexture.isPlaying)
            {
                Debug.LogError("[AppCoord] Camera init failed. Aborting.");
                UpdateStatus("Error: Camera Failed");
                enabled = false;
                yield break;
            }

            UpdateStatus("Initializing ChArUco Tracking...");
            InitializeMarkerTracking();

            if (!m_charucoMarkerTracking.IsInitialized)
            {
                Debug.LogError("[AppCoord] ChArUco init failed. Aborting.");
                UpdateStatus("Error: Tracking Failed");
                enabled = false;
                yield break;
            }

            // Setup UI
            if (m_cameraCanvas != null)
            {
                ScaleCameraCanvas();
                m_cameraCanvas.gameObject.SetActive(m_showCameraCanvas);
            }

            if (m_resultRawImage != null && m_resultTexture != null)
            {
                m_resultRawImage.texture = m_resultTexture;
            }

            // Show/hide board anchor based on whether the canvas is visible
            SetAnchorVisibility(!m_showCameraCanvas);

            UpdateStatus("Ready.");
            Debug.Log("[AppCoord] Start done.");
        }

        private IEnumerator WaitForCameraPermission()
        {
            var perms = FindAnyObjectByType<PassthroughCameraPermissions>();
            while (perms != null && PassthroughCameraPermissions.HasCameraPermission != true)
            {
                yield return null;
            }
            Debug.Log("[AppCoord] Camera permission granted or skipping check.");
        }

        private IEnumerator InitializeCamera()
        {
            Debug.Log("[AppCoord] Initializing camera...");
            m_webCamTextureManager.enabled = false;

            // Attempt to use native resolution
            var intrinsics = PassthroughCameraUtils.GetCameraIntrinsics(CameraEye);
            if (intrinsics.Resolution.x == 0 || intrinsics.Resolution.y == 0)
            {
                Debug.LogError("[AppCoord] No valid camera intrinsics. Aborting.");
                enabled = false;
                yield break;
            }
            m_webCamTextureManager.RequestedResolution = intrinsics.Resolution;
            m_webCamTextureManager.enabled = true;

            float waitStart = Time.time;
            yield return new WaitUntil(() =>
                m_webCamTextureManager.WebCamTexture != null
                && m_webCamTextureManager.WebCamTexture.isPlaying);

            if (m_webCamTextureManager.WebCamTexture == null
                || !m_webCamTextureManager.WebCamTexture.isPlaying)
            {
                Debug.LogError("[AppCoord] WebCamTexture not playing after wait.");
                m_webCamTextureManager.enabled = false;
                enabled = false;
            }
            else
            {
                Debug.Log($"[AppCoord] Camera init after {Time.time - waitStart:F2}s. " +
                          $"Tex={m_webCamTextureManager.WebCamTexture.width}x{m_webCamTextureManager.WebCamTexture.height}");
            }
        }

        private void InitializeMarkerTracking()
        {
            if (m_webCamTextureManager.WebCamTexture == null)
                return;

            int width = m_webCamTextureManager.WebCamTexture.width;
            int height = m_webCamTextureManager.WebCamTexture.height;

            var intrinsics = PassthroughCameraUtils.GetCameraIntrinsics(CameraEye);
            m_charucoMarkerTracking.Initialize(width, height,
                intrinsics.PrincipalPoint.x, intrinsics.PrincipalPoint.y,
                intrinsics.FocalLength.x, intrinsics.FocalLength.y);

            int procW = width / m_charucoMarkerTracking.ResolutionDivider;
            int procH = height / m_charucoMarkerTracking.ResolutionDivider;
            if (m_resultTexture != null) Destroy(m_resultTexture);
            m_resultTexture = new Texture2D(procW, procH, TextureFormat.RGB24, false);
        }

        private void Update()
        {
            if (!m_webCamTextureManager.enabled
                || m_webCamTextureManager.WebCamTexture == null
                || !m_webCamTextureManager.WebCamTexture.isPlaying)
            {
                return;
            }
            if (!m_charucoMarkerTracking.IsInitialized)
                return;

            HandleVisualizationToggle();
            UpdateCameraPoseAnchor();

            // Detect and update board pose
            m_charucoMarkerTracking.DetectMarkers(m_webCamTextureManager.WebCamTexture, m_resultTexture);
            m_charucoMarkerTracking.EstimateWorldPose(_charucoBoardWorldAnchor, m_cameraAnchor);

            UpdateStatus(m_charucoMarkerTracking.HasValidPoseThisFrame ? "Tracking Board" : "Searching for Board...");

            if (!m_showCameraCanvas)
                m_anchorShouldBeVisible = m_charucoMarkerTracking.HasValidPoseThisFrame;
            SetAnchorVisibility(m_anchorShouldBeVisible);

            if (m_showCameraCanvas && m_cameraCanvas != null)
            {
                UpdateCameraCanvasPose();
            }

            // Press X to finalize
            if (_scanningActive && OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.LTouch))
            {
                Debug.Log("[AppCoord] X pressed -> finalize scanning & send pose to server.");
                _scanningActive = false;
                m_charucoMarkerTracking.enabled = false; // optionally stop scanning

                // 1) Board pose
                Vector3 boardPos = _charucoBoardWorldAnchor.transform.position;
                Quaternion boardRot = _charucoBoardWorldAnchor.transform.rotation;

                // 2) Camera (HMD) pose
                Vector3 camPos = m_cameraAnchor.position;
                Quaternion camRot = m_cameraAnchor.rotation;

                // Pack them:
                BoardAndCameraPose data;
                data.boardPos = boardPos;
                data.boardRot = boardRot;
                data.cameraPos = camPos;
                data.cameraRot = camRot;

                SubmitBoardAndCameraPoseServerRpc(data);
            }
        }

        private void UpdateStatus(string message)
        {
            if (m_statusText != null && m_statusText.text != message)
                m_statusText.text = message;
        }

        private void HandleVisualizationToggle()
        {
            if (OVRInput.GetDown(OVRInput.Button.One) || Input.GetKeyDown(KeyCode.Space))
            {
                m_showCameraCanvas = !m_showCameraCanvas;
                if (m_cameraCanvas != null)
                    m_cameraCanvas.gameObject.SetActive(m_showCameraCanvas);

                if (m_showCameraCanvas)
                {
                    m_anchorShouldBeVisible = false;
                    SetAnchorVisibility(false);
                }
                else
                {
                    Debug.Log("[AppCoord] Canvas hidden -> anchor depends on pose.");
                }
            }
        }

        private void SetAnchorVisibility(bool isVisible)
        {
            if (_charucoBoardWorldAnchor != null && _charucoBoardWorldAnchor.activeSelf != isVisible)
                _charucoBoardWorldAnchor.SetActive(isVisible);
        }

        private void UpdateCameraPoseAnchor()
        {
            var camPose = PassthroughCameraUtils.GetCameraPoseInWorld(CameraEye);
            m_cameraAnchor.SetPositionAndRotation(camPose.position, camPose.rotation);
        }

        private void UpdateCameraCanvasPose()
        {
            if (m_cameraCanvas != null)
            {
                m_cameraCanvas.transform.position = m_cameraAnchor.position + m_cameraAnchor.forward * m_canvasDistance;
                m_cameraCanvas.transform.rotation = m_cameraAnchor.rotation;
            }
        }

        private void ScaleCameraCanvas()
        {
            if (m_cameraCanvas == null) return;
            if (CameraResolution.x == 0) return;

            var rtf = m_cameraCanvas.GetComponent<RectTransform>();
            if (rtf == null) return;

            var intr = PassthroughCameraUtils.GetCameraIntrinsics(CameraEye);
            if (intr.FocalLength.x == 0) return;

            float fovX = 2.0f * Mathf.Atan(intr.Resolution.x / (2.0f * intr.FocalLength.x));
            float widthAtDist = 2.0f * m_canvasDistance * Mathf.Tan(fovX / 2.0f);

            float curWidth = rtf.sizeDelta.x;
            if (curWidth <= 0.001f) curWidth = 1f;

            float scale = widthAtDist / curWidth;
            rtf.localScale = new Vector3(scale, scale, scale);
        }

        void OnDestroy()
        {
            if (m_resultTexture != null)
            {
                Destroy(m_resultTexture);
                m_resultTexture = null;
            }
        }

        // -------------------------------------------------------
        //         NETWORKING: Board+Camera -> Server
        // -------------------------------------------------------

        [ServerRpc(RequireOwnership = false)]
        private void SubmitBoardAndCameraPoseServerRpc(BoardAndCameraPose data, NetworkConnection sender = null)
        {
            Debug.Log($"[Server] Received Board+Cam from client={sender.ClientId}:\n" +
                      $"  Board=pos:{data.boardPos},rot:{data.boardRot.eulerAngles}\n" +
                      $"  Cam=pos:{data.cameraPos},rot:{data.cameraRot.eulerAngles}");

            if (!_hasAuthoritativePose)
            {
                // This is the first user => authoritative
                _hasAuthoritativePose = true;

                // We want the board at (0,0,0) with rotation=identity in the overall scene.
                // So the user is offset so that the board moves from (data.boardPos, data.boardRot) to (0,0,0).
                Quaternion rotOffset = Quaternion.Inverse(data.boardRot);
                Vector3 posOffset = -(rotOffset * data.boardPos);

                Debug.Log("[Server] This user is now authoritative. Snapping them so board => (0,0,0).");
                ApplyOffsetTargetRpc(sender, posOffset, rotOffset);

                // Now in the official coordinate system, we consider the board at (0,0,0).
                // If you want, you can store the camera as well if needed, but here we just know
                // "board is zeroed out".
            }
            else
            {
                // We have an authoritative user who made the board => (0,0,0).
                // So for new user, we compute offset from (0,0,0) minus new board.
                Quaternion rotOffset = Quaternion.Inverse(data.boardRot);
                Vector3 posOffset = -(rotOffset * data.boardPos);
                Debug.Log("[Server] Another user. Sending offset so their board => (0,0,0).");
                ApplyOffsetTargetRpc(sender, posOffset, rotOffset);
            }
        }

        [TargetRpc]
        private void ApplyOffsetTargetRpc(NetworkConnection targetConn, Vector3 posOffset, Quaternion rotOffset)
        {
            // On client, shift "Root" so board => (0,0,0).
            Debug.Log($"[Client] offset => pos:{posOffset}, rot:{rotOffset.eulerAngles}");

            GameObject root = GameObject.Find("Root");
            if (root == null)
            {
                Debug.LogWarning("[Client] Root not found!");
                return;
            }

            // Shift root in world space with full transform
            root.transform.SetPositionAndRotation(
                rotOffset * root.transform.position + posOffset,
                rotOffset * root.transform.rotation);
            Debug.Log($"[Client] Root after offset => pos={root.transform.position}, rot={root.transform.rotation.eulerAngles}");
        }
    }
}
// --- END OF FILE ChArUcoTrackingAppCoordinator.cs ---
