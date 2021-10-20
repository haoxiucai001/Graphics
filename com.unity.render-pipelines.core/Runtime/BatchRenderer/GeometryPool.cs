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
        public GraphicsBuffer globalIndexBuffer { get { return m_globalIndexBuffer;  } }
        public int indicesCount => m_maxIndexCounts;
        private GraphicsBuffer m_globalIndexBuffer = null;

        private int m_maxVertCounts;
        private int m_maxIndexCounts;

        private BlockAllocator m_vertexAllocator;
        private BlockAllocator m_indexAllocator;

        private NativeHashMap<int, MeshSlot> m_meshSlots;
        private NativeList<GeometrySlot> m_geoSlots;
        private NativeList<GeometryPoolHandle> m_freeGeoSlots;

        private List<GraphicsBuffer> m_inputBufferReferences;

        private int m_usedGeoSlots;

        private ComputeShader m_geometryPoolKernelsCS;
        private int m_kernelMainUpdateIndexBuffer16;
        private int m_kernelMainUpdateIndexBuffer32;
        private int m_paramInputIBCount;
        private int m_paramOutputIBOffset;
        private int m_paramInputIndexBuffer;
        private int m_paramOutputIndexBuffer;

        private CommandBuffer m_cmdBuffer;
        private bool m_mustClearCmdBuffer;
        private int m_pendingCmds;

        public GeometryPool(in GeometryPoolDesc desc)
        {
            LoadShaders();

            m_cmdBuffer = new CommandBuffer();
            m_inputBufferReferences = new List<GraphicsBuffer>();
            m_mustClearCmdBuffer = false;
            m_pendingCmds = 0;

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
            m_globalIndexBuffer = globalMesh.GetIndexBuffer();            

            Assertions.Assert.IsTrue(m_globalIndexBuffer != null);
            Assertions.Assert.IsTrue((m_globalIndexBuffer.target & GraphicsBuffer.Target.Raw) != 0);

            m_meshSlots = new NativeHashMap<int, MeshSlot>(desc.maxMeshes, Allocator.Persistent);
            m_geoSlots = new NativeList<GeometrySlot>(Allocator.Persistent);
            m_freeGeoSlots = new NativeList<GeometryPoolHandle>(Allocator.Persistent);

            m_vertexAllocator = new BlockAllocator();
            m_vertexAllocator.Initialize(m_maxVertCounts);

            m_indexAllocator = new BlockAllocator();
            m_indexAllocator.Initialize(m_maxIndexCounts);

        }

        public void DisposeInputBuffers()
        {
            if (m_inputBufferReferences.Count == 0)
                return;

            foreach (var b in m_inputBufferReferences)
                b.Dispose();
            m_inputBufferReferences.Clear();
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
            m_cmdBuffer.Release();

            m_globalIndexBuffer.Dispose();
            CoreUtils.Destroy(globalMesh);            
            globalMesh = null;
            DisposeInputBuffers();
        }

        private void LoadShaders()
        {
            m_geometryPoolKernelsCS = (ComputeShader)Resources.Load("GeometryPoolKernels");
            m_kernelMainUpdateIndexBuffer16 = m_geometryPoolKernelsCS.FindKernel("MainUpdateIndexBuffer16");
            m_kernelMainUpdateIndexBuffer32 = m_geometryPoolKernelsCS.FindKernel("MainUpdateIndexBuffer32");
            m_paramInputIndexBuffer = Shader.PropertyToID("_InputIndexBuffer");
            m_paramOutputIndexBuffer = Shader.PropertyToID("_OutputIndexBuffer");
            m_paramInputIBCount = Shader.PropertyToID("_InputIBCount");
            m_paramOutputIBOffset = Shader.PropertyToID("_OutputIBOffset");
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

                var geoSlot = m_geoSlots[outHandle.index];

                CommandBuffer cmdBuffer = AllocateCommandBuffer(); //clear any previous cmd buffers.
                GraphicsBuffer buffer = LoadIndexBuffer(cmdBuffer, mesh, out var indexBufferFormat);
                Assertions.Assert.IsTrue((buffer.target & GraphicsBuffer.Target.Raw) != 0);
                AddIndexUpdateCommand(cmdBuffer, indexBufferFormat, buffer, geoSlot.indexAlloc, m_globalIndexBuffer);

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

        public void SendGpuCommands()
        {
            if (m_pendingCmds != 0)
            {
                Graphics.ExecuteCommandBuffer(m_cmdBuffer);
                m_mustClearCmdBuffer = true;
                m_pendingCmds = 0;
            }

            DisposeInputBuffers();
        }

        public BlockAllocator.Allocation GetIndexBufferBlock(GeometryPoolHandle handle)
        {
            if (handle.index < 0 || handle.index >= m_geoSlots.Length)
                throw new System.Exception("Handle utilized is invalid");

            return m_geoSlots[handle.index].indexAlloc;
        }

        private GraphicsBuffer LoadIndexBuffer(CommandBuffer cmdBuffer, Mesh mesh, out IndexFormat fmt)
        {
            if ((mesh.indexBufferTarget & GraphicsBuffer.Target.Raw) != 0)
            {
                fmt = mesh.indexFormat;
                var idxBuffer = mesh.GetIndexBuffer();
                m_inputBufferReferences.Add(idxBuffer);
                return mesh.GetIndexBuffer();
            }
            else
            {
                fmt = IndexFormat.UInt32;

                int indexCount = 0;
                for (int i = 0; i < (int)mesh.subMeshCount; ++i)
                    indexCount += (int)mesh.GetIndexCount(i);

                var idxBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Index | GraphicsBuffer.Target.Raw, indexCount, 4);
                m_inputBufferReferences.Add(idxBuffer);

                int indexOffset = 0;

                for (int i = 0; i < (int)mesh.subMeshCount; ++i)
                {
                    int currentIndexCount = (int)mesh.GetIndexCount(i);
                    cmdBuffer.SetBufferData(idxBuffer, mesh.GetIndices(i), 0, indexOffset, currentIndexCount);
                    indexOffset += currentIndexCount;
                }

                return idxBuffer;
            }
        }

        private CommandBuffer AllocateCommandBuffer()
        {
            if (m_mustClearCmdBuffer)
            {
                m_cmdBuffer.Clear();
                m_mustClearCmdBuffer = false;
            }

            ++m_pendingCmds;
            return m_cmdBuffer;
        }

        private void AddIndexUpdateCommand(CommandBuffer cmdBuffer, IndexFormat inputFormat, in GraphicsBuffer inputBuffer, in BlockAllocator.Allocation location, GraphicsBuffer outputBuffer)
        {
            cmdBuffer.SetComputeIntParam(m_geometryPoolKernelsCS, m_paramInputIBCount, location.block.count);
            cmdBuffer.SetComputeIntParam(m_geometryPoolKernelsCS, m_paramOutputIBOffset, location.block.offset);
            int kernel = inputFormat == IndexFormat.UInt16 ? m_kernelMainUpdateIndexBuffer16 : m_kernelMainUpdateIndexBuffer32;
            cmdBuffer.SetComputeBufferParam(m_geometryPoolKernelsCS, kernel, m_paramInputIndexBuffer, inputBuffer);
            cmdBuffer.SetComputeBufferParam(m_geometryPoolKernelsCS, kernel, m_paramOutputIndexBuffer, outputBuffer);
            int groupCountsX = DivUp(location.block.count, 64);
            cmdBuffer.DispatchCompute(m_geometryPoolKernelsCS, kernel, groupCountsX, 1, 1);
        }
    }
}
