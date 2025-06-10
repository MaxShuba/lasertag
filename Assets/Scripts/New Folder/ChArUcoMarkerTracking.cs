// --- START OF FILE ChArUcoMarkerTracking.cs ---

using OpenCVForUnity.Calib3dModule;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.ObjdetectModule;
using OpenCVForUnity.UnityUtils;
using OpenCVForUnity.UnityUtils.Helper;
using System;
using System.Collections.Generic;
using System.Text; // Added for StringBuilder
using UnityEngine;
using UnityEngine.Assertions;

using PoseData = ColocationARUtils.PoseData; // Alias remains

namespace TryAR.MarkerTracking
{
    /// <summary>
    /// ChArUco marker detection and tracking component.
    /// Handles detection of ChArUco boards in camera frames and provides raw (no smoothing) pose estimation.
    /// </summary>
    public class ChArUcoMarkerTracking : MonoBehaviour
    {
        [SerializeField] private ArUcoDictionary _dictionaryId = ArUcoDictionary.DICT_4X4_50;

        [Header("Board Physical Dimensions (Meters!)")]
        [Tooltip("Measure precisely! The length of the SQUARE sides of the ArUco markers in meters.")]
        [SerializeField] private float _markerLength = 0.03f;
        [Tooltip("Measure precisely! The length of the SQUARE sides of the chessboard squares in meters.")]
        [SerializeField] private float _squareLength = 0.05f;

        [Header("Board Layout")]
        [Tooltip("Number of chessboard squares in the X direction.")]
        [SerializeField] private int _squaresX = 5;
        [Tooltip("Number of chessboard squares in the Y direction.")]
        [SerializeField] private int _squaresY = 4;

        [Header("Detection Parameters")]
        [Tooltip("Minimum number of detected ChArUco corners required to estimate pose.")]
        [SerializeField] private int _minDetectedCornersForPose = 4;

        [Header("Resolution")]
        [Tooltip("Division factor for input image resolution. 1 = Full resolution, >1 = Lower resolution.")]
        [SerializeField] private int _resolutionDivider = 1;

        public int ResolutionDivider => _resolutionDivider;

        // --- Internal OpenCV Variables ---
        private Mat _processingRgbMat;
        private Mat _fullResolutionMat;
        private Mat _scaledResolutionMat;

        private Mat _cameraIntrinsicMatrix;
        private MatOfDouble _cameraDistortionCoeffs;

        private Mat _detectedMarkerIds;
        private List<Mat> _detectedMarkerCorners;
        private List<Mat> _rejectedMarkerCandidates;
        private Dictionary _markerDictionary;
        private ArucoDetector _arucoDetector;

        private Mat _charucoCorners;
        private Mat _charucoIds;
        private CharucoBoard _charucoBoard;
        private CharucoDetector _charucoDetector;

        private bool _isInitialized = false;
        public bool IsInitialized => _isInitialized;

        /// <summary>
        /// Flag indicating if a valid pose was estimated in the current frame.
        /// </summary>
        public bool HasValidPoseThisFrame { get; private set; } = false;

        public void Initialize(int imageWidth, int imageHeight, float cx, float cy, float fx, float fy)
        {
            Assert.IsTrue(_markerLength > 0, $"{nameof(_markerLength)} must be positive.");
            Assert.IsTrue(_squareLength > 0, $"{nameof(_squareLength)} must be positive.");
            Assert.IsTrue(_squaresX > 0, $"{nameof(_squaresX)} must be positive.");
            Assert.IsTrue(_squaresY > 0, $"{nameof(_squaresY)} must be positive.");
            Assert.IsTrue(_resolutionDivider >= 1, $"{nameof(_resolutionDivider)} must be 1 or greater.");
            Assert.IsTrue(_minDetectedCornersForPose >= 3, $"{nameof(_minDetectedCornersForPose)} should be at least 3 or 4 for stability.");

            InitializeOpenCVComponents(imageWidth, imageHeight, cx, cy, fx, fy);
            _isInitialized = true;

            Debug.Log($"[ChArUco] Initialized. Resolution: {imageWidth}x{imageHeight}, " +
                      $"Processing Resolution: {imageWidth / _resolutionDivider}x{imageHeight / _resolutionDivider}, " +
                      $"Board: {_squaresX}x{_squaresY}, Marker: {_markerLength}m, Square: {_squareLength}m");
            Debug.Log($"[ChArUco] Camera Matrix (Processing Res):\n{_cameraIntrinsicMatrix.dump()}"); // Log matrix
            Debug.Log($"[ChArUco] Distortion Coeffs:\n{_cameraDistortionCoeffs.dump()}"); // Log coeffs
        }

