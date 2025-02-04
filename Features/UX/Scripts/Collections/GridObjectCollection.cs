﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XRTK.Definitions.Utilities;
using XRTK.Extensions;
using UnityEngine;

namespace XRTK.SDK.UX.Collections
{
    /// <summary>
    /// A Grid Object Collection is simply a set of child objects organized with some
    /// layout parameters.  The collection can be used to quickly create 
    /// control panels or sets of prefab/objects.
    /// </summary>
    public class GridObjectCollection : BaseObjectCollection
    {
        [Tooltip("Type of surface to map the collection to")]
        [SerializeField]
        private ObjectOrientationSurfaceType surfaceType = ObjectOrientationSurfaceType.Plane;

        /// <summary>
        /// Type of surface to map the collection to.
        /// </summary>
        public ObjectOrientationSurfaceType SurfaceType
        {
            get => surfaceType;
            set => surfaceType = value;
        }

        [Tooltip("Should the objects in the collection be rotated / how should they be rotated")]
        [SerializeField]
        private OrientationType orientType = OrientationType.FaceOrigin;

        /// <summary>
        /// Should the objects in the collection face the origin of the collection
        /// </summary>
        public OrientationType OrientType
        {
            get => orientType;
            set => orientType = value;
        }

        [Tooltip("Whether to sort objects by row first or by column first")]
        [SerializeField]
        private LayoutOrderType layout = LayoutOrderType.ColumnThenRow;

        /// <summary>
        /// Whether to sort objects by row first or by column first
        /// </summary>
        public LayoutOrderType Layout
        {
            get => layout;
            set => layout = value;
        }

        [Range(0.05f, 100.0f)]
        [Tooltip("Radius for the sphere or cylinder")]
        [SerializeField]
        private float radius = 2f;

        /// <summary>
        /// This is the radius of either the Cylinder or Sphere mapping and is ignored when using the plane mapping.
        /// </summary>
        public float Radius
        {
            get => radius;
            set => radius = value;
        }

        [SerializeField]
        [Tooltip("Radial range for radial layout")]
        [Range(5f, 360f)]
        private float radialRange = 180f;

        /// <summary>
        /// This is the radial range for creating a radial fan layout.
        /// </summary>
        public float RadialRange
        {
            get => radialRange;
            set => radialRange = value;
        }

        [SerializeField]
        [Tooltip("The offset for the first position in the radial layout.")]
        [Range(0.0f, 1f)]
        private float radialOffset = 0.0f;

        /// <summary>
        /// The offset for the first position in the radial layout.
        /// </summary>
        public float RadialOffset
        {
            get => radialOffset;
            set => radialOffset = value;
        }

        [Tooltip("Number of rows per column")]
        [SerializeField]
        private int rows = 3;

        /// <summary>
        /// Number of rows per column, column number is automatically determined
        /// </summary>
        public int Rows
        {
            get => rows;
            set => rows = value;
        }

        [Tooltip("Width of cell per object")]
        [SerializeField]
        private float cellWidth = 0.5f;

        /// <summary>
        /// Width of the cell per object in the collection.
        /// </summary>
        public float CellWidth
        {
            get => cellWidth;
            set => cellWidth = value;
        }

        [Tooltip("Height of cell per object")]
        [SerializeField]
        private float cellHeight = 0.5f;

        /// <summary>
        /// Height of the cell per object in the collection.
        /// </summary>
        public float CellHeight
        {
            get => cellHeight;
            set => cellHeight = value;
        }

        /// <summary>
        /// Total Width of collection
        /// </summary>
        public float Width => Columns * CellWidth;

        /// <summary>
        /// Total Height of collection
        /// </summary>
        public float Height => rows * CellHeight;

        /// <summary>
        /// Reference mesh to use for rendering the sphere layout
        /// </summary>
        public Mesh SphereMesh { get; set; }

        /// <summary>
        /// Reference mesh to use for rendering the cylinder layout
        /// </summary>
        public Mesh CylinderMesh { get; set; }

        protected int Columns;

        protected Vector2 HalfCell;

