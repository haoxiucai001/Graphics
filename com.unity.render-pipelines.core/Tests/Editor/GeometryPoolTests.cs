using NUnit.Framework;
using System.Collections.Generic;
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
    }
}
