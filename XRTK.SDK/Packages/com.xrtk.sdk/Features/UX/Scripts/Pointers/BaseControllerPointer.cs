﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Serialization;
using XRTK.Definitions.InputSystem;
using XRTK.Definitions.Physics;
using XRTK.EventDatum.Input;
using XRTK.EventDatum.Teleport;
using XRTK.Interfaces.InputSystem;
using XRTK.Interfaces.InputSystem.Handlers;
using XRTK.Interfaces.Physics;
using XRTK.Interfaces.Providers.Controllers;
using XRTK.Interfaces.TeleportSystem;
using XRTK.SDK.Input.Handlers;
using XRTK.Services;
using XRTK.Utilities.Async;
using XRTK.Utilities.Physics;

namespace XRTK.SDK.UX.Pointers
{
    /// <summary>
    /// Base Pointer class for pointers that exist in the scene as GameObjects.
    /// </summary>
    [DisallowMultipleComponent]
    public abstract class BaseControllerPointer : ControllerPoseSynchronizer,
        IMixedRealityPointer,
        IMixedRealityTeleportHandler
    {
        [SerializeField]
        private GameObject cursorPrefab = null;

        [SerializeField]
        private bool disableCursorOnStart = false;

        protected bool DisableCursorOnStart => disableCursorOnStart;

        [SerializeField]
        private bool setCursorVisibilityOnSourceDetected = false;

        private GameObject cursorInstance = null;

        [SerializeField]
        [Tooltip("Source transform for raycast origin - leave null to use default transform")]
        private Transform raycastOrigin = null;

        [SerializeField]
        [Tooltip("The hold action that will enable the raise the input event for this pointer.")]
        private MixedRealityInputAction activeHoldAction = MixedRealityInputAction.None;

        [SerializeField]
        [Tooltip("The action that will enable the raise the input event for this pointer.")]
        private MixedRealityInputAction pointerAction = MixedRealityInputAction.None;

        [SerializeField]
        [Tooltip("Does the interaction require hold?")]
        private bool requiresHoldAction = false;

        [SerializeField]
        [Tooltip("Enables the pointer ray when the pointer is started.")]
        private bool enablePointerOnStart = false;

        /// <summary>
        /// True if select is pressed right now
        /// </summary>
        protected bool IsSelectPressed = false;

        /// <summary>
        /// True if select has been pressed once since this component was enabled
        /// </summary>
        protected bool HasSelectPressedOnce = false;

        protected bool IsHoldPressed = false;

        protected bool IsTeleportRequestActive = false;

        private bool lateRegisterTeleport = true;

        /// <summary>
        /// The forward direction of the targeting ray
        /// </summary>
        public virtual Vector3 PointerDirection => raycastOrigin != null ? raycastOrigin.forward : transform.forward;

        /// <summary>
        /// Set a new cursor for this <see cref="IMixedRealityPointer"/>
        /// </summary>
        /// <remarks>This <see cref="GameObject"/> must have a <see cref="IMixedRealityCursor"/> attached to it.</remarks>
        /// <param name="newCursor">The new cursor</param>
        public virtual void SetCursor(GameObject newCursor = null)
        {
            if (cursorInstance != null)
            {
                if (Application.isEditor)
                {
                    DestroyImmediate(cursorInstance);
                }
                else
                {
                    Destroy(cursorInstance);
                }

                cursorInstance = newCursor;
            }

            if (cursorInstance == null && cursorPrefab != null)
            {
                cursorInstance = Instantiate(cursorPrefab, transform);
            }

            if (cursorInstance != null)
            {
                cursorInstance.name = $"{Handedness}_{name}_Cursor";
                BaseCursor = cursorInstance.GetComponent<IMixedRealityCursor>();

                if (BaseCursor != null)
                {
                    BaseCursor.DefaultCursorDistance = PointerExtent;
                    BaseCursor.Pointer = this;
                    BaseCursor.SetVisibilityOnSourceDetected = setCursorVisibilityOnSourceDetected;
                    BaseCursor.SetVisibility(!disableCursorOnStart);
                }
                else
                {
                    Debug.LogError($"No IMixedRealityCursor component found on {cursorInstance.name}");
                }
            }
        }

        #region MonoBehaviour Implementation

        protected override void OnEnable()
        {
            base.OnEnable();

            if (!lateRegisterTeleport &&
                MixedRealityToolkit.TeleportSystem != null &&
                MixedRealityToolkit.Instance.ActiveProfile.IsTeleportSystemEnabled)
            {
                MixedRealityToolkit.TeleportSystem.Register(gameObject);
            }
        }

        protected override async void Start()
        {
            base.Start();

            if (lateRegisterTeleport && MixedRealityToolkit.Instance.ActiveProfile.IsTeleportSystemEnabled)
            {
                await new WaitUntil(() => MixedRealityToolkit.TeleportSystem != null);

                // We've been destroyed during the await.
                if (this == null) { return; }

                lateRegisterTeleport = false;
                MixedRealityToolkit.TeleportSystem.Register(gameObject);
            }

            await WaitUntilInputSystemValid;

            // We've been destroyed during the await.
            if (this == null) { return; }

            SetCursor();
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            MixedRealityToolkit.TeleportSystem?.Unregister(gameObject);

            IsHoldPressed = false;
            IsSelectPressed = false;
            HasSelectPressedOnce = false;
            BaseCursor?.SetVisibility(false);
        }

        #endregion  MonoBehaviour Implementation

        #region IMixedRealityPointer Implementation

        /// <inheritdoc cref="IMixedRealityController" />
        public override IMixedRealityController Controller
        {
            get => base.Controller;
            set
            {
                base.Controller = value;
                pointerName = gameObject.name;
                InputSourceParent = base.Controller.InputSource;
            }
        }

        private uint pointerId;

        /// <inheritdoc />
        public uint PointerId
        {
            get
            {
                if (pointerId == 0)
                {
                    pointerId = MixedRealityToolkit.InputSystem.FocusProvider.GenerateNewPointerId();
                }

                return pointerId;
            }
        }

        private string pointerName = string.Empty;

        /// <inheritdoc />
        public string PointerName
        {
            get => pointerName;
            set
            {
                pointerName = value;
                gameObject.name = value;
            }
        }

        /// <inheritdoc />
        public IMixedRealityInputSource InputSourceParent { get; protected set; }

        /// <inheritdoc />
        public IMixedRealityCursor BaseCursor { get; set; }

        /// <inheritdoc />
        public ICursorModifier CursorModifier { get; set; }

        /// <inheritdoc />
        public IMixedRealityTeleportHotSpot TeleportHotSpot { get; set; }

        /// <inheritdoc />
        public virtual bool IsInteractionEnabled
        {
            get
            {
                if (IsTeleportRequestActive)
                {
                    return false;
                }

                if (requiresHoldAction && IsHoldPressed)
                {
                    return true;
                }

                if (IsSelectPressed)
                {
                    return true;
                }

                return HasSelectPressedOnce || enablePointerOnStart;
            }
        }

        /// <inheritdoc />
        public bool IsFocusLocked { get; set; }

        /// <inheritdoc />
        public bool SyncPointerTargetPosition { get; set; }

        [SerializeField]
        private bool overrideGlobalPointerExtent = false;

        [NonSerialized]
        private float pointerExtent;

        /// <inheritdoc />
        public float PointerExtent
        {
            get
            {
                if (overrideGlobalPointerExtent)
                {
                    if (MixedRealityToolkit.InputSystem?.FocusProvider != null)
                    {
                        return MixedRealityToolkit.InputSystem.FocusProvider.GlobalPointingExtent;
                    }
                }

                if (pointerExtent.Equals(0f))
                {
                    pointerExtent = defaultPointerExtent;
                }

                Debug.Assert(pointerExtent > 0f);
                return pointerExtent;
            }
            set
            {
                pointerExtent = value;
                Debug.Assert(pointerExtent > 0f, "Cannot set the pointer extent to 0. Resetting to the default pointer extent");
                overrideGlobalPointerExtent = false;
            }
        }

        [Min(0.01f)]
        [SerializeField]
        [FormerlySerializedAs("pointerExtent")]
        private float defaultPointerExtent = 10f;

        /// <inheritdoc />
        public float DefaultPointerExtent => defaultPointerExtent;

        /// <inheritdoc />
        public RayStep[] Rays { get; protected set; } = { new RayStep(Vector3.zero, Vector3.forward) };

        /// <inheritdoc />
        public LayerMask[] PrioritizedLayerMasksOverride { get; set; } = null;

        /// <inheritdoc />
        public IMixedRealityFocusHandler FocusHandler { get; set; }

        /// <inheritdoc />
        public IMixedRealityInputHandler InputHandler { get; set; }

        /// <inheritdoc />
        public IPointerResult Result { get; set; }

        /// <inheritdoc />
        public IBaseRayStabilizer RayStabilizer { get; set; } = new GenericStabilizer();

        /// <inheritdoc />
        public RaycastMode RaycastMode { get; set; } = RaycastMode.Simple;

        /// <inheritdoc />
        public float SphereCastRadius { get; set; } = 0.1f;

        [SerializeField]
        [Range(0f, 360f)]
        [Tooltip("The Y orientation of the pointer - used for rotation and navigation")]
        private float pointerOrientation = 0f;

        /// <inheritdoc />
        public virtual float PointerOrientation
        {
            get => pointerOrientation + (raycastOrigin != null ? raycastOrigin.eulerAngles.y : transform.eulerAngles.y);
            set => pointerOrientation = value < 0
                        ? Mathf.Clamp(value, -360f, 0f)
                        : Mathf.Clamp(value, 0f, 360f);
        }

        /// <inheritdoc />
        public virtual void OnPreRaycast() { }

        /// <inheritdoc />
        public virtual void OnPostRaycast() { }

        /// <inheritdoc />
        public virtual bool TryGetPointerPosition(out Vector3 position)
        {
            position = raycastOrigin != null ? raycastOrigin.position : transform.position;
            return true;
        }

        /// <inheritdoc />
        public virtual bool TryGetPointingRay(out Ray pointingRay)
        {
            TryGetPointerPosition(out var pointerPosition);
            pointingRay = pointerRay;
            pointingRay.origin = pointerPosition;
            pointingRay.direction = PointerDirection;
            return true;
        }

        private readonly Ray pointerRay = new Ray();

        /// <inheritdoc />
        public virtual bool TryGetPointerRotation(out Quaternion rotation)
        {
            var pointerRotation = raycastOrigin != null ? raycastOrigin.eulerAngles : transform.eulerAngles;
            rotation = Quaternion.Euler(pointerRotation.x, PointerOrientation, pointerRotation.z);
            return true;
        }

        #region IEquality Implementation

        private static bool Equals(IMixedRealityPointer left, IMixedRealityPointer right)
        {
            return left.Equals(right);
        }

        /// <inheritdoc />
        bool IEqualityComparer.Equals(object left, object right)
        {
            return left.Equals(right);
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) { return false; }
            if (ReferenceEquals(this, obj)) { return true; }
            if (obj.GetType() != GetType()) { return false; }

            return Equals((IMixedRealityPointer)obj);
        }

