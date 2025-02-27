using System;
using UnityEngine.Serialization;
using UnityEditor.Experimental;
using Unity.Collections;
using System.Collections.Generic;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UnityEngine.Experimental.Rendering
{
    /// <summary>
    /// A marker to determine what area of the scene is considered by the Probe Volumes system
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Light/Probe Volume (Experimental)")]
    public class ProbeVolume : MonoBehaviour
    {
        public bool globalVolume = false;
        public Vector3 size = new Vector3(10, 10, 10);
        [HideInInspector, Range(0f, 2f)]
        public float geometryDistanceOffset = 0.2f;

        public LayerMask objectLayerMask = -1;


        [HideInInspector]
        public int lowestSubdivLevelOverride = 0;
        [HideInInspector]
        public int highestSubdivLevelOverride = -1;
        [HideInInspector]
        public bool overridesSubdivLevels = false;

        [SerializeField] internal bool mightNeedRebaking = false;

        [SerializeField] internal Matrix4x4 cachedTransform;
        [SerializeField] internal int cachedHashCode;

        /// <summary>
        /// Returns the extents of the volume.
        /// </summary>
        /// <returns>The extents of the ProbeVolume.</returns>
        public Vector3 GetExtents()
        {
            return size;
        }

#if UNITY_EDITOR
        internal void UpdateGlobalVolume(Scene scene)
        {
            if (gameObject.scene != scene) return;

            Bounds bounds = new Bounds();
            bool foundABound = false;
            bool ContributesToGI(Renderer renderer)
            {
                var flags = GameObjectUtility.GetStaticEditorFlags(renderer.gameObject) & StaticEditorFlags.ContributeGI;
                return (flags & StaticEditorFlags.ContributeGI) != 0;
            }

            void ExpandBounds(Bounds currBound)
            {
                if (!foundABound)
                {
                    bounds = currBound;
                    foundABound = true;
                }
                else
                {
                    bounds.Encapsulate(currBound);
                }
            }

            var renderers = UnityEngine.GameObject.FindObjectsOfType<Renderer>();

            foreach (Renderer renderer in renderers)
            {
                bool contributeGI = ContributesToGI(renderer) && renderer.gameObject.activeInHierarchy && renderer.enabled;

                if (contributeGI && renderer.gameObject.scene == scene)
                {
                    ExpandBounds(renderer.bounds);
                }
            }

            transform.position = bounds.center;

            float minBrickSize = ProbeReferenceVolume.instance.MinBrickSize();
            Vector3 tmpClamp = (bounds.size + new Vector3(minBrickSize, minBrickSize, minBrickSize));
            tmpClamp.x = Mathf.Max(0f, tmpClamp.x);
            tmpClamp.y = Mathf.Max(0f, tmpClamp.y);
            tmpClamp.z = Mathf.Max(0f, tmpClamp.z);
            size = tmpClamp;
        }

        internal void OnLightingDataAssetCleared()
        {
            mightNeedRebaking = true;
        }

        internal void OnBakeCompleted()
        {
            // We cache the data of last bake completed.
            cachedTransform = gameObject.transform.worldToLocalMatrix;
            cachedHashCode = GetHashCode();
            mightNeedRebaking = false;
        }

        public override int GetHashCode()
        {
            int hash = 17;

            unchecked
            {
                hash = hash * 23 + size.GetHashCode();
                hash = hash * 23 + overridesSubdivLevels.GetHashCode();
                hash = hash * 23 + highestSubdivLevelOverride.GetHashCode();
                hash = hash * 23 + lowestSubdivLevelOverride.GetHashCode();
                hash = hash * 23 + geometryDistanceOffset.GetHashCode();
                hash = hash * 23 + objectLayerMask.GetHashCode();
            }

            return hash;
        }

#endif

        internal float GetMinSubdivMultiplier()
        {
            float maxSubdiv = ProbeReferenceVolume.instance.GetMaxSubdivision() - 1;
            return overridesSubdivLevels ? Mathf.Clamp(lowestSubdivLevelOverride / maxSubdiv, 0.0f, 1.0f) : 0.0f;
        }

        internal float GetMaxSubdivMultiplier()
        {
            float maxSubdiv = ProbeReferenceVolume.instance.GetMaxSubdivision() - 1;
            return overridesSubdivLevels ? Mathf.Clamp(highestSubdivLevelOverride / maxSubdiv, 0.0f, 1.0f) : 1.0f;
        }

        // Momentarily moving the gizmo rendering for bricks and cells to Probe Volume itself,
        // only the first probe volume in the scene will render them. The reason is that we dont have any
        // other non-hidden component related to APV.
        #region APVGizmo

        MeshGizmo brickGizmos;
        MeshGizmo cellGizmo;

        void DisposeGizmos()
        {
            brickGizmos?.Dispose();
            brickGizmos = null;
            cellGizmo?.Dispose();
            cellGizmo = null;
        }

        void OnDestroy()
        {
            DisposeGizmos();
        }

        void OnDisable()
        {
            DisposeGizmos();
        }
#if UNITY_EDITOR

        // Only the first PV of the available ones will draw gizmos.
        bool IsResponsibleToDrawGizmo()
        {
            var pvList = GameObject.FindObjectsOfType<ProbeVolume>();
            return this == pvList[0];
        }

        internal bool ShouldCullCell(Vector3 cellPosition, Vector3 originWS = default(Vector3))
        {
            var cellSizeInMeters = ProbeReferenceVolume.instance.MaxBrickSize();
            var debugDisplay = ProbeReferenceVolume.instance.debugDisplay;
            if (debugDisplay.realtimeSubdivision)
            {
                var profile = ProbeReferenceVolume.instance.sceneData.GetProfileForScene(gameObject.scene);
                if (profile == null)
                    return true;
                cellSizeInMeters = profile.cellSizeInMeters;
            }

            var cameraTransform = SceneView.lastActiveSceneView.camera.transform;

            Vector3 cellCenterWS = cellPosition * cellSizeInMeters + originWS + Vector3.one * (cellSizeInMeters / 2.0f);

            // Round down to cell size distance
            float roundedDownDist = Mathf.Floor(Vector3.Distance(cameraTransform.position, cellCenterWS) / cellSizeInMeters) * cellSizeInMeters;

            if (roundedDownDist > ProbeReferenceVolume.instance.debugDisplay.subdivisionViewCullingDistance)
                return true;

            var frustumPlanes = GeometryUtility.CalculateFrustumPlanes(SceneView.lastActiveSceneView.camera);
            var volumeAABB = new Bounds(cellCenterWS, cellSizeInMeters * Vector3.one);

            return !GeometryUtility.TestPlanesAABB(frustumPlanes, volumeAABB);
        }

        // TODO: We need to get rid of Handles.DrawWireCube to be able to have those at runtime as well.
        void OnDrawGizmos()
        {
            if (!ProbeReferenceVolume.instance.isInitialized || !IsResponsibleToDrawGizmo() || ProbeReferenceVolume.instance.sceneData == null)
                return;

            var debugDisplay = ProbeReferenceVolume.instance.debugDisplay;

            var cellSizeInMeters = ProbeReferenceVolume.instance.MaxBrickSize();
            if (debugDisplay.realtimeSubdivision)
            {
                var profile = ProbeReferenceVolume.instance.sceneData.GetProfileForScene(gameObject.scene);
                if (profile == null)
                    return;
                cellSizeInMeters = profile.cellSizeInMeters;
            }


            if (debugDisplay.drawBricks)
            {
                var subdivColors = ProbeReferenceVolume.instance.subdivisionDebugColors;

                IEnumerable<ProbeBrickIndex.Brick> GetVisibleBricks()
                {
                    if (debugDisplay.realtimeSubdivision)
                    {
                        // realtime subdiv cells are already culled
                        foreach (var kp in ProbeReferenceVolume.instance.realtimeSubdivisionInfo)
                        {
                            var cellVolume = kp.Key;

                            foreach (var brick in kp.Value)
                            {
                                yield return brick;
                            }
                        }
                    }
                    else
                    {
                        foreach (var cellInfo in ProbeReferenceVolume.instance.cells.Values)
                        {
                            if (!cellInfo.loaded)
                                continue;

                            if (ShouldCullCell(cellInfo.cell.position, ProbeReferenceVolume.instance.GetTransform().posWS))
                                continue;

                            if (cellInfo.cell.bricks == null)
                                continue;

                            foreach (var brick in cellInfo.cell.bricks)
                                yield return brick;
                        }
                    }
                }

                if (brickGizmos == null)
                    brickGizmos = new MeshGizmo((int)(Mathf.Pow(3, ProbeBrickIndex.kMaxSubdivisionLevels) * MeshGizmo.vertexCountPerCube));

                brickGizmos.Clear();
                foreach (var brick in GetVisibleBricks())
                {
                    if (brick.subdivisionLevel < 0)
                        continue;

                    float brickSize = ProbeReferenceVolume.instance.BrickSize(brick.subdivisionLevel);
                    float minBrickSize = ProbeReferenceVolume.instance.MinBrickSize();
                    Vector3 scaledSize = new Vector3(brickSize, brickSize, brickSize);
                    Vector3 scaledPos = new Vector3(brick.position.x * minBrickSize, brick.position.y * minBrickSize, brick.position.z * minBrickSize) + scaledSize / 2;
                    brickGizmos.AddWireCube(scaledPos, scaledSize, subdivColors[brick.subdivisionLevel]);
                }

                brickGizmos.RenderWireframe(Matrix4x4.identity, gizmoName: "Brick Gizmo Rendering");
            }

            if (debugDisplay.drawCells)
            {
                IEnumerable<Vector4> GetVisibleCellCentersAndState()
                {
                    if (debugDisplay.realtimeSubdivision)
                    {
                        foreach (var kp in ProbeReferenceVolume.instance.realtimeSubdivisionInfo)
                        {
                            kp.Key.CalculateCenterAndSize(out var center, out var _);
                            yield return new Vector4(center.x, center.y, center.z, 1.0f);
                        }
                    }
                    else
                    {
                        foreach (var cellInfo in ProbeReferenceVolume.instance.cells.Values)
                        {
                            if (ShouldCullCell(cellInfo.cell.position, ProbeReferenceVolume.instance.GetTransform().posWS))
                                continue;

                            var cell = cellInfo.cell;
                            var positionF = new Vector4(cell.position.x, cell.position.y, cell.position.z, 0.0f);
                            var center = positionF * cellSizeInMeters + cellSizeInMeters * 0.5f * Vector4.one;
                            center.w = cellInfo.loaded ? 1.0f : 0.0f;
                            yield return center;
                        }
                    }
                }

                Matrix4x4 trs = Matrix4x4.TRS(ProbeReferenceVolume.instance.GetTransform().posWS, ProbeReferenceVolume.instance.GetTransform().rot, Vector3.one);

                // For realtime subdivision, the matrix from ProbeReferenceVolume.instance can be wrong if the profile changed since the last bake
                if (debugDisplay.realtimeSubdivision)
                    trs = Matrix4x4.TRS(transform.position, Quaternion.identity, Vector3.one);

                if (cellGizmo == null)
                    cellGizmo = new MeshGizmo();
                cellGizmo.Clear();
                foreach (var center in GetVisibleCellCentersAndState())
                {
                    bool loaded = center.w == 1.0f;

                    Gizmos.color = loaded ? new Color(0, 1, 0.5f, 0.2f) : new Color(1, 0.0f, 0.0f, 0.2f);
                    Gizmos.matrix = trs;

                    Gizmos.DrawCube(center, Vector3.one * cellSizeInMeters);
                    cellGizmo.AddWireCube(center, Vector3.one * cellSizeInMeters, loaded ? new Color(0, 1, 0.5f, 1) : new Color(1, 0.0f, 0.0f, 1));
                }
                cellGizmo.RenderWireframe(Gizmos.matrix, gizmoName: "Brick Gizmo Rendering");
            }
        }

#endif
        #endregion
    }
} // UnityEngine.Experimental.Rendering.HDPipeline
