using System;
using System.Collections.Generic;
using System.Linq;

using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using Unity.Collections;
using Unity.Mathematics;

using CraftSharp.Resource;

namespace CraftSharp.Rendering
{
    public class ChunkRenderBuilder
    {
        private static readonly int WATER_LAYER_INDEX = ChunkRender.TypeIndex(RenderType.WATER);
        private static readonly int LAVA_LAYER_INDEX  = ChunkRender.TypeIndex(RenderType.SOLID);
        public const int MOVEMENT_RADIUS = 4;
        public const int MOVEMENT_RADIUS_SQR = MOVEMENT_RADIUS * MOVEMENT_RADIUS;
        public const int MOVEMENT_DIAMETER = MOVEMENT_RADIUS + 1 + MOVEMENT_RADIUS;

        private readonly Dictionary<int, BlockStateModel> modelTable;

        public ChunkRenderBuilder(Dictionary<int, BlockStateModel> modelTable)
        {
            this.modelTable = modelTable;
        }

        public ChunkBuildResult Build(ChunkBuildData data, ChunkRender chunkRender)
        {
            try
            {
                var ts = chunkRender.TokenSource;

                int count = ChunkRender.TYPES.Length, layerMask = 0;
                int offsetY = World.GetDimension().minY;

                var visualBuffer = new VertexBuffer[count];
                for (int i = 0;i < count;i++)
                    visualBuffer[i] = new();
                
                float3[] colliderVerts = { };

                var blocs = data.Blocks;
                var light = data.Light;
                var allao = data.AO;
                var allcolor = data.Color;

                int getCullFlags(int x, int y, int z, Block self, BlockNeighborCheck check)
                {
                    int cullFlags = 0;

                    if (check(self, blocs[x, y + 1, z])) // Up
                        cullFlags |= (1 << 0);

                    if (check(self, blocs[x, y - 1, z])) // Down
                        cullFlags |= (1 << 1);
                    
                    if (check(self, blocs[x, y, z + 1])) // South
                        cullFlags |= (1 << 2);

                    if (check(self, blocs[x, y, z - 1])) // North
                        cullFlags |= (1 << 3);
                    
                    if (check(self, blocs[x + 1, y, z])) // East
                        cullFlags |= (1 << 4);

                    if (check(self, blocs[x - 1, y, z])) // West
                        cullFlags |= (1 << 5);
                    
                    return cullFlags;
                }

                float[] getCornerLights(int x, int y, int z)
                {
                    var result = new float[8];

                    for (int y_ = 0; y_ < 3; y_++) for (int z_ = 0; z_ < 3; z_++) for (int x_ = 0; x_ < 3; x_++)
                    {
                        float sample = math.max(0.5F, math.pow(light[x + x_ - 1, y + y_ - 1, z + z_ - 1] / 9F, 1.4f));

                        if (y_ != 2 && z_ != 2 && x_ != 2) // [0] x0z0 y0
                        {
                            result[0] += sample;
                        }
                        if (y_ != 2 && z_ != 2 && x_ != 0) // [1] x1z0 y0
                        {
                            result[1] += sample;
                        }
                        if (y_ != 2 && z_ != 0 && x_ != 2) // [2] x0z1 y0
                        {
                            result[2] += sample;
                        }
                        if (y_ != 2 && z_ != 0 && x_ != 0) // [3] x1z1 y0
                        {
                            result[3] += sample;
                        }
                        if (y_ != 0 && z_ != 2 && x_ != 2) // [4] x0z0 y1
                        {
                            result[4] += sample;
                        }
                        if (y_ != 0 && z_ != 2 && x_ != 0) // [5] x1z0 y1
                        {
                            result[5] += sample;
                        }
                        if (y_ != 0 && z_ != 0 && x_ != 2) // [6] x0z1 y1
                        {
                            result[6] += sample;
                        }
                        if (y_ != 0 && z_ != 0 && x_ != 0) // [7] x1z1 y1
                        {
                            result[7] += sample;
                        }
                    }

                    return result.Select(x => x / 8F).ToArray();
                }

                bool[] getAllNeighborAO(int x, int y, int z)
                {
                    var result = new bool[27];

                    for (int y_ = 0; y_ < 3; y_++) for (int z_ = 0; z_ < 3; z_++) for (int x_ = 0; x_ < 3; x_++)
                    {
                        result[y_ * 9 + z_ * 3 + x_] = allao[x + x_ - 1, y + y_ - 1, z + z_ - 1];
                    }

                    return result;
                }

                // Build chunk vertices block by block
                for (int x = 1;x <= Chunk.SIZE;x++) // From 1 to 16, because we have a padding for blocks in adjacent chunks
                {
                    int blocX = x - 1;

                    for (int y = 1;y <= Chunk.SIZE;y++)
                    {
                        int blocY = y - 1;

                        for (int z = 1;z <= Chunk.SIZE;z++)
                        {
                            int blocZ = z - 1;

                            if (ts.IsCancellationRequested)
                                return ChunkBuildResult.Cancelled;

                            var bloc = data.Blocks[x, y, z];
                            var state = bloc.State;
                            var stateId = bloc.StateId;

                            if (state.InLiquid) // Build liquid here
                            {
                                var neighborCheck = state.InWater ? BlockNeighborChecks.WATER_SURFACE : BlockNeighborChecks.LAVA_SURFACE;
                                int liquidCullFlags = getCullFlags(x, y, z, bloc, neighborCheck);

                                if (liquidCullFlags != 0)
                                {
                                    var liquidHeights = new byte[9] { 15, 15, 15, 15, 15, 15, 15, 15, 15 }; // TODO: Implement
                                    var liquidLayerIndex = state.InWater ? WATER_LAYER_INDEX : LAVA_LAYER_INDEX;
                                    var liquidTexture = FluidGeometry.LiquidTextures[state.InWater ? 0 : 1];
                                    var lights = getCornerLights(x, y, z);

                                    FluidGeometry.Build(ref visualBuffer[liquidLayerIndex], new float3(blocZ, blocY, blocX), liquidTexture,
                                            liquidHeights, liquidCullFlags, lights, /*world.GetWaterColor(blockLoc)*/ new float3(1F));
                                    
                                    layerMask |= (1 << liquidLayerIndex);
                                }
                            }

                            // If air-like (air, water block, etc), ignore it...
                            if (state.LikeAir) continue;
                            
                            int cullFlags = getCullFlags(x, y, z, bloc, BlockNeighborChecks.NON_FULL_SOLID); // TODO Make it more accurate
                            
                            if (cullFlags != 0 && modelTable.ContainsKey(stateId)) // This chunk has at least one visible block of this render type
                            {
                                int layerIndex = ChunkRender.TypeIndex(modelTable[stateId].RenderType);

                                var models = modelTable[stateId].Geometries;
                                var chosen = (x + y + z) % models.Length;
                                var color  = allcolor[blocX, blocY, blocZ];
                                var lights = getCornerLights(x, y, z);
                                var ao = getAllNeighborAO(x, y, z);

                                if (state.NoCollision)
                                    models[chosen].Build(ref visualBuffer[layerIndex], new float3(blocZ, blocY, blocX), cullFlags, ao, lights, color);
                                else
                                    models[chosen].BuildWithCollider(ref visualBuffer[layerIndex], ref colliderVerts, new float3(blocZ, blocY, blocX), cullFlags, ao, lights, color);
                                
                                layerMask |= (1 << layerIndex);
                            }
                        }
                    }
                }

                if (layerMask == 0) // Skip empty chunks...
                {
                    Loom.QueueOnMainThread(() => 
                    {
                        if (chunkRender == null || chunkRender.gameObject == null)
                            return;

                        // TODO Improve below cleaning
                        Profiler.BeginSample("Clear chunk render mesh");

                        var mesh = chunkRender.GetComponent<MeshFilter>().sharedMesh;
                        if (mesh != null)
                        {
                            mesh.Clear(false);
                        }
                        chunkRender.ClearCollider();
                    
                        Profiler.EndSample();

                        chunkRender.State = ChunkBuildState.Ready;
                    });
                
                    return ChunkBuildResult.Succeeded;
                }
                else
                {
                    Loom.QueueOnMainThreadMinor(() => {
                        if (chunkRender == null || chunkRender.gameObject == null)
                            return;
                        
                        Profiler.BeginSample("Update chunk render mesh");

                        Profiler.BeginSample("Build and apply mesh data");

                        Profiler.BeginSample("Build mesh data");

                        // Visual Mesh
                        // Count layers, vertices and face indices
                        int layerCount = 0, totalVertCount = 0;
                        for (int layer = 0;layer < count;layer++)
                        {
                            if ((layerMask & (1 << layer)) != 0)
                            {
                                layerCount++;
                                totalVertCount += visualBuffer[layer].vert.Length;
                            }
                        }
                        
                        var meshDataArr  = Mesh.AllocateWritableMeshData(1);
                        var materialArr  = new Material[layerCount];
                        var meshData = meshDataArr[0];
                        meshData.subMeshCount = layerCount;

                        // Set mesh attributes
                        var visVertAttrs = new NativeArray<VertexAttributeDescriptor>(4, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                        visVertAttrs[0]  = new(VertexAttribute.Position,  dimension: 3, stream: 0);
                        visVertAttrs[1]  = new(VertexAttribute.TexCoord0, dimension: 3, stream: 1);
                        visVertAttrs[2]  = new(VertexAttribute.TexCoord1, dimension: 4, stream: 2);
                        visVertAttrs[3]  = new(VertexAttribute.Color,     dimension: 4, stream: 3);

                        meshData.SetVertexBufferParams(totalVertCount,          visVertAttrs);
                        meshData.SetIndexBufferParams((totalVertCount / 2) * 3, IndexFormat.UInt32);

                        visVertAttrs.Dispose();

                        // Prepare source data arrays
                        var allVerts = new float3[totalVertCount];
                        var allUVs   = new float3[totalVertCount];
                        var allAnims = new float4[totalVertCount];
                        var allTints = new float4[totalVertCount];

                        int vertOffset = 0;
                        for (int layer = 0;layer < count;layer++)
                        {
                            if ((layerMask & (1 << layer)) != 0)
                            {
                                visualBuffer[layer].vert.CopyTo(allVerts, vertOffset);
                                visualBuffer[layer].txuv.CopyTo(allUVs,   vertOffset);
                                visualBuffer[layer].uvan.CopyTo(allAnims, vertOffset);
                                visualBuffer[layer].tint.CopyTo(allTints, vertOffset);

                                vertOffset += visualBuffer[layer].vert.Length;
                            }
                        }

                        // Copy the source arrays to mesh data
                        var positions  = meshData.GetVertexData<float3>(0);
                        positions.CopyFrom(allVerts);
                        var texCoords  = meshData.GetVertexData<float3>(1);
                        texCoords.CopyFrom(allUVs);
                        var texAnims   = meshData.GetVertexData<float4>(2);
                        texAnims.CopyFrom(allAnims);
                        var vertColors = meshData.GetVertexData<float4>(3);
                        vertColors.CopyFrom(allTints);

                        // Generate triangle arrays
                        var triIndices = meshData.GetIndexData<uint>();
                        uint vi = 0; int ti = 0;
                        for (;vi < totalVertCount;vi += 4U, ti += 6)
                        {
                            triIndices[ti]     = vi;
                            triIndices[ti + 1] = vi + 3U;
                            triIndices[ti + 2] = vi + 2U;
                            triIndices[ti + 3] = vi;
                            triIndices[ti + 4] = vi + 1U;
                            triIndices[ti + 5] = vi + 3U;
                        }

                        int subMeshIndex = 0;
                        vertOffset = 0;
                        for (int layer = 0;layer < count;layer++)
                        {
                            if ((layerMask & (1 << layer)) != 0)
                            {
                                materialArr[subMeshIndex] = CornApp.CurrentClient!.ChunkMaterialManager.GetAtlasMaterial(ChunkRender.TYPES[layer]);
                                int vertCount = visualBuffer[layer].vert.Length;
                                meshData.SetSubMesh(subMeshIndex, new((vertOffset / 2) * 3, (vertCount / 2) * 3){ vertexCount = vertCount });
                                vertOffset += vertCount;
                                subMeshIndex++;
                            }
                        }

                        Profiler.EndSample(); // "Build mesh data"
                        Profiler.BeginSample("Create mesh object");

                        var visualMesh = new Mesh { subMeshCount = layerCount };
                        Mesh.ApplyAndDisposeWritableMeshData(meshDataArr, visualMesh);

                        Profiler.EndSample(); // "Create mesh object"

                        Profiler.EndSample(); // "Build and apply mesh data"

                        visualMesh.RecalculateNormals();
                        visualMesh.RecalculateBounds();
                        //visualMesh.RecalculateTangents();

                        chunkRender.GetComponent<MeshFilter>().sharedMesh = visualMesh;
                        chunkRender.GetComponent<MeshRenderer>().sharedMaterials = materialArr;

                        // Collider Mesh
                        int colVertCount = colliderVerts.Length;

                        if (colVertCount > 0)
                        {
                            Profiler.BeginSample("Build and apply mesh data");

                            Profiler.BeginSample("Build mesh data");

                            var colMeshDataArr  = Mesh.AllocateWritableMeshData(1);
                            var colMeshData = colMeshDataArr[0];
                            colMeshData.subMeshCount = 1;

                            // Set mesh attributes
                            var colVertAttrs = new NativeArray<VertexAttributeDescriptor>(1, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                            colVertAttrs[0]  = new(VertexAttribute.Position,  dimension: 3);

                            colMeshData.SetVertexBufferParams(colVertCount,          colVertAttrs);
                            colMeshData.SetIndexBufferParams((colVertCount / 2) * 3, IndexFormat.UInt32);

                            colVertAttrs.Dispose();

                            // Copy the source arrays to mesh data
                            var colPositions  = colMeshData.GetVertexData<float3>(0);
                            colPositions.CopyFrom(colliderVerts);

                            // Generate triangle arrays
                            var colTriIndices = colMeshData.GetIndexData<uint>();
                            vi = 0; ti = 0;
                            for (;vi < colliderVerts.Length;vi += 4U, ti += 6)
                            {
                                colTriIndices[ti]     = vi;
                                colTriIndices[ti + 1] = vi + 3U;
                                colTriIndices[ti + 2] = vi + 2U;
                                colTriIndices[ti + 3] = vi;
                                colTriIndices[ti + 4] = vi + 1U;
                                colTriIndices[ti + 5] = vi + 3U;
                            }

                            colMeshData.SetSubMesh(0, new(0, (colVertCount / 2) * 3){ vertexCount = colVertCount });

                            Profiler.EndSample(); // "Build mesh data"
                            Profiler.BeginSample("Create mesh object");

                            var colliderMesh = new Mesh { subMeshCount = 1 };
                            Mesh.ApplyAndDisposeWritableMeshData(colMeshDataArr, colliderMesh);

                            Profiler.EndSample(); // "Create mesh object"

                            Profiler.EndSample(); // "Build and apply mesh data"

                            colliderMesh.RecalculateNormals();
                            colliderMesh.RecalculateBounds();

                            chunkRender.UpdateCollider(colliderMesh);

                        }
                        else
                        {
                            chunkRender.ClearCollider();
                        }
                        
                        Profiler.EndSample(); // "Update chunk render mesh"
                        
                        chunkRender.State = ChunkBuildState.Ready;

                        chunkRender.gameObject.SetActive(true);
                    });

                    return ChunkBuildResult.Succeeded;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"{e.Message}: {e.StackTrace}");

                return ChunkBuildResult.Cancelled;
            }
        }

        private static readonly Func<Block, Block> getBlock = (bloc) => bloc;

        // For player movement, it is not favorable to use per-chunk mesh colliders
        // because player can get stuck on the edge of chunks due to a bug of Unity
        // (or say PhysX) bug, so we dynamically build the mesh collider around the
        // player as a solution to this. See the problem discussion at
        // https://forum.unity.com/threads/ball-rolling-on-mesh-hits-edges.772760/
        public void BuildTerrainCollider(World world, BlockLoc playerBlockLoc, MeshCollider movementCollider, MeshCollider liquidCollider, Action callback)
        {
            int offsetY = World.GetDimension().minY;

            var blocs = world.GetValuesFromSection(
                    playerBlockLoc.X - MOVEMENT_RADIUS,
                    playerBlockLoc.Y - MOVEMENT_RADIUS,
                    playerBlockLoc.Z - MOVEMENT_RADIUS,
                    MOVEMENT_DIAMETER, MOVEMENT_DIAMETER, MOVEMENT_DIAMETER, getBlock);
            
            float3[] movementVerts = { }, fluidVerts = { };

            int getCullFlags(int x, int y, int z, Block self, BlockNeighborCheck check)
            {
                int cullFlags = 0;

                if (check(self, blocs[x, y + 1, z])) // Up
                    cullFlags |= (1 << 0);

                if (check(self, blocs[x, y - 1, z])) // Down
                    cullFlags |= (1 << 1);
                
                if (check(self, blocs[x, y, z + 1])) // South
                    cullFlags |= (1 << 2);

                if (check(self, blocs[x, y, z - 1])) // North
                    cullFlags |= (1 << 3);
                
                if (check(self, blocs[x + 1, y, z])) // East
                    cullFlags |= (1 << 4);

                if (check(self, blocs[x - 1, y, z])) // West
                    cullFlags |= (1 << 5);
                
                return cullFlags;
            }

            for (int x = -MOVEMENT_RADIUS + 1;x < MOVEMENT_RADIUS;x++)
                for (int y = -MOVEMENT_RADIUS + 1;y < MOVEMENT_RADIUS;y++)
                    for (int z = -MOVEMENT_RADIUS + 1;z < MOVEMENT_RADIUS;z++)
                    {
                        if (x * x + y * y + z * z > MOVEMENT_RADIUS_SQR)
                            continue;

                        var blockLoc = playerBlockLoc + new BlockLoc(x, y, z);
                        var bloc = blocs[MOVEMENT_RADIUS + x, MOVEMENT_RADIUS + y, MOVEMENT_RADIUS + z];
                        var state = bloc.State;

                        if (state.InWater || state.InLava) // Build liquid collider
                        {
                            var neighborCheck = state.InWater ? BlockNeighborChecks.WATER_SURFACE : BlockNeighborChecks.LAVA_SURFACE;
                            int liquidCullFlags = getCullFlags(x + MOVEMENT_RADIUS, y + MOVEMENT_RADIUS, z + MOVEMENT_RADIUS, bloc, neighborCheck);

                            if (liquidCullFlags != 0)
                                FluidGeometry.BuildCollider(ref fluidVerts, new(blockLoc.Z, blockLoc.Y, blockLoc.X), liquidCullFlags);
                        }

                        if (state.LikeAir || state.NoCollision)
                            continue;
                        
                        // Build collider here
                        var stateId = bloc.StateId;
                        int cullFlags = getCullFlags(x + MOVEMENT_RADIUS, y + MOVEMENT_RADIUS, z + MOVEMENT_RADIUS, bloc, BlockNeighborChecks.NON_FULL_SOLID);
                        
                        if (cullFlags != 0 && modelTable.ContainsKey(stateId)) // This chunk has at least one visible block of this render type
                        {
                            // They all have the same collider so we just pick the 1st one
                            modelTable[stateId].Geometries[0].BuildCollider(ref movementVerts, new(blockLoc.Z, blockLoc.Y, blockLoc.X), cullFlags);
                        }
                    }

            Loom.QueueOnMainThread(() => {
                int movVertCount = movementVerts.Length;
                int fldVertCount = fluidVerts.Length;

                // Make vertex attributes
                var colVertAttrs = new NativeArray<VertexAttributeDescriptor>(1, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                colVertAttrs[0]  = new(VertexAttribute.Position,  dimension: 3);

                if (movVertCount > 0)
                {
                    var colMeshDataArr  = Mesh.AllocateWritableMeshData(1);
                    var colMeshData = colMeshDataArr[0];
                    colMeshData.subMeshCount = 1;

                    colMeshData.SetVertexBufferParams(movVertCount,          colVertAttrs);
                    colMeshData.SetIndexBufferParams((movVertCount / 2) * 3, IndexFormat.UInt32);

                    // Copy the source arrays to mesh data
                    var colPositions = colMeshData.GetVertexData<float3>(0);
                    colPositions.CopyFrom(movementVerts);

                    // Generate triangle arrays
                    var colTriIndices = colMeshData.GetIndexData<uint>();
                    uint vi = 0; int ti = 0;
                    for (;vi < movVertCount;vi += 4U, ti += 6)
                    {
                        colTriIndices[ti]     = vi;
                        colTriIndices[ti + 1] = vi + 3U;
                        colTriIndices[ti + 2] = vi + 2U;
                        colTriIndices[ti + 3] = vi;
                        colTriIndices[ti + 4] = vi + 1U;
                        colTriIndices[ti + 5] = vi + 3U;
                    }

                    colMeshData.SetSubMesh(0, new(0, (movVertCount / 2) * 3){ vertexCount = movVertCount });
                    var colliderMesh = new Mesh { subMeshCount = 1 };
                    Mesh.ApplyAndDisposeWritableMeshData(colMeshDataArr, colliderMesh);

                    colliderMesh.RecalculateNormals();
                    colliderMesh.RecalculateBounds();

                    movementCollider!.sharedMesh = colliderMesh;
                }
                else
                {
                    movementCollider!.sharedMesh = null;
                }
                
                if (fldVertCount > 0)
                {
                    var colMeshDataArr  = Mesh.AllocateWritableMeshData(1);
                    var colMeshData = colMeshDataArr[0];
                    colMeshData.subMeshCount = 1;

                    colMeshData.SetVertexBufferParams(fldVertCount,          colVertAttrs);
                    colMeshData.SetIndexBufferParams((fldVertCount / 2) * 3, IndexFormat.UInt32);

                    // Copy the source arrays to mesh data
                    var colPositions = colMeshData.GetVertexData<float3>(0);
                    colPositions.CopyFrom(fluidVerts);

                    // Generate triangle arrays
                    var colTriIndices = colMeshData.GetIndexData<uint>();
                    uint vi = 0; int ti = 0;
                    for (;vi < fldVertCount;vi += 4U, ti += 6)
                    {
                        colTriIndices[ti]     = vi;
                        colTriIndices[ti + 1] = vi + 3U;
                        colTriIndices[ti + 2] = vi + 2U;
                        colTriIndices[ti + 3] = vi;
                        colTriIndices[ti + 4] = vi + 1U;
                        colTriIndices[ti + 5] = vi + 3U;
                    }

                    colMeshData.SetSubMesh(0, new(0, (fldVertCount / 2) * 3){ vertexCount = fldVertCount });
                    var colliderMesh = new Mesh { subMeshCount = 1 };
                    Mesh.ApplyAndDisposeWritableMeshData(colMeshDataArr, colliderMesh);

                    colliderMesh.RecalculateNormals();
                    colliderMesh.RecalculateBounds();

                    liquidCollider!.sharedMesh = colliderMesh;
                }
                else
                {
                    liquidCollider!.sharedMesh = null;
                }
                
                colVertAttrs.Dispose();

                // Invoke callback, for example enable player
                // physics when collider rebuild is complete
                callback?.Invoke();
            });
        }
    }
}