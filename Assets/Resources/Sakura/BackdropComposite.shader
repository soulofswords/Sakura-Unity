Shader "Hidden/Sakura/BackdropComposite"
{
    Properties { _MainTex("Avatar", 2D) = "black" {} _Background("Background", Color) = (0,1,0,1) _Media("Background media", 2D) = "black" {} _UseMedia("Use media", Float) = 0 _MediaScale("Media crop", Vector) = (1,1,0,0) }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            ZTest Always ZWrite Off Cull Off
            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            sampler2D _Media;
            float4 _Background;
            float _UseMedia;
            float4 _MediaScale;
            float4 frag(v2f_img input) : SV_Target
            {
                float4 avatar = tex2D(_MainTex, input.uv);
                float4 media = tex2D(_Media, (input.uv - .5) * _MediaScale.xy + .5) * _UseMedia;
                float4 background = float4(media.rgb * media.a + _Background.rgb * _Background.a * (1 - media.a), media.a + _Background.a * (1 - media.a));
                return float4(avatar.rgb + background.rgb * (1 - avatar.a), avatar.a + background.a * (1 - avatar.a));
            }
            ENDHLSL
        }
    }
}
