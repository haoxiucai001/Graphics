using System.Collections.Generic;
using Unity.Collections;
using UnityEngine.VFX;
using System.Diagnostics;
using System.Linq;
using UnityEngine.Experimental.GlobalIllumination;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Experimental.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RendererUtils;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityEngine.Rendering
{
    public struct GeometryPoolDesc
    {
        public int vertexPoolByteSize;
        public int indexPoolByteSize;
        public int maxMeshes;

        public static GeometryPoolDesc NewDefault()
        {
            return new GeometryPoolDesc()
            {
                vertexPoolByteSize = 32 * 1024 * 1024, //32 mb
                indexPoolByteSize = 16 * 1024 * 1024, //16 mb
                maxMeshes = 4096
            }; 
        }
    }

    public struct GeometryPoolHandle
    {
        public int index;
        public static GeometryPoolHandle Invalid = new GeometryPoolHandle() { index = -1 };
        public bool valid => index != -1;
    }

    public class GeometryPool
    {
        private struct MeshSlot
        {
            public int refCount;
            public int meshHash;
            public GeometryPoolHandle geometryHandle;
        }

        private struct GeometrySlot
        {
            public BlockAllocator.Allocation vertexAlloc;
            public BlockAllocator.Allocation indexAlloc;

            public static GeometrySlot Invalid = new GeometrySlot()
            {
                vertexAlloc = BlockAllocator.Allocation.Invalid,
                indexAlloc = BlockAllocator.Allocation.Invalid
            };

            public bool valid => vertexAlloc.valid && indexAlloc.valid;
        }

        public static int GetVertexByteSize()
        {
            return System.Runtime.InteropServices.Marshal.SizeOf<Vector3>() + /*pos*/
                   System.Runtime.InteropServices.Marshal.SizeOf<Vector2>() + /*uv0*/
                   System.Runtime.InteropServices.Marshal.SizeOf<Vector2>() + /*uv1*/
                   System.Runtime.InteropServices.Marshal.SizeOf<Vector3>() + /*N*/
                   System.Runtime.InteropServices.Marshal.SizeOf<Vector3>();  /*T*/
        }

        public static int GetIndexByteSize()
        {
            return System.Runtime.InteropServices.Marshal.SizeOf<int>();
        }

        GeometryPoolDesc m_desc;

        public ComputeBuffer m_vertexPoolP   = null;
        public ComputeBuffer m_vertexPoolUV  = null;
        public ComputeBuffer m_vertexPoolUV1 = null;
        public ComputeBuffer m_vertexPoolN   = null;
        public ComputeBuffer m_vertexPoolT   = null;

        public Mesh globalMesh = null;

        private int m_maxVertCounts;
        private int m_maxIndexCounts;

        private BlockAllocator m_vertexAllocator;
        private BlockAllocator m_indexAllocator;

        private NativeHashMap<int, MeshSlot> m_meshSlots;
        private NativeList<GeometrySlot> m_geoSlots;
        private NativeList<GeometryPoolHandle> m_freeGeoSlots;

        private int m_usedGeoSlots;

        public GeometryPool(in GeometryPoolDesc desc)
        {
            m_desc = desc;
            m_maxVertCounts = CalcVertexCount();
            m_maxIndexCounts = CalcIndexCount();
            m_usedGeoSlots = 0;

            m_vertexPoolP   = new ComputeBuffer(m_maxVertCounts, System.Runtime.InteropServices.Marshal.SizeOf<Vector3>());
            m_vertexPoolUV  = new ComputeBuffer(m_maxVertCounts, System.Runtime.InteropServices.Marshal.SizeOf<Vector2>());
            m_vertexPoolUV1 = new ComputeBuffer(m_maxVertCounts, System.Runtime.InteropServices.Marshal.SizeOf<Vector2>());
            m_vertexPoolN   = new ComputeBuffer(m_maxVertCounts, System.Runtime.InteropServices.Marshal.SizeOf<Vector3>());
            m_vertexPoolT   = new ComputeBuffer(m_maxVertCounts, System.Runtime.InteropServices.Marshal.SizeOf<Vector3>());

            globalMesh      = new Mesh();
            globalMesh.indexBufferTarget = GraphicsBuffer.Target.Raw;
            globalMesh.SetIndexBufferParams(m_maxIndexCounts, IndexFormat.UInt32);
            globalMesh.subMeshCount = desc.maxMeshes;            
            globalMesh.vertices = new Vector3[1];
            globalMesh.UploadMeshData(false);

            Assertions.Assert.IsTrue(globalMesh.GetIndexBuffer() != null);
            var ib = globalMesh.GetIndexBuffer();
            Assertions.Assert.IsTrue((ib.target & GraphicsBuffer.Target.Raw) != 0);

            m_meshSlots = new NativeHashMap<int, MeshSlot>(desc.maxMeshes, Allocator.Persistent);
            m_geoSlots = new NativeList<GeometrySlot>(Allocator.Persistent);
            m_freeGeoSlots = new NativeList<GeometryPoolHandle>(Allocator.Persistent);

            m_vertexAllocator = new BlockAllocator();
            m_vertexAllocator.Initialize(m_maxVertCounts);

            m_indexAllocator = new BlockAllocator();
            m_indexAllocator.Initialize(m_maxIndexCounts);

        }

        public void Dispose()
        {
            m_indexAllocator.Dispose();
            m_vertexAllocator.Dispose();

            m_freeGeoSlots.Dispose();
            m_geoSlots.Dispose();
            m_meshSlots.Dispose();

            m_vertexPoolT.Release();
            m_vertexPoolN.Release();
            m_vertexPoolUV1.Release();
            m_vertexPoolUV.Release();
            m_vertexPoolP.Release();

            CoreUtils.Destroy(globalMesh);
            globalMesh = null;
        }

        private static int DivUp(int x, int y) => (x + y - 1) / y;

        private int CalcVertexCount() => DivUp(m_desc.vertexPoolByteSize, GetVertexByteSize());
        private int CalcIndexCount() => DivUp(m_desc.indexPoolByteSize, GetIndexByteSize());

        private void DeallocateSlot(ref GeometrySlot slot)
        {
            if (slot.vertexAlloc.valid)
            {
                m_vertexAllocator.FreeAllocation(slot.vertexAlloc);
                slot.vertexAlloc = BlockAllocator.Allocation.Invalid;
            }

            if (slot.indexAlloc.valid)
            {
                m_indexAllocator.FreeAllocation(slot.indexAlloc);
                slot.indexAlloc = BlockAllocator.Allocation.Invalid;
            }
        }

        public bool AllocateGeo(int vertexCount, int indexCount, out GeometryPoolHandle outHandle)
        {
            var newSlot = new GeometrySlot()
            {
                vertexAlloc = BlockAllocator.Allocation.Invalid,
                indexAlloc = BlockAllocator.Allocation.Invalid
            };

            if ((m_usedGeoSlots + 1) > m_desc.maxMeshes)
            {
                outHandle = GeometryPoolHandle.Invalid;
                return false;
            }

            newSlot.vertexAlloc = m_vertexAllocator.Allocate(vertexCount);
            if (!newSlot.vertexAlloc.valid)
            {
                outHandle = GeometryPoolHandle.Invalid;
                return false;
            }

            newSlot.indexAlloc = m_indexAllocator.Allocate(indexCount);
            if (!newSlot.indexAlloc.valid)
            {
                //revert the allocation.
                DeallocateSlot(ref newSlot);
                outHandle = GeometryPoolHandle.Invalid;
                return false;
            }
            
            if (m_freeGeoSlots.IsEmpty)
            {
                outHandle.index = m_geoSlots.Length;
                m_geoSlots.Add(newSlot);
            }
            else
            {
                outHandle = m_freeGeoSlots[m_freeGeoSlots.Length - 1];
                m_freeGeoSlots.RemoveAtSwapBack(m_freeGeoSlots.Length - 1);
                Assertions.Assert.IsTrue(!m_geoSlots[outHandle.index].valid);
                m_geoSlots[outHandle.index] = newSlot;
            }

            ++m_usedGeoSlots;
            var descriptor = new SubMeshDescriptor();
            descriptor.baseVertex = 0;
            descriptor.firstVertex = 0;
            descriptor.indexCount = newSlot.indexAlloc.block.count;
            descriptor.indexStart = newSlot.indexAlloc.block.offset;
            descriptor.topology = MeshTopology.Triangles;
            descriptor.vertexCount = 1;
            globalMesh.SetSubMesh(outHandle.index, descriptor, MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontRecalculateBounds);
            return true;
        }

        public void DeallocateGeo(GeometryPoolHandle handle)
        {
            if (!handle.valid)
                throw new System.Exception("Cannot free invalid geo pool handle");

            --m_usedGeoSlots;
            m_freeGeoSlots.Add(handle);
            GeometrySlot slot = m_geoSlots[handle.index];
            DeallocateSlot(ref slot);
            m_geoSlots[handle.index] = slot;
        }

        public bool Register(Mesh mesh, out GeometryPoolHandle outHandle)
        {
            int meshHashCode = mesh.GetHashCode();
            Assertions.Assert.IsTrue(meshHashCode != -1);
            if (m_meshSlots.TryGetValue(meshHashCode, out MeshSlot meshSlot))
            {
                Assertions.Assert.IsTrue(meshHashCode == meshSlot.meshHash);
                ++meshSlot.refCount;
                m_meshSlots[meshSlot.meshHash] = meshSlot;
                outHandle = meshSlot.geometryHandle;
                return true;
            }
            else
            {
                var newSlot = new MeshSlot()
                {
                    refCount = 1,
                    meshHash = meshHashCode,
                };

                int indexCount = 0;
                for (int i = 0; i < (int)mesh.subMeshCount; ++i)
                    indexCount += (int)mesh.GetIndexCount(i);

                if (!AllocateGeo(mesh.vertexCount, indexCount, out outHandle))
                    return false;

                newSlot.geometryHandle = outHandle;
                if (!m_meshSlots.TryAdd(meshHashCode, newSlot))
                {
                    //revert the allocation.
                    DeallocateGeo(outHandle);
                    outHandle = GeometryPoolHandle.Invalid;
                    return false;
                }

                return true;
            }
        }

        public void Unregister(Mesh mesh)
        {
            int meshHashCode = mesh.GetHashCode();
            if (!m_meshSlots.TryGetValue(meshHashCode, out MeshSlot outSlot))
                return;

            --outSlot.refCount;
            if (outSlot.refCount == 0)
            {
                m_meshSlots.Remove(meshHashCode);
                DeallocateGeo(outSlot.geometryHandle);
            }
            else
                m_meshSlots[meshHashCode] = outSlot;
        }

        public GeometryPoolHandle GetHandle(Mesh mesh)
        {
            int meshHashCode = mesh.GetHashCode();
            if (!m_meshSlots.TryGetValue(meshHashCode, out MeshSlot outSlot))
                return GeometryPoolHandle.Invalid;

            return outSlot.geometryHandle;
        }
    }
}
