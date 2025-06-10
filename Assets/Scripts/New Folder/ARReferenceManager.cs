using UnityEngine;

// Simple singleton to hold a reference to the local AR anchor
public class ARReferenceManager : MonoBehaviour
{
    public static ARReferenceManager Instance { get; private set; }

    [SerializeField, Tooltip("Assign the Transform of the local AR anchor GameObject here in the scene.")]
    private Transform _localARAnchorTransform;

    public Transform LocalARAnchorTransform => _localARAnchorTransform;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("Duplicate ARReferenceManager instance found. Destroying this one.", this);
            Destroy(gameObject);
        }
        else
        {
            Instance = this;
            if (_localARAnchorTransform == null)
            {
                Debug.LogError("ARReferenceManager: _localARAnchorTransform is not assigned in the Inspector!", this);
            }
        }
    }

    void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }
    }
}