#ifndef URP_VOLUMETRIC_CLOUDS_DEFINES_HLSL
#define URP_VOLUMETRIC_CLOUDS_DEFINES_HLSL

CBUFFER_START(UnityPerMaterial)
float _Seed;
half _NumPrimarySteps;
half _NumLightSteps;
half _MaxStepSize;
float _HighestCloudAltitude;
float _LowestCloudAltitude;
half4 _ShapeNoiseOffset;
half _VerticalShapeNoiseOffset;
half4 _WindDirection;
half4 _WindVector;
half _VerticalShapeWindDisplacement;
half _VerticalErosionWindDisplacement;
half _MediumWindSpeed;
half _SmallWindSpeed;
half _AltitudeDistortion;
half _DensityMultiplier;
half _PowderEffectIntensity;
half _ShapeScale;
half _ShapeFactor;
half _ErosionScale;
half _ErosionFactor;
half _ErosionOcclusion;
half _MicroErosionScale;
half _MicroErosionFactor;
half _FadeInStart;
half _FadeInDistance;
half _MultiScattering;
half4 _ScatteringTint;
half _AmbientProbeDimmer;
half _SunLightDimmer;
float _EarthRadius;
half _AccumulationFactor;
half _NormalizationFactor;
half _CloudNearPlane;
CBUFFER_END

// Ambient Probe (unity_SH)
half4 clouds_SHAr;
half4 clouds_SHAg;
half4 clouds_SHAb;
half4 clouds_SHBr;
half4 clouds_SHBg;
half4 clouds_SHBb;
half4 clouds_SHC;

half _ImprovedTransmittanceBlend;
float _PostExposure; // Exposure from the ColorAdjustments override
half3 _SunColor;

// Floating-origin compensation, driven by CirrusFS.Sim.VolumetricCloudsOrigin.
// xyz: metres to add back to a scene-space position to recover the position it had in the
// world the clouds were first sampled in, i.e. minus the sum of every origin shift so far.
// Must stay full float: it reaches into the millions of metres on a long flight.
float4 _CloudsOriginOffset;
// xyz: the translation applied to the world this frame, zero on every other frame. Used to
// pull last frame's reprojection back into the pre-shift world.
float4 _CloudsOriginShiftDelta;

#ifndef URP_PHYSICALLY_BASED_SKY_DEFINES_INCLUDED
float4 _PlanetCenterRadius;
#endif

#endif