        // ... (InitializeOpenCVComponents remains largely the same, logs added in Initialize) ...
        private void InitializeOpenCVComponents(int originalWidth, int originalHeight, float cX, float cY, float fX, float fY)
        {
            int processingWidth = originalWidth / _resolutionDivider;
            int processingHeight = originalHeight / _resolutionDivider;
            float proc_fX = fX / _resolutionDivider;
            float proc_fY = fY / _resolutionDivider;
            float proc_cX = cX / _resolutionDivider;
            float proc_cY = cY / _resolutionDivider;

            _cameraIntrinsicMatrix = new Mat(3, 3, CvType.CV_64FC1);
            _cameraIntrinsicMatrix.put(0, 0, proc_fX); _cameraIntrinsicMatrix.put(0, 1, 0); _cameraIntrinsicMatrix.put(0, 2, proc_cX);
            _cameraIntrinsicMatrix.put(1, 0, 0); _cameraIntrinsicMatrix.put(1, 1, proc_fY); _cameraIntrinsicMatrix.put(1, 2, proc_cY);
            _cameraIntrinsicMatrix.put(2, 0, 0); _cameraIntrinsicMatrix.put(2, 1, 0); _cameraIntrinsicMatrix.put(2, 2, 1.0f);

            _cameraDistortionCoeffs = new MatOfDouble(0, 0, 0, 0, 0);

            _fullResolutionMat = new Mat(originalHeight, originalWidth, CvType.CV_8UC4);
            if (_resolutionDivider > 1)
            {
                _scaledResolutionMat = new Mat(processingHeight, processingWidth, CvType.CV_8UC4);
            }
            _processingRgbMat = new Mat(processingHeight, processingWidth, CvType.CV_8UC3);

            _detectedMarkerIds = new Mat();
            _detectedMarkerCorners = new List<Mat>();
            _rejectedMarkerCandidates = new List<Mat>();
            _markerDictionary = Objdetect.getPredefinedDictionary((int)_dictionaryId);

            DetectorParameters detectorParams = new DetectorParameters();
            detectorParams.set_minDistanceToBorder(3);
            detectorParams.set_useAruco3Detection(false);
            detectorParams.set_cornerRefinementMethod(Objdetect.CORNER_REFINE_SUBPIX);
            detectorParams.set_minMarkerPerimeterRate(0.03);
            detectorParams.set_maxMarkerPerimeterRate(4.0);
            detectorParams.set_minSideLengthCanonicalImg(16);
            detectorParams.set_errorCorrectionRate(0.6);

            RefineParameters refineParameters = new RefineParameters(10f, 3f, true);
            _arucoDetector = new ArucoDetector(_markerDictionary, detectorParams, refineParameters);

            _charucoCorners = new Mat();
            _charucoIds = new Mat();
            _charucoBoard = new CharucoBoard(new Size(_squaresX, _squaresY), _squareLength, _markerLength, _markerDictionary);
            CharucoParameters charucoParameters = new CharucoParameters();
            _charucoDetector = new CharucoDetector(_charucoBoard, charucoParameters, detectorParams, refineParameters);
        }

