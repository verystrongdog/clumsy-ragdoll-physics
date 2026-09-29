using System.Collections.Generic;
using UnityEngine;

namespace ClumsyRagdoll
{
    public static class SurfaceKit
    {
        static readonly Dictionary<int, Material> Materials = new Dictionary<int, Material>();
        static Shader _shader;
        static bool _shaderMissingLogged;
        static Font _font;

        public static Font UiFont
        {
            get
            {
                if (_font == null)
                    _font = Resources.Load<Font>("Ui");
                return _font;
            }
        }

        public static PhysicsMaterial Friction(float friction, float bounce, PhysicsMaterialCombine frictionCombine, PhysicsMaterialCombine bounceCombine)
        {
            return new PhysicsMaterial
            {
                dynamicFriction = friction,
                staticFriction = friction,
                bounciness = bounce,
                frictionCombine = frictionCombine,
                bounceCombine = bounceCombine
            };
        }

        public static Material Lit(Color color, float smoothness = 0.16f)
        {
            int key = ColorKey(color, smoothness);
            if (Materials.TryGetValue(key, out Material existing))
                return existing;

            Material mat = new Material(FindLitShader());
            if (mat.HasProperty("_Color"))
                mat.SetColor("_Color", color);
            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Glossiness"))
                mat.SetFloat("_Glossiness", smoothness);
            if (mat.HasProperty("_Smoothness"))
                mat.SetFloat("_Smoothness", smoothness);
            if (mat.HasProperty("_Metallic"))
                mat.SetFloat("_Metallic", 0f);
            Materials[key] = mat;
            return mat;
        }

        public static GameObject Decor(Transform parent, PrimitiveType type, Vector3 worldOffset, float worldSize, Material material)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            Collider collider = go.GetComponent<Collider>();
            if (collider != null)
            {
                collider.enabled = false;
                Object.Destroy(collider);
            }

            go.transform.SetParent(parent, false);
            float parentScale = Mathf.Max(0.0001f, parent.lossyScale.x);
            go.transform.localPosition = worldOffset / parentScale;
            // 球的直径和方块的边长在缩放 1 时都是 1。worldSize 是期望的世界尺寸。
            go.transform.localScale = Vector3.one * (worldSize / parentScale);
            go.GetComponent<Renderer>().sharedMaterial = material;
            return go;
        }

        public static FaceCamera CreateLabel(Transform follow, string text, Color color, float height)
        {
            GameObject go = new GameObject("名字");
            TextMesh mesh = go.AddComponent<TextMesh>();
            mesh.text = text;
            mesh.anchor = TextAnchor.LowerCenter;
            mesh.alignment = TextAlignment.Center;
            mesh.characterSize = 0.08f;
            mesh.fontSize = 64;
            mesh.color = color;
            Font font = UiFont;
            if (font != null)
            {
                mesh.font = font;
                font.RequestCharactersInTexture(text, mesh.fontSize);
                if (font.material != null)
                    go.GetComponent<MeshRenderer>().sharedMaterial = font.material;
            }

            FaceCamera face = go.AddComponent<FaceCamera>();
            face.Follow = follow;
            face.Height = height;
            return face;
        }

        static Shader FindLitShader()
        {
            if (_shader != null)
                return _shader;

            _shader = Shader.Find("Standard")
                ?? Shader.Find("Legacy Shaders/Diffuse")
                ?? Shader.Find("Unlit/Color")
                ?? Shader.Find("Sprites/Default")
                ?? Shader.Find("UI/Default")
                ?? Shader.Find("Hidden/InternalErrorShader");

            if (_shader == null && !_shaderMissingLogged)
            {
                _shaderMissingLogged = true;
                Debug.LogError("笨拙布娃娃：找不到可用着色器。请让 Graphics 的 Scriptable Render Pipeline 保持为空，使用内置渲染管线。");
            }

            return _shader;
        }

        static int ColorKey(Color color, float smoothness)
        {
            int r = Mathf.RoundToInt(color.r * 255f);
            int g = Mathf.RoundToInt(color.g * 255f);
            int b = Mathf.RoundToInt(color.b * 255f);
            int s = Mathf.RoundToInt(smoothness * 100f);
            return (r << 24) ^ (g << 16) ^ (b << 8) ^ s;
        }
    }

    public sealed class FaceCamera : MonoBehaviour
    {
        public Transform Follow;
        public float Height = 0.4f;

        void LateUpdate()
        {
            if (Follow == null)
                return;
            transform.position = Follow.position + Vector3.up * Height;
            Camera cam = Camera.main;
            if (cam == null)
                return;
            Vector3 to = transform.position - cam.transform.position;
            to.y = 0f;
            if (to.sqrMagnitude < 1e-6f)
                return;
            transform.rotation = Quaternion.LookRotation(to.normalized, Vector3.up);
        }
    }
}
