﻿using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace Barotrauma.Items.Components
{
    partial class ElectricalDischarger : ItemComponent
    {
        private static List<ElectricalDischarger> list = new List<ElectricalDischarger>();
        public static IEnumerable<ElectricalDischarger> List
        {
            get { return list; }
        }

        const int MaxNodes = 100;
        const float MaxNodeDistance = 256.0f;
        const float RangeMultiplierInWalls = 10.0f;

        public struct Node
        {
            public Vector2 WorldPosition;
            public int ParentIndex;
            public float Length;
            public float Angle;

            public Node(Vector2 worldPosition, int parentIndex, float length = 0.0f, float angle = 0.0f)
            {
                WorldPosition = worldPosition;
                ParentIndex = parentIndex;
                Length = length;
                Angle = angle;
            }
        }

        [Serialize(100.0f, true), Editable(MinValueFloat = 0.0f, MaxValueFloat = 5000.0f)]
        public float Range
        {
            get;
            set;
        }

        private List<Node> nodes = new List<Node>();

        public IEnumerable<Node> Nodes
        {
            get { return nodes; }
        }

        public ElectricalDischarger(Item item, XElement element) : 
            base(item, element)
        {
            list.Add(this);
            InitProjSpecific();
        }

        partial void InitProjSpecific();

        public override bool Use(float deltaTime, Character character = null)
        {
            FindNodes(item.WorldPosition);
            return true;
        }

        public override void Update(float deltaTime, Camera cam)
        {
#if CLIENT
            frameOffset = Rand.Int(electricitySprite.FrameCount);
#endif
        }

        private void FindNodes(Vector2 worldPosition)
        {
            //see which submarines are within range so we can skip structures that are in far-away subs
            List<Submarine> submarinesInRange = new List<Submarine>();
            foreach (Submarine sub in Submarine.Loaded)
            {
                if (item.Submarine == sub)
                {
                    submarinesInRange.Add(sub);
                }
                else
                {
                    Rectangle subBorders = new Rectangle(
                        sub.Borders.X - (int)Range, sub.Borders.Y + (int)Range,
                        sub.Borders.Width + (int)(Range * 2), sub.Borders.Height + (int)(Range * 2));
                    subBorders.Location += MathUtils.ToPoint(sub.SubBody.Position);
                    if (Submarine.RectContains(subBorders, worldPosition))
                    {
                        submarinesInRange.Add(sub);
                    }
                }
            }

            //get all walls within range
            List<Structure> structuresInRange = new List<Structure>(100);
            foreach (Structure structure in Structure.WallList)
            {
                if (!structure.HasBody || structure.IsPlatform) { continue; }
                if (structure.Submarine != null)
                {
                    if (!submarinesInRange.Contains(structure.Submarine)) { continue; }
                }

                var structureWorldRect = structure.WorldRect;
                if (worldPosition.X < structureWorldRect.X - Range) continue;
                if (worldPosition.X > structureWorldRect.Right + Range) continue;
                if (worldPosition.Y > structureWorldRect.Y + Range) continue;
                if (worldPosition.Y < structureWorldRect.Y -structureWorldRect.Height - Range) continue;

                structuresInRange.Add(structure);
            }

            nodes.Clear();
            nodes.Add(new Node(worldPosition, -1));
            FindNodes(structuresInRange, worldPosition, 0, Range);

            //construct final nodes (w/ lengths and angles so they don't have to be recalculated when rendering the discharge)
            for (int i = 0; i < nodes.Count; i++)
            {
                if (nodes[i].ParentIndex < 0) continue;
                Node parentNode = nodes[nodes[i].ParentIndex];
                float length = Vector2.Distance(nodes[i].WorldPosition, parentNode.WorldPosition) * Rand.Range(1.0f, 1.25f);
                float angle = MathUtils.VectorToAngle(parentNode.WorldPosition - nodes[i].WorldPosition);
                nodes[i] = new Node(nodes[i].WorldPosition, nodes[i].ParentIndex, length, angle);
            }
        }

        private void FindNodes(List<Structure> structuresInRange, Vector2 currPos, int parentNodeIndex, float currentRange)
        {
            if (currentRange <= 0.0f || nodes.Count >= MaxNodes) return;

            //find the closest structure
            int closestIndex = -1;
            float closestDist = float.MaxValue;
            for (int i = 0; i < structuresInRange.Count; i++)
            {
                float dist = 0.0f;

                if (structuresInRange[i].IsHorizontal)
                {
                    dist = Math.Abs(structuresInRange[i].WorldPosition.Y - currPos.Y);
                    if (currPos.X < structuresInRange[i].WorldRect.X)
                        dist += structuresInRange[i].WorldRect.X - currPos.X;
                    else if (currPos.X > structuresInRange[i].WorldRect.Right)
                        dist += currPos.X - structuresInRange[i].WorldRect.Right;
                }
                else
                {
                    dist = Math.Abs(structuresInRange[i].WorldPosition.X - currPos.X);
                    if (currPos.Y < structuresInRange[i].WorldRect.Y - structuresInRange[i].Rect.Height)
                        dist += (structuresInRange[i].WorldRect.Y - structuresInRange[i].Rect.Height) - currPos.Y;
                    else if (currPos.Y > structuresInRange[i].WorldRect.Y)
                        dist += currPos.Y - structuresInRange[i].WorldRect.Y;
                }                

                if (dist < closestDist)
                {
                    closestIndex = i;
                    closestDist = dist;
                }
            }

            if (closestIndex == -1 || closestDist > currentRange) return;
            currentRange -= closestDist;
            Structure targetStructure = structuresInRange[closestIndex];

            Vector2 targetPos = targetStructure.IsHorizontal ?
                new Vector2(MathHelper.Clamp(currPos.X, targetStructure.WorldRect.X, targetStructure.WorldRect.Right), targetStructure.WorldPosition.Y) :
                new Vector2(targetStructure.WorldPosition.X, MathHelper.Clamp(currPos.Y, targetStructure.WorldRect.Y - targetStructure.Rect.Height, targetStructure.WorldRect.Y));
                        
            //create nodes from the current position to the closest point on the structure
            AddNodesBetweenPoints(currPos, targetPos, ref parentNodeIndex);

            if (targetStructure.IsHorizontal)
            {
                //add a node at the closest point
                nodes.Add(new Node(targetPos, parentNodeIndex));
                int nodeIndex = nodes.Count - 1;
                structuresInRange.RemoveAt(closestIndex);

                float newRange = currentRange - (targetStructure.Rect.Width / 2) * (1.0f / RangeMultiplierInWalls);

                //continue the discharge to the left edge of the structure and extend from there
                int leftNodeIndex = nodeIndex;
                Vector2 leftPos = new Vector2(targetStructure.WorldRect.X, targetStructure.WorldPosition.Y);
                AddNodesBetweenPoints(targetPos, leftPos, ref leftNodeIndex);
                nodes.Add(new Node(leftPos, leftNodeIndex));
                FindNodes(structuresInRange, leftPos, nodes.Count - 1, newRange);

                //continue the discharge to the right edge of the structure and extend from there
                int rightNodeIndex = nodeIndex; 
                Vector2 rightPos = new Vector2(targetStructure.WorldRect.Right, targetStructure.WorldPosition.Y);
                AddNodesBetweenPoints(targetPos, rightPos, ref rightNodeIndex);
                nodes.Add(new Node(rightPos, rightNodeIndex));
                FindNodes(structuresInRange, rightPos, nodes.Count - 1, newRange);
            }
            else
            {
                //add a node at the closest point
                nodes.Add(new Node(targetPos, parentNodeIndex));
                int nodeIndex = nodes.Count - 1;
                structuresInRange.RemoveAt(closestIndex);

                float newRange = currentRange - (targetStructure.Rect.Height / 2) * (1.0f / RangeMultiplierInWalls);

                //continue the discharge to the top edge of the structure and extend from there
                int topNodeIndex = nodeIndex;
                Vector2 topPos = new Vector2(targetStructure.WorldPosition.X, targetStructure.WorldRect.Y);
                AddNodesBetweenPoints(targetPos, topPos, ref topNodeIndex);
                nodes.Add(new Node(topPos, topNodeIndex));
                FindNodes(structuresInRange, topPos, nodes.Count - 1, newRange);

                //continue the discharge to the bottom edge of the structure and extend from there
                int bottomNodeIndex = nodeIndex;
                Vector2 bottomBos = new Vector2(targetStructure.WorldPosition.X, targetStructure.WorldRect.Y - targetStructure.Rect.Height);
                AddNodesBetweenPoints(targetPos, bottomBos, ref bottomNodeIndex);
                nodes.Add(new Node(bottomBos, bottomNodeIndex));
                FindNodes(structuresInRange, bottomBos, nodes.Count - 1, newRange);
            }            
        }

        private void AddNodesBetweenPoints(Vector2 currPos, Vector2 targetPos, ref int parentNodeIndex)
        {
            Vector2 diff = targetPos - currPos;
            float dist = diff.Length();
            for (float x = MaxNodeDistance; x < dist - MaxNodeDistance; x += MaxNodeDistance)
            {
                nodes.Add(new Node(currPos + (diff / dist) * x, parentNodeIndex));
                parentNodeIndex = nodes.Count - 1;
            }
        }

        protected override void RemoveComponentSpecific()
        {
            list.Remove(this);
        }
    }
}
