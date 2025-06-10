// --- START OF FILE ColocationARUtils.cs ---

using UnityEngine;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.Calib3dModule;
using System;
using System.Text; // Added for StringBuilder logging

/// <summary>
/// Utility class for handling conversions between OpenCV pose data (rvec, tvec)
/// and Unity pose data (Pose, Matrix4x4, Transform), accounting for
/// coordinate system differences.
///
/// Coordinate Systems:
/// - OpenCV: Right-Handed, +X right, +Y down, +Z forward (away from camera)
/// - Unity:  Left-Handed,  +X right, +Y up,   +Z forward
/// </summary>
public static class ColocationARUtils
{

    /// <summary>
    /// Converts OpenCV rvec/tvec (pose of object in camera frame)
    /// to a Unity PoseData representing the POSE OF THE BOARD IN CAMERA SPACE.
    /// Correctly handles RH Y-down (OpenCV) -> LH Y-up (Unity) and INVERTS ONLY the rotation component.
    /// Uses local temporary Mats for thread safety.
    /// </summary>
    /// <param name="rvec">OpenCV rotation vector (3x1 or 1x3 double array).</param>
    /// <param name="tvec">OpenCV translation vector (3x1 or 1x3 double array).</param>
    /// <returns>A Unity PoseData struct (Vector3 position, Quaternion rotation) in Unity camera space.</returns>
    /// <exception cref="ArgumentException">Thrown if input arrays are null or not size 3.</exception>
    public static PoseData ConvertRvecTvecToPoseData(double[] rvec, double[] tvec)
    {
        Debug.Log($"[Util] ---- Start ConvertRvecTvecToPoseData (REVISED 2) ----");
        if (rvec == null || rvec.Length != 3) throw new ArgumentException("rvec", nameof(rvec));
        if (tvec == null || tvec.Length != 3) throw new ArgumentException("tvec", nameof(tvec));

        Debug.Log($"[Util-1] Input rvec: ({rvec[0]:F4}, {rvec[1]:F4}, {rvec[2]:F4})");
        Debug.Log($"[Util-1] Input tvec: ({tvec[0]:F4}, {tvec[1]:F4}, {tvec[2]:F4}) (Meters, OpenCV Cam Space: +X R, +Y D, +Z Fwd)");

        Quaternion finalRotation = Quaternion.identity; // Default

        using (Mat rvecMat = new Mat(3, 1, CvType.CV_64FC1))
        using (Mat R_cam_obj = new Mat(3, 3, CvType.CV_64FC1)) // OpenCV Rotation Matrix (Board orientation IN Camera frame)
        {
            rvecMat.put(0, 0, rvec);
            try
            {
                Calib3d.Rodrigues(rvecMat, R_cam_obj);
                Debug.Log($"[Util-2b] R_cam_obj (OpenCV RH Y-Down Rot Matrix) from Rodrigues:\n{R_cam_obj.dump()}");
                // Columns of R_cam_obj are the Board's X, Y, Z axes expressed in Camera's OpenCV Coordinate system.
            }
            catch (CvException cvEx)
            {
                Debug.LogError($"[Util] Rodrigues failed: {cvEx.ToString()}. Returning identity pose.");
                Debug.Log($"[Util] ---- End ConvertRvecTvecToPoseData (Error) ----");
                return PoseData.identity;
            }

            // --- Conversion using OpenCVForUnity Utils.matrixToPose logic ---
            Matrix4x4 unityBoardRotInCam = Matrix4x4.identity;
            double[] RcamObjData = new double[9];
            R_cam_obj.get(0, 0, RcamObjData);
            // RcamObjData = [ r00, r01, r02, r10, r11, r12, r20, r21, r22 ]

            // Fill Unity Matrix (Column-Major) based on OpenCVForUnity convention
            // Unity Col 0 (X basis): Maps to OpenCV X basis [r00, r10, r20] -> Apply Y flip
            unityBoardRotInCam[0, 0] = (float)RcamObjData[0]; // Xx
            unityBoardRotInCam[1, 0] = -(float)RcamObjData[3]; // Xy (flipped)
            unityBoardRotInCam[2, 0] = (float)RcamObjData[6]; // Xz

            // Unity Col 1 (Y basis): Maps to OpenCV -Y basis [-r01, -r11, -r21] -> Apply Y flip
            unityBoardRotInCam[0, 1] = -(float)RcamObjData[1]; // Yx (flipped)
            unityBoardRotInCam[1, 1] = (float)RcamObjData[4]; // Yy (- * - = +)
            unityBoardRotInCam[2, 1] = -(float)RcamObjData[7]; // Yz (flipped)

            // Unity Col 2 (Z basis): Maps to OpenCV Z basis [r02, r12, r22] -> Apply Y flip
            unityBoardRotInCam[0, 2] = (float)RcamObjData[2]; // Zx
            unityBoardRotInCam[1, 2] = -(float)RcamObjData[5]; // Zy (flipped)
            unityBoardRotInCam[2, 2] = (float)RcamObjData[8]; // Zz


            Debug.Log($"[Util-4 REVISED 2] Unity LH Rotation Matrix (Board in Camera Space from R_cam_obj):\n{unityBoardRotInCam.ToString("F4")}");

            // Sanity checks
            float determinant = unityBoardRotInCam.determinant;
            if (Mathf.Abs(determinant - 1.0f) > 0.01f)
            {
                Debug.LogWarning($"[Util] Determinant of intermediate rotation matrix is {determinant:F4} (should be ~1). Rotation may be invalid.");
                // If determinant is -1, attempt to fix by flipping one axis (e.g., Z)
                if (Mathf.Abs(determinant + 1.0f) < 0.01f)
                {
                    Debug.LogWarning("[Util] Determinant is ~-1. Attempting correction by flipping Z-axis signs.");
                    unityBoardRotInCam[0, 2] = -unityBoardRotInCam[0, 2];
                    unityBoardRotInCam[1, 2] = -unityBoardRotInCam[1, 2];
                    unityBoardRotInCam[2, 2] = -unityBoardRotInCam[2, 2];
                    Debug.Log($"[Util-4 CORRECTED] Unity LH Rotation Matrix:\n{unityBoardRotInCam.ToString("F4")}");
                }
            }
            // ... other sanity checks (column magnitude, orthogonality) if needed ...

            try
            {
                finalRotation = unityBoardRotInCam.rotation; // Extract Quaternion
                Debug.Log($"[Util-5 REVISED 2] Final Rotation Quaternion (Unity LH Y-Up):\n{finalRotation.eulerAngles.ToString("F4")}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Util] Failed to convert Unity Matrix to Quaternion: {ex.Message}. Matrix was:\n{unityBoardRotInCam.ToString("F4")}. Using Identity.");
                // finalRotation remains Quaternion.identity
            }
        } // Dispose Mats

        // --- Translation Conversion (remains the same) ---
        Vector3 finalPosition = new Vector3(
            (float)tvec[0],    // X maps directly
           -(float)tvec[1],   // Flip Y (OpenCV Down is Unity Up relative to camera frame)
            (float)tvec[2]    // Z maps directly
        );
        Debug.Log($"[Util-6] Final Position Vector (Unity LH Y-Up Camera Space): {finalPosition.ToString("F4")}");

        PoseData resultPose = new PoseData { pos = finalPosition, rot = finalRotation };
        Debug.Log($"[Util-7] Returning PoseData: {resultPose}");
        Debug.Log($"[Util] ---- End ConvertRvecTvecToPoseData (REVISED 2) ----");
        return resultPose;
    }