        // ... (DetectMarkers remains the same) ...
        public void DetectMarkers(WebCamTexture webCamTexture, Texture2D resultTexture = null)
        {
            if (!_isInitialized || webCamTexture == null || !webCamTexture.didUpdateThisFrame)
            {
                HasValidPoseThisFrame = false;
                return;
            }

            Utils.webCamTextureToMat(webCamTexture, _fullResolutionMat);

            Mat matToProcess = (_resolutionDivider > 1) ? _scaledResolutionMat : _fullResolutionMat;
            if (_resolutionDivider > 1)
            {
                Imgproc.resize(_fullResolutionMat, _scaledResolutionMat, _scaledResolutionMat.size(), 0, 0, Imgproc.INTER_LINEAR);
            }
            Imgproc.cvtColor(matToProcess, _processingRgbMat, Imgproc.COLOR_RGBA2RGB);

            _detectedMarkerIds.create(0, 1, CvType.CV_32S);
            _detectedMarkerCorners.Clear();
            _rejectedMarkerCandidates.Clear();
            _charucoCorners.create(0, 1, CvType.CV_32FC2);
            _charucoIds.create(0, 1, CvType.CV_32S);
            HasValidPoseThisFrame = false;

            _arucoDetector.detectMarkers(_processingRgbMat, _detectedMarkerCorners, _detectedMarkerIds, _rejectedMarkerCandidates);

            if (_detectedMarkerIds.total() > 0)
            {
                _arucoDetector.refineDetectedMarkers(
                    _processingRgbMat, _charucoBoard, _detectedMarkerCorners,
                    _detectedMarkerIds, _rejectedMarkerCandidates,
                    _cameraIntrinsicMatrix, _cameraDistortionCoeffs
                );

                _charucoDetector.detectBoard(
                    _processingRgbMat, _charucoCorners, _charucoIds,
                    _detectedMarkerCorners, _detectedMarkerIds
                );
            }

            // Optionally draw debug visuals
            if (resultTexture != null)
            {
                if (_detectedMarkerIds.total() > 0)
                {
                    Objdetect.drawDetectedMarkers(_processingRgbMat, _detectedMarkerCorners, _detectedMarkerIds, new Scalar(0, 255, 0));
                }
                if (_charucoIds.total() > 0)
                {
                    if (_charucoCorners.total() == _charucoIds.total())
                    {
                        Objdetect.drawDetectedCornersCharuco(_processingRgbMat, _charucoCorners, _charucoIds, new Scalar(0, 0, 255));
                    }
                }
                Utils.matToTexture2D(_processingRgbMat, resultTexture);
            }
        }