        private bool Equals(IMixedRealityPointer other)
        {
            return other != null && PointerId == other.PointerId && string.Equals(PointerName, other.PointerName);
        }

        /// <inheritdoc />
        int IEqualityComparer.GetHashCode(object obj)
        {
            return obj.GetHashCode();
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            unchecked
            {
                int hashCode = 0;
                hashCode = (hashCode * 397) ^ (int)PointerId;
                hashCode = (hashCode * 397) ^ (PointerName != null ? PointerName.GetHashCode() : 0);
                return hashCode;
            }
        }

        #endregion IEquality Implementation

        #endregion IMixedRealityPointer Implementation

        #region IMixedRealitySourcePoseHandler Implementation

        /// <inheritdoc />
        public override void OnSourceLost(SourceStateEventData eventData)
        {
            base.OnSourceLost(eventData);

            if (eventData.SourceId == InputSourceParent.SourceId)
            {
                if (requiresHoldAction)
                {
                    IsHoldPressed = false;
                }

                if (IsSelectPressed)
                {
                    MixedRealityToolkit.InputSystem.RaisePointerUp(this, pointerAction);
                }

                IsSelectPressed = false;
            }
        }

        #endregion IMixedRealitySourcePoseHandler Implementation

        #region IMixedRealityInputHandler Implementation

