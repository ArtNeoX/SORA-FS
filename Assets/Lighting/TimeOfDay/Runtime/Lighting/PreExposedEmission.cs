using UnityEngine;

namespace CirrusFS.TimeOfDay
{
    /// Keeps a renderer's emission in scene light units under pre-exposure: sets the material's
    /// emission colour to `emission` x ExposureController.PreExposure through a property block.
    /// For displays, panel backlighting, light lenses and anything else that glows.
    ///
    /// Cost: a property block takes the renderer out of SRP batching. For many emissive objects
    /// sharing a custom shader, multiply by TodPreExposure() in the shader instead
    /// (AtmosphereCommon.hlsl).
    [ExecuteAlways]
    [RequireComponent(typeof(Renderer))]
    [DefaultExecutionOrder(340)]
    public class PreExposedEmission : MonoBehaviour
    {
        [Tooltip("Emission in scene light units, linear HDR. A daylight-readable display is ~0.5-1.")]
        [ColorUsage(false, true)] public Color emission = Color.white;

        [Tooltip("Shader property that holds the emission colour (_EmissionColor for URP Lit).")]
        public string colorProperty = "_EmissionColor";

        Renderer target;
        MaterialPropertyBlock block;
        int propertyId;
        int appliedVersion = -1;
        Color appliedEmission;

        void OnEnable()
        {
            target = GetComponent<Renderer>();
            block ??= new MaterialPropertyBlock();
            propertyId = Shader.PropertyToID(colorProperty);
            appliedVersion = -1;
        }

        void OnDisable()
        {
            if (target == null) return;
            target.SetPropertyBlock(null);
        }

        void OnValidate() => appliedVersion = -1;

        void LateUpdate()
        {
            if (appliedVersion == ExposureController.Version && appliedEmission == emission) return;
            if (propertyId != Shader.PropertyToID(colorProperty)) propertyId = Shader.PropertyToID(colorProperty);
            target.GetPropertyBlock(block);
            block.SetColor(propertyId, ExposureController.Expose(emission));
            target.SetPropertyBlock(block);
            appliedVersion = ExposureController.Version;
            appliedEmission = emission;
        }
    }
}
