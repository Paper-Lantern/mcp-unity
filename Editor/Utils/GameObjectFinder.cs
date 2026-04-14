using UnityEngine;
using UnityEditor;
using McpUnity.Unity;
using Newtonsoft.Json.Linq;

namespace McpUnity.Utils
{
    /// <summary>
    /// Centralized utility for finding GameObjects by instance ID or hierarchy path.
    /// Replaces duplicated GameObjectToolUtils, MaterialToolUtils, and TransformToolUtils helpers.
    /// </summary>
    public static class GameObjectFinder
    {
        /// <summary>
        /// Find a GameObject from JObject parameters containing "instanceId" and/or "objectPath".
        /// </summary>
        /// <param name="parameters">JObject with optional instanceId (int) and objectPath (string)</param>
        /// <param name="gameObject">Output GameObject if found</param>
        /// <param name="identifierInfo">Description of how the object was identified (for error messages)</param>
        /// <returns>Error JObject if not found or missing params, null on success</returns>
        public static JObject Find(JObject parameters, out GameObject gameObject, out string identifierInfo)
        {
            int? instanceId = parameters?["instanceId"]?.ToObject<int?>();
            string objectPath = parameters?["objectPath"]?.ToObject<string>();
            return Find(instanceId, objectPath, out gameObject, out identifierInfo);
        }

        /// <summary>
        /// Find a GameObject by instance ID or hierarchy path.
        /// </summary>
        /// <param name="instanceId">Optional instance ID</param>
        /// <param name="objectPath">Optional hierarchy path (e.g. "Canvas/Panel/Button")</param>
        /// <param name="gameObject">Output GameObject if found</param>
        /// <param name="identifierInfo">Description of how the object was identified</param>
        /// <returns>Error JObject if not found, null on success</returns>
        public static JObject Find(int? instanceId, string objectPath, out GameObject gameObject, out string identifierInfo)
        {
            gameObject = null;
            identifierInfo = "";

            if (instanceId.HasValue)
            {
                gameObject = EditorUtility.InstanceIDToObject(instanceId.Value) as GameObject;
                identifierInfo = $"instance ID {instanceId.Value}";
            }
            else if (!string.IsNullOrEmpty(objectPath))
            {
                gameObject = GameObject.Find(objectPath);
                if (gameObject == null)
                {
                    gameObject = FindByPath(objectPath);
                }
                identifierInfo = $"path '{objectPath}'";
            }
            else
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Either 'instanceId' or 'objectPath' must be provided.",
                    "validation_error"
                );
            }

            if (gameObject == null)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"GameObject not found using {identifierInfo}.",
                    "not_found_error"
                );
            }

            return null; // Success
        }

        /// <summary>
        /// Find a GameObject by traversing the scene hierarchy path.
        /// Handles paths like "Parent/Child/GrandChild" or "/Parent/Child".
        /// Unlike GameObject.Find(), this works with inactive GameObjects when traversing children.
        /// </summary>
        public static GameObject FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            path = path.TrimStart('/');
            string[] parts = path.Split('/');
            if (parts.Length == 0) return null;

            // Find root object in active scene
            GameObject current = null;
            GameObject[] rootObjects = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();

            foreach (var root in rootObjects)
            {
                if (root.name == parts[0])
                {
                    current = root;
                    break;
                }
            }

            if (current == null) return null;

            // Traverse children using Transform.Find (works with inactive children)
            for (int i = 1; i < parts.Length; i++)
            {
                Transform child = current.transform.Find(parts[i]);
                if (child == null) return null;
                current = child.gameObject;
            }

            return current;
        }

        /// <summary>
        /// Get the full hierarchy path of a GameObject (e.g. "/Canvas/Panel/Button").
        /// </summary>
        public static string GetPath(GameObject obj)
        {
            if (obj == null) return null;
            string path = "/" + obj.name;
            while (obj.transform.parent != null)
            {
                obj = obj.transform.parent.gameObject;
                path = "/" + obj.name + path;
            }
            return path;
        }
    }
}
