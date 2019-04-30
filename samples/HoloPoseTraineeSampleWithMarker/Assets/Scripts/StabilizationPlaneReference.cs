using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using HoloToolkit.Unity;

// Allows for overriding the stabilization plane distance based on a specific object.
public class StabilizationPlaneReference : HoloToolkit.Unity.Singleton<StabilizationPlaneReference> {

    public bool OverrideStabilizationPlaneDistance = true;

    private bool _initialUseGazeManager = false;
    private float _initialDefaultPlaneDistance = 2.0f;

	// Use this for initialization
	void Start () {
        _initialUseGazeManager = StabilizationPlaneModifier.Instance.UseGazeManager;
        _initialDefaultPlaneDistance = StabilizationPlaneModifier.Instance.DefaultPlaneDistance;

    }
	
	void LateUpdate () {
		if (OverrideStabilizationPlaneDistance)
        {
            Vector3 targetWorldPosition = transform.position;
            
            Vector3 targetPositionInCameraSpace = Camera.main.transform.InverseTransformPoint(targetWorldPosition);

            float zCameraToTarget = targetPositionInCameraSpace.z;
            var nearClipPlane = CameraCache.Main.nearClipPlane;
            
            if (zCameraToTarget > nearClipPlane)
            {
                StabilizationPlaneModifier.Instance.UseGazeManager = false;
                StabilizationPlaneModifier.Instance.DefaultPlaneDistance = zCameraToTarget;
            }
            else
            {
                StabilizationPlaneModifier.Instance.UseGazeManager = _initialUseGazeManager;
                StabilizationPlaneModifier.Instance.DefaultPlaneDistance = _initialDefaultPlaneDistance;
            }

            
        } else
        {
            StabilizationPlaneModifier.Instance.UseGazeManager = _initialUseGazeManager;
            StabilizationPlaneModifier.Instance.DefaultPlaneDistance = _initialDefaultPlaneDistance;
        }
	}
}
