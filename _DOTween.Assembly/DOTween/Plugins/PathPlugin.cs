﻿// Author: Daniele Giardini - http://www.demigiant.com
// Created: 2014/09/03 19:36
// 
// License Copyright (c) Daniele Giardini.
// This work is subject to the terms at http://dotween.demigiant.com/license.php

using DG.Tweening.Core;
using DG.Tweening.Core.Easing;
using DG.Tweening.Core.Enums;
using DG.Tweening.Plugins.Core;
using DG.Tweening.Plugins.Core.PathCore;
using DG.Tweening.Plugins.Options;
using UnityEngine;

#pragma warning disable 1591
namespace DG.Tweening.Plugins
{
    /// <summary>
    /// Path plugin works exclusively with Transforms
    /// </summary>
    public class PathPlugin : ABSTweenPlugin<Vector3, Path, PathOptions>
    {
        public const float MinLookAhead = 0.0001f;

        public override void Reset(TweenerCore<Vector3, Path, PathOptions> t)
        {
            t.endValue.Destroy(); // Clear path
            t.startValue = t.endValue = t.changeValue = null;
        }

        public override void SetFrom(TweenerCore<Vector3, Path, PathOptions> t, bool isRelative) {}
        public override void SetFrom(TweenerCore<Vector3, Path, PathOptions> t, Path fromValue, bool setImmediately, bool isRelative) {}

        public static ABSTweenPlugin<Vector3, Path, PathOptions> Get()
        {
            return PluginsManager.GetCustomPlugin<PathPlugin, Vector3, Path, PathOptions>();
        }

        public override Path ConvertToStartValue(TweenerCore<Vector3, Path, PathOptions> t, Vector3 value)
        {
            // Simply sets the same path as start and endValue
            return t.endValue;
        }

        public override void SetRelativeEndValue(TweenerCore<Vector3, Path, PathOptions> t)
        {
            if (t.endValue.isFinalized) return;

            Vector3 startP = t.getter();
            int count = t.endValue.wps.Length;
            for (int i = 0; i < count; ++i) t.endValue.wps[i] += startP;
        }

        // Recreates waypoints with correct control points and eventual additional starting point
        // then sets the final path version
        public override void SetChangeValue(TweenerCore<Vector3, Path, PathOptions> t)
        {
            Transform trans = ((Component)t.target).transform;
            // if (t.plugOptions.orientType == OrientType.ToPath && t.plugOptions.useLocalPosition) t.plugOptions.parent = trans.parent;
            if (t.plugOptions.orientType == OrientType.ToPath) t.plugOptions.parent = trans.parent;

            if (t.endValue.isFinalized) {
                t.changeValue = t.endValue;
                return;
            }

            Vector3 currVal = t.getter();
            Path path = t.endValue;
            path.plugOptions = t.plugOptions;
            int unmodifiedWpsLen = path.wps.Length;
            int additionalWps = 0;
            bool hasAdditionalStartingP = false, hasAdditionalEndingP = false;
            
            // Create final wps and add eventual starting/ending waypoints.
            if (unmodifiedWpsLen <= path.minInputWaypoints || !DOTweenUtils.Vector3AreApproximatelyEqual(path.wps[0], currVal)) {
                hasAdditionalStartingP = true;
                additionalWps += 1;
            }
            if (t.plugOptions.isClosedPath) {
                Vector3 endWp = path.wps[unmodifiedWpsLen - 1];
                if (path.type == PathType.CubicBezier) {
                    if (unmodifiedWpsLen < 3) {
                        Debug.LogError(
                            "CubicBezier paths must contain waypoints in multiple of 3 excluding the starting point added automatically by DOTween" +
                            " (1: waypoint, 2: IN control point, 3: OUT control point — the minimum amount of waypoints for a single curve is 3)"
                        );
                    } else endWp = path.wps[unmodifiedWpsLen - 3];
                }
                if (endWp != currVal) {
                    hasAdditionalEndingP = true;
                    additionalWps += 1;
                }
            }
            int wpsLen = unmodifiedWpsLen + additionalWps;
            Vector3[] wps = new Vector3[wpsLen];
            int indMod = hasAdditionalStartingP ? 1 : 0;
            if (hasAdditionalStartingP) wps[0] = currVal;
            for (int i = 0; i < unmodifiedWpsLen; ++i) wps[i + indMod] = path.wps[i];
            if (hasAdditionalEndingP) wps[wps.Length - 1] = wps[0];
            path.wps = wps;

            // Finalize path
            path.addedExtraStartWp = hasAdditionalStartingP;
            path.addedExtraEndWp = hasAdditionalEndingP;
            path.FinalizePath(t.plugOptions.isClosedPath, t.plugOptions.lockPositionAxis, currVal);

            t.plugOptions.startupRot = trans.rotation;
            t.plugOptions.startupZRot = trans.eulerAngles.z;

            // Set changeValue as a reference to endValue
            t.changeValue = t.endValue;
        }

