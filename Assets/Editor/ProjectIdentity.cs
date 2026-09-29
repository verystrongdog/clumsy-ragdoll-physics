using UnityEditor;
using UnityEngine;

namespace ClumsyRagdoll.EditorTools
{
    [InitializeOnLoad]
    static class ProjectIdentity
    {
        static ProjectIdentity()
        {
            EditorApplication.delayCall += Ensure;
        }

        static void Ensure()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            const string key = "ClumsyRagdoll.ProjectNamed";
            if (!EditorPrefs.GetBool(key, false))
            {
                PlayerSettings.productName = "笨拙布娃娃物理";
                PlayerSettings.companyName = "ClumsyRagdoll";
                PlayerSettings.runInBackground = true;
                EditorPrefs.SetBool(key, true);
            }

            EnsureLitMaterial();
        }

        [MenuItem("笨拙布娃娃/生成内置材质")]
        static void EnsureLitMaterial()
        {
            const string path = "Assets/Resources/PrototypeLit.mat";
            if (AssetDatabase.LoadAssetAtPath<Material>(path) != null)
                return;

            Shader shader = Shader.Find("Standard");
            if (shader == null)
                return;

            if (!AssetDatabase.IsValidFolder("Assets/Resources"))
                AssetDatabase.CreateFolder("Assets", "Resources");

            Material material = new Material(shader);
            AssetDatabase.CreateAsset(material, path);
            AssetDatabase.SaveAssets();
        }
    }
}