        /// <summary>
        /// Estimates the 3D pose of the ChArUco board in World Space (no smoothing).
        /// </summary>
        public void EstimateWorldPose(GameObject targetObject, Transform cameraTransform)
        {
            if (!_isInitialized || targetObject == null || cameraTransform == null)
            {
                HasValidPoseThisFrame = false;
                return;
            }

            // Check we have enough detected corners for a stable pose
            if (_charucoIds == null || _charucoCorners == null
                || _charucoIds.total() < _minDetectedCornersForPose
                || _charucoCorners.total() != _charucoIds.total())
            {
                // Log only if state changes from valid to invalid
                if (HasValidPoseThisFrame)
                {
                    Debug.LogWarning($"[ChArUco] Not enough ChArUco corners detected ({_charucoIds?.total() ?? 0} corners, {_charucoIds?.total() ?? 0} IDs) for pose estimation. Required: {_minDetectedCornersForPose}.");
                }
                HasValidPoseThisFrame = false;
                return;
            }
            // Log if we *have* enough corners (first frame or after losing track)
            if (!HasValidPoseThisFrame)
            {
                Debug.Log($"[ChArUco] Sufficient ChArUco corners detected ({_charucoIds.total()}). Proceeding to pose estimation.");
            }


            using (Mat rvec = new Mat(3, 1, CvType.CV_64FC1)) // Rotation vector from solvePnP
            using (Mat tvec = new Mat(3, 1, CvType.CV_64FC1)) // Translation vector from solvePnP
            using (Mat objectPoints = new Mat()) // 3D points of corners in board space
            using (Mat imagePoints = new Mat())  // 2D points of corners in image space
            {
                // ---> STEP 0: Match Image Points
                try
                {
                    if (_charucoCorners.type() != CvType.CV_32FC2 || _charucoCorners.cols() != 1)
                    {
                        Debug.LogError($"[ChArUco] _charucoCorners type ({_charucoCorners.type()}) or cols ({_charucoCorners.cols()}) mismatch. Expected CV_32FC2 with 1 col.");
                        HasValidPoseThisFrame = false;
                        return;
                    }
                    List<Mat> charucoCornersList = new List<Mat>((int)_charucoCorners.total());
                    for (int i = 0; i < _charucoCorners.rows(); i++)
                    {
                        charucoCornersList.Add(_charucoCorners.row(i));
                    }
                    _charucoBoard.matchImagePoints(charucoCornersList, _charucoIds, objectPoints, imagePoints);

                    if (objectPoints.empty() || imagePoints.empty() || objectPoints.rows() < _minDetectedCornersForPose)
                    {
                        Debug.LogWarning($"[ChArUco] matchImagePoints found insufficient points ({objectPoints.rows()}). Required: {_minDetectedCornersForPose}");
                        HasValidPoseThisFrame = false;
                        return;
                    }
                    Debug.Log($"[ChArUco] matchImagePoints successful. Object Points:\n{objectPoints.dump()}\nImage Points:\n{imagePoints.dump()}");
                }
                catch (CvException cvEx)
                {
                    Debug.LogError($"[ChArUco] matchImagePoints failed: {cvEx.ToString()}"); // Log full exception
                    HasValidPoseThisFrame = false;
                    return;
                }

                // ---> STEP 1: Run solvePnP
                bool poseEstimated = false;
                using (MatOfPoint3f objectPointsMat = new MatOfPoint3f(objectPoints))
                using (MatOfPoint2f imagePointsMat = new MatOfPoint2f(imagePoints))
                {
                    try
                    {
                        Debug.Log($"[ChArUco] Calling solvePnP with {objectPointsMat.total()} points. Intrinsics:\n{_cameraIntrinsicMatrix.dump()}");
                        poseEstimated = Calib3d.solvePnP(
                            objectPointsMat, imagePointsMat,
                            _cameraIntrinsicMatrix, _cameraDistortionCoeffs,
                            rvec, tvec, false, Calib3d.SOLVEPNP_ITERATIVE // Consider other methods like ITERATIVE if IPPE fails
                        );
                    }
                    catch (CvException cvEx)
                    {
                        Debug.LogError($"[ChArUco] solvePnP failed: {cvEx.ToString()}"); // Log full exception
                        HasValidPoseThisFrame = false;
                        return;
                    }
                }

                if (!poseEstimated)
                {
                    Debug.LogWarning("[ChArUco] solvePnP returned false. Pose estimation failed.");
                    HasValidPoseThisFrame = false;
                    return;
                }

                // Log raw rvec/tvec Mats
                Debug.Log($"[ChArUco][1] Raw solvePnP Mat Results:\nrvec:\n{rvec.dump()}\ntvec:\n{tvec.dump()}");

                // Extract double arrays for easier logging and conversion
                double[] rvecArr = new double[3]; rvec.get(0, 0, rvecArr);
                double[] tvecArr = new double[3]; tvec.get(0, 0, tvecArr);
                Debug.Log($"[ChArUco][1b] Raw solvePnP (double[]): rvec=({rvecArr[0]:F4}, {rvecArr[1]:F4}, {rvecArr[2]:F4}), tvec=({tvecArr[0]:F4}, {tvecArr[1]:F4}, {tvecArr[2]:F4}) (Units: rad, meters)");

                // ---> STEP 2: Convert OpenCV rvec/tvec to Unity PoseData (in Camera Space)
                Debug.Log($"[ChArUco][2] Calling ConvertRvecTvecToPoseData with rvec={VecToString(rvecArr)}, tvec={VecToString(tvecArr)}");
                PoseData rawPoseInCameraSpace = ColocationARUtils.ConvertRvecTvecToPoseData(rvecArr, tvecArr);
                Debug.Log($"[ChArUco][3] Resulting PoseData (Board in Camera Space, LH Y-Up):\n{rawPoseInCameraSpace}");

                // ---> STEP 3: Convert PoseData to Unity Matrix4x4 (Board in Camera Space)
                Debug.Log($"[ChArUco][4] Calling ConvertPoseDataToMatrix with PoseData:\n{rawPoseInCameraSpace}");
                Matrix4x4 boardPoseInCameraSpace = ColocationARUtils.ConvertPoseDataToMatrix(ref rawPoseInCameraSpace, true); // applyLHFlip=true is default, kept for clarity
                Debug.Log($"[ChArUco][5] Resulting Matrix (Board in Camera Space, LH Y-Up):\n{boardPoseInCameraSpace.ToString("F4")}");

                // ---> STEP 4: Get Camera World Pose
                Matrix4x4 cameraPoseInWorldSpace = cameraTransform.localToWorldMatrix;
                Debug.Log($"[ChArUco][6] Camera Pose in World Space (from cameraTransform.localToWorldMatrix):\n{cameraPoseInWorldSpace.ToString("F4")}");

                // ---> STEP 5: Calculate Board World Pose
                Debug.Log($"[ChArUco][7] Calculating Board World Pose = CameraWorld * BoardCamera");
                Matrix4x4 boardPoseInWorldSpace = cameraPoseInWorldSpace * boardPoseInCameraSpace;
                Debug.Log($"[ChArUco][8] Final Calculated Matrix (Board in World Space, LH Y-Up):\n{boardPoseInWorldSpace.ToString("F4")}");

                // ---> STEP 6: Apply World Pose to Target Object
                Debug.Log($"[ChArUco][9] Calling SetTransformFromMatrix for target '{targetObject.name}' with World Matrix:\n{boardPoseInWorldSpace.ToString("F4")}");
                ColocationARUtils.SetTransformFromMatrix(targetObject.transform, ref boardPoseInWorldSpace);
                Debug.Log($"[ChArUco][10] Target '{targetObject.name}' transform AFTER update: Pos={targetObject.transform.position.ToString("F4")}, Rot={targetObject.transform.rotation.eulerAngles.ToString("F4")}");

                HasValidPoseThisFrame = true; // Mark as successful for this frame
            }
        }

