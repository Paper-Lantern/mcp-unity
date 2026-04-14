using UnityEngine;
using UnityEditor;
using McpUnity.Unity;
using McpUnity.Utils;
using Newtonsoft.Json.Linq;

namespace McpUnity.Tools
{
    /// <summary>
    /// Tool for moving a GameObject's position in the Unity Editor.
    /// Supports world/local space and absolute/relative positioning.
    /// </summary>
    public class MoveGameObjectTool : McpToolBase
    {
        public MoveGameObjectTool()
        {
            Name = "move_gameobject";
            Description = "Moves a GameObject to a new position. Supports world/local space and absolute/relative positioning.";
            IsAsync = false;
        }

        public override JObject Execute(JObject parameters)
        {
            // Find the GameObject
            JObject findError = GameObjectFinder.Find(parameters, out GameObject gameObject, out _);
            if (findError != null) return findError;

            Transform transform = gameObject.transform;

            // Extract position
            JObject positionObj = parameters["position"] as JObject;
            if (positionObj == null)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Required parameter 'position' not provided",
                    "validation_error"
                );
            }

            Vector3 position = new Vector3(
                positionObj["x"]?.ToObject<float>() ?? 0f,
                positionObj["y"]?.ToObject<float>() ?? 0f,
                positionObj["z"]?.ToObject<float>() ?? 0f
            );

            // Get space and relative flags
            string space = parameters["space"]?.ToObject<string>() ?? "world";
            bool relative = parameters["relative"]?.ToObject<bool>() ?? false;

            // Record undo
            Undo.RecordObject(transform, "Move GameObject");

            // Apply the position change
            if (space.ToLower() == "local")
            {
                if (relative)
                    transform.localPosition += position;
                else
                    transform.localPosition = position;
            }
            else // world space
            {
                if (relative)
                    transform.position += position;
                else
                    transform.position = position;
            }

            EditorUtility.SetDirty(gameObject);

