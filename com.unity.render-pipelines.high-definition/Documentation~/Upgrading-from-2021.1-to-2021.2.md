# Upgrading HDRP from 2021.1 to 2021.2

In the High Definition Render Pipeline (HDRP), some features work differently between major versions. This document helps you upgrade HDRP from 11.x to 12.x.

## Shader code

The following shader code behaviour has changed slightly for HDRP version 12.x

### Decals

Decals in HDRP have changed in the following ways:

* HDRP Decals can now use a method based on surface gradient to disturb the normal of the affected GameObjects. To use this feature, enable it in the HDRP asset.

* When you create a custom decal shader, the accumulated normal value stored in the DBuffer now represents the surface gradient instead of the tangent space normal. You can find an example of this implementation in `DecalUtilities.hlsl`.

* When you write a shader for a surface that recieves decals, the normals should now be blended using the surface gradient framework. The prototype for the function `ApplyDecalToSurfaceData` has changed from: `void ApplyDecalToSurfaceData(DecalSurfaceData decalSurfaceData, float3 vtxNormal, inout SurfaceData surfaceData)` to `void ApplyDecalToSurfaceData(DecalSurfaceData decalSurfaceData, float3 vtxNormal, inout SurfaceData surfaceData, inout float3 normalTS)`. You can refer to `LitData.hlsl` and `LitDecalData.hlsl` for an example implementation.

### Tessellation
HDRP 2021.2 has various tessellation shader code to enable tessellation support in [Master Stacks](master-stack-hdrp.md).  has changed the tessellation shader code in the following ways:

* The function `GetTessellationFactors()` has moved from `LitDataMeshModification.hlsl` to `TessellationShare.hlsl`. It calls a new function, `GetTessellationFactor()`, that is in the`LitDataMeshModification.hlsl`file.
* The prototype of `ApplyTessellationModification()` function has changed from:<br/> `void ApplyTessellationModification(VaryingsMeshToDS input, float3 normalWS, inout float3 positionRWS)`<br/>to:<br/>`VaryingsMeshToDS ApplyTessellationModification(VaryingsMeshToDS input, float3 timeParameters)`.
* HDRP has improved support of motion vectors for tessellation. Only `previousPositionRWS` is part of the varyings. HDRP also added the `MotionVectorTessellation()` function. For more information, see the `MotionVectorVertexShaderCommon.hlsl` file.
* HDRP now evaluates the `tessellationFactor` in the vertex shader and passes it to the hull shader as an interpolator. For more information, see the `VaryingMesh.hlsl` and `VertMesh.hlsl` files.

### Ambient Occlusion and Specular Occlusion

The algorithm for computing specular occlusion from bent normals and ambient occlusion has been changed to improve visual results.
To use the old algorithm, function calls to `GetSpecularOcclusionFromBentAO` should be replaced by calls to `GetSpecularOcclusionFromBentAO_ConeCone`

The algorithm to calculate the contribution of ambient occlusion and specular occlusion to direct lighting have been change from taking into account the multi-bounce contribution (GTAOMultiBounce) to not using the multi-bounce which is more correct.

### Light list

The previous `g_vLightListGlobal` uniform have been rename to explicit `g_vLightListTile` and `g_vLightListCluster` light list name. This work required to fix a wrong behavior on console.

## Density Volumes

Density Volumes are now known as **Local Volumetric Fog**.

If a Scene uses Density Volumes, HDRP automatically changes the GameObjects to use the new component name, with all the same properties set for the Density Volume.

However, if you reference a **Density Volume** through a C# script, a warning appears (**DensityVolume has been deprecated (UnityUpgradable) -> Local Volumetric Fog**) in the Console window. This warning may stop your Project from compiling in future versions of HDRP. To resolve this, change your code to target the new component.

## ClearFlag

HDRP 2021.2 includes the new `ClearFlag.Stencil` function. Use this to clear all flags from a stencil.

From HDRP 2021.2,  `ClearFlag.Depth` does not clear stencils.

### Remove of Receive SSGI flags

From HDRP2021.2, it is no longer required to use the receive SSGI flags to have Emissive compatible with Screen Space Global Illumination and related.
For this reasons the receive SSGI flags have been removed and is no longer available.

## HDRP Global Settings

HDRP 2021.2 introduces a new HDRP Global Settings Asset which saves all settings that are unrelated to which HDRP Asset is active.

The HDRP Asset assigned in the Graphics Settings is no longer the default Asset for HDRP.

To ensure your build uses up to date data, the The HDRP Asset and the HDRP Global Settings Asset can cause a build error if they are not up to date when building.

If both assets are already included in your project (in QualitySettings or in GraphicsSettings), HDRP automatically upgrades the when you open the Unity Editor.

To upgrade these assets manually, include them in your project and build from the command line.

The Runtime Debug Display toggle has moved from the HDRP Asset to HDRP Global Settings Asset. This toggle uses the currently active HDRP Asset as the source.

## Materials

### Transparent Surface Type

From 2021.2, the range for **Sorting Priority** values has decreased from between -100 and 100, to between -50 and 50.

If you used transparent materials (**Surface Type** set to **Transparent**) with a sorting priority lower than -50 or greater than 50, you must remap them to within the new range.

 HDRP does not clamp the Sorting Priority to the new range until you edit the Sorting Priority property.

## RendererList API

From 2021.2, HDRP includes an updated `RendererList` API in the `UnityEngine.Rendering.RendererUtils` namespace. This API performs fewer operations than the previous version of the `RendererList` API when it submits the RendererList for drawing. You can use this new version to query if the list of visible objects is empty.

The previous version of the API in the `UnityEngine.Experimental.Rendering` namespace is still available for compatibility purposes but is now deprecated.

When the **Dynamic Render Pass Culling** option is enabled in the HDRP Global Settings, HDRP will use the new API to dynamically skip certain drawing passes based on the type of currently visible objects. For example if no objects with distortion are drawn, the Render Graph passes that draw the distortion effect (and their dependencies - like the color pyramid generation) will be skipped.

## Dynamic Resolution

From 2021.2, Bilinear and Lanczos upscale filters have been removed as they are mostly redundant with other better options. A project using Bilinear filter will migrate to use Catmull-Rom, if using Lanczos it will migrate to Contrast Adaptive Sharpening (CAS).  If your project was relying on those filters also consider the newly added filters TAA Upscale and FidelityFX Super Resolution 1.0.

## Ambient Mode

From version 12.0 HDRP sets the **Ambient Mode** parameter in the **Visual Environment** volume component to **Dynamic** by default. This might impact existing projects where no default volume profile overrides the **Ambient Mode** parameter. To change this behavior:
1. Add a **Visual Environment** component to the default volume profile.
2. Change the **Ambient Mode** to **Static**.