        /// <summary>
        /// Overriding base function for laying out all the children when UpdateCollection is called.
        /// </summary>
        protected override void LayoutChildren()
        {
            var nodeGrid = new Vector3[NodeList.Count];
            Vector3 newPos;

            // Now lets lay out the grid
            Columns = Mathf.CeilToInt((float)NodeList.Count / rows);
            var startOffsetX = (Columns * 0.5f) * CellWidth;
            var startOffsetY = (rows * 0.5f) * CellHeight;
            HalfCell = new Vector2(CellWidth * 0.5f, CellHeight * 0.5f);

            // First start with a grid then project onto surface
            ResolveGridLayout(nodeGrid, startOffsetX, startOffsetY, layout);

            switch (SurfaceType)
            {
                case ObjectOrientationSurfaceType.Plane:
                    for (int i = 0; i < NodeList.Count; i++)
                    {
                        var node = NodeList[i];
                        newPos = nodeGrid[i];
                        //Debug.Log(newPos);
                        node.Transform.localPosition = newPos;
                        UpdateNodeFacing(node);
                        NodeList[i] = node;
                    }
                    break;

                case ObjectOrientationSurfaceType.Cylinder:
                    for (int i = 0; i < NodeList.Count; i++)
                    {
                        var node = NodeList[i];
                        newPos = VectorExtensions.CylindricalMapping(nodeGrid[i], radius);
                        node.Transform.localPosition = newPos;
                        UpdateNodeFacing(node);
                        NodeList[i] = node;
                    }
                    break;

                case ObjectOrientationSurfaceType.Sphere:

                    for (int i = 0; i < NodeList.Count; i++)
                    {
                        var node = NodeList[i];
                        newPos = VectorExtensions.SphericalMapping(nodeGrid[i], radius);
                        node.Transform.localPosition = newPos;
                        UpdateNodeFacing(node);
                        NodeList[i] = node;
                    }
                    break;

                case ObjectOrientationSurfaceType.Radial:
                    int curColumn = 0;
                    int curRow = 1;

                    for (int i = 0; i < NodeList.Count; i++)
                    {
                        var node = NodeList[i];
                        newPos = VectorExtensions.RadialMapping(nodeGrid[i], radialRange, radius, curRow, rows, curColumn, Columns, radialOffset);

                        if (curColumn == (Columns - 1))
                        {
                            curColumn = 0;
                            ++curRow;
                        }
                        else
                        {
                            ++curColumn;
                        }

                        node.Transform.localPosition = newPos;
                        UpdateNodeFacing(node);
                        NodeList[i] = node;
                    }
                    break;
            }
        }

        protected void ResolveGridLayout(Vector3[] grid, float offsetX, float offsetY, LayoutOrderType order)
        {
            int cellCounter = 0;
            float iMax;
            float jMax;

            if (order == LayoutOrderType.RowThenColumn)
            {
                iMax = Rows;
                jMax = Columns;
            }
            else
            {
                iMax = Columns;
                jMax = Rows;
            }

            for (int i = 0; i < iMax; i++)
            {
                for (int j = 0; j < jMax; j++)
                {
                    if (cellCounter < NodeList.Count)
                    {
                        grid[cellCounter].Set(((i * CellWidth) - offsetX + HalfCell.x) + NodeList[cellCounter].Offset.x,
                                             (-(j * CellHeight) + offsetY - HalfCell.y) + NodeList[cellCounter].Offset.y,
                                             0.0f);
                    }
                    cellCounter++;
                }
            }
        }

        /// <summary>
        /// Update the facing of a node given the nodes new position for facing origin with node and orientation type
        /// </summary>
        /// <param name="node"></param>
        protected void UpdateNodeFacing(ObjectCollectionNode node)
        {
            Vector3 centerAxis;
            Vector3 pointOnAxisNearestNode;
            switch (OrientType)
            {
                case OrientationType.FaceOrigin:
                    node.Transform.rotation = Quaternion.LookRotation(node.Transform.position - transform.position, transform.up);
                    break;

                case OrientationType.FaceOriginReversed:
                    node.Transform.rotation = Quaternion.LookRotation(transform.position - node.Transform.position, transform.up);
                    break;

                case OrientationType.FaceCenterAxis:
                    centerAxis = Vector3.Project(node.Transform.position - transform.position, transform.up);
                    pointOnAxisNearestNode = transform.position + centerAxis;
                    node.Transform.rotation = Quaternion.LookRotation(node.Transform.position - pointOnAxisNearestNode, transform.up);
                    break;

                case OrientationType.FaceCenterAxisReversed:
                    centerAxis = Vector3.Project(node.Transform.position - transform.position, transform.up);
                    pointOnAxisNearestNode = transform.position + centerAxis;
                    node.Transform.rotation = Quaternion.LookRotation(pointOnAxisNearestNode - node.Transform.position, transform.up);
                    break;

                case OrientationType.FaceParentFoward:
                    node.Transform.forward = transform.rotation * Vector3.forward;
                    break;

                case OrientationType.FaceParentForwardReversed:
                    node.Transform.forward = transform.rotation * Vector3.back;
                    break;

                case OrientationType.FaceParentUp:
                    node.Transform.forward = transform.rotation * Vector3.up;
                    break;

                case OrientationType.FaceParentDown:
                    node.Transform.forward = transform.rotation * Vector3.down;
                    break;

                case OrientationType.None:
                    break;

                default:
                    Debug.LogWarning("OrientationType out of range");
                    break;
            }
        }

        // Gizmos to draw when the Collection is selected.
        protected virtual void OnDrawGizmosSelected()
        {
            var scale = (2f * radius) * Vector3.one;

            switch (surfaceType)
            {
                case ObjectOrientationSurfaceType.Plane:
                    break;
                case ObjectOrientationSurfaceType.Cylinder:
                    Gizmos.color = Color.green;
                    Gizmos.DrawWireMesh(CylinderMesh, transform.position, transform.rotation, scale);
                    break;
                case ObjectOrientationSurfaceType.Sphere:
                    Gizmos.color = Color.green;
                    Gizmos.DrawWireMesh(SphereMesh, transform.position, transform.rotation, scale);
                    break;
                case ObjectOrientationSurfaceType.Radial:
                    Gizmos.color = Color.green;
                    Gizmos.DrawWireMesh(SphereMesh, transform.position, transform.rotation, scale);
                    break;
            }
        }
    }
}