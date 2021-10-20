using NUnit.Framework;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine.Rendering;

namespace UnityEngine.Rendering.Tests
{
    class GeometryPoolTests
    {
        public static Mesh sCube = null;
        public static Mesh sSphere = null;
        public static Mesh sCapsule = null;

        [SetUp]
        public void SetupGeometryPoolTests()
        {
            sCube = GameObject.CreatePrimitive(PrimitiveType.Cube).GetComponent<MeshFilter>().sharedMesh;
            sSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere).GetComponent<MeshFilter>().sharedMesh;
            sCapsule = GameObject.CreatePrimitive(PrimitiveType.Capsule).GetComponent<MeshFilter>().sharedMesh;
        }

        [TearDown]
        public void TearDownGeometryPoolTests()
        {
            sCube = null;
            sSphere = null;
        }

        [Test]
        public void TestGeometryPoolAddRemove()
        {
            var geometryPool = new GeometryPool(GeometryPoolDesc.NewDefault());
            bool status;
            status = geometryPool.Register(sCube, out var handle0);
            Assert.IsTrue(status);
            Assert.AreEqual(handle0.index, geometryPool.GetHandle(sCube).index);

            status = geometryPool.Register(sSphere, out var handle1);
            Assert.IsTrue(status);
            Assert.AreEqual(handle1.index, geometryPool.GetHandle(sSphere).index);

            geometryPool.Unregister(sSphere);
            Assert.IsTrue(!geometryPool.GetHandle(sSphere).valid);

            geometryPool.Unregister(sCube);
            Assert.IsTrue(!geometryPool.GetHandle(sCube).valid);

            geometryPool.Dispose();
        }

        [Test]
        public void TestGeometryPoolRefCount()
        {
            var geometryPool = new GeometryPool(GeometryPoolDesc.NewDefault());

            bool status;

            status = geometryPool.Register(sCube, out var handle0);
            Assert.IsTrue(status);
            status = geometryPool.Register(sCube, out var handle1);
            Assert.IsTrue(status);
            status = geometryPool.Register(sSphere, out var handle2);
            Assert.IsTrue(status);

            Assert.AreEqual(handle0.index, handle1.index);
            Assert.AreNotEqual(handle0.index, handle2.index);

            geometryPool.Unregister(sCube);

            Assert.IsTrue(geometryPool.GetHandle(sCube).valid);

            geometryPool.Unregister(sCube);

            Assert.IsTrue(!geometryPool.GetHandle(sCube).valid);

            status = geometryPool.Register(sCube, out var _);
            Assert.IsTrue(status);
            Assert.IsTrue(geometryPool.GetHandle(sCube).valid);

            geometryPool.Dispose();
        }

        [Test]
        public void TestGeometryPoolFailedAllocByIndex()
        {
            int cubeIndices = 0;
            for (int i = 0; i < (int)sCube.subMeshCount; ++i)
                cubeIndices += (int)sCube.GetIndexCount(i);

            int sphereIndices = 0;
            for (int i = 0; i < (int)sSphere.subMeshCount; ++i)
                sphereIndices += (int)sSphere.GetIndexCount(i);

            int capsuleIndices = 0;
            for (int i = 0; i < (int)sCapsule.subMeshCount; ++i)
                capsuleIndices += (int)sCapsule.GetIndexCount(i);

            var gpdesc = GeometryPoolDesc.NewDefault();
            gpdesc.indexPoolByteSize = (cubeIndices + capsuleIndices) * GeometryPool.GetIndexByteSize();

            var geometryPool = new GeometryPool(gpdesc);

            bool status;
            status = geometryPool.Register(sCube, out var _);
            Assert.IsTrue(status);

            status = geometryPool.Register(sCapsule, out var _);
            Assert.IsTrue(status);

            status = geometryPool.Register(sSphere, out var _);
            Assert.IsTrue(!status);

            geometryPool.Unregister(sCapsule);

            status = geometryPool.Register(sSphere, out var _);
            Assert.IsTrue(status);

            geometryPool.Dispose();
        }

        [Test]
        public void TestGeometryPoolFailedAllocByMaxVertex()
        {
            int cubeVertices = sCube.vertexCount;
            int sphereVertices = sSphere.vertexCount;
            int capsuleVertices = sCapsule.vertexCount;

            var gpdesc = GeometryPoolDesc.NewDefault();
            gpdesc.vertexPoolByteSize = (cubeVertices + capsuleVertices) * GeometryPool.GetVertexByteSize();

            var geometryPool = new GeometryPool(gpdesc);

            bool status;
            status = geometryPool.Register(sCube, out var _);
            Assert.IsTrue(status);

            status = geometryPool.Register(sCapsule, out var _);
            Assert.IsTrue(status);

            status = geometryPool.Register(sSphere, out var _);
            Assert.IsTrue(!status);

            geometryPool.Unregister(sCapsule);

            status = geometryPool.Register(sSphere, out var _);
            Assert.IsTrue(status);

            geometryPool.Dispose();
        }

