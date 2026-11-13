Shader "Hidden/Sakura/ModelEffects"
{
    Properties
    {
        _MainTex ("Avatar", 2D) = "black" {}
        _Effects ("Glow, saturation, contrast, overlay", Vector) = (0,1,1,0)
        _Overlay ("Scene colour overlay", Color) = (1,1,1,1)
    }
    SubShader
    {
        Pass
        {
            ZTest Always ZWrite Off Cull Off
            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float4 _Effects;
            float4 _Overlay;

            float3 highlight(float2 uv)
            {
                float4 pixel = tex2D(_MainTex, uv);
                float3 straight = pixel.rgb / max(pixel.a, .0001);
                float brightness = max(straight.r, max(straight.g, straight.b));
                return straight * saturate((brightness - .6) / .4) * pixel.a;
            }
            float4 frag(v2f_img input) : SV_Target
            {
                float4 source = tex2D(_MainTex, input.uv);
                // The avatar render target contains premultiplied RGB.
                float3 color = source.rgb / max(source.a, .0001);
                if (_Effects.x > 0)
                {
                    float2 texelOffset = _MainTex_TexelSize.xy * 3;
                    float3 glow = highlight(input.uv) * 4;
                    glow += highlight(input.uv + float2(texelOffset.x, 0)) * 2;
                    glow += highlight(input.uv - float2(texelOffset.x, 0)) * 2;
                    glow += highlight(input.uv + float2(0, texelOffset.y)) * 2;
                    glow += highlight(input.uv - float2(0, texelOffset.y)) * 2;
                    glow += highlight(input.uv + texelOffset);
                    glow += highlight(input.uv - texelOffset);
                    glow += highlight(input.uv + float2(texelOffset.x, -texelOffset.y));
                    glow += highlight(input.uv + float2(-texelOffset.x, texelOffset.y));
                    color += glow * (_Effects.x / 16);
                }
                float luminance = dot(color, float3(.2126, .7152, .0722));
                color = lerp(luminance.xxx, color, _Effects.y);
                color = (color - .5) * _Effects.z + .5;
                color *= lerp(float3(1,1,1), _Overlay.rgb, _Effects.w);
                // Keep the original silhouette alpha for transparent output and masks.
                return float4(saturate(color) * source.a, source.a);
            }
            ENDHLSL
        }
    }
}
