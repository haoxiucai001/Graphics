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
        public static Mesh sCube16bit = null;
        public static Mesh sCapsule16bit = null;

        private static Mesh Create16BitIndexMesh(Mesh input)
        {
            Mesh newMesh = new Mesh();
            newMesh.vertices = new Vector3[input.vertexCount];
            System.Array.Copy(input.vertices, newMesh.vertices, input.vertexCount);

            newMesh.uv = new Vector2[input.vertexCount];
            System.Array.Copy(input.uv, newMesh.uv, input.vertexCount);

            newMesh.normals = new Vector3[input.vertexCount];
            System.Array.Copy(input.normals, newMesh.normals, input.vertexCount);

            newMesh.vertexBufferTarget = GraphicsBuffer.Target.Raw;
            newMesh.indexBufferTarget = GraphicsBuffer.Target.Raw;

            newMesh.subMeshCount = input.subMeshCount;

            int indexCounts = 0;
            for (int i = 0; i < input.subMeshCount; ++i)
                indexCounts += (int)input.GetIndexCount(i);

            newMesh.SetIndexBufferParams(indexCounts, IndexFormat.UInt16);

            for (int i = 0; i < input.subMeshCount; ++i)
            {
                newMesh.SetSubMesh(i, input.GetSubMesh(i), MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontRecalculateBounds);
                newMesh.SetIndices(input.GetIndices(i), MeshTopology.Triangles, i);
            }

            newMesh.UploadMeshData(false);
            return newMesh;
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

        [SetUp]
        public void SetupGeometryPoolTests()
        {
            sCube = GameObject.CreatePrimitive(PrimitiveType.Cube).GetComponent<MeshFilter>().sharedMesh;
            sSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere).GetComponent<MeshFilter>().sharedMesh;
            sCapsule = GameObject.CreatePrimitive(PrimitiveType.Capsule).GetComponent<MeshFilter>().sharedMesh;
            sCube16bit = Create16BitIndexMesh(sCube);
            sCapsule16bit = Create16BitIndexMesh(sCapsule);
        }

        [TearDown]
        public void TearDownGeometryPoolTests()
        {
            sCube = null;
            sSphere = null;
            sCapsule = null;
            sCube16bit = null;
            sCapsule16bit = null;
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

        [Test]
        public void TestGpuUploadIndexBuffer16bitGeometryPool()
        {
            var geometryPool = new GeometryPool(GeometryPoolDesc.NewDefault());

            bool status;

            status = geometryPool.Register(sCube16bit, out var cubeHandle);
            Assert.IsTrue(status);

            status = geometryPool.Register(sSphere, out var sphereHandle);
            Assert.IsTrue(status);

            geometryPool.SendGpuCommands();

            GeometryPoolTestIndexCpuData indexCpuData = new GeometryPoolTestIndexCpuData();
            indexCpuData.Load(geometryPool);

            VerifyIndicesInPool(indexCpuData, cubeHandle, sCube16bit);
            VerifyIndicesInPool(indexCpuData, sphereHandle, sSphere);

            indexCpuData.Dispose();
            geometryPool.Dispose();
        }

        [Test]
        public void TestGpuUploadAddRemoveIndexBuffer16bitGeometryPool()
        {
            var geometryPool = new GeometryPool(GeometryPoolDesc.NewDefault());

            bool status;

            status = geometryPool.Register(sSphere, out var sphereHandle);
            Assert.IsTrue(status);

            status = geometryPool.Register(sCube16bit, out var cubeHandle);
            Assert.IsTrue(status);

            geometryPool.SendGpuCommands();

            geometryPool.Unregister(sSphere);

            status = geometryPool.Register(sCapsule16bit, out var capsuleHandle);
            Assert.IsTrue(status);

            geometryPool.SendGpuCommands();

            GeometryPoolTestIndexCpuData indexCpuData = new GeometryPoolTestIndexCpuData();
            indexCpuData.Load(geometryPool);
            VerifyIndicesInPool(indexCpuData, cubeHandle, sCube16bit);
            VerifyIndicesInPool(indexCpuData, capsuleHandle, sCapsule16bit);

            indexCpuData.Dispose();
            geometryPool.Dispose();
        }

    }
}