        /// <inheritdoc />
        public override void OnInputUp(InputEventData eventData)
        {
            base.OnInputUp(eventData);

            if (eventData.SourceId == InputSourceParent.SourceId)
            {
                if (requiresHoldAction && eventData.MixedRealityInputAction == activeHoldAction)
                {
                    IsHoldPressed = false;
                }

                if (eventData.MixedRealityInputAction == pointerAction)
                {
                    IsSelectPressed = false;
                    MixedRealityToolkit.InputSystem.RaisePointerClicked(this, pointerAction, 0);
                    MixedRealityToolkit.InputSystem.RaisePointerUp(this, pointerAction);
                }
            }
        }

        /// <inheritdoc />
        public override void OnInputDown(InputEventData eventData)
        {
            base.OnInputDown(eventData);

            if (eventData.SourceId == InputSourceParent.SourceId)
            {
                if (requiresHoldAction && eventData.MixedRealityInputAction == activeHoldAction)
                {
                    IsHoldPressed = true;
                }

                if (eventData.MixedRealityInputAction == pointerAction)
                {
                    IsSelectPressed = true;
                    HasSelectPressedOnce = true;
                    MixedRealityToolkit.InputSystem.RaisePointerDown(this, pointerAction);
                }
            }
        }

        #endregion  IMixedRealityInputHandler Implementation

        #region IMixedRealityTeleportHandler Implementation

        /// <inheritdoc />
        public virtual void OnTeleportRequest(TeleportEventData eventData)
        {
            // Only turn off pointers that aren't making the request.
            IsTeleportRequestActive = true;
            BaseCursor?.SetVisibility(false);
        }

        /// <inheritdoc />
        public virtual void OnTeleportStarted(TeleportEventData eventData)
        {
            // Turn off all pointers while we teleport.
            IsTeleportRequestActive = true;
            BaseCursor?.SetVisibility(false);
        }

        /// <inheritdoc />
        public virtual void OnTeleportCompleted(TeleportEventData eventData)
        {
            // Turn all our pointers back on.
            IsTeleportRequestActive = false;
            BaseCursor?.SetVisibility(true);
        }

        /// <inheritdoc />
        public virtual void OnTeleportCanceled(TeleportEventData eventData)
        {
            // Turn all our pointers back on.
            IsTeleportRequestActive = false;
            BaseCursor?.SetVisibility(true);
        }

        #endregion IMixedRealityTeleportHandler Implementation
    }
}