    /// <summary>
    /// Converts a Unity PoseData struct into a standard Unity Matrix4x4 (Left-Handed).
    /// </summary>
    public static Matrix4x4 ConvertPoseDataToMatrix(ref PoseData pose, bool applyLHFlip = true /* Param deprecated but kept */)
    {
        Debug.Log($"[Util] ---- Start ConvertPoseDataToMatrix ----");
        Debug.Log($"[Util-8] Input PoseData: {pose}");
        // Matrix4x4.TRS creates a transformation matrix: World = Parent * TRS(pos, rot, scale)
        // Here, Parent is Camera Space, World is Board Space relative to Camera.
        // Input pose is already in Unity LH Y-Up Camera Space.
        Matrix4x4 matrix = Matrix4x4.TRS(pose.pos, pose.rot, Vector3.one);
        if (!applyLHFlip) { Debug.LogWarning("[Util] ConvertPoseDataToMatrix: applyLHFlip=false is deprecated and ignored. Input PoseData should already be LH Y-Up."); }
        Debug.Log($"[Util-9] Resulting Matrix4x4 (TRS):\n{matrix.ToString("F4")}");
        Debug.Log($"[Util] ---- End ConvertPoseDataToMatrix ----");
        return matrix;
    }

    /// <summary>
    /// Sets a Unity Transform's world pose from a Matrix4x4.
    /// </summary>
    public static void SetTransformFromMatrix(Transform transform, ref Matrix4x4 matrix)
    {
        Debug.Log($"[Util] ---- Start SetTransformFromMatrix ({transform?.name ?? "null"}) ----");
        if (transform == null) throw new ArgumentNullException(nameof(transform));

        Debug.Log($"[Util-10] Input World Matrix:\n{matrix.ToString("F4")}");

        // Log current transform state BEFORE applying
        Debug.Log($"[Util-14] Target Transform BEFORE: Pos={transform.position.ToString("F4")}, Rot={transform.rotation.eulerAngles.ToString("F4")}, Scale={transform.localScale.ToString("F4")}");

        // Extract components
        Vector3 position = matrix.GetColumn(3);
        Quaternion rotation = matrix.rotation; // Handles LH/RH extraction correctly if matrix is valid
        Vector3 scale = matrix.lossyScale; // World scale

        Debug.Log($"[Util-11] Extracted Position: {position.ToString("F4")}");
        Debug.Log($"[Util-12] Extracted Rotation: {rotation.eulerAngles.ToString("F4")}");
        Debug.Log($"[Util-13] Extracted LossyScale: {scale.ToString("F4")}");

        // Apply to transform
        transform.position = position;
        transform.rotation = rotation;
        // Setting localScale based on lossyScale assumes parent scale is identity or uniform.
        // If the parent has non-uniform scale, this might not perfectly reproduce the input matrix's world scale.
        // However, in most AR cases, the final matrix represents the desired world pose.
        transform.localScale = scale; // Adjust if local scale is needed instead

        // Log current transform state AFTER applying
        Debug.Log($"[Util-15] Target Transform AFTER : Pos={transform.position.ToString("F4")}, Rot={transform.rotation.eulerAngles.ToString("F4")}, Scale={transform.localScale.ToString("F4")}");
        Debug.Log($"[Util] ---- End SetTransformFromMatrix ({transform.name}) ----");
    }

    /// <summary>
    /// Helper structure for storing Unity pose data.
    /// </summary>
    [System.Serializable]
    public struct PoseData
    {
        public Vector3 pos;
        public Quaternion rot;
        public static readonly PoseData identity = new PoseData { pos = Vector3.zero, rot = Quaternion.identity };
        public override string ToString() => $"Pos: {pos.ToString("F4")}, Rot(Euler): {rot.eulerAngles.ToString("F4")}"; // Use Euler for easier reading in logs
        public static bool operator ==(PoseData a, PoseData b) => a.pos == b.pos && a.rot == b.rot;
        public static bool operator !=(PoseData a, PoseData b) => !(a == b);
        public override bool Equals(object obj) => obj is PoseData other && this == other;
        public override int GetHashCode() => HashCode.Combine(pos, rot);
    }
}
// --- END OF FILE ColocationARUtils.cs ---