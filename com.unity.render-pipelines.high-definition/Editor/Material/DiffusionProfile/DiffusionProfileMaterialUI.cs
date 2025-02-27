using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using System.Linq;
using System;

namespace UnityEditor.Rendering.HighDefinition
{
    static class DiffusionProfileMaterialUI
    {
        internal static GUIContent diffusionProfileNotInHDRPAsset = new GUIContent("Make sure this Diffusion Profile is referenced in either a Diffusion Profile Override or the HDRP Global Settings. If the Diffusion Profile is not referenced in either, HDRP cannot use it. To add a reference to the Diffusion Profile in the HDRP Global Settings, press Fix.", EditorGUIUtility.IconContent("console.infoicon").image);

        public static bool IsSupported(MaterialEditor materialEditor)
        {
            return !materialEditor.targets.Any(o =>
            {
                Material m = o as Material;
                return !m.HasProperty("_DiffusionProfileAsset") || !m.HasProperty("_DiffusionProfileHash");
            });
        }

        public static void OnGUI(MaterialEditor materialEditor, MaterialProperty diffusionProfileAsset, MaterialProperty diffusionProfileHash, int profileIndex, string displayName = "Diffusion Profile")
        {
            // We can't cache these fields because of several edge cases like undo/redo or pressing escape in the object picker
            string guid = HDUtils.ConvertVector4ToGUID(diffusionProfileAsset.vectorValue);
            DiffusionProfileSettings diffusionProfile = AssetDatabase.LoadAssetAtPath<DiffusionProfileSettings>(AssetDatabase.GUIDToAssetPath(guid));

            // is it okay to do this every frame ?
            EditorGUI.BeginChangeCheck();
            diffusionProfile = (DiffusionProfileSettings)EditorGUILayout.ObjectField(displayName, diffusionProfile, typeof(DiffusionProfileSettings), false);
            if (EditorGUI.EndChangeCheck())
            {
                Vector4 newGuid = Vector4.zero;
                float hash = 0;

                if (diffusionProfile != null)
                {
                    guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(diffusionProfile));
                    newGuid = HDUtils.ConvertGUIDToVector4(guid);
                    hash = HDShadowUtils.Asfloat(diffusionProfile.profile.hash);
                }

                // encode back GUID and it's hash
                diffusionProfileAsset.vectorValue = newGuid;
                diffusionProfileHash.floatValue = hash;

                // Update external reference.
                foreach (var target in materialEditor.targets)
                {
                    MaterialExternalReferences matExternalRefs = MaterialExternalReferences.GetMaterialExternalReferences(target as Material);
                    matExternalRefs.SetDiffusionProfileReference(profileIndex, diffusionProfile);
                }
            }

            DrawDiffusionProfileWarning(diffusionProfile);
        }

        internal static void DrawDiffusionProfileWarning(DiffusionProfileSettings materialProfile)
        {
            if (materialProfile != null && !HDRenderPipelineGlobalSettings.instance.diffusionProfileSettingsList.Any(d => d == materialProfile))
                CoreEditorUtils.DrawFixMeBox(diffusionProfileNotInHDRPAsset, "Fix", () => HDRenderPipelineGlobalSettings.instance.AddDiffusionProfile(materialProfile));
        }
    }
}
