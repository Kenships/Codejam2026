// Sprite shader that shows the weighted HSV blend wherever this sprite
// overlaps a neighbouring ColorBody. Neighbour data is supplied per renderer
// by ColorOverlapBlender. Both overlapping sprites compute the same blend for
// the shared region, so draw order doesn't matter.
//
// Colours are passed in sRGB (same values as Color in C#) so the HSV maths
// matches ColorPhysics.AverageHSV exactly; the result is converted to linear
// at the end when the project uses linear colour space.
Shader "Codejam/ColorOverlapBlend"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}

        // Fraction of each body's radius over which its influence fades in
        // from the edge. 0 = hard edge, higher = wider, muddier gradient.
        _Softness ("Blend Softness", Range(0, 1)) = 0.5
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
        }

        Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
        Cull Off
        ZWrite Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        // Must match ColorOverlapBlender.MaxNeighbors
        #define MAX_NEIGHBORS 4

        TEXTURE2D(_MainTex);
        SAMPLER(sampler_MainTex);

        float _Softness;

        // Per-renderer data from ColorOverlapBlender
        float4 _SelfColor;                          // sRGB
        float  _SelfWeight;                         // area
        float4 _SelfFrame;                          // same layout as _NeighborFrames
        float4 _SelfExtents;                        // xy = half size along local X/Y
        float  _NeighborCount;
        float4 _NeighborColors[MAX_NEIGHBORS];      // sRGB
        float4 _NeighborFrames[MAX_NEIGHBORS];      // xy = centre, zw = local X axis (world, unit)
        float4 _NeighborExtents[MAX_NEIGHBORS];     // xy = half size along local X/Y, z = area weight

        struct Attributes
        {
            float3 positionOS : POSITION;
            float4 color      : COLOR;
            float2 uv         : TEXCOORD0;
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float4 color      : COLOR;
            float2 uv         : TEXCOORD0;
            float2 positionWS : TEXCOORD1;
        };

        float3 OverlapRgbToHsv(float3 c)
        {
            float4 k = float4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
            float4 p = lerp(float4(c.bg, k.wz), float4(c.gb, k.xy), step(c.b, c.g));
            float4 q = lerp(float4(p.xyw, c.r), float4(c.r, p.yzx), step(p.x, c.r));
            float d = q.x - min(q.w, q.y);
            float e = 1.0e-10;
            return float3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
        }

        float3 OverlapHsvToRgb(float3 c)
        {
            float3 p = abs(frac(c.xxx + float3(1.0, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0);
            return c.z * lerp(1.0, saturate(p - 1.0), c.y);
        }

        float3 OverlapSrgbToLinear(float3 c)
        {
            float3 lo = c / 12.92;
            float3 hi = pow((c + 0.055) / 1.055, 2.4);
            return lerp(hi, lo, step(c, 0.04045));
        }

        // Same rules as ColorPhysics.AverageHSV: hue along the shortest arc,
        // a grey (no saturation) takes the other colour's hue.
        float3 BlendHsv(float3 a, float3 b, float t)
        {
            float3 ha = OverlapRgbToHsv(a);
            float3 hb = OverlapRgbToHsv(b);

            float h;

            if (ha.y < 0.0001)
                h = hb.x;
            else if (hb.y < 0.0001)
                h = ha.x;
            else
            {
                float delta = hb.x - ha.x;
                delta -= round(delta);          // shortest signed arc, [-0.5, 0.5]
                h = frac(ha.x + delta * t);
            }

            return OverlapHsvToRgb(float3(
                h,
                lerp(ha.y, hb.y, t),
                lerp(ha.z, hb.z, t)
            ));
        }

        // 0 outside the body's ellipse, ramping smoothly to 1 at _Softness
        // of the way in from the edge. Self and neighbours use the same
        // function, so both overlapping sprites compute identical weights.
        float ShapeInfluence(float2 positionWS, float4 frame, float2 extents)
        {
            float2 offset = positionWS - frame.xy;
            float2 axisX = frame.zw;
            float2 axisY = float2(-axisX.y, axisX.x);

            // Position in the body's unit circle (handles squash)
            float2 local = float2(
                dot(offset, axisX) / extents.x,
                dot(offset, axisY) / extents.y
            );

            float dist = length(local);

            // Never sharper than a pixel, so a 0 softness stays anti-aliased
            float ramp = max(_Softness, fwidth(dist));

            return smoothstep(0.0, ramp, 1.0 - dist);
        }

        Varyings vert(Attributes input)
        {
            Varyings output;

            float3 positionWS = TransformObjectToWorld(input.positionOS);

            output.positionCS = TransformWorldToHClip(positionWS);
            output.positionWS = positionWS.xy;
            output.color = input.color;
            output.uv = input.uv;

            return output;
        }

        half4 frag(Varyings input) : SV_Target
        {
            float4 tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);

            // Every body's weight is area * how far inside it this pixel is,
            // so the overlap fades from one colour to the other instead of
            // switching at an edge. Where no neighbour reaches, only self
            // contributes and the colour is just self.
            float3 color = _SelfColor.rgb;
            float totalWeight =
                _SelfWeight *
                ShapeInfluence(input.positionWS, _SelfFrame, _SelfExtents.xy);

            // Fold neighbours in one at a time. For a single neighbour this
            // is AverageHSV(self, neighbour, selfWeight, neighbourWeight),
            // which is symmetric, so both sprites agree on the colour.
            // Fixed-length unrolled loop (unused slots masked to zero weight)
            // so fwidth never sits in divergent flow control.
            [unroll]
            for (int n = 0; n < MAX_NEIGHBORS; n++)
            {
                float active = step(n + 0.5, _NeighborCount);

                float influence = ShapeInfluence(
                    input.positionWS,
                    _NeighborFrames[n],
                    _NeighborExtents[n].xy
                );

                float weight = _NeighborExtents[n].z * influence * active;

                if (weight > 0.0)
                {
                    color = BlendHsv(color, _NeighborColors[n].rgb, weight / (totalWeight + weight));
                    totalWeight += weight;
                }
            }

            #ifndef UNITY_COLORSPACE_GAMMA
            color = OverlapSrgbToLinear(color);
            #endif

            return half4(color * tex.rgb, tex.a * input.color.a);
        }
        ENDHLSL

        // 2D Renderer
        Pass
        {
            Tags { "LightMode" = "Universal2D" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            ENDHLSL
        }

        // Universal (forward) Renderer fallback
        Pass
        {
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            ENDHLSL
        }
    }
}
