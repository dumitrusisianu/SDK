﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XRTK.EventDatum.Input;
using XRTK.Interfaces.Providers.Controllers;
using XRTK.Interfaces.InputSystem;
using XRTK.Interfaces.InputSystem.Handlers;
using XRTK.Services;
using XRTK.Services.InputSystem.Pointers;
using XRTK.Services.InputSystem.Sources;
using XRTK.Utilities;
using XRTK.Utilities.Async;
using XRTK.Utilities.Physics;
using UnityEngine;

namespace XRTK.SDK.Input
{
    /// <summary>
    /// This class provides Gaze as an Input Source so users can interact with objects using their head.
    /// </summary>
    [DisallowMultipleComponent]
    public class GazeProvider : InputSystemGlobalListener, IMixedRealityGazeProvider, IMixedRealityInputHandler
    {
        private const float VelocityThreshold = 0.1f;

        private const float MovementThreshold = 0.01f;

        [SerializeField]
        [Tooltip("Maximum distance at which the gaze can hit a GameObject.")]
        private float maxGazeCollisionDistance = 10.0f;

        /// <summary>
        /// The LayerMasks, in prioritized order, that are used to determine the GazeTarget when raycasting.
        /// <example>
        /// <para>Allow the cursor to hit SR, but first prioritize any DefaultRaycastLayers (potentially behind SR)</para>
        /// <code language="csharp"><![CDATA[
        /// int sr = LayerMask.GetMask("SR");
        /// int nonSR = Physics.DefaultRaycastLayers &amp; ~sr;
        /// GazeProvider.Instance.RaycastLayerMasks = new LayerMask[] { nonSR, sr };
        /// ]]></code>
        /// </example>
        /// </summary>
        [SerializeField]
        [Tooltip("The LayerMasks, in prioritized order, that are used to determine the GazeTarget when raycasting.")]
        private LayerMask[] raycastLayerMasks = { Physics.DefaultRaycastLayers };

        /// <summary>
        /// Current stabilization method, used to smooth out the gaze ray data.
        /// If left null, no stabilization will be performed.
        /// </summary>
        [SerializeField]
        [Tooltip("Stabilizer, if any, used to smooth out the gaze ray data.")]
        private GenericStabilizer stabilizer = null;

        /// <summary>
        /// Transform that should be used as the source of the gaze position and rotation.
        /// Defaults to the main camera.
        /// </summary>
        [SerializeField]
        [Tooltip("Transform that should be used to represent the gaze position and rotation. Defaults to CameraCache.Main")]
        private Transform gazeTransform = null;

        [SerializeField]
        [Range(0.01f, 1f)]
        [Tooltip("Minimum head velocity threshold")]
        private float minHeadVelocityThreshold = 0.5f;

        [SerializeField]
        [Range(0.1f, 5f)]
        [Tooltip("Maximum head velocity threshold")]
        private float maxHeadVelocityThreshold = 2f;

        /// <inheritdoc />
        public bool Enabled
        {
            get => enabled;
            set => enabled = value;
        }

        /// <inheritdoc />
        public IMixedRealityInputSource GazeInputSource
        {
            get
            {
                if (gazeInputSource == null)
                {
                    gazeInputSource = new BaseGenericInputSource("Gaze");
                    gazePointer.SetGazeInputSourceParent(gazeInputSource);
                }

                return gazeInputSource;
            }
        }

        private BaseGenericInputSource gazeInputSource;

        /// <inheritdoc />
        public IMixedRealityPointer GazePointer => gazePointer ?? InitializeGazePointer();
        private InternalGazePointer gazePointer = null;

        /// <inheritdoc />
        public IMixedRealityCursor GazeCursor => GazePointer.BaseCursor;

        /// <inheritdoc />
        public GameObject GazeTarget { get; private set; }

        /// <inheritdoc />
        public RaycastHit HitInfo { get; private set; }

        /// <inheritdoc />
        public Vector3 HitPosition { get; private set; }

        /// <inheritdoc />
        public Vector3 HitNormal { get; private set; }

        /// <inheritdoc />
        public Vector3 GazeOrigin => GazePointer.Rays[0].Origin;

        /// <inheritdoc />
        public Vector3 GazeDirection => GazePointer.Rays[0].Direction;

        /// <inheritdoc />
        public Vector3 HeadVelocity { get; private set; }

        /// <inheritdoc />
        public Vector3 HeadMovementDirection { get; private set; }

        /// <inheritdoc />
        public GameObject GameObjectReference => gameObject;

        private float lastHitDistance = 2.0f;

        private bool delayInitialization = true;

        private Vector3 lastHeadPosition = Vector3.zero;

        #region InternalGazePointer Class

