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
            return GeometryPoolConstants.GeoPoolVertexByteSize;
        }

        public static int GetIndexByteSize()
        {
            return GeometryPoolConstants.GeoPoolIndexByteSize;
        }

        private int GetFormatByteCount(VertexAttributeFormat format)
        {
            switch (format)
            {
                case VertexAttributeFormat.Float32: return 4;
                case VertexAttributeFormat.Float16: return 2;
                case VertexAttributeFormat.UNorm8: return 1;
                case VertexAttributeFormat.SNorm8: return 1;
                case VertexAttributeFormat.UNorm16: return 2;
                case VertexAttributeFormat.SNorm16: return 2;
                case VertexAttributeFormat.UInt8: return 1;
                case VertexAttributeFormat.SInt8: return 1;
                case VertexAttributeFormat.UInt16: return 2;
                case VertexAttributeFormat.SInt16: return 2;
                case VertexAttributeFormat.UInt32: return 4;
                case VertexAttributeFormat.SInt32: return 4;
            }
            return 4;
        }
    
        private static int DivUp(int x, int y) => (x + y - 1) / y;

        GeometryPoolDesc m_Desc;

        public ComputeBuffer m_VertexPool  = null;

        public Mesh globalMesh = null;
        public GraphicsBuffer globalIndexBuffer { get { return m_globalIndexBuffer;  } }
        public int indicesCount => m_MaxIndexCounts;
        private GraphicsBuffer m_globalIndexBuffer = null;

        private int m_MaxVertCounts;
        private int m_MaxIndexCounts;

        private BlockAllocator m_VertexAllocator;
        private BlockAllocator m_IndexAllocator;

        private NativeHashMap<int, MeshSlot> m_MeshSlots;
        private NativeList<GeometrySlot> m_GeoSlots;
        private NativeList<GeometryPoolHandle> m_FreeGeoSlots;

        private List<GraphicsBuffer> m_InputBufferReferences;

        private int m_UsedGeoSlots;

        private ComputeShader m_GeometryPoolKernelsCS;
        private int m_KernelMainUpdateIndexBuffer16;
        private int m_KernelMainUpdateIndexBuffer32;
        private int m_KernelMainUpdateVertexBuffer;

        private int m_ParamInputIBCount;
        private int m_ParamOutputIBOffset;
        private int m_ParamInputIndexBuffer;
        private int m_ParamOutputIndexBuffer;
        private int m_ParamInputVBCount;
        private int m_ParamOutputVBSize;
        private int m_ParamOutputVBOffset;
        private int m_ParamInputPosBufferStride;
        private int m_ParamInputUv0BufferStride;
        private int m_ParamInputUv1BufferStride;
        private int m_ParamInputNormalBufferStride;
        private int m_ParamInputTangentBufferStride;
        private int m_ParamInputFlags;
        private int m_ParamPosBuffer;
        private int m_ParamUv0Buffer;
        private int m_ParamUv1Buffer;
        private int m_ParamNormalBuffer;
        private int m_ParamTangentBuffer;
        private int m_ParamOutputVB;

        private CommandBuffer m_CmdBuffer;
        private bool m_MustClearCmdBuffer;
        private int m_PendingCmds;

        public GeometryPool(in GeometryPoolDesc desc)
        {
            LoadShaders();

            m_CmdBuffer = new CommandBuffer();
            m_InputBufferReferences = new List<GraphicsBuffer>();
            m_MustClearCmdBuffer = false;
            m_PendingCmds = 0;

            m_Desc = desc;
            m_MaxVertCounts = CalcVertexCount();
            m_MaxIndexCounts = CalcIndexCount();
            m_UsedGeoSlots = 0;

            m_VertexPool = new ComputeBuffer(DivUp(m_MaxVertCounts * GetVertexByteSize(), 4), 4, ComputeBufferType.Raw);

            globalMesh      = new Mesh();
            globalMesh.indexBufferTarget = GraphicsBuffer.Target.Raw;
            globalMesh.SetIndexBufferParams(m_MaxIndexCounts, IndexFormat.UInt32);
            globalMesh.subMeshCount = desc.maxMeshes;            
            globalMesh.vertices = new Vector3[1];
            globalMesh.UploadMeshData(false);
            m_globalIndexBuffer = globalMesh.GetIndexBuffer();            

            Assertions.Assert.IsTrue(m_globalIndexBuffer != null);
            Assertions.Assert.IsTrue((m_globalIndexBuffer.target & GraphicsBuffer.Target.Raw) != 0);

            m_MeshSlots = new NativeHashMap<int, MeshSlot>(desc.maxMeshes, Allocator.Persistent);
            m_GeoSlots = new NativeList<GeometrySlot>(Allocator.Persistent);
            m_FreeGeoSlots = new NativeList<GeometryPoolHandle>(Allocator.Persistent);

            m_VertexAllocator = new BlockAllocator();
            m_VertexAllocator.Initialize(m_MaxVertCounts);

            m_IndexAllocator = new BlockAllocator();
            m_IndexAllocator.Initialize(m_MaxIndexCounts);
        }

        public void DisposeInputBuffers()
        {
            if (m_InputBufferReferences.Count == 0)
                return;

            foreach (var b in m_InputBufferReferences)
                b.Dispose();
            m_InputBufferReferences.Clear();
        }

        public void Dispose()
        {
            m_IndexAllocator.Dispose();
            m_VertexAllocator.Dispose();

            m_FreeGeoSlots.Dispose();
            m_GeoSlots.Dispose();
            m_MeshSlots.Dispose();

            m_VertexPool.Release();
            m_CmdBuffer.Release();

            m_globalIndexBuffer.Dispose();
            CoreUtils.Destroy(globalMesh);            
            globalMesh = null;
            DisposeInputBuffers();
        }

        private void LoadShaders()
        {
            m_GeometryPoolKernelsCS = (ComputeShader)Resources.Load("GeometryPoolKernels");

            m_KernelMainUpdateIndexBuffer16 = m_GeometryPoolKernelsCS.FindKernel("MainUpdateIndexBuffer16");
            m_KernelMainUpdateIndexBuffer32 = m_GeometryPoolKernelsCS.FindKernel("MainUpdateIndexBuffer32");
            m_KernelMainUpdateVertexBuffer = m_GeometryPoolKernelsCS.FindKernel("MainUpdateVertexBuffer");
            m_ParamInputIndexBuffer = Shader.PropertyToID("_InputIndexBuffer");
            m_ParamOutputIndexBuffer = Shader.PropertyToID("_OutputIndexBuffer");
            m_ParamInputIBCount = Shader.PropertyToID("_InputIBCount");
            m_ParamOutputIBOffset = Shader.PropertyToID("_OutputIBOffset");
            m_ParamInputVBCount = Shader.PropertyToID("_InputVBCount");
            m_ParamOutputVBSize = Shader.PropertyToID("_OutputVBSize");
            m_ParamOutputVBOffset = Shader.PropertyToID("_OutputVBOffset");
            m_ParamInputPosBufferStride = Shader.PropertyToID("_InputPosBufferStride");
            m_ParamInputUv0BufferStride = Shader.PropertyToID("_InputUv0BufferStride");
            m_ParamInputUv1BufferStride = Shader.PropertyToID("_InputUv1BufferStride");
            m_ParamInputNormalBufferStride = Shader.PropertyToID("_InputNormalBufferStride");
            m_ParamInputTangentBufferStride = Shader.PropertyToID("_InputTangentBufferStride");
            m_ParamInputFlags = Shader.PropertyToID("_InputFlags");
            m_ParamPosBuffer = Shader.PropertyToID("_PosBuffer");
            m_ParamUv0Buffer = Shader.PropertyToID("_Uv0Buffer");
            m_ParamUv1Buffer = Shader.PropertyToID("_Uv1Buffer");
            m_ParamNormalBuffer = Shader.PropertyToID("_NormalBuffer");
            m_ParamTangentBuffer = Shader.PropertyToID("_TangentBuffer");
            m_ParamOutputVB = Shader.PropertyToID("_OutputVB");
        }

        private int CalcVertexCount() => DivUp(m_Desc.vertexPoolByteSize, GetVertexByteSize());
        private int CalcIndexCount() => DivUp(m_Desc.indexPoolByteSize, GetIndexByteSize());

        private void DeallocateSlot(ref GeometrySlot slot)
        {
            if (slot.vertexAlloc.valid)
            {
                m_VertexAllocator.FreeAllocation(slot.vertexAlloc);
                slot.vertexAlloc = BlockAllocator.Allocation.Invalid;
            }

            if (slot.indexAlloc.valid)
            {
                m_IndexAllocator.FreeAllocation(slot.indexAlloc);
                slot.indexAlloc = BlockAllocator.Allocation.Invalid;
            }
        }

        private bool AllocateGeo(int vertexCount, int indexCount, out GeometryPoolHandle outHandle)
        {
            var newSlot = new GeometrySlot()
            {
                vertexAlloc = BlockAllocator.Allocation.Invalid,
                indexAlloc = BlockAllocator.Allocation.Invalid
            };

            if ((m_UsedGeoSlots + 1) > m_Desc.maxMeshes)
            {
                outHandle = GeometryPoolHandle.Invalid;
                return false;
            }

            newSlot.vertexAlloc = m_VertexAllocator.Allocate(vertexCount);
            if (!newSlot.vertexAlloc.valid)
            {
                outHandle = GeometryPoolHandle.Invalid;
                return false;
            }

            newSlot.indexAlloc = m_IndexAllocator.Allocate(indexCount);
            if (!newSlot.indexAlloc.valid)
            {
                //revert the allocation.
                DeallocateSlot(ref newSlot);
                outHandle = GeometryPoolHandle.Invalid;
                return false;
            }
            
            if (m_FreeGeoSlots.IsEmpty)
            {
                outHandle.index = m_GeoSlots.Length;
                m_GeoSlots.Add(newSlot);
            }
            else
            {
                outHandle = m_FreeGeoSlots[m_FreeGeoSlots.Length - 1];
                m_FreeGeoSlots.RemoveAtSwapBack(m_FreeGeoSlots.Length - 1);
                Assertions.Assert.IsTrue(!m_GeoSlots[outHandle.index].valid);
                m_GeoSlots[outHandle.index] = newSlot;
            }

            ++m_UsedGeoSlots;
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

        private void DeallocateGeo(GeometryPoolHandle handle)
        {
            if (!handle.valid)
                throw new System.Exception("Cannot free invalid geo pool handle");

            --m_UsedGeoSlots;
            m_FreeGeoSlots.Add(handle);
            GeometrySlot slot = m_GeoSlots[handle.index];
            DeallocateSlot(ref slot);
            m_GeoSlots[handle.index] = slot;
        }

        private void UpdateGeoGpuState(Mesh mesh, GeometryPoolHandle handle)
        {
            var geoSlot = m_GeoSlots[handle.index];
            CommandBuffer cmdBuffer = AllocateCommandBuffer(); //clear any previous cmd buffers.

            //Update index buffer
            GraphicsBuffer buffer = LoadIndexBuffer(cmdBuffer, mesh, out var indexBufferFormat);
            Assertions.Assert.IsTrue((buffer.target & GraphicsBuffer.Target.Raw) != 0);
            AddIndexUpdateCommand(cmdBuffer, indexBufferFormat, buffer, geoSlot.indexAlloc, m_globalIndexBuffer);
        }

        public bool Register(Mesh mesh, out GeometryPoolHandle outHandle)
        {
            int meshHashCode = mesh.GetHashCode();
            Assertions.Assert.IsTrue(meshHashCode != -1);
            if (m_MeshSlots.TryGetValue(meshHashCode, out MeshSlot meshSlot))
            {
                Assertions.Assert.IsTrue(meshHashCode == meshSlot.meshHash);
                ++meshSlot.refCount;
                m_MeshSlots[meshSlot.meshHash] = meshSlot;
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
                if (!m_MeshSlots.TryAdd(meshHashCode, newSlot))
                {
                    //revert the allocation.
                    DeallocateGeo(outHandle);
                    outHandle = GeometryPoolHandle.Invalid;
                    return false;
                }

                UpdateGeoGpuState(mesh, outHandle);

                return true;
            }
        }

        public void Unregister(Mesh mesh)
        {
            int meshHashCode = mesh.GetHashCode();
            if (!m_MeshSlots.TryGetValue(meshHashCode, out MeshSlot outSlot))
                return;

            --outSlot.refCount;
            if (outSlot.refCount == 0)
            {
                m_MeshSlots.Remove(meshHashCode);
                DeallocateGeo(outSlot.geometryHandle);
            }
            else
                m_MeshSlots[meshHashCode] = outSlot;
        }

        public GeometryPoolHandle GetHandle(Mesh mesh)
        {
            int meshHashCode = mesh.GetHashCode();
            if (!m_MeshSlots.TryGetValue(meshHashCode, out MeshSlot outSlot))
                return GeometryPoolHandle.Invalid;

            return outSlot.geometryHandle;
        }

        public void SendGpuCommands()
        {
            if (m_PendingCmds != 0)
            {
                Graphics.ExecuteCommandBuffer(m_CmdBuffer);
                m_MustClearCmdBuffer = true;
                m_PendingCmds = 0;
            }

            DisposeInputBuffers();
        }

        public BlockAllocator.Allocation GetIndexBufferBlock(GeometryPoolHandle handle)
        {
            if (handle.index < 0 || handle.index >= m_GeoSlots.Length)
                throw new System.Exception("Handle utilized is invalid");

            return m_GeoSlots[handle.index].indexAlloc;
        }

        private GraphicsBuffer LoadIndexBuffer(CommandBuffer cmdBuffer, Mesh mesh, out IndexFormat fmt)
        {
            if ((mesh.indexBufferTarget & GraphicsBuffer.Target.Raw) != 0)
            {
                fmt = mesh.indexFormat;
                var idxBuffer = mesh.GetIndexBuffer();
                m_InputBufferReferences.Add(idxBuffer);
                return mesh.GetIndexBuffer();
            }
            else
            {
                fmt = IndexFormat.UInt32;

                int indexCount = 0;
                for (int i = 0; i < (int)mesh.subMeshCount; ++i)
                    indexCount += (int)mesh.GetIndexCount(i);

                var idxBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Index | GraphicsBuffer.Target.Raw, indexCount, 4);
                m_InputBufferReferences.Add(idxBuffer);

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
            if (m_MustClearCmdBuffer)
            {
                m_CmdBuffer.Clear();
                m_MustClearCmdBuffer = false;
            }

            ++m_PendingCmds;
            return m_CmdBuffer;
        }

        private void AddIndexUpdateCommand(CommandBuffer cmdBuffer, IndexFormat inputFormat, in GraphicsBuffer inputBuffer, in BlockAllocator.Allocation location, GraphicsBuffer outputBuffer)
        {
            cmdBuffer.SetComputeIntParam(m_GeometryPoolKernelsCS, m_ParamInputIBCount, location.block.count);
            cmdBuffer.SetComputeIntParam(m_GeometryPoolKernelsCS, m_ParamOutputIBOffset, location.block.offset);
            int kernel = inputFormat == IndexFormat.UInt16 ? m_KernelMainUpdateIndexBuffer16 : m_KernelMainUpdateIndexBuffer32;
            cmdBuffer.SetComputeBufferParam(m_GeometryPoolKernelsCS, kernel, m_ParamInputIndexBuffer, inputBuffer);
            cmdBuffer.SetComputeBufferParam(m_GeometryPoolKernelsCS, kernel, m_ParamOutputIndexBuffer, outputBuffer);
            int groupCountsX = DivUp(location.block.count, 64);
            cmdBuffer.DispatchCompute(m_GeometryPoolKernelsCS, kernel, groupCountsX, 1, 1);
        }

        private void AddVertexUpdateCommand(
            CommandBuffer cmdBuffer,
            in GraphicsBuffer p, in GraphicsBuffer uv0, in GraphicsBuffer uv1, in GraphicsBuffer n, in GraphicsBuffer t,
            int posStride, int uv0Stride, int uv1Stride, int normalStride, int tangentStride,
            in BlockAllocator.Allocation location,
            GraphicsBuffer outputVertexBuffer)
        {
            
            GeoPoolInputFlags flags =
                (uv1 != null ? GeoPoolInputFlags.HasUV1 : GeoPoolInputFlags.None)
              | (t != null ? GeoPoolInputFlags.HasTangent : GeoPoolInputFlags.None); 

            cmdBuffer.SetComputeIntParam(m_GeometryPoolKernelsCS, m_ParamInputVBCount, location.block.count);
            cmdBuffer.SetComputeIntParam(m_GeometryPoolKernelsCS, m_ParamOutputVBSize, m_MaxVertCounts);
            cmdBuffer.SetComputeIntParam(m_GeometryPoolKernelsCS, m_ParamOutputVBOffset, location.block.offset);
            cmdBuffer.SetComputeIntParam(m_GeometryPoolKernelsCS, m_ParamInputPosBufferStride, posStride);
            cmdBuffer.SetComputeIntParam(m_GeometryPoolKernelsCS, m_ParamInputUv0BufferStride, uv0Stride);
            cmdBuffer.SetComputeIntParam(m_GeometryPoolKernelsCS, m_ParamInputUv1BufferStride, uv1Stride);
            cmdBuffer.SetComputeIntParam(m_GeometryPoolKernelsCS, m_ParamInputNormalBufferStride, normalStride);
            cmdBuffer.SetComputeIntParam(m_GeometryPoolKernelsCS, m_ParamInputTangentBufferStride, tangentStride);
            cmdBuffer.SetComputeIntParam(m_GeometryPoolKernelsCS, m_ParamInputFlags, (int)flags);

            int kernel = m_KernelMainUpdateVertexBuffer;
            cmdBuffer.SetComputeBufferParam(m_GeometryPoolKernelsCS, kernel, m_ParamPosBuffer, p);
            cmdBuffer.SetComputeBufferParam(m_GeometryPoolKernelsCS, kernel, m_ParamUv0Buffer, uv0);
            if (uv1 != null)
                cmdBuffer.SetComputeBufferParam(m_GeometryPoolKernelsCS, kernel, m_ParamUv1Buffer, uv1);
            cmdBuffer.SetComputeBufferParam(m_GeometryPoolKernelsCS, kernel, m_ParamNormalBuffer, n);
            if (t != null)
                cmdBuffer.SetComputeBufferParam(m_GeometryPoolKernelsCS, kernel, m_ParamTangentBuffer, t);

            cmdBuffer.SetComputeBufferParam(m_GeometryPoolKernelsCS, kernel, m_ParamOutputVB, outputVertexBuffer);

            int groupCountsX = DivUp(location.block.count, 64);
            cmdBuffer.DispatchCompute(m_GeometryPoolKernelsCS, kernel, groupCountsX, 1, 1);
        }
    }
}
