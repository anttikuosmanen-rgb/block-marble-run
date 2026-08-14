Shader "Block Marble Run/Infinite Stud Grid"
{
    // Ground for an unbounded world (DESIGN.md §4.2). Drawn on one large quad in world space, so
    // the grid appears infinite at constant cost and with no geometry to generate. Never collided
    // against - placement solves the ground analytically.
    Properties
    {
        _BaseColor      ("Base Color", Color) = (0.13, 0.14, 0.16, 1)
        _LineColor      ("Line Color", Color) = (0.30, 0.33, 0.38, 1)
        _StudColor      ("Stud Color", Color) = (0.38, 0.42, 0.48, 1)
        _AxisColor      ("Axis Color", Color) = (0.55, 0.62, 0.72, 1)
        _StudPitch      ("Stud Pitch (world units)", Float) = 0.16
        _StudRadius     ("Stud Radius (fraction of pitch)", Range(0.05, 0.5)) = 0.22
        _LineWidth      ("Line Width (pixels)", Range(0.5, 4)) = 1.0
        _FadeStart      ("Fade Start (world units)", Float) = 4.0
        _FadeEnd        ("Fade End (world units)", Float) = 26.0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "ForwardUnlit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float  fogCoord   : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _LineColor;
                float4 _StudColor;
                float4 _AxisColor;
                float  _StudPitch;
                float  _StudRadius;
                float  _LineWidth;
                float  _FadeStart;
                float  _FadeEnd;
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs positions = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = positions.positionCS;
                output.positionWS = positions.positionWS;

                // Without this the grid stays crisp all the way to its own edge while everything
                // around it fades, and the edge is exactly what the fog is there to hide.
                output.fogCoord = ComputeFogFactor(positions.positionCS.z);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float2 world = input.positionWS.xz;

                // Screen-space derivative of the world position gives the size of one pixel in world
                // units. Scaling every feature by it keeps lines a constant pixel width at any zoom,
                // which is what stops the grid aliasing into moire when the camera pulls back.
                float2 pixel = fwidth(world);

                float2 cell = world / _StudPitch;
                float2 toEdge = abs(frac(cell) - 0.5);
                float2 edgeWidth = pixel / _StudPitch * _LineWidth;
                float2 lines = smoothstep(0.5 - edgeWidth, 0.5, toEdge);
                float lineMask = max(lines.x, lines.y);

                // A dot per stud, so the ground reads as a baseplate rather than graph paper.
                float2 toCentre = frac(cell) - 0.5;
                float radius = length(toCentre);
                float studWidth = max(length(pixel) / _StudPitch, 1e-5);
                float studMask = 1.0 - smoothstep(_StudRadius - studWidth, _StudRadius + studWidth, radius);

                // The origin axes are the landmark that makes an unbounded world navigable.
                float2 axisWidth = pixel * _LineWidth * 1.5;
                float2 axes = 1.0 - smoothstep(float2(0, 0), axisWidth, abs(world));
                float axisMask = max(axes.x, axes.y);

                half4 color = _BaseColor;
                color = lerp(color, _StudColor, studMask * 0.65);
                color = lerp(color, _LineColor, lineMask);
                color = lerp(color, _AxisColor, axisMask);

                // Fade the pattern out with distance so the horizon settles to flat colour instead of
                // dissolving into interference.
                float viewDistance = length(input.positionWS - _WorldSpaceCameraPos);
                float fade = saturate((viewDistance - _FadeStart) / max(_FadeEnd - _FadeStart, 1e-3));
                color = lerp(color, _BaseColor, fade);

                color.rgb = MixFog(color.rgb, input.fogCoord);

                return color;
            }
            ENDHLSL
        }
    }

    Fallback Off
}