        // Helper for logging vectors
        private string VecToString(double[] vec)
        {
            if (vec == null) return "null";
            if (vec.Length != 3) return $"invalid length {vec.Length}";
            return $"({vec[0]:F4}, {vec[1]:F4}, {vec[2]:F4})";
        }


        public void Dispose()
        {
            Debug.Log("[ChArUco] Disposing OpenCV resources."); // Log disposal
            _processingRgbMat?.Dispose();
            _fullResolutionMat?.Dispose();
            _scaledResolutionMat?.Dispose();
            _cameraIntrinsicMatrix?.Dispose();
            _cameraDistortionCoeffs?.Dispose();
            _arucoDetector?.Dispose();
            _detectedMarkerIds?.Dispose();

            if (_detectedMarkerCorners != null)
            {
                foreach (var corner in _detectedMarkerCorners)
                    corner?.Dispose();
                _detectedMarkerCorners.Clear();
            }

            if (_rejectedMarkerCandidates != null)
            {
                foreach (var rej in _rejectedMarkerCandidates)
                    rej?.Dispose();
                _rejectedMarkerCandidates.Clear();
            }

            _charucoCorners?.Dispose();
            _charucoIds?.Dispose();
            _charucoBoard?.Dispose();
            _charucoDetector?.Dispose();

            _isInitialized = false;
        }

        void OnDestroy()
        {
            Dispose();
        }

        // --- Enum Definitions ---
        public enum ArUcoDictionary
        {
            DICT_4X4_50 = Objdetect.DICT_4X4_50,
            DICT_4X4_100 = Objdetect.DICT_4X4_100,
            DICT_4X4_250 = Objdetect.DICT_4X4_250,
            DICT_4X4_1000 = Objdetect.DICT_4X4_1000,
            DICT_5X5_50 = Objdetect.DICT_5X5_50,
            DICT_5X5_100 = Objdetect.DICT_5X5_100,
            DICT_5X5_250 = Objdetect.DICT_5X5_250,
            DICT_5X5_1000 = Objdetect.DICT_5X5_1000,
            DICT_6X6_50 = Objdetect.DICT_6X6_50,
            DICT_6X6_100 = Objdetect.DICT_6X6_100,
            DICT_6X6_250 = Objdetect.DICT_6X6_250,
            DICT_6X6_1000 = Objdetect.DICT_6X6_1000,
            DICT_7X7_50 = Objdetect.DICT_7X7_50,
            DICT_7X7_100 = Objdetect.DICT_7X7_100,
            DICT_7X7_250 = Objdetect.DICT_7X7_250,
            DICT_7X7_1000 = Objdetect.DICT_7X7_1000,
            DICT_ARUCO_ORIGINAL = Objdetect.DICT_ARUCO_ORIGINAL,
        }
    }
}
// --- END OF FILE ChArUcoMarkerTracking.cs ---