using System;
using System.Threading;
using UnityEngine;

namespace CraftSharp.Rendering
{
    [RequireComponent(typeof (MeshFilter), typeof (MeshRenderer))]
    public class ChunkRender : MonoBehaviour, IComparable<ChunkRender>
    {
        public static readonly RenderType[] TYPES = new RenderType[]
        {
            RenderType.SOLID,         RenderType.CUTOUT,
            RenderType.CUTOUT_MIPPED, RenderType.TRANSLUCENT,
            RenderType.WATER
        };

        public static int TypeIndex(RenderType type) => type switch
        {
            RenderType.SOLID         => 0,
            RenderType.CUTOUT        => 1,
            RenderType.CUTOUT_MIPPED => 2,
            RenderType.TRANSLUCENT   => 3,
            RenderType.WATER         => 4,
            
            _                        => 0
        };

        public int ChunkX, ChunkZ;
        /// <summary>
        /// Non-negative value marking the position of this chunk in the chunk column from bottom to top.
        /// <br/>
        /// This value always starts from 0, and is unaffected by y-offset value.
        /// </summary>
        public int ChunkYIndex;
        public ChunkBuildState State = ChunkBuildState.Pending;

        public CancellationTokenSource TokenSource = null;
        public int Priority = 0;
        public MeshCollider InteractionCollider;

        public void UpdateCollider(Mesh colliderMesh)
        {
            if (InteractionCollider == null)
            {
                InteractionCollider = gameObject.AddComponent<MeshCollider>();
            }
            InteractionCollider.sharedMesh = colliderMesh;
        }

        public void ClearCollider()
        {
            if (InteractionCollider != null)
            {
                // Set this to make sure the empty mesh is no longer used by the collider, which will raise errors
                // See https://forum.unity.com/threads/when-assigning-mesh-collider-errors-about-doesnt-have-read-write-enabled.1248541/
                InteractionCollider.sharedMesh = null;
            }
        }

        public int CompareTo(ChunkRender chunkRender) => Priority - chunkRender.Priority;

        public void Unload()
        {
            TokenSource?.Cancel();
        }

        public override string ToString() => $"[ChunkRender {ChunkX}, {ChunkYIndex}, {ChunkZ}]";
    }
}