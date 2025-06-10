using UnityEngine;
using FishNet.Object;

public class CenterEyeRelay : NetworkBehaviour
{
    private Transform _centerEyeAnchor;

    public override void OnStartClient()
    {
        base.OnStartClient();

        // If we aren't the owner, return. We want only the local owner to do VR logic.
        if (!IsOwner)
            return;

        // [Optional] If you also don't want the host to do it, add "|| IsServer" check above.

        // 1) Disable MeshRenderer for the local owner so they don't see their own head.
        //    If your player has a single MeshRenderer on the root:
        MeshRenderer mr = GetComponent<MeshRenderer>();
        if (mr != null)
            mr.enabled = false;

        // If you have multiple MeshRenderers (e.g. in children), do something like:
        /*
        MeshRenderer[] renderers = GetComponentsInChildren<MeshRenderer>();
        foreach (var r in renderers)
            r.enabled = false;
        */

        // 2) Find the CenterEyeAnchor if needed
        GameObject anchorObj = GameObject.Find("CenterEyeAnchor");
        if (anchorObj != null)
            _centerEyeAnchor = anchorObj.transform;
        else
            Debug.LogWarning("Could not find an object named 'CenterEyeAnchor' in the scene!");
    }

    private void Update()
    {
        // Only the local owner updates the transform to match the VR anchor.
        if (!IsOwner)
            return;

        if (_centerEyeAnchor != null)
        {
            transform.position = _centerEyeAnchor.position;
            transform.rotation = _centerEyeAnchor.rotation;
        }
    }
}