            // Return result with new position
            return new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = $"GameObject '{gameObject.name}' moved successfully.",
                ["instanceId"] = gameObject.GetInstanceID(),
                ["name"] = gameObject.name,
                ["path"] = GameObjectFinder.GetPath(gameObject),
                ["position"] = new JObject
                {
                    ["world"] = new JObject
                    {
                        ["x"] = transform.position.x,
                        ["y"] = transform.position.y,
                        ["z"] = transform.position.z
                    },
                    ["local"] = new JObject
                    {
                        ["x"] = transform.localPosition.x,
                        ["y"] = transform.localPosition.y,
                        ["z"] = transform.localPosition.z
                    }
                }
            };
        }
    }

    /// <summary>
    /// Tool for rotating a GameObject in the Unity Editor.
    /// Supports world/local space and absolute/relative rotation using Euler angles.
    /// </summary>
    public class RotateGameObjectTool : McpToolBase
    {
        public RotateGameObjectTool()
        {
            Name = "rotate_gameobject";
            Description = "Rotates a GameObject using Euler angles. Supports world/local space and absolute/relative rotation.";
            IsAsync = false;
        }

        public override JObject Execute(JObject parameters)
        {
            // Find the GameObject
            JObject findError = GameObjectFinder.Find(parameters, out GameObject gameObject, out _);
            if (findError != null) return findError;

            Transform transform = gameObject.transform;

            // Extract rotation (Euler angles)
            JObject rotationObj = parameters["rotation"] as JObject;
            if (rotationObj == null)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Required parameter 'rotation' not provided",
                    "validation_error"
                );
            }

            Vector3 eulerAngles = new Vector3(
                rotationObj["x"]?.ToObject<float>() ?? 0f,
                rotationObj["y"]?.ToObject<float>() ?? 0f,
                rotationObj["z"]?.ToObject<float>() ?? 0f
            );

            // Get space and relative flags
            string space = parameters["space"]?.ToObject<string>() ?? "world";
            bool relative = parameters["relative"]?.ToObject<bool>() ?? false;

            // Record undo
            Undo.RecordObject(transform, "Rotate GameObject");

            // Apply the rotation
            if (relative)
            {
                // Relative rotation - add to current rotation
                Space unitySpace = space.ToLower() == "local" ? Space.Self : Space.World;
                transform.Rotate(eulerAngles, unitySpace);
            }
            else
            {
                // Absolute rotation - set directly
                if (space.ToLower() == "local")
                    transform.localEulerAngles = eulerAngles;
                else
                    transform.eulerAngles = eulerAngles;
            }

            EditorUtility.SetDirty(gameObject);

            // Return result with new rotation
            return new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = $"GameObject '{gameObject.name}' rotated successfully.",
                ["instanceId"] = gameObject.GetInstanceID(),
                ["name"] = gameObject.name,
                ["path"] = GameObjectFinder.GetPath(gameObject),
                ["rotation"] = new JObject
                {
                    ["world"] = new JObject
                    {
                        ["x"] = transform.eulerAngles.x,
                        ["y"] = transform.eulerAngles.y,
                        ["z"] = transform.eulerAngles.z
                    },
                    ["local"] = new JObject
                    {
                        ["x"] = transform.localEulerAngles.x,
                        ["y"] = transform.localEulerAngles.y,
                        ["z"] = transform.localEulerAngles.z
                    }
                }
            };
        }
    }

    /// <summary>
    /// Tool for scaling a GameObject in the Unity Editor.
    /// Supports absolute and relative (multiplicative) scaling.
    /// </summary>
    public class ScaleGameObjectTool : McpToolBase
    {
        public ScaleGameObjectTool()
        {
            Name = "scale_gameobject";
            Description = "Scales a GameObject. Supports absolute and relative (multiplicative) scaling.";
            IsAsync = false;
        }

        public override JObject Execute(JObject parameters)
        {
            // Find the GameObject
            JObject findError = GameObjectFinder.Find(parameters, out GameObject gameObject, out _);
            if (findError != null) return findError;

            Transform transform = gameObject.transform;

            // Extract scale
            JObject scaleObj = parameters["scale"] as JObject;
            if (scaleObj == null)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Required parameter 'scale' not provided",
                    "validation_error"
                );
            }

            Vector3 scale = new Vector3(
                scaleObj["x"]?.ToObject<float>() ?? 1f,
                scaleObj["y"]?.ToObject<float>() ?? 1f,
                scaleObj["z"]?.ToObject<float>() ?? 1f
            );

            // Get relative flag
            bool relative = parameters["relative"]?.ToObject<bool>() ?? false;

            // Record undo
            Undo.RecordObject(transform, "Scale GameObject");

            // Apply the scale
            if (relative)
            {
                // Relative scale - multiply current scale
                transform.localScale = Vector3.Scale(transform.localScale, scale);
            }
            else
            {
                // Absolute scale - set directly
                transform.localScale = scale;
            }

            EditorUtility.SetDirty(gameObject);

            // Return result with new scale
            return new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = $"GameObject '{gameObject.name}' scaled successfully.",
                ["instanceId"] = gameObject.GetInstanceID(),
                ["name"] = gameObject.name,
                ["path"] = GameObjectFinder.GetPath(gameObject),
                ["scale"] = new JObject
                {
                    ["x"] = transform.localScale.x,
                    ["y"] = transform.localScale.y,
                    ["z"] = transform.localScale.z
                }
            };
        }
    }

    /// <summary>
    /// Tool for setting a GameObject's full transform (position, rotation, scale) in one operation.
    /// All parameters are optional - only provided values will be changed.
    /// </summary>
    public class SetTransformTool : McpToolBase
    {
        public SetTransformTool()
        {
            Name = "set_transform";
            Description = "Sets a GameObject's transform (position, rotation, scale) in one operation. All transform properties are optional.";
            IsAsync = false;
        }

        public override JObject Execute(JObject parameters)
        {
            // Find the GameObject
            JObject findError = GameObjectFinder.Find(parameters, out GameObject gameObject, out _);
            if (findError != null) return findError;

            Transform transform = gameObject.transform;

            // Check that at least one transform property is provided
            JObject positionObj = parameters["position"] as JObject;
            JObject rotationObj = parameters["rotation"] as JObject;
            JObject scaleObj = parameters["scale"] as JObject;

            if (positionObj == null && rotationObj == null && scaleObj == null)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "At least one of 'position', 'rotation', or 'scale' must be provided",
                    "validation_error"
                );
            }

            // Get space flag (applies to position and rotation)
            string space = parameters["space"]?.ToObject<string>() ?? "world";
            bool isLocal = space.ToLower() == "local";

            // Record undo
            Undo.RecordObject(transform, "Set Transform");

            // Apply position if provided
            if (positionObj != null)
            {
                Vector3 position = new Vector3(
                    positionObj["x"]?.ToObject<float>() ?? (isLocal ? transform.localPosition.x : transform.position.x),
                    positionObj["y"]?.ToObject<float>() ?? (isLocal ? transform.localPosition.y : transform.position.y),
                    positionObj["z"]?.ToObject<float>() ?? (isLocal ? transform.localPosition.z : transform.position.z)
                );

                if (isLocal)
                    transform.localPosition = position;
                else
                    transform.position = position;
            }

            // Apply rotation if provided
            if (rotationObj != null)
            {
                Vector3 eulerAngles = new Vector3(
                    rotationObj["x"]?.ToObject<float>() ?? (isLocal ? transform.localEulerAngles.x : transform.eulerAngles.x),
                    rotationObj["y"]?.ToObject<float>() ?? (isLocal ? transform.localEulerAngles.y : transform.eulerAngles.y),
                    rotationObj["z"]?.ToObject<float>() ?? (isLocal ? transform.localEulerAngles.z : transform.eulerAngles.z)
                );

                if (isLocal)
                    transform.localEulerAngles = eulerAngles;
                else
                    transform.eulerAngles = eulerAngles;
            }

            // Apply scale if provided
            if (scaleObj != null)
            {
                Vector3 scale = new Vector3(
                    scaleObj["x"]?.ToObject<float>() ?? transform.localScale.x,
                    scaleObj["y"]?.ToObject<float>() ?? transform.localScale.y,
                    scaleObj["z"]?.ToObject<float>() ?? transform.localScale.z
                );
                transform.localScale = scale;
            }

            EditorUtility.SetDirty(gameObject);

            // Return result with full transform data
            return new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = $"GameObject '{gameObject.name}' transform updated successfully.",
                ["instanceId"] = gameObject.GetInstanceID(),
                ["name"] = gameObject.name,
                ["path"] = GameObjectFinder.GetPath(gameObject),
                ["transform"] = new JObject
                {
                    ["position"] = new JObject
                    {
                        ["world"] = new JObject
                        {
                            ["x"] = transform.position.x,
                            ["y"] = transform.position.y,
                            ["z"] = transform.position.z
                        },
                        ["local"] = new JObject
                        {
                            ["x"] = transform.localPosition.x,
                            ["y"] = transform.localPosition.y,
                            ["z"] = transform.localPosition.z
                        }
                    },
                    ["rotation"] = new JObject
                    {
                        ["world"] = new JObject
                        {
                            ["x"] = transform.eulerAngles.x,
                            ["y"] = transform.eulerAngles.y,
                            ["z"] = transform.eulerAngles.z
                        },
                        ["local"] = new JObject
                        {
                            ["x"] = transform.localEulerAngles.x,
                            ["y"] = transform.localEulerAngles.y,
                            ["z"] = transform.localEulerAngles.z
                        }
                    },
                    ["scale"] = new JObject
                    {
                        ["x"] = transform.localScale.x,
                        ["y"] = transform.localScale.y,
                        ["z"] = transform.localScale.z
                    }
                }
            };
        }
    }

}