        [Test]
        public void TestGeometryPoolFailedAllocByMaxMeshes()
        {
            var gpdesc = GeometryPoolDesc.NewDefault();
            gpdesc.maxMeshes = 2;

            var geometryPool = new GeometryPool(gpdesc);

            bool status;
            status = geometryPool.Register(sCube, out var _);
            Assert.IsTrue(status);

            status = geometryPool.Register(sCapsule, out var _);
            Assert.IsTrue(status);

            status = geometryPool.Register(sSphere, out var _);
            Assert.IsTrue(!status);

            geometryPool.Unregister(sCapsule);

            status = geometryPool.Register(sSphere, out var _);
            Assert.IsTrue(status);

            geometryPool.Dispose();
        }

    
        internal struct GeometryPoolTestIndexCpuData
        {
            CommandBuffer m_cmdBuffer;
            AsyncGPUReadbackRequest m_request;
            GeometryPool m_geometryPool;

            public GeometryPool geoPool { get { return m_geometryPool; } }
            public NativeArray<int> cpuIndexData;

            public void Load(GeometryPool geometryPool)
            {
                m_cmdBuffer = new CommandBuffer();
                m_geometryPool = geometryPool;
                /*

                m_request = AsyncGPUReadback.Request(geometryPool.globalIndexBuffer);
                m_request.WaitForCompletion();
                Assert.IsTrue(m_request.done);
                Assert.IsTrue(!m_request.hasError);

                cpuIndexData = m_request.GetData<int>();*/

                var indexData = new NativeArray<int>(geometryPool.indicesCount, Allocator.Persistent);                
                m_cmdBuffer.RequestAsyncReadback(geometryPool.globalIndexBuffer, (AsyncGPUReadbackRequest req) =>
                {
                    if (req.done)
                        indexData.CopyFrom(req.GetData<int>());
                });
                m_cmdBuffer.WaitAllAsyncReadbackRequests();

                Graphics.ExecuteCommandBuffer(m_cmdBuffer);                
                cpuIndexData = indexData;
            }

            public void Dispose()
            {
                cpuIndexData.Dispose();
                m_cmdBuffer.Dispose();
            }
        }

        private void VerifyIndicesInPool(
            in GeometryPoolTestIndexCpuData indexCpuData,
            in GeometryPoolHandle handle,
            Mesh mesh)
        {
            var gpuIndexData = indexCpuData.cpuIndexData;
            var idxBufferBlock = indexCpuData.geoPool.GetIndexBufferBlock(handle).block;
            for (int smId = 0; smId < (int)mesh.subMeshCount; ++smId)
            {
                var indices = mesh.GetIndices(smId);
                Assert.IsTrue(indices.Length == idxBufferBlock.count);
                if (indices.Length != idxBufferBlock.count)
                    continue;

                for (int i = 0; i < idxBufferBlock.count; ++i)
                {
                    int expected = indices[i];
                    int result = gpuIndexData[idxBufferBlock.offset + i];

                    if (expected != result)
                        Debug.LogError("Expected index " + expected + " but got " + result);
                    Assert.IsTrue(expected == result);
                }
            }
        }

        [Test]
        public void TestGpuUploadIndexBufferGeometryPool()
        {
            var geometryPool = new GeometryPool(GeometryPoolDesc.NewDefault());

            bool status;

            status = geometryPool.Register(sCube, out var cubeHandle);
            Assert.IsTrue(status);

            status = geometryPool.Register(sSphere, out var sphereHandle);
            Assert.IsTrue(status);

            geometryPool.SendGpuCommands();

            GeometryPoolTestIndexCpuData indexCpuData = new GeometryPoolTestIndexCpuData();
            indexCpuData.Load(geometryPool);

            VerifyIndicesInPool(indexCpuData, cubeHandle, sCube);
            VerifyIndicesInPool(indexCpuData, sphereHandle, sSphere);

            indexCpuData.Dispose();
            geometryPool.Dispose();
        }

        [Test]
        public void TestGpuUploadAddRemoveIndexBufferGeometryPool()
        {
            var geometryPool = new GeometryPool(GeometryPoolDesc.NewDefault());

            bool status;

            status = geometryPool.Register(sSphere, out var sphereHandle);
            Assert.IsTrue(status);

            status = geometryPool.Register(sCube, out var cubeHandle);
            Assert.IsTrue(status);

            geometryPool.SendGpuCommands();

            geometryPool.Unregister(sSphere);

            status = geometryPool.Register(sCapsule, out var capsuleHandle);
            Assert.IsTrue(status);

            geometryPool.SendGpuCommands();

            GeometryPoolTestIndexCpuData indexCpuData = new GeometryPoolTestIndexCpuData();
            indexCpuData.Load(geometryPool);
            VerifyIndicesInPool(indexCpuData, cubeHandle, sCube);
            VerifyIndicesInPool(indexCpuData, capsuleHandle, sCapsule);

            indexCpuData.Dispose();
            geometryPool.Dispose();
        }
    }
}