        public override float GetSpeedBasedDuration(PathOptions options, float unitsXSecond, Path changeValue)
        {
            return changeValue.length / unitsXSecond;
        }

        public override void EvaluateAndApply(PathOptions options, Tween t, bool isRelative, DOGetter<Vector3> getter, DOSetter<Vector3> setter, float elapsed, Path startValue, Path changeValue, float duration, bool usingInversePosition, UpdateNotice updateNotice)
        {
            if (t.loopType == LoopType.Incremental && !options.isClosedPath) {
                int increment = (t.isComplete ? t.completedLoops - 1 : t.completedLoops);
                if (increment > 0) changeValue = changeValue.CloneIncremental(increment);
            }

            float pathPerc = EaseManager.Evaluate(t.easeType, t.customEase, elapsed, duration, t.easeOvershootOrAmplitude, t.easePeriod);
            float constantPathPerc = changeValue.ConvertToConstantPathPerc(pathPerc);
            Vector3 newPos = changeValue.GetPoint(constantPathPerc);
            changeValue.targetPosition = newPos; // Used to draw editor gizmos
            setter(newPos);

            if (options.mode != PathMode.Ignore && options.orientType != OrientType.None) SetOrientation(options, t, changeValue, constantPathPerc, newPos, updateNotice);

            // Determine if current waypoint changed and eventually dispatch callback
            bool isForward = !usingInversePosition;
            if (t.isBackwards) isForward = !isForward;
            int newWaypointIndex = changeValue.GetWaypointIndexFromPerc(pathPerc, isForward);
            if (newWaypointIndex != t.miscInt) {
                int prevWPIndex = t.miscInt;
                t.miscInt = newWaypointIndex;
                if (t.onWaypointChange != null) {
                    // If more than one waypoint changed, dispatch multiple callbacks
//                    bool isBackwards = newWaypointIndex < prevWPIndex;
                    bool isBackwards = t.isBackwards;
                    if (t.hasLoops && t.loopType == LoopType.Yoyo) {
                        isBackwards = !t.isBackwards && t.completedLoops % 2 != 0
                                      || t.isBackwards && t.completedLoops % 2 == 0;
                    }
                    if (isBackwards) {
//                        for (int i = prevWPIndex - 1; i > newWaypointIndex - 1; --i) Tween.OnTweenCallback(t.onWaypointChange, i);
                        for (int i = prevWPIndex - 1; i > newWaypointIndex - 1; --i) Tween.OnTweenCallback(t.onWaypointChange, t, i);
                    } else {
//                        for (int i = prevWPIndex + 1; i < newWaypointIndex + 1; ++i) Tween.OnTweenCallback(t.onWaypointChange, i);
                        for (int i = prevWPIndex + 1; i < newWaypointIndex; ++i) Tween.OnTweenCallback(t.onWaypointChange, t, i);
                    }
                    Tween.OnTweenCallback(t.onWaypointChange, t, newWaypointIndex);
                }
            }
        }