        private class InternalGazePointer : GenericPointer
        {
            private readonly Transform gazeTransform;
            private readonly BaseRayStabilizer stabilizer;
            private readonly GazeProvider gazeProvider;

            public InternalGazePointer(GazeProvider gazeProvider, string pointerName, IMixedRealityInputSource inputSourceParent, LayerMask[] raycastLayerMasks, float pointerExtent, Transform gazeTransform, BaseRayStabilizer stabilizer)
                    : base(pointerName, inputSourceParent)
            {
                this.gazeProvider = gazeProvider;
                PrioritizedLayerMasksOverride = raycastLayerMasks;
                this.pointerExtent = pointerExtent;
                this.gazeTransform = gazeTransform;
                this.stabilizer = stabilizer;
                IsInteractionEnabled = true;
            }

            #region IMixedRealityPointer Implementation

            /// <inheritdoc />
            public override IMixedRealityController Controller { get; set; }

            /// <inheritdoc />
            public override IMixedRealityInputSource InputSourceParent { get; protected set; }

            private float pointerExtent;

            /// <inheritdoc />
            public override float PointerExtent
            {
                get => pointerExtent;
                set => pointerExtent = value;
            }

            /// <summary>
            /// Only for use when initializing Gaze Pointer on startup.
            /// </summary>
            /// <param name="gazeInputSource"></param>
            internal void SetGazeInputSourceParent(IMixedRealityInputSource gazeInputSource)
            {
                InputSourceParent = gazeInputSource;
            }

            /// <inheritdoc />
            public override void OnPreRaycast()
            {
                Vector3 newGazeOrigin = gazeTransform.position;
                Vector3 newGazeNormal = gazeTransform.forward;

                // Update gaze info from stabilizer
                if (stabilizer != null)
                {
                    stabilizer.UpdateStability(gazeTransform.localPosition, gazeTransform.localRotation * Vector3.forward);
                    var transformParent = gazeTransform.parent;
                    newGazeOrigin = transformParent.TransformPoint(stabilizer.StablePosition);
                    newGazeNormal = transformParent.TransformDirection(stabilizer.StableRay.direction);
                }

                Vector3 endPoint = newGazeOrigin + (newGazeNormal * pointerExtent);
                Rays[0].UpdateRayStep(ref newGazeOrigin, ref endPoint);

                gazeProvider.HitPosition = Rays[0].Origin + (gazeProvider.lastHitDistance * Rays[0].Direction);
            }

            public override void OnPostRaycast()
            {
                gazeProvider.HitInfo = Result.Details.LastRaycastHit;
                gazeProvider.GazeTarget = Result.Details.Object;

                if (Result.Details.Object != null)
                {
                    gazeProvider.lastHitDistance = (Result.Details.Point - Rays[0].Origin).magnitude;
                    gazeProvider.HitPosition = Rays[0].Origin + (gazeProvider.lastHitDistance * Rays[0].Direction);
                    gazeProvider.HitNormal = Result.Details.Normal;
                }
            }

            public override bool TryGetPointerPosition(out Vector3 position)
            {
                position = gazeTransform.position;
                return true;
            }

            public override bool TryGetPointingRay(out Ray pointingRay)
            {
                pointingRay = new Ray(gazeProvider.GazeOrigin, gazeProvider.GazeDirection);
                return true;
            }

            public override bool TryGetPointerRotation(out Quaternion rotation)
            {
                rotation = Quaternion.identity;
                return false;
            }

            #endregion IMixedRealityPointer Implementation
        }

        #endregion InternalGazePointer Class

        #region MonoBehaviour Implementation

        private void OnValidate()
        {
            Debug.Assert(minHeadVelocityThreshold < maxHeadVelocityThreshold, "Minimum head velocity threshold should be less than the maximum velocity threshold.");
        }

        protected override void OnEnable()
        {
            base.OnEnable();

            if (!delayInitialization)
            {
                // The first time we call OnEnable we skip this.
                RaiseSourceDetected();
            }
        }

        protected override async void Start()
        {
            base.Start();

            await WaitUntilInputSystemValid;

            GazePointer.BaseCursor?.SetVisibility(true);

            if (delayInitialization)
            {
                delayInitialization = false;
                RaiseSourceDetected();
            }
        }

        private void Update()
        {
            if (MixedRealityRaycaster.DebugEnabled && gazeTransform != null)
            {
                Debug.DrawRay(GazeOrigin, (HitPosition - GazeOrigin), Color.white);
            }
        }

