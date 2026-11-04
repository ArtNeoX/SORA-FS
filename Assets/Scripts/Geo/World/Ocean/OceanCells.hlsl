// Where the ocean sphere steps aside for the terrain.
//
// Inside a 1° terrain tile that GlobeTerrain is drawing, the terrain draws its own water from
// its land classes (sea, lakes, rivers alike), so its shoreline is wherever the class says, and
// the ocean sphere would only be a second, disagreeing surface. GlobeTerrain publishes the
// cells it drew this frame; the ocean discards its pixels inside them. The ocean reaches a
// couple of metres into each cell, under the terrain's water (which sits a few centimetres up),
// so float error can never open a gap at a cell edge.
#ifndef CIRRUSFS_OCEAN_CELLS_INCLUDED
#define CIRRUSFS_OCEAN_CELLS_INCLUDED

float4x4 _OceanUnityToEcef;      // rotation only: Unity world direction -> ECEF direction (OceanSphere)
float3 _OceanOriginEcef;         // ECEF of Unity (0, 0, 0), metres (OceanSphere)
float4 _TerrainDrawnCells[16];   // xy = south-west corner (lat, lon) of a drawn tile (GlobeTerrain)
float _TerrainDrawnCellCount;

// True when a point of the ocean surface lies under terrain that is being drawn.
bool UnderDrawnTerrain(float3 positionWS)
{
    int count = (int)_TerrainDrawnCellCount;
    if (count <= 0) return false;

    float3 p = _OceanOriginEcef + mul((float3x3)_OceanUnityToEcef, positionWS);
    float lon = degrees(atan2(p.y, p.x));
    // geodetic latitude of a point on the ellipsoid: tan(lat) = z / ((1 - e^2) * r_xy)
    float lat = degrees(atan2(p.z, 0.99330562 * length(p.xy)));

    const float inset = 2e-5;    // about 2 m: the ocean runs a little under the terrain's water
    [loop] for (int k = 0; k < count && k < 16; k++)
    {
        float2 c = _TerrainDrawnCells[k].xy;
        if (lat > c.x + inset && lat < c.x + 1.0 - inset && lon > c.y + inset && lon < c.y + 1.0 - inset)
            return true;
    }
    return false;
}

#endif
