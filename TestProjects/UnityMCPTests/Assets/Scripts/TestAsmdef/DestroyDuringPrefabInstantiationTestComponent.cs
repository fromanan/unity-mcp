using UnityEngine;

namespace MCPForUnityTests.Editor.Tools
{
    /// <summary>
    /// Test-only lifecycle fixture that destroys scene instances when enabled.
    /// </summary>
    [ExecuteAlways]
    public sealed class DestroyDuringPrefabInstantiationTestComponent : MonoBehaviour
    {
        /// <summary>
        /// Gets or sets whether newly enabled scene instances should destroy their root object.
        /// </summary>
        public static bool DestroyInstances { get; set; }

        /// <summary>
        /// Gets or sets whether lifecycle initialization should emit a warning without failing.
        /// </summary>
        public static bool LogWarnings { get; set; }

        private void Awake()
        {
            DestroyIfRequested();
        }

        private void OnEnable()
        {
            DestroyIfRequested();
        }

        private void DestroyIfRequested()
        {
            if (!gameObject.scene.IsValid())
            {
                return;
            }

            if (LogWarnings)
            {
                Debug.LogWarning("Prefab hardening fixture emitted a lifecycle warning.");
            }

            if (!DestroyInstances)
            {
                return;
            }

            Debug.LogError("Prefab hardening fixture destroyed its instance during lifecycle initialization.");
            Object.DestroyImmediate(gameObject);
        }
    }
}