        private void LateUpdate()
        {
            // Update head velocity.
            Vector3 headPosition = GazeOrigin;
            Vector3 headDelta = headPosition - lastHeadPosition;

            if (headDelta.sqrMagnitude < MovementThreshold * MovementThreshold)
            {
                headDelta = Vector3.zero;
            }

            if (Time.fixedDeltaTime > 0)
            {
                float velocityAdjustmentRate = 3f * Time.fixedDeltaTime;
                HeadVelocity = HeadVelocity * (1f - velocityAdjustmentRate) + headDelta * velocityAdjustmentRate / Time.fixedDeltaTime;

                if (HeadVelocity.sqrMagnitude < VelocityThreshold * VelocityThreshold)
                {
                    HeadVelocity = Vector3.zero;
                }
            }

            // Update Head Movement Direction
            float multiplier = Mathf.Clamp01(Mathf.InverseLerp(minHeadVelocityThreshold, maxHeadVelocityThreshold, HeadVelocity.magnitude));

            Vector3 newHeadMoveDirection = Vector3.Lerp(headPosition, HeadVelocity, multiplier).normalized;
            lastHeadPosition = headPosition;
            float directionAdjustmentRate = Mathf.Clamp01(5f * Time.fixedDeltaTime);

            HeadMovementDirection = Vector3.Slerp(HeadMovementDirection, newHeadMoveDirection, directionAdjustmentRate);

            if (MixedRealityRaycaster.DebugEnabled && gazeTransform != null)
            {
                Debug.DrawLine(lastHeadPosition, lastHeadPosition + HeadMovementDirection * 10f, Color.Lerp(Color.red, Color.green, multiplier));
                Debug.DrawLine(lastHeadPosition, lastHeadPosition + HeadVelocity, Color.yellow);
            }
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            GazePointer.BaseCursor?.SetVisibility(false);
            MixedRealityToolkit.InputSystem?.RaiseSourceLost(GazeInputSource);
        }

        #endregion MonoBehaviour Implementation

        #region IMixedRealityInputHandler Implementation

        /// <inheritdoc />
        void IMixedRealityInputHandler.OnInputUp(InputEventData eventData)
        {
            for (int i = 0; i < eventData.InputSource.Pointers.Length; i++)
            {
                if (eventData.InputSource.Pointers[i].PointerId == GazePointer.PointerId)
                {
                    MixedRealityToolkit.InputSystem.RaisePointerClicked(gazePointer, eventData.MixedRealityInputAction, 0);
                    MixedRealityToolkit.InputSystem.RaisePointerUp(gazePointer, eventData.MixedRealityInputAction);
                    return;
                }
            }
        }

        /// <inheritdoc />
        void IMixedRealityInputHandler.OnInputDown(InputEventData eventData)
        {
            for (int i = 0; i < eventData.InputSource.Pointers.Length; i++)
            {
                if (eventData.InputSource.Pointers[i].PointerId == GazePointer.PointerId)
                {
                    MixedRealityToolkit.InputSystem.RaisePointerDown(gazePointer, eventData.MixedRealityInputAction, eventData.InputSource);
                    return;
                }
            }
        }

        #endregion IMixedRealityInputHandler Implementation

        #region Utilities

        private IMixedRealityPointer InitializeGazePointer()
        {
            if (gazeTransform == null)
            {
                gazeTransform = CameraCache.Main.transform;
            }

            Debug.Assert(gazeTransform != null, "No gaze transform to raycast from!");

            gazePointer = new InternalGazePointer(this, "Gaze Pointer", null, raycastLayerMasks, maxGazeCollisionDistance, gazeTransform, stabilizer);

            if (GazeCursor == null &&
                MixedRealityToolkit.Instance.ActiveProfile.InputSystemProfile != null &&
                MixedRealityToolkit.Instance.ActiveProfile.InputSystemProfile.PointerProfile != null &&
                MixedRealityToolkit.Instance.ActiveProfile.InputSystemProfile.PointerProfile.GazeCursorPrefab != null)
            {
                var cursor = Instantiate(MixedRealityToolkit.Instance.ActiveProfile.InputSystemProfile.PointerProfile.GazeCursorPrefab, MixedRealityToolkit.Instance.MixedRealityPlayspace);
                SetGazeCursor(cursor);
            }

            return gazePointer;
        }

        private async void RaiseSourceDetected()
        {
            await WaitUntilInputSystemValid;
            MixedRealityToolkit.InputSystem.RaiseSourceDetected(GazeInputSource);
            GazePointer.BaseCursor?.SetVisibility(true);
        }

        /// <summary>
        /// Set the gaze cursor.
        /// </summary>
        public void SetGazeCursor(GameObject cursor)
        {
            Debug.Assert(cursor != null);
            cursor.transform.parent = transform.parent;
            GazePointer.BaseCursor = cursor.GetComponent<IMixedRealityCursor>();
            Debug.Assert(GazePointer.BaseCursor != null, "Failed to load cursor");
            GazePointer.BaseCursor.SetVisibilityOnSourceDetected = false;
            GazePointer.BaseCursor.Pointer = GazePointer;
        }

        #endregion Utilities
    }
}