        // Public so it can be called by GotoWaypoint
        public void SetOrientation(PathOptions options, Tween t, Path path, float pathPerc, Vector3 tPos, UpdateNotice updateNotice)
        {
            Transform trans = ((Component)t.target).transform;
            Quaternion newRot = Quaternion.identity;
            Vector3 transP = trans.position;

            if (updateNotice == UpdateNotice.RewindStep) {
                // Reset orientation before continuing
                trans.rotation = options.startupRot;
            }

            switch (options.orientType) {
            case OrientType.LookAtPosition:
                path.lookAtPosition = options.lookAtPosition; // Used to draw editor gizmos
//                newRot = Quaternion.LookRotation(options.lookAtPosition - transP, trans.up);
                newRot = Quaternion.LookRotation(options.lookAtPosition - transP, options.stableZRotation ? Vector3.up : trans.up);
                break;
            case OrientType.LookAtTransform:
                if (options.lookAtTransform != null) {
                    path.lookAtPosition = options.lookAtTransform.position; // Used to draw editor gizmos
//                    newRot = Quaternion.LookRotation(options.lookAtTransform.position - transP, trans.up);
                    newRot = Quaternion.LookRotation(options.lookAtTransform.position - transP, options.stableZRotation ? Vector3.up : trans.up);
                }
                break;
            case OrientType.ToPath:
                Vector3 lookAtP;
                if (path.type == PathType.Linear && options.lookAhead <= MinLookAhead) {
                    // Calculate lookAhead so that it doesn't turn until it starts moving on next waypoint
                    lookAtP = tPos + path.wps[path.linearWPIndex] - path.wps[path.linearWPIndex - 1];
                } else {
                    float lookAheadPerc = pathPerc + options.lookAhead;
                    if (lookAheadPerc > 1) lookAheadPerc = (options.isClosedPath ? lookAheadPerc - 1 : path.type == PathType.Linear ? 1 : 1.00001f);
                    lookAtP = path.GetPoint(lookAheadPerc);
                }
                if (path.type == PathType.Linear) {
                    // Check if it's the last waypoint, and keep correct direction
                    Vector3 lastWp = path.wps[path.wps.Length - 1];
                    if (lookAtP == lastWp) lookAtP = tPos == lastWp ? lastWp + (lastWp - path.wps[path.wps.Length - 2]) : lastWp;
                }
                Vector3 transUp = trans.up;
                // Apply basic modification for local position movement
                bool hasParent = options.parent != null;
                bool hasLocalPositionAndParent = options.useLocalPosition && hasParent;
                if (hasLocalPositionAndParent) lookAtP = options.parent.TransformPoint(lookAtP);
                // LookAt axis constraint
                if (options.lockRotationAxis != AxisConstraint.None) {
                    if ((options.lockRotationAxis & AxisConstraint.X) == AxisConstraint.X) {
                        Vector3 v0 = trans.InverseTransformPoint(lookAtP);
                        v0.y = 0;
                        lookAtP = trans.TransformPoint(v0);
                        transUp = hasLocalPositionAndParent ? options.parent.up : Vector3.up;
                    }
                    if ((options.lockRotationAxis & AxisConstraint.Y) == AxisConstraint.Y) {
                        Vector3 v0 = trans.InverseTransformPoint(lookAtP);
                        if (v0.z < 0) v0.z = -v0.z;
                        v0.x = 0;
                        lookAtP = trans.TransformPoint(v0);
                    }
                    if ((options.lockRotationAxis & AxisConstraint.Z) == AxisConstraint.Z) {
                        // Fix to allow racing loops to keep cars straight and not flip it
                        if (hasLocalPositionAndParent) transUp = options.parent.TransformDirection(Vector3.up);
                        else transUp = trans.TransformDirection(Vector3.up);
                        transUp.z = options.startupZRot;
                    }
                }
                if (options.mode == PathMode.Full3D) {
                    // 3D path
                    Vector3 diff = lookAtP - transP;
                    if (diff == Vector3.zero) diff = trans.forward;
                    if (hasParent) {
                        // Adapt diff to parent scale, fixes problems with non-uniform parent scale
                        diff = DivideVectorByVector(diff, options.parent.localScale);
                    }
                    newRot = Quaternion.LookRotation(diff, transUp);
                } else {
                    // 2D path
                    if (hasParent) {
                        // Adapt diff to parent scale, fixes problems with non-uniform parent scale
                        Vector3 diffScaled = DivideVectorByVector(lookAtP - transP, options.parent.localScale);
                        lookAtP = transP + diffScaled;
                    }
                    float rotY = 0;
                    float rotZ = DOTweenUtils.Angle2D(transP, lookAtP);
                    if (rotZ < 0) rotZ = 360 + rotZ;
                    if (options.mode == PathMode.Sidescroller2D) {
                        // Manage Y and modified Z rotation
                        rotY = lookAtP.x < transP.x ? 180 : 0;
                        if (rotZ > 90 && rotZ < 270) rotZ = 180 - rotZ;
                    }
                    newRot = Quaternion.Euler(0, rotY, rotZ);
                }
                break;
            }

            if (options.hasCustomForwardDirection) newRot *= options.forward;
            DOTweenExternalCommand.Dispatch_SetOrientationOnPath(options, t, newRot, trans);
//#if RIGIDBODY
//            if (options.isRigidbody) ((Rigidbody)t.target).rotation = newRot;
//            else trans.rotation = newRot;
//#else
//            trans.rotation = newRot;
//#endif
        }

        Vector3 DivideVectorByVector(Vector3 vector, Vector3 byVector)
        {
            return new Vector3(vector.x / byVector.x, vector.y / byVector.y, vector.z / byVector.z);
        }

        Vector3 MultiplyVectorByVector(Vector3 vector, Vector3 byVector)
        {
            return new Vector3(vector.x * byVector.x, vector.y * byVector.y, vector.z * byVector.z);
        }
    }
}