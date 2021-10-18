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

        static GeometryPoolDesc NewDefault()
        {
            return new GeometryPoolDesc()
            {
                vertexPoolByteSize = 32 * 1024 * 1024, //32 mb
                indexPoolByteSize = 16 * 1024 * 1024 //16 mb
            }; 
        }
    }

    internal struct GeometryPoolLinearAllocator
    {
        public struct Block
        {
            public int offset;
            public int count;
        }

        public struct Allocation
        {
            public int handle;
            public Block block;

            public static Allocation Invalid = new Allocation() { handle = -1 };
            public bool Valid() => handle != -1;
        }

        private int m_freeElementCount;
        private NativeList<Block> m_freeBlocks;

        public void Initialize(int maxElementCounts)
        {
            m_freeElementCount = maxElementCounts;
            m_freeBlocks  = new NativeList<Block>(Allocator.Persistent);
            m_freeBlocks.Add(new Block() { offset = 0, count = m_freeElementCount });
        }

        public Allocation Allocate(int elementCounts)
        {
            if (elementCounts > m_freeElementCount || m_freeBlocks.IsEmpty)
                return Allocation.Invalid;

            int selectedBlock = -1;
            int currentBlockCount = 0;
            for (int b = 0; b < m_freeBlocks.Count(); ++b)
            {
                Block block = m_freeBlocks[b];

                //simple naive allocator, we find the smallest possible space to allocate in our blocks.
                if (elementCounts <= block.count && (selectedBlock == -1 || block.count < currentBlockCount))
                {
                    currentBlockCount = block.count;
                    selectedBlock = b;
                }
            }

            if (selectedBlock == -1)
                return Allocation.Invalid;


            Block activeBlock = m_freeBlocks[selectedBlock];
            Block split = activeBlock;

            split.offset += elementCounts;
            split.count -= elementCounts;
            activeBlock.count = elementCounts;

            if (split.count > 0)
                m_freeBlocks[selectedBlock] = split;
            else
                m_freeBlocks.RemoveAtSwapBack(selectedBlock);

            return Allocation.Invalid;

        }

        public void FreeAllocation(in Allocation allocation)
        {
            if (!allocation.Valid())
                throw new System.Exception("Cannot free invalid allocation");
        }

        public void Dispose()
        {
            m_freeBlocks.Dispose();
        }
    }

    public class GeometryPool
    {
        GeometryPoolDesc m_desc;

        public ComputeBuffer m_vertexPoolP   = null;
        public ComputeBuffer m_vertexPoolUV  = null;
        public ComputeBuffer m_vertexPoolUV1 = null;
        public ComputeBuffer m_vertexPoolN   = null;
        public ComputeBuffer m_vertexPoolT   = null;
        public ComputeBuffer m_indexPool  = null;

        private int m_maxVertCounts;
        private int m_maxIndexCounts;

        public GeometryPool(in GeometryPoolDesc desc)
        {
            m_desc = desc;
            m_maxVertCounts = CalcVertexCount();
            m_maxIndexCounts = CalcIndexCount();

            
            m_vertexPoolP   = new ComputeBuffer(m_maxVertCounts, System.Runtime.InteropServices.Marshal.SizeOf<Vector3>());
            m_vertexPoolUV  = new ComputeBuffer(m_maxVertCounts, System.Runtime.InteropServices.Marshal.SizeOf<Vector2>());
            m_vertexPoolUV1 = new ComputeBuffer(m_maxVertCounts, System.Runtime.InteropServices.Marshal.SizeOf<Vector2>());
            m_vertexPoolN   = new ComputeBuffer(m_maxVertCounts, System.Runtime.InteropServices.Marshal.SizeOf<Vector3>());
            m_vertexPoolT   = new ComputeBuffer(m_maxVertCounts, System.Runtime.InteropServices.Marshal.SizeOf<Vector3>());
            
            m_indexPool     = new ComputeBuffer(m_maxIndexCounts, System.Runtime.InteropServices.Marshal.SizeOf<int>());

        }

        internal static int DivUp(int x, int y) => (x + y - 1) / y;

        private static int GetVertByteSize()
        {
            return System.Runtime.InteropServices.Marshal.SizeOf<Vector3>() + /*pos*/
                   System.Runtime.InteropServices.Marshal.SizeOf<Vector2>() + /*uv0*/
                   System.Runtime.InteropServices.Marshal.SizeOf<Vector2>() + /*uv1*/
                   System.Runtime.InteropServices.Marshal.SizeOf<Vector3>() + /*N*/
                   System.Runtime.InteropServices.Marshal.SizeOf<Vector3>();  /*T*/
        }

        private static int GetIndexByteSize()
        {
            return System.Runtime.InteropServices.Marshal.SizeOf<int>();
        }

        private int CalcVertexCount() => DivUp(m_desc.vertexPoolByteSize, GetVertByteSize());
        private int CalcIndexCount() => DivUp(m_desc.indexPoolByteSize, GetIndexByteSize());

        public void Dispose()
        {
            m_vertexPoolP.Release();
            m_vertexPoolUV.Release();
            m_vertexPoolUV1.Release();
            m_vertexPoolN.Release();
            m_vertexPoolT.Release();
            m_indexPool.Release();
        }
    }

